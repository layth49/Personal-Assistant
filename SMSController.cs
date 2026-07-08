using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Personal_Assistant.SpeechManager;
using Python.Runtime;
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
        private readonly SpeechService speechManager = new SpeechService();

        public async Task SendSMS(string contactName, string contactNumber)
        {
            try
            {
                Process.Start(new ProcessStartInfo("powershell",
                    "start shell:AppsFolder\\Microsoft.YourPhone_8wekyb3d8bbwe!App")
                {
                    UseShellExecute = true
                });

                FocusPhoneLink();

                const int maxAttempts = 3;
                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    await speechManager.Say(Program.recognizedText, $"Okay! What would you like to send {contactName}?");

                    string userText = await speechManager.RecognizeOnceAsync();

                    if (userText.IndexOf("Introduce yourself", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        FocusPhoneLink();
                        SendMessageToContact(contactNumber,
                            "Hello! This was sent by L.A.I.T.H.49, AKA Layth's Logical Assistant for Intelligent Task Handling 49!");

                        await speechManager.Say(userText, $"Okay! Introducing myself to {contactName}.");
                        return;
                    }

                    await speechManager.Say(userText, $"You'd like to send \"{userText}\" to {contactName}. Is that correct?");

                    string confirmation = await speechManager.RecognizeOnceAsync();

                    if (confirmation.IndexOf("no", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        await speechManager.Say(confirmation, "Okay, message cancelled.");
                        continue;
                    }

                    FocusPhoneLink();
                    SendMessageToContact(contactNumber, userText);

                    await speechManager.Say(userText, $"Sending \"{userText}\" to {contactName}.");
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
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
