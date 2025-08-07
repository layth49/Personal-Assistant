using Microsoft.CognitiveServices.Speech;
using Personal_Assistant.Arduino;
using Personal_Assistant.GeminiClient;
using Personal_Assistant.Geolocator;
using Personal_Assistant.LightAutomator;
using Personal_Assistant.PlaystationController;
using Personal_Assistant.PrayerTimesCalculator;
using Personal_Assistant.SMSController;
using Personal_Assistant.SpeechManager;
using Personal_Assistant.WeatherService;
using Python.Runtime;
using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WindowsInput;
using System.Linq;

namespace Personal_Assistant
{
    class Program
    {
        public static readonly string weatherAPIKey = Environment.GetEnvironmentVariable("WEATHERAPI_KEY");

        public static string ipAddressPlug = Environment.GetEnvironmentVariable("IP_ADDRESS:PLUG");
        public static string ipAddressSwitch = Environment.GetEnvironmentVariable("IP_ADDRESS:SWITCH");

        public static string recognizedText = string.Empty; // Variable to store the recognized text

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        // Inform the user that environment variables are required and how to set them
        static void CheckEnvironmentVariables()
        {
            if (string.IsNullOrEmpty(GeminiService.geminiApiKey) || string.IsNullOrEmpty(SpeechService.speechKey) || string.IsNullOrEmpty(SpeechService.speechRegion) || string.IsNullOrEmpty(weatherAPIKey))
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
        public async static Task Main()
        {
            CheckEnvironmentVariables(); // Ensure required environment variables are set

            // 49 (ASCII art)
            Console.WriteLine("                                    \r\n     ,AM  .d*\"*bg.\r\n    AVMM 6MP    Mb\r\n  ,W' MM YMb    MM\r\n,W'   MM  `MbmmdM9\r\nAmmmmmMMmm     .M'\r\n      MM     .d9  \r\n      MM   m\"'    \n\n");

            // Initializing code
            InputSimulator simulator = new InputSimulator();

            Runtime.PythonDLL = @"..\..\..\..\AppData\Local\Programs\Python\Python312\python312.dll";
            PythonEngine.Initialize();

            dynamic sys = Py.Import("sys");
            sys.path.append(@"..\..\");


            SpeechService speechManager = new SpeechService();

            // Create a speech recognizer
            var speechRecognizer = new SpeechRecognizer(speechManager.speechConfig);

            var phraseList = PhraseListGrammar.FromRecognizer(speechRecognizer);


            Dictionary<string, string> contacts = null;

            var contactsPath = Environment.GetEnvironmentVariable("CONTACTS_PATH");
            if (contactsPath != null && File.Exists(contactsPath))
            {
                var json = File.ReadAllText(contactsPath);
                contacts = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            }

            var phrases = contacts.Keys.ToList();

            foreach (var phrase in phrases)
            {
                phraseList.AddPhrase(phrase);
            }

            while (true)
            {
                // More initialization code

                int hour = DateTime.Now.Hour;


                // Gets the location of user using the Geolocator class
                GetLocation location = new GetLocation();

                // Preloading the weather to get faster response time
                GetWeather weather = new GetWeather(weatherAPIKey);

                LightControl lightControl = new LightControl();

                PlaystationControl playstationControl = new PlaystationControl();

                SMSControl smsControl = new SMSControl();

                ArduinoService arduino = new ArduinoService();

                // Waits for keyword ("Hey 49")
                speechManager.KeywordRecognizer().GetAwaiter().GetResult();


                Random random = new Random();

                // Greet the user based on the time of day
                // Before 12 PM
                if (hour <= 12)
                {
                    string[] morningGreetings = new string[] 
                    {
                        "Good morning! What can I do for you?",
                        "Morning! How can I assist you today?",
                        "Rise and shine! What's on your agenda?",
                        "Good morning! How can I help?",
                        "Morning! What's up?"
                    };
                    string greeting = morningGreetings[random.Next(morningGreetings.Length)];

                    speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", greeting);
                    speechManager.SpeechBubble("Hey 49", greeting);
                }


                // Between 12 PM and 6 PM
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

                    speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", greeting);
                    speechManager.SpeechBubble("Hey 49", greeting);
                }


                // Between 6 PM and 9 PM
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

                    speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", greeting);
                    speechManager.SpeechBubble("Hey 49", greeting);
                }


                // After 9 PM
                else
                {
                   string[] nightGreetings = new string[]
                   {
                        "Good night! How can I assist you?",
                        "Hi there! How can I help you tonight?",
                        "Hello! What do you need this late?",
                        "Good night! I'm here if you need anything.",
                        "Hope you're having a peaceful night. How can I assist?"
                   };
                    string greeting = nightGreetings[random.Next(nightGreetings.Length)];

                    speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", greeting);
                    speechManager.SpeechBubble("Hey 49", greeting);
                }

                // Recognize microphone input
                var speechRecognitionResult = speechRecognizer.RecognizeOnceAsync().GetAwaiter().GetResult();
                speechManager.ConvertSpeechToText(speechRecognitionResult);


                // Use the recognized text
                recognizedText = speechRecognitionResult.Text;
                string lowercaseRecognizedText = recognizedText.ToLower();

                // Explain himself
                if (lowercaseRecognizedText == "who are you?")
                {
                    speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "hhi! I'm layth 49, your own personal assistant!");
                    speechManager.SpeechBubble(recognizedText, "Hi! I'm L.A.I.T.H.49, your own personal assistant!");
                }


                // Close the assistant
                else if (lowercaseRecognizedText.Contains("exit"))
                {
                    speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Alright goodbye!");
                    speechManager.SpeechBubble(recognizedText, "Alright goodbye!");

                    PythonEngine.Shutdown();
                    Environment.Exit(0);
                }


                // Nevermind
                else if (lowercaseRecognizedText.Contains("never mind"))
                {
                    speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Let me know if you need anything else.");
                    speechManager.SpeechBubble(recognizedText, "Okay! Let me know if you need anything else.");
                }


                // What time is it?
                else if (lowercaseRecognizedText == "what time is it?" || lowercaseRecognizedText == "what's the time?")
                {
                    DateTime time = DateTime.Now.ToLocalTime();
                    string response = $"It's {time:t}";

                    speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", response);
                    speechManager.SpeechBubble(recognizedText, response);
                }


                // What day is it?
                else if (lowercaseRecognizedText == "what day is it?")
                {
                    DateTime today = DateTime.Now.Date;
                    string response = $"It's {today:D}";
                   
                    speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", response);
                    speechManager.SpeechBubble(recognizedText, response);
                }


                // Search up something
                else if (lowercaseRecognizedText.StartsWith("search up") || lowercaseRecognizedText.StartsWith("google"))
                {
                    string query = recognizedText.Contains("search up") ? recognizedText.Remove(0, "search up".Length).TrimEnd('.', '?') : recognizedText.Remove(0, "google".Length).TrimEnd('.');

                    speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", $"Okay! Searching up {query} now");
                    speechManager.SpeechBubble(recognizedText, $"Okay! Searching up {query} now");

                    Process.Start("https://www.google.com/search?q=" + query);
                }


                // Open YouTube
                else if (lowercaseRecognizedText.Contains("youtube"))
                {
                    speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Would you like a specific video or to just open it?");
                    speechManager.SpeechBubble(recognizedText, "Okay! Would you like a specific video or to just open it?");

                    SpeechRecognizer confirmationSpeechRecognizer = new SpeechRecognizer(speechManager.speechConfig);
                    SpeechRecognitionResult confirmationResult = confirmationSpeechRecognizer.RecognizeOnceAsync().GetAwaiter().GetResult();
                    speechManager.ConvertSpeechToText(confirmationResult);
                    string confirmation = confirmationResult.Text.ToLower();

                    // Search for a specific video
                    if (confirmation.StartsWith("search for") || confirmation.StartsWith("search up"))
                    {
                        string query = confirmation.Contains("search up") ? confirmation.Remove(0, "search up ".Length).TrimEnd('.') : confirmation.Remove(0, "search for ".Length).TrimEnd('.');

                        speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", $"Ok! Searching for {query} now");
                        speechManager.SpeechBubble(recognizedText, $"Ok! Searching for {query} now");

                        Process.Start($"https://www.youtube.com/results?search_query={query.Replace(" ", "+")}");
                    }

                    // Just open YouTube
                    else if (confirmation.Contains("open"))
                    {
                        speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Opening Youtube now.");
                        speechManager.SpeechBubble(recognizedText, "Okay! Opening YouTube now.");

                        Process.Start("https://www.youtube.com");
                    }
                    // Don't open YouTube
                    else if (lowercaseRecognizedText.Contains("nevermind") || lowercaseRecognizedText.Contains("never mind"))
                    {
                        speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Let me know if you need anything else.");
                        speechManager.SpeechBubble(recognizedText, "Okay! Let me know if you need anything else.");
                    }
                }


                // Open IDE
                else if (lowercaseRecognizedText.Contains("visual studio") || lowercaseRecognizedText.Contains("code") || lowercaseRecognizedText.Contains("coding"))
                {
                    speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Opening Visual Studio now.");
                    speechManager.SpeechBubble(recognizedText, "Okay! Opening Visual Studio now.");

                    Process.Start("devenv");
                }


                // Turn on PS5
                else if (lowercaseRecognizedText.Contains("turn on") && (lowercaseRecognizedText.Contains("playstation") || lowercaseRecognizedText.Contains("ps-5")))
                {
                    playstationControl.TurnOnPlaystation();
                }


                // Light control
                else if (lowercaseRecognizedText.Contains("turn on") && lowercaseRecognizedText.Contains("lights"))
                {
                    if (lowercaseRecognizedText.Contains("led"))
                    {
                        lightControl.TurnOnLights("LED", ipAddressPlug);
                    }
                    else if (lowercaseRecognizedText.Contains("bedroom"))
                    {
                        lightControl.TurnOnLights("bedroom", ipAddressSwitch);
                    }
                }
                else if (lowercaseRecognizedText.Contains("turn off") && lowercaseRecognizedText.Contains("lights"))
                {
                    if (lowercaseRecognizedText.Contains("led"))
                    {
                        lightControl.TurnOffLights("LED", ipAddressPlug);
                    }
                    else if (lowercaseRecognizedText.Contains("bedroom"))
                    {
                        lightControl.TurnOffLights("bedroom", ipAddressSwitch);
                    }
                }


                // Weather
                else if (lowercaseRecognizedText.Contains("weather"))
                {
                    try
                    {
                        weather.GetWeatherData().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("An error occurred: " + ex.Message);
                    }
                }


                // Prayer times
                else if (lowercaseRecognizedText.Contains("pray times") || lowercaseRecognizedText.Contains("prayer times"))
                {
                    double latitude = location.GetLatitude().Result;
                    double longitude = location.GetLongitude().Result;

                    GetPrayerTimes prayerTimesLogic = new GetPrayerTimes(latitude, longitude);

                    prayerTimesLogic.AnnouncePrayerTimes(DateTime.Now).GetAwaiter().GetResult();
                }


                // Send Text Messages
                else if (contacts.Keys.Any(name => lowercaseRecognizedText.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) || lowercaseRecognizedText.Contains("leith"))
                //     ^^^           This looks to see if the recognized text contains a contact name                     ^^^
                {
                    string contactName = null;
                    string contactNumber = null;

                    foreach (var name in contacts.Keys)
                    {
                        if (lowercaseRecognizedText.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            contactName = name;
                            contactNumber = contacts[name];
                            break;
                        }
                    }

                    if (contactName != null && contactNumber != null)
                    {
                        smsControl.SendSMS(contactName, contactNumber);
                    }
                }


                // Door Control
                else if (lowercaseRecognizedText.Contains("door")) {

                    if (lowercaseRecognizedText.Contains("open"))
                    {
                        arduino.ArduinoCommunication("OPEN");

                        speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Opening your door now.");
                        speechManager.SpeechBubble(recognizedText, "Okay! Opening your door now.");
                    }
                    else if (lowercaseRecognizedText.Contains("close"))
                    {
                        arduino.ArduinoCommunication("CLOSE");

                        speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Closing your door now.");
                        speechManager.SpeechBubble(recognizedText, "Okay! Closing your door now.");
                    }
                }


                // Shut down or restart
                else if (lowercaseRecognizedText == "shut down." || lowercaseRecognizedText == "restart.")
                {
                    speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Are you sure?");
                    speechManager.SpeechBubble(recognizedText, "Are you sure?");

                    SpeechRecognizer confirmationSpeechRecognizer = new SpeechRecognizer(speechManager.speechConfig);
                    SpeechRecognitionResult confirmationResult = confirmationSpeechRecognizer.RecognizeOnceAsync().GetAwaiter().GetResult();
                    speechManager.ConvertSpeechToText(confirmationResult);

                    string action = recognizedText.Contains("shut down") ? "Shutting down" : "Restarting now";

                    if (confirmationResult.Text.ToLower() == "yes.")
                    {

                        speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", $"Ok. {action}");
                        speechManager.SpeechBubble(recognizedText, $"Ok. {action}");

                        Process.Start("shutdown", recognizedText.Contains("shut down") ? "/s /t 0" : "/r /t 0");
                    }
                    else
                    {
                        speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", $"Ok. NOT {action}");
                        speechManager.SpeechBubble(recognizedText, $"Ok. NOT {action}"); ;
                        Thread.Sleep(500);
                    }
                }

                // If the recognized text is not a command, we can use the Gemini API to generate a response
                // Gemini API
                else
                {
                    if (speechRecognitionResult.Reason != ResultReason.NoMatch)
                    {
                        string geminiResponse = GeminiService.GenerateGeminiResponse(recognizedText).GetAwaiter().GetResult();

                        speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", geminiResponse);
                        speechManager.SpeechBubble(recognizedText, geminiResponse);
                    }
                }
            }
        }
    }
}