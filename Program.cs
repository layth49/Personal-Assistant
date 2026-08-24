using Microsoft.CognitiveServices.Speech;
using Personal_Assistant.AppLaunching;
using Personal_Assistant.Configuration;
using Personal_Assistant.Arduino;
using Personal_Assistant.AudioControl;
using Personal_Assistant.Diagnostics;
using Personal_Assistant.Dispatch;
using Personal_Assistant.Events;
using Personal_Assistant.GeminiClient;
using Personal_Assistant.Geolocator;
using Personal_Assistant.LightAutomator;
using Personal_Assistant.Live;
using Personal_Assistant.MediaControl;
using Personal_Assistant.PlaystationController;
using Personal_Assistant.Power;
using Personal_Assistant.PrayerTimesCalculator;
using Personal_Assistant.Presence;
using Personal_Assistant.ProcessControl;
using Personal_Assistant.Reminders;
using Personal_Assistant.Resume;
using Personal_Assistant.ScreenCapture;
using Personal_Assistant.SMSController;
using Personal_Assistant.SpeechManager;
using Personal_Assistant.Suggestions;
using Personal_Assistant.Triggers;
using Personal_Assistant.VoiceClips;
using Personal_Assistant.WeatherService;
using Python.Runtime;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

            // Fired timers and alarms speak outside any Live session too. Taken
            // from ReminderService rather than retyped — the wording has to match
            // exactly or every pre-rendered clip misses.
            lines.Add(ReminderService.AnnouncementFor(null, ReminderKind.Timer));
            lines.Add(ReminderService.AnnouncementFor(null, ReminderKind.Alarm));

            // Prayer announcements are the other thing that speaks with no
            // conversation open. Their wording depends on PrayerLeadMinutes, so
            // changing that setting needs a re-run of --render-clips the same way
            // changing the voice does.
            lines.AddRange(PrayerAnnouncer.ClipLines());
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
            // FIRST, ahead of even the config dump: anything written before this
            // is not captured, and the config dump is one of the lines most worth
            // having. The app is a WinExe with no console, so without this every
            // Console.WriteLine in the process goes nowhere — which is how a whole
            // screened call could fail leaving no record of why.
            FileLog.Start();

            CheckEnvironmentVariables();
            LaithConfig.Dump();

            // Offline mode: render the greeting/goodbye clips in the configured
            // Live voice and exit. Rendering goes through the Live API, which is
            // unmetered on this project, rather than the TTS model, which is
            // 3/min and 10/day. Re-run after changing LiveVoice.
            int renderAt = Array.IndexOf(args, "--render-clips");
            if (renderAt >= 0)
            {
                string voice = LiveSessionOptions.ConfiguredVoice;

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

            // A crash on a background thread — an NAudio capture callback, the
            // Live receive pump, the bubble pump — takes the process down with no
            // console output at all, so it reads as "it just closed". Nothing here
            // can prevent that; the point is purely that it leaves a stack trace
            // in the log instead of requiring the debugger to have been watching.
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Console.WriteLine(new string('!', 72));
                Console.WriteLine($"[crash] unhandled exception (terminating={e.IsTerminating})");
                Console.WriteLine(e.ExceptionObject?.ToString() ?? "(no exception object)");
                Console.WriteLine(new string('!', 72));
                Console.Out.Flush();
            };

            Runtime.PythonDLL = @"C:\Users\layth\AppData\Local\Programs\Python\Python312\python312.dll";
            PythonEngine.Initialize();
            PythonEngine.BeginAllowThreads();

            // Python modules ship next to the exe, so each deployed copy imports its
            // own. This used to point at a single shared folder, which meant a .py
            // deployed while working on one branch silently rebound the other
            // branch's imports — the 2026-08-03 bubble race was exactly that.
            // The shared folder stays on the path *after* the app directory purely
            // as a fallback, so an output dir missing a module still starts.
            using (Py.GIL())
            {
                dynamic sys = Py.Import("sys");
                sys.path.append(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'));

                string extra = LaithConfig.Text("PythonModulePath", @"C:\Users\layth\LAITH\local");
                if (!string.IsNullOrWhiteSpace(extra)) sys.path.append(extra.TrimEnd('\\'));
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
            // A fired reminder has no user utterance, so use a clock as the
            // bubble's "you said" label — a nice reminder indicator now that the
            // bubble renders emoji. It's only shown, never spoken.
            // SayClip, not Say: a fired timer speaks outside any Live session, so
            // it was the last thing still answering in the Azure voice. The two
            // unlabelled announcements are pre-rendered by --render-clips; a
            // labelled reminder ("Reminder: take the bins out.") is user-supplied
            // text that cannot be pre-rendered, and falls back to Azure.
            var reminders = new ReminderService(
                message => speechManager.SayClip("⏰", message, renderOnMiss: true),
                timerWidgets,
                // Rendering a clip takes ~5-7s, so a labelled reminder gets its
                // line rendered when the timer is SET rather than when it fires.
                // By the time it goes off the clip is already cached, and the
                // announcement is instant and in the right voice.
                prepare: message => VoiceClipRenderer.TryEnsureAsync(
                    LiveSessionOptions.ConfiguredVoice, message),
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
            // middle of the night?); the trigger service decides when one is due.
            // Both run off their own tickers and neither touches the wake-word
            // loop below — the assistant stays entirely usable if they do nothing.
            // The gate is told when a conversation is open, so nothing unprompted
            // speaks over the Live model's reply — or into the microphone it is
            // still listening on. `activeSession` is set by the wake-word loop
            // below and cleared when the session closes; reading it through a
            // lambda means the gate sees the current value, not the null it was
            // at construction.
            var presence = new PresenceGate(
                isBusy: () => System.Threading.Volatile.Read(ref activeSession) != null);
            var triggers = new TriggerService(presence);

            PrayerAnnouncer prayerAnnouncer = null;
            if (LaithConfig.Bool("PrayerAnnouncements", true))
            {
                prayerAnnouncer = new PrayerAnnouncer(
                    triggers,
                    location,
                    // 🕌 as the bubble's "you said" label, the way a fired reminder
                    // uses ⏰: there is no user utterance behind this one either.
                    // SayClip with renderOnMiss because it speaks outside any Live
                    // session, so an unrendered line would arrive in the Azure
                    // voice — and mispronounce the prayer names while it did.
                    message => speechManager.SayClip("🕌", message, renderOnMiss: true),
                    prepare: message => VoiceClipRenderer.TryEnsureAsync(
                        LiveSessionOptions.ConfiguredVoice, message));

                // Not awaited: planning the day needs a location lookup over the
                // network, and startup must not sit behind it. StartAsync handles
                // its own failures and re-plans on a timer.
                _ = prayerAnnouncer.StartAsync();
            }

            // Standing rules the user set by voice.
            //
            // There is a genuine cycle here: the rules need the dispatcher to run
            // tools, the dispatcher needs the registry, the registry needs the
            // context, and the context needs the rules so set_trigger can reach
            // them. Broken by capturing these two by reference and assigning them
            // below — the lambdas are only ever called from a fired trigger, which
            // cannot happen before Restore(), which is called after both are set.
            IntentDispatcher dispatcherRef = null;
            ToolRegistry registryRef = null;
            ConversationMemory conversationMemoryRef = null;

            // Things the assistant volunteers. Same cycle as the standing rules
            // below, resolved the same way — a suggestion's accept action runs a
            // tool, so it needs the dispatcher that doesn't exist yet.
            //
            // 💡 as the bubble label, alongside ⏰ / 🕌 / 📌: this one is the
            // assistant speaking first, which nothing else in the app does.
            var suggestions = new SuggestionService(
                triggers,
                message => speechManager.SayClip("💡", message, renderOnMiss: true),
                // Recorded as a model turn so the TURN-BASED path can resolve
                // "yes" from history. The Live path can't — it gets no history —
                // and is handled by LiveSession.BuildSuggestionHint instead.
                remember: offer => conversationMemoryRef?.AddModel(offer));

            // Things the user is waiting on that the world decides rather than
            // the clock. 🔔 as the bubble label, alongside ⏰ / 🕌 / 📌 / 💡.
            //
            // Given `suggestions` so a confirmed event becomes an OFFER ("it's
            // out — want me to open it?") rather than a browser window appearing
            // on its own. Nothing here opens a link without a spoken yes.
            var watcher = new EventWatchService(
                triggers,
                message => speechManager.SayClip("🔔", message, renderOnMiss: true),
                suggestions,
                persist: true);
            watcherRef = watcher;

            var voiceTriggers = new VoiceTriggers(
                triggers,
                processes,
                // 📌 as the bubble label, alongside ⏰ for reminders and 🕌 for
                // prayers: a standing rule speaking has no user utterance either.
                message => speechManager.SayClip("📌", message, renderOnMiss: true),
                runTool: (name, toolArgs, speak) => dispatcherRef.RunToolByNameAsync(name, toolArgs, speak),
                isKnownTool: name => registryRef?.FindByName(name) != null,
                prepare: message => VoiceClipRenderer.TryEnsureAsync(
                    LiveSessionOptions.ConfiguredVoice, message));

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
            // codebase is one a harness or a smoke test constructed to satisfy a
            // parameter, and those must not write over the real conversation.
            var conversationMemory = new ConversationMemory(persist: true);
            conversationMemory.Restore();
            conversationMemoryRef = conversationMemory;
            var dispatcher = new IntentDispatcher(
                registry,
                context,
                GeminiService.DetectToolAsync,
                GeminiService.GenerateGeminiResponse,
                conversationMemory,
                latency);

            // Close the cycle described above, then arm whatever the user had set
            // before the last restart. Restore must come after BOTH assignments: a
            // rule with a run_tool fires through dispatcherRef, and a rule due the
            // moment it is armed would otherwise hit a null.
            dispatcherRef = dispatcher;
            registryRef = registry;
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
                    () => speechManager.SayClip("↩️", catchUp, renderOnMiss: true),
                    grace: TimeSpan.FromHours(8));
            }

            // Suggestions last: every accept action runs a tool through the
            // dispatcher, and Start() arms an evaluator that could fire within the
            // minute. Registering the catalogue before dispatcherRef was set would
            // put a null on the trigger ticker — a background thread, i.e. the
            // failure that takes the process down with nothing on the console.
            SuggestionCatalogue.Register(
                suggestions,
                context,
                prayerAnnouncer,
                PresenceGate.IdleTime,
                (name, toolArgs, speak) => dispatcher.RunToolByNameAsync(name, toolArgs, speak));
            suggestions.Start();

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

                    // Close the session BEFORE Python goes away, not after.
                    // Environment.Exit fires ProcessExit, which closes it anyway —
                    // but by then the interpreter is gone, and disposing a session
                    // stops its bubble pump, whose exit path retracts the bubble
                    // through pythonnet. That is a Py.GIL() into a shut-down
                    // interpreter, and HideBubble only catches PythonException.
                    // This tool runs from inside a Live session that has almost
                    // certainly posted a bubble for the model's own goodbye, so
                    // the ordering is reachable on the ordinary path.
                    //
                    // CloseActiveSession takes the field with Interlocked.Exchange,
                    // so the ProcessExit handler simply finds nothing to do.
                    CloseActiveSession("exit_assistant");

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
                // NOT named `google_search`. That is the name of Gemini's OWN
                // built-in search tool, and declaring a function with the same
                // name shadows it: the model's grounding calls (which carry a
                // `queries` array, not our `query` string) were being routed here
                // and rejected, so grounding never ran and the model answered
                // from memory instead. Renaming frees the built-in name.
                ToolDefinition.Create("open_web_search",
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
                        // Covers every false path, not just the first one. Remote
                        // Play failing to start, its window never appearing, and
                        // the navigation throwing are all "the game did not launch"
                        // to the user, and naming only Remote Play described the
                        // wrong step for two of the three.
                        : ToolResult.Failed(
                            $"Sorry, I couldn't get {game} started on your PlayStation.",
                            "playstation_launch_failed");
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
                    // here is exactly the sort of thing the model would read out
                    // as "no time left".
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
                            return MediaOutcome(await ctx.Media.PlayAsync(), "Playing.", "play");
                        case "pause":
                            return MediaOutcome(await ctx.Media.PauseAsync(), "Paused.", "pause");

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
                        Required: false),
                    new ToolParameter("event_subject", "string",
                        "ONLY when the countdown is until a real-world event that either happens or " +
                        "doesn't — a game or episode release, a match kickoff, a launch. Give a short " +
                        "searchable description of the event itself, e.g. 'Re:Zero Season 4 Recapture " +
                        "Arc episode release'. Setting this means the countdown is treated as a " +
                        "DEADLINE that survives a shutdown, and when it runs out I check whether the " +
                        "event actually happened instead of just announcing it. Omit for ordinary " +
                        "timers ('remind me in 10 minutes'), which are paused while the PC is off.",
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
                        : $"Okay — in {DescribeDuration(secs)} I'll check whether {subject} has happened " +
                          "and let you know either way.";

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

                    // Watches are listed alongside, because from the user's side
                    // "the Re:Zero thing" is one of their pending items — the
                    // fact that it graduated from a countdown into something
                    // being checked is an implementation detail they never asked
                    // about, and hiding it makes it look like it was forgotten.
                    var watching = ctx.Watches?.Snapshot() ?? (IReadOnlyList<EventWatch>)new List<EventWatch>();

                    if (pending.Count == 0 && watching.Count == 0)
                    {
                        return Task.FromResult(
                            ToolResult.Speak("You have no timers or alarms set.").With("count", "0"));
                    }

                    if (pending.Count == 0)
                    {
                        string subjects = string.Join(", ", watching.Select(w => w.Describe()));
                        return Task.FromResult(ToolResult
                            .Speak($"No timers, but I'm still checking on {subjects}.")
                            .With("count", "0")
                            .With("watching_count", watching.Count.ToString())
                            .With("watching", subjects));
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

                    if (watching.Count > 0)
                    {
                        string subjects = string.Join(", ", watching.Select(w => w.Describe()));
                        sb.Append($" I'm also still checking on {subjects}.");
                        result = result
                            .With("watching_count", watching.Count.ToString())
                            .With("watching", subjects);
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
            // Only registered when there is a suggestion service, and the model is
            // told what is pending through the Live session's system instruction
            // (see LiveSession.BuildSuggestionHint) — a fresh session has no
            // conversation history, so without that hint "yeah, go on" refers to
            // nothing it can see.
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
                        "restarts; the Suggestions setting in the config is what decides the " +
                        "level it starts at.",
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

                        // A pending offer belongs to the old setting. Being told
                        // to shut up and then acting on the thing you were told to
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

            // Only offered once the trigger engine is wired. BuildRegistry is also
            // called by the Live smoke harness, which builds a context without one.
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
                        // Spelled out because the model produced both readings of
                        // "what to say": once "Discord has closed" (right) and
                        // once "I will tell you when Discord closes" (a
                        // confirmation, which would then be announced AT the
                        // moment it closed). The acknowledgement is written by
                        // this tool's own result — this field is only ever the
                        // future announcement.
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
                        new ToolParameter("until", "string",
                            "For every: stop for the day after this 24-hour HH:mm time.",
                            Required: false),
                        new ToolParameter("app", "string",
                            "For app_starts / app_stops: the program name, e.g. Discord or chrome.",
                            Required: false),
                        // Deliberately does NOT say which tools are disallowed.
                        // It used to, and the model routed around the prohibition:
                        // "text my mum every morning" came back as a rule that
                        // SAYS "good morning" out loud, silently turning a request
                        // to message someone into a different feature. Naming the
                        // tool the user asked for is always right; whether it may
                        // run unattended is the assistant's call to make, out loud,
                        // in VoiceTriggers.Validate.
                        new ToolParameter("run_tool", "string",
                            "Optional tool to run when it fires — name the tool the user actually " +
                            "asked for, e.g. control_lights, send_sms, power_control. Never " +
                            "substitute a different action from the one they asked for.",
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
                            sb.Append($"{i + 1}. {rules[i].Describe()}");
                            sb.Append(i < rules.Count - 1 ? "; " : ".");
                            // Numbered so "cancel the second one" has a referent,
                            // matching how list_reminders numbers its own.
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
        // at_time", so that shape is enforced here and reported back in words the
        // model can act on — a rejection that says which parameter was missing is
        // a retry, where a bare failure is a dead end.
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
                    pendingSubject = Canonical(subject);
                    pendingAtUtc = DateTime.UtcNow;
                }
            }

            // Both sides of the match go through this, so a subject can only fail
            // to match when the WORDS differ.
            //
            // The model retypes the subject on the confirming call and does not
            // reproduce it byte for byte: the first real send_sms confirmation
            // dropped a trailing full stop between the two calls, the Ordinal
            // compare below missed, the gate silently re-armed, and the model
            // announced it had sent a message that was never sent. Case,
            // whitespace and trailing punctuation are not things a user can hear,
            // so they cannot be things consent turns on.
            //
            // This lives here rather than in one handler because the gate is
            // shared: power_control builds its subject the same way and would
            // have failed the same way.
            private static string Canonical(string subject)
            {
                if (string.IsNullOrWhiteSpace(subject)) return string.Empty;

                var sb = new StringBuilder(subject.Length);
                bool lastWasSpace = false;
                foreach (char c in subject.Trim())
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
                    string wanted = Canonical(subject);
                    if (pendingSubject == null ||
                        !string.Equals(pendingSubject, wanted, StringComparison.Ordinal))
                    {
                        // The other two refusals below announce themselves; this one
                        // used not to, which is exactly why an unsent SMS looked like
                        // a sent one. A confirmation matching nothing is the most
                        // suspicious of the three — it means the model confirmed
                        // something the user was never asked about.
                        Console.WriteLine(
                            $"[confirm] '{wanted}' matches no armed request " +
                            $"(pending: '{pendingSubject ?? "none"}') -> refused");
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

            // "text her to introduce yourself" is an instruction, not a body.
            // Substituting the canned introduction has to happen HERE, above
            // everything else: the read-back below quotes `message`, the gate
            // subject binds to `message`, and the result reports `message`. Doing
            // the swap inside SMSControl.SendSMS — i.e. after the user had already
            // said yes — meant the read-back quoted one text, the gate consented
            // to it, and a different one went to a real phone.
            if (IsIntroductionRequest(message)) message = SMSControl.IntroductionText;

            if (ctx.Contacts == null ||
                !ctx.Contacts.TryGetValue(contact, out string number) ||
                string.IsNullOrWhiteSpace(number))
            {
                return ToolResult.Failed($"I don't have a number for {contact}.", "unknown_contact");
            }

            // Binds the approval to this contact AND this body, so a "yes" cannot
            // be spent on a message the user never heard read back. ConfirmationGate
            // canonicalises both sides, so trivial re-typing by the model does not
            // break the match while different words still do.
            string subject = "send_sms:" + contact + ":" + message;

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
                ConfirmationGate.Arm(subject);
                string question = $"You'd like to send \"{message}\" to {contact}. Should I send it?";
                return ToolResult
                    .Speak(question)
                    .With("status", "needs_confirmation")
                    .With("instruction",
                        "NOT sent. Ask the question verbatim and wait for the user's answer. " +
                        "Do not tell the user the message has been sent.")
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


        // Maps a media command's outcome onto what to say about it.
        //
        // Refused gets its own sentence. It used to share "Nothing is playing right
        // now" with NoSession, which was a plain untruth about a session that was
        // playing and had simply declined the command — and it hid the case worth
        // knowing about, because a session that refuses is a bug worth seeing.
        private static ToolResult MediaOutcome(
            MediaCommandResult outcome, string spoken, string action)
        {
            switch (outcome)
            {
                case MediaCommandResult.Done:
                    return ToolResult.Speak(spoken).With("action", action);
                case MediaCommandResult.NoSession:
                    return ToolResult.Failed("Nothing is playing right now.", "no_media_session");
                default:
                    return ToolResult.Failed(
                        $"I couldn't get it to {action}.", "media_command_refused");
            }
        }

        // Whether the "body" is really the standing request to send the canned
        // self-introduction, left over from when this flow dictated its own text.
        //
        // Matched whole, not scanned for. The substring test this replaces fired
        // on any occurrence anywhere, so "text mom that I still need to introduce
        // yourself to the new manager" had its entire body thrown away and
        // replaced with the introduction.
        private static bool IsIntroductionRequest(string message)
        {
            string t = message.Trim().TrimEnd('.', '!', '?').Trim();
            return t.Equals("introduce yourself", StringComparison.OrdinalIgnoreCase)
                || t.Equals("introduce yourself to them", StringComparison.OrdinalIgnoreCase)
                || t.Equals("introduce yourself to her", StringComparison.OrdinalIgnoreCase)
                || t.Equals("introduce yourself to him", StringComparison.OrdinalIgnoreCase);
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
