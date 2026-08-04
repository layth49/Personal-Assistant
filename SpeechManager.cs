using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Personal_Assistant.Diagnostics;
using Personal_Assistant.VoiceClips;
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

        // Cached once instead of re-evaluated on every retract. PythonEngine.Eval
        // parses and compiles a fresh expression each call, and the retract path
        // runs at the end of every single utterance.
        private PyObject pyTrue;
        private PyObject pyFalse;

        // Serialises Say so overlapping callers never garble each other's audio
        // or clobber the single shared bubble `state`. The main loop is already
        // sequential; this matters when a background reminder/timer fires while
        // the assistant happens to be speaking.
        //
        // Because BeginSpeaking/EndSpeaking are both inside the gate on every
        // path, `speaking` and the echo reference can't be clobbered by an
        // overlapping speaker either.
        private readonly System.Threading.SemaphoreSlim sayGate =
            new System.Threading.SemaphoreSlim(1, 1);

        // True for exactly as long as assistant audio is playing. Written only
        // between BeginSpeaking and EndSpeaking, which are always inside sayGate.
        private volatile bool speaking;
        private volatile string speakingText;

        // Optional per-turn latency breakdown (null-safe — a caller that doesn't
        // care about timing can just not pass one).
        private readonly LatencyTracker latency;

        // The one instance the app is actually running — the one holding the
        // synthesizer whose audio the barge-in path stops, and the one whose
        // bubble `state` the Python daemon is watching.
        //
        // Command handlers MUST use this rather than newing their own. Five of
        // them did, and a second instance is silently useless in both
        // directions: its BeginSpeaking updates state nothing else reads, so the
        // real echo gate never learns the assistant is talking and every prompt
        // that handler speaks escapes it wholesale; and its RecognizeOnceAsync
        // competes for the same microphone. In the SMS flow that meant an empty
        // message body was handed to Phone Link and actually sent, while the
        // escaped prompts queued up as turns and the assistant talked to itself
        // for a minute.
        public static SpeechService Current { get; private set; }

        public SpeechService(LatencyTracker latency = null)
        {
            // First one wins: Program.Main builds the real one before any handler
            // exists, so a stray `new` elsewhere can't steal the slot.
            if (Current == null) Current = this;

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
                // Resolve next to the exe, not against the working directory. The app
                // runs from a deploy folder (C:\Users\layth\LAITH\main) as often as from
                // bin\Debug, and those sit at different depths, so no relative path is
                // correct for both. The csproj copies keyword.table to the output dir.
                keywordModel = KeywordRecognitionModel.FromFile(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "keyword.table"));
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
                // Importing the module starts its bubble daemon thread, which
                // needs the GIL to run — Program.Main's PythonEngine.BeginAllowThreads()
                // is what lets it get it once we drop out of this block.
                textDisplay = Py.Import("SpeechBubble");
                pyTrue = PythonEngine.Eval("True");
                pyFalse = PythonEngine.Eval("False");
            }
        }

        // Brackets every source of assistant audio — streamed replies, canned
        // Say() lines, and reminders firing in the background alike. Anything
        // that needs to know whether the assistant is currently talking (the
        // barge-in check, and any echo gate on the microphone) keys off this, so
        // a path that skips it is a path where the assistant can hear itself.
        //
        // Public, unlike local-laith's private pair: on this branch the Live
        // session plays its own audio through LiveAudioPlayback rather than
        // through this class, so it has to bracket that audio itself.
        public void BeginSpeaking(string text)
        {
            speakingText = text;
            speaking = true;
            AssistantSpeechStarted?.Invoke(text);
        }

        public void EndSpeaking()
        {
            speaking = false;
            speakingText = null;
            AssistantSpeechEnded?.Invoke();
        }

        // Grows the echo reference mid-utterance. A streamed reply doesn't exist
        // as one string when BeginSpeaking(null) is called — it arrives sentence
        // by sentence — so callers append each piece as they voice it.
        public void AppendSpeakingText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            speakingText = string.IsNullOrEmpty(speakingText) ? text : speakingText + " " + text;
        }

        public bool IsSpeaking => speaking;

        // What the assistant is saying right now, for comparing against anything
        // the microphone picks up. Null when it isn't speaking.
        public string SpeakingText => speakingText;

        // Raised inside sayGate, so handlers see a consistent view of `speaking`.
        // A handler that blocks here delays the reply, so keep them cheap.
        public event Action<string> AssistantSpeechStarted;
        public event Action AssistantSpeechEnded;

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
                    // Fire-and-forget: this method is synchronous and is called
                    // from inside `using (var recognizer = ...)` blocks, so it
                    // can't await. Routing through Say rather than raw synthesis
                    // is what keeps the re-prompt inside sayGate and inside the
                    // BeginSpeaking/EndSpeaking brackets. Callers that speak next
                    // queue behind it on that gate rather than talking over it.
                    _ = Say(string.Empty, "Sorry I didn't get that. Can you say it again?");
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

                RetractBubble();
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
                RetractBubble();
            }
        }

        // Flips the current bubble's shared state so the Python daemon animates
        // it out. Safe to call more than once, and a no-op if no bubble was set
        // up for this utterance.
        private void RetractBubble()
        {
            if (state == null) return;
            using (Py.GIL())
            {
                state.SetItem("running", pyFalse);
            }
        }

        // Posts the bubble to the persistent Python daemon and returns immediately.
        // The pygame window lives on its own long-lived Python thread, so this no
        // longer parks the calling thread for the length of the utterance. The
        // daemon holds the bubble while state["running"] is true and animates it
        // out when the synth flips it to false.
        public void SpeechBubble(string userInput, string response)
        {
            var sw = Stopwatch.StartNew();
            using (Py.GIL())
            {
                state = new PyDict();
                state.SetItem("running", pyTrue);
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
            }
            Console.WriteLine($"[bubble] posted in {sw.ElapsedMilliseconds}ms");
        }

        // Swaps the text of the bubble already on screen, with no exit/enter
        // animation — how a streamed reply grows as sentences arrive. A no-op if
        // nothing is currently being held.
        public void UpdateBubble(string userInput, string response)
        {
            using (Py.GIL())
            {
                try
                {
                    textDisplay.update_bubble(userInput, response);
                }
                catch (PythonException ex)
                {
                    Console.WriteLine("Bubble update failed: " + ex.Message);
                }
            }
        }

        // Retracts the current bubble without waiting on its state dict — for
        // callers that show a bubble without an accompanying synth to flip it.
        public void HideBubble()
        {
            using (Py.GIL())
            {
                try
                {
                    textDisplay.hide_bubble();
                }
                catch (PythonException ex)
                {
                    Console.WriteLine("Bubble hide failed: " + ex.Message);
                }
            }
        }

        // Plays ~250ms of silence to wake the audio output device. Bluetooth
        // headphones / wireless speakers / sleep-enabled DACs suppress the first
        // ~200ms after a period of silence, which clips the start of the greeting.
        // Call this once at startup so the first real synth plays in full.
        public async Task WarmUpAudioAsync()
        {
            await sayGate.WaitAsync();
            try
            {
                // Goes through the same brackets as every other speech path even
                // though it is silent and runs before anything is listening: the
                // invariant is worth more than the one line it costs here.
                BeginSpeaking(null);
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
            finally
            {
                EndSpeaking();
                sayGate.Release();
            }
        }

        // Convenience wrapper: shows the bubble, speaks, and the bubble retracts
        // automatically when the audio finishes. Always prefer this over calling
        // Synthesize+SpeechBubble separately — it is the path that brackets the
        // audio and serialises against other speakers.
        public async Task Say(string userInput, string response)
        {
            await sayGate.WaitAsync();
            try
            {
                // Bubble first. Posting it is non-blocking now, and doing it
                // before the synth starts means the synth's retract signal can
                // never land on a stale state dict — previously the synth was
                // scheduled first only because showing the bubble parked the
                // calling thread for the whole utterance.
                SpeechBubble(userInput, response);
                BeginSpeaking(response);
                try { await SynthesizeTextToSpeech(response); }
                catch (Exception ex) { Console.WriteLine($"TTS error: {ex.Message}"); }
            }
            finally
            {
                EndSpeaking();
                sayGate.Release();
            }
        }

        // Say, but plays a pre-rendered Live-voice clip when one exists, so the
        // greeting and the goodbye are in the same voice as the conversation
        // between them. Falls back to Azure TTS on a miss — an unrendered line
        // must still be spoken, just in the old voice.
        //
        // Identical bracketing to Say: the bubble, BeginSpeaking/EndSpeaking and
        // sayGate all apply, because a clip is assistant audio like any other and
        // the echo gate has to know about it.
        public async Task SayClip(string userInput, string response)
        {
            string voice = Environment.GetEnvironmentVariable("LAITH_LIVE_VOICE");

            // A miss is rendered now rather than dropping to the other voice.
            // Layth's call: a labelled reminder arriving a beat late is better
            // than a different voice appearing out of nowhere. Only the FIRST
            // utterance of a given line pays this; it is cached afterwards.
            if (!VoiceClipCache.TryGet(voice, response, out string clip))
            {
                await VoiceClipRenderer.TryEnsureAsync(voice, response);
            }

            if (!VoiceClipCache.TryGet(voice, response, out clip))
            {
                await Say(userInput, response);
                return;
            }

            await sayGate.WaitAsync();
            try
            {
                SpeechBubble(userInput, response);
                BeginSpeaking(response);
                try
                {
                    await VoiceClipCache.PlayAsync(clip);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[clips] playback failed, falling back to TTS: {ex.Message}");
                    try { await SynthesizeTextToSpeech(response); }
                    catch (Exception inner) { Console.WriteLine($"TTS error: {inner.Message}"); }
                }
            }
            finally
            {
                RetractBubble();
                EndSpeaking();
                sayGate.Release();
            }
        }

        // Say, but for SSML — for callers that need <phoneme>, <lang>, <break> or
        // other pronunciation control. `displayText` is what the bubble shows,
        // since the markup itself must never be rendered.
        public async Task SaySsml(string userInput, string displayText, string ssml)
        {
            await sayGate.WaitAsync();
            try
            {
                SpeechBubble(userInput, displayText);
                BeginSpeaking(displayText);
                try { await SynthesizeSsmlAsync(ssml); }
                catch (Exception ex) { Console.WriteLine($"TTS error: {ex.Message}"); }
            }
            finally
            {
                EndSpeaking();
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
                // Bubble first (non-blocking), then the synth — same ordering as
                // Say, so the retract signal always targets this turn's state dict.
                SpeechBubble(userInput, response);
                BeginSpeaking(response);

                // The synth still goes to the threadpool, but for a different
                // reason than it used to: the race below needs a Task handle to
                // put up against the keyword recogniser. It is no longer working
                // around a bubble that parked the calling thread, which is why
                // the race itself is now awaited right here rather than pushed
                // onto its own thread and collected through a
                // TaskCompletionSource with a timeout.
                var synthTask = Task.Run(() => SynthesizeTextToSpeech(response));

                Console.WriteLine("[interrupt] listening for wakeword during speech");
                bool wasInterrupted = false;
                try
                {
                    var keywordTask = interruptKeywordRecognizer.RecognizeOnceAsync(keywordModel);
                    var finished = await Task.WhenAny(synthTask, keywordTask);

                    if (finished == keywordTask)
                    {
                        KeywordRecognitionResult kw = null;
                        try { kw = await keywordTask; }
                        catch (Exception ex) { Console.WriteLine($"[interrupt] keyword await error: {ex.Message}"); }

                        wasInterrupted = kw != null && kw.Reason == ResultReason.RecognizedKeyword;
                        if (wasInterrupted)
                        {
                            Console.WriteLine("[interrupt] wakeword during speech -> cutting off");
                            // Stopping the synth makes SpeakTextAsync return,
                            // which flips state.running false and retracts the
                            // bubble — the same path as a natural finish.
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

                try { await synthTask; }
                catch (Exception ex) { Console.WriteLine($"TTS error: {ex.Message}"); }

                // Retract the bubble defensively (normally already closed).
                RetractBubble();

                Console.WriteLine($"[interrupt] returning interrupted = {wasInterrupted}");
                return wasInterrupted;
            }
            finally
            {
                EndSpeaking();
                sayGate.Release();
            }
        }

        // Runs the on-device keyword spotter until it fires or `cancellationToken`
        // is cancelled, and reports whether the wakeword was actually heard.
        //
        // SayInterruptible above owns the same race for Azure TTS, where the thing
        // being cut is a synthesizer this class holds. The Live session's audio
        // belongs to LiveAudioPlayback instead, so it needs the detection without
        // the cutting — hence this rather than another copy of the race.
        //
        // Uses the DEDICATED interrupt recognizer for the reason documented on the
        // field: borrowing the main-loop one and stopping it mid-flight invalidates
        // its handle and permanently breaks the wakeword wait. The worst case here
        // stays "barge-in stops working", never "the assistant stops waking up".
        public async Task<bool> WatchForWakewordAsync(System.Threading.CancellationToken cancellationToken)
        {
            if (interruptKeywordRecognizer == null || keywordModel == null) return false;

            try
            {
                var keywordTask = interruptKeywordRecognizer.RecognizeOnceAsync(keywordModel);
                var cancelled = new TaskCompletionSource<bool>();
                using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
                {
                    var finished = await Task.WhenAny(keywordTask, cancelled.Task);
                    if (finished != keywordTask) return false;
                }

                var kw = await keywordTask;
                return kw != null && kw.Reason == ResultReason.RecognizedKeyword;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[interrupt] wakeword watch error: {ex.Message}");
                return false;
            }
        }

        // Stops the interrupt recognizer so the next WatchForWakewordAsync starts
        // clean. Fire-and-forget on purpose: the caller is usually finishing a turn
        // and must not be held up by a recogniser that won't stop — and because
        // it's the dedicated instance, a wedged one costs later barge-ins only.
        public void StopWakewordWatch()
        {
            if (interruptKeywordRecognizer == null) return;
            _ = Task.Run(async () =>
            {
                try { await WithTimeout(interruptKeywordRecognizer.StopRecognitionAsync(), 3000, "StopRecognition"); }
                catch (Exception ex) { Console.WriteLine($"[interrupt] stop failed: {ex.Message}"); }
            });
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