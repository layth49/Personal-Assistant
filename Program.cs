using System;
using System.Media;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using OpenAI_API;
using OpenAI_API.Models;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Personal_Assistant.LocationLogic;
using Personal_Assistant.PrayTimesLogic;
using Personal_Assistant.WeatherLogic;

namespace Personal_Assistant
{
    class Program
    {
        // Replace these with your own Cognitive Services Speech API subscription key and service region endpoint
        static string openAIApiKey = Environment.GetEnvironmentVariable("OPENAIAPI_KEY");
        static string speechKey = Environment.GetEnvironmentVariable("SPEECH_KEY");
        static string speechRegion = Environment.GetEnvironmentVariable("SPEECH_REGION");
        static string weatherAPIKey = Environment.GetEnvironmentVariable("WEATHERAPI_KEY");

        // Inform the user that environment variables are required and how to set them
        static void CheckEnvironmentVariables()
        {
            if (string.IsNullOrEmpty(openAIApiKey) || string.IsNullOrEmpty(speechKey) || string.IsNullOrEmpty(speechRegion))
            {
                Console.WriteLine("Error: Please set the following environment variables before running the program:");
                Console.WriteLine("  - OPENAIAPI_KEY: Your OpenAI API key");
                Console.WriteLine("  - SPEECH_KEY: Your Cognitive Services Speech API subscription key");
                Console.WriteLine("  - SPEECH_REGION: Your Cognitive Services Speech API service region (e.g., westus)");
                Console.WriteLine("You can set them using the following commands (replace 'your_key' with your actual keys):");
                Console.WriteLine("  - setx OPENAIAPI_KEY your_openai_key");
                Console.WriteLine("  - setx SPEECH_KEY your_speech_key");
                Console.WriteLine("  - setx SPEECH_REGION your_speech_region");
                Console.WriteLine("  - setx WEATHERAPI_KEY your_weatherapi_key");
                Environment.Exit(1);
            }
        }

        public static void PlaySound() // Plays a chime sound to indicate the assistant is ready
        {
            SoundPlayer player = new SoundPlayer();
            player.SoundLocation = "C:\\Users\\15048\\source\\repos\\Personal Assistant(.Net Framework)\\bin\\Debug\\chime.wav";
            player.Play();
        }

        // Got this beautiful method from https://bit.ly/3GVo2r1
        // This method handles the speech-to-text conversion result
        static void ConvertSpeechToText(SpeechRecognitionResult speechRecognitionResult)
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
        public static async Task SynthesizeTextToSpeech(string voiceName, string textToSynthesize)
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
                    if (result.Reason == ResultReason.SynthesizingAudioCompleted)
                    {
                        // This was for testing but I decided to still keep it as commented anyway in case anyone else wanted it
                        // Console.WriteLine($"Speech synthesized for text [{textToSynthesize}]");
                    }
                    else if (result.Reason == ResultReason.Canceled)
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

        // This helped a lot https://bit.ly/3vgUqBO
        // This method interacts with the OpenAI API to generate a response
        static async Task<string> GenerateOpenAIResponse(string inputText)
        {
            OpenAIAPI api = new OpenAIAPI(openAIApiKey);
            // Create a new conversation with OpenAI
            OpenAI_API.Chat.Conversation chat = api.Chat.CreateConversation();
            chat.Model = Model.ChatGPTTurbo;
            chat.RequestParameters.Temperature = 1;

            chat.AppendSystemMessage("You are a personal assistant who helps the user for various things. " +
                "This can range from simple requests like \"What is the time?/What day is it?\" to more complex questions.");

            // Add user's response to the conversation
            chat.AppendUserInput(inputText);

            // Get the response from OpenAI
            return await chat.GetResponseFromChatbotAsync();
        }

        // Here's the Main method where we put everything together
        async static Task Main(string[] args)
        {
            CheckEnvironmentVariables(); // Ensure required environment variables are set

            // pUbErtYy (ASCII art)
            //Console.WriteLine("                        ,,                                                     \r\n         `7MMF'   `7MF'*MM      `7MM\"\"\"YMM             mm `YMM'   `MM'         \r\n           MM       M   MM        MM    `7             MM   VMA   ,V           \r\n`7MMpdMAo. MM       M   MM,dMMb.  MM   d    `7Mb,od8 mmMMmm  VMA ,V `7M'   `MF'\r\n  MM   `Wb MM       M   MM    `Mb MMmmMM      MM' \"'   MM     VMMP    VA   ,V  \r\n  MM    M8 MM       M   MM     M8 MM   Y  ,   MM       MM      MM      VA ,V   \r\n  MM   ,AP YM.     ,M   MM.   ,M9 MM     ,M   MM       MM      MM       VVV    \r\n  MMbmmd'   `bmmmmd\"'   P^YbmdP'.JMMmmmmMMM .JMML.     `Mbmo .JMML.     ,V     \r\n  MM                                                                   ,V      \r\n.JMML.                                                              OOb\"       \n\n");
            // 49 (ASCII art)
            Console.WriteLine("                                    \r\n     ,AM  .d*\"*bg.\r\n    AVMM 6MP    Mb\r\n  ,W' MM YMb    MM\r\n,W'   MM  `MbmmdM9\r\nAmmmmmMMmm     .M'\r\n      MM     .d9  \r\n      MM   m\"'    \n\n");


            // Set up Speech SDK configuration
            SpeechConfig speechConfig = SpeechConfig.FromSubscription(speechKey, speechRegion);
            speechConfig.SpeechRecognitionLanguage = "en-US";


            while (true)
            {
                // Set up audio configuration using the default microphone input
                var audioConfig = AudioConfig.FromDefaultMicrophoneInput();

                // Gets the location of user using the LocationLogic class
                GetLocation location = new GetLocation();


                // Waits for keyword ("Hey Computer")
                var keywordModel = KeywordRecognitionModel.FromFile("C:\\Users\\15048\\source\\repos\\Personal Assistant(.Net Framework)\\bin\\Debug\\42b1e1dd-320e-4426-b693-4b7c163d4e46.table");
                var keywordRecognizer = new KeywordRecognizer(audioConfig);
                KeywordRecognitionResult result = await keywordRecognizer.RecognizeOnceAsync(keywordModel);

                PlaySound();
                Thread.Sleep(500);

                Console.WriteLine("\nAssistant: Hello! What can I do for you?\n");
                await SynthesizeTextToSpeech("en-US-AndrewNeural", "Hello! What can I do for you?");

                // Create a speech recognizer
                var speechRecognizer = new SpeechRecognizer(speechConfig, audioConfig);

                // Recognize microphone input
                var speechRecognitionResult = await speechRecognizer.RecognizeOnceAsync();
                ConvertSpeechToText(speechRecognitionResult);

                // Use the recognized text
                string recognizedText = speechRecognitionResult.Text.ToLower();


                // If the recognized text detects exit. Then exit the loop.
                if (recognizedText.Contains("exit"))
                {
                    Console.WriteLine("Exiting the program");
                    await SynthesizeTextToSpeech("en-US-AndrewNeural", "Exiting the program");
                    Environment.Exit(0);
                }
                else if (recognizedText.Contains("close"))
                {
                    Console.WriteLine("Assistant: Ok! Closing current window now.\n");
                    await SynthesizeTextToSpeech("en-US-AndrewNeural", "Ok! Closing current window now.");
                    Process.GetCurrentProcess().Close();
                }
                else if (recognizedText == "what time is it?" || recognizedText == "what's the time?")
                {
                    DateTime time = DateTime.Now.ToLocalTime();
                    string response = $"It's {time.ToString("t")}\n";
                    Console.WriteLine("Assistant: " + response);
                    await SynthesizeTextToSpeech("en-US-AndrewNeural", response);
                    Thread.Sleep(500);
                }
                else if (recognizedText == "what day is it?")
                {
                    DateTime today = DateTime.Now.Date;
                    string response = $"It's {today.ToString("t")}\n";
                    Console.WriteLine("Assistant: " + response);
                    await SynthesizeTextToSpeech("en-US-AndrewNeural", response);
                    Thread.Sleep(500);
                }
                else if (recognizedText.StartsWith("search up") || recognizedText.StartsWith("google"))
                {
                    string query = recognizedText.Contains("search up") ? recognizedText.Remove("search up".Length) : recognizedText.Remove("google".Length);
                    Console.WriteLine($"Assistant: Ok! Searching up {query.TrimEnd('.')} now\n");
                    SynthesizeTextToSpeech("en-US-AndrewNeural", $"Ok! Searching up {query.TrimEnd('.')} now");
                    Process.Start("https://www.google.com/search?q=" + query.TrimEnd('.'));
                }
                else if (recognizedText.Contains("youtube"))
                {
                    Console.WriteLine("Assistant: Ok! Opening YouTube now.\n");
                    SynthesizeTextToSpeech("en-US-AndrewNeural", "Ok! Opening Youtube now.");
                    Process.Start("https://www.youtube.com");
                }
                else if (recognizedText.Contains("weather"))
                {
                    GetWeather weather = new GetWeather(weatherAPIKey);

                    try
                    {
                        await weather.GetWeatherData();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("An error occurred: " + ex.Message);
                    }
                }
                else if (recognizedText.Contains("pray times") || recognizedText.Contains("prayer times"))
                {
                    double latitude = location.GetLatitude().Result;
                    double longitude = location.GetLongitude().Result;

                    GetPrayTimesLogic prayerTimesLogic = new GetPrayTimesLogic(latitude, longitude);

                    await prayerTimesLogic.AnnouncePrayerTimes(DateTime.Now);
                }
                else if (recognizedText == "shut down." || recognizedText == "restart.")
                {
                    Console.WriteLine("Assistant: Are you sure?");
                    SynthesizeTextToSpeech("en-US-AndrewNeural", "Are you sure?");

                    SpeechRecognizer confirmationSpeechRecognizer = new SpeechRecognizer(speechConfig, audioConfig);
                    SpeechRecognitionResult confirmationResult = await confirmationSpeechRecognizer.RecognizeOnceAsync();
                    ConvertSpeechToText(confirmationResult);
                    var confirmation = confirmationResult.Text.ToLower();

                    if (confirmation == "yes.")
                    {
                        string action = recognizedText.Contains("shut down") ? "Shutting down" : "Restarting now";
                        Console.WriteLine($"Assistant: Ok. {action}");
                        await SynthesizeTextToSpeech("en-US-AndrewNeural", $"Ok. {action}");
                        Process.Start("shutdown", recognizedText.Contains("shut down") ? "/s /t 0" : "/r /t 0");
                    }
                    else
                    {
                        Console.WriteLine("\n");
                        Thread.Sleep(500);
                    }
                }
                else if (recognizedText.Contains("visual studio") || recognizedText.Contains("code") || recognizedText.Contains("coding"))
                {
                    Console.WriteLine("Assistant: Ok! Opening Visual Studio now.\n");
                    SynthesizeTextToSpeech("en-US-AndrewNeural", "Ok! Opening Visual Studio now.");
                    Process.Start("devenv");
                }
                else
                {
                    string openaiResponse = await GenerateOpenAIResponse(recognizedText);
                    Console.WriteLine("Assistant: " + openaiResponse + " Is there anything else you'd like to ask?\n");

                    // Synthesize the OpenAI response using text-to-speech
                    await SynthesizeTextToSpeech("en-US-AndrewNeural", openaiResponse + " Is there anything else you'd like to ask?");
                }
            }
        }
    }
}


