using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Personal_Assistant.Diagnostics;
using Personal_Assistant.STTClient;
using Personal_Assistant.TTSClient;
using Python.Runtime;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace Personal_Assistant.SpeechManager
{
    // Produces an answer incrementally: calls `onSentence` for each speakable
    // chunk the moment it's complete, and returns the whole text when done.
    // Lets SpeechService drive streamed speech without knowing which LLM
    // client (or which prompt) is behind it.
    public delegate Task<string> SentenceProducer(
        Func<string, Task> onSentence,
        CancellationToken ct);

    public class SpeechService
    {
        // Default mic — used by the on-device KeywordRecognizer (wake word).
        // The KeywordRecognizer runs entirely on-device against keyword.table
        // and needs no API key.
        public AudioConfig audioConfig = AudioConfig.FromDefaultMicrophoneInput();

        // Local-stack clients. Replace Azure Neural TTS / Speech-to-Text.
        private readonly KokoroTTSService kokoroTTS = new KokoroTTSService();

        // Imported lazily in the constructor under Py.GIL — field initializers
        // run before Main has acquired the GIL, and Py.Import without the GIL
        // tears into protected memory (AccessViolationException).
        private readonly dynamic text_display;

        private PyDict state;

        // Wakeword model, loaded once and shared by the main-loop wait and the
        // barge-in listener.
        private readonly KeywordRecognitionModel keywordModel;

        // A SEPARATE recognizer + mic config used only for barge-in while speaking.
        // Keeping it distinct from the per-call main-loop recognizer means a
        // misbehaving interrupt can never wedge the main wakeword wait.
        private readonly AudioConfig interruptAudioConfig;
        private readonly KeywordRecognizer interruptKeywordRecognizer;

        // Serialises Say / SayInterruptible so a background reminder firing while
        // the assistant is speaking can't garble audio or clobber the bubble state.
        // It guards SPEAKING only — the listener runs on its own thread and is
        // never blocked by it, which is what lets the two happen at once.
        private readonly SemaphoreSlim sayGate = new SemaphoreSlim(1, 1);

        // Always-on microphone + Silero VAD. Owns the mic for the app's lifetime,
        // so listening no longer stops while the assistant talks.
        private readonly ContinuousListener listener = new ContinuousListener();
        public ContinuousListener Listener => listener;

        // The reply currently being generated/spoken, so a barge-in can cancel the
        // whole pipeline (LLM stream + TTS queue + playback) at once.
        private CancellationTokenSource activeResponseCts;
        private volatile bool speaking;
        private volatile bool bargedIn;

        // Optional per-turn latency breakdown (null-safe — a caller that doesn't
        // care about timing can just not pass one).
        private readonly LatencyTracker latency;

        public SpeechService(LatencyTracker latency = null)
        {
            this.latency = latency;
            try
            {
                keywordModel = KeywordRecognitionModel.FromFile(@"C:\Users\layth\LAITH\local\keyword.table");
                interruptAudioConfig = AudioConfig.FromDefaultMicrophoneInput();
                interruptKeywordRecognizer = new KeywordRecognizer(interruptAudioConfig);
            }
            catch (FileNotFoundException ex)
            {
                Console.WriteLine("Error: Keyword model file not found. " + ex.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Keyword model load failed: " + ex.Message);
            }

            using (Py.GIL())
            {
                text_display = Py.Import("SpeechBubble");
            }

            listener.SpeechOnset += OnSpeechOnset;
        }

        // Begins always-on capture. Separate from the constructor so the caller
        // controls when the mic opens.
        public void StartListening()
        {
            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Continuous listener failed to start: " + ex.Message);
            }
        }

        public void StopListening()
        {
            try { listener.Dispose(); } catch { }
        }

        // The barge-in trigger: the user started talking while the assistant was
        // mid-reply, so cut everything and let their utterance become the next
        // turn (the listener is already recording it).
        //
        // TODO(G): echo/AEC. On speakers, Kokoro's own output bleeds into the mic
        // and will trip this, making the assistant interrupt itself. Session G
        // owns that; until then this assumes a headset, as the code always has.
        private void OnSpeechOnset()
        {
            if (!speaking) return;

            Console.WriteLine("[barge-in] user spoke over the reply -> cutting off");
            bargedIn = true;
            StopSpeaking();

            var cts = activeResponseCts;
            if (cts != null)
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }
        }

        // Waits for the wakeword. Returns true only if the keyword actually fired,
        // so the caller can distinguish a real wake from an early/errored return
        // (returning on error and treating it as a wake caused a runaway loop).
        public async Task<bool> KeywordRecognizer()
        {
            if (keywordModel == null)
            {
                Console.WriteLine("KeywordRecognizer: model not loaded. Waiting forever.");
                await Task.Delay(-1);
                return false;
            }
            try
            {
                // Fresh recognizer per wait, as the original did — isolated from the
                // dedicated interrupt recognizer, so neither can corrupt the other.
                var keywordRecognizer = new KeywordRecognizer(audioConfig);
                KeywordRecognitionResult result = await keywordRecognizer.RecognizeOnceAsync(keywordModel);
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
                await Task.Delay(1000); // back off so a wedged recognizer can't hot-spin
                return false;
            }
        }

        // Waits for the user's next utterance, which the always-on listener has
        // been capturing all along (including while the assistant was speaking).
        // Empty return == they stayed quiet; callers check string.IsNullOrEmpty()
        // the same way they used to check ResultReason.NoMatch.
        public async Task<string> RecognizeOnceAsync(int maxSeconds = 15)
        {
            string text = await ListenForTurnAsync(TimeSpan.FromSeconds(maxSeconds));
            if (string.IsNullOrEmpty(text))
            {
                await Say(string.Empty, "Sorry I didn't get that. Can you say it again?");
            }
            return text;
        }

        // Same wait, without the re-prompt. The main loop uses this: silence there
        // means "the conversation is over", not "I misheard you".
        public async Task<string> ListenForTurnAsync(TimeSpan timeout)
        {
            string text = await listener.NextUtteranceAsync(timeout);
            latency?.RecordStt(listener.LastTranscribeElapsed);
            return text;
        }

        public async Task SynthesizeTextToSpeech(string textToSynthesize)
        {
            try
            {
                await kokoroTTS.SpeakAsync(textToSynthesize);
                latency?.RecordTts(kokoroTTS.LastSynthesisElapsed);
            }
            finally
            {
                // Retract the speech bubble once playback completes (or fails).
                RetractBubble();
            }
        }

        // Flips the current bubble's shared state so the Python daemon animates
        // it out. Safe to call more than once.
        private void RetractBubble()
        {
            if (state == null) return;
            using (Py.GIL())
            {
                state.SetItem("running", PythonEngine.Eval("False"));
            }
        }

        // Cuts current TTS playback immediately.
        public void StopSpeaking()
        {
            kokoroTTS.StopSpeaking();
        }

        // Posts the bubble to the persistent Python daemon and returns immediately.
        // The pygame window lives on its own long-lived Python thread, so this no
        // longer parks the calling thread for the length of the utterance. The
        // daemon holds the bubble while state["running"] is true and animates it
        // out when SynthesizeTextToSpeech flips it to false.
        public void SpeechBubble(string userInput, string response)
        {
            var sw = Stopwatch.StartNew();
            using (Py.GIL())
            {
                state = new PyDict();
                state.SetItem("running", PythonEngine.Eval("True"));
                try
                {
                    text_display.show_bubble(userInput, response, state);
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
        // animation — how a streamed reply grows as sentences arrive.
        public void UpdateBubble(string userInput, string response)
        {
            using (Py.GIL())
            {
                try
                {
                    text_display.update_bubble(userInput, response);
                }
                catch (PythonException ex)
                {
                    Console.WriteLine("Bubble update failed: " + ex.Message);
                }
            }
        }

        // Drives the audio device with a short Kokoro synth at startup. Bluetooth
        // headphones / wireless speakers / sleep-enabled DACs suppress the first
        // ~200ms after a period of silence, which clips the start of the greeting.
        // Doubles as a Kokoro server warm-up so the first real synth is fast.
        public async Task WarmUpAudioAsync()
        {
            try
            {
                await kokoroTTS.SpeakAsync("Laith Online");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Audio warm-up failed (non-fatal): {ex.Message}");
            }
        }

        // Convenience wrapper: starts TTS, shows the bubble in parallel, and the
        // bubble retracts automatically when the audio finishes.
        public async Task Say(string userInput, string response)
        {
            await sayGate.WaitAsync();
            try
            {
                // Bubble first: posting it is non-blocking now, and doing it before
                // the synth starts means the synth's retract signal can never land
                // on a stale state dict.
                SpeechBubble(userInput, response);
                var synthTask = Task.Run(() => SynthesizeTextToSpeech(response));
                try { await synthTask; }
                catch (Exception ex) { Console.WriteLine($"TTS error: {ex.Message}"); }
            }
            finally
            {
                sayGate.Release();
            }
        }

        // Like Say, but listens for the wakeword on the mic WHILE speaking. If the
        // user says it mid-utterance, Kokoro playback is cut short and this returns
        // true so the caller can jump straight to listening (barge-in). Returns
        // false if the speech finished normally.
        public async Task<bool> SayInterruptible(string userInput, string response)
        {
            // No interrupt recognizer available -> behave exactly like Say
            // (which shows emoji in the bubble but strips them for TTS).
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
                var synthTask = Task.Run(() => SynthesizeTextToSpeech(response));

                // The barge-in race runs on its own thread so it stays independent
                // of whatever the caller does while the reply plays.
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
                                // Cancels Kokoro playback, which lets SpeakAsync
                                // return and flips state.running false, closing the
                                // bubble on the main thread (same path as a finish).
                                StopSpeaking();
                            }
                        }
                        else
                        {
                            // Speech finished first -> stop listening. Bounded so a
                            // stuck recognizer can't hang anything; and it's the
                            // dedicated recognizer, so worst case only future
                            // barge-ins suffer, never the main loop.
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

                try { await synthTask; }
                catch (Exception ex) { Console.WriteLine($"TTS error: {ex.Message}"); }

                // Retract the bubble defensively (normally already closed).
                if (state != null)
                {
                    using (Py.GIL())
                    {
                        state.SetItem("running", PythonEngine.Eval("False"));
                    }
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

        // Speaks an answer AS IT IS GENERATED. Each sentence is synthesised and
        // queued the moment the model finishes it, so audio starts after sentence
        // one instead of after the whole reply — the Session D latency win.
        // Returns the complete text (for conversation memory).
        public async Task<string> SayStreaming(string userInput, SentenceProducer produce)
        {
            await sayGate.WaitAsync();
            var cts = new CancellationTokenSource();
            try
            {
                return await SpeakStreamAsync(userInput, produce, cts);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"TTS stream error: {ex.Message}");
                return string.Empty;
            }
            finally
            {
                RetractBubble();
                sayGate.Release();
            }
        }

        // Streaming reply that the user can simply talk over. The interrupt no
        // longer needs the wakeword: the always-on listener raises SpeechOnset,
        // OnSpeechOnset cuts playback and cancels this reply's token, and the
        // utterance already being recorded becomes the next turn.
        public async Task<(string Text, bool Interrupted)> SayStreamingInterruptible(
            string userInput, SentenceProducer produce)
        {
            await sayGate.WaitAsync();
            var cts = new CancellationTokenSource();
            try
            {
                string text = string.Empty;
                try { text = await SpeakStreamAsync(userInput, produce, cts); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Console.WriteLine($"TTS stream error: {ex.Message}"); }

                RetractBubble();

                bool wasInterrupted = bargedIn;
                if (wasInterrupted) Console.WriteLine("[barge-in] reply cut; next turn is already being recorded");
                return (text, wasInterrupted);
            }
            finally
            {
                sayGate.Release();
            }
        }

        // Shared core: drives the producer, feeding each sentence to the TTS queue
        // and growing the bubble in step, then waits for playback to drain.
        private async Task<string> SpeakStreamAsync(
            string userInput, SentenceProducer produce, CancellationTokenSource cts)
        {
            // Publish this reply so OnSpeechOnset can cancel it from the listener
            // thread. Streamed replies are the long ones — exactly what the user
            // wants to be able to talk over.
            bargedIn = false;
            activeResponseCts = cts;
            speaking = true;

            kokoroTTS.BeginStream();

            var spoken = new StringBuilder();
            bool bubbleShown = false;

            Func<string, Task> onSentence = sentence =>
            {
                if (string.IsNullOrWhiteSpace(sentence)) return Task.CompletedTask;

                // A chunk that's pure emoji/punctuation still belongs in the
                // bubble but must not reach Kokoro — the system prompt promises
                // emoji are shown, not spoken, and synthesising one costs a whole
                // round trip to produce a noise at the end of the reply.
                if (HasSpeakableContent(sentence)) kokoroTTS.EnqueueSentence(sentence);

                if (spoken.Length > 0) spoken.Append(' ');
                spoken.Append(sentence.Trim());
                string soFar = spoken.ToString();

                // The bubble appears with the first sentence — roughly when audio
                // starts — then grows in place as the rest arrives.
                if (!bubbleShown) { SpeechBubble(userInput, soFar); bubbleShown = true; }
                else UpdateBubble(userInput, soFar);

                return Task.CompletedTask;
            };

            string full;
            try
            {
                try
                {
                    full = await produce(onSentence, cts.Token);
                }
                finally
                {
                    kokoroTTS.EndStreamInput();
                }

                await kokoroTTS.CompleteStreamAsync();

                latency?.RecordTts(kokoroTTS.LastSynthesisElapsed);
                if (kokoroTTS.FirstAudioLatency.HasValue)
                {
                    latency?.RecordFirstAudio(kokoroTTS.FirstAudioLatency.Value);
                }
            }
            finally
            {
                speaking = false;
                activeResponseCts = null;
            }

            return full;
        }

        // True if there's anything a voice could actually pronounce.
        private static bool HasSpeakableContent(string text)
        {
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c)) return true;
            }
            return false;
        }

        // Awaits `task` but gives up after `ms`, so interrupt teardown can never
        // block the assistant on a recognizer that won't stop.
        private static async Task WithTimeout(Task task, int ms, string label)
        {
            var completed = await Task.WhenAny(task, Task.Delay(ms));
            if (completed != task)
            {
                Console.WriteLine($"[interrupt] {label} timed out after {ms}ms");
                return;
            }
            await task;
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
