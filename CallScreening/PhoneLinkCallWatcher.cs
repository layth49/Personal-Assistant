using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Personal_Assistant.Triggers;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Personal_Assistant.CallScreening
{
    // Notices that the phone is ringing.
    //
    // Wired exactly like VoiceTriggers.BindFileWatcher (VoiceTriggers.cs:589):
    // TriggerService.AddSignal registers what to DO, and a source outside the
    // ticker calls Signal to say WHEN. Routing through the trigger engine rather
    // than invoking the action directly is what keeps one screened call from
    // becoming three when the poll sees the same toast three times, and keeps the
    // firing rules in one place.
    //
    // Two deliberate differences from every other trigger in the app:
    //
    //   requiresPresence: false — this is the one feature whose whole point is to
    //   fire while nobody is there. The presence gate exists to stop the assistant
    //   TALKING to an empty room; screening does not talk to the room, it talks to
    //   a caller, and gating it would mean it only ever works when it is not
    //   needed. respectQuietHours is false for the same reason: a phone that rings
    //   at 2am is exactly the call worth screening.
    //
    //   It polls, and it also listens. Both, because neither is sufficient:
    //
    //     * A UIA WindowOpened event on the desktop DOES fire for the toast —
    //       measured 2026-08-16 against a real one: exactly one event, correctly
    //       identified as ShellExperienceHost / 'New notification', no noise from
    //       anything else in twenty seconds. That is the fast path.
    //     * But toasts STACK INTO AN EXISTING WINDOW. The verified Phone Link
    //       capture had the call sharing one 'New notification' window with a
    //       Settings notification, and if that window is already open when the
    //       phone rings, no window opens and no event fires. So the poll is not a
    //       belt-and-braces backstop; it is the only thing that catches that case.
    //
    //   The poll is slow on purpose — see PhoneLinkCallController for why a look
    //   costs a third of a second — and a phone rings for about twenty, so a
    //   two-second cadence still gets ten chances at it.
    public sealed class PhoneLinkCallWatcher : IDisposable
    {
        public const string TriggerName = CallTriggers.Incoming;

        private readonly TriggerService triggers;
        private readonly Func<bool> isArmed;
        private readonly TimeSpan pollInterval;

        // Set by the poll thread, taken by the trigger action. Handing over data
        // rather than a UIA element is the point — see IncomingCall.
        private IncomingCall pending;

        // Edge latch. A phone rings for about twenty seconds, so at any sane poll
        // rate the same toast is seen thirty times; only its ARRIVAL is an event.
        // TriggerService's minInterval would mostly cover this, but the latch is
        // what makes the console log readable and what lets a second call, right
        // after the first, still register.
        private bool toastWasUp;

        private Thread thread;
        private volatile bool stopping;

        // Raised by the WindowOpened handler; waited on instead of sleeping, so a
        // toast that opens its own window is looked at within milliseconds rather
        // than at the next poll.
        private readonly ManualResetEventSlim wakeup = new ManualResetEventSlim(false);

        // Logged once per spell of failure rather than twice a second. UIA throws
        // routinely when a window closes mid-walk, and a screenful of identical
        // exceptions hides the one line that matters.
        private string lastFailure;

        public PhoneLinkCallWatcher(
            TriggerService triggers,
            Func<bool> isArmed,
            Func<IncomingCall, Task> onIncomingCall,
            TimeSpan? pollInterval = null)
        {
            this.triggers = triggers ?? throw new ArgumentNullException(nameof(triggers));
            this.isArmed = isArmed ?? throw new ArgumentNullException(nameof(isArmed));
            if (onIncomingCall == null) throw new ArgumentNullException(nameof(onIncomingCall));
            this.pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);

            triggers.AddSignal(
                TriggerName,
                async () =>
                {
                    IncomingCall call = Interlocked.Exchange(ref pending, null);
                    // Nothing to answer: the signal was latched, held, and by the
                    // time the ticker got to it the call had already been dealt
                    // with. Better than answering a call that stopped ringing.
                    if (call == null) return;
                    await onIncomingCall(call).ConfigureAwait(false);
                },
                // Two rings inside five seconds is one call, not two — Phone Link
                // re-raises the toast when it is dismissed and re-shown.
                minInterval: TimeSpan.FromSeconds(5),
                // A ring lasts ~20s. A signal still waiting to run after ten is
                // about a call that has already stopped ringing, and answering it
                // then is worse than missing it.
                grace: TimeSpan.FromSeconds(10),
                respectQuietHours: false,
                requiresPresence: false);
        }

        /// <summary>
        /// Starts polling. Cheap while disarmed — it does no UI Automation work at
        /// all until <c>isArmed</c> says yes.
        /// </summary>
        public void Start()
        {
            if (thread != null) return;

            thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "call-toast-poll"
            };

            // One thread, one apartment, one UIA3Automation for its whole life.
            // The alternative — a System.Threading.Timer — hands each tick to a
            // different thread-pool thread, and marshalling the same UIA COM
            // objects across those is how this kind of poller starts throwing
            // RPC_E_* an hour into a run. MTA because that is what UIA3 wants.
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        private void Loop()
        {
            try
            {
                using (var automation = new UIA3Automation())
                {
                    IDisposable subscription = SubscribeToWindowOpened(automation);
                    try
                    {
                        while (!stopping)
                        {
                            Tick(automation);

                            // Woken early by a window opening, or by the poll
                            // interval, whichever comes first. Disarmed costs
                            // nothing but a DateTime comparison once a second.
                            wakeup.Wait(Armed() ? pollInterval : TimeSpan.FromSeconds(1));
                            if (!wakeup.IsSet) continue;

                            wakeup.Reset();
                            // A window that has just opened has not necessarily
                            // filled in yet; the toast's text arrives a beat after
                            // the frame does. Missing it here would not lose the
                            // call — the poll would catch it — but it would spend
                            // the fast path for nothing.
                            Thread.Sleep(150);
                        }
                    }
                    finally
                    {
                        try { subscription?.Dispose(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                // The thread dying takes call screening with it, silently, for the
                // rest of the process's life. Nothing in Tick should escape; if
                // something does, say so rather than leaving a feature that looks
                // armed and is not.
                Console.WriteLine($"[call] the ring watcher stopped: {ex.Message}");
            }
        }

        // Wakes the loop whenever any top-level window opens while screening is
        // armed. Deliberately reads NOTHING off the element: this runs on a UIA
        // callback thread, where a slow cross-process property read blocks the
        // provider, and the loop is about to do the real look anyway.
        //
        // Returns null if the subscription cannot be made — the poll alone is
        // still correct, just slower, so this is a downgrade and not a failure.
        private IDisposable SubscribeToWindowOpened(UIA3Automation automation)
        {
            try
            {
                return automation.GetDesktop().RegisterAutomationEvent(
                    automation.EventLibrary.Window.WindowOpenedEvent,
                    TreeScope.Children,
                    (element, id) => { if (Armed()) wakeup.Set(); });
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[call] no window-opened subscription ({ex.Message}) — polling only.");
                return null;
            }
        }

        private void Tick(UIA3Automation automation)
        {
            if (!Armed())
            {
                // Disarming mid-ring resets the edge, so re-arming during the same
                // call still signals rather than silently swallowing it.
                toastWasUp = false;
                return;
            }

            IncomingCall call;
            try
            {
                call = PhoneLinkCallController.FindIncomingCall(automation);
                lastFailure = null;
            }
            catch (Exception ex)
            {
                string failure = ex.GetType().Name + ": " + ex.Message;
                if (failure != lastFailure)
                {
                    lastFailure = failure;
                    Console.WriteLine($"[call] could not read the notification tree — {failure}");
                }
                return;
            }

            if (call == null)
            {
                toastWasUp = false;
                return;
            }

            if (toastWasUp) return; // same ring, already signalled
            toastWasUp = true;

            Console.WriteLine($"[call] incoming: {call.Describe()}");
            Volatile.Write(ref pending, call);
            if (!triggers.Signal(TriggerName))
            {
                // Registration happens in the constructor, so this can only mean
                // something removed the trigger underneath us.
                Console.WriteLine($"[call] no '{TriggerName}' trigger is registered — nothing will answer.");
            }
        }

        private bool Armed()
        {
            try { return isArmed(); }
            catch (Exception ex)
            {
                // A broken arm check must not be read as "armed". Answering a
                // stranger's call because a predicate threw is the wrong way round.
                Console.WriteLine($"[call] arm check threw, treating screening as off: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            stopping = true;
            try { wakeup.Set(); } catch { }
            // Not joined. The thread is a background thread on a one-second worst
            // case, and blocking process teardown behind a UIA call that may
            // itself be stuck is the trade the ProcessExit budget cannot afford.
            thread = null;
        }
    }
}
