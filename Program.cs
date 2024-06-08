using System;
using System.Media;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using WindowsInput;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Personal_Assistant.Geolocator;
using Personal_Assistant.PrayerTimesCalculator;
using Personal_Assistant.WeatherService;
using Personal_Assistant.GeminiClient;
using Personal_Assistant.SpeechManager;

namespace Personal_Assistant
{
    class Program
    {
        // Replace these with your own Cognitive Services Speech API subscription key and service region endpoint
        public static readonly string geminiApiKey = Environment.GetEnvironmentVariable("GEMINIAPI_KEY");
        public static readonly string speechKey = Environment.GetEnvironmentVariable("SPEECH_KEY");
        public static readonly string speechRegion = Environment.GetEnvironmentVariable("SPEECH_REGION");
        public static readonly string weatherAPIKey = Environment.GetEnvironmentVariable("WEATHERAPI_KEY");

        // Inform the user that environment variables are required and how to set them
        static void CheckEnvironmentVariables()
        {
            if (string.IsNullOrEmpty(geminiApiKey) || string.IsNullOrEmpty(speechKey) || string.IsNullOrEmpty(speechRegion) || string.IsNullOrEmpty(weatherAPIKey))
            {
                Console.WriteLine("Error: Please set the following environment variables before running the program:");
                Console.WriteLine("  - GEMINIAPI_KEY: Your Gemini API key");
                Console.WriteLine("  - SPEECH_KEY: Your Cognitive Services Speech API subscription key");
                Console.WriteLine("  - SPEECH_REGION: Your Cognitive Services Speech API service region (e.g., westus)");
                Console.WriteLine("  - WEATHERAPI_KEY: Your OpenWeatherMap API Key");
                Console.WriteLine("You can set them using the following commands (replace 'your_key' with your actual keys):");
                Console.WriteLine("  - setx GEMINIAPI_KEY your_gemini_key");
                Console.WriteLine("  - setx SPEECH_KEY your_speech_key");
                Console.WriteLine("  - setx SPEECH_REGION your_speech_region");
                Console.WriteLine("  - setx WEATHERAPI_KEY your_weatherapi_key");
                Thread.Sleep(5000);
                Environment.Exit(1);
            }
        }

        public static void PlaySound() // Plays a chime sound to indicate the assistant is ready
        {
            SoundPlayer player = new SoundPlayer();
            player.SoundLocation = @"C:\Users\15048\vstudio\repos\Projects\Personal Assistant\bin\Debug\chime.wav";
            player.Play();
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
                // Set up audio configuration using the default microphone input
                AudioConfig audioConfig = AudioConfig.FromDefaultMicrophoneInput();

                int hour = DateTime.Now.Hour;

                SpeechService speechManager = new SpeechService(speechKey, speechRegion);

                // Gets the location of user using the LocationLogic class
                GetLocation location = new GetLocation();

                // Preloading the weather to get faster response time
                GetWeather weather = new GetWeather(weatherAPIKey);

                // Waits for keyword ("Hey Computer")
                using (var keywordModel = KeywordRecognitionModel.FromFile(@"C:\Users\15048\vstudio\repos\Projects\Personal Assistant\bin\Debug\42b1e1dd-320e-4426-b693-4b7c163d4e46.table"))
                {
                    var keywordRecognizer = new KeywordRecognizer(audioConfig);
                    KeywordRecognitionResult result = await keywordRecognizer.RecognizeOnceAsync(keywordModel);
                }

                PlaySound();
                simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Pause

                // Create a speech recognizer
                var speechRecognizer = new SpeechRecognizer(speechConfig, audioConfig);

                if (hour < 12)
                {
                    Console.WriteLine("\nAssistant: Good Morning! What can I do for you?\n");
                    await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Good Morning! What can I do for you?");
                }//                         6 PM
                else if (hour >= 12 && hour < 18)
                {
                    Console.WriteLine("\nAssistant: Good Evening! What can I do for you?\n");
                    await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Good Evening! What can I do for you?");
                }
                else
                {
                    Console.WriteLine("\nAssistant: Good Afternoon! What can I do for you?\n");
                    await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Good Afternoon! What can I do for you?");
                }


                // Recognize microphone input
                var speechRecognitionResult = await speechRecognizer.RecognizeOnceAsync();
                speechManager.ConvertSpeechToText(speechRecognitionResult);

                // Use the recognized text
                string recognizedText = speechRecognitionResult.Text.ToLower();

                if (recognizedText == "who are you?")
                {
                    Console.WriteLine("Assistant: Hi! I'm BOT49, your own personal assistant!");
                    await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "hhi! I'm bot 49, your own personal assistant!");

                    simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play
                }
                else if (recognizedText.Contains("exit"))
                {
                    Console.WriteLine("Exiting the program");
                    await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Exiting the program");

                    simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play
                    Environment.Exit(0);
                }
                // Needs to be fixed/deleted
                else if (recognizedText.Contains("close"))
                {
                    Console.WriteLine("Assistant: Ok! Closing current window now.\n");
                    await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Closing current window now.");
                    Console.WriteLine(Process.GetCurrentProcess());

                    Process.GetCurrentProcess().CloseMainWindow();

                    simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play
                }
                else if (recognizedText.Contains("nevermind") || recognizedText.Contains("never mind"))
                {
                    Console.WriteLine("Assistant: Ok! Let me know if you need anything else.");
                    await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Let me know if you need anything else.");

                    simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play
                }
                else if (recognizedText == "what time is it?" || recognizedText == "what's the time?")
                {
                    DateTime time = DateTime.Now.ToLocalTime();
                    string response = $"It's {time:t}\n";
                    Console.WriteLine("Assistant: " + response);
                    await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", response);

                    simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play
                }
                else if (recognizedText == "what day is it?")
                {
                    DateTime today = DateTime.Now.Date;
                    string response = $"It's {today:D}\n";
                    Console.WriteLine("Assistant: " + response);
                    await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", response);

                    simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play
                }
                else if (recognizedText.StartsWith("search up") || recognizedText.StartsWith("google"))
                {
                    string query = recognizedText.Contains("search up") ? recognizedText.Remove(0, "search up".Length).TrimEnd('.', '?') : recognizedText.Remove(0, "google".Length).TrimEnd('.');
                    Console.WriteLine($"Assistant: Ok! Searching up{query} now\n");
                    speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", $"Okay! Searching up {query} now");
                    Process.Start("https://www.google.com/search?q=" + query);

                    simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play
                }
                else if (recognizedText.Contains("youtube"))
                {
                    Console.WriteLine("Assistant: Ok! Would you like a specific video or to just open it?\n");
                    await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Would you like a specific video or to just open it?");

                    SpeechRecognizer confirmationSpeechRecognizer = new SpeechRecognizer(speechConfig, audioConfig);
                    SpeechRecognitionResult confirmationResult = await confirmationSpeechRecognizer.RecognizeOnceAsync();
                    speechManager.ConvertSpeechToText(confirmationResult);
                    string confirmation = confirmationResult.Text.ToLower();

                    if (confirmation == "open." || confirmation == "open it." || confirmation == "just open it.")
                    {
                        Console.WriteLine("Assistant: Ok! Opening YouTube now.\n");
                        speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Opening Youtube now.");
                        Process.Start("https://www.youtube.com");

                        simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play
                    }
                    else if (confirmation.StartsWith("search for") || confirmation.StartsWith("search up"))
                    {
                        string query = confirmation.Contains("search up") ? confirmation.Remove(0, "search up ".Length).TrimEnd('.') : confirmation.Remove(0, "search for ".Length).TrimEnd('.');
                        Console.WriteLine($"Assistant: Ok! Searching for {query} now");
                        speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", $"Searching for {query} now");
                        Process.Start($"https://www.youtube.com/results?search_query={query.Replace(" ", "+")}");

                        simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play
                    }
                    else if (recognizedText.Contains("nevermind") || recognizedText.Contains("never mind"))
                    {
                        Console.WriteLine("Assistant: Ok! Let me know if you need anything else.");
                        await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Let me know if you need anything else.");

                        simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play
                    }
                }
                else if (recognizedText.Contains("visual studio") || recognizedText.Contains("code") || recognizedText.Contains("coding"))
                {
                    Console.WriteLine("Assistant: Ok! Opening Visual Studio now.\n");
                    speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Opening Visual Studio now.");
                    Process.Start("devenv");

                    simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play
                }
                else if (recognizedText.Contains("turn on") && recognizedText.Contains("playstation") || recognizedText.Contains("ps5"))
                {
                    Console.WriteLine("Assistant: Ok! Turning on your PlayStation 5 now.\n");
                    Process remoteplay = Process.Start(@"C:\Program Files (x86)\Sony\PS Remote Play\RemotePlay.exe");
                    await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Turning on your PlayStation 5 now.");
                    simulator.Mouse.MoveMouseTo(32500, 40000).LeftButtonClick();

                    simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play

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

                    simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play
                }
                else if (recognizedText.Contains("pray times") || recognizedText.Contains("prayer times"))
                {
                    double latitude = location.GetLatitude().Result;
                    double longitude = location.GetLongitude().Result;

                    GetPrayerTimes prayerTimesLogic = new GetPrayerTimes(latitude, longitude);

                    await prayerTimesLogic.AnnouncePrayerTimes(DateTime.Now);

                    simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play
                }
                else if (recognizedText == "shut down." || recognizedText == "restart.")
                {
                    Console.WriteLine("Assistant: Are you sure?");
                    await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Are you sure?");

                    SpeechRecognizer confirmationSpeechRecognizer = new SpeechRecognizer(speechConfig, audioConfig);
                    SpeechRecognitionResult confirmationResult = await confirmationSpeechRecognizer.RecognizeOnceAsync();
                    speechManager.ConvertSpeechToText(confirmationResult);
                    var confirmation = confirmationResult.Text.ToLower();

                    if (confirmation == "yes.")
                    {
                        string action = recognizedText.Contains("shut down") ? "Shutting down" : "Restarting now";
                        Console.WriteLine($"Assistant: Ok. {action}");
                        await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", $"Ok. {action}");
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
                        // Do nothing because it's already handled in the SpeechManager Class
                    }
                    else
                    {
                        string geminiResponse = await GeminiService.GenerateGeminiResponse(recognizedText, geminiApiKey, "gemini-pro");

                        Console.WriteLine("Assistant: " + geminiResponse);
                        await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", geminiResponse);

                        simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_PLAY_PAUSE);    // Play
                    }
                }
            }
        }
    }
}