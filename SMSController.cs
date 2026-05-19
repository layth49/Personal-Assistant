using Microsoft.CognitiveServices.Speech;
using Personal_Assistant.SpeechManager;
using Python.Runtime;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WindowsInput;


namespace Personal_Assistant.SMSController
{
    class SMSControl
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

                    string userText = await RecognizeOnceAsync();

                    if (userText.IndexOf("Introduce yourself", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        FocusPhoneLink();
                        SendMessageToContact(contactNumber,
                            "Hello! This was sent by L.A.I.T.H.49, AKA Layth's Logical Assistant for Intelligent Task Handling 49!");

                        await speechManager.Say(userText, $"Okay! Introducing myself to {contactName}.");
                        return;
                    }

                    await speechManager.Say(userText, $"You'd like to send \"{userText}\" to {contactName}. Is that correct?");

                    string confirmation = await RecognizeOnceAsync();

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

        private async Task<string> RecognizeOnceAsync()
        {
            using (var recognizer = new SpeechRecognizer(speechManager.speechConfig))
            {
                SpeechRecognitionResult result = await recognizer.RecognizeOnceAsync();
                speechManager.ConvertSpeechToText(result);
                return result.Text ?? string.Empty;
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
            using (Py.GIL())
            {
                Console.WriteLine($"Sending message to {contactNumber}: {message}");
                try
                {
                    dynamic smsModule = Py.Import("SMSService");
                    smsModule.smsService(contactNumber, message);
                }
                catch (PythonException ex)
                {
                    Console.WriteLine("PythonException caught:");
                    Console.WriteLine("Type: " + ex.Type);
                    Console.WriteLine("Message: " + ex.Message);
                    Console.WriteLine("StackTrace: " + ex.StackTrace);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }
            }
        }
    }
}
