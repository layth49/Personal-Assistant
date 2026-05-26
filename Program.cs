using Microsoft.CognitiveServices.Speech;
using Personal_Assistant.Arduino;
using Personal_Assistant.LLMClient;
using Personal_Assistant.Geolocator;
using Personal_Assistant.LightAutomator;
using Personal_Assistant.PlaystationController;
using Personal_Assistant.PrayerTimesCalculator;
using Personal_Assistant.SMSController;
using Personal_Assistant.SpeechManager;
using Personal_Assistant.WeatherService;
using Python.Runtime;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Personal_Assistant
{
    class Program
    {
        public static readonly string weatherAPIKey = Environment.GetEnvironmentVariable("WEATHERAPI_KEY");

        public static string ipAddressPlug = Environment.GetEnvironmentVariable("IP_ADDRESS:PLUG");
        public static string ipAddressSwitch = Environment.GetEnvironmentVariable("IP_ADDRESS:SWITCH");

        public static string recognizedText = string.Empty;

        private static readonly string[] morningGreetings =
        {
            "Good morning! What can I do for you?",
            "Morning! How can I assist you today?",
            "Rise and shine! What's on your agenda?",
            "Good morning! How can I help?",
            "Morning! What's up?"
        };

        private static readonly string[] afternoonGreetings =
        {
            "Good afternoon! What can I do for you?",
            "Afternoon! How can I help?",
            "Hi! What's up?",
            "Good afternoon! I'm here to assist you.",
            "Hope you're having a great afternoon! How can I help?",
            "Hello there! What can I do for you this afternoon?"
        };

        private static readonly string[] eveningGreetings =
        {
            "Good evening! What can I do for you?",
            "Evening! How can I help?",
            "Hi! What's up?",
            "Good evening! I'm here to assist you.",
            "Hope your evening is going well! What can I do for you?",
            "Hello! How can I help you this evening?"
        };

        private static readonly string[] nightGreetings =
        {
            "Good night! How can I assist you?",
            "Hi there! How can I help you tonight?",
            "Hello! What do you need this late?",
            "Good night! I'm here if you need anything.",
            "Hope you're having a peaceful night. How can I assist?"
        };

        private static readonly Random random = new Random();

        static void CheckEnvironmentVariables()
        {
            if (string.IsNullOrEmpty(SpeechService.speechKey) ||
                string.IsNullOrEmpty(SpeechService.speechRegion) ||
                string.IsNullOrEmpty(weatherAPIKey))
            {
                Console.WriteLine("Error: Please set the following environment variables before running the program:");
                Console.WriteLine("  - SPEECH_KEY: Your Cognitive Services Speech API subscription key");
                Console.WriteLine("  - SPEECH_REGION: Your Cognitive Services Speech API service region (e.g., westus)");
                Console.WriteLine("  - WEATHERAPI_KEY: Your OpenWeatherMap API Key");
                Console.WriteLine("You can set them using the following commands (replace 'your_key' with your actual keys):");
                Console.WriteLine("  - setx SPEECH_KEY your_speech_key");
                Console.WriteLine("  - setx SPEECH_REGION your_speech_region");
                Console.WriteLine("  - setx WEATHERAPI_KEY your_weatherapi_key");
                Console.ReadLine();
                Environment.Exit(1);
            }
        }

        private static string PickGreeting(int hour)
        {
            string[] pool;
            if (hour < 12) pool = morningGreetings;
            else if (hour < 18) pool = afternoonGreetings;
            else if (hour < 21) pool = eveningGreetings;
            else pool = nightGreetings;
            return pool[random.Next(pool.Length)];
        }

        public static async Task Main()
        {
            CheckEnvironmentVariables();

            // 49 (ASCII art)
            Console.WriteLine("                                    \r\n     ,AM  .d*\"*bg.\r\n    AVMM 6MP    Mb\r\n  ,W' MM YMb    MM\r\n,W'   MM  `MbmmdM9\r\nAmmmmmMMmm     .M'\r\n      MM     .d9  \r\n      MM   m\"'    \n\n");

            Runtime.PythonDLL = @"..\..\..\..\AppData\Local\Programs\Python\Python312\python312.dll";
            PythonEngine.Initialize();
            PythonEngine.BeginAllowThreads();

            using (Py.GIL())
            {
                dynamic sys = Py.Import("sys");
                sys.path.append(@"..\..\");
            }

            // Single-instance services. Speech recognizer and synthesizer reuse
            // websocket connections, so creating them once cuts handshake latency.
            var speechManager = new SpeechService();
            await speechManager.WarmUpAudioAsync(); // wakes the audio device so first greeting isn't clipped
            var speechRecognizer = new SpeechRecognizer(speechManager.speechConfig);
            var phraseList = PhraseListGrammar.FromRecognizer(speechRecognizer);

            var contacts = LoadContacts();
            if (contacts != null)
            {
                foreach (var phrase in contacts.Keys)
                {
                    phraseList.AddPhrase(phrase);
                }
            }

            var location = new GetLocation();
            var weather = new GetWeather(weatherAPIKey);
            var lightControl = new LightControl();
            var playstationControl = new PlaystationControl();
            var smsControl = new SMSControl();
            var arduino = new ArduinoService();

            while (true)
            {
                int hour = DateTime.Now.Hour;

                await speechManager.KeywordRecognizer();
                Console.WriteLine($"[loop] KeywordRecognizer awaited returned at {DateTime.Now:HH:mm:ss.fff}");

                string greeting = PickGreeting(hour);
                Console.WriteLine($"[loop] about to call Say at {DateTime.Now:HH:mm:ss.fff}");
                await speechManager.Say("Hey 49", greeting);

                var speechRecognitionResult = await speechRecognizer.RecognizeOnceAsync();
                speechManager.ConvertSpeechToText(speechRecognitionResult);

                recognizedText = speechRecognitionResult.Text ?? string.Empty;
                string lower = recognizedText.ToLower();

                if (lower == "who are you?")
                {
                    await speechManager.Say(recognizedText, "Hi! I'm L.A.I.T.H.49, your own personal assistant!");
                }
                else if (lower.Contains("exit"))
                {
                    await speechManager.Say(recognizedText, "Alright goodbye!");
                    PythonEngine.Shutdown();
                    Environment.Exit(0);
                }
                else if (lower.Contains("never mind") || lower.Contains("nevermind"))
                {
                    await speechManager.Say(recognizedText, "Okay! Let me know if you need anything else.");
                }
                else if (lower == "what time is it?" || lower == "what's the time?")
                {
                    await speechManager.Say(recognizedText, $"It's {DateTime.Now.ToLocalTime():t}");
                }
                else if (lower == "what day is it?")
                {
                    await speechManager.Say(recognizedText, $"It's {DateTime.Now.Date:D}");
                }
                else if (lower.StartsWith("search up") || lower.StartsWith("google"))
                {
                    string prefix = lower.StartsWith("search up") ? "search up" : "google";
                    string query = recognizedText.Substring(prefix.Length).Trim().TrimEnd('.', '?');

                    await speechManager.Say(recognizedText, $"Okay! Searching up {query} now");
                    Process.Start("https://www.google.com/search?q=" + Uri.EscapeDataString(query));
                }
                else if (lower.Contains("youtube"))
                {
                    await HandleYouTubeAsync(speechManager);
                }
                else if (lower.Contains("visual studio") || lower.Contains("code") || lower.Contains("coding"))
                {
                    await speechManager.Say(recognizedText, "Okay! Opening Visual Studio now.");
                    Process.Start("devenv");
                }
                else if (lower.Contains("turn on") && (lower.Contains("playstation") || lower.Contains("ps-5")))
                {
                    await playstationControl.TurnOnPlaystation();
                }
                else if (lower.Contains("turn on") && lower.Contains("light"))
                {
                    if (lower.Contains("led")) await lightControl.TurnOnLights("LED", ipAddressPlug);
                    else if (lower.Contains("bedroom")) await lightControl.TurnOnLights("bedroom", ipAddressSwitch);
                }
                else if (lower.Contains("turn off") && lower.Contains("light"))
                {
                    if (lower.Contains("led")) await lightControl.TurnOffLights("LED", ipAddressPlug);
                    else if (lower.Contains("bedroom")) await lightControl.TurnOffLights("bedroom", ipAddressSwitch);
                }
                else if (lower.Contains("weather"))
                {
                    try { await weather.GetWeatherData(); }
                    catch (Exception ex) { Console.WriteLine("An error occurred: " + ex.Message); }
                }
                else if (lower.Contains("pray times") || lower.Contains("prayer times"))
                {
                    try
                    {
                        double latitude = await location.GetLatitude();
                        double longitude = await location.GetLongitude();
                        var prayerTimesLogic = new GetPrayerTimes(latitude, longitude);
                        await prayerTimesLogic.AnnouncePrayerTimes(DateTime.Now);
                    }
                    catch (InvalidOperationException ex)
                    {
                        Console.WriteLine($"Location lookup failed: {ex.Message}");
                        await speechManager.Say(recognizedText,
                            "Sorry, I couldn't get your location. Make sure Windows location services are enabled.");
                    }
                }
                else if (contacts != null && TryMatchContact(contacts, lower, out string contactName, out string contactNumber))
                {
                    await smsControl.SendSMS(contactName, contactNumber);
                }
                else if (lower.Contains("door"))
                {
                    if (lower.Contains("open"))
                    {
                        await arduino.ArduinoCommunication("OPEN");
                        await speechManager.Say(recognizedText, "Okay! Opening your door now.");
                    }
                    else if (lower.Contains("close"))
                    {
                        await arduino.ArduinoCommunication("CLOSE");
                        await speechManager.Say(recognizedText, "Okay! Closing your door now.");
                    }
                }
                else if (lower == "shut down." || lower == "restart.")
                {
                    await HandleShutdownAsync(speechManager, speechRecognizer, lower);
                }
                else if (speechRecognitionResult.Reason != ResultReason.NoMatch)
                {
                    string llmResponse = await LocalLLMService.GenerateResponse(recognizedText);
                    await speechManager.Say(recognizedText, llmResponse);
                }
            }
        }

        private static Dictionary<string, string> LoadContacts()
        {
            var contactsPath = Environment.GetEnvironmentVariable("CONTACTS_PATH");
            if (string.IsNullOrEmpty(contactsPath) || !File.Exists(contactsPath))
            {
                return null;
            }
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(contactsPath));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load contacts from {contactsPath}: {ex.Message}");
                return null;
            }
        }

        private static bool TryMatchContact(
            Dictionary<string, string> contacts,
            string lower,
            out string contactName,
            out string contactNumber)
        {
            foreach (var kv in contacts)
            {
                if (lower.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    contactName = kv.Key;
                    contactNumber = kv.Value;
                    return true;
                }
            }
            contactName = null;
            contactNumber = null;
            return false;
        }

        private static async Task HandleYouTubeAsync(SpeechService speechManager)
        {
            await speechManager.Say(recognizedText, "Okay! Would you like a specific video or to just open it?");

            string confirmation;
            using (var recognizer = new SpeechRecognizer(speechManager.speechConfig))
            {
                var result = await recognizer.RecognizeOnceAsync();
                speechManager.ConvertSpeechToText(result);
                confirmation = (result.Text ?? string.Empty).ToLower();
            }

            if (confirmation.StartsWith("search for") || confirmation.StartsWith("search up"))
            {
                string prefix = confirmation.StartsWith("search up") ? "search up " : "search for ";
                string query = confirmation.Substring(prefix.Length).TrimEnd('.');
                await speechManager.Say(recognizedText, $"Ok! Searching for {query} now");
                Process.Start($"https://www.youtube.com/results?search_query={Uri.EscapeDataString(query)}");
            }
            else if (confirmation.Contains("open"))
            {
                await speechManager.Say(recognizedText, "Okay! Opening Youtube now.");
                Process.Start("https://www.youtube.com");
            }
            else if (confirmation.Contains("nevermind") || confirmation.Contains("never mind"))
            {
                await speechManager.Say(recognizedText, "Okay! Let me know if you need anything else.");
            }
        }

        private static async Task HandleShutdownAsync(
            SpeechService speechManager,
            SpeechRecognizer speechRecognizer,
            string lower)
        {
            await speechManager.Say(recognizedText, "Are you sure?");

            SpeechRecognitionResult confirmationResult;
            using (var confirmRecognizer = new SpeechRecognizer(speechManager.speechConfig))
            {
                confirmationResult = await confirmRecognizer.RecognizeOnceAsync();
                speechManager.ConvertSpeechToText(confirmationResult);
            }

            bool isShutdown = lower.Contains("shut down");
            string action = isShutdown ? "Shutting down" : "Restarting now";

            if (string.Equals(confirmationResult.Text?.TrimEnd('.'), "yes", StringComparison.OrdinalIgnoreCase))
            {
                await speechManager.Say(confirmationResult.Text, $"Ok. {action}");
                Process.Start("shutdown", isShutdown ? "/s /t 0" : "/r /t 0");
            }
            else
            {
                await speechManager.Say(recognizedText, $"Ok. NOT {action}");
                await Task.Delay(500);
            }
        }
    }
}