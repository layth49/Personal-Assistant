using System;
using System.Threading.Tasks;
using Microsoft.CognitiveServices.Speech; // Library for text-to-speech functionality

namespace Personal_Assistant.SpeechManager
{
    public class SpeechService
    {
        private string speechKey;
        private string speechRegion;

        public SpeechService(string speechKey, string speechRegion) 
        {
            this.speechKey = speechKey;
            this.speechRegion = speechRegion;
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
                    Console.WriteLine("Assistant: Sorry I didn't get that. Can you say it again?");
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
            // Creates an instance of a speech config with specified subscription key and service region. Replace this with the same style as other comments
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
    }
}