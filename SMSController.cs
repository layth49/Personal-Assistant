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

        // The one running instance — never `new SpeechService()`. This handler is
        // where that bug did its worst damage: its own instance waited on a
        // microphone it didn't own, so RecognizeOnceAsync below returned "" and
        // an empty message was handed to Phone Link and actually sent, while the
        // prompts it spoke escaped the echo gate and queued up as fresh turns.
        // A property, not a field, so there is no construction-order trap.
        private static SpeechService speechManager => SpeechService.Current;

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

                SendMessageToContact(contactNumber, message);
                return true;
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

        public void SendMessageToContact(string contactNumber, string message)
        {
            // Public, so it gets its own check rather than inheriting the caller's.
            if (string.IsNullOrWhiteSpace(message))
            {
                Console.WriteLine("[sms] refusing to send an empty message.");
                return;
            }

            Console.WriteLine($"Sending message to {contactNumber}: {message}");

            // Phone Link usually runs under this process name
            var processes = Process.GetProcessesByName("PhoneExperienceHost");
            if (processes.Length == 0)
            {
                Console.WriteLine("Phone Link is not running.");
                return;
            }

            using (var automation = new UIA3Automation())
            {
                var app = Application.Attach(processes[0].Id);
                var window = app.GetMainWindow(automation);
                var conditionFactory = new ConditionFactory(new UIA3PropertyLibrary());

                try
                {
                    // 1. Find and click the Compose button
                    var composeButton = window.FindFirstDescendant(conditionFactory.ByAutomationId("NewMessageButton"))?.AsButton();
                    composeButton?.Invoke();
                    Wait.UntilInputIsProcessed(); // Let the UI catch up
                    Console.WriteLine("Shouldve pressed the new message by now");

                    // 2. Find the "To" field, type number, press Enter
                    var toField = window.FindFirstDescendant(conditionFactory.ByAutomationId("TextBox"))?.AsTextBox();
                    toField?.Enter(contactNumber);
                    Keyboard.Press(VirtualKeyShort.ENTER);
                    Wait.UntilInputIsProcessed();
                    Console.WriteLine("Shouldve pressed on the 'To' box");

                    // 3. Find the message box, type message, press Enter
                    var messageBox = window.FindFirstDescendant(conditionFactory.ByAutomationId("InputTextBox"))?.AsTextBox();
                    messageBox.Text = message;
                    Keyboard.Press(VirtualKeyShort.ENTER);

                    Console.WriteLine("Message sent successfully via FlaUI.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FlaUI Automation Error: {ex.Message}");
                }
            }
        }
    }
}
