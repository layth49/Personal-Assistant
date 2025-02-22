﻿using System;
using System.Media;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.CognitiveServices.Speech;
using WindowsInput;
using Python.Runtime;
using Personal_Assistant.Arduino;
using Personal_Assistant.Geolocator;
using Personal_Assistant.GeminiClient;
using Personal_Assistant.SpeechManager;
using Personal_Assistant.LightAutomator;
using Personal_Assistant.WeatherService;
using Personal_Assistant.PrayerTimesCalculator;
using Personal_Assistant.PlaystationController;

namespace Personal_Assistant
{
    class Program
    {
        // Replace these with your own Cognitive Services Speech API subscription key and service region endpoint
        public static readonly string geminiApiKey = Environment.GetEnvironmentVariable("GEMINIAPI_KEY");
        public static readonly string weatherAPIKey = Environment.GetEnvironmentVariable("WEATHERAPI_KEY");

        // This is used to turn my personal lights and is therefore not required
        public static string ipAddressPlug = Environment.GetEnvironmentVariable("IP_ADDRESS:PLUG");
        public static string ipAddressSwitch = Environment.GetEnvironmentVariable("IP_ADDRESS:SWITCH");


        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        // Inform the user that environment variables are required and how to set them
        static void CheckEnvironmentVariables()
        {
            if (string.IsNullOrEmpty(geminiApiKey) || string.IsNullOrEmpty(SpeechService.speechKey) || string.IsNullOrEmpty(SpeechService.speechRegion) || string.IsNullOrEmpty(weatherAPIKey))
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
                Console.ReadLine();
                Environment.Exit(1);
            }
        }

        // Here's the Main method where we put everything together
        async static Task Main()
        {
            CheckEnvironmentVariables(); // Ensure required environment variables are set

            // 49 (ASCII art)
            Console.WriteLine("                                    \r\n     ,AM  .d*\"*bg.\r\n    AVMM 6MP    Mb\r\n  ,W' MM YMb    MM\r\n,W'   MM  `MbmmdM9\r\nAmmmmmMMmm     .M'\r\n      MM     .d9  \r\n      MM   m\"'    \n\n");

            InputSimulator simulator = new InputSimulator();
            while (true)
            {
                int hour = DateTime.Now.Hour;

                SpeechService speechManager = new SpeechService();

                // Gets the location of user using the Geolocator class
                GetLocation location = new GetLocation();

                // Preloading the weather to get faster response time
                GetWeather weather = new GetWeather(weatherAPIKey);

                LightControl lightControl = new LightControl();

                PlaystationControl playstationControl = new PlaystationControl();

                ArduinoService arduino = new ArduinoService();

                // Waits for keyword ("Hey Computer")
                await speechManager.KeywordRecognizer();

                
                speechManager.AudioVisualizer();

                simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.MEDIA_STOP);    // Stop all media

                // Create a speech recognizer
                var speechRecognizer = new SpeechRecognizer(speechManager.speechConfig);

                Random random = new Random();

                if (hour <= 12)
                {
                    string[] morningGreetings = new string[] 
                    {
                        "Good morning! What can I do for you?",
                        "Morning! How can I assist you today?",
                        "Rise and shine! What's on your agenda?",
                        "Good morning! How can I help?",
                        "Morning! What's up?",
                    };
                    string greeting = morningGreetings[random.Next(morningGreetings.Length)];
                    Console.WriteLine($"\nAssistant: {greeting}\n");
                    await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", greeting);
                }
                else if (hour > 12 && hour <= 18)
                {
                    string[] afternoonGreetings = new string[] 
                    {
                        "Good afternoon! What can I do for you?",
                        "Afternoon! How can I help?",
                        "Hi! What's up?",
                        "Good afternoon! I'm here to assist you.",
                        "Hope you're having a great afternoon! How can I help?",
                        "Hello there! What can I do for you this afternoon?"
                    };
                    string greeting = afternoonGreetings[random.Next(afternoonGreetings.Length)];
                    Console.WriteLine($"\nAssistant: {greeting}\n");
                    await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", greeting);
                }
                else if (hour >= 18 && hour <= 21)
                {
                    string[] eveningGreetings = new string[] 
                    {
                        "Good evening! What can I do for you?",
                        "Evening! How can I help?",
                        "Hi! What's up?",
                        "Good evening! I'm here to assist you.",
                        "Hope your evening is going well! What can I do for you?",
                        "Hello! How can I help you this evening?"
                    };
                    string greeting = eveningGreetings[random.Next(eveningGreetings.Length)];
                    Console.WriteLine($"\nAssistant: {greeting}\n");
                    await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", greeting);
                }
                else
                {
                   string[] nightGreetings = new string[]
                   {
                        "Good night! How can I assist you?",
                        "Hi there! How can I help you tonight?",
                        "Hello! What do you need this late?",
                        "Good night! I'm here if you need anything.",
                        "Hope you're having a peaceful night. How can I assist?",
                        "Evening! Feel free to ask me anything before you rest."
                   };
                    string greeting = nightGreetings[random.Next(nightGreetings.Length)];
                    Console.WriteLine($"\nAssistant: {greeting}\n");
                    await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", greeting);
                }

                // Recognize microphone input
                var speechRecognitionResult = await speechRecognizer.RecognizeOnceAsync();
                speechManager.ConvertSpeechToText(speechRecognitionResult);

                // Use the recognized text
                string recognizedText = speechRecognitionResult.Text.ToLower();

                // Explain himself
                if (recognizedText == "who are you?")
                {
                    Console.WriteLine("Assistant: Hi! I'm BOT49, your own personal assistant!");
                    await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "hhi! I'm bot 49, your own personal assistant!");
                }
                // Close the assistant
                else if (recognizedText.Contains("exit"))
                {
                    Console.WriteLine("Exiting the program");
                    await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Exiting the program");

                    Environment.Exit(0);
                }
                // Nevermind
                else if (recognizedText.Contains("never mind"))
                {
                    Console.WriteLine("Assistant: Ok! Let me know if you need anything else.");
                    await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Let me know if you need anything else.");
                }
                // What time is it?
                else if (recognizedText == "what time is it?" || recognizedText == "what's the time?")
                {
                    DateTime time = DateTime.Now.ToLocalTime();
                    string response = $"It's {time:t}\n";
                    Console.WriteLine("Assistant: " + response);
                    await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", response);
                }
                // What day is it?
                else if (recognizedText == "what day is it?")
                {
                    DateTime today = DateTime.Now.Date;
                    string response = $"It's {today:D}\n";
                    Console.WriteLine("Assistant: " + response);
                    await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", response);
                }
                // Search up something
                else if (recognizedText.StartsWith("search up") || recognizedText.StartsWith("google"))
                {
                    string query = recognizedText.Contains("search up") ? recognizedText.Remove(0, "search up".Length).TrimEnd('.', '?') : recognizedText.Remove(0, "google".Length).TrimEnd('.');
                    Console.WriteLine($"Assistant: Ok! Searching up{query} now\n");
                    speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", $"Okay! Searching up {query} now");
                    Process.Start("https://www.google.com/search?q=" + query);
                }
                // Open YouTube
                else if (recognizedText.Contains("youtube"))
                {
                    Console.WriteLine("Assistant: Ok! Would you like a specific video or to just open it?\n");
                    await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Would you like a specific video or to just open it?");

                    SpeechRecognizer confirmationSpeechRecognizer = new SpeechRecognizer(speechManager.speechConfig);
                    SpeechRecognitionResult confirmationResult = await confirmationSpeechRecognizer.RecognizeOnceAsync();
                    speechManager.ConvertSpeechToText(confirmationResult);
                    string confirmation = confirmationResult.Text.ToLower();

                    // Search for a specific video
                    if (confirmation.StartsWith("search for") || confirmation.StartsWith("search up"))
                    {
                        string query = confirmation.Contains("search up") ? confirmation.Remove(0, "search up ".Length).TrimEnd('.') : confirmation.Remove(0, "search for ".Length).TrimEnd('.');
                        Console.WriteLine($"Assistant: Ok! Searching for {query} now");
                        speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", $"Searching for {query} now");
                        Process.Start($"https://www.youtube.com/results?search_query={query.Replace(" ", "+")}");
                    }

                    // Just open YouTube
                    else if (confirmation.Contains("open"))
                    {
                        Console.WriteLine("Assistant: Ok! Opening YouTube now.\n");
                        speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Opening Youtube now.");
                        Process.Start("https://www.youtube.com");
                    }
                    // Don't open YouTube
                    else if (recognizedText.Contains("nevermind") || recognizedText.Contains("never mind"))
                    {
                        Console.WriteLine("Assistant: Ok! Let me know if you need anything else.");
                        await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Let me know if you need anything else.");
                    }
                }
                // Open IDE
                else if (recognizedText.Contains("visual studio") || recognizedText.Contains("code") || recognizedText.Contains("coding"))
                {
                    Console.WriteLine("Assistant: Ok! Opening Visual Studio now.\n");
                    speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Opening Visual Studio now.");
                    Process.Start("devenv");
                }
                // Turn on PS5
                else if (recognizedText.Contains("turn on") && recognizedText.Contains("playstation") || recognizedText.Contains("ps5"))
                {
                    playstationControl.TurnOnPlaystation();
                }
                // Light control
                else if (recognizedText.Contains("turn on") && recognizedText.Contains("lights"))
                {
                    if (recognizedText.Contains("led"))
                    {
                        lightControl.TurnOnLights("LED", ipAddressPlug);
                    }
                    else if (recognizedText.Contains("bedroom"))
                    {
                        lightControl.TurnOnLights("bedroom", ipAddressSwitch);
                    }
                }
                else if (recognizedText.Contains("turn off") && recognizedText.Contains("lights"))
                {
                    if (recognizedText.Contains("led"))
                    {
                        lightControl.TurnOffLights("LED", ipAddressPlug);
                    }
                    else if (recognizedText.Contains("bedroom"))
                    {
                        lightControl.TurnOffLights("bedroom", ipAddressSwitch);
                    }
                }
                // Weather
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
                }
                // Prayer times
                else if (recognizedText.Contains("pray times") || recognizedText.Contains("prayer times"))
                {
                    double latitude = location.GetLatitude().Result;
                    double longitude = location.GetLongitude().Result;

                    GetPrayerTimes prayerTimesLogic = new GetPrayerTimes(latitude, longitude);

                    await prayerTimesLogic.AnnouncePrayerTimes(DateTime.Now);
                }
                // Door Control
                else if (recognizedText.Contains("door")) {

                    if (recognizedText.Contains("open"))
                    {
                        arduino.ArduinoCommunication("OPEN");
                        Console.WriteLine("Assistant: Ok! Opening your door now.");
                        await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Opening your door now.");
                    }
                    else if (recognizedText.Contains("close"))
                    {
                        arduino.ArduinoCommunication("CLOSE");
                        Console.WriteLine("Assistant: Ok! Closing your door now.");
                        await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Closing your door now.");
                    }
                }
                // Shut down or restart
                else if (recognizedText == "shut down." || recognizedText == "restart.")
                {
                    Console.WriteLine("Assistant: Are you sure?");
                    await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Are you sure?");

                    SpeechRecognizer confirmationSpeechRecognizer = new SpeechRecognizer(speechManager.speechConfig);
                    SpeechRecognitionResult confirmationResult = await confirmationSpeechRecognizer.RecognizeOnceAsync();
                    speechManager.ConvertSpeechToText(confirmationResult);

                    if (confirmationResult.Text.ToLower() == "yes.")
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
                // Gemini API
                else
                {
                    if (speechRecognitionResult.Reason != ResultReason.NoMatch)
                    {
                        string geminiResponse = await GeminiService.GenerateGeminiResponse(recognizedText, geminiApiKey, "gemini-1.5-flash");

                        Console.WriteLine("Assistant: " + geminiResponse);
                        await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", geminiResponse);
                    }
                }
            }
        }
    }
}