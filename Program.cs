using System;
using System.Media;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Personal_Assistant.LocationLogic;
using Personal_Assistant.PrayTimesLogic;
using Personal_Assistant.WeatherLogic;
using Personal_Assistant.GeminiLogic;
using WindowsInput;

namespace Personal_Assistant
{
    class Program
    {
        // Replace these with your own Cognitive Services Speech API subscription key and service region endpoint
        static readonly string geminiApiKey = Environment.GetEnvironmentVariable("GEMINIAPI_KEY");
        static readonly string speechKey = Environment.GetEnvironmentVariable("SPEECH_KEY");
        static readonly string speechRegion = Environment.GetEnvironmentVariable("SPEECH_REGION");
        static readonly string weatherAPIKey = Environment.GetEnvironmentVariable("WEATHERAPI_KEY");

        // Inform the user that environment variables are required and how to set them
        static void CheckEnvironmentVariables()
        {
            if (string.IsNullOrEmpty(geminiApiKey) || string.IsNullOrEmpty(speechKey) || string.IsNullOrEmpty(speechRegion))
            {
                Console.WriteLine("Error: Please set the following environment variables before running the program:");
                Console.WriteLine("  - OPENAIAPI_KEY: Your OpenAI API key");
                Console.WriteLine("  - SPEECH_KEY: Your Cognitive Services Speech API subscription key");
                Console.WriteLine("  - SPEECH_REGION: Your Cognitive Services Speech API service region (e.g., westus)");
                Console.WriteLine("You can set them using the following commands (replace 'your_key' with your actual keys):");
                Console.WriteLine("  - setx GEMINIAPI_KEY your_gemini_key");
                Console.WriteLine("  - setx SPEECH_KEY your_speech_key");
                Console.WriteLine("  - setx SPEECH_REGION your_speech_region");
                Console.WriteLine("  - setx WEATHERAPI_KEY your_weatherapi_key");
                Environment.Exit(1);
            }
        }

        public static void PlaySound() // Plays a chime sound to indicate the assistant is ready
        {
            SoundPlayer player = new SoundPlayer();
            player.SoundLocation = @"./chime.wav";
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

        // Here's the Main method where we put everything together
        async static Task Main()
        {
            CheckEnvironmentVariables(); // Ensure required environment variables are set

            // 49 (ASCII art)
            Console.WriteLine("                                    \r\n     ,AM  .d*\"*bg.\r\n    AVMM 6MP    Mb\r\n  ,W' MM YMb    MM\r\n,W'   MM  `MbmmdM9\r\nAmmmmmMMmm     .M'\r\n      MM     .d9  \r\n      MM   m\"'    \n\n");


            // Set up Speech SDK configuration
            SpeechConfig speechConfig = SpeechConfig.FromSubscription(speechKey, speechRegion);
            speechConfig.SpeechRecognitionLanguage = "en-US";

            InputSimulator simulator = new InputSimulator();
            while (true)
            {
                Console.WriteLine(Process.GetCurrentProcess());

                // Set up audio configuration using the default microphone input
                AudioConfig audioConfig = AudioConfig.FromDefaultMicrophoneInput();

                int hour = DateTime.Now.Hour;

                // Gets the location of user using the LocationLogic class
                GetLocation location = new GetLocation();

                // Preloading the weather to get faster response time
                GetWeather weather = new GetWeather(weatherAPIKey);

                // Waits for keyword ("Hey Computer")
                var keywordModel = KeywordRecognitionModel.FromFile(@"./42b1e1dd-320e-4426-b693-4b7c163d4e46.table");
                var keywordRecognizer = new KeywordRecognizer(audioConfig);
                KeywordRecognitionResult result = await keywordRecognizer.RecognizeOnceAsync(keywordModel);

                PlaySound();
                simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play/Pause

                // Create a speech recognizer
                var speechRecognizer = new SpeechRecognizer(speechConfig, audioConfig);

                if (hour < 12)
                {
                    Console.WriteLine("\nAssistant: Good Morning! What can I do for you?\n");
                    await SynthesizeTextToSpeech("en-US-AndrewNeural", "Good Morning! What can I do for you?");
                }//                         6 PM
                else if (hour >= 12 && hour < 18)
                {
                    Console.WriteLine("\nAssistant: Good Evening! What can I do for you?\n");
                    await SynthesizeTextToSpeech("en-US-AndrewNeural", "Good Evening! What can I do for you?");
                }
                else
                {
                    Console.WriteLine("\nAssistant: Good Afternoon! What can I do for you?\n");
                    await SynthesizeTextToSpeech("en-US-AndrewNeural", "Good Afternoon! What can I do for you?");
                }


                // Recognize microphone input
                var speechRecognitionResult = await speechRecognizer.RecognizeOnceAsync();
                ConvertSpeechToText(speechRecognitionResult);

                // Use the recognized text
                string recognizedText = speechRecognitionResult.Text.ToLower();

                if (recognizedText == "who are you?")
                {
                    Console.WriteLine("Assistant: Hi! I'm BOT49, your own personal assistant!");
                    await SynthesizeTextToSpeech("en-US-AndrewNeural", "hhi! I'm bot 49, your own personal assistant!");

                    simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play/Pause
                }
                else if (recognizedText.Contains("exit"))
                {
                    Console.WriteLine("Exiting the program");
                    await SynthesizeTextToSpeech("en-US-AndrewNeural", "Exiting the program");

                    simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play/Pause
                    Environment.Exit(0);
                }
                else if (recognizedText.Contains("close"))
                {
                    Console.WriteLine("Assistant: Ok! Closing current window now.\n");
                    await SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Closing current window now.");
                    Console.WriteLine(Process.GetCurrentProcess());

                    Process.GetCurrentProcess().CloseMainWindow();

                    simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play/Pause
                }
                else if (recognizedText.Contains("nevermind") || recognizedText.Contains("never mind"))
                {
                    Console.WriteLine("Assistant: Ok! Let me know if you need anything else.");
                    await SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Let me know if you need anything else.");

                    simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play/Pause
                }
                else if (recognizedText == "what time is it?" || recognizedText == "what's the time?")
                {
                    DateTime time = DateTime.Now.ToLocalTime();
                    string response = $"It's {time:t}\n";
                    Console.WriteLine("Assistant: " + response);
                    await SynthesizeTextToSpeech("en-US-AndrewNeural", response);

                    simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play/Pause
                }
                else if (recognizedText == "what day is it?")
                {
                    DateTime today = DateTime.Now.Date;
                    string response = $"It's {today:D}\n";
                    Console.WriteLine("Assistant: " + response);
                    await SynthesizeTextToSpeech("en-US-AndrewNeural", response);

                    simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play/Pause
                }
                else if (recognizedText.StartsWith("search up") || recognizedText.StartsWith("google"))
                {
                    string query = recognizedText.Contains("search up") ? recognizedText.Remove(0, "search up".Length).TrimEnd('.', '?') : recognizedText.Remove(0, "google".Length).TrimEnd('.');
                    Console.WriteLine($"Assistant: Ok! Searching up{query} now\n");
                    SynthesizeTextToSpeech("en-US-AndrewNeural", $"Okay! Searching up {query} now");
                    Process.Start("https://www.google.com/search?q=" + query);

                    simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play/Pause
                }
                else if (recognizedText.Contains("youtube"))
                {
                    Console.WriteLine("Assistant: Ok! Would you like a specific video or to just open it?\n");
                    await SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Would you like a specific video or to just open it?");

                    SpeechRecognizer confirmationSpeechRecognizer = new SpeechRecognizer(speechConfig, audioConfig);
                    SpeechRecognitionResult confirmationResult = await confirmationSpeechRecognizer.RecognizeOnceAsync();
                    ConvertSpeechToText(confirmationResult);
                    string confirmation = confirmationResult.Text.ToLower();

                    if (confirmation == "open." || confirmation == "open it." || confirmation == "just open it.")
                    {
                        Console.WriteLine("Assistant: Ok! Opening YouTube now.\n");
                        SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Opening Youtube now.");
                        Process.Start("https://www.youtube.com");

                        simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play/Pause
                    }
                    else if (confirmation.StartsWith("search for") || confirmation.StartsWith("search up"))
                    {
                        string query = confirmation.Contains("search up") ? confirmation.Remove(0, "search up ".Length).TrimEnd('.') : confirmation.Remove(0, "search for ".Length).TrimEnd('.');
                        Console.WriteLine($"Assistant: Ok! Searching for {query} now");
                        SynthesizeTextToSpeech("en-US-AndrewNeural", $"Searching for {query} now");
                        Process.Start($"https://www.youtube.com/results?search_query={query.Replace(" ", "+")}");

                        simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play/Pause
                    }
                    else if (recognizedText.Contains("nevermind") || recognizedText.Contains("never mind"))
                    {
                        Console.WriteLine("Assistant: Ok! Let me know if you need anything else.");
                        await SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Let me know if you need anything else.");

                        simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play/Pause
                    }
                }
                else if (recognizedText.Contains("visual studio") || recognizedText.Contains("code") || recognizedText.Contains("coding"))
                {
                    Console.WriteLine("Assistant: Ok! Opening Visual Studio now.\n");
                    SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Opening Visual Studio now.");
                    Process.Start("devenv");

                    simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play/Pause
                }
                else if (recognizedText.Contains("playstation") || recognizedText.Contains("ps5"))
                {
                    Console.WriteLine("Assistant: Ok! Turning on your PlayStation 5 now.\n");
                    Process remoteplay = Process.Start(@"C:\Program Files (x86)\Sony\PS Remote Play\RemotePlay.exe");
                    await SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Turning on your PlayStation 5 now.");
                    simulator.Mouse.MoveMouseTo(32500, 40000).LeftButtonClick();


                    // Create a timer for 30 seconds to automatically close Remote Play
                    System.Timers.Timer autoCloseTimer = new System.Timers.Timer(30000);

                    autoCloseTimer.Start();

                    // Define event handler for when timer is done
                    autoCloseTimer.Elapsed += (sender, e) =>
                    {
                        // Send a request to close Remote Play
                        remoteplay.CloseMainWindow();
                        // Confirm request to close Remote Play
                        simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.RETURN);
                        autoCloseTimer.Stop(); // Stop the timer after closing attempt
                    };

                    simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play/Pause
                }
                else if (recognizedText.Contains("weather"))
                {
                    try
                    {
                        await weather.GetWeatherData();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("An error occurred: " + ex.Message);
                    }

                    simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play/Pause
                }
                else if (recognizedText.Contains("pray times") || recognizedText.Contains("prayer times"))
                {
                    double latitude = location.GetLatitude().Result;
                    double longitude = location.GetLongitude().Result;

                    GetPrayTimesLogic prayerTimesLogic = new GetPrayTimesLogic(latitude, longitude);

                    await prayerTimesLogic.AnnouncePrayerTimes(DateTime.Now);

                    simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play/Pause
                }
                else if (recognizedText == "shut down." || recognizedText == "restart.")
                {
                    Console.WriteLine("Assistant: Are you sure?");
                    await SynthesizeTextToSpeech("en-US-AndrewNeural", "Are you sure?");

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
                else
                {

                    if (speechRecognitionResult.Reason == ResultReason.NoMatch)
                    {

                    }
                    else
                    {
                        string geminiResponse = await GeminiClient.GenerateGeminiResponse(recognizedText, geminiApiKey, "gemini-pro");

                        Console.WriteLine("Assistant: " + geminiResponse);
                        await SynthesizeTextToSpeech("en-US-AndrewNeural", geminiResponse);

                        simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play/Pause
                    }
                }
            }
        }
    }
}