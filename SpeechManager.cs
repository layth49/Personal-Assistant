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

        // Cached instead of PythonEngine.Eval("True"/"False") per call: the
        // listener's audio callback needs the GIL every 30ms, so anything the
        // turn thread does while holding it comes straight out of capture.
        private readonly PyObject pyTrue;
        private readonly PyObject pyFalse;

        // Wakeword model, loaded once and shared by the main-loop wait and the
        // barge-in listener.
        private readonly KeywordRecognitionModel keywordModel;

        // A SEPARATE recognizer + mic config used only for barge-in while speaking.
        // Keeping it distinct from the per-call main-loop recognizer means a
        // misbehaving interrupt can never wedge the main wakeword wait.
        private readonly AudioConfig interruptAudioConfig;
        private readonly KeywordRecognizer interruptKeywordRecognizer;

        // Serialises every speech path so a background reminder firing while the
        // assistant is speaking can't garble audio or clobber the bubble state.
        // It guards SPEAKING only — the listener owns its own thread and never
        // touches this, which is what lets the two happen at once: a reminder
        // queued behind a long reply delays the announcement, never the mic.
        //
        // Because BeginSpeaking/EndSpeaking are both inside the gate on every
        // path, `speaking` and the listener's echo reference can't be clobbered
        // by an overlapping speaker either.
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
        private volatile bool wakewordWatchActive;
        private volatile bool replyNamesAssistant;

        // Optional per-turn latency breakdown (null-safe — a caller that doesn't
        // care about timing can just not pass one).
        private readonly LatencyTracker latency;

        // The one instance the app is actually running — the one whose
        // microphone is open, whose echo reference the listener reads, and whose
        // playback the barge-in path is watching.
        //
        // Command handlers MUST use this rather than newing their own. Six of
        // them used to, and a second instance is silently useless in both
        // directions: its `BeginSpeaking` updates a ContinuousListener that was
        // never started, so the real listener never learns the assistant is
        // talking and every prompt it speaks escapes the echo gate wholesale;
        // and its `RecognizeOnceAsync` waits on that same dead listener, so it
        // always times out. In the SMS flow that meant an empty message body was
        // handed to Phone Link and actually sent, while the escaped prompts
        // queued up as turns and the assistant talked to itself for a minute.
        public static SpeechService Current { get; private set; }

        public SpeechService(LatencyTracker latency = null)
        {
            // First one wins: Program.Main builds the real one before any
            // handler exists, so a stray `new` elsewhere can't steal the slot.
            if (Current == null) Current = this;

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
                pyTrue = PythonEngine.Eval("True");
                pyFalse = PythonEngine.Eval("False");
            }

            listener.SpeechOnset += OnSpeechOnset;
            listener.UtteranceReady += OnUtteranceReady;
            // The echo gate can't infer this from the microphone without losing a
            // race against the VAD, so the TTS reports it directly.
            kokoroTTS.PlaybackStarted += listener.NotifyAudioStarted;
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
        // The listener only raises this for onsets that cleared its echo gate,
        // so on speakers Kokoro's own output no longer interrupts the assistant
        // mid-sentence. See the echo notes in ContinuousListener.
        private void OnSpeechOnset()
        {
            CutOffReply("user spoke over the reply");
        }

        // Late barge-in. If the echo gate judged an onset too quiet to be sure
        // about, the reply kept playing — but the utterance was still captured,
        // and by the time it comes back transcribed the listener has confirmed
        // it wasn't the assistant. Cut then, so a genuine interruption that the
        // energy test was cautious about still lands rather than being ignored.
        private void OnUtteranceReady(string text)
        {
            CutOffReply("utterance landed mid-reply");
        }

        private void CutOffReply(string why)
        {
            if (!speaking) return;

            Console.WriteLine($"[barge-in] {why} -> cutting off");
            bargedIn = true;
            StopSpeaking();

            var cts = activeResponseCts;
            if (cts != null)
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }
        }

        // Runs the on-device keyword spotter for the length of a streamed reply,
        // so saying "49" cuts it.
        //
        // This is the barge-in that survives loud speakers, and it exists
        // because the level-based one can't. Once the speakers are loud enough,
        // the microphone hears the assistant continuously: the VAD never sees a
        // gap, so the whole reply comes back as ONE utterance with the user's
        // interruption buried inside it, and the echo check then drops the lot.
        // Text can't rescue it either — a reply necessarily reuses the question's
        // vocabulary, so "tell me how a jet engine works" against a reply opening
        // "A jet engine works by…" is a verbatim four-word run.
        //
        // The keyword spotter sidesteps all of it by matching one specific
        // acoustic pattern instead of measuring loudness, so bleed doesn't fool
        // it and no threshold needs tuning.
        private Task StartWakewordWatch()
        {
            if (interruptKeywordRecognizer == null || keywordModel == null) return null;

            wakewordWatchActive = true;
            return Task.Run(async () =>
            {
                try
                {
                    var kw = await interruptKeywordRecognizer.RecognizeOnceAsync(keywordModel);
                    if (!wakewordWatchActive) return;   // the reply already ended
                    if (kw == null || kw.Reason != ResultReason.RecognizedKeyword) return;

                    // The assistant saying its own name would otherwise cut its
                    // own reply off — "L.A.I.T.H. 49" through the speakers is a
                    // perfectly good wakeword as far as the spotter is concerned.
                    if (replyNamesAssistant)
                    {
                        Console.WriteLine("[barge-in] wakeword ignored — the reply says it itself");
                        return;
                    }

                    CutOffReply("wakeword heard over the reply");
                    // Start the capture over: what's buffered is the reply's own
                    // echo, and keeping it would get the command that follows
                    // the wakeword dropped along with it.
                    listener.RestartCapture();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[interrupt] wakeword watch error: {ex.Message}");
                }
            });
        }

        private async Task StopWakewordWatch(Task watch)
        {
            if (watch == null) return;
            wakewordWatchActive = false;
            // Bounded: a wedged recognizer must never hold up the turn loop. It's
            // the dedicated instance, so the worst case is that later barge-ins
            // suffer, never the main wakeword wait.
            try { await WithTimeout(interruptKeywordRecognizer.StopRecognitionAsync(), 3000, "StopRecognition"); }
            catch { }
            try { await WithTimeout(watch, 3000, "wakeword watch drain"); }
            catch { }
        }

        // True if the wakeword appears in what the assistant is saying.
        private static bool NamesAssistant(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return text.IndexOf("49", StringComparison.Ordinal) >= 0
                || text.IndexOf("laith", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("l.a.i.t.h", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Brackets every source of assistant audio — streamed replies, canned
        // Say() lines, and reminders firing in the background alike. Both the
        // barge-in check and the listener's echo gate key off this, so a path
        // that skips it is a path where the assistant can hear itself.
        private void BeginSpeaking(string text)
        {
            listener.BeginAssistantSpeech(text);
            speaking = true;
        }

        private void EndSpeaking()
        {
            speaking = false;
            listener.EndAssistantSpeech();
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
                state.SetItem("running", pyFalse);
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
                state.SetItem("running", pyTrue);
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
            await sayGate.WaitAsync();
            try
            {
                // Goes through the same brackets as every other speech path even
                // though it runs before the mic is open: the invariant is worth
                // more than the one line it costs here.
                BeginSpeaking("Laith Online");
                await kokoroTTS.SpeakAsync("Laith Online");
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
                BeginSpeaking(response);
                var synthTask = Task.Run(() => SynthesizeTextToSpeech(response));
                try { await synthTask; }
                catch (Exception ex) { Console.WriteLine($"TTS error: {ex.Message}"); }
            }
            finally
            {
                EndSpeaking();
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
                BeginSpeaking(response);
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
                EndSpeaking();
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
            replyNamesAssistant = false;
            // No reference text yet — a streamed reply doesn't exist until the
            // model produces it, so the echo reference grows sentence by
            // sentence in onSentence below.
            BeginSpeaking(null);

            // Say "49" to cut a reply. Independent of the level gate, which on
            // loud speakers can't tell the user from the bleed at all.
            Task wakewordWatch = StartWakewordWatch();

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
                if (HasSpeakableContent(sentence))
                {
                    kokoroTTS.EnqueueSentence(sentence);
                    // Only what's actually voiced can come back as echo, so the
                    // emoji-only chunks skipped above are skipped here too.
                    listener.AppendAssistantSpeech(sentence);
                    // Latches for the rest of the reply: once the assistant has
                    // said its own name, anything the spotter hears afterwards
                    // could be that coming back.
                    if (NamesAssistant(sentence)) replyNamesAssistant = true;
                }

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
                EndSpeaking();
                activeResponseCts = null;
                await StopWakewordWatch(wakewordWatch);
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