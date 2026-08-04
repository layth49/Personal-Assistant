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
using WindowsInput;


namespace Personal_Assistant.SMSController
{
    public class SMSControl
    {
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        private readonly InputSimulator simulator = new InputSimulator();

        // The canned message behind "text her to introduce yourself".
        private const string IntroductionText =
            "Hello! This was sent by L.A.I.T.H.49, AKA Layth's Logical Assistant for Intelligent Task Handling 49!";

        // Sends `message` to `contactNumber` through Phone Link. Returns whether it
        // was actually handed off.
        //
        // This used to dictate the body itself: it spoke "what would you like to
        // send?", opened its own recognizer, read back, and opened another. All of
        // that is gone — the body and the user's approval now arrive as tool
        // arguments (Program.HandleSendSmsAsync), because a handler holding the
        // microphone deadlocks a Gemini Live session, and because the recognizer it
        // was trusting is what returned "" and sent a real empty text to a real
        // number.
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

            if (message.IndexOf("introduce yourself", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                message = IntroductionText;
            }

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
                // an unconditional `return true`, so a Phone Link window that never
                // appeared still reported success and the assistant told Layth it
                // had sent a text that does not exist.
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

        // How long to wait for each Phone Link control to appear. The window is
        // launched ~1.5s earlier and populates progressively, so a single
        // FindFirstDescendant right after launch legitimately misses — which is
        // what produced the null message box.
        private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(10);

        // Polls for an element instead of taking one shot at it. Returns null on
        // timeout; every call site treats that as a hard failure.
        private static AutomationElement WaitForElement(
            Window window, ConditionFactory cf, string automationId, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
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
                    // Each step is required. Previously the first two were `?.` and
                    // the third was a bare dereference, so a compose click that
                    // never landed walked silently into a NullReferenceException on
                    // the message box — reporting the failure two steps after the
                    // one that actually broke.

                    // 1. Find and click the Compose button
                    var composeButton = WaitForElement(
                        window, conditionFactory, "NewMessageButton", ElementTimeout)?.AsButton();
                    if (composeButton == null)
                    {
                        Console.WriteLine("[sms] compose button never appeared — nothing was sent.");
                        return false;
                    }
                    composeButton.Invoke();
                    Wait.UntilInputIsProcessed(); // Let the UI catch up

                    // 2. Find the "To" field, type number, press Enter
                    var toField = WaitForElement(
                        window, conditionFactory, "TextBox", ElementTimeout)?.AsTextBox();
                    if (toField == null)
                    {
                        Console.WriteLine("[sms] recipient box never appeared — nothing was sent.");
                        return false;
                    }
                    toField.Enter(contactNumber);
                    Keyboard.Press(VirtualKeyShort.ENTER);
                    Wait.UntilInputIsProcessed();

                    // 3. Find the message box, type message, press Enter
                    var messageBox = WaitForElement(
                        window, conditionFactory, "InputTextBox", ElementTimeout)?.AsTextBox();
                    if (messageBox == null)
                    {
                        Console.WriteLine("[sms] message box never appeared — nothing was sent.");
                        return false;
                    }
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
