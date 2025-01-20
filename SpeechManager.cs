using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.CognitiveServices.Speech; // Library for text-to-speech functionality
using Microsoft.CognitiveServices.Speech.Audio;

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


        public SpeechService()
        {
            speechConfig.SpeechRecognitionLanguage = "en-US";
        }

        public async Task KeywordRecognizer()
        {
            try
            {
                // Waits for keyword ("Hey Computer")
                using (var keywordModel = KeywordRecognitionModel.FromFile(@"C:\Users\15048\vstudio\repos\Projects\Personal Assistant\keyword.table"))
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
                Console.WriteLine("An unexpected error occurred: " + ex.Message);
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
                    _cmd.Close();
                    break;
                case ResultReason.NoMatch:
                    Console.WriteLine("Assistant: Sorry I didn't get that. Can you say it again?");
                    SynthesizeTextToSpeech("en-US-AndrewNeural", "Sorry I didn't get that. Can you say it again?");
                    _cmd.Close();
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
                    _cmd.Close();
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

        private Process _cmd;

        public async Task AudioVisualizer()
        {
            _cmd = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    FileName = @"C:\Users\15048\AppData\Local\Programs\Python\Python312\python.exe",
                    Arguments = "\"C:\\Users\\15048\\vstudio\\repos\\Projects\\Personal Assistant\\AudioVisualizer.py\""
                }
            };
            _cmd.Start();
        }

        public void EndVisualizer()
        {
            if (_cmd != null && !_cmd.HasExited)
            {
                _cmd.Close();
                Console.WriteLine("Python script terminated.");
            }
        }

    }
}