using Microsoft.CognitiveServices.Speech;
using Personal_Assistant.AppLaunching;
using Personal_Assistant.Arduino;
using Personal_Assistant.AudioControl;
using Personal_Assistant.Diagnostics;
using Personal_Assistant.Dispatch;
using Personal_Assistant.GeminiClient;
using Personal_Assistant.Geolocator;
using Personal_Assistant.LightAutomator;
using Personal_Assistant.MediaControl;
using Personal_Assistant.PlaystationController;
using Personal_Assistant.PrayerTimesCalculator;
using Personal_Assistant.ProcessControl;
using Personal_Assistant.Reminders;
using Personal_Assistant.ScreenCapture;
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
    public class Program
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
                sys.path.append(@"C:\Users\layth\LAITH\local");
            }

            // Tracks per-turn stt/llm/tts latency so we can see what's actually
            // the bottleneck. Reset before each recognition attempt, printed
            // after the turn's dispatch completes.
            var latency = new LatencyTracker();

            // Single-instance services. Speech recognizer and synthesizer reuse
            // websocket connections, so creating them once cuts handshake latency.
            var speechManager = new SpeechService(latency);
            await speechManager.WarmUpAudioAsync(); // wakes the audio device so first greeting isn't clipped
            var speechRecognizer = new SpeechRecognizer(speechManager.speechConfig);
            var phraseList = PhraseListGrammar.FromRecognizer(speechRecognizer);

            // Azure's own VAD tells us when it thinks the user stopped talking
            // (SpeechEndDetected). The time from there until RecognizeOnceAsync's
            // task actually completes is the SDK's finalization/network latency —
            // "how long it takes to understand", excluding however long the user
            // spent speaking. Reset to null before each attempt; if the event
            // never fires (e.g. NoMatch with no speech at all), STT records zero.
            DateTime? speechEndDetectedAt = null;
            speechRecognizer.SpeechEndDetected += (s, e) => { speechEndDetectedAt = DateTime.UtcNow; };

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
            var audio = new AudioController();
            var screenshot = new ScreenshotService();
            var processes = new ProcessController();
            var apps = new AppLauncher();
            var media = new MediaController();
            var nowPlaying = new NowPlayingReader();
            // Fires timers/alarms/reminders by speaking them. Say is serialised
            // internally, so a reminder firing mid-conversation won't garble
            // whatever the assistant is already saying. The widget host mirrors
            // each one as an on-screen floating countdown.
            var timerWidgets = new TimerWidgetHost();
            // A fired reminder has no user utterance, so use a clock as the
            // bubble's "you said" label — a nice reminder indicator now that the
            // bubble renders emoji. It's only shown, never spoken.
            var reminders = new ReminderService(
                message => speechManager.Say("⏰", message),
                timerWidgets);

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
                Audio = audio,
                Screenshot = screenshot,
                Processes = processes,
                Apps = apps,
                Media = media,
                NowPlaying = nowPlaying,
                Reminders = reminders,
                Contacts = contacts,
                IpAddressPlug = ipAddressPlug,
                IpAddressSwitch = ipAddressSwitch
            };

            // LLM-first dispatch: every utterance goes to Gemini, which picks a tool
            // (and extracts its arguments) or answers conversationally. The keyword
            // matcher in the registry is only used as a fallback if Gemini is
            // unavailable / malformed / times out.
            var registry = BuildRegistry(context);
            var conversationMemory = new ConversationMemory();
            var dispatcher = new IntentDispatcher(
                registry,
                context,
                GeminiService.DetectToolAsync,
                GeminiService.GenerateGeminiResponse,
                conversationMemory,
                latency);

            // Let the `repeat` tool run other tools by name (validated). Speaking
            // is explicit rather than defaulted: the repeated actions are the only
            // thing the user hears on this path, and a method group would silently
            // bind whatever the default happened to be.
            context.RunTool = (name, args) => dispatcher.RunToolByNameAsync(name, args, speak: true);

            // When the user barges in over a spoken reply with the wakeword, we
            // skip the wakeword wait + greeting on the next turn and listen for
            // their new command straight away.
            bool listenImmediately = false;

            while (true)
            {
                int hour = DateTime.Now.Hour;

                if (!listenImmediately)
                {
                    bool woke = await speechManager.KeywordRecognizer();
                    Console.WriteLine($"[loop] KeywordRecognizer returned {woke} at {DateTime.Now:HH:mm:ss.fff}");
                    // Only greet + listen when the wakeword actually fired. On an
                    // errored/early return, loop back and keep waiting instead of
                    // spuriously greeting (which previously ran away in a loop).
                    if (!woke) continue;

                    string greeting = PickGreeting(hour);
                    Console.WriteLine($"[loop] about to call Say at {DateTime.Now:HH:mm:ss.fff}");
                    // Greeting is NOT interruptible: barge-in matters for long
                    // conversational replies, not a two-second greeting.
                    await speechManager.Say("Hey 49", greeting);
                }
                listenImmediately = false;

                // Fresh latency counters for this turn.
                latency.Reset();
                speechEndDetectedAt = null;

                var speechRecognitionResult = await speechRecognizer.RecognizeOnceAsync();
                DateTime recognizedAt = DateTime.UtcNow;
                speechManager.ConvertSpeechToText(speechRecognitionResult);

                // "Understanding" time = however long it took AFTER Azure's VAD
                // decided the user stopped talking. If the event never fired
                // (no speech at all), there's nothing to attribute to STT.
                if (speechEndDetectedAt.HasValue)
                {
                    latency.RecordStt(recognizedAt - speechEndDetectedAt.Value);
                }

                recognizedText = speechRecognitionResult.Text ?? string.Empty;

                // NoMatch is already handled (spoken) by ConvertSpeechToText; only
                // dispatch real recognised speech.
                if (speechRecognitionResult.Reason != ResultReason.NoMatch)
                {
                    bool interrupted = await dispatcher.DispatchAsync(recognizedText);
                    if (interrupted) listenImmediately = true;
                    Console.WriteLine(latency.Summary());
                }
            }
        }

        // Builds the command catalogue. Each VoiceCommand carries its LLM tool
        // schema (for Gemini dispatch) plus a keyword predicate + arg extractor
        // (for the fallback path). Registration order == the original if/else
        // order, so "first keyword match wins" is preserved on fallback.
        // Public so the Live smoke harness can drive the REAL tool catalogue.
        // It built its own stub registry once, which meant a test that "passed"
        // was only ever exercising its own fake handlers — it reported a working
        // tool round trip while the actual one was still handing the model
        // {"result":"done"} and letting it invent the answer.
        public static ToolRegistry BuildRegistry(CommandContext context)
        {
            var registry = new ToolRegistry();

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("who_are_you",
                    "Introduce the assistant when the user asks who or what it is."),
                lower => lower == "who are you?",
                (ctx, args) => Task.FromResult(ToolResult.Speak(
                    "Hi! I'm L.A.I.T.H.49, your own personal assistant!"))));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("exit_assistant",
                    "Quit and shut down the assistant program entirely."),
                lower => lower.Contains("exit"),
                async (ctx, args) =>
                {
                    // The one handler that still speaks for itself, because it
                    // never returns: Environment.Exit runs before any caller could
                    // voice a result. Awaiting the goodbye is what keeps it from
                    // being cut off mid-word by the process ending.
                    await ctx.Speech.Say(ctx.RecognizedText, "Alright goodbye!");
                    PythonEngine.Shutdown();
                    Environment.Exit(0);
                }));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("never_mind",
                    "Cancel or dismiss the current request without doing anything."),
                lower => lower.Contains("never mind") || lower.Contains("nevermind"),
                (ctx, args) => Task.FromResult(ToolResult.Speak(
                    "Okay! Let me know if you need anything else."))));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("get_time",
                    "Tell the user the current time of day."),
                lower => lower == "what time is it?" || lower == "what's the time?",
                (ctx, args) =>
                {
                    DateTime now = DateTime.Now;
                    // The 24-hour value and the zone go along for the ride so the
                    // model states the user's ACTUAL local time. Given only a
                    // sentence it will happily re-derive one and get it wrong —
                    // this tool is why: it announced "7:20 AM UTC" for 2:20 AM.
                    return Task.FromResult(ToolResult.Speak($"It's {now:t}")
                        .With("time_local", now.ToString("HH:mm"))
                        .With("time_zone", TimeZoneInfo.Local.StandardName));
                }));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("get_date",
                    "Tell the user today's date / what day it is."),
                lower => lower == "what day is it?",
                (ctx, args) =>
                {
                    DateTime today = DateTime.Now.Date;
                    return Task.FromResult(ToolResult.Speak($"It's {today:D}")
                        .With("date_local", today.ToString("yyyy-MM-dd"))
                        .With("weekday", today.DayOfWeek.ToString()));
                }));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("google_search",
                    "Open a Google web search for what the user wants to look up.",
                    new ToolParameter("query", "string",
                        "The search terms to look up on Google.")),
                lower => lower.StartsWith("search up") || lower.StartsWith("google"),
                (ctx, args) =>
                {
                    string query = args["query"];
                    Process.Start("https://www.google.com/search?q=" + Uri.EscapeDataString(query));
                    return Task.FromResult(ToolResult.Speak($"Okay! Searching up {query} now")
                        .With("query", query));
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
                (ctx, args) =>
                {
                    Process.Start("devenv");
                    return Task.FromResult(ToolResult.Speak("Okay! Opening Visual Studio now."));
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
                (ctx, args) =>
                {
                    string state = args["state"];
                    string room = args["room"];
                    string ip = room == "LED" ? ctx.IpAddressPlug : ctx.IpAddressSwitch;
                    return Task.FromResult(state == "on"
                        ? ctx.Lights.TurnOnLights(room, ip)
                        : ctx.Lights.TurnOffLights(room, ip));
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
                    "Report the CURRENT weather conditions right now."),
                lower => lower.Contains("weather") && !lower.Contains("forecast") &&
                         !lower.Contains("tomorrow") && !lower.Contains("this week") && !lower.Contains("next few days"),
                async (ctx, args) =>
                {
                    try { return await ctx.Weather.GetWeatherData(); }
                    catch (Exception ex)
                    {
                        Console.WriteLine("An error occurred: " + ex.Message);
                        return ToolResult.Failed(
                            "Sorry, I couldn't get the weather right now.", ex.Message);
                    }
                }));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("get_forecast",
                    "Report the multi-day weather forecast (the days ahead, e.g. tomorrow " +
                    "or the next few days) — not the current conditions.",
                    new ToolParameter("days", "integer",
                        "How many days ahead to forecast, from 1 to 5. Defaults to 3.",
                        Required: false)),
                lower => lower.Contains("forecast") ||
                         (lower.Contains("weather") &&
                          (lower.Contains("tomorrow") || lower.Contains("this week") ||
                           lower.Contains("next few days") || lower.Contains("coming days"))),
                async (ctx, args) =>
                {
                    int days = 3;
                    if (args.TryGetValue("days", out string d) && int.TryParse(d, out int parsed))
                        days = Math.Max(1, Math.Min(5, parsed));
                    try { return await ctx.Weather.GetForecastData(days); }
                    catch (Exception ex)
                    {
                        Console.WriteLine("An error occurred: " + ex.Message);
                        return ToolResult.Failed(
                            "Sorry, I ran into a problem getting the forecast.", ex.Message);
                    }
                },
                text =>
                {
                    var m = System.Text.RegularExpressions.Regex.Match(text, @"\d+");
                    return m.Success
                        ? new Dictionary<string, string> { ["days"] = m.Value }
                        : VoiceCommand.EmptyArgs;
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
                        return prayerTimesLogic.DescribePrayerTimes(DateTime.Now);
                    }
                    catch (InvalidOperationException ex)
                    {
                        Console.WriteLine($"Location lookup failed: {ex.Message}");
                        return ToolResult.Failed(
                            "Sorry, I couldn't get your location. Make sure Windows location services are enabled.",
                            ex.Message);
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
                    bool opening = args["state"] == "open";
                    await ctx.Arduino.ArduinoCommunication(opening ? "OPEN" : "CLOSE");
                    return ToolResult
                        .Speak(opening ? "Okay! Opening your door now." : "Okay! Closing your door now.")
                        .With("door_state", opening ? "open" : "closed");
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

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("control_volume",
                    "Adjust the computer's audio volume: turn it up or down, mute, unmute, " +
                    "or set it to a specific percentage.",
                    new ToolParameter("action", "string",
                        "What to do to the volume.",
                        AllowedValues: new[] { "up", "down", "mute", "unmute", "set" }),
                    new ToolParameter("level", "integer",
                        "Target volume as a percentage from 0 to 100. Only used when action is 'set'.",
                        Required: false)),
                lower => lower.Contains("volume") || lower.Contains("mute"),
                (ctx, args) =>
                {
                    ToolResult result;
                    switch (args["action"])
                    {
                        case "up":
                            int up = ctx.Audio.VolumeUp();
                            result = ToolResult.Speak($"Volume's now at {up} percent.")
                                .With("volume_percent", up.ToString());
                            break;
                        case "down":
                            int down = ctx.Audio.VolumeDown();
                            result = ToolResult.Speak($"Volume's now at {down} percent.")
                                .With("volume_percent", down.ToString());
                            break;
                        case "mute":
                            ctx.Audio.Mute();
                            result = ToolResult.Speak("Muted.").With("muted", "true");
                            break;
                        case "unmute":
                            ctx.Audio.Unmute();
                            result = ToolResult.Speak("Unmuted.").With("muted", "false");
                            break;
                        case "set":
                            if (args.TryGetValue("level", out string lvl) && int.TryParse(lvl, out int target))
                            {
                                int actual = ctx.Audio.SetVolume(target);
                                result = ToolResult.Speak($"Volume set to {actual} percent.")
                                    .With("volume_percent", actual.ToString());
                            }
                            else
                            {
                                result = ToolResult.Speak("What level would you like the volume set to?")
                                    .With("needs", "level");
                            }
                            break;
                        default:
                            result = ToolResult.None;
                            break;
                    }
                    return Task.FromResult(result);
                },
                text =>
                {
                    string lower = text.ToLower();
                    var d = new Dictionary<string, string>();
                    var num = System.Text.RegularExpressions.Regex.Match(lower, @"\d+");
                    if (num.Success)
                    {
                        d["action"] = "set";
                        d["level"] = num.Value;
                    }
                    else if (lower.Contains("unmute")) d["action"] = "unmute";
                    else if (lower.Contains("mute")) d["action"] = "mute";
                    else if (lower.Contains("down") || lower.Contains("lower") || lower.Contains("decrease"))
                        d["action"] = "down";
                    else d["action"] = "up";
                    return d;
                }));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("take_screenshot",
                    "Capture a screenshot of the whole screen, save it, and open it."),
                lower => lower.Contains("screenshot") || lower.Contains("screen shot") ||
                         (lower.Contains("capture") && lower.Contains("screen")),
                (ctx, args) =>
                {
                    try
                    {
                        string path = ctx.Screenshot.Capture();
                        ctx.Screenshot.Open(path);
                        return Task.FromResult(
                            ToolResult.Speak("Done! I took a screenshot and opened it for you.")
                                .With("path", path));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Screenshot failed: " + ex.Message);
                        return Task.FromResult(ToolResult.Failed(
                            "Sorry, I couldn't take the screenshot.", ex.Message));
                    }
                }));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("kill_process",
                    "Force-close a running program by its process or application name.",
                    new ToolParameter("name", "string",
                        "The process or application name to terminate, e.g. 'chrome' or 'spotify'.")),
                lower => (lower.Contains("kill") || lower.Contains("terminate") || lower.Contains("force close") ||
                          lower.Contains("force quit")) &&
                         (lower.Contains("process") || lower.Contains("task") || lower.Contains("program") ||
                          lower.Contains("app")),
                (ctx, args) =>
                {
                    var killed = ctx.Processes.KillByName(args["name"]);
                    ToolResult result = killed.Killed > 0
                        ? ToolResult.Speak(
                            $"Closed {killed.Killed} {killed.MatchedName} " +
                            $"{(killed.Killed == 1 ? "process" : "processes")}.")
                        : ToolResult.Speak(
                            $"I couldn't find a running process called {killed.MatchedName}.");
                    return Task.FromResult(result
                        .With("matched_name", killed.MatchedName)
                        .With("killed_count", killed.Killed.ToString()));
                },
                text =>
                {
                    string name = text.ToLower().TrimEnd('.', '!', '?');
                    foreach (var verb in new[] { "terminate", "force close", "force quit", "kill", "close", "end", "stop", "quit" })
                    {
                        int i = name.IndexOf(verb);
                        if (i >= 0) { name = name.Substring(i + verb.Length); break; }
                    }
                    foreach (var filler in new[] { "the process", "the task", "the program", "the app",
                                                   "process", "task", "program", "application", "app", "the" })
                        name = name.Replace(filler, " ");
                    return new Dictionary<string, string> { ["name"] = name.Trim() };
                }));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("open_app",
                    "Open or launch ANY installed desktop application by name — it resolves " +
                    "against the user's Start menu, so pass whatever app they said (e.g. " +
                    "Chrome, Spotify, OBS, Blender, Photoshop, Steam, Discord).",
                    new ToolParameter("name", "string", "The application name the user said.")),
                lower => lower.StartsWith("open ") || lower.StartsWith("launch ") || lower.StartsWith("start "),
                (ctx, args) =>
                {
                    ToolResult result = ctx.Apps.TryLaunch(args["name"], out string launched)
                        ? ToolResult.Speak($"Opening {launched}.").With("launched", launched)
                        : ToolResult.Failed(
                            $"Sorry, I couldn't find an app called {args["name"]}.",
                            $"no Start-menu match for '{args["name"]}'");
                    return Task.FromResult(result);
                },
                text =>
                {
                    string name = text.TrimEnd('.', '!', '?');
                    foreach (var verb in new[] { "open ", "launch ", "start " })
                    {
                        int i = name.ToLower().IndexOf(verb);
                        if (i >= 0) { name = name.Substring(i + verb.Length); break; }
                    }
                    return new Dictionary<string, string> { ["name"] = name.Trim() };
                }));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("switch_audio_output",
                    "Switch the default audio output device, e.g. to speakers or headphones.",
                    new ToolParameter("device", "string",
                        "Name (or part of the name) of the output device to switch to, e.g. 'headphones' or 'speakers'.")),
                lower => (lower.Contains("switch") || lower.Contains("change") || lower.Contains("set")) &&
                         (lower.Contains("headphone") || lower.Contains("speaker") ||
                          lower.Contains("output") || lower.Contains("audio device") || lower.Contains("sound device")),
                (ctx, args) =>
                {
                    string matched = ctx.Audio.SwitchOutputDevice(args["device"]);
                    if (matched != null)
                    {
                        return Task.FromResult(
                            ToolResult.Speak($"Switched audio output to {matched}.")
                                .With("device", matched));
                    }
                    else
                    {
                        var available = ctx.Audio.ListOutputDevices();
                        string list = available.Count > 0
                            ? string.Join(", ", available)
                            : "no active output devices";
                        // The device list goes back as data too: asked to switch to
                        // something that isn't there, the model can offer what is.
                        return Task.FromResult(ToolResult
                            .Speak($"I couldn't find an output device matching {args["device"]}. " +
                                   $"Available devices are: {list}.")
                            .With("error", $"no output device matching '{args["device"]}'")
                            .With("available_devices", list));
                    }
                },
                text =>
                {
                    string lower = text.ToLower();
                    string device = lower.Contains("headphone") ? "headphone"
                        : lower.Contains("speaker") ? "speaker"
                        : string.Empty;
                    return new Dictionary<string, string> { ["device"] = device };
                }));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("control_media",
                    "Control the currently playing music or video: play/pause, skip to the " +
                    "next track, go back to the previous track, or stop.",
                    new ToolParameter("action", "string",
                        "The media action to perform.",
                        AllowedValues: new[] { "playpause", "play", "pause", "next", "previous", "stop" })),
                lower => lower.Contains("music") || lower.Contains("song") || lower.Contains("track") ||
                         lower.Contains("play ") || lower == "play" || lower == "play." ||
                         lower.Contains("pause") || lower.Contains("resume") ||
                         lower.Contains("skip") || lower.Contains("next") || lower.Contains("previous"),
                (ctx, args) =>
                {
                    ToolResult result;
                    switch (args["action"])
                    {
                        case "next":
                            ctx.Media.Next();
                            result = ToolResult.Speak("Skipping ahead.").With("action", "next");
                            break;
                        case "previous":
                            ctx.Media.Previous();
                            result = ToolResult.Speak("Going back.").With("action", "previous");
                            break;
                        case "stop":
                            ctx.Media.Stop();
                            result = ToolResult.Speak("Stopped.").With("action", "stop");
                            break;
                        // play, pause, and playpause all map to the play/pause
                        // toggle — the media key is a single toggle regardless.
                        default:
                            ctx.Media.PlayPause();
                            result = ToolResult.Speak("Done.").With("action", "playpause");
                            break;
                    }
                    return Task.FromResult(result);
                },
                text =>
                {
                    string lower = text.ToLower();
                    string action;
                    if (lower.Contains("next") || lower.Contains("skip")) action = "next";
                    else if (lower.Contains("previous") || lower.Contains("back") || lower.Contains("last")) action = "previous";
                    else if (lower.Contains("stop")) action = "stop";
                    else if (lower.Contains("pause")) action = "pause";
                    else action = "play";
                    return new Dictionary<string, string> { ["action"] = action };
                }));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("whats_playing",
                    "Say what song, track, or video is currently playing."),
                lower => (lower.Contains("what") || lower.Contains("who")) &&
                         (lower.Contains("playing") || lower.Contains("song") || lower.Contains("this")),
                async (ctx, args) =>
                {
                    var np = await ctx.NowPlaying.GetCurrentAsync();
                    string spoken = np?.Spoken();
                    if (spoken == null)
                    {
                        return ToolResult.Speak("Nothing seems to be playing right now.")
                            .With("playing", "false");
                    }
                    // Title and artist separately as well as the spoken form, so a
                    // follow-up ("who's the artist?") doesn't need the model to
                    // re-parse the sentence it just said.
                    return ToolResult.Speak($"This is {spoken}.")
                        .With("playing", "true")
                        .With("title", np.Title)
                        .With("artist", np.Artist);
                }));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("set_timer",
                    "Set a countdown timer or a reminder that fires after a delay, e.g. " +
                    "'set a timer for 10 minutes' or 'remind me to check the oven in 20 minutes'.",
                    new ToolParameter("duration_seconds", "integer",
                        "The countdown length in seconds (convert minutes/hours yourself, " +
                        "e.g. 10 minutes = 600)."),
                    new ToolParameter("label", "string",
                        "What to remind the user about when it fires, if they said. Omit for a plain timer.",
                        Required: false)),
                lower => lower.Contains("timer") ||
                         (lower.Contains("remind") && (lower.Contains(" in ") || lower.Contains("minute") ||
                                                       lower.Contains("hour") || lower.Contains("second"))),
                (ctx, args) =>
                {
                    if (!args.TryGetValue("duration_seconds", out string ds) ||
                        !int.TryParse(ds, out int secs) || secs < 1)
                    {
                        return Task.FromResult(
                            ToolResult.Speak("How long would you like the timer for?")
                                .With("needs", "duration_seconds"));
                    }
                    string label = args.TryGetValue("label", out string l) ? l : null;
                    ctx.Reminders.AddTimer(secs, label);
                    string what = string.IsNullOrWhiteSpace(label) ? "" : $" to {label}";
                    return Task.FromResult(ToolResult
                        .Speak($"Okay, I'll remind you{what} in {DescribeDuration(secs)}.")
                        .With("duration_seconds", secs.ToString())
                        .With("label", label ?? string.Empty)
                        .With("fires_at_local", DateTime.Now.AddSeconds(secs).ToString("HH:mm")));
                },
                text =>
                {
                    string lower = text.ToLower();
                    var d = new Dictionary<string, string>();
                    int total = 0;
                    var mh = System.Text.RegularExpressions.Regex.Match(lower, @"(\d+)\s*(hour|hr)");
                    if (mh.Success) total += int.Parse(mh.Groups[1].Value) * 3600;
                    var mm = System.Text.RegularExpressions.Regex.Match(lower, @"(\d+)\s*(minute|min)");
                    if (mm.Success) total += int.Parse(mm.Groups[1].Value) * 60;
                    var msec = System.Text.RegularExpressions.Regex.Match(lower, @"(\d+)\s*(second|sec)");
                    if (msec.Success) total += int.Parse(msec.Groups[1].Value);
                    if (total == 0)
                    {
                        var bare = System.Text.RegularExpressions.Regex.Match(lower, @"(\d+)");
                        if (bare.Success) total = int.Parse(bare.Groups[1].Value) * 60; // bare number => minutes
                    }
                    if (total > 0) d["duration_seconds"] = total.ToString();
                    int ti = lower.IndexOf(" to ");
                    if (ti >= 0) d["label"] = text.Substring(ti + 4).TrimEnd('.', '!', '?');
                    return d;
                }));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("set_alarm",
                    "Set an alarm or reminder for a specific clock time, e.g. 'set an alarm " +
                    "for 7 AM' or 'remind me to leave at 5:30 PM'.",
                    new ToolParameter("time", "string",
                        "The target time in 24-hour HH:mm format (e.g. 07:00 or 17:30)."),
                    new ToolParameter("label", "string",
                        "What to remind the user about when it fires, if they said. Omit for a plain alarm.",
                        Required: false)),
                lower => lower.Contains("alarm") || lower.Contains("wake me") ||
                         (lower.Contains("remind") && lower.Contains(" at ")),
                (ctx, args) =>
                {
                    if (!args.TryGetValue("time", out string timeText) || string.IsNullOrWhiteSpace(timeText))
                    {
                        return Task.FromResult(
                            ToolResult.Speak("What time should I set it for?").With("needs", "time"));
                    }
                    string label = args.TryGetValue("label", out string l) ? l : null;
                    DateTime? fireAt = ctx.Reminders.AddAlarm(timeText, label);
                    if (fireAt == null)
                    {
                        return Task.FromResult(ToolResult.Failed(
                            "Sorry, I didn't catch what time you meant.",
                            $"could not parse '{timeText}' as a time"));
                    }
                    string what = string.IsNullOrWhiteSpace(label) ? "" : $" to {label}";
                    string when = fireAt.Value.Date == DateTime.Today
                        ? $"at {fireAt.Value:t}"
                        : $"tomorrow at {fireAt.Value:t}";
                    return Task.FromResult(ToolResult
                        .Speak($"Okay, I'll remind you{what} {when}.")
                        .With("fires_at_local", fireAt.Value.ToString("yyyy-MM-dd HH:mm"))
                        .With("label", label ?? string.Empty));
                },
                text =>
                {
                    var d = new Dictionary<string, string>();
                    var tm = System.Text.RegularExpressions.Regex.Match(
                        text, @"\d{1,2}(:\d{2})?\s*(am|pm|AM|PM)?");
                    if (tm.Success) d["time"] = tm.Value.Trim();
                    int ti = text.ToLower().IndexOf(" to ");
                    if (ti >= 0) d["label"] = text.Substring(ti + 4).TrimEnd('.', '!', '?');
                    return d;
                }));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("list_reminders",
                    "List the user's pending timers, alarms, and reminders."),
                lower => (lower.Contains("list") || lower.Contains("what") || lower.Contains("any")) &&
                         (lower.Contains("timer") || lower.Contains("alarm") || lower.Contains("reminder")),
                (ctx, args) =>
                {
                    var pending = ctx.Reminders.Pending();
                    if (pending.Count == 0)
                    {
                        return Task.FromResult(
                            ToolResult.Speak("You have no timers or alarms set.").With("count", "0"));
                    }
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"You have {pending.Count} {(pending.Count == 1 ? "reminder" : "reminders")}: ");
                    ToolResult result = ToolResult.None.With("count", pending.Count.ToString());
                    for (int i = 0; i < pending.Count; i++)
                    {
                        var p = pending[i];
                        string when = p.FireAt.Date == DateTime.Today
                            ? p.FireAt.ToString("t")
                            : $"tomorrow at {p.FireAt:t}";
                        sb.Append(string.IsNullOrWhiteSpace(p.Label) ? when : $"{p.Label} at {when}");
                        sb.Append(i < pending.Count - 1 ? "; " : ".");

                        // Each reminder numbered, so "cancel the second one" has
                        // something to refer to.
                        result = result
                            .With($"reminder_{i + 1}_at", p.FireAt.ToString("yyyy-MM-dd HH:mm"))
                            .With($"reminder_{i + 1}_label", p.Label ?? string.Empty);
                    }

                    ToolResult spoken = ToolResult.Speak(sb.ToString());
                    foreach (KeyValuePair<string, string> kv in result.Data) spoken = spoken.With(kv.Key, kv.Value);
                    return Task.FromResult(spoken);
                }));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("cancel_reminders",
                    "Cancel all pending timers, alarms, and reminders."),
                lower => lower.Contains("cancel") &&
                         (lower.Contains("timer") || lower.Contains("alarm") || lower.Contains("reminder")),
                (ctx, args) =>
                {
                    int n = ctx.Reminders.CancelAll();
                    return Task.FromResult(ToolResult
                        .Speak(n == 0
                            ? "There was nothing to cancel."
                            : $"Cancelled {n} {(n == 1 ? "reminder" : "reminders")}.")
                        .With("cancelled_count", n.ToString()));
                }));

            // --- Composition primitives (LLM-only; no keyword path) ------------------

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("wait",
                    "Pause for a number of seconds. Use it BETWEEN other tool calls to make " +
                    "timed effects, e.g. turning a light on, waiting, then off so a flash is " +
                    "visible. Keep the wait short.",
                    new ToolParameter("seconds", "integer", "Seconds to pause, from 1 to 30.")),
                lower => false, // composition-only — the model calls it, not the keyword path
                async (ctx, args) =>
                {
                    int secs = 1;
                    if (args.TryGetValue("seconds", out string s) && int.TryParse(s, out int parsed)) secs = parsed;
                    secs = Math.Max(0, Math.Min(30, secs));
                    await Task.Delay(secs * 1000);
                },
                ephemeral: true));

            registry.Add(new VoiceCommand(
                ToolDefinition.Create("repeat",
                    "Repeat a sequence of tool calls several times — use for looping effects " +
                    "like flashing a light N times. 'actions' is a JSON array of steps, each an " +
                    "object {\"tool\":\"<tool name>\",\"args\":{...}}. Example to flash the bedroom " +
                    "light 3 times: times=3, actions=" +
                    "[{\"tool\":\"control_lights\",\"args\":{\"state\":\"on\",\"room\":\"bedroom\"}}," +
                    "{\"tool\":\"wait\",\"args\":{\"seconds\":\"1\"}}," +
                    "{\"tool\":\"control_lights\",\"args\":{\"state\":\"off\",\"room\":\"bedroom\"}}," +
                    "{\"tool\":\"wait\",\"args\":{\"seconds\":\"1\"}}].",
                    new ToolParameter("times", "integer", "How many times to repeat the sequence, from 1 to 10."),
                    new ToolParameter("actions", "string",
                        "JSON array of steps to repeat, each {\"tool\":\"<name>\",\"args\":{...}}.")),
                lower => false,
                async (ctx, args) =>
                {
                    if (ctx.RunTool == null) return;
                    int times = 1;
                    if (args.TryGetValue("times", out string t) && int.TryParse(t, out int parsedT)) times = parsedT;
                    times = Math.Max(1, Math.Min(10, times));

                    if (!args.TryGetValue("actions", out string actionsJson) || string.IsNullOrWhiteSpace(actionsJson))
                        return;
                    var steps = ParseRepeatActions(actionsJson);
                    if (steps.Count == 0) return;

                    for (int i = 0; i < times; i++)
                    {
                        foreach (var step in steps)
                        {
                            // No nesting — a repeat inside a repeat could block for a long time.
                            if (string.Equals(step.Tool, "repeat", StringComparison.OrdinalIgnoreCase)) continue;
                            await ctx.RunTool(step.Tool, step.Args);
                        }
                    }
                },
                ephemeral: true));

            return registry;
        }

        private sealed class RepeatStep
        {
            public string Tool;
            public Dictionary<string, string> Args;
        }

        // Parses the `repeat` tool's `actions` argument (a JSON array of
        // {tool, args} steps) into a runnable list. Defensive: malformed input
        // yields an empty list rather than throwing.
        private static List<RepeatStep> ParseRepeatActions(string json)
        {
            var result = new List<RepeatStep>();
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        if (el.ValueKind != JsonValueKind.Object) continue;
                        if (!el.TryGetProperty("tool", out var toolEl) ||
                            toolEl.ValueKind != JsonValueKind.String) continue;

                        var step = new RepeatStep
                        {
                            Tool = toolEl.GetString(),
                            Args = new Dictionary<string, string>()
                        };
                        if (el.TryGetProperty("args", out var argsEl) &&
                            argsEl.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var p in argsEl.EnumerateObject())
                            {
                                step.Args[p.Name] = p.Value.ValueKind == JsonValueKind.String
                                    ? p.Value.GetString()
                                    : p.Value.GetRawText();
                            }
                        }
                        result.Add(step);
                    }
                }
            }
            catch (JsonException)
            {
                // Malformed actions -> nothing to run.
            }
            return result;
        }

        // Human-friendly spoken duration, e.g. "5 minutes", "1 hour and 30 minutes".
        private static string DescribeDuration(int seconds)
        {
            int hours = seconds / 3600;
            int minutes = (seconds % 3600) / 60;
            int secs = seconds % 60;

            var parts = new List<string>();
            if (hours > 0) parts.Add($"{hours} {(hours == 1 ? "hour" : "hours")}");
            if (minutes > 0) parts.Add($"{minutes} {(minutes == 1 ? "minute" : "minutes")}");
            if (secs > 0 && hours == 0) parts.Add($"{secs} {(secs == 1 ? "second" : "seconds")}");
            if (parts.Count == 0) return "a moment";
            if (parts.Count == 1) return parts[0];
            return string.Join(" and ", parts);
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

        // ── Blocking sub-dialogs — deliberately NOT converted here ──────────────
        //
        // These still speak for themselves and open their own SpeechRecognizer,
        // which is why they are the only handlers left holding a microphone. That
        // is Phase 4a's scope, not this one: converting them means changing the
        // SHAPE of the interaction (a confirmation becomes a tool round trip the
        // model drives), and it carries a safety requirement — `confirmed` has to
        // become a declared, validated parameter so a model cannot hallucinate its
        // way to `shutdown /s /t 0`. Returning a ToolResult would not fix them; it
        // would only hide that they still steal the mic.
        //
        // Note for Phase 4a: its brief lists three of these — shutdown, YouTube,
        // and SMS. There is a FOURTH, PlaystationController.TurnOnPlaystation
        // (PlaystationController.cs:56), which opens its own SpeechRecognizer to
        // ask which game to load. It will deadlock a Live session exactly like the
        // other three.
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
