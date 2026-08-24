using Personal_Assistant.AudioControl;
using Personal_Assistant.Configuration;
using Personal_Assistant.Dispatch;
using Personal_Assistant.MediaControl;
using Personal_Assistant.Presence;
using Personal_Assistant.Resume;
using Personal_Assistant.Triggers;
using Personal_Assistant.VoiceClips;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Personal_Assistant.CallScreening
{
    // Why an arm request was refused, in words the assistant can say. Same shape
    // and same reasoning as TriggerRejection (VoiceTriggers.cs:17): a one-sentence
    // refusal Layth can act on beats silently arming into a path that cannot work.
    public sealed class ArmRefusal
    {
        public string Spoken { get; }   // what the user hears
        public string Reason { get; }   // for the model and the log

        public ArmRefusal(string spoken, string reason)
        {
            Spoken = spoken;
            Reason = reason;
        }
    }

    /// <summary>The answer to "may I start screening?", plus when it runs out.</summary>
    public sealed class ArmResult
    {
        /// <summary>Null when screening is now armed.</summary>
        public ArmRefusal Refusal { get; }
        public DateTime Until { get; }

        /// <summary>True when screening has no expiry and simply stays on.</summary>
        public bool Indefinite { get; }

        public ArmResult(ArmRefusal refusal, DateTime until = default, bool indefinite = false)
        {
            Refusal = refusal;
            Until = until;
            Indefinite = indefinite;
        }
    }

    // Whether screening is on, and what happens when the phone rings while it is.
    //
    // The four pieces are deliberately separate — the watcher knows WHEN, the
    // controller knows WHICH BUTTON, the router and bridge know WHERE THE AUDIO
    // GOES, and this knows WHETHER and WHAT NEXT.
    //
    // Phase 3 is where they finally meet. Phases 1 and 2 were built in parallel
    // and never wired together, so up to now the call was answered correctly and
    // the greeting was played correctly — to the laptop speakers, while the caller
    // listened to silence. What this class adds is the order the pieces have to
    // happen in, and that order is almost entirely made of things that fail
    // quietly if you get them wrong:
    //
    //   1. the audio route is engaged BEFORE the call is answered, not after
    //   2. nothing is spoken until TitleTextBlock actually reads "Call on PC"
    //   3. the machine is hushed first, because the speakers are the inbound leg
    //   4. the call is hung up BEFORE the route is put back
    //
    // WHEN IT IS ALLOWED TO ANSWER — and this REVERSED on 2026-08-23, so read the
    // reasoning before changing it back.
    //
    // It used to be "armed only, ever, and only when Layth said so out loud", on
    // the grounds that an assistant which silently picks up the phone whenever the
    // app happens to be running is a much worse product. That is still exactly
    // right for PHONE LINK, which answers a handset ringing in his hand.
    //
    // It is precisely wrong for GOOGLE VOICE. A call only reaches that number
    // because the carrier already forwarded it after he did not answer, so the
    // choice is never "the assistant or Layth" — it is "the assistant or
    // voicemail". And the arm had to be opened by SPEAKING to the machine, for
    // thirty minutes at a time, while the entire purpose is calls arriving when he
    // is out and cannot speak to it. The window was guaranteed to be shut exactly
    // when it mattered.
    //
    // So the default now follows the transport (ICallTransport.AnswersOnlyMissedCalls)
    // and "stop screening my calls" PAUSES rather than disarms — it comes back on
    // by itself, because a pause he forgets about would otherwise kill the feature
    // silently, and he would only find out by missing a screened call while out.
    public sealed class CallScreeningService : IDisposable
    {
        private readonly BluetoothHeadset headset = new BluetoothHeadset();
        // The one way a call can reach this PC on this run. See ICallTransport:
        // everything below the seam — arm state, audio routing, the conversation —
        // is identical whether the call came from Phone Link or a browser tab.
        private readonly ICallTransport transport;

        // The only visible sign a call is happening. Optional, and null when
        // switched off — every use below is null-conditional, because a machine
        // with no desktop session (or a harness) must still screen calls.
        private readonly CallWidgetHost widget;

        // Set as the conversation finishes so the card can show HOW it ended, and
        // read once by the teardown. A field rather than a return value because
        // the teardown that dismisses the card sits several frames above the code
        // that knows the outcome.
        private string lastCallSummary;
        private readonly CallAudioRouter router;

        // Both optional, and both only affect what happens DURING a call — see
        // HushAsync. Null in the bakeoff harnesses, which have no reason to own a
        // presence gate or a media session.
        private readonly PresenceGate presence;
        private readonly MediaController media;

        private readonly TimeSpan defaultArm;
        private readonly TimeSpan maxCall;
        private readonly string greetingPath;
        private readonly TimeSpan greetingLeadIn;
        private readonly bool disconnectWhileRinging;

        private readonly object gate = new object();
        private DateTime armedUntil = DateTime.MinValue;

        // Always-armed mode: screening is on unless PAUSED, rather than off unless
        // armed. Set from the transport, overridable by config.
        private readonly bool alwaysArmed;
        private readonly TimeSpan pauseWindow;

        // Deliberately NOT persisted. A restart resuming screening is the safe
        // direction: the failure it protects against is a pause nobody remembers,
        // and there is no failure in coming back on.
        private DateTime pausedUntil = DateTime.MinValue;

        // What a caller is allowed to make the assistant do. Assigned after the
        // registry exists — see UseAssistantTools — and deliberately starts at
        // CallTools.None so that a startup path which forgets to call it yields a
        // caller who can take a message and nothing else. Failing closed is the
        // only acceptable direction here.
        private CallTools tools = CallTools.None;

        // 1 while a call is being screened. Phone Link has one call surface, so a
        // second overlapping attempt could only fight the first for the same
        // buttons.
        private int handling;

        // The conversation in progress, so end_call and process teardown can stop
        // it rather than waiting for the cap.
        private CallSession live;

        // Getting what a caller said in front of Layth, rather than leaving it in
        // the log for him to think to open. Never null — with no browser and no
        // speech it simply has no channels and says so.
        private readonly MessageDelivery delivery;

        // The last call's log entry, handed from the conversation to the teardown
        // that delivers it. A field for the same reason lastCallSummary is one:
        // the code that knows the outcome is several frames below the code that
        // acts on it once the line is down.
        private CallRecord lastRecord;

        // Owned here rather than by the transport, because the text channel needs
        // the SAME signed-in browser. Null on Phone Link. Disposed by this class,
        // since passing it in makes the transport a borrower rather than an owner.
        private readonly GoogleVoiceBrowserHost gvBrowser;

        public CallScreeningService(
            TriggerService triggers,
            PresenceGate presence = null,
            MediaController media = null,
            CallAudioRouter router = null,
            Func<string, Task> announce = null)
        {
            if (triggers == null) throw new ArgumentNullException(nameof(triggers));

            this.presence = presence;
            this.media = media;
            this.router = router ?? new CallAudioRouter();

            // Through LaithConfig, so every one of these gets a LAITH_* override
            // and a line in the startup [config] dump for free.
            defaultArm = TimeSpan.FromMinutes(
                LaithConfig.Int("CallScreeningArmMinutes", 30, 1, 480));
            maxCall = TimeSpan.FromSeconds(
                LaithConfig.Int("CallMaxSeconds", 180, 10, 1800));
            greetingPath = ResolveGreetingPath();
            greetingLeadIn = TimeSpan.FromMilliseconds(
                LaithConfig.Int("CallGreetingLeadInMs", 2000, 0, 10000));
            disconnectWhileRinging = LaithConfig.Bool("CallDropHeadsetWhileRinging", true);

            // WHICH TRANSPORT. Exactly one per process: both register the same
            // `call.incoming` trigger, so running two would mean two things
            // racing to answer one call.
            //
            // The route-B headset wiring that used to sit here moved into
            // PhoneLinkCallTransport, because it is Phone Link's problem and not
            // screening's — a browser has no opinion about headsets. Everything
            // below this line is transport-agnostic.
            string want = LaithConfig.Text("CallTransport", "phonelink").ToLowerInvariant();
            GoogleVoiceTextSender sms = null;

            if (want == "googlevoice" || want == "gv")
            {
                // The browser is built HERE and lent to the transport, rather
                // than left for the transport to make privately. Delivering a
                // message by text drives the same signed-in Google Voice session
                // — in a tab of its own, but the same session — and a second
                // browser on the same profile would mean two Chrome instances
                // fighting over one user-data-dir, which is how a stale instance
                // once absorbed a whole call.
                gvBrowser = new GoogleVoiceBrowserHost();

                transport = new GoogleVoiceCallTransport(
                    triggers,
                    isArmed: () => IsArmed,
                    onIncomingCall: OnIncomingCallAsync,
                    browser: gvBrowser);

                sms = new GoogleVoiceTextSender(gvBrowser);
            }
            else
            {
                if (want != "phonelink")
                    Console.WriteLine(
                        $"[call] unknown CallTransport '{want}' — falling back to phone link.");

                transport = new PhoneLinkCallTransport(
                    triggers,
                    headset,
                    isArmed: () => IsArmed,
                    onIncomingCall: OnIncomingCallAsync,
                    pollInterval: TimeSpan.FromMilliseconds(
                        LaithConfig.Int("CallScreeningPollMs", 2000, 250, 10000)));
            }

            // Built with whatever channels this transport actually has. On Phone
            // Link that is the spoken one only, which is correct rather than
            // degraded: there is no Google Voice session there to text through.
            delivery = new MessageDelivery(triggers, sms, announce);

            if (LaithConfig.Bool("CallWidget", true))
            {
                widget = new CallWidgetHost();
                // Wired to whatever call is live at the moment it is pressed. Null
                // between calls, which is exactly when the button draws disabled.
                widget.OnHangUp = () =>
                {
                    Console.WriteLine("[call] hang up pressed on the call widget.");

                    // TWO SEPARATE THINGS, and pressing this must do both.
                    //
                    // Stopping the session ends the CONVERSATION; it does not end
                    // the CALL. Measured on the first Google Voice call: the model
                    // said goodbye, the session closed, and the line stayed open
                    // with the card counting upwards — at which point the only
                    // control offered did nothing, because it only knew how to
                    // stop a session that had already stopped.
                    CallSession s = Volatile.Read(ref live);
                    if (s != null) s.Stop(CallEnding.Cancelled);

                    // Off the UI thread: hanging up drives the browser over CDP
                    // and can take seconds, and this runs on the widget's message
                    // loop — blocking it would freeze the very card that is
                    // showing the call.
                    Task.Run(() =>
                    {
                        try { transport.HangUp(); }
                        catch (Exception ex) { Console.WriteLine($"[call] widget hang up failed: {ex.Message}"); }

                        // Dismiss the card here too, rather than relying on the
                        // normal teardown to do it. Pressing Hang up and watching
                        // the line drop while the card carries on counting is the
                        // button appearing not to work — which is precisely how it
                        // was reported. Ending twice is harmless; the second call
                        // finds the card already closing.
                        widget?.Ended(lastCallSummary ?? "hung up from the widget");
                    });
                };
            }

            // Its own line, the way PresenceGate prints its own: LaithConfig.Dump()
            // runs at the top of Main, long before this is constructed, so these
            // settings would otherwise never appear anywhere at startup.
            //
            // The missing-greeting warning is here and not only in Arm because
            // that is the difference between finding out now and finding out the
            // moment you try to walk away.
            // Read AFTER the transport exists, because the default comes from it.
            // A tri-state so config can force either answer and "unset" still means
            // "whatever this transport should do".
            alwaysArmed = LaithConfig.TriState("CallScreeningAlwaysArmed")
                          ?? transport.AnswersOnlyMissedCalls;
            pauseWindow = TimeSpan.FromMinutes(
                LaithConfig.Int("CallScreeningPauseMinutes", 120, 1, 1440));

            if (alwaysArmed)
                Console.WriteLine(
                    $"[call] screening is ON by default over {transport.Name} — " +
                    $"\"stop screening my calls\" pauses it for {pauseWindow.TotalMinutes:F0}m.");

            Console.WriteLine(
                $"[call] screening ready — arms for {defaultArm.TotalMinutes:F0}m, " +
                $"{maxCall.TotalSeconds:F0}s cap, persona {CallPersona.Configured()}, " +
                $"over {transport.Name}, greeting {greetingPath}" +
                (File.Exists(greetingPath) ? "" : "  (MISSING — screening will refuse to arm)"));
        }

        /// <summary>
        /// Hands the call path the two read-only tools it may borrow from the main
        /// registry. Call it once, after the registry is built.
        /// </summary>
        /// <remarks>
        /// A method rather than a constructor argument because of a genuine cycle:
        /// the registry is built from a CommandContext, and the context carries
        /// this service so that screen_calls and end_call can reach it. The same
        /// knot Program breaks for the dispatcher and the standing rules.
        /// </remarks>
        public void UseAssistantTools(ToolRegistry registry, CommandContext context)
        {
            tools = CallTools.From(registry, context);
            Console.WriteLine($"[call] a caller may use: {tools.Describe()}");
        }

        /// <summary>True while a call is actually being screened.</summary>
        /// <remarks>
        /// Read by PresenceGate, which must report BUSY for the duration: nothing
        /// unprompted may be spoken while a stranger is on the line, because the
        /// inbound leg is a loopback on the speakers and the announcement would go
        /// straight down the phone — and then straight back into the model as
        /// though the caller had said it.
        /// </remarks>
        public bool IsOnCall => Volatile.Read(ref handling) != 0;

        /// <summary>Begins watching for calls. Answers nothing until armed.</summary>
        public void Start() => transport.Start();

        public bool IsArmed
        {
            get
            {
                lock (gate)
                {
                    // Two different questions, depending on which way round the
                    // default runs: "has a pause expired?" or "is a window open?".
                    if (alwaysArmed)
                    {
                        if (DateTime.Now < pausedUntil) return false;

                        // THE GREETING GUARD, which always-armed mode would
                        // otherwise lose. ArmAsync refuses without a greeting
                        // recording — answering a caller with silence is worse
                        // than letting it ring — but in this mode nobody calls
                        // ArmAsync, so that check has to live here instead.
                        return GreetingPresent();
                    }

                    return DateTime.Now < armedUntil;
                }
            }
        }

        public DateTime? ArmedUntil
        {
            get
            {
                lock (gate)
                {
                    // No expiry to report when it simply stays on.
                    if (alwaysArmed) return null;
                    return DateTime.Now < armedUntil ? armedUntil : (DateTime?)null;
                }
            }
        }

        /// <summary>
        /// Turns screening on for <paramref name="duration"/> (the configured
        /// default when null), once it has proved it could actually answer.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every check here refuses OUT LOUD rather than arming optimistically,
        /// because every way this feature fails sounds identical from the caller's
        /// side: the phone is picked up and nobody is there. An armed window that
        /// cannot answer is worse than no screening at all — Layth walked away
        /// having been told it was handled.
        /// </para>
        /// <para>
        /// The preflight is the expensive one, and it is a SIGNAL test, not a
        /// presence test: a tone is played into each leg and it is asked whether it
        /// arrived. It costs about two seconds and it is worth all of them — a
        /// driver being installed and sound reaching the far end of a cable are
        /// different questions, and only the second one matters. Set
        /// CallPreflightTone=false to skip straight to a presence check, which is
        /// faster and proves less.
        /// </para>
        /// </remarks>
        public async Task<ArmResult> ArmAsync(TimeSpan? duration, CancellationToken cancel = default)
        {
            if (!File.Exists(greetingPath))
            {
                return Refuse(
                    "I can't screen calls yet — there's no greeting recording for me to play.",
                    $"greeting wav missing at {greetingPath}");
            }

            // Asked of the TRANSPORT, not assumed. This used to test for Phone
            // Link unconditionally, which refused every arm on the Google Voice
            // path if that app happened not to be running — while blaming a
            // component that path never touches.
            ArmRefusal notReady = transport.NotReady();
            if (notReady != null) return Refuse(notReady.Spoken, notReady.Reason);

            CallAudioFault audio;
            try
            {
                audio = await router.PreflightAsync(cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // A preflight that THREW proves nothing about the legs, so it is
                // treated as a failure rather than waved through. Answering into
                // an unproven path is the outcome this whole method exists to
                // prevent.
                audio = new CallAudioFault(
                    "I can't screen calls — something went wrong checking the call audio.",
                    $"preflight threw: {ex.GetType().Name}: {ex.Message}");
            }

            // The spoken half of the fault already names the broken leg — "my voice
            // can't reach the caller" / "I can't hear the caller" — which is the
            // difference between a sentence Layth can act on and "it didn't work".
            if (audio != null) return Refuse(audio.Spoken, audio.Reason);

            TimeSpan window = duration ?? defaultArm;
            if (window <= TimeSpan.Zero) window = defaultArm;

            if (alwaysArmed)
            {
                // Nothing to open — screening is already the resting state. What
                // "arm" means here is CANCEL A PAUSE, which is what someone saying
                // "screen my calls" after having stopped them actually wants.
                bool wasPaused;
                lock (gate)
                {
                    wasPaused = DateTime.Now < pausedUntil;
                    pausedUntil = DateTime.MinValue;
                }

                Console.WriteLine(wasPaused
                    ? "[call] screening resumed (the pause was cancelled)."
                    : "[call] screening was already on.");
                return new ArmResult(null, DateTime.MaxValue, indefinite: true);
            }

            DateTime until;
            lock (gate)
            {
                armedUntil = DateTime.Now.Add(window);
                until = armedUntil;
                CallScreeningStore.Save(armedUntil);
            }

            Console.WriteLine($"[call] screening armed until {until:HH:mm} ({window.TotalMinutes:F0}m)");
            return new ArmResult(null, until);
        }

        private static ArmResult Refuse(string spoken, string reason)
        {
            Console.WriteLine($"[call] not arming — {reason}");
            return new ArmResult(new ArmRefusal(spoken, reason));
        }

        /// <summary>
        /// Turns screening off. False when it was already off.
        /// </summary>
        /// <remarks>
        /// In always-armed mode this PAUSES rather than disarms, and says so, for
        /// a reason worth keeping: an off switch he forgets about would kill the
        /// feature silently, and the only way he would ever find out is by missing
        /// a screened call while out — the exact situation it exists for. Coming
        /// back on by itself is the failure this can afford; staying off is not.
        /// </remarks>
        public bool Disarm()
        {
            if (alwaysArmed)
            {
                DateTime until;
                lock (gate)
                {
                    if (DateTime.Now < pausedUntil) return false;   // already paused
                    pausedUntil = DateTime.Now.Add(pauseWindow);
                    until = pausedUntil;
                }

                Console.WriteLine(
                    $"[call] screening paused until {until:HH:mm} " +
                    $"({pauseWindow.TotalMinutes:F0}m), then it comes back on.");
                return true;
            }

            lock (gate)
            {
                if (DateTime.Now >= armedUntil) return false;
                armedUntil = DateTime.MinValue;
                CallScreeningStore.Save(null);
            }
            Console.WriteLine("[call] screening disarmed.");
            return true;
        }

        /// <summary>
        /// Puts the arm back after a restart, the way timers and standing rules
        /// come back. A crash inside a thirty-minute armed window is exactly when
        /// silently disarming would be worst: Layth walked away believing his
        /// calls were covered, and nothing would tell him otherwise.
        /// </summary>
        public ResumeSummary Restore()
        {
            var summary = new ResumeSummary();

            // Before anything else, and unconditionally — a headset left disabled
            // by a crash is silent on this PC until something re-enables it, with
            // nothing in Windows' UI to explain why. It has nothing to do with
            // whether screening was armed, so it must not be behind that check.
            string headsetRepair = BluetoothHeadset.RepairFromDisk();
            if (headsetRepair != null)
            {
                Console.WriteLine($"[headset] {headsetRepair}");
                summary.Resumed.Add("Bluetooth audio re-enabled after a call was cut short");
            }

            // Messages that were taken but never got to him before the last
            // shutdown. Unconditional, like the headset repair above and for the
            // same reason: whether screening happens to be armed right now has
            // nothing to do with whether somebody left a message yesterday.
            summary.Absorb(delivery.Restore());

            DateTime? saved = CallScreeningStore.LoadArmedUntil();
            if (!saved.HasValue) return summary;

            if (saved.Value <= DateTime.Now)
            {
                // Logged, never spoken — the window ended on its own terms, which
                // is what it was supposed to do.
                CallScreeningStore.Save(null);
                summary.Dropped.Add($"call screening (expired {saved.Value:ddd HH:mm})");
                return summary;
            }

            lock (gate) { armedUntil = saved.Value; }
            Console.WriteLine($"[call] screening still armed until {saved.Value:HH:mm}");
            summary.Resumed.Add($"call screening until {saved.Value:HH:mm}");
            return summary;
        }

        /// <summary>
        /// Hangs up whatever is connected. True if there is now no call — including
        /// when there was none to begin with.
        /// </summary>
        /// <param name="attempts">
        /// One, from process teardown, where the whole handler has about two
        /// seconds; the default two everywhere else.
        /// </param>
        public Task<bool> EndCallAsync(int attempts = 2)
        {
            // The conversation is stopped first so the Live session closes tidily
            // and its final transcript is written, rather than being cut off by a
            // call window that vanished underneath it.
            Volatile.Read(ref live)?.Stop(CallEnding.Cancelled);
            return Task.Run(() => transport.HangUp(attempts));
        }

        /// <summary>Where the current call is, for the tools to report.</summary>
        public CallLocation CurrentLocation() => transport.CurrentLocation();

        /// <summary>
        /// Re-enables a headset route B disabled. Safe to call when nothing was
        /// disabled, which is what the teardown hooks rely on.
        /// </summary>
        public void RestoreHeadset(string why) => headset.Reconnect(why);

        // --- The screening itself -------------------------------------------------

        /// <summary>
        /// Undoes the damage if a call never finishes tearing itself down.
        /// </summary>
        /// <remarks>
        /// Twice on 2026-08-22 a screened call hung after the conversation ended:
        /// the line stayed open, the widget counted upwards forever, and — the part
        /// that actually hurts — the Communications/Console/Multimedia capture role
        /// was left pointing at CABLE Output. That does not break the call it
        /// happened on. It breaks the NEXT one, and every dictation and meeting in
        /// between, silently, which is the exact failure mode this project has
        /// already been bitten by once.
        ///
        /// This does NOT fix the hang — the hung task keeps running and the
        /// instrumentation exists to find it. It only ensures the machine is not
        /// left broken while that investigation continues. `handling` is
        /// deliberately NOT cleared: the stuck call still owns whatever it owns,
        /// and inviting a second call into that is worse than screening nothing.
        /// </remarks>
        private void StartTeardownWatchdog()
        {
            // Past the hard cap by a clear margin, so a call running to its full
            // length can never trip this.
            TimeSpan limit = maxCall + TimeSpan.FromSeconds(60);

            _ = Task.Run(async () =>
            {
                await Task.Delay(limit).ConfigureAwait(false);
                if (Volatile.Read(ref handling) == 0) return;   // ended normally

                Console.WriteLine(
                    $"[call] WATCHDOG: a call has been handling for over {limit.TotalSeconds:F0}s " +
                    "and never tore down. Forcing the line down and putting the audio back.");

                try { transport.HangUp(); }
                catch (Exception ex) { Console.WriteLine($"[call] watchdog hang up failed: {ex.Message}"); }

                try { router.Restore("watchdog: the call never tore down"); }
                catch (Exception ex) { Console.WriteLine($"[call] watchdog restore failed: {ex.Message}"); }

                widget?.Ended("did not end cleanly - see the log");
            });
        }

        private async Task OnIncomingCallAsync(IncomingCall call)
        {
            // Re-checked here and not only in the watcher. Between the poll that
            // saw the ring and this tick, the window can have expired or Layth can
            // have said "stop screening" — and the last check before picking up a
            // real phone should be as late as possible.
            if (!IsArmed)
            {
                Console.WriteLine($"[call] {call.Caller} rang but screening is no longer armed — leaving it.");
                return;
            }

            if (Interlocked.CompareExchange(ref handling, 1, 0) != 0)
            {
                Console.WriteLine($"[call] already screening a call — leaving {call.Caller} alone.");
                return;
            }

            widget?.Ringing(call);
            StartTeardownWatchdog();

            var started = Stopwatch.StartNew();
            try
            {
                // Re-checked at ring time, not just at arm time: a file that was
                // there when Layth armed can be gone now, and answering with
                // nothing to play is a caller listening to silence. Refusing to
                // pick up at least lets them reach voicemail.
                if (!File.Exists(greetingPath))
                {
                    Console.WriteLine($"[call] not answering — the greeting is missing ({greetingPath}).");
                    return;
                }

                // THE ROUTE GOES FIRST, BEFORE THE CALL IS ANSWERED.
                //
                // Not what the plan says ("engage when a call is answered"), and
                // the difference matters: an app that has already opened its audio
                // streams is under no obligation to notice that the default
                // endpoint changed underneath it. Phone Link opens its streams the
                // moment the call connects, so moving the Communications role
                // afterwards is a race that would be won and lost silently — the
                // call answered, the caller on a dead cable, nothing in the log.
                // Engaging first means Phone Link finds the call path already in
                // place and simply uses it.
                //
                // The window this widens is the one between here and the hang-up,
                // and it is covered: Restore runs in the finally below, in the
                // ProcessExit and Ctrl+C hooks, and from disk at the next startup.
                CallAudioFault fault = router.Engage(out CallAudioRoute route);
                if (fault != null)
                {
                    // Not answered at all. Picking up with no audio path is the one
                    // outcome that is worse than letting it ring: the caller has
                    // then lost voicemail as well.
                    Console.WriteLine($"[call] NOT answering {call.Caller} — {fault.Reason}");
                    return;
                }

                try
                {
                    await ScreenAsync(call, route, started).ConfigureAwait(false);
                }
                finally
                {
                    // Hang up BEFORE putting the role back. The other order leaves
                    // a connected call whose audio devices move underneath it,
                    // which is both a worse experience for whoever is still on the
                    // line and a good way to leave Phone Link holding the cable.
                    Console.WriteLine("[call/teardown] hanging up the line");
                    bool ended = await Task.Run(() => transport.HangUp()).ConfigureAwait(false);
                    Console.WriteLine($"[call/teardown] hang up returned {ended}");
                    if (!ended) Console.WriteLine("[call] THE LINE MAY STILL BE OPEN.");

                    // THE HEADSET GOES BACK BEFORE THE ROLES, and the order is the
                    // whole point.
                    //
                    // It used to be the other way round, on the reasoning that the
                    // headset is slowest and affects the caller least. Measured on
                    // the first route-B call (2026-08-17), that produced four
                    // WARNING lines and a failed restore: the defaults saved at
                    // engage time pointed at `Headphones (Layth's AirPods Pro)` and
                    // `Headset (Layth's AirPods Pro)`, because the AirPods were
                    // connected when the call arrived — and route B had since
                    // DELETED those endpoints in order to answer. Restoring to a
                    // device that no longer exists cannot work.
                    //
                    // It looked harmless only because Windows re-pointed everything
                    // at the AirPods by itself once they came back. That is Windows'
                    // preference for the last-connected device doing our job for
                    // us, and it would not have covered a machine whose defaults
                    // were anything else.
                    // Read BEFORE the reconnect, which clears the flag it asks about.
                    bool tookTheHeadset = headset.WasDisconnected;
                    headset.Reconnect("the call ended");
                    await WaitForHeadsetEndpointsAsync(tookTheHeadset).ConfigureAwait(false);

                    Console.WriteLine("[call/teardown] restoring the audio route");
                    router.Restore("the call ended");

                    // Dismissed here rather than where the call ended, so the card
                    // clears on EVERY path — refused, never answered, failed
                    // mid-conversation — instead of only the happy one. A card
                    // left showing "screening" over a dead line is worse than no
                    // card at all.
                    widget?.Ended(lastCallSummary ?? "call ended");
                    lastCallSummary = null;
                }
            }
            catch (Exception ex)
            {
                // A throw here would surface as "[trigger] call.incoming failed" and
                // nothing else. Worse, it would skip the hang-up — so try once more
                // to put the phone down, and put the audio back, before giving up.
                Console.WriteLine($"[call] screening {call.Caller} failed: {ex.Message}");
                try { transport.HangUp(); } catch { }
                try { router.Restore("screening failed"); } catch { }
                try { headset.Reconnect("screening failed"); } catch { }
            }
            finally
            {
                Volatile.Write(ref handling, 0);

                // DELIVERY RUNS AFTER THE INTERLOCK IS RELEASED, and off this
                // thread. Sending a text drives a browser and can take fifteen
                // seconds; holding `handling` for that long would make the
                // assistant refuse to screen a call that rang while it was
                // typing — trading the message it just took for the next one it
                // would have answered.
                // A greeting for next time, if this caller had none. Off the call
                // path entirely — it opens its own Live session and takes the
                // better part of ten seconds, which is why it can never happen
                // while somebody is waiting on the line.
                EnsureGreetingClip(call.Caller);

                CallRecord toDeliver = Interlocked.Exchange(ref lastRecord, null);
                if (toDeliver != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await delivery.OnCallEndedAsync(toDeliver).ConfigureAwait(false); }
                        catch (Exception ex)
                        {
                            // Belt and braces; OnCallEndedAsync handles its own.
                            // A throw on a pool thread with nobody awaiting it
                            // takes the process down, which is not a thing a
                            // failed text should be able to do.
                            Console.WriteLine($"[call/deliver] failed: {ex.Message}");
                        }
                    });
                }
            }
        }

        // Answer, greet, converse. Everything from here on assumes the audio route
        // is already engaged and guarantees nothing about hanging up — the caller
        // above owns both.
        private async Task ScreenAsync(IncomingCall call, CallAudioRoute route, Stopwatch started)
        {
            // DROP THE HEADSET WHILE IT IS STILL RINGING, not after answering.
            //
            // Route B works, but it costs the caller ~9s of silence on a call they
            // have already been connected to: ~7.3s of disconnect (which does not
            // reduce — see BluetoothHeadset) plus the transfer and the greeting
            // run-up. Layth's call, and he is right: three extra rings is a normal
            // experience, being answered and met with nothing is not.
            //
            // The plan rejected ring-time disconnection because of the ~20s ring
            // deadline. That was written before the disconnect was measured — 7.3s
            // fits inside the ring with room to spare, and answering first was only
            // ever the safer choice because the cost was unknown.
            //
            // This needs no new routing logic, which is the nice part: Answer()
            // already reads the toast's first button and branches on its NAME. If
            // Phone Link re-renders the toast once the headset is gone, it finds
            // `Accept on PC` and route B never happens. If it does not re-render,
            // it finds `Use mobile device` as before and takes route B — but with
            // the disconnect already paid for, so the transfer is immediate. The
            // dead air moves into the ring either way.
            await DisconnectHeadsetWhileRingingAsync().ConfigureAwait(false);

            widget?.Stage(CallStage.Answering);

            AnswerResult answer = await Task.Run(() => transport.Answer()).ConfigureAwait(false);
            Console.WriteLine($"[call] {answer.Outcome}: {answer.Detail}");

            if (answer.Outcome != AnswerOutcome.OnPc)
            {
                // Every other outcome has already left the phone in a defensible
                // state: nothing was clicked, or the call was ended.
                return;
            }

            // THE TITLE GATE, read again rather than inherited.
            //
            // Answer only returns OnPc having seen TitleTextBlock say so, so this
            // is belt and braces — but it is cheap belt and braces against the one
            // mistake that wastes a whole real-call test: a greeting spoken while
            // the audio is still on the handset is a greeting nobody hears, and it
            // looks exactly like a broken cable from the outside. The seconds
            // between Answer returning and here are also long enough for a caller
            // to give up.
            CallLocation where = transport.CurrentLocation();
            if (where != CallLocation.OnPc)
            {
                Console.WriteLine(
                    $"[call] the call is {transport.Describe(where)} by the time I " +
                    "looked again — not speaking into it.");
                return;
            }

            widget?.Stage(CallStage.Screening);

            // Only now is the machine hushed. Doing it at arm time would mean
            // muting announcements and pausing music for a thirty-minute window in
            // which no call may ever arrive.
            using (Hush hush = await HushAsync().ConfigureAwait(false))
            using (var bridge = new CallAudioBridge(route.MonitorRenderId, route.CableRenderId))
            {
                bridge.Start();

                await PlayGreetingAsync(bridge, route, started, call.Caller).ConfigureAwait(false);

                TimeSpan left = maxCall - started.Elapsed;
                if (left <= TimeSpan.Zero)
                {
                    Console.WriteLine("[call] the greeting used the whole call budget — hanging up.");
                    return;
                }

                await ConverseAsync(call, bridge, left).ConfigureAwait(false);
                Console.WriteLine("[call/teardown] conversation returned; tearing the bridge down");
            }
        }

        // The conversation itself. Phase 3a: the caller hears Gemini's own voice.
        private async Task ConverseAsync(IncomingCall call, CallAudioBridge bridge, TimeSpan budget)
        {
            // The card shows the last thing said, so a glance tells you how the
            // call is going without reading the console.
            var session = new CallSession(
                bridge,
                tools,
                CallPersona.Build(call.Caller, greeted: true),
                budget);

            // Subscribed before the session runs, so the very first thing said is
            // on the card. The handler only marshals onto the widget's own UI
            // thread — see CallSession.LineRecorded, which fires while a stranger
            // is on the line and cannot afford a slow subscriber.
            if (widget != null)
                session.LineRecorded += line =>
                    widget.Said(line.Speaker == CallSpeaker.Caller, line.Text);

            Volatile.Write(ref live, session);
            CallOutcome outcome;
            try
            {
                using (session)
                {
                    outcome = await session.RunAsync(
                        call,
                        // The only honest source for "are they still there" — a
                        // dead line and a caller thinking sound identical.
                        stillConnected: () => transport.CurrentLocation() != CallLocation.None)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                Console.WriteLine("[call/teardown] session disposed, clearing live");
                Volatile.Write(ref live, null);
            }

            lastCallSummary = outcome.Summary();
            Console.WriteLine($"[call] screened {outcome.Summary()}");

            // Written down first, delivered by the teardown. Appending here and
            // delivering later is deliberate: the record must survive even if the
            // delivery, the process, or the machine does not.
            CallRecord record = CallRecord.From(outcome);
            CallLogStore.Append(record);
            lastRecord = record;
        }

        // Plays the fixed greeting DOWN THE PHONE LINE, capped.
        //
        // Not through VoiceClipCache.PlayAsync, which is what Phase 2 did: that
        // opens a WaveOutEvent on the machine's default output — the laptop
        // speakers — so the caller heard nothing and Layth heard the greeting. The
        // bridge writes to CABLE Input, which VB-CABLE presents back to Phone Link
        // as its microphone.
        //
        // It is still worth playing a fixed recording rather than letting the model
        // open: it covers the Live session's connect handshake, exactly the way the
        // wake greeting covers it for a normal conversation.
        /// <summary>
        /// Gives a re-enabled headset a moment to reappear as an audio endpoint,
        /// so the role restore has something to restore TO.
        /// </summary>
        //
        // Bounded and best-effort on purpose. If the AirPods are in their case, or
        // out of range, or simply slow, they are never coming back within any wait
        // worth having — and the restore must still run. A failed restore prints
        // its own warning; a restore that never ran prints nothing at all.
        private async Task DisconnectHeadsetWhileRingingAsync()
        {
            // Not merely pointless on a browser transport — actively wrong. This
            // disables a hardware device and re-enables it afterwards, costing the
            // caller ~7.3s of dead air that could not be reduced. Google Voice
            // renders to whatever the default endpoint is and never hands a call
            // to the handset, so there is nothing to protect against.
            if (!transport.RequiresHeadsetDisconnect) return;
            if (!disconnectWhileRinging) return;

            bool connected = await Task.Run(() => headset.AnyConnected()).ConfigureAwait(false);
            if (!connected) return;

            Console.WriteLine(
                "[call] a headset is on the PC — dropping it while the phone is still ringing, " +
                "so the caller waits through rings rather than through silence");

            await Task.Run(() => headset.Disconnect()).ConfigureAwait(false);

            // Deliberately no refusal on failure. If the disconnect did not work,
            // Answer() still finds `Use mobile device` and route B runs exactly as
            // it did before — which is a working call. Refusing here would turn a
            // slow answer into no answer.
        }

        private async Task WaitForHeadsetEndpointsAsync(bool tookTheHeadset)
        {
            if (!tookTheHeadset) return;

            DateTime deadline = DateTime.Now.AddSeconds(5);
            while (DateTime.Now < deadline)
            {
                if (headset.AnyConnected()) return;
                await Task.Delay(250).ConfigureAwait(false);
            }

            Console.WriteLine(
                "[headset] did not come back within 5s — restoring the audio roles anyway, " +
                "which may leave them pointing at the speakers until it reconnects");
        }

        /// <summary>
        /// Renders a named greeting for a caller who did not have one, so the
        /// NEXT call from them opens with their name.
        /// </summary>
        /// <remarks>
        /// Fire and forget, and deliberately after the call rather than before:
        /// rendering goes through the Live API and takes seconds, which is time a
        /// caller does not have. It is free — the Live API is unmetered on this
        /// project's tier, which is the whole reason the clip cache renders
        /// through it instead of through the 10-a-day TTS model.
        ///
        /// Silent when there is nothing to do, which is the common case: every
        /// contact already has a clip from --render-clips, and a caller with no
        /// usable name never gets one.
        /// </remarks>
        private void EnsureGreetingClip(string caller)
        {
            string line;
            try
            {
                if (!CallGreeting.NeedsRender(caller)) return;
                line = CallGreeting.LineFor(caller);
                if (line == null) return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[call] could not check the greeting clip: {ex.Message}");
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    Console.WriteLine(
                        $"[call] rendering a greeting for {CallGreeting.SpeakableName(caller)} " +
                        "so the next call opens with their name.");

                    await VoiceClipRenderer.RenderAsync(new[] { line }, CallGreeting.Voice)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Never fatal. The cost of failing is one more call greeted by
                    // the stock recording, and this runs on a pool thread where an
                    // escaping exception would take the process down instead.
                    Console.WriteLine($"[call] the greeting render failed: {ex.Message}");
                }
            });
        }

        private async Task PlayGreetingAsync(
            CallAudioBridge bridge, CallAudioRoute route, Stopwatch started, string caller)
        {
            // BY NAME WHEN WE HAVE ONE. A fixed recording is indistinguishable
            // from an answering machine, and a caller who thinks they reached
            // voicemail either talks past the assistant or hangs up. Their own
            // name in the first sentence says this is live and it knows who
            // they are.
            //
            // Falls back silently: a caller with no name, or a name nothing has
            // been rendered for yet, simply gets the stock file. There is no
            // rendering on this path — it costs the better part of ten seconds,
            // and the greeting is the thing covering the connect handshake.
            string named = CallGreeting.ClipFor(caller);
            string playing = named ?? greetingPath;

            // Deliberately not announced until the wait below has run: printing
            // "playing the greeting" first made the wait look like it happened
            // afterwards, which is the opposite of the order that matters.
            string greetingName = named != null
                ? "a greeting for " + CallGreeting.SpeakableName(caller)
                : Path.GetFileName(greetingPath);

            // The cap is on the WHOLE call, and a wrong file here — an hour of
            // audio, something that is not speech — would otherwise hold a stranger
            // on the line for all of it. Hanging up mid-greeting is the correct
            // failure.
            using (var cap = new CancellationTokenSource(maxCall - started.Elapsed))
            {
                try
                {
                    // A RUN-UP OF SILENCE BEFORE THE FIRST WORD.
                    //
                    // Measured on a real call (2026-08-17): the caller reliably
                    // heard the last two thirds of the greeting and never the
                    // start — about the first 1.8s of a 5.4s file, every time. The
                    // audio is written the instant the bridge reports itself up,
                    // but the path it is written into is not carrying yet: WasapiOut
                    // has been told to play rather than observed playing, and on
                    // route B the HFP link to the handset has only just been
                    // transferred. Whatever the exact split, the first word is
                    // spent on the path waking up.
                    //
                    // Silence costs nothing and is the only thing that can be lost
                    // harmlessly, so it goes first. Deliberately NOT a Task.Delay:
                    // the point is to have audio flowing through the cable while
                    // the link settles, not to wait and then start cold.
                    bool listening = await CallAudioBridge.WaitForCaptureConsumerAsync(
                        route.CableCaptureId, TimeSpan.FromSeconds(8), cap.Token).ConfigureAwait(false);

                    Console.WriteLine(listening
                        ? $"[call] the phone stack is reading the cable — playing {greetingName}"
                        : $"[call] nothing is reading the cable after 8s — playing {greetingName} " +
                          "anyway, the caller may miss the start");

                    // The run-up still goes out even once a listener is there:
                    // knowing the endpoint is open is not the same as knowing the
                    // link all the way to the handset is carrying, and silence is
                    // the only thing that can be lost harmlessly.
                    await bridge.SendSilenceAsync(greetingLeadIn, cap.Token).ConfigureAwait(false);

                    await bridge.SendWavAsync(playing, cap.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine(
                        $"[call] the greeting outran CallMaxSeconds ({maxCall.TotalSeconds:F0}s).");
                }
                catch (Exception ex)
                {
                    // Observed rather than ignored, and NOT fatal: a caller who
                    // hears no greeting but gets a conversation is far better off
                    // than one who is hung up on because a WAV was malformed.
                    Console.WriteLine($"[call] the greeting did not play: {ex.Message}");
                }
            }
        }

        // --- quieting the machine for the duration ---------------------------------

        // THE SPEAKERS ARE THE INBOUND LEG. Everything Windows plays through them
        // is captured by the loopback and sent to Gemini as though the caller had
        // said it, and Phone Link sends the same mix to the caller. So for the
        // length of a call:
        //
        //   * media is paused — and put back only if it was actually playing, so a
        //     call never starts music that Layth had deliberately stopped
        //   * PresenceGate is muted, so no prayer announcement, reminder, timer or
        //     suggestion can speak into a stranger's ear. The previous mute
        //     deadline is restored rather than cleared: if he had already asked for
        //     quiet until midnight, a call ending at nine must not undo that.
        //
        // PresenceGate ALSO reports busy for the duration (IsOnCall, wired in
        // Program), which covers everything that asks "is a conversation open?"
        // rather than looking at the mute.
        private async Task<Hush> HushAsync()
        {
            var hush = new Hush(presence, media);
            await hush.BeginAsync(maxCall).ConfigureAwait(false);
            return hush;
        }

        private sealed class Hush : IDisposable
        {
            private readonly PresenceGate presence;
            private readonly MediaController media;

            private DateTime? previousMute;
            private bool resumeMedia;
            private bool held;

            // THE INBOUND LEG IS A LOOPBACK ON THE SPEAKERS, so how loudly Windows
            // renders the caller IS the microphone level. Measured 2026-08-22: the
            // volume happened to sit at 23%, inbound arrived at 0.0005 against a
            // 0.05 target, and the bridge's 8x ceiling could not close a gap that
            // needed ~100x. The caller was audible but repeatedly misheard.
            //
            // Pinned for the duration and put back afterwards, rather than asking
            // anyone to remember: a level that depends on where a slider was left
            // is not a level, and this one is silently load-bearing.
            private readonly AudioController audio = new AudioController();
            private int? previousVolume;

            public Hush(PresenceGate presence, MediaController media)
            {
                this.presence = presence;
                this.media = media;
            }

            public async Task BeginAsync(TimeSpan maxCall)
            {
                RaiseSpeakers();

                if (presence != null)
                {
                    previousMute = presence.MutedUntil;
                    // A minute past the cap, so the hush cannot expire while the
                    // hang-up is still being clicked through.
                    presence.MuteFor(maxCall + TimeSpan.FromMinutes(1));
                    held = true;
                }

                if (media == null) return;

                try
                {
                    resumeMedia = await media.IsPlayingAsync().ConfigureAwait(false);
                    if (!resumeMedia) return;

                    Console.WriteLine("[call] pausing media for the call.");
                    await media.PauseAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // A call still works with music playing — it just leaks into
                    // the line. Not worth failing the call over.
                    Console.WriteLine($"[call] could not pause media: {ex.Message}");
                    resumeMedia = false;
                }
            }

            /// <summary>
            /// Brings the speakers up to a level the loopback can actually hear.
            /// Only ever RAISES — someone who already runs at 90% wants 90%, and
            /// quietly turning a machine down before a call would be its own bug.
            /// </summary>
            private void RaiseSpeakers()
            {
                int want = LaithConfig.Int("CallSpeakerVolume", 85, 0, 100);
                if (want == 0) return;   // explicitly disabled

                try
                {
                    int now = audio.CurrentVolumePercent();
                    if (now < 0 || now >= want) return;

                    previousVolume = now;
                    audio.SetVolume(want);
                    Console.WriteLine(
                        $"[call] speakers were at {now}% — raising to {want}% so the caller " +
                        "can be heard; will restore afterwards.");
                }
                catch (Exception ex)
                {
                    // A quiet call still works. Failing one over a volume slider
                    // would not be an improvement.
                    Console.WriteLine($"[call] could not raise the speakers: {ex.Message}");
                    previousVolume = null;
                }
            }

            public void Dispose()
            {
                if (previousVolume.HasValue)
                {
                    int back = previousVolume.Value;
                    previousVolume = null;
                    try
                    {
                        audio.SetVolume(back);
                        Console.WriteLine($"[call] speakers back to {back}%.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[call] could not put the volume back to {back}%: {ex.Message}");
                    }
                }

                if (held)
                {
                    try { presence.MuteUntil(previousMute); } catch { }
                    held = false;
                }

                if (!resumeMedia) return;
                resumeMedia = false;

                // Fire and forget: teardown may be running on the ProcessExit
                // budget, and putting music back is the least urgent thing here.
                _ = Task.Run(async () =>
                {
                    try { await media.PlayAsync().ConfigureAwait(false); }
                    catch (Exception ex) { Console.WriteLine($"[call] could not resume media: {ex.Message}"); }
                });
            }
        }

        // --- Setup ----------------------------------------------------------------

        // Next to the exe, like the voice clips and keyword.table, so it survives
        // both the bin\Debug and the deploy-folder layouts.
        private static string ResolveGreetingPath()
        {
            string configured = LaithConfig.Text("CallGreetingWav", string.Empty);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Path.IsPathRooted(configured)
                    ? configured
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configured);
            }

            return Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "callscreening", "greeting.wav");
        }

        public string GreetingPath => greetingPath;

        // Cached, because IsArmed is read by the poll twice a second and this is a
        // filesystem hit. Re-checked often enough that dropping a greeting in (or
        // losing one) is noticed within seconds rather than at the next restart.
        private DateTime greetingCheckedAt = DateTime.MinValue;
        private bool greetingThere;

        private bool GreetingPresent()
        {
            if (DateTime.Now - greetingCheckedAt < TimeSpan.FromSeconds(10)) return greetingThere;

            greetingCheckedAt = DateTime.Now;
            bool now;
            try { now = File.Exists(greetingPath); } catch { now = false; }

            // Said once per change, not twice a second — but said, because a
            // machine that looks armed and cannot actually speak to a caller is
            // exactly the failure this whole feature is supposed to prevent.
            if (now != greetingThere)
            {
                Console.WriteLine(now
                    ? "[call] the greeting recording is back — screening is live again."
                    : $"[call] NOT SCREENING: the greeting recording is missing ({greetingPath}). " +
                      "Calls will ring through to voicemail until it is restored.");
            }

            greetingThere = now;
            return now;
        }

        /// <summary>The screened calls on record, oldest first.</summary>
        public static IReadOnlyList<CallRecord> Log() => CallLogStore.Load();

        public void Dispose()
        {
            transport?.Dispose();
            widget?.Dispose();

            // AFTER the transport, which is only borrowing it. Disposing this
            // kills the Chrome tree, so doing it first would pull the browser out
            // from under a transport still trying to hang up a live call.
            gvBrowser?.Dispose();

            // A live conversation is stopped, not abandoned: the socket closes
            // tidily and the transcript is written. Hanging up is the teardown
            // hook's job (HangUpAnyCall) and putting the audio back is
            // CallAudioRouter.RestoreAll's — both registered in Program.
            Volatile.Read(ref live)?.Stop(CallEnding.Cancelled);
        }
    }

    // Where the arm state lives between runs. Same discipline as EventWatchStore
    // (EventWatch.cs:497) and TriggerStore: AppData, per-write temp plus
    // File.Replace, one save lock, and every failure non-fatal — an unreadable
    // file costs the arm, never the app.
    //
    // One value rather than a list, because Phase 2 has exactly one thing worth
    // outliving the process. The per-call record the plan calls CallLog is Phase 4
    // and gets its own store; it does not belong in the file that says whether the
    // assistant is currently allowed to pick up the phone.
    public static class CallScreeningStore
    {
        public static string Path
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("LAITH_CALLSCREENING_PATH");
                if (!string.IsNullOrWhiteSpace(over)) return over.Trim();

                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LAITH",
                    "callscreening.json");
            }
        }

        private static readonly object saveGate = new object();

        public static DateTime? LoadArmedUntil()
        {
            try
            {
                if (!File.Exists(Path)) return null;
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path)))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                    if (!doc.RootElement.TryGetProperty("armed_until", out JsonElement v)) return null;
                    if (v.ValueKind != JsonValueKind.String) return null;

                    return DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out DateTime parsed)
                        ? parsed
                        : (DateTime?)null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[call] could not read {Path}: {ex.Message}");
                return null;
            }
        }

        public static void Save(DateTime? armedUntil)
        {
            lock (saveGate)
            {
                string temp = null;
                try
                {
                    string path = Path;
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));

                    temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                    {
                        writer.WriteStartObject();
                        if (armedUntil.HasValue)
                        {
                            writer.WriteString("armed_until",
                                armedUntil.Value.ToString("o", CultureInfo.InvariantCulture));
                        }
                        writer.WriteEndObject();
                    }

                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                    temp = null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[call] could not save to {Path}: {ex.Message}");
                }
                finally
                {
                    if (temp != null)
                    {
                        try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    }
                }
            }
        }
    }
}
