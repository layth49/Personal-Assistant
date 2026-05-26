using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Personal_Assistant.TTSClient;
using Python.Runtime;
using System;
using System.IO;
using System.Threading.Tasks;


namespace Personal_Assistant.SpeechManager
{
    public class SpeechService
    {
        public static readonly string speechKey = Environment.GetEnvironmentVariable("SPEECH_KEY");
        public static readonly string speechRegion = Environment.GetEnvironmentVariable("SPEECH_REGION");


        // Set up audio configuration using the default microphone input
        public AudioConfig audioConfig = AudioConfig.FromDefaultMicrophoneInput();

        // Set up Speech SDK configuration
        public SpeechConfig speechConfig = SpeechConfig.FromSubscription(speechKey, speechRegion);

        dynamic text_display = Py.Import("SpeechBubble");

        private PyDict state;

        private readonly KokoroTTSService kokoroTTS = new KokoroTTSService();

        public SpeechService()
        {
            speechConfig.SpeechRecognitionLanguage = "en-US";

            speechConfig.EndpointId = Environment.GetEnvironmentVariable("SPEECH_ENDPOINT_ID");
        }

        public async Task KeywordRecognizer()
        {
            try
            {
                // Waits for keyword ("Hey 49")
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

        public void ConvertSpeechToText(SpeechRecognitionResult speechRecognitionResult)
        {
            switch (speechRecognitionResult.Reason)
            {
                case ResultReason.RecognizedSpeech:
                    Console.WriteLine($"RECOGNIZED: {speechRecognitionResult.Text}");
                    break;
                case ResultReason.NoMatch:
                    SpeechBubble("", "Sorry I didn't get that. Can you say it again?");
                    SynthesizeTextToSpeech("Sorry I didn't get that. Can you say it again?");
                    break;
                case ResultReason.Canceled:
                    CancellationDetails cancellation = CancellationDetails.FromResult(speechRecognitionResult);
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

        public async Task SynthesizeTextToSpeech(string textToSynthesize)
        {
            try
            {
                await kokoroTTS.SpeakAsync(textToSynthesize);
            }
            finally
            {
                // Retract the speech bubble once playback completes (or fails)
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

    }
}