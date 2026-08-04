using Microsoft.CognitiveServices.Speech;
using Personal_Assistant.AppLaunching;
using Personal_Assistant.Arduino;
using Personal_Assistant.AudioControl;
using Personal_Assistant.Diagnostics;
using Personal_Assistant.Dispatch;
using Personal_Assistant.GeminiClient;
using Personal_Assistant.Geolocator;
using Personal_Assistant.LightAutomator;
using Personal_Assistant.Live;
using Personal_Assistant.MediaControl;
using Personal_Assistant.PlaystationController;
using Personal_Assistant.PrayerTimesCalculator;
using Personal_Assistant.ProcessControl;
using Personal_Assistant.Reminders;
using Personal_Assistant.ScreenCapture;
using Personal_Assistant.SMSController;
using Personal_Assistant.SpeechManager;
using Personal_Assistant.VoiceClips;
using Personal_Assistant.WeatherService;
using Python.Runtime;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
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

        // The one line the exit tool speaks. A constant because the clip cache is
        // keyed on the exact text — an inline literal here and a different one in
        // the render list would miss every time and silently fall back to Azure.
        public const string Goodbye = "Alright goodbye!";

        // Every line spoken outside a Live conversation, i.e. everything that
        // needs a pre-rendered clip to stay in voice. Keep in sync with the
        // greeting pools; --render-clips reads exactly this.
        public static IReadOnlyList<string> ClipLines()
        {
            var lines = new List<string>();
            lines.AddRange(morningGreetings);
            lines.AddRange(afternoonGreetings);
            lines.AddRange(eveningGreetings);
            lines.AddRange(nightGreetings);
            lines.Add(Goodbye);

            // Fired timers and alarms speak outside any Live session too. Must
            // match ReminderService.Fire's wording exactly or the cache misses.
            lines.Add("Your timer is done.");
            lines.Add("Your alarm is going off.");
            return lines;
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

        public static async Task Main(string[] args)
        {
            CheckEnvironmentVariables();

            // Offline mode: render the greeting/goodbye clips in the configured
            // Live voice and exit. Rendering goes through the Live API, which is
            // unmetered on this project, rather than the TTS model, which is
            // 3/min and 10/day. Re-run after changing LAITH_LIVE_VOICE.
            int renderAt = args == null ? -1 : Array.IndexOf(args, "--render-clips");
            if (renderAt >= 0)
            {
                string voice = Environment.GetEnvironmentVariable("LAITH_LIVE_VOICE");

                // Any text after the switch is rendered instead of the standard
                // set — for pre-rendering reminder labels you use often, so they
                // never pay the on-demand render at the moment the timer fires.
                var extra = new List<string>();
                for (int i = renderAt + 1; i < args.Length; i++) extra.Add(args[i]);

                int failed = await VoiceClipRenderer.RenderAsync(
                    extra.Count > 0 ? extra : ClipLines(), voice);
                Environment.Exit(failed == 0 ? 0 : 1);
            }

            // 49 (ASCII art)
            Console.WriteLine("                                    \r\n     ,AM  .d*\"*bg.\r\n    AVMM 6MP    Mb\r\n  ,W' MM YMb    MM\r\n,W'   MM  `MbmmdM9\r\nAmmmmmMMmm     .M'\r\n      MM     .d9  \r\n      MM   m\"'    \n\n");

            // Teardown for the Live session, registered before anything can open
            // one. ProcessExit covers both an ordinary return and the
            // Environment.Exit(0) the `exit_assistant` tool calls after
            // PythonEngine.Shutdown(); CancelKeyPress covers Ctrl+C. Neither path
            // may leave a WebSocket streaming silence behind it.
            AppDomain.CurrentDomain.ProcessExit += (s, e) => CloseActiveSession("process exiting");
            Console.CancelKeyPress += (s, e) => CloseActiveSession("Ctrl+C");

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

            // Azure STT no longer runs the conversation — the Live session does its
            // own — but the recognizer and its contact phrase list are kept built
            // and warm because Phase 5's fallback drops straight back onto them
            // when a Live session ends dirty. Azure keeps the wake word either way.
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
            // SayClip, not Say: a fired timer speaks outside any Live session, so
            // it was the last thing still answering in the Azure voice. The two
            // unlabelled announcements are pre-rendered by --render-clips; a
            // labelled reminder ("Reminder: take the bins out.") is user-supplied
            // text that cannot be pre-rendered, and falls back to Azure.
            var reminders = new ReminderService(
                message => speechManager.SayClip("⏰", message),
                timerWidgets,
                // Rendering a clip takes ~5-7s, so a labelled reminder gets its
                // line rendered when the timer is SET rather than when it fires.
                // By the time it goes off the clip is already cached, and the
                // announcement is instant and in the right voice.
                prepare: message => VoiceClipRenderer.TryEnsureAsync(
                    Environment.GetEnvironmentVariable("LAITH_LIVE_VOICE"), message));

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

            // The `listenImmediately` flag the turn-based loop used for barge-in is
            // gone from THIS loop — a Live conversation stays open across follow-ups
            // on its own, so there is no "skip the wakeword next time" state to keep
            // between wakes. It still exists inside the fallback below, which is the
            // old turn-based path and still barges in the old way.

            // Latched so the "switching to the backup" notice is said once, not on
            // every turn of a broken evening. Cleared by a Live conversation that
            // ends cleanly, so a SECOND outage after a recovery is announced again
            // — the alternative is an assistant that silently runs degraded for the
            // rest of the process run and never says so.
            bool fallbackNoticeGiven = false;

            // The fallback: the ORIGINAL turn-based conversation, unchanged.
            // RecognizeOnceAsync (Azure STT) -> IntentDispatcher.DispatchAsync ->
            // Say (Azure TTS), with the same barge-in continuation the old loop had,
            // the same per-turn latency summary, and the same recognizer and warm
            // contact phrase list built at startup. Nothing about this path is new,
            // which is the point: it is known to work, so it is what a failed Live
            // session falls back ONTO.
            //
            // One conversation's worth, i.e. one turn plus however many follow-ups
            // the user barges in for — then control returns to the wake word, and
            // the next wake tries Live again.
            async Task RunFallbackConversationAsync()
            {
                if (!fallbackNoticeGiven)
                {
                    fallbackNoticeGiven = true;
                    // Through the SAME SpeechService the rest of the app uses. A
                    // second instance silently breaks the echo gate and dictation;
                    // it once sent a real empty SMS to a real number.
                    await speechManager.Say(
                        "Hey 49",
                        "I'm having trouble with the live connection, switching to the backup.");
                }

                bool listenImmediately;
                do
                {
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

                    // NoMatch is already handled (spoken) by ConvertSpeechToText;
                    // only dispatch real recognised speech.
                    if (speechRecognitionResult.Reason != ResultReason.NoMatch)
                    {
                        bool interrupted = await dispatcher.DispatchAsync(recognizedText);
                        if (interrupted) listenImmediately = true;
                        Console.WriteLine(latency.Summary());
                    }
                } while (listenImmediately);
            }

            while (true)
            {
                bool woke = await speechManager.KeywordRecognizer();
                Console.WriteLine($"[loop] KeywordRecognizer returned {woke} at {DateTime.Now:HH:mm:ss.fff}");
                // Only greet + listen when the wakeword actually fired. On an
                // errored/early return, loop back and keep waiting instead of
                // spuriously greeting (which previously ran away in a loop).
                if (!woke) continue;

                string greeting = PickGreeting(DateTime.Now.Hour);
                Console.WriteLine($"[loop] about to call Say at {DateTime.Now:HH:mm:ss.fff}");
                // Greeting is NOT interruptible: barge-in matters for long
                // conversational replies, not a two-second greeting. It also has to
                // finish before the session opens its microphone, or the Live model
                // hears the greeting as the user's first utterance.
                //
                // SayClip so the greeting is in the SAME voice as the conversation
                // it introduces. It cannot be spoken by the Live model itself --
                // this is what covers socket setup, so it has to be audible before
                // the session exists. Falls back to Azure TTS if unrendered.
                await speechManager.SayClip("Hey 49", greeting);

                // One Gemini Live conversation, wake word to close. It owns STT,
                // reasoning, tool calls and TTS over a single WebSocket, so there
                // is no RecognizeOnceAsync / DispatchAsync turn to run here — the
                // session drives tools through the same dispatcher directly.
                //
                // `using` is the outermost of several guarantees that the socket
                // closes: RunConversationAsync's own finally already tore it down
                // before this ever runs, and Dispose is idempotent. Belt and braces
                // is the right amount of caution for the one failure mode that can
                // exhaust the free tier.
                bool clean = false;
                LiveSessionOutcome outcome = LiveSessionOutcome.Faulted;
                using (var session = new LiveSession(
                    dispatcher, context, registry.ToolDefinitions, speechManager, latency: latency))
                {
                    activeSession = session;
                    try
                    {
                        clean = await session.RunConversationAsync();
                    }
                    finally
                    {
                        outcome = session.Outcome;
                        activeSession = null;
                    }
                }

                // The session logs its own close line with duration, turn count and
                // audio totals, plus the [session] accounting lines. The per-turn
                // latency summary the turn-based loop printed here is deliberately
                // not printed for a Live conversation: nothing on the Live path
                // feeds the stt/llm/tts counters — the model does all three inside
                // one socket — so it would only ever print zeros. It comes back on
                // the fallback path below, where those three stages are real again.
                if (clean)
                {
                    // Live is working, so re-arm the notice. See the latch above.
                    fallbackNoticeGiven = false;
                }
                else
                {
                    // A dirty outcome is Faulted (handshake failed, or the session
                    // died mid-turn) or HardCap. Either way the conversation did not
                    // end the way one is supposed to, and the user is standing there
                    // having been greeted, so give them a working assistant rather
                    // than silence.
                    //
                    // NOT sticky: the next wake word tries Live again. A transient
                    // socket failure shouldn't permanently downgrade the assistant,
                    // and the wake word is a natural, free retry point. There is
                    // deliberately no retry ladder, backoff, health monitor or quota
                    // detection here — Layth's usage bounds steady-state quota well
                    // inside any plausible free tier, and the failure that CAN burn
                    // it is a stuck session, which the watchdog in LiveSession owns.
                    Console.WriteLine(
                        $"[loop] Live session ended abnormally (outcome={outcome}) — " +
                        "falling back to the turn-based path for this conversation");
                    await RunFallbackConversationAsync();
                }
            }
        }

        // The conversation currently open, if any. Exists so process teardown can
        // close its socket: a leaked WebSocket streaming silence costs ~115k input
        // tokens an hour, and "the app was killed mid-conversation" is the ordinary
        // way that happens. Registered at the top of Main.
        //
        // Dispose rather than a cancellation token on purpose: ProcessExit gives
        // roughly two seconds, and Dispose aborts the socket outright where a
        // graceful close would still be waiting on a round trip.
        private static LiveSession activeSession;

        private static void CloseActiveSession(string why)
        {
            LiveSession session = System.Threading.Interlocked.Exchange(ref activeSession, null);
            if (session == null) return;
            Console.WriteLine($"[loop] {why} — closing the open Live session");
            try { session.Dispose(); } catch { }
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
                    await ctx.Speech.SayClip(ctx.RecognizedText, Goodbye);
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
                    "Opens a Google results page in the user's browser. This is a BROWSER " +
                    "ACTION, not a way to find things out: it returns no results to you and " +
                    "shows the user a web page. Use it ONLY when the user asks to open, pull " +
                    "up, or show a search — \"google X\", \"search up X\", \"show me results for " +
                    "X\". To ANSWER a question about the world, use your own built-in Google " +
                    "Search grounding and reply directly; never call this tool for that.",
                    new ToolParameter("query", "string",
                        "The search terms to put in the browser's search box.")),
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
                    "Open YouTube, either searching for a specific video or just opening the " +
                    "site. If the user did not make clear which one they want, ask them first " +
                    "and then call this — the tool cannot ask on its own.",
                    new ToolParameter("mode", "string",
                        "\"search\" to search for a specific video, \"open\" to just open YouTube.",
                        AllowedValues: new[] { "search", "open" }),
                    new ToolParameter("query", "string",
                        "What to search for. Required when mode is \"search\".",
                        Required: false)),
                lower => lower.Contains("youtube"),
                (ctx, args) =>
                {
                    args.TryGetValue("query", out string query);
                    return HandleYouTubeAsync(args["mode"], query);
                },
                text =>
                {
                    string query = ExtractYouTubeQuery(text.ToLower());
                    return string.IsNullOrWhiteSpace(query)
                        ? new Dictionary<string, string> { ["mode"] = "open" }
                        : new Dictionary<string, string> { ["mode"] = "search", ["query"] = query };
                }));

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
                    "Turn on the PlayStation 5 via Remote Play and launch a game. Ask the user " +
                    "which game they want before calling this — the tool cannot ask on its own.",
                    new ToolParameter("game", "string",
                        "The title of the game to load, as the user said it.")),
                lower => lower.Contains("turn on") && (lower.Contains("playstation") || lower.Contains("ps-5")),
                async (ctx, args) =>
                {
                    string game = args["game"];
                    bool launched = await ctx.Playstation.TurnOnPlaystation(game);
                    return launched
                        ? ToolResult.Speak($"{game} is ready! Have fun!").With("game", game)
                        : ToolResult.Failed(
                            "Sorry, I couldn't get Remote Play started.", "remote_play_unavailable");
                },
                text =>
                {
                    string game = ExtractGameTitle(text.ToLower());
                    return string.IsNullOrWhiteSpace(game)
                        ? VoiceCommand.EmptyArgs
                        : new Dictionary<string, string> { ["game"] = game };
                }));

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
                        "Send a text message to one of the user's known contacts. This really " +
                        "sends to a real phone and cannot be undone. Read the message back to " +
                        "the user and ask whether to send it; only then call again with " +
                        "`confirmed` set from what they actually said. Never decide `confirmed` " +
                        "yourself — pass \"unknown\" until the user has answered out loud. The " +
                        "first call never sends; it returns the question for you to ask.",
                        new ToolParameter("contact", "string",
                            "Which contact to message.",
                            AllowedValues: new List<string>(contacts.Keys)),
                        new ToolParameter("message", "string",
                            "The exact words to send. Never empty, and never invented — this is " +
                            "what the contact will read."),
                        new ToolParameter("confirmed", "string",
                            "The user's own answer to the read-back question. \"unknown\" means " +
                            "you have not asked yet; only ever \"yes\" if they said so out loud.",
                            AllowedValues: Confirmation.AllowedValues)),
                    lower => TryMatchContact(contacts, lower, out _, out _),
                    (ctx, args) =>
                    {
                        args.TryGetValue("message", out string message);
                        return HandleSendSmsAsync(ctx, args["contact"], message, args["confirmed"]);
                    },
                    text =>
                    {
                        if (!TryMatchContact(contacts, text.ToLower(), out string name, out _))
                            return VoiceCommand.EmptyArgs;

                        // The keyword path has no model to compose a body, so the
                        // best it can do is carry across whatever the user already
                        // said ("text mom I'll be late"). With nothing to send this
                        // fails validation and falls through to conversation, which
                        // is the right direction to fail in.
                        var args = new Dictionary<string, string>
                        {
                            ["contact"] = name,
                            ["confirmed"] = Confirmation.Unknown
                        };
                        string body = ExtractSmsBody(text, name);
                        if (!string.IsNullOrWhiteSpace(body)) args["message"] = body;
                        return args;
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
                    "Shut down or restart the computer. This ends everything the user is doing " +
                    "and cannot be undone. Ask them out loud whether they are sure, and only " +
                    "then call again with `confirmed` set from what they actually said. Never " +
                    "decide `confirmed` yourself — pass \"unknown\" until the user has answered " +
                    "out loud. The first call never acts; it returns the question for you to ask.",
                    new ToolParameter("action", "string",
                        "Whether to shut down or restart the machine.",
                        AllowedValues: new[] { "shutdown", "restart" }),
                    new ToolParameter("confirmed", "string",
                        "The user's own answer to the confirmation question. \"unknown\" means " +
                        "you have not asked yet; only ever \"yes\" if they said so out loud.",
                        AllowedValues: Confirmation.AllowedValues)),
                lower => lower == "shut down." || lower == "restart.",
                (ctx, args) => HandlePowerControlAsync(args["action"], args["confirmed"]),
                text =>
                {
                    string lower = text.ToLower();
                    return new Dictionary<string, string>
                    {
                        ["action"] = lower.Contains("shut down") ? "shutdown" : "restart",
                        ["confirmed"] = Confirmation.Unknown
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
                        "The media action to perform. Use 'play' to resume (including for " +
                        "\"unpause\") and 'pause' to pause — both are explicit and safe to repeat. " +
                        "Only use 'playpause' when the user genuinely means \"toggle\".",
                        AllowedValues: new[] { "playpause", "play", "pause", "next", "previous", "stop" })),
                lower => lower.Contains("music") || lower.Contains("song") || lower.Contains("track") ||
                         lower.Contains("play ") || lower == "play" || lower == "play." ||
                         lower.Contains("pause") || lower.Contains("resume") ||
                         lower.Contains("skip") || lower.Contains("next") || lower.Contains("previous"),
                async (ctx, args) =>
                {
                    switch (args["action"])
                    {
                        case "next":
                            ctx.Media.Next();
                            return ToolResult.Speak("Skipping ahead.").With("action", "next");
                        case "previous":
                            ctx.Media.Previous();
                            return ToolResult.Speak("Going back.").With("action", "previous");
                        case "stop":
                            ctx.Media.Stop();
                            return ToolResult.Speak("Stopped.").With("action", "stop");

                        // play and pause are explicit and idempotent, NOT the
                        // toggle they used to share. Asking to play something
                        // already playing used to pause it, so repeated requests
                        // fought each other and the video never resumed.
                        case "play":
                            return (await ctx.Media.PlayAsync())
                                ? ToolResult.Speak("Playing.").With("action", "play")
                                : ToolResult.Failed("Nothing is playing right now.", "no_media_session");
                        case "pause":
                            return (await ctx.Media.PauseAsync())
                                ? ToolResult.Speak("Paused.").With("action", "pause")
                                : ToolResult.Failed("Nothing is playing right now.", "no_media_session");

                        // Only an explicit "playpause" still toggles.
                        default:
                            ctx.Media.PlayPause();
                            return ToolResult.Speak("Done.").With("action", "playpause");
                    }
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

        // ── Sub-dialogs, reshaped into tool round trips ─────────────────────────
        //
        // These four used to speak a question and then open their OWN
        // SpeechRecognizer to hear the answer: shutdown, YouTube, SMS, and
        // PlayStation. A Gemini Live session owns the microphone for the length of
        // a conversation, so a handler that grabs it mid-command deadlocks the
        // session. Now the handler returns the question and the model asks it
        // in-session; the user's answer arrives as a follow-up tool call.
        //
        // What that trades away is that the MODEL now decides when a confirmation
        // is satisfied, and two of these actions are destructive. See Confirmation
        // for what stands in for the recognizer that used to be the gate.

        // The confirmation vocabulary, shared by every destructive tool.
        //
        // It is an enum rather than a boolean on purpose: TryValidate already
        // canonicalises AllowedValues case-insensitively and rejects anything else
        // before a handler runs, so this reuses machinery that works instead of
        // adding a boolean parse. "unknown" exists so `confirmed` can be REQUIRED —
        // a call that omits it is rejected outright, and there is still an honest
        // value for "I have not asked yet".
        public static class Confirmation
        {
            public const string Yes = "yes";
            public const string No = "no";
            public const string Unknown = "unknown";

            public static readonly IReadOnlyList<string> AllowedValues =
                new[] { Yes, No, Unknown };

            public static bool IsYes(string value) =>
                string.Equals(value, Yes, StringComparison.OrdinalIgnoreCase);

            public static bool IsNo(string value) =>
                string.Equals(value, No, StringComparison.OrdinalIgnoreCase);
        }

        // Two-phase gate for the destructive tools.
        //
        // Declaring `confirmed` stops a MALFORMED call, but no schema stops a model
        // from simply writing confirmed:"yes" on the first call it makes — and this
        // project has already sent one real empty text to a real number by trusting
        // a step that looked like it had happened. So the gate lives here, in code
        // the model cannot reach:
        //
        //   1. A destructive tool ARMS the gate and returns its question. That call
        //      performs no action, whatever `confirmed` claims.
        //   2. Only a LATER call, for the same subject, is allowed through.
        //
        // "Later" is wall-clock, not turn bookkeeping. A genuine confirmation costs
        // a spoken question plus a spoken answer and cannot arrive inside MinDelay;
        // a model issuing both calls in one batch arrives in milliseconds and is
        // refused. (Turn identity would be the tighter test, but ctx.RecognizedText
        // is set by the Azure turn loop and the Live session has no equivalent, so
        // it would deadlock the very path this exists for.) MaxAge stops a "yes"
        // left over from an abandoned request authorising anything later.
        //
        // Subject includes the arguments, so approving one message does not approve
        // a different one sent under the same "yes".
        //
        // Public for the same reason BuildRegistry is: the only two handlers that
        // pass this gate shut the machine down and text a real phone, so a harness
        // cannot verify the positive path by running them. It drives the gate
        // itself instead. A test that reimplemented this logic would prove nothing.
        public static class ConfirmationGate
        {
            private static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(1.5);
            private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

            private static readonly object sync = new object();
            private static string pendingSubject;
            private static DateTime pendingAtUtc;

            // Records that this exact request has been put to the user, and returns
            // the question to ask. Re-arming an already-pending subject restarts the
            // clock, so repeated asking never shortens the wait.
            public static void Arm(string subject)
            {
                lock (sync)
                {
                    pendingSubject = subject;
                    pendingAtUtc = DateTime.UtcNow;
                }
            }

            public static void Clear()
            {
                lock (sync) { pendingSubject = null; }
            }

            // True only if this subject was armed, long enough ago to have been
            // answered by a person, and recently enough to still mean anything.
            // Consumes the pending state either way it succeeds.
            public static bool TryConsume(string subject)
            {
                lock (sync)
                {
                    if (pendingSubject == null ||
                        !string.Equals(pendingSubject, subject, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    TimeSpan age = DateTime.UtcNow - pendingAtUtc;
                    if (age < MinDelay)
                    {
                        Console.WriteLine($"[confirm] '{subject}' confirmed after only {age.TotalMilliseconds:F0} ms -> refused");
                        return false;
                    }
                    if (age > MaxAge)
                    {
                        Console.WriteLine($"[confirm] '{subject}' confirmation is {age.TotalSeconds:F0}s stale -> refused");
                        pendingSubject = null;
                        return false;
                    }

                    pendingSubject = null;
                    return true;
                }
            }
        }

        // mode is "search" or "open" (validated by the dispatcher); query is
        // optional and only meaningful for "search".
        private static Task<ToolResult> HandleYouTubeAsync(string mode, string query)
        {
            if (!string.Equals(mode, "search", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start("https://www.youtube.com");
                return Task.FromResult(ToolResult
                    .Speak("Okay! Opening Youtube now.")
                    .With("mode", "open"));
            }

            query = (query ?? string.Empty).Trim();
            if (query.Length == 0)
            {
                // A search with nothing to search for: ask rather than opening a
                // blank results page. Not destructive, so no gate — just a question.
                const string question = "What would you like me to search for on YouTube?";
                return Task.FromResult(ToolResult
                    .Speak(question)
                    .With("status", "needs_query")
                    .With("question", question));
            }

            Process.Start($"https://www.youtube.com/results?search_query={Uri.EscapeDataString(query)}");
            return Task.FromResult(ToolResult
                .Speak($"Ok! Searching for {query} now")
                .With("mode", "search")
                .With("query", query));
        }

        // Pulls a search subject out of "search up X on youtube" and friends, for
        // the keyword fallback path (where there is no model to fill `mode`).
        // Returns null when the user just asked for YouTube itself.
        private static string ExtractYouTubeQuery(string lower)
        {
            string[] cues = { "search for ", "search up ", "look up ", "play " };
            foreach (string cue in cues)
            {
                int at = lower.IndexOf(cue, StringComparison.Ordinal);
                if (at < 0) continue;

                string rest = lower.Substring(at + cue.Length);
                int tail = rest.IndexOf("on youtube", StringComparison.Ordinal);
                if (tail < 0) tail = rest.IndexOf("youtube", StringComparison.Ordinal);
                if (tail >= 0) rest = rest.Substring(0, tail);

                rest = rest.Trim().TrimEnd('.', '?', '!');
                if (rest.Length > 0) return rest;
            }
            return null;
        }

        // Pulls a game title out of "turn on the playstation and play X", for the
        // keyword fallback path. Returns null when the user named no game, which
        // fails validation — better than launching Remote Play and guessing.
        private static string ExtractGameTitle(string lower)
        {
            string[] cues = { "and play ", "then play ", "to play ", "play " };
            foreach (string cue in cues)
            {
                int at = lower.IndexOf(cue, StringComparison.Ordinal);
                if (at < 0) continue;

                string rest = lower.Substring(at + cue.Length).Trim().TrimEnd('.', '?', '!');
                if (rest.Length > 0) return rest;
            }
            return null;
        }

        // action is "shutdown" or "restart", confirmed is one of Confirmation's
        // values — both validated by the dispatcher before this runs.
        private static Task<ToolResult> HandlePowerControlAsync(string action, string confirmed)
        {
            bool isShutdown = string.Equals(action, "shutdown", StringComparison.OrdinalIgnoreCase);
            string verb = isShutdown ? "shut down" : "restart";
            string gerund = isShutdown ? "Shutting down" : "Restarting now";
            string subject = "power_control:" + (isShutdown ? "shutdown" : "restart");

            if (Confirmation.IsNo(confirmed))
            {
                ConfirmationGate.Clear();
                return Task.FromResult(ToolResult
                    .Speak($"Ok. NOT {gerund.ToLower()}")
                    .With("status", "cancelled")
                    .With("action", action));
            }

            // Everything that is not an armed, aged "yes" only ever asks. Note this
            // deliberately covers confirmed == "yes" on a first call: asserting the
            // answer is not a way to skip the question.
            if (!Confirmation.IsYes(confirmed) || !ConfirmationGate.TryConsume(subject))
            {
                ConfirmationGate.Arm(subject);
                string question = $"Are you sure you want to {verb}?";
                return Task.FromResult(ToolResult
                    .Speak(question)
                    .With("status", "needs_confirmation")
                    .With("question", question)
                    .With("action", action));
            }

            Process.Start("shutdown", isShutdown ? "/s /t 0" : "/r /t 0");
            return Task.FromResult(ToolResult
                .Speak($"Ok. {gerund}")
                .With("status", "confirmed")
                .With("action", action));
        }

        // contact and confirmed are validated by the dispatcher; message is
        // re-checked here regardless, because the empty-SMS incident happened on
        // exactly this path and a schema is not a guarantee about what arrives.
        private static async Task<ToolResult> HandleSendSmsAsync(
            CommandContext ctx,
            string contact,
            string message,
            string confirmed)
        {
            message = (message ?? string.Empty).Trim();
            if (message.Length == 0)
            {
                return ToolResult.Failed(
                    $"I don't have anything to send yet — what should I say to {contact}?",
                    "empty_message");
            }

            if (ctx.Contacts == null ||
                !ctx.Contacts.TryGetValue(contact, out string number) ||
                string.IsNullOrWhiteSpace(number))
            {
                return ToolResult.Failed($"I don't have a number for {contact}.", "unknown_contact");
            }

            // The subject binds the approval to this contact AND this body, so a
            // "yes" cannot be spent on a message the user never heard read back.
            //
            // Normalised, because the model re-types the body on the confirming
            // call and does not reproduce it byte for byte — the first real run of
            // this dropped a trailing full stop between the two calls, the subject
            // missed, and the gate silently re-asked while the model announced it
            // had sent the message. Case, whitespace and trailing punctuation are
            // not meaningful differences; different WORDS still miss, which is the
            // property actually worth protecting.
            string subject = "send_sms:" + contact.Trim().ToLowerInvariant() + ":" + NormalizeForConsent(message);

            if (Confirmation.IsNo(confirmed))
            {
                ConfirmationGate.Clear();
                return ToolResult
                    .Speak("Okay, message cancelled.")
                    .With("status", "cancelled")
                    .With("contact", contact);
            }

            if (!Confirmation.IsYes(confirmed) || !ConfirmationGate.TryConsume(subject))
            {
                // A `yes` that lands here matched no armed request — the body
                // changed, it expired, or the model issued it unprompted. Say so
                // out loud: this case previously looked identical to a first ask,
                // and the model responded by announcing it had sent the message.
                if (Confirmation.IsYes(confirmed))
                {
                    Console.WriteLine(
                        $"[confirm] send_sms 'yes' matched no armed request -> NOT sent. " +
                        $"contact={contact} message=\"{message}\"");
                }

                ConfirmationGate.Arm(subject);
                string question = $"You'd like to send \"{message}\" to {contact}. Should I send it?";
                return ToolResult
                    .Speak(question)
                    .With("status", "needs_confirmation")
                    .With("sent", "false")
                    .With("instruction",
                        "NOT sent. Ask the question verbatim and wait for the user's answer. " +
                        "Do not tell the user the message has been sent.")
                    .With("question", question)
                    .With("contact", contact)
                    .With("message", message);
            }

            bool sent = await ctx.Sms.SendSMS(contact, number, message);
            if (!sent)
            {
                return ToolResult.Failed(
                    $"Sorry, I couldn't send that to {contact}.", "send_failed");
            }

            return ToolResult
                .Speak($"Sending \"{message}\" to {contact}.")
                .With("status", "sent")
                .With("contact", contact)
                .With("message", message);
        }

        // Collapses the differences a model introduces when it repeats a body back
        // — case, run-together whitespace, and trailing punctuation — while leaving
        // the words themselves intact. Consent is bound to what the user HEARD,
        // and they cannot hear a full stop.
        private static string NormalizeForConsent(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            var sb = new StringBuilder(text.Length);
            bool lastWasSpace = false;
            foreach (char c in text.Trim())
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace) sb.Append(' ');
                    lastWasSpace = true;
                }
                else
                {
                    sb.Append(char.ToLowerInvariant(c));
                    lastWasSpace = false;
                }
            }

            return sb.ToString().TrimEnd('.', '!', '?', ',', ';', ':', ' ');
        }

        // Pulls the message body out of "text mom I'll be late", for the keyword
        // fallback path. Everything after the contact's name is the body; if that
        // leaves nothing, the caller omits `message` and validation refuses the
        // call rather than sending a blank text.
        private static string ExtractSmsBody(string text, string contactName)
        {
            int at = text.IndexOf(contactName, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;

            string rest = text.Substring(at + contactName.Length)
                              .TrimStart(' ', ',', ':', '-')
                              .Trim();

            // Strip a lead-in the user may have said before the body itself
            // ("text mom saying I'll be late" / "... that I'll be late").
            string[] leadIns = { "saying ", "that ", "and say ", "say " };
            foreach (string leadIn in leadIns)
            {
                if (rest.StartsWith(leadIn, StringComparison.OrdinalIgnoreCase))
                {
                    rest = rest.Substring(leadIn.Length).Trim();
                    break;
                }
            }

            return rest.Length == 0 ? null : rest;
        }
    }
}
