using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Personal_Assistant.CallScreening
{
    // CallRoute, IncomingCall, CallLocation, AnswerOutcome and AnswerResult used
    // to be declared here. They moved to CallTransport.cs when Google Voice became
    // a second way for a call to reach this PC — they were never Phone Link's, only
    // Phone Link-shaped. Same namespace, so nothing else changed.

    // Answers, declines and hangs up Phone Link calls through UI Automation.
    //
    // Same technique as SMSControl.SendMessageToContact (SMSController.cs:126) —
    // FlaUI/UIA3 against the Phone Link process, every step verified, a missing
    // control meaning the step did NOT happen — with four differences that the
    // Phase 0 probe measured and that each cost a failed attempt to learn:
    //
    //  1. MATCH BY NAME, NOT BY AutomationId. All three toast actions share the id
    //     `VerbButton`, and every in-call transfer button has no id at all. A
    //     ByAutomationId lookup returns whichever one UIA reaches first — which
    //     is `Accept on PC` when the intent was `Decline`, or the reverse.
    //
    //  2. SCOPE TO THE CALL'S TOAST FIRST. Toasts stack. The verified capture had
    //     a Settings notification about Nearby Share sitting above the call, both
    //     inside one `New notification` window, both full of `VerbButton`s. So the
    //     view whose SenderName is `Calls` is located first and the buttons are
    //     searched inside THAT view.
    //
    //  3. THERE ARE TWO PhoneExperienceHost WINDOWS DURING A CALL, both named
    //     "Phone Link", both the same window class. The call lives in the second
    //     one; the first is the ordinary app window with its notification list.
    //     `Application.Attach(...).GetMainWindow(...)` — what SMSController does —
    //     can hand back the wrong one, and then `EndCallButton` "does not exist".
    //     Measured 2026-08-15; not in the plan, found in the probe dumps.
    //     FindCallWindow therefore enumerates the process's top-level windows and
    //     picks the one whose TitleTextBlock reads like a call.
    //
    //  4. HANGING UP IS TWO CLICKS. `EndCallButton` raises "Are you sure you want
    //     to end the call?" and a second button named `Yes, end call` has to be
    //     invoked. One click leaves the caller connected behind a dialog.
    //
    // Synchronous, like SendMessageToContact, and for the same reason: FlaUI is a
    // blocking API and interleaving it with awaits buys nothing. Callers run it on
    // a background thread (the trigger ticker dispatches its actions with
    // Task.Run, so that is already true).
    public sealed class PhoneLinkCallController
    {
        // --- The measured UI map. Every string here came out of a real call. -----

        private const string PhoneLinkProcess = "PhoneExperienceHost";

        // The toast host process is deliberately NOT matched on. It differs by
        // Windows build (ShellExperienceHost / ShellHost), it is started on demand
        // — ShellExperienceHost was not even running until the toast appeared —
        // and the window name is both cheaper to read and more specific anyway.

        private const string ToastWindowName = "New notification";
        private const string ToastViewClass = "FlexibleToastView";

        // Phone Link's window class, used to narrow the Win32 handle list before
        // UIA is involved at all. There is deliberately no equivalent for the
        // toast — see the block above ToastWindows for why it cannot have one.
        private const string CallWindowClass = "WinUIDesktopWin32WindowClass";

        private const string SenderNameId = "SenderName";
        private const string MessageTextId = "MessageText";
        private const string TitleId = "Title";
        private const string VerbButtonId = "VerbButton";

        private const string CallsSender = "Calls";
        private const string IncomingCallText = "Incoming Call";

        private const string AcceptOnPcAction = "Accept on PC";
        private const string UseMobileDeviceAction = "Use mobile device";
        private const string DeclineAction = "Decline";

        private const string TitleTextBlockId = "TitleTextBlock";
        private const string CallOnPcTitle = "Call on PC";
        private const string CallOnMobileTitle = "Call on mobile device";

        private const string EndCallButtonId = "EndCallButton";
        private const string ConfirmEndCallName = "Yes, end call";
        private const string TransferToPcName = "Transfer call to PC";

        // --- Budgets ------------------------------------------------------------
        //
        // Whole-step budgets, not per-control, exactly like SMSController's
        // ComposeBudget (SMSController.cs:106) — and bounded by the same ceiling,
        // the session's tool timeout, past which the model is told the tool failed
        // while the automation is still clicking things.

        // The toast is already on screen when we look (the watcher saw it), so
        // this only absorbs the gap between the poll that spotted it and the
        // trigger tick that acts on it.
        private static readonly TimeSpan ToastBudget = TimeSpan.FromSeconds(4);

        // Accept -> the window retitling to "Call on PC". Generous: this is a
        // radio handshake with a phone, not a local UI transition.
        private static readonly TimeSpan ConnectBudget = TimeSpan.FromSeconds(12);

        // Route B's transfer. The one step in the whole feature that has never
        // been observed to succeed, so it gets a real budget and a clean failure
        // rather than an optimistic assumption.
        private static readonly TimeSpan TransferBudget = TimeSpan.FromSeconds(10);

        // Both hang-up clicks plus confirmation that the call window went away.
        private static readonly TimeSpan HangUpBudget = TimeSpan.FromSeconds(6);

        // Optional, and NOT implemented in Phase 2: drops the Bluetooth headset
        // from the PC so route B's transfer has a chance of landing. Returns true
        // if the headset was actually disconnected.
        //
        // A seam rather than a stub because the two candidate mechanisms
        // (BluetoothSetServiceState against the handsfree GUID, or disabling the
        // device node) both need elevation testing against real AirPods, and an
        // untested P/Invoke that silently does nothing is worse than an absent one
        // — it makes the transfer look like the thing that failed.
        public Func<bool> DisconnectHeadset { get; set; }

        private static ConditionFactory Conditions => new ConditionFactory(new UIA3PropertyLibrary());

        // --- Detection -----------------------------------------------------------

        /// <summary>
        /// The call currently ringing, or null. Takes an automation so the watcher
        /// can poll on one long-lived instance instead of building a UIA3Automation
        /// — and its COM apartment — on every look.
        /// </summary>
        public static IncomingCall FindIncomingCall(UIA3Automation automation)
        {
            AutomationElement view = FindCallToastView(automation, out IncomingCall call);
            return view == null ? null : call;
        }

        // Locates the CALL's toast view and reads it. Returns the view itself so a
        // caller that means to click something searches inside it — see trap 2.
        private static AutomationElement FindCallToastView(
            UIA3Automation automation, out IncomingCall call)
        {
            call = null;
            ConditionFactory cf = Conditions;

            foreach (AutomationElement window in ToastWindows(automation))
            {
                AutomationElement[] views;
                try { views = window.FindAllDescendants(cf.ByClassName(ToastViewClass)); }
                catch { continue; }

                foreach (AutomationElement view in views)
                {
                    // Both, not either. `SenderName == Calls` alone would also
                    // match a missed-call notification, and screening a call that
                    // already rang out would answer nothing and hang up on silence.
                    if (!Same(TextOf(view, cf, SenderNameId), CallsSender)) continue;
                    if (!Same(TextOf(view, cf, MessageTextId), IncomingCallText)) continue;

                    call = new IncomingCall(TextOf(view, cf, TitleId), RouteOf(view, cf));
                    return view;
                }
            }

            return null;
        }

        // Reads which of the two first actions this toast is offering.
        private static CallRoute RouteOf(AutomationElement view, ConditionFactory cf)
        {
            List<string> names = ActionNames(view, cf);
            if (names.Any(n => Same(n, AcceptOnPcAction))) return CallRoute.AcceptOnPc;
            if (names.Any(n => Same(n, UseMobileDeviceAction))) return CallRoute.UseMobileDevice;

            // Named out loud rather than swallowed: if Phone Link ever renames
            // these, this line is the whole diagnosis.
            Console.WriteLine(
                "[call] toast actions are none of the ones I know: " +
                (names.Count == 0 ? "(none found)" : string.Join(" / ", names)));
            return CallRoute.Unknown;
        }

        private static List<string> ActionNames(AutomationElement view, ConditionFactory cf)
        {
            try
            {
                return view.FindAllDescendants(cf.ByAutomationId(VerbButtonId))
                    .Select(NameOf)
                    .Where(n => n.Length > 0)
                    .ToList();
            }
            catch { return new List<string>(); }
        }

        // --- Answering -----------------------------------------------------------

        /// <summary>
        /// Picks up the ringing call and gets its audio onto the laptop, or ends it
        /// cleanly if it cannot. Never returns OnPc without having SEEN the title
        /// say so — a greeting played into a call that is still on the handset is a
        /// greeting nobody hears.
        /// </summary>
        public AnswerResult Answer()
        {
            try
            {
                using (var automation = new UIA3Automation())
                {
                    AutomationElement view = FindCallToastView(automation, out IncomingCall call);
                    if (view == null)
                    {
                        // Ordinary, not an error: the caller gave up, or Layth
                        // answered it himself in the second between the poll and
                        // the tick.
                        return new AnswerResult(AnswerOutcome.NoToast, null,
                            "the call toast was gone by the time I looked");
                    }

                    Console.WriteLine($"[call] answering {call.Describe()}");
                    ConditionFactory cf = Conditions;

                    switch (call.Route)
                    {
                        case CallRoute.AcceptOnPc:
                            return AcceptOnPc(automation, cf, view, call);

                        case CallRoute.UseMobileDevice:
                            return UseMobileDeviceThenTransfer(automation, cf, view, call);

                        default:
                            // Deliberately does nothing. The alternative — clicking
                            // whatever button is in that slot — is how a call gets
                            // silently handed to the phone, and declining someone
                            // because a label changed is worse than letting it ring.
                            return new AnswerResult(AnswerOutcome.NoKnownAction, call,
                                "neither 'Accept on PC' nor 'Use mobile device' was on the toast");
                    }
                }
            }
            catch (Exception ex)
            {
                // Caught, but never mistaken for success — the same discipline as
                // SendMessageToContact. The caller decides what to say about it.
                return new AnswerResult(AnswerOutcome.Failed, null,
                    $"Phone Link automation failed: {ex.Message}");
            }
        }

        // Route A: no Bluetooth headset on the PC. One click, verified.
        private AnswerResult AcceptOnPc(
            UIA3Automation automation, ConditionFactory cf, AutomationElement view, IncomingCall call)
        {
            if (!InvokeAction(view, cf, AcceptOnPcAction))
            {
                return new AnswerResult(AnswerOutcome.Failed, call,
                    $"could not click '{AcceptOnPcAction}'");
            }

            if (WaitForLocation(automation, CallLocation.OnPc, ConnectBudget))
            {
                return new AnswerResult(AnswerOutcome.OnPc, call, "connected, audio on the PC");
            }

            // Clicked, and the title never agreed. Something IS connected (or was),
            // so leaving it be would strand a caller: end it the tidy way.
            string where = Describe(CurrentLocation(automation));
            HangUp();
            return new AnswerResult(AnswerOutcome.EndedTransferFailed, call,
                $"'{AcceptOnPcAction}' did not put the call on the PC ({where}) — hung up");
        }

        // Route B: a Bluetooth headset is connected to the PC, so Phone Link will
        // not take the audio. Answer on the handset first (this auto-accepts —
        // confirmed 2026-08-15), which stops the twenty-second ring clock, and only
        // then fight with the transfer.
        //
        // Step 3 has NEVER been observed to work. Phone Link's own error text reads
        // as instructions for exactly this sequence, which is why it is worth
        // trying, but nothing here assumes it succeeds.
        private AnswerResult UseMobileDeviceThenTransfer(
            UIA3Automation automation, ConditionFactory cf, AutomationElement view, IncomingCall call)
        {
            if (!InvokeAction(view, cf, UseMobileDeviceAction))
            {
                return new AnswerResult(AnswerOutcome.Failed, call,
                    $"could not click '{UseMobileDeviceAction}'");
            }

            // The handset picks up on its own; wait for the desktop window to
            // start tracking it before reaching for the transfer button.
            if (!WaitForAnyCall(automation, ConnectBudget))
            {
                return new AnswerResult(AnswerOutcome.Failed, call,
                    $"'{UseMobileDeviceAction}' was clicked but no call window appeared");
            }

            // Already where we want it. Reachable if the headset dropped off the
            // PC on its own between the toast being drawn and the click — Windows
            // and AirPods both re-pair and un-pair unprompted, which is why the
            // plan says to re-check at ring time.
            //
            // Worth its own branch because the next steps would be actively
            // harmful here: `Transfer call to PC` does not exist on a call that is
            // already on the PC (the button in that state is `Send call to mobile
            // device`), so the code below would hang up on a working call.
            if (CurrentLocation(automation) == CallLocation.OnPc)
            {
                return new AnswerResult(AnswerOutcome.OnPc, call,
                    "the handset answered and the audio was already on the PC");
            }

            // Not built in Phase 2 — see DisconnectHeadset. Said out loud because
            // the transfer below is then extremely likely to fail, and a silent
            // attempt would leave the failure looking like Phone Link's fault.
            if (DisconnectHeadset == null)
            {
                Console.WriteLine(
                    "[call] a Bluetooth headset is on the PC and nothing is wired to disconnect it — " +
                    "trying the transfer anyway, expecting it to fail");
            }
            else
            {
                bool dropped;
                try { dropped = DisconnectHeadset(); }
                catch (Exception ex)
                {
                    dropped = false;
                    Console.WriteLine($"[call] headset disconnect threw: {ex.Message}");
                }
                Console.WriteLine($"[call] headset disconnect {(dropped ? "succeeded" : "did not happen")}");
            }

            AutomationElement window = FindCallWindow(automation);
            if (window == null || !InvokeNamed(window, cf, TransferToPcName))
            {
                HangUp();
                return new AnswerResult(AnswerOutcome.EndedTransferFailed, call,
                    $"'{TransferToPcName}' was not there to click — hung up");
            }

            if (WaitForLocation(automation, CallLocation.OnPc, TransferBudget))
            {
                return new AnswerResult(AnswerOutcome.OnPc, call,
                    "connected via the handset, then transferred to the PC");
            }

            // The unverified path, failing the way it was expected to. Hanging up
            // is the honest outcome: the caller is connected to a phone in another
            // room with nobody at either end of it.
            HangUp();
            return new AnswerResult(AnswerOutcome.EndedTransferFailed, call,
                $"the call stayed on the handset after '{TransferToPcName}' — hung up");
        }

        /// <summary>Rejects the ringing call. Nothing is answered and nothing is said.</summary>
        public bool Decline()
        {
            try
            {
                using (var automation = new UIA3Automation())
                {
                    AutomationElement view = FindCallToastView(automation, out IncomingCall call);
                    if (view == null)
                    {
                        Console.WriteLine("[call] nothing ringing to decline.");
                        return false;
                    }

                    bool clicked = InvokeAction(view, Conditions, DeclineAction);
                    Console.WriteLine(clicked
                        ? $"[call] declined {call.Caller}."
                        : $"[call] could not click '{DeclineAction}'.");
                    return clicked;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[call] decline failed: {ex.Message}");
                return false;
            }
        }

        // --- Hanging up ----------------------------------------------------------

        /// <summary>
        /// Ends the connected call, through BOTH click steps, and confirms the call
        /// window actually went away. True also when there was no call to end.
        /// </summary>
        /// <param name="attempts">
        /// Retried by default. The confirmation flyout is the one control here that
        /// is genuinely racy — it animates in — and the cost of giving up too early
        /// is a real person left on an open line.
        /// </param>
        public bool HangUp(int attempts = 2)
        {
            for (int attempt = 1; attempt <= Math.Max(1, attempts); attempt++)
            {
                try
                {
                    using (var automation = new UIA3Automation())
                    {
                        if (CurrentLocation(automation) == CallLocation.None)
                        {
                            if (attempt > 1) Console.WriteLine("[call] call ended.");
                            return true;
                        }
                        if (HangUpOnce(automation)) return true;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[call] hang-up attempt {attempt} failed: {ex.Message}");
                }
            }

            Console.WriteLine(
                "[call] COULD NOT HANG UP — the call may still be connected. " +
                "End it from the Phone Link window.");
            return false;
        }

        private bool HangUpOnce(UIA3Automation automation)
        {
            ConditionFactory cf = Conditions;

            AutomationElement window = FindCallWindow(automation);
            if (window == null) return true; // gone between the check and here

            AutomationElement endCall = window.FindFirstDescendant(cf.ByAutomationId(EndCallButtonId));
            if (endCall == null)
            {
                Console.WriteLine($"[call] '{EndCallButtonId}' is not in the call window.");
                return false;
            }

            if (!Invoke(endCall, EndCallButtonId)) return false;
            Wait.UntilInputIsProcessed();

            // Click two. Without it the call stays up behind "Are you sure you want
            // to end the call?" — which looks, from the outside, exactly like a
            // hang-up that worked.
            DateTime deadline = DateTime.UtcNow + HangUpBudget;
            AutomationElement confirm = WaitFor(
                () => FindNamed(window, cf, ConfirmEndCallName), deadline);
            if (confirm == null)
            {
                // Informational, not alarming. Measured across every call on
                // 2026-08-17: this fires every time, and every time the call has in
                // fact ended — the confirmation only appears while the line is
                // still live, and by the time the assistant hangs up the caller has
                // usually gone first. The line below still checks that the call is
                // really down, so this stays a note rather than a warning. If the
                // call is ever found still up after this, THAT is the failure worth
                // shouting about.
                Console.WriteLine(
                    $"[call] no '{ConfirmEndCallName}' step was needed — the call had already ended.");
                return false;
            }
            if (!Invoke(confirm, ConfirmEndCallName)) return false;

            // Verified, not assumed. Both clicks landing and the call surviving is
            // a state worth knowing about.
            bool gone = WaitUntil(
                () => CurrentLocation(automation) == CallLocation.None, HangUpBudget);

            Console.WriteLine(gone ? "[call] hung up." : "[call] both clicks landed but the call is still up.");
            return gone;
        }

        // --- Call state ----------------------------------------------------------

        /// <summary>Where the audio of the connected call is, if there is one.</summary>
        public CallLocation CurrentLocation()
        {
            try
            {
                using (var automation = new UIA3Automation()) return CurrentLocation(automation);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[call] could not read the call state: {ex.Message}");
                return CallLocation.Unknown;
            }
        }

        private static CallLocation CurrentLocation(UIA3Automation automation)
        {
            string title = TitleOfCallWindow(automation, out _);
            if (title == null) return CallLocation.None;
            if (Same(title, CallOnPcTitle)) return CallLocation.OnPc;
            if (Same(title, CallOnMobileTitle)) return CallLocation.OnMobile;
            return CallLocation.Unknown;
        }

        private static bool WaitForLocation(
            UIA3Automation automation, CallLocation wanted, TimeSpan budget) =>
            WaitUntil(() => CurrentLocation(automation) == wanted, budget);

        private static bool WaitForAnyCall(UIA3Automation automation, TimeSpan budget) =>
            WaitUntil(() => CurrentLocation(automation) != CallLocation.None, budget);

        // 250ms rather than WaitFor's 200: each probe here is a whole window walk,
        // not one FindFirstDescendant, and nothing downstream is racing a deadline
        // this tight.
        private static bool WaitUntil(Func<bool> settled, TimeSpan budget)
        {
            DateTime deadline = DateTime.UtcNow + budget;
            do
            {
                if (settled()) return true;
                Thread.Sleep(250);
            }
            while (DateTime.UtcNow < deadline);
            return false;
        }

        // The call window, NOT the app window — see trap 3. Both are top-level,
        // both are called "Phone Link", and only one of them has a call in it.
        private static AutomationElement FindCallWindow(UIA3Automation automation)
        {
            TitleOfCallWindow(automation, out AutomationElement window);
            return window;
        }

        // One walk, two answers, because every caller wants both and the walk is
        // the expensive half.
        private static string TitleOfCallWindow(
            UIA3Automation automation, out AutomationElement window)
        {
            window = null;
            ConditionFactory cf = Conditions;

            foreach (AutomationElement candidate in PhoneLinkWindows(automation))
            {
                AutomationElement titleBlock;
                try { titleBlock = candidate.FindFirstDescendant(cf.ByAutomationId(TitleTextBlockId)); }
                catch { continue; }
                if (titleBlock == null) continue;

                // The app window's TitleTextBlock reads "Phone Link"; a call window's
                // reads "Call on PC" or "Call on mobile device". Matched on the
                // prefix so an unrecognised variant still resolves to the right
                // WINDOW and gets reported as Unknown, rather than being invisible.
                string title = NameOf(titleBlock);
                if (title.StartsWith("Call", StringComparison.OrdinalIgnoreCase))
                {
                    window = candidate;
                    return title;
                }
            }

            return null;
        }

        // --- UIA plumbing --------------------------------------------------------

        // --- Finding the two windows, which need two different techniques --------
        //
        // Measured on this box 2026-08-16, against a real toast:
        //
        //     GetDesktop().FindAllChildren()                 310-430 ms (13 windows)
        //     ...FindAllChildren(ByName("New notification")) 761 ms
        //     ...FindAllChildren(ByProcessId(shell hosts))  1383 ms
        //     Win32 EnumWindows over all 326 windows           0 ms
        //     automation.FromHandle + one FindFirstDescendant 10-26 ms
        //
        // A UIA condition buys nothing: UIA evaluates it by reading a property off
        // every top-level window on the desktop, one cross-process COM call each,
        // and some of them are very slow to answer. The bare enumeration is the
        // cheapest UIA primitive there is, and it is still a third of a second.
        //
        // THE TOAST CANNOT USE THE WIN32 SHORTCUT. Measured the hard way: with a
        // toast on screen, UIA reports the window as ShellExperienceHost /
        // 'New notification' / CoreWindow — and its native handle IS NOT IN THE
        // EnumWindows LIST. It is a UIA desktop child that is not a Win32 top-level
        // window, so EnumWindows-then-FromHandle finds precisely nothing, forever,
        // with no error to notice. (A first cut of this file did exactly that and
        // passed every idle test.)
        //
        // The Phone Link call window is an ordinary desktop window and IS in the
        // list, so it gets the fast path — with the slow path behind it, because
        // "no call window" is the answer that decides whether to keep trying to
        // hang up on somebody.

        // Toast host windows: the slow, correct way. The name filter is applied in
        // managed code because ByName costs MORE than reading the property here.
        private static IEnumerable<AutomationElement> ToastWindows(UIA3Automation automation)
        {
            var found = new List<AutomationElement>();
            foreach (AutomationElement window in DesktopChildren(automation))
            {
                if (Same(NameOf(window), ToastWindowName)) found.Add(window);
            }
            return found;
        }

        // Phone Link's top-level windows — the app window and, during a call, the
        // separate call window (trap 3).
        private static IEnumerable<AutomationElement> PhoneLinkWindows(UIA3Automation automation)
        {
            var found = new List<AutomationElement>();

            var wanted = new HashSet<int>(ProcessIds(new[] { PhoneLinkProcess }));
            if (wanted.Count == 0) return found; // Phone Link is not running

            foreach (IntPtr hwnd in TopLevelWindows())
            {
                if (GetWindowThreadProcessId(hwnd, out int pid) == 0) continue;
                if (!wanted.Contains(pid)) continue;
                if (!Same(ClassOf(hwnd), CallWindowClass)) continue;

                try
                {
                    AutomationElement element = automation.FromHandle(hwnd);
                    if (element != null) found.Add(element);
                }
                catch
                {
                    // The window closed between the enumeration and here — normal
                    // while a call is ending.
                }
            }

            if (found.Count > 0) return found;

            // Nothing in Win32. Either Phone Link genuinely has no window, or this
            // one is a UIA-only surface the way the toast is. Pay the slow scan
            // before concluding there is no call to hang up.
            foreach (AutomationElement window in DesktopChildren(automation))
            {
                int pid;
                try { pid = window.Properties.ProcessId.ValueOrDefault; } catch { continue; }
                if (wanted.Contains(pid)) found.Add(window);
            }
            return found;
        }

        private static IEnumerable<AutomationElement> DesktopChildren(UIA3Automation automation)
        {
            try { return automation.GetDesktop().FindAllChildren(); }
            catch (Exception ex)
            {
                Console.WriteLine($"[call] could not enumerate the desktop: {ex.Message}");
                return new AutomationElement[0];
            }
        }

        private static IEnumerable<int> ProcessIds(IReadOnlyList<string> names)
        {
            var ids = new List<int>();
            foreach (string name in names)
            {
                Process[] processes;
                try { processes = Process.GetProcessesByName(name); }
                catch { continue; }

                foreach (Process p in processes)
                {
                    using (p) ids.Add(p.Id);
                }
            }
            return ids;
        }

        private static List<IntPtr> TopLevelWindows()
        {
            var handles = new List<IntPtr>();
            try
            {
                EnumWindows((hwnd, _) => { handles.Add(hwnd); return true; }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[call] could not enumerate windows: {ex.Message}");
            }
            return handles;
        }

        private static string ClassOf(IntPtr hwnd)
        {
            var buffer = new StringBuilder(256);
            return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);

        // Clicks a toast action BY NAME, inside the given view — trap 1 and trap 2
        // in one method. Nothing else in this file may click a VerbButton.
        private static bool InvokeAction(AutomationElement view, ConditionFactory cf, string actionName)
        {
            AutomationElement button = WaitFor(
                () => FindVerbButton(view, cf, actionName),
                DateTime.UtcNow + ToastBudget);

            if (button == null)
            {
                Console.WriteLine($"[call] no toast action named '{actionName}'.");
                return false;
            }
            return Invoke(button, actionName);
        }

        private static AutomationElement FindVerbButton(
            AutomationElement view, ConditionFactory cf, string actionName)
        {
            try
            {
                return view.FindAllDescendants(cf.ByAutomationId(VerbButtonId))
                    .FirstOrDefault(b => Same(NameOf(b), actionName));
            }
            catch { return null; }
        }

        // The in-call transfer buttons have no AutomationId at all, so name is the
        // only handle there is.
        private static bool InvokeNamed(AutomationElement scope, ConditionFactory cf, string name)
        {
            AutomationElement button = WaitFor(
                () => FindNamed(scope, cf, name), DateTime.UtcNow + ToastBudget);
            if (button == null)
            {
                Console.WriteLine($"[call] no button named '{name}'.");
                return false;
            }
            return Invoke(button, name);
        }

        private static AutomationElement FindNamed(
            AutomationElement scope, ConditionFactory cf, string name)
        {
            try { return scope.FindFirstDescendant(cf.ByName(name)); }
            catch { return null; }
        }

        private static bool Invoke(AutomationElement element, string label)
        {
            try
            {
                element.AsButton().Invoke();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[call] '{label}' would not invoke: {ex.Message}");
                return false;
            }
        }

        // Polls for an element instead of taking one shot at it, exactly like
        // SMSController.WaitForElement (SMSController.cs:110): these surfaces
        // animate in, and one FindFirstDescendant at the wrong moment legitimately
        // misses. Null on timeout; every call site treats that as a hard failure.
        private static AutomationElement WaitFor(Func<AutomationElement> find, DateTime deadline)
        {
            do
            {
                AutomationElement found = find();
                if (found != null) return found;
                Thread.Sleep(200);
            }
            while (DateTime.UtcNow < deadline);
            return null;
        }

        private static string TextOf(AutomationElement scope, ConditionFactory cf, string automationId)
        {
            try
            {
                AutomationElement el = scope.FindFirstDescendant(cf.ByAutomationId(automationId));
                return el == null ? string.Empty : NameOf(el);
            }
            catch { return string.Empty; }
        }

        // Every property read here is a cross-process COM call against a UI that
        // may be tearing down mid-read, so none of them is allowed to throw.
        private static string NameOf(AutomationElement element)
        {
            try { return (element.Properties.Name.ValueOrDefault ?? string.Empty).Trim(); }
            catch { return string.Empty; }
        }

        private static bool Same(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        public static string Describe(CallLocation location)
        {
            switch (location)
            {
                case CallLocation.OnPc: return "on the PC";
                case CallLocation.OnMobile: return "on the handset";
                case CallLocation.None: return "no call";
                default: return "an unrecognised call state";
            }
        }
    }
}
