using Microsoft.CognitiveServices.Speech;
using Personal_Assistant.Arduino;
using Personal_Assistant.Dispatch;
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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
            if (string.IsNullOrEmpty(GeminiService.geminiApiKey) ||
                string.IsNullOrEmpty(SpeechService.speechKey) ||
                string.IsNullOrEmpty(SpeechService.speechRegion) ||
                string.IsNullOrEmpty(weatherAPIKey))
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

            Runtime.PythonDLL = @"C:\Users\layth\AppData\Local\Programs\Python\Python312\python312.dll";
            PythonEngine.Initialize();
            PythonEngine.BeginAllowThreads();

            using (Py.GIL())
            {
                dynamic sys = Py.Import("sys");
                sys.path.append(@"C:\Users\layth\LAITH\main");
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

            // Shared dependencies handed to every command handler.
            var context = new CommandContext
            {
                Speech = speechManager,
                Lights = lightControl,
                Playstation = playstationControl,
                Sms = smsControl,
                Arduino = arduino,
                Weather = weather,
                Location = location,
                Contacts = contacts,
                IpAddressPlug = ipAddressPlug,
                IpAddressSwitch = ipAddressSwitch
            };

            // LLM-first dispatch: every utterance goes to Gemini, which picks a tool
            // (and extracts its arguments) or answers conversationally. The keyword
            // matcher in the registry is only used as a fallback if Gemini is
            // unavailable / malformed / times out.
            var registry = BuildRegistry(context);
            var dispatcher = new IntentDispatcher(
                registry,
                context,
                GeminiService.DetectToolAsync,
                GeminiService.GenerateGeminiResponse);

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

                // NoMatch is already handled (spoken) by ConvertSpeechToText; only
                // dispatch real recognised speech.
                if (speechRecognitionResult.Reason != ResultReason.NoMatch)
                {
                    await dispatcher.DispatchAsync(recognizedText);
                }
            }
        }

        // Builds the command catalogue. Each VoiceCommand carries its LLM tool
        // schema (for Gemini dispatch) plus a keyword predicate + arg extractor
        // (for the fallback path). Registration order == the original if/else
        // order, so "first keyword match wins" is preserved on fallback.
        private static ToolRegistry BuildRegistry(CommandContext context)
        {
            var registry = new ToolRegistry();

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("who_are_you",
                    "Introduce the assistant when the user asks who or what it is."),
                lower => lower == "who are you?",
                (ctx, args) => ctx.Speech.Say(ctx.RecognizedText,
                    "Hi! I'm L.A.I.T.H.49, your own personal assistant!")));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("exit_assistant",
                    "Quit and shut down the assistant program entirely."),
                lower => lower.Contains("exit"),
                async (ctx, args) =>
                {
                    await ctx.Speech.Say(ctx.RecognizedText, "Alright goodbye!");
                    PythonEngine.Shutdown();
                    Environment.Exit(0);
                }));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("never_mind",
                    "Cancel or dismiss the current request without doing anything."),
                lower => lower.Contains("never mind") || lower.Contains("nevermind"),
                (ctx, args) => ctx.Speech.Say(ctx.RecognizedText,
                    "Okay! Let me know if you need anything else.")));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("get_time",
                    "Tell the user the current time of day."),
                lower => lower == "what time is it?" || lower == "what's the time?",
                (ctx, args) => ctx.Speech.Say(ctx.RecognizedText,
                    $"It's {DateTime.Now.ToLocalTime():t}")));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("get_date",
                    "Tell the user today's date / what day it is."),
                lower => lower == "what day is it?",
                (ctx, args) => ctx.Speech.Say(ctx.RecognizedText,
                    $"It's {DateTime.Now.Date:D}")));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("google_search",
                    "Open a Google web search for what the user wants to look up.",
                    new ToolParameter("query", "string",
                        "The search terms to look up on Google.")),
                lower => lower.StartsWith("search up") || lower.StartsWith("google"),
                async (ctx, args) =>
                {
                    string query = args["query"];
                    await ctx.Speech.Say(ctx.RecognizedText, $"Okay! Searching up {query} now");
                    Process.Start("https://www.google.com/search?q=" + Uri.EscapeDataString(query));
                },
                text =>
                {
                    string lower = text.ToLower();
                    string prefix = lower.StartsWith("search up") ? "search up" : "google";
                    string query = text.Substring(prefix.Length).Trim().TrimEnd('.', '?');
                    return new Dictionary<string, string> { ["query"] = query };
                }));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("open_youtube",
                    "Open YouTube, optionally searching for a specific video."),
                lower => lower.Contains("youtube"),
                (ctx, args) => HandleYouTubeAsync(ctx.Speech)));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("open_visual_studio",
                    "Open Visual Studio for coding."),
                lower => lower.Contains("visual studio") || lower.Contains("code") || lower.Contains("coding"),
                async (ctx, args) =>
                {
                    await ctx.Speech.Say(ctx.RecognizedText, "Okay! Opening Visual Studio now.");
                    Process.Start("devenv");
                }));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("turn_on_playstation",
                    "Turn on the PlayStation 5 via Remote Play and launch a game."),
                lower => lower.Contains("turn on") && (lower.Contains("playstation") || lower.Contains("ps-5")),
                (ctx, args) => ctx.Playstation.TurnOnPlaystation()));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("control_lights",
                    "Turn a smart light on or off.",
                    new ToolParameter("state", "string",
                        "Whether to turn the light on or off.",
                        AllowedValues: new[] { "on", "off" }),
                    new ToolParameter("room", "string",
                        "Which light to control.",
                        AllowedValues: new[] { "LED", "bedroom" })),
                lower => (lower.Contains("turn on") || lower.Contains("turn off")) && lower.Contains("light"),
                async (ctx, args) =>
                {
                    string state = args["state"];
                    string room = args["room"];
                    string ip = room == "LED" ? ctx.IpAddressPlug : ctx.IpAddressSwitch;
                    if (state == "on") await ctx.Lights.TurnOnLights(room, ip);
                    else await ctx.Lights.TurnOffLights(room, ip);
                },
                text =>
                {
                    string lower = text.ToLower();
                    var d = new Dictionary<string, string>
                    {
                        ["state"] = lower.Contains("turn off") ? "off" : "on"
                    };
                    if (lower.Contains("led")) d["room"] = "LED";
                    else if (lower.Contains("bedroom")) d["room"] = "bedroom";
                    return d;
                }));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("get_weather",
                    "Report the current weather."),
                lower => lower.Contains("weather"),
                async (ctx, args) =>
                {
                    try { await ctx.Weather.GetWeatherData(); }
                    catch (Exception ex) { Console.WriteLine("An error occurred: " + ex.Message); }
                }));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("get_prayer_times",
                    "Announce today's Islamic prayer times for the user's location."),
                lower => lower.Contains("pray times") || lower.Contains("prayer times"),
                async (ctx, args) =>
                {
                    try
                    {
                        double latitude = await ctx.Location.GetLatitude();
                        double longitude = await ctx.Location.GetLongitude();
                        var prayerTimesLogic = new GetPrayerTimes(latitude, longitude);
                        await prayerTimesLogic.AnnouncePrayerTimes(DateTime.Now);
                    }
                    catch (InvalidOperationException ex)
                    {
                        Console.WriteLine($"Location lookup failed: {ex.Message}");
                        await ctx.Speech.Say(ctx.RecognizedText,
                            "Sorry, I couldn't get your location. Make sure Windows location services are enabled.");
                    }
                }));

            // Only expose SMS if there are contacts to send to. The allowed contact
            // names double as the tool's enum and the dispatcher's validation set.
            if (context.Contacts != null && context.Contacts.Count > 0)
            {
                var contacts = context.Contacts;
                registry.Add(new VoiceCommand(
                    ToolDefinition.Create("send_sms",
                        "Send a text message to one of the user's known contacts.",
                        new ToolParameter("contact", "string",
                            "Which contact to message.",
                            AllowedValues: new List<string>(contacts.Keys))),
                    lower => TryMatchContact(contacts, lower, out _, out _),
                    (ctx, args) =>
                    {
                        string name = args["contact"];
                        string number = ctx.Contacts[name];
                        return ctx.Sms.SendSMS(name, number);
                    },
                    text =>
                    {
                        if (TryMatchContact(contacts, text.ToLower(), out string name, out _))
                            return new Dictionary<string, string> { ["contact"] = name };
                        return VoiceCommand.EmptyArgs;
                    }));
            }

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("control_door",
                    "Open or close the door via the Arduino controller.",
                    new ToolParameter("state", "string",
                        "Whether to open or close the door.",
                        AllowedValues: new[] { "open", "close" })),
                lower => lower.Contains("door") && (lower.Contains("open") || lower.Contains("close")),
                async (ctx, args) =>
                {
                    if (args["state"] == "open")
                    {
                        await ctx.Arduino.ArduinoCommunication("OPEN");
                        await ctx.Speech.Say(ctx.RecognizedText, "Okay! Opening your door now.");
                    }
                    else
                    {
                        await ctx.Arduino.ArduinoCommunication("CLOSE");
                        await ctx.Speech.Say(ctx.RecognizedText, "Okay! Closing your door now.");
                    }
                },
                text =>
                {
                    string lower = text.ToLower();
                    return new Dictionary<string, string>
                    {
                        ["state"] = lower.Contains("open") ? "open" : "close"
                    };
                }));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("power_control",
                    "Shut down or restart the computer (asks for confirmation first).",
                    new ToolParameter("action", "string",
                        "Whether to shut down or restart the machine.",
                        AllowedValues: new[] { "shutdown", "restart" })),
                lower => lower == "shut down." || lower == "restart.",
                (ctx, args) => HandleShutdownAsync(ctx.Speech, args["action"]),
                text =>
                {
                    string lower = text.ToLower();
                    return new Dictionary<string, string>
                    {
                        ["action"] = lower.Contains("shut down") ? "shutdown" : "restart"
                    };
                }));

            return registry;
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

        // contacts is IReadOnlyDictionary here (from CommandContext); same logic.
        private static bool TryMatchContact(
            IReadOnlyDictionary<string, string> contacts,
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

        // action is "shutdown" or "restart" (validated by the dispatcher).
        private static async Task HandleShutdownAsync(SpeechService speechManager, string action)
        {
            await speechManager.Say(recognizedText, "Are you sure?");

            SpeechRecognitionResult confirmationResult;
            using (var confirmRecognizer = new SpeechRecognizer(speechManager.speechConfig))
            {
                confirmationResult = await confirmRecognizer.RecognizeOnceAsync();
                speechManager.ConvertSpeechToText(confirmationResult);
            }

            bool isShutdown = action == "shutdown";
            string actionText = isShutdown ? "Shutting down" : "Restarting now";

            if (string.Equals(confirmationResult.Text?.TrimEnd('.'), "yes", StringComparison.OrdinalIgnoreCase))
            {
                await speechManager.Say(confirmationResult.Text, $"Ok. {actionText}");
                Process.Start("shutdown", isShutdown ? "/s /t 0" : "/r /t 0");
            }
            else
            {
                await speechManager.Say(recognizedText, $"Ok. NOT {actionText}");
                await Task.Delay(500);
            }
        }
    }
}
