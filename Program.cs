using Personal_Assistant.AppLaunching;
using Personal_Assistant.Arduino;
using Personal_Assistant.Bakeoff;
using Personal_Assistant.AudioControl;
using Personal_Assistant.Diagnostics;
using Personal_Assistant.Dispatch;
using Personal_Assistant.Events;
using Personal_Assistant.Geolocator;
using Personal_Assistant.LightAutomator;
using Personal_Assistant.LLMClient;
using Personal_Assistant.MediaControl;
using Personal_Assistant.PlaystationController;
using Personal_Assistant.PrayerTimesCalculator;
using Personal_Assistant.ProcessControl;
using Personal_Assistant.Power;
using Personal_Assistant.Presence;
using Personal_Assistant.Suggestions;
using Personal_Assistant.Triggers;
using Personal_Assistant.Configuration;
using Personal_Assistant.Reminders;
using Personal_Assistant.Resume;
using Personal_Assistant.ScreenCapture;
using Personal_Assistant.SearxNGClient;
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
using System.Text;
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
            if (string.IsNullOrEmpty(weatherAPIKey))
            {
                Console.WriteLine("Error: Please set the following environment variables before running the program:");
                Console.WriteLine("  - WEATHERAPI_KEY: Your OpenWeatherMap API Key");
                Console.WriteLine("You can set it using: setx WEATHERAPI_KEY your_key");
                Console.WriteLine();
                Console.WriteLine("Optional overrides (defaults used if unset):");
                Console.WriteLine($"  LMSTUDIO_URL  (default {LocalLLMService.lmStudioUrl})");
                Console.WriteLine($"  SEARXNG_URL   (default {SearxNGService.searxNGUrl})");
                Console.WriteLine("  STT_URL       (default http://127.0.0.1:8001 - the Parakeet service)");
                Console.WriteLine("  KOKORO_URL    (default http://localhost:8880)");
                Console.WriteLine("  KOKORO_VOICE  (default am_onyx)");
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

        public static async Task Main(string[] args)
        {
            // Score whichever model LM Studio has loaded against the real tool
            // catalogue, then exit. Deliberately the FIRST thing in Main: the
            // harness needs no microphone, no Python, no Kokoro and no audio
            // device, and standing all of that up costs about a minute per
            // candidate in a sweep that loads a dozen models.
            if (BakeoffHarness.IsRequested(args))
            {
                await BakeoffHarness.RunAsync(BuildBakeoffRegistry(), args);
                return;
            }

            CheckEnvironmentVariables();

            // 49 (ASCII art)
            Console.WriteLine("                                    \r\n     ,AM  .d*\"*bg.\r\n    AVMM 6MP    Mb\r\n  ,W' MM YMb    MM\r\n,W'   MM  `MbmmdM9\r\nAmmmmmMMmm     .M'\r\n      MM     .d9  \r\n      MM   m\"'    \n\n");

            Runtime.PythonDLL = @"C:\Users\layth\AppData\Local\Programs\Python\Python312\python312.dll";
            PythonEngine.Initialize();
            PythonEngine.BeginAllowThreads();

            // Python modules ship next to the exe, so each deployed copy imports its
            // own. This used to point at a single shared folder, which meant a .py
            // deployed while working on one branch silently rebound the other
            // branch's imports. The shared folder stays on the path *after* the app
            // directory purely as a fallback, so an output dir missing a module
            // still starts.
            using (Py.GIL())
            {
                dynamic sys = Py.Import("sys");
                sys.path.append(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'));

                string extra = Environment.GetEnvironmentVariable("LAITH_PYTHON_MODULE_PATH");
                if (string.IsNullOrWhiteSpace(extra)) extra = @"C:\Users\layth\LAITH\local";
                sys.path.append(extra.TrimEnd('\\'));
            }

            // Tracks per-turn stt/llm/tts latency so we can see what's actually
            // the bottleneck. Reset before each recognition attempt, printed
            // after the turn's dispatch completes.
            var latency = new LatencyTracker();

            // Single-instance services. Kokoro / Whisper clients each reuse a
            // single HttpClient so creating SpeechService once keeps requests warm.
            var speechManager = new SpeechService(latency);

            // Mic first, warm-up second — deliberately in this order. The audio
            // warm-up plays real audio out of the real output device, so with
            // the mic already open it doubles as the echo gate's calibration
            // pass: the listener measures the quiet room, then hears the bleed,
            // and decides whether speakers are in play before the user has said
            // anything. Without this the FIRST reply of every session is
            // ungated and interrupts itself, because `auto` mode has to observe
            // bleed once before it can gate any.
            speechManager.StartListening();

            // Warm both cold starts at once, before the first wakeword can fire:
            // the audio device + Kokoro (so the greeting isn't clipped), and LM
            // Studio, whose first completion after a model load is much slower
            // than the rest. They're independent services, so serialising them
            // would only make launch slower. Both are best-effort.
            await Task.WhenAll(
                speechManager.WarmUpAudioAsync(),
                LocalLLMService.WarmUpAsync());

            var contacts = LoadContacts();

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
            // How long the assistant was off, read BEFORE the heartbeat starts.
            // Start() overwrites the single value this reads, so reversing these
            // two lines reports "no downtime" for every restart, forever, and
            // every paused timer silently degrades to wall-clock.
            DateTime? lastSeen = Downtime.ReadLastSeen();
            TimeSpan downtime = Downtime.GapSince(lastSeen);
            var heartbeat = new Downtime();
            heartbeat.Start();
            AppDomain.CurrentDomain.ProcessExit += (s, e) => heartbeat.Dispose();
            Console.WriteLine(lastSeen.HasValue
                ? $"[resume] last seen {lastSeen:ddd HH:mm} — off for {Downtime.Describe(downtime)}"
                : "[resume] no heartbeat from a previous run");

            // Set below, once the trigger engine and the suggestion service it
            // needs exist. Captured by reference for the same reason dispatcherRef
            // is: reminders are constructed first, but an anchored one coming due
            // has to reach the watcher, and nothing can come due before Restore.
            EventWatchService watcherRef = null;

            var reminders = new ReminderService(
                message => speechManager.Say("⏰", message),
                timerWidgets,
                // persist:true only here, like ConversationMemory. Every harness
                // gets the default false so it cannot write over real timers.
                persist: true,
                // A timer tied to a real event hands itself to the watcher
                // instead of announcing. Null-safe because the watcher is
                // assigned below and nothing can fire before Restore.
                onEventDue: item =>
                {
                    watcherRef?.Begin(item.Subject, item.Label, item.FireAt);
                    return Task.CompletedTask;
                });

            // Everything LAITH does without being asked. The gate decides whether
            // an unprompted announcement is welcome (is anyone here? is it the
            // middle of the night? are they mid-conversation?); the trigger
            // service decides when one is due. Both run off their own tickers and
            // neither touches the wake-word loop below.
            //
            // Declared up here rather than beside the loop because the gate closes
            // over it: an announcement fired while a conversation is open talks
            // over the reply the user is waiting for, and lands in the microphone
            // still listening for their next turn.
            bool conversationOpen = false;

            var presence = new PresenceGate(isBusy: () => conversationOpen);
            var triggers = new TriggerService(presence);

            PrayerAnnouncer prayerAnnouncer = null;
            if (LaithConfig.Bool("PrayerAnnouncements", true))
            {
                // 🕌 as the bubble's "you said" label, the way a fired reminder
                // uses ⏰: there is no user utterance behind this one either.
                // Straight through Say — this branch synthesises with Kokoro on
                // demand and has no pre-rendered clip cache to keep in voice.
                prayerAnnouncer = new PrayerAnnouncer(
                    triggers,
                    location,
                    message => speechManager.Say("🕌", message));

                // Not awaited: planning the day needs a location lookup over the
                // network, and startup must not sit behind it.
                _ = prayerAnnouncer.StartAsync();
            }

            // The same construction cycle main has: rules and suggestions need the
            // dispatcher to run tools, the dispatcher needs the registry, the
            // registry needs the context, and the context needs both of these.
            // Broken by capturing these by reference and assigning them below.
            IntentDispatcher dispatcherRef = null;
            ToolRegistry registryRef = null;
            ConversationMemory conversationMemoryRef = null;

            var suggestions = new SuggestionService(
                triggers,
                message => speechManager.Say("💡", message),
                // Recorded as a model turn so the dispatcher hands it to the model
                // as history — which is how "yeah, go on" resolves to the offer.
                // Main needs a system-instruction hint for this because its Live
                // sessions start with no history at all; here the turn-based path
                // already carries it.
                remember: offer => conversationMemoryRef?.AddModel(offer));

            // Things the user is waiting on that the world decides rather than
            // the clock. 🔔 as the bubble label, alongside ⏰ / 🕌 / 📌 / 💡.
            //
            // Given `suggestions` so a confirmed event becomes an OFFER ("it's
            // out — want me to open it?") rather than a browser window appearing
            // on its own. Nothing here opens a link without a spoken yes, and the
            // link itself always came from SearxNG rather than from the model.
            var watcher = new EventWatchService(
                triggers,
                message => speechManager.Say("🔔", message),
                suggestions,
                persist: true);
            watcherRef = watcher;

            var voiceTriggers = new VoiceTriggers(
                triggers,
                processes,
                message => speechManager.Say("📌", message),
                runTool: (name, toolArgs) => dispatcherRef.RunToolByNameAsync(name, toolArgs),
                isKnownTool: name => registryRef?.FindByName(name) != null);

            // Shared dependencies handed to every command handler.
            var context = new CommandContext
            {
                Speech = speechManager,
                VoiceTriggers = voiceTriggers,
                Suggestions = suggestions,
                Watches = watcher,
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

            // persist:true only here. Every other ConversationMemory in the
            // codebase is one a harness constructed to satisfy a parameter, and
            // those must not write over the real conversation.
            var conversationMemory = new ConversationMemory(persist: true);
            conversationMemory.Restore();
            var dispatcher = new IntentDispatcher(
                registry,
                context,
                LocalLLMService.DetectToolAsync,
                LocalLLMService.GenerateResponse,
                conversationMemory,
                latency,
                // Streamed replies: speech starts after the first sentence rather
                // than after the whole answer. GenerateResponse above stays as the
                // non-streamed fallback.
                LocalLLMService.StreamResponse);

            // Let the `repeat` tool run other tools by name (validated). A lambda
            // rather than the method group: RunToolByNameAsync's `speak` is
            // optional, and optional parameters don't survive a delegate
            // conversion.
            context.RunTool = (name, toolArgs) => dispatcher.RunToolByNameAsync(name, toolArgs, speak: true);

            // Close the cycle above, then arm whatever was set before the last
            // restart. Restore must come after BOTH assignments: a rule with a
            // run_tool fires through dispatcherRef, and a rule due the moment it is
            // armed would otherwise hit a null on the trigger ticker — a background
            // thread, i.e. the failure that takes the process down silently.
            dispatcherRef = dispatcher;
            registryRef = registry;
            conversationMemoryRef = conversationMemory;

            // Everything that was pending when the assistant last stopped, picked
            // back up in one pass.
            //
            // Each service reports what it found rather than announcing it, so
            // the whole restart produces ONE sentence instead of four services
            // talking over each other at the moment the desktop finishes loading.
            var resumed = new ResumeSummary();
            resumed.Absorb(voiceTriggers.Restore(lastSeen));
            resumed.Absorb(watcher.Restore());
            // Reminders last of the three: an anchored one whose moment passed
            // during downtime hands itself to the watcher, which must already
            // have loaded its own store or the new watch is written into a list
            // that Restore is about to clear.
            resumed.Absorb(reminders.Restore(lastSeen));
            watcher.Start();

            Console.WriteLine($"[resume] {resumed.LogLine()}");

            // Said through the trigger engine rather than straight out, so it
            // gets the presence gate and quiet hours like every other unprompted
            // line. Booting at 3am for a reboot and walking away should not mean
            // the catch-up is spoken to an empty room and then lost — a generous
            // grace keeps it waiting until somebody is actually there.
            string catchUp = resumed.SpokenLine();
            if (!string.IsNullOrWhiteSpace(catchUp))
            {
                triggers.AddOneShot(
                    "resume:report",
                    // Far enough after the startup greeting that the two don't
                    // land on top of each other.
                    DateTime.Now.AddSeconds(20),
                    () => speechManager.Say("↩️", catchUp),
                    grace: TimeSpan.FromHours(8));
            }

            // Suggestions last, for the same reason: every accept action runs a
            // tool through the dispatcher, and Start() arms an evaluator that
            // could fire within the minute.
            SuggestionCatalogue.Register(
                suggestions,
                context,
                prayerAnnouncer,
                PresenceGate.IdleTime,
                (name, toolArgs) => dispatcher.RunToolByNameAsync(name, toolArgs));
            suggestions.Start();

            // A conversation stays open after the wakeword so follow-ups don't
            // need re-waking; it closes when the user goes quiet this long.
            var followUpWindow = TimeSpan.FromSeconds(12);

            while (true)
            {
                if (!conversationOpen)
                {
                    bool woke = await speechManager.KeywordRecognizer();
                    Console.WriteLine($"[loop] KeywordRecognizer returned {woke} at {DateTime.Now:HH:mm:ss.fff}");
                    // Only greet + listen when the wakeword actually fired. On an
                    // errored/early return, loop back and keep waiting instead of
                    // spuriously greeting (which previously ran away in a loop).
                    if (!woke) continue;

                    // Arm BEFORE the greeting: anything said over it is captured
                    // and answered rather than lost, even though the greeting
                    // itself is too short to be worth cutting.
                    speechManager.Listener.Arm();
                    await speechManager.Say("Hey 49", PickGreeting(DateTime.Now.Hour));
                    conversationOpen = true;
                }

                // Fresh latency counters for this turn — ListenForTurnAsync
                // records STT (understanding-only) internally.
                latency.Reset();

                // Silence here means the user is done talking to us, not that we
                // misheard, so this deliberately doesn't re-prompt.
                recognizedText = await speechManager.ListenForTurnAsync(followUpWindow);

                if (string.IsNullOrEmpty(recognizedText))
                {
                    Console.WriteLine("[loop] no follow-up — closing the conversation");
                    speechManager.Listener.Disarm();
                    conversationOpen = false;
                    continue;
                }

                // A barge-in needs no special handling any more: the utterance the
                // user interrupted with is already recorded and queued, so the next
                // pass through this loop picks it straight up.
                await dispatcher.DispatchAsync(recognizedText);
                Console.WriteLine(latency.Summary());
            }
        }

        // The catalogue the bake-off scores against: the SHIPPING one, not a
        // convenient subset.
        //
        // Three tool groups only register when their service is non-null
        // (set_trigger/list_triggers/cancel_trigger behind VoiceTriggers,
        // accept_suggestion/adjust_suggestions behind Suggestions, send_sms behind
        // Contacts). A harness that leaves any of them out still runs, still
        // prints a score, and is quietly answering a different question — an
        // easier one, since the hardest schema in the app is the one that goes
        // missing. Hence the assertion at the bottom.
        //
        // Nothing here is ever executed: the harness scores what the model CHOSE
        // and drops the decision. The services exist so the schemas exist.
        private static ToolRegistry BuildBakeoffRegistry()
        {
            // Belt and braces. Nothing below calls Restore() or writes the store,
            // but a scratch path means a mistake here can't rewrite the user's
            // real standing rules in %APPDATA%\LAITH\triggers.json.
            Environment.SetEnvironmentVariable("LAITH_TRIGGERS_PATH",
                Path.Combine(Path.GetTempPath(), "laith-bakeoff-triggers.json"));

            // idleSource is dictated rather than read: the real one would make the
            // catalogue depend on how long ago somebody touched the mouse.
            var presence = new PresenceGate(idleSource: () => TimeSpan.Zero);
            var triggers = new TriggerService(presence);

            var voiceTriggers = new VoiceTriggers(
                triggers,
                new ProcessController(),
                _ => Task.CompletedTask,
                (name, toolArgs) => Task.CompletedTask,
                isKnownTool: _ => true,
                idle: () => TimeSpan.Zero);

            var contacts = LoadContacts();

            var context = new CommandContext
            {
                VoiceTriggers = voiceTriggers,
                Suggestions = new SuggestionService(triggers, _ => Task.CompletedTask),
                Processes = new ProcessController(),
                Battery = new BatteryReader(),
                // send_sms is part of the shipped catalogue on this machine
                // (CONTACTS_PATH resolves), and its contact enum is one more thing
                // competing for an utterance. A stub keeps the tool count honest
                // if the file is ever missing; the case only asserts `contact:any`.
                Contacts = contacts != null && contacts.Count > 0
                    ? contacts
                    : new Dictionary<string, string> { ["mum"] = "+10000000000" }
            };

            var registry = BuildRegistry(context);

            // Fail loudly rather than score a smaller catalogue. Every name here
            // is conditionally registered, so a null service silently removes it.
            string[] mustExist =
            {
                "set_trigger", "list_triggers", "cancel_trigger",
                "accept_suggestion", "adjust_suggestions",
                "get_battery", "send_sms"
            };
            var missing = mustExist.Where(n => registry.FindByName(n) == null).ToList();
            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    "bake-off registry is incomplete — missing " + string.Join(", ", missing) +
                    ". Scoring this would answer a different question than the app asks.");
            }

            return registry;
        }

        // Builds the command catalogue. Each VoiceCommand carries its LLM tool
        // schema (for Gemini dispatch) plus a keyword predicate + arg extractor
        // (for the fallback path). Registration order == the original if/else
        // order, so "first keyword match wins" is preserved on fallback.
        // Public so a harness can drive the REAL tool catalogue. A harness that
        // builds its own stub registry is only ever exercising its own fakes — on
        // main that produced a test which reported a working tool round trip while
        // the shipping handlers were still returning nothing.
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
                    // Still speaks for itself, and has to: it never returns.
                    // Environment.Exit runs before any caller could voice a
                    // result, and awaiting the goodbye here is what keeps it from
                    // being cut off mid-word by the process ending. main leaves
                    // this one speaking for the same reason.
                    await ctx.Speech.Say(ctx.RecognizedText, "Alright goodbye!");
                    // Close the mic before tearing the interpreter down — the
                    // listener calls into Python on every audio frame.
                    ctx.Speech.StopListening();
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
                    // The 24-hour value and the zone go along for the ride so a
                    // model relaying this states the user's ACTUAL local time.
                    // Given only a sentence it will happily re-derive one and get
                    // it wrong — on main this tool announced "7:20 AM UTC" for
                    // 2:20 AM, from a handler that had computed 2:20 correctly.
                    return Task.FromResult(ToolResult.Speak($"It's {now.ToLocalTime():t}")
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
                ToolDefinition.Create("web_search",
                    "Search the web and SPEAK the answer for anything needing current or " +
                    "external information you don't already know — news, prices, sports scores, " +
                    "recent events, weather in another city, or facts you're unsure about. Unlike " +
                    "google_search (which just opens a browser tab), this reads results and answers " +
                    "out loud. Only use this when the request actually needs a search — don't use it " +
                    "for things another tool already handles, or for plain conversation.",
                    new ToolParameter("query", "string", "What to search for.")),
                lower => false, // LLM-only; no sensible keyword fallback for "look this up"
                async (ctx, args) =>
                {
                    string query = args["query"];
                    List<SearchHit> hits;
                    try { hits = await SearxNGService.SearchAsync(query); }
                    catch (Exception ex)
                    {
                        Console.WriteLine("web_search: SearxNG search failed: " + ex.Message);
                        hits = new List<SearchHit>();
                    }
                    // No conversation history here — handlers don't have access to it, and a
                    // search-and-answer is naturally a one-off lookup anyway.
                    // Streamed: grounded answers are the longest replies the
                    // assistant gives, so they benefit most from early first audio.
                    string answer = await ctx.Speech.SayStreaming(ctx.RecognizedText,
                        (onSentence, ct) => LocalLLMService.StreamWithSearchResults(
                            ctx.RecognizedText, hits, null, onSentence, ct));

                    // The one tool that still speaks for itself, and has to: the
                    // point of this path is that sentences reach the speaker as the
                    // model produces them, which a single finished Speech string
                    // cannot express. So the answer comes back as DATA with no
                    // Speech — putting it in Speech would say the whole thing a
                    // second time.
                    return ToolResult.None
                        .With("query", query)
                        .With("answer", answer ?? string.Empty)
                        .With("results_used", hits.Count.ToString())
                        .With("instruction",
                            "This answer has ALREADY been spoken to the user. Do not repeat it.");
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
                ToolDefinition.Create("get_battery",
                    "How much battery is left. Answers in TIME remaining where Windows will " +
                    "say — that's what the user actually wants to know — and falls back to a " +
                    "percentage when it won't."),
                lower => lower.Contains("battery") ||
                         (lower.Contains("how long") && lower.Contains("charge")),
                (ctx, args) =>
                {
                    BatteryInfo info = ctx.Battery.Read();
                    ToolResult result = ToolResult.Speak(info.Spoken())
                        .With("has_battery", info.HasBattery ? "yes" : "no")
                        .With("on_mains", info.OnMains ? "yes" : "no")
                        .With("percent", info.Percent.ToString());

                    // Absent rather than zero when Windows won't estimate: a "0"
                    // here is exactly the sort of thing a model would read out as
                    // "no time left".
                    if (info.Remaining.HasValue)
                    {
                        result = result.With(
                            "minutes_remaining",
                            ((int)info.Remaining.Value.TotalMinutes).ToString());
                    }
                    return Task.FromResult(result);
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
                        // something that isn't there, a model can offer what is.
                        return Task.FromResult(ToolResult
                            .Speak($"I couldn't find an output device matching {args["device"]}. Available devices are: {list}.")
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
                        // That is wrong ("unpause" while playing pauses it) and
                        // main fixed it, but the fix is its own change; this one
                        // is only about where the answer comes from.
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
                        Required: false),
                    new ToolParameter("event_subject", "string",
                        "ONLY when the countdown is until a real-world event that either happens or " +
                        "doesn't — a game or episode release, a match kickoff, a launch. Give a short " +
                        "searchable description of the event itself, e.g. 'Re:Zero Season 4 Recapture " +
                        "Arc episode release'. Setting this means the countdown is treated as a " +
                        "DEADLINE that survives a shutdown, and when it runs out I search the web to " +
                        "check whether the event actually happened instead of just announcing it. " +
                        "Omit for ordinary timers ('remind me in 10 minutes'), which are paused while " +
                        "the PC is off.",
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
                    string subject = args.TryGetValue("event_subject", out string sub) ? sub : null;
                    ctx.Reminders.AddTimer(secs, label, subject);

                    string what = string.IsNullOrWhiteSpace(label) ? "" : $" to {label}";
                    // Said differently on purpose: an event timer promises to go
                    // and CHECK, and the user needs to know that is what they got
                    // — it is the difference between being told a guess expired
                    // and being told the thing happened.
                    string speech = string.IsNullOrWhiteSpace(subject)
                        ? $"Okay, I'll remind you{what} in {DescribeDuration(secs)}."
                        : $"Okay — in {DescribeDuration(secs)} I'll check whether {subject} has " +
                          "happened and let you know either way.";

                    return Task.FromResult(ToolResult
                        .Speak(speech)
                        .With("duration_seconds", secs.ToString())
                        .With("label", label ?? string.Empty)
                        .With("event_subject", subject ?? string.Empty)
                        .With("survives_restart", "true")
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
                            $"Sorry, I didn't catch what time you meant.",
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

                    // Watches are listed alongside, because from the user's side
                    // "the Re:Zero thing" is one of their pending items — the
                    // fact that it graduated from a countdown into something
                    // being checked is an implementation detail they never asked
                    // about, and hiding it makes it look like it was forgotten.
                    var watching = ctx.Watches?.Snapshot()
                        ?? (IReadOnlyList<EventWatch>)new List<EventWatch>();

                    if (pending.Count == 0 && watching.Count == 0)
                    {
                        return Task.FromResult(
                            ToolResult.Speak("You have no timers or alarms set.").With("count", "0"));
                    }

                    if (pending.Count == 0)
                    {
                        string subjects = string.Join(", ", watching.Select(w => w.Describe()));
                        return Task.FromResult(ToolResult
                            .Speak("No timers, but I'm still checking on " + subjects + ".")
                            .With("count", "0")
                            .With("watching_count", watching.Count.ToString())
                            .With("watching", subjects));
                    }

                    var sb = new System.Text.StringBuilder();
                    sb.Append($"You have {pending.Count} {(pending.Count == 1 ? "reminder" : "reminders")}: ");
                    ToolResult data = ToolResult.None.With("count", pending.Count.ToString());
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
                        data = data
                            .With($"reminder_{i + 1}_at", p.FireAt.ToString("yyyy-MM-dd HH:mm"))
                            .With($"reminder_{i + 1}_label", p.Label ?? string.Empty);
                    }
                    if (watching.Count > 0)
                    {
                        string subjects = string.Join(", ", watching.Select(w => w.Describe()));
                        sb.Append(" I'm also still checking on ");
                        sb.Append(subjects);
                        sb.Append(".");
                        data = data
                            .With("watching_count", watching.Count.ToString())
                            .With("watching", subjects);
                    }

                    ToolResult spoken = ToolResult.Speak(sb.ToString());
                    foreach (KeyValuePair<string, string> kv in data.Data) spoken = spoken.With(kv.Key, kv.Value);
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
                    // Watches go too. "Cancel my reminders" means stop bothering
                    // me about this stuff, and leaving something that still
                    // speaks unprompted hours later would read as the cancel
                    // having failed.
                    int w = ctx.Watches?.CancelAll() ?? 0;
                    int total = n + w;
                    return Task.FromResult(ToolResult
                        .Speak(total == 0
                            ? "There was nothing to cancel."
                            : $"Cancelled {total} {(total == 1 ? "reminder" : "reminders")}.")
                        .With("cancelled_count", n.ToString())
                        .With("cancelled_watches", w.ToString()));
                }));

            // Answering "yes" to something the assistant offered on its own.
            //
            // Unlike main, no system-instruction hint is needed: this branch's
            // dispatcher hands ConversationMemory to the model as history, and the
            // offer was recorded there when it was spoken, so "yeah, go on" has
            // something to refer to already.
            if (context.Suggestions != null)
            {
                var suggestions = context.Suggestions;

                registry.Add(new VoiceCommand(
                    ToolDefinition.Create("accept_suggestion",
                        "Call this ONLY when the user agrees to something you offered them " +
                        "unprompted — \"yes\", \"go on\", \"do it\", \"sure\". Never call it for " +
                        "an ordinary request; use the tool that does the thing instead. If they " +
                        "decline or change the subject, do not call it at all."),
                    lower => false, // no keyword path — "yes" alone is far too broad
                    async (ctx, args) =>
                    {
                        string said = await suggestions.AcceptPendingAsync();
                        if (said == null)
                        {
                            // Nothing pending. Saying so beats silently doing
                            // nothing, which reads as the assistant ignoring you.
                            return ToolResult.Failed(
                                "Sorry, I'm not sure what you're saying yes to.",
                                "no live suggestion pending");
                        }
                        return ToolResult.Speak(said).With("accepted", "yes");
                    }));

                registry.Add(new VoiceCommand(
                    ToolDefinition.Create("adjust_suggestions",
                        "Change how often the assistant volunteers things on its own — use this " +
                        "when the user says it's being annoying, too quiet, or to stop " +
                        "suggesting things. Takes effect immediately and lasts until the app " +
                        "restarts; the Suggestions setting in the config decides the level it " +
                        "starts at.",
                        new ToolParameter("level", "string",
                            "off = never volunteer anything. rare = a few times a day at most. " +
                            "normal = the default. chatty = surface things readily.",
                            AllowedValues: new List<string> { "off", "rare", "normal", "chatty" })),
                    lower => lower.Contains("stop suggesting") ||
                             lower.Contains("suggest less") || lower.Contains("suggest more"),
                    (ctx, args) =>
                    {
                        args.TryGetValue("level", out string level);
                        if (!Enum.TryParse(level, ignoreCase: true, out SuggestionLevel parsed))
                        {
                            return Task.FromResult(ToolResult.Failed(
                                "I'm not sure how often you want me to speak up.",
                                $"unknown suggestion level '{level}'"));
                        }

                        SuggestionBudget.SetLevelForThisRun(parsed);

                        // A pending offer belongs to the old setting. Being told to
                        // shut up and then acting on the thing you were told to
                        // shut up about is the wrong way round.
                        if (parsed == SuggestionLevel.Off) suggestions.DeclinePending();

                        string said;
                        switch (parsed)
                        {
                            case SuggestionLevel.Off: said = "Alright, I'll keep quiet."; break;
                            case SuggestionLevel.Rare: said = "Okay, I'll only mention the big things."; break;
                            case SuggestionLevel.Chatty: said = "Okay, I'll speak up more often."; break;
                            default: said = "Okay, back to normal."; break;
                        }
                        return Task.FromResult(ToolResult
                            .Speak(said)
                            .With("level", parsed.ToString().ToLowerInvariant())
                            .With("pacing", suggestions.Budget.Describe()));
                    }));
            }

            // --- Standing rules (LLM-only; the phrasing is too open for keywords) ----

            if (context.VoiceTriggers != null)
            {
                var voiceTriggers = context.VoiceTriggers;

                registry.Add(new VoiceCommand(
                    ToolDefinition.Create("set_trigger",
                        "Create a STANDING RULE that keeps working in the background — " +
                        "'tell me when Discord closes', 'every weekday at 8 turn on the bedroom " +
                        "light', 'remind me to stretch every 30 minutes until 6'. Use this only " +
                        "for an ongoing rule; a one-off countdown is set_timer and a single " +
                        "clock-time reminder is set_alarm. Give `message`, or `run_tool`, or both.",
                        new ToolParameter("condition", "string",
                            "What makes it fire. at_time = a clock time. every = a repeating " +
                            "interval. app_starts / app_stops = a program opening or closing. " +
                            "file_appears = a download or file finishes arriving in a folder. " +
                            "idle_for = the user has been away a while (use this for DOING " +
                            "something while they're out, like turning lights off — they won't " +
                            "hear a message). on_return = they come back after being away. " +
                            "battery_below = running on battery and low.",
                            AllowedValues: new List<string>
                            {
                                "at_time", "every", "app_starts", "app_stops",
                                "file_appears", "idle_for", "on_return", "battery_below"
                            }),
                        new ToolParameter("message", "string",
                            "The announcement to speak AT THE MOMENT the rule fires, later — not " +
                            "a confirmation that you have set it up. For 'tell me when Discord " +
                            "closes' this is \"Discord has closed\", never \"I'll tell you when " +
                            "Discord closes\". Omit only if run_tool is self-explanatory.",
                            Required: false),
                        new ToolParameter("time", "string",
                            "For at_time: 24-hour HH:mm, e.g. 08:00 or 17:30.",
                            Required: false),
                        new ToolParameter("repeat", "string",
                            "For at_time: how often it comes round. Defaults to once.",
                            Required: false,
                            AllowedValues: new List<string> { "once", "daily", "weekdays", "weekends" }),
                        new ToolParameter("interval_minutes", "integer",
                            "For every: how many minutes between runs, 1 to 1440. For idle_for " +
                            "and on_return: how many minutes away counts as away.",
                            Required: false),
                        new ToolParameter("until", "string",
                            "For every: stop for the day after this 24-hour HH:mm time.",
                            Required: false),
                        new ToolParameter("app", "string",
                            "For app_starts / app_stops: the program name, e.g. Discord or chrome.",
                            Required: false),
                        new ToolParameter("folder", "string",
                            "For file_appears: which folder to watch. Defaults to Downloads.",
                            Required: false),
                        new ToolParameter("pattern", "string",
                            "For file_appears: which files count, as a glob like *.pdf. " +
                            "Omit for any file.",
                            Required: false),
                        new ToolParameter("percent", "integer",
                            "For battery_below: the charge percentage to warn at, 1 to 100.",
                            Required: false),
                        new ToolParameter("minutes_left", "integer",
                            "For battery_below: warn when this many minutes of battery remain. " +
                            "Prefer this over percent when the user talks in time (\"when I'm " +
                            "down to half an hour\").",
                            Required: false),
                        // Deliberately does NOT say which tools are disallowed. It
                        // used to, and the model routed around the prohibition:
                        // "text my mum every morning" came back as a rule that SAYS
                        // "good morning" out loud, silently turning a request to
                        // message someone into a different feature.
                        new ToolParameter("run_tool", "string",
                            "OMIT THIS unless the user asked for an ACTION as well as being told. " +
                            "\"Tell me when X\" and \"let me know when X\" are notifications and " +
                            "need only a message — adding a tool to those is wrong and the rule " +
                            "will be rejected. Use it only for \"when X, DO Y\", and then name the " +
                            "tool the user actually asked for, e.g. control_lights, send_sms. " +
                            "Never substitute a different action from the one they asked for.",
                            Required: false),
                        new ToolParameter("run_tool_args", "string",
                            "Optional JSON object of arguments for run_tool, " +
                            "e.g. {\"state\":\"on\",\"room\":\"bedroom\"}.",
                            Required: false)),
                    lower => false, // no keyword path — see the note above
                    (ctx, args) => Task.FromResult(HandleSetTrigger(voiceTriggers, args))));

                registry.Add(new VoiceCommand(
                    ToolDefinition.Create("list_triggers",
                        "List the standing rules the user has set up (things that fire on their " +
                        "own, like 'when Discord closes'). Not timers or alarms — that's " +
                        "list_reminders."),
                    lower => false,
                    (ctx, args) =>
                    {
                        IReadOnlyList<TriggerSpec> rules = voiceTriggers.Snapshot();
                        if (rules.Count == 0)
                        {
                            return Task.FromResult(ToolResult
                                .Speak("You don't have any standing rules set up.")
                                .With("count", "0"));
                        }

                        var sb = new StringBuilder();
                        sb.Append($"You have {rules.Count} standing {(rules.Count == 1 ? "rule" : "rules")}: ");
                        ToolResult data = ToolResult.None.With("count", rules.Count.ToString());
                        for (int i = 0; i < rules.Count; i++)
                        {
                            // Numbered so "cancel the second one" has a referent,
                            // matching how list_reminders numbers its own.
                            sb.Append($"{i + 1}. {rules[i].Describe()}");
                            sb.Append(i < rules.Count - 1 ? "; " : ".");
                            data = data.With($"rule_{i + 1}", rules[i].Describe());
                        }

                        ToolResult spoken = ToolResult.Speak(sb.ToString());
                        foreach (KeyValuePair<string, string> kv in data.Data) spoken = spoken.With(kv.Key, kv.Value);
                        return Task.FromResult(spoken);
                    }));

                registry.Add(new VoiceCommand(
                    ToolDefinition.Create("cancel_trigger",
                        "Cancel a standing rule. Give `which` as the number from list_triggers, " +
                        "or \"all\".",
                        new ToolParameter("which", "string",
                            "The rule's number as listed, e.g. \"2\", or \"all\" for every rule.")),
                    lower => false,
                    (ctx, args) =>
                    {
                        args.TryGetValue("which", out string which);
                        which = (which ?? string.Empty).Trim();

                        if (string.Equals(which, "all", StringComparison.OrdinalIgnoreCase))
                        {
                            int n = voiceTriggers.CancelAll();
                            return Task.FromResult(ToolResult
                                .Speak(n == 0
                                    ? "There were no standing rules to cancel."
                                    : $"Cancelled {n} standing {(n == 1 ? "rule" : "rules")}.")
                                .With("cancelled_count", n.ToString()));
                        }

                        if (!int.TryParse(which, out int index))
                        {
                            return Task.FromResult(ToolResult.Failed(
                                "Which one did you mean?", $"could not read '{which}' as a rule number"));
                        }

                        TriggerSpec removed = voiceTriggers.CancelAt(index);
                        if (removed == null)
                        {
                            return Task.FromResult(ToolResult.Failed(
                                "I don't have a rule with that number.", $"no rule at position {index}"));
                        }
                        return Task.FromResult(ToolResult
                            .Speak($"Cancelled: {removed.Describe()}.")
                            .With("cancelled", removed.Describe()));
                    }));
            }

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

        // Turns set_trigger's arguments into a TriggerSpec and arms it.
        //
        // The schema can't express "time is required, but only when condition is
        // at_time", so that shape is enforced here and reported back in words a
        // model can act on — a rejection that names the missing parameter is a
        // retry, where a bare failure is a dead end.
        private static ToolResult HandleSetTrigger(
            VoiceTriggers voiceTriggers, IReadOnlyDictionary<string, string> args)
        {
            args.TryGetValue("condition", out string condition);
            if (!TryParseCondition(condition, out TriggerWhen when))
            {
                return ToolResult.Failed(
                    "I'm not sure when you want that to happen.",
                    $"unknown condition '{condition}'");
            }

            var spec = new TriggerSpec { When = when, Repeat = TriggerRepeat.Once };

            if (args.TryGetValue("message", out string message) && !string.IsNullOrWhiteSpace(message))
            {
                spec.Message = message.Trim();
            }
            if (args.TryGetValue("run_tool", out string runTool) && !string.IsNullOrWhiteSpace(runTool))
            {
                spec.RunTool = runTool.Trim();
            }
            if (args.TryGetValue("run_tool_args", out string toolArgs) && !string.IsNullOrWhiteSpace(toolArgs))
            {
                spec.RunToolArgs = ParseToolArgs(toolArgs);
            }

            switch (when)
            {
                case TriggerWhen.AtTime:
                    if (!args.TryGetValue("time", out string timeText) ||
                        !ReminderService.TryParseTimeOfDay(timeText, out TimeSpan tod))
                    {
                        return ToolResult
                            .Speak("What time should that be?")
                            .With("needs", "time");
                    }
                    spec.TimeOfDay = tod;
                    if (args.TryGetValue("repeat", out string repeat) &&
                        Enum.TryParse(repeat, ignoreCase: true, out TriggerRepeat parsedRepeat))
                    {
                        spec.Repeat = parsedRepeat;
                    }
                    break;

                case TriggerWhen.Every:
                    if (!args.TryGetValue("interval_minutes", out string every) ||
                        !int.TryParse(every, out int minutes))
                    {
                        return ToolResult
                            .Speak("How often should I do that?")
                            .With("needs", "interval_minutes");
                    }
                    spec.IntervalMinutes = minutes;
                    if (args.TryGetValue("until", out string until) &&
                        ReminderService.TryParseTimeOfDay(until, out TimeSpan untilTod))
                    {
                        spec.Until = untilTod;
                    }
                    break;

                case TriggerWhen.AppStarts:
                case TriggerWhen.AppStops:
                    if (!args.TryGetValue("app", out string app) || string.IsNullOrWhiteSpace(app))
                    {
                        return ToolResult
                            .Speak("Which app should I watch for?")
                            .With("needs", "app");
                    }
                    spec.App = app.Trim();
                    break;

                case TriggerWhen.FileAppears:
                    // Both optional: the default is "any file in Downloads",
                    // which is what "tell me when my download finishes" means.
                    if (args.TryGetValue("folder", out string folder) &&
                        !string.IsNullOrWhiteSpace(folder))
                    {
                        spec.Folder = folder.Trim();
                    }
                    if (args.TryGetValue("pattern", out string pattern) &&
                        !string.IsNullOrWhiteSpace(pattern))
                    {
                        spec.Pattern = pattern.Trim();
                    }
                    break;

                case TriggerWhen.IdleFor:
                case TriggerWhen.OnReturn:
                    if (!args.TryGetValue("interval_minutes", out string away) ||
                        !int.TryParse(away, out int awayMinutes))
                    {
                        return ToolResult
                            .Speak("How long away should I count as away?")
                            .With("needs", "interval_minutes");
                    }
                    spec.AwayMinutes = awayMinutes;
                    break;

                case TriggerWhen.BatteryBelow:
                    if (args.TryGetValue("percent", out string pct) && int.TryParse(pct, out int percent))
                    {
                        spec.Percent = percent;
                    }
                    if (args.TryGetValue("minutes_left", out string left) &&
                        int.TryParse(left, out int minutesLeft))
                    {
                        spec.MinutesLeft = minutesLeft;
                    }
                    if (spec.Percent == 0 && spec.MinutesLeft == 0)
                    {
                        return ToolResult
                            .Speak("At what battery level should I tell you?")
                            .With("needs", "percent or minutes_left");
                    }
                    break;
            }

            TriggerSpec added = voiceTriggers.Add(spec, out TriggerRejection rejected);
            if (added == null)
            {
                return ToolResult.Failed(rejected.Spoken, rejected.Reason);
            }

            // Read the whole rule back. It is a standing instruction that will act
            // on its own later, so "okay" is not enough — the user has to be able
            // to hear that it was understood the way they meant it.
            return ToolResult
                .Speak($"Right — I'll {added.Describe()}.")
                .With("rule", added.Describe())
                .With("condition", condition);
        }

        private static bool TryParseCondition(string raw, out TriggerWhen when)
        {
            switch ((raw ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "at_time": when = TriggerWhen.AtTime; return true;
                case "every": when = TriggerWhen.Every; return true;
                case "app_starts": when = TriggerWhen.AppStarts; return true;
                case "app_stops": when = TriggerWhen.AppStops; return true;
                case "file_appears": when = TriggerWhen.FileAppears; return true;
                case "idle_for": when = TriggerWhen.IdleFor; return true;
                case "on_return": when = TriggerWhen.OnReturn; return true;
                case "battery_below": when = TriggerWhen.BatteryBelow; return true;
                default: when = TriggerWhen.AtTime; return false;
            }
        }

        // set_trigger's run_tool_args is a JSON object, where `repeat`'s actions
        // are a JSON array — same defensive treatment, different shape, so it does
        // not go through ParseRepeatActions.
        private static Dictionary<string, string> ParseToolArgs(string json)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
                    foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                    {
                        result[p.Name] = p.Value.ValueKind == JsonValueKind.String
                            ? p.Value.GetString()
                            : p.Value.GetRawText();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[triggers] could not read run_tool_args: {ex.Message}");
            }
            return result;
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

        public static Dictionary<string, string> LoadContacts()
        {
            var contactsPath = Environment.GetEnvironmentVariable("CONTACTS_PATH");
            if (string.IsNullOrEmpty(contactsPath) || !File.Exists(contactsPath))
            {
                return null;
            }
            try
            {
                var contacts = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(contactsPath));
                return contacts;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load contacts from {contactsPath}: {ex.Message}");
                return null;
            }
        }
        private static bool TryMatchContact(
            IReadOnlyDictionary<string, string> contacts,
            string transcription,
            out string contactName,
            out string contactNumber)
        {
            contactName = null;
            contactNumber = null;

            if (string.IsNullOrWhiteSpace(transcription)) return false;

            // Clean up the incoming text
            string lowerText = transcription.ToLowerInvariant();
            string bestMatchKey = null;
            int lowestDistance = int.MaxValue;

            // We only care about matching individual words or short phrases
            string[] words = lowerText.Split(new[] { ' ', '.', ',', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var kv in contacts)
            {
                string targetName = kv.Key.ToLowerInvariant();

                // 1. Check for a strict match first (always preferred)
                if (lowerText.Contains(targetName))
                {
                    contactName = kv.Key;
                    contactNumber = kv.Value;
                    return true;
                }

                // 2. Fuzzy match word-by-word (handles phonetic misses like "shavon" vs "siobhan")
                foreach (var word in words)
                {
                    int distance = ComputeLevenshteinDistance(word, targetName);

                    // Threshold: adjust based on name lengths. 
                    // A max distance of 2 allows for minor phonetic misspellings.
                    if (distance <= 2 && distance < lowestDistance)
                    {
                        lowestDistance = distance;
                        bestMatchKey = kv.Key;
                    }
                }
            }

            if (bestMatchKey != null)
            {
                contactName = bestMatchKey;
                contactNumber = contacts[bestMatchKey];
                Console.WriteLine($"[Fuzzy Match] Mapped transcribed word to contact: '{bestMatchKey}' (Distance: {lowestDistance})");
                return true;
            }

            return false;
        }

        // Ultra-fast Levenshtein Distance implementation
        private static int ComputeLevenshteinDistance(string s, string t)
        {
            if (string.IsNullOrEmpty(s)) return t?.Length ?? 0;
            if (string.IsNullOrEmpty(t)) return s.Length;

            int n = s.Length;
            int m = t.Length;
            int[] d = new int[m + 1];

            for (int j = 0; j <= m; j++) d[j] = j;

            for (int i = 1; i <= n; i++)
            {
                int prevIdx = i;
                for (int j = 1; j <= m; j++)
                {
                    int oldD = d[j];
                    int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;

                    d[j] = Math.Min(Math.Min(d[j] + 1, prevIdx + 1), d[j - 1] + cost);
                    prevIdx = oldD;
                }
            }
            return d[m];
        }


        // NOT converted to ToolResult, deliberately. This is not one handler with
        // one answer — it asks a question, opens the mic for the reply, and
        // branches on what comes back, so there is no single sentence to hand
        // back. main converted it eventually, but only by deleting the sub-dialog
        // and moving the choice into `mode`/`query` tool parameters, which changes
        // both the schema and what the assistant says. That is a redesign, not a
        // return-channel change, and it belongs with the same pass that fixes
        // send_sms's dictation loop — which is the identical shape of bug.
        private static async Task HandleYouTubeAsync(SpeechService speechManager)
        {
            await speechManager.Say(recognizedText, "Okay! Would you like a specific video or to just open it?");

            string confirmation = (await speechManager.RecognizeOnceAsync()).ToLower();

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
        //
        // Also NOT converted, for the reason above plus one of its own: the
        // question it asks is a consent gate on an irreversible action. main's
        // replacement makes the model ask and pass the user's own answer back in a
        // `confirmed` parameter — a gate redesign, where getting it subtly wrong
        // shuts the machine down without being asked. It gets its own change,
        // reviewed as a gate rather than as part of a mechanical sweep.
        private static async Task HandleShutdownAsync(SpeechService speechManager, string action)
        {
            await speechManager.Say(recognizedText, "Are you sure?");

            string confirmationText = await speechManager.RecognizeOnceAsync();

            bool isShutdown = action == "shutdown";
            string actionText = isShutdown ? "Shutting down" : "Restarting now";

            if (string.Equals(confirmationText?.TrimEnd('.'), "yes", StringComparison.OrdinalIgnoreCase))
            {
                await speechManager.Say(confirmationText, $"Ok. {actionText}");
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