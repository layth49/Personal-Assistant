using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;


namespace Personal_Assistant.SMSController
{
    public class SMSControl
    {
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        // The canned message behind "text her to introduce yourself".
        //
        // Public because the substitution has to happen ABOVE the confirmation
        // gate, in Program.HandleSendSmsAsync, not here. See IsIntroductionRequest
        // there — swapping the body at this depth meant the user approved one
        // message and a different one was sent.
        public const string IntroductionText =
            "Hello! This was sent by L.A.I.T.H.49, AKA Layth's Logical Assistant for Intelligent Task Handling 49!";

        // Sends `message` to `contactNumber` through Phone Link. Returns whether it
        // was actually handed off.
        //
        // This used to dictate the body itself: it spoke "what would you like to
        // send?", opened its own recognizer, read the answer back, opened another,
        // and looped three times. All of that is gone — the body and the user's
        // approval now arrive from Program.HandleSendSmsAsync, which owns the turn
        // and talks to the one listener that is actually running. The recognizer
        // this class was trusting is what returned "" and sent a real empty text to
        // a real number, because the SpeechService it had built for itself was not
        // the one holding the microphone.
        //
        // The empty-body check below is deliberately redundant with the caller's.
        // It is the last thing standing between a bad string and a real phone, and
        // it costs nothing.
        public async Task<bool> SendSMS(string contactName, string contactNumber, string message)
        {
            message = (message ?? string.Empty).Trim();
            if (message.Length == 0)
            {
                Console.WriteLine($"[sms] refusing to send an empty message to {contactName}.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(contactNumber))
            {
                Console.WriteLine($"[sms] no number for {contactName}.");
                return false;
            }

            // No body rewriting here, deliberately. Whatever reaches this method
            // is what the user was read back and said yes to, and the last place
            // a message may change is above that read-back — see
            // Program.HandleSendSmsAsync.

            try
            {
                Process.Start(new ProcessStartInfo("powershell",
                    "start shell:AppsFolder\\Microsoft.YourPhone_8wekyb3d8bbwe!App")
                {
                    UseShellExecute = true
                });

                // Phone Link needs a moment to come up before it can be focused;
                // the dictation prompt used to provide that delay for free.
                await Task.Delay(1500);
                FocusPhoneLink();

                // Propagated, not ignored. This used to be a void call followed by
                // a bare return, so a Phone Link window that never appeared still
                // reported success and the assistant told Layth it had sent a text
                // that does not exist.
                return SendMessageToContact(contactNumber, message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                return false;
            }
        }

        private static void FocusPhoneLink()
        {
            foreach (var p in Process.GetProcessesByName("Phone Link"))
            {
                if (SetForegroundWindow(p.MainWindowHandle)) break;
            }
        }

        // Budget for the WHOLE compose flow, not per control. The window is
        // launched ~1.5s earlier and populates progressively, so one
        // FindFirstDescendant right after launch legitimately misses — but a
        // per-control timeout across three controls compounds, and the whole time
        // it is running the assistant is stuck mid-turn with the conversation
        // window ticking down.
        private static readonly TimeSpan ComposeBudget = TimeSpan.FromSeconds(8);

        // Polls for an element instead of taking one shot at it. Returns null on
        // timeout; every call site treats that as a hard failure.
        private static AutomationElement WaitForElement(
            Window window, ConditionFactory cf, string automationId, DateTime deadline)
        {
            do
            {
                var found = window.FindFirstDescendant(cf.ByAutomationId(automationId));
                if (found != null) return found;
                System.Threading.Thread.Sleep(200);
            }
            while (DateTime.UtcNow < deadline);
            return null;
        }

        // Returns whether the message actually reached Phone Link's send box.
        // Every step is verified: a missing control means the send did NOT happen,
        // and saying so is the whole point — the caller reports this to the user.
        public bool SendMessageToContact(string contactNumber, string message)
        {
            // Public, so it gets its own check rather than inheriting the caller's.
            if (string.IsNullOrWhiteSpace(message))
            {
                Console.WriteLine("[sms] refusing to send an empty message.");
                return false;
            }

            Console.WriteLine($"Sending message to {contactNumber}: {message}");

            // Phone Link usually runs under this process name
            var processes = Process.GetProcessesByName("PhoneExperienceHost");
            if (processes.Length == 0)
            {
                Console.WriteLine("[sms] Phone Link is not running — nothing was sent.");
                return false;
            }

            using (var automation = new UIA3Automation())
            {
                var app = Application.Attach(processes[0].Id);
                var window = app.GetMainWindow(automation);
                var conditionFactory = new ConditionFactory(new UIA3PropertyLibrary());

                try
                {
                    // Every step is required, and a missing control means the send
                    // did NOT happen. Previously the first two were `?.` and the
                    // third a bare dereference, so a compose click that never landed
                    // walked silently into a NullReferenceException on the message
                    // box — blaming a step two later than the one that broke.
                    DateTime deadline = DateTime.UtcNow + ComposeBudget;

                    AutomationElement Require(string automationId, string label)
                    {
                        var found = WaitForElement(window, conditionFactory, automationId, deadline);
                        if (found == null)
                        {
                            Console.WriteLine($"[sms] {label} never appeared — nothing was sent.");
                        }
                        return found;
                    }

                    var composeButton = Require("NewMessageButton", "compose button")?.AsButton();
                    if (composeButton == null) return false;
                    composeButton.Invoke();
                    Wait.UntilInputIsProcessed(); // Let the UI catch up

                    var toField = Require("TextBox", "recipient box")?.AsTextBox();
                    if (toField == null) return false;
                    toField.Enter(contactNumber);
                    Keyboard.Press(VirtualKeyShort.ENTER);
                    Wait.UntilInputIsProcessed();

                    var messageBox = Require("InputTextBox", "message box")?.AsTextBox();
                    if (messageBox == null) return false;
                    messageBox.Text = message;
                    Keyboard.Press(VirtualKeyShort.ENTER);

                    Console.WriteLine($"[sms] handed to Phone Link for {contactNumber}.");
                    return true;
                }
                catch (Exception ex)
                {
                    // Caught rather than thrown, but no longer mistaken for success.
                    Console.WriteLine($"[sms] Phone Link automation failed: {ex.Message}");
                    return false;
                }
            }
        }
    }
}
