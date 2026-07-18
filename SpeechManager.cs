using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Personal_Assistant.Diagnostics;
using Python.Runtime;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;


namespace Personal_Assistant.SpeechManager
{
    public class SpeechService
    {
        public static readonly string speechKey = Environment.GetEnvironmentVariable("SPEECH_KEY");
        public static readonly string speechRegion = Environment.GetEnvironmentVariable("SPEECH_REGION");

        // Shared across SpeechRecognizer / KeywordRecognizer
        public readonly AudioConfig audioConfig = AudioConfig.FromDefaultMicrophoneInput();
        public readonly SpeechConfig speechConfig;

        // Reused — recreating per call adds websocket handshake latency to every TTS.
        // (Azure Speech SDK guidance: reuse SpeechSynthesizer.) The connection
        // is established lazily by the SDK on the first SpeakTextAsync call.
        private readonly SpeechSynthesizer synthesizer;

        // Reused across keyword cycles. Constructing a new KeywordRecognizer per
        // cycle re-opens the microphone via WASAPI and reloads the on-device
        // keyword model — on some machines that setup takes several seconds,
        // during which the SDK misses the keyword and only catches it on a
        // later retry. Creating once at startup eliminates that gap entirely.
        private readonly KeywordRecognitionModel keywordModel;
        private readonly KeywordRecognizer keywordRecognizer;

        // A SEPARATE keyword recognizer (with its own mic AudioConfig) used only
        // for barge-in detection while the assistant is speaking. It must be
        // distinct from the main-loop `keywordRecognizer`: borrowing that one and
        // stopping it mid-flight invalidates its handle (SPXERR_INVALID_HANDLE),
        // which permanently breaks the wakeword wait. Keeping interrupts on their
        // own recognizer means the worst case is "barge-in stops working", never
        // "the whole assistant breaks".
        private readonly AudioConfig interruptAudioConfig;
        private readonly KeywordRecognizer interruptKeywordRecognizer;

        private readonly dynamic textDisplay;
        private PyDict state;

        // Serialises Say so overlapping callers never garble each other's audio
        // or clobber the single shared bubble `state`. The main loop is already
        // sequential; this matters when a background reminder/timer fires while
        // the assistant happens to be speaking.
        private readonly System.Threading.SemaphoreSlim sayGate =
            new System.Threading.SemaphoreSlim(1, 1);

        // Optional per-turn latency breakdown (null-safe — a caller that doesn't
        // care about timing can just not pass one).
        private readonly LatencyTracker latency;

        public SpeechService(LatencyTracker latency = null)
        {
            this.latency = latency;
            // Recognition config — EndpointId here targets a CUSTOM RECOGNITION model.
            // It must NOT be applied to the synthesizer, or TTS calls hit the wrong
            // endpoint and return immediately with no audio.
            speechConfig = SpeechConfig.FromSubscription(speechKey, speechRegion);
            speechConfig.SpeechRecognitionLanguage = "en-US";

            var endpointId = Environment.GetEnvironmentVariable("SPEECH_ENDPOINT_ID");
            if (!string.IsNullOrEmpty(endpointId))
            {
                speechConfig.EndpointId = endpointId;
            }

            // Dedicated synthesis config — no EndpointId.
            var synthConfig = SpeechConfig.FromSubscription(speechKey, speechRegion);
            synthConfig.SpeechSynthesisVoiceName = "en-US-AndrewMultilingualNeural";
            synthesizer = new SpeechSynthesizer(synthConfig);

            // Pre-open the synth websocket so the first SpeakTextAsync doesn't pay
            // the TCP+TLS+protocol-upgrade handshake (which clips the start of audio).
            // The Connection wrapper is disposed; the underlying connection stays
            // attached to the synthesizer. Per Azure Speech SDK guidance.
            using (var connection = Connection.FromSpeechSynthesizer(synthesizer))
            {
                connection.Open(forContinuousRecognition: true);
            }

            // Load the keyword model + recognizer once. Reused across every
            // keyword cycle so we don't re-pay WASAPI setup each time.
            try
            {
                keywordModel = KeywordRecognitionModel.FromFile(@"..\..\keyword.table");
                keywordRecognizer = new KeywordRecognizer(audioConfig);
                // Dedicated recognizer + mic config for barge-in (see field docs).
                interruptAudioConfig = AudioConfig.FromDefaultMicrophoneInput();
                interruptKeywordRecognizer = new KeywordRecognizer(interruptAudioConfig);
            }
            catch (FileNotFoundException ex)
            {
                Console.WriteLine("Error: Keyword model file not found. " + ex.Message);
            }

            using (Py.GIL())
            {
                textDisplay = Py.Import("SpeechBubble");
            }
        }

        // Waits for the wakeword. Returns true only if the keyword actually fired,
        // so the caller can distinguish a real wake from an early/errored return.
        // (Returning on error and letting the loop treat that as a wake is what
        // caused a runaway re-greet loop.)
        public async Task<bool> KeywordRecognizer()
        {
            if (keywordRecognizer == null || keywordModel == null)
            {
                Console.WriteLine("KeywordRecognizer: not initialised (model load failed). Waiting forever.");
                await Task.Delay(-1);
                return false;
            }
            try
            {
                var result = await keywordRecognizer.RecognizeOnceAsync(keywordModel);
                if (result.Reason == ResultReason.RecognizedKeyword)
                {
                    Console.WriteLine("Keyword was recognized!");
                    return true;
                }
                Console.WriteLine($"KeywordRecognizer: returned without a keyword ({result.Reason}).");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine("An unexpected error occurred in the KeywordRecognizer Method: " + ex.Message);
                // Back off so a wedged recognizer can't hot-spin the wake loop.
                await Task.Delay(1000);
                return false;
            }
        }

        public void ConvertSpeechToText(SpeechRecognitionResult speechRecognitionResult)
        {
            switch (speechRecognitionResult.Reason)
            {
                case ResultReason.RecognizedSpeech:
                    Console.WriteLine($"RECOGNIZED: {speechRecognitionResult.Text}");
                    break;
                case ResultReason.NoMatch:
                    // Fire-and-forget the synth so the bubble can show in parallel
                    // and be retracted when audio finishes.
                    _ = SynthesizeTextToSpeech("Sorry I didn't get that. Can you say it again?");
                    SpeechBubble("", "Sorry I didn't get that. Can you say it again?");
                    break;
                case ResultReason.Canceled:
                    var cancellation = CancellationDetails.FromResult(speechRecognitionResult);
                    Console.WriteLine($"CANCELED: Reason={cancellation.Reason}");
                    if (cancellation.Reason == CancellationReason.Error)
                    {
                        Console.WriteLine($"CANCELED: ErrorCode={cancellation.ErrorCode}");
                        Console.WriteLine($"CANCELED: ErrorDetails={cancellation.ErrorDetails}");
                        Console.WriteLine($"CANCELED: Did you set the speech resource key and region values?");
                    }
                    break;
            }
        }

        // Captures one utterance and returns the recognised text (empty string on
        // NoMatch, for which it also speaks a re-prompt — mirroring the local
        // Whisper SpeechService's signature so shared callers like SMSController
        // work identically on both backends). A fresh recognizer per call is fine
        // for the infrequent interactive prompts that use this.
        public async Task<string> RecognizeOnceAsync()
        {
            using (var recognizer = new SpeechRecognizer(speechConfig))
            {
                var result = await recognizer.RecognizeOnceAsync();
                ConvertSpeechToText(result);
                return result.Text ?? string.Empty;
            }
        }

        // Synthesise SSML directly. Use this when you need <lang>, <phoneme>,
        // <break>, <prosody>, or other pronunciation control beyond plain text.
        public async Task SynthesizeSsmlAsync(string ssml)
        {
            using (var result = await synthesizer.SpeakSsmlAsync(ssml))
            {
                Console.WriteLine($"TTS (SSML): Reason={result.Reason}, AudioDuration={result.AudioDuration}");

                if (result.Reason == ResultReason.Canceled)
                {
                    var cancellation = SpeechSynthesisCancellationDetails.FromResult(result);
                    Console.WriteLine($"CANCELED: Reason={cancellation.Reason}");
                    if (cancellation.Reason == CancellationReason.Error)
                    {
                        Console.WriteLine($"CANCELED: ErrorCode={cancellation.ErrorCode}");
                        Console.WriteLine($"CANCELED: ErrorDetails=[{cancellation.ErrorDetails}]");
                    }
                }

                if (state != null)
                {
                    using (Py.GIL())
                    {
                        state.SetItem("running", PythonEngine.Eval("False"));
                    }
                }
            }
        }

        public async Task SynthesizeTextToSpeech(string textToSynthesize)
        {
            var sw = Stopwatch.StartNew();
            using (var result = await synthesizer.SpeakTextAsync(textToSynthesize))
            {
                sw.Stop();
                Console.WriteLine($"TTS: Reason={result.Reason}, AudioDuration={result.AudioDuration}");

                if (result.Reason == ResultReason.Canceled)
                {
                    var cancellation = SpeechSynthesisCancellationDetails.FromResult(result);
                    Console.WriteLine($"CANCELED: Reason={cancellation.Reason}");
                    if (cancellation.Reason == CancellationReason.Error)
                    {
                        Console.WriteLine($"CANCELED: ErrorCode={cancellation.ErrorCode}");
                        Console.WriteLine($"CANCELED: ErrorDetails=[{cancellation.ErrorDetails}]");
                    }
                }

                if (result.Reason == ResultReason.SynthesizingAudioCompleted)
                {
                    // SpeakTextAsync's wall-clock time is synthesis + playback of
                    // the default speaker output combined; AudioDuration is just
                    // the natural length of the audio. Subtracting approximates
                    // the "processing overhead" beyond simply playing the reply
                    // out loud — the parallel to excluding STT's recording time.
                    var overhead = sw.Elapsed - result.AudioDuration;
                    latency?.RecordTts(overhead < TimeSpan.Zero ? TimeSpan.Zero : overhead);
                }

                // Signal the speech bubble to retract once audio finishes.
                // (state may be null if no bubble was set up — that's fine.)
                if (state != null)
                {
                    using (Py.GIL())
                    {
                        state.SetItem("running", PythonEngine.Eval("False"));
                    }
                }
            }
        }

        public void SpeechBubble(string userInput, string response)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Console.WriteLine($"[bubble t+{sw.ElapsedMilliseconds}ms] SpeechBubble: entered");
            using (Py.GIL())
            {
                Console.WriteLine($"[bubble t+{sw.ElapsedMilliseconds}ms] SpeechBubble: GIL acquired");
                state = new PyDict();
                state.SetItem("running", PythonEngine.Eval("True"));
                Console.WriteLine($"[bubble t+{sw.ElapsedMilliseconds}ms] SpeechBubble: state set, calling show_bubble");
                try
                {
                    textDisplay.show_bubble(userInput, response, state);
                }
                catch (PythonException ex)
                {
                    Console.WriteLine("PythonException caught:");
                    Console.WriteLine("Type: " + ex.Type);
                    Console.WriteLine("Message: " + ex.Message);
                    Console.WriteLine("StackTrace: " + ex.StackTrace);
                }
                Console.WriteLine($"[bubble t+{sw.ElapsedMilliseconds}ms] SpeechBubble: show_bubble returned");
            }
        }

        // Plays ~250ms of silence to wake the audio output device. Bluetooth
        // headphones / wireless speakers / sleep-enabled DACs suppress the first
        // ~200ms after a period of silence, which clips the start of the greeting.
        // Call this once at startup so the first real synth plays in full.
        public async Task WarmUpAudioAsync()
        {
            try
            {
                const string ssml =
                    "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>" +
                    "<voice name='en-US-AndrewMultilingualNeural'> " +
                    "<break time='750ms'/>" +
                    "</voice></speak>";
                using (await synthesizer.SpeakSsmlAsync(ssml)) { }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Audio warm-up failed (non-fatal): {ex.Message}");
            }
        }

        // Convenience wrapper: starts TTS, shows the bubble in parallel, and the
        // bubble retracts automatically when the audio finishes. Always prefer this
        // over calling Synthesize+SpeechBubble separately — the synth must run
        // concurrently with the bubble so it can signal completion.
        //
        // SpeakTextAsync has a synchronous prelude (audio device + format setup)
        // that runs on the calling thread before it yields. Hand it to the
        // threadpool so the main thread can immediately show the bubble.
        public async Task Say(string userInput, string response)
        {
            await sayGate.WaitAsync();
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Console.WriteLine($"[t+{sw.ElapsedMilliseconds}ms] Say: entered");
                var synthTask = Task.Run(() => SynthesizeTextToSpeech(response));
                Console.WriteLine($"[t+{sw.ElapsedMilliseconds}ms] Say: synth task scheduled, calling SpeechBubble");
                SpeechBubble(userInput, response);
                Console.WriteLine($"[t+{sw.ElapsedMilliseconds}ms] Say: SpeechBubble returned");
                try { await synthTask; }
                catch (Exception ex) { Console.WriteLine($"TTS error: {ex.Message}"); }
            }
            finally
            {
                sayGate.Release();
            }
        }

        // Like Say, but listens for the wakeword on the mic WHILE speaking. If the
        // user says it mid-utterance, the speech is cut short and this returns
        // true so the caller can jump straight to listening (barge-in). Returns
        // false if the speech finished normally.
        //
        // TTS plays to the speaker and the keyword recogniser reads the mic, so
        // they run on independent devices; the main acoustic caveat is speaker
        // bleed into the mic (a non-issue on headphones).
        public async Task<bool> SayInterruptible(string userInput, string response)
        {
            // No interrupt recognizer available -> behave exactly like Say.
            if (interruptKeywordRecognizer == null || keywordModel == null)
            {
                await Say(userInput, response);
                return false;
            }

            await sayGate.WaitAsync();
            try
            {
                var synthTask = Task.Run(() => SynthesizeTextToSpeech(response));

                // Start listening for the wakeword NOW, before the bubble. The
                // bubble call below blocks the calling thread for the whole speech
                // (it runs pygame until the synth signals done), so the barge-in
                // race has to run on its own thread or it would only begin after
                // the speech already finished.
                Console.WriteLine("[interrupt] listening for wakeword during speech");
                var interruptTcs = new TaskCompletionSource<bool>();
                _ = Task.Run(async () =>
                {
                    bool interrupted = false;
                    try
                    {
                        var keywordTask = interruptKeywordRecognizer.RecognizeOnceAsync(keywordModel);
                        var finished = await Task.WhenAny(synthTask, keywordTask);

                        if (finished == keywordTask)
                        {
                            KeywordRecognitionResult kw = null;
                            try { kw = await keywordTask; }
                            catch (Exception ex) { Console.WriteLine($"[interrupt] keyword await error: {ex.Message}"); }

                            interrupted = kw != null && kw.Reason == ResultReason.RecognizedKeyword;
                            if (interrupted)
                            {
                                Console.WriteLine("[interrupt] wakeword during speech -> cutting off");
                                // Stopping the synth makes SpeakTextAsync return,
                                // which flips state.running false and closes the
                                // bubble on the main thread — same path as a
                                // natural finish.
                                try { await WithTimeout(synthesizer.StopSpeakingAsync(), 3000, "StopSpeaking"); } catch { }
                            }
                        }
                        else
                        {
                            // Speech finished first -> stop listening. Bounded so a
                            // stuck recognizer can't hang anything; and it's the
                            // DEDICATED recognizer, so even if it's left in a bad
                            // state only future barge-ins suffer, never the main loop.
                            try { await WithTimeout(interruptKeywordRecognizer.StopRecognitionAsync(), 3000, "StopRecognition"); } catch { }
                            try { await WithTimeout(keywordTask, 3000, "keywordTask drain"); } catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[interrupt] race error: {ex.Message}");
                    }
                    interruptTcs.TrySetResult(interrupted);
                });

                // Bubble on the calling thread; returns when the synth completes
                // (naturally or because it was stopped by a barge-in).
                SpeechBubble(userInput, response);
                try { await synthTask; } catch (Exception ex) { Console.WriteLine($"TTS error: {ex.Message}"); }

                // Retract the bubble defensively (normally already closed).
                if (state != null)
                {
                    using (Py.GIL()) { state.SetItem("running", PythonEngine.Eval("False")); }
                }

                bool wasInterrupted = false;
                var settled = await Task.WhenAny(interruptTcs.Task, Task.Delay(4000));
                if (settled == interruptTcs.Task) wasInterrupted = interruptTcs.Task.Result;
                else Console.WriteLine("[interrupt] race did not settle in time");

                Console.WriteLine($"[interrupt] returning interrupted = {wasInterrupted}");
                return wasInterrupted;
            }
            finally
            {
                sayGate.Release();
            }
        }

        // Awaits `task` but gives up after `ms`, logging a warning. Used to keep
        // the interrupt teardown from ever blocking the assistant indefinitely on
        // a recogniser that won't stop.
        private static async Task WithTimeout(Task task, int ms, string label)
        {
            var completed = await Task.WhenAny(task, Task.Delay(ms));
            if (completed != task)
            {
                Console.WriteLine($"[interrupt] {label} timed out after {ms}ms");
                return;
            }
            await task; // surface any exception / result
        }

        private static async Task WithTimeout<T>(Task<T> task, int ms, string label)
        {
            var completed = await Task.WhenAny(task, Task.Delay(ms));
            if (completed != task)
            {
                Console.WriteLine($"[interrupt] {label} timed out after {ms}ms");
                return;
            }
            await task;
        }
    }
}