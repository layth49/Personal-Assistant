using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Personal_Assistant.STTClient;
using Personal_Assistant.TTSClient;
using Python.Runtime;
using System;
using System.IO;
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

        public SpeechService()
        {
            using (Py.GIL())
            {
                text_display = Py.Import("SpeechBubble");
            }
        }

        public async Task KeywordRecognizer()
        {
            try
            {
                using (var keywordModel = KeywordRecognitionModel.FromFile(@"..\..\keyword.table"))
                {
                    var keywordRecognizer = new KeywordRecognizer(audioConfig);
                    KeywordRecognitionResult result = await keywordRecognizer.RecognizeOnceAsync(keywordModel);
                }
            }
            catch (FileNotFoundException ex)
            {
                Console.WriteLine("Error: Keyword model file not found. " + ex.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine("An unexpected error occurred in the KeywordRecognizer Method: " + ex.Message);
            }
        }

        // Captures speech via faster-whisper-server. An empty return value is the
        // new NoMatch signal — callers check string.IsNullOrEmpty() the same way
        // they used to check ResultReason.NoMatch.
        public async Task<string> RecognizeOnceAsync(int maxSeconds = 15)
        {
            string text = await whisper.RecognizeOnceAsync(maxSeconds);
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
                await kokoroTTS.SpeakAsync(".");
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
            var synthTask = Task.Run(() => SynthesizeTextToSpeech(response));
            SpeechBubble(userInput, response);
            try { await synthTask; }
            catch (Exception ex) { Console.WriteLine($"TTS error: {ex.Message}"); }
        }
    }
}
