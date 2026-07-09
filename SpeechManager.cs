using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Personal_Assistant.Diagnostics;
using Personal_Assistant.STTClient;
using Personal_Assistant.TTSClient;
using Python.Runtime;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;


namespace Personal_Assistant.SpeechManager
{
    public class SpeechService
    {
        // Default mic — used by the on-device KeywordRecognizer (wake word).
        // The KeywordRecognizer runs entirely on-device against keyword.table
        // and needs no API key.
        public AudioConfig audioConfig = AudioConfig.FromDefaultMicrophoneInput();

        // Local-stack clients. Replace Azure Neural TTS / Speech-to-Text.
        private readonly KokoroTTSService kokoroTTS = new KokoroTTSService();
        private readonly WhisperSTTService whisper = new WhisperSTTService();

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
        private readonly SemaphoreSlim sayGate = new SemaphoreSlim(1, 1);

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

        // Captures speech via faster-whisper-server. An empty return value is the
        // new NoMatch signal — callers check string.IsNullOrEmpty() the same way
        // they used to check ResultReason.NoMatch.
        public async Task<string> RecognizeOnceAsync(int maxSeconds = 15)
        {
            string text = await whisper.RecognizeOnceAsync(maxSeconds);
            latency?.RecordStt(whisper.LastTranscribeElapsed);
            if (string.IsNullOrEmpty(text))
            {
                await Say(string.Empty, "Sorry I didn't get that. Can you say it again?");
            }
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
                if (state != null)
                {
                    using (Py.GIL())
                    {
                        var pyFalse = PythonEngine.Eval("False");
                        state.SetItem("running", pyFalse);
                    }
                }
            }
        }

        // Cuts current TTS playback immediately.
        public void StopSpeaking()
        {
            kokoroTTS.StopSpeaking();
        }

        public void SpeechBubble(string userInput, string response)
        {
            using (Py.GIL())
            {
                state = new PyDict();
                state.SetItem("running", PythonEngine.Eval("True"));
                IntPtr gil = PythonEngine.BeginAllowThreads();
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
                finally
                {
                    PythonEngine.EndAllowThreads(gil);
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
                var synthTask = Task.Run(() => SynthesizeTextToSpeech(response));
                SpeechBubble(userInput, response);
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
                // bubble call below blocks the calling thread for the whole speech,
                // so the barge-in race must run on its own thread or it would only
                // begin after the speech already finished.
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

                // Bubble on the calling thread; returns when the synth completes
                // (naturally or because it was stopped by a barge-in).
                SpeechBubble(userInput, response);
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
