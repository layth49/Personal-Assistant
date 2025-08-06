using Microsoft.CognitiveServices.Speech; // Library for text-to-speech functionality
using Microsoft.CognitiveServices.Speech.Audio;
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

        public SpeechService()
        {
            speechConfig.SpeechRecognitionLanguage = "en-US";
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

        // Got this beautiful method from https://bit.ly/3GVo2r1
        // This method handles the speech-to-text conversion result
        public void ConvertSpeechToText(SpeechRecognitionResult speechRecognitionResult)
        {
            switch (speechRecognitionResult.Reason)
            {
                case ResultReason.RecognizedSpeech:
                    Console.WriteLine($"RECOGNIZED: {speechRecognitionResult.Text}");
                    break;
                case ResultReason.NoMatch:
                    SpeechBubble("","Sorry I didn't get that. Can you say it again?");
                    SynthesizeTextToSpeech("en-US-AndrewNeural", "Sorry I didn't get that. Can you say it again?");
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

        // Got this beautiful method from https://bit.ly/3GVnSjc
        // This method handles the text-to-speech synthesis
        public async Task SynthesizeTextToSpeech(string voiceName, string textToSynthesize)
        {
            // Creates an instance of a speech config with specified subscription key and service region.
            SpeechConfig config = SpeechConfig.FromSubscription(speechKey, speechRegion);

            // I liked this voice but you can look for others on https://bit.ly/3ttEGuH
            config.SpeechSynthesisVoiceName = voiceName;

            // Use the default speaker as audio output
            using (SpeechSynthesizer synthesizer = new SpeechSynthesizer(config))
            {
                using (SpeechSynthesisResult result = await synthesizer.SpeakTextAsync(textToSynthesize))
                {
                    // This is to close the speech bubble after the text is spoken
                    using (Py.GIL())
                    {
                        var pyFalse = PythonEngine.Eval("False");
                        state.SetItem("running", pyFalse);
                    }

                    if (result.Reason == ResultReason.Canceled)
                    {
                        SpeechSynthesisCancellationDetails cancellation = SpeechSynthesisCancellationDetails.FromResult(result);
                        Console.WriteLine($"CANCELED: Reason={cancellation.Reason}");

                        if (cancellation.Reason == CancellationReason.Error)
                        {
                            Console.WriteLine($"CANCELED: ErrorCode={cancellation.ErrorCode}");
                            Console.WriteLine($"CANCELED: ErrorDetails=[{cancellation.ErrorDetails}]");
                            Console.WriteLine($"CANCELED: Did you update the subscription info?");
                        }
                    }
                }
            }
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