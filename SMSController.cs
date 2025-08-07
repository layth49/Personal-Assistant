using Microsoft.CognitiveServices.Speech;
using Personal_Assistant.SpeechManager;
using Python.Runtime;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WindowsInput;



namespace Personal_Assistant.SMSController
{
    class SMSControl
    {
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);
        InputSimulator simulator = new InputSimulator();

        SpeechService speechManager = new SpeechService();

        async public void SendSMS(string contactName, string contactNumber)
        {
            try
            {
                // Open the Phone Link app
                Process.Start(new ProcessStartInfo("powershell", "start shell:AppsFolder\\Microsoft.YourPhone_8wekyb3d8bbwe!App")
                {
                    UseShellExecute = true
                });

                foreach (var p in Process.GetProcessesByName("Phone Link"))
                    if (SetForegroundWindow(p.MainWindowHandle)) break;

                while (true)
                {
                    speechManager.SynthesizeTextToSpeech("en-US-AndrewMultilingualNeural", $"Okay! What would you like to send {contactName}?");
                    speechManager.SpeechBubble(Program.recognizedText, $"Okay! What would you like to send {contactName}?");

                    SpeechRecognizer recognizer = new SpeechRecognizer(speechManager.speechConfig);
                    SpeechRecognitionResult userResponse = recognizer.RecognizeOnceAsync().GetAwaiter().GetResult();
                    speechManager.ConvertSpeechToText(userResponse);

                    if (userResponse.Text.Contains("Introduce yourself"))
                    {
                        try
                        {
                            foreach (var p in Process.GetProcessesByName("Phone Link"))
                                if (SetForegroundWindow(p.MainWindowHandle)) break;

                            SendMessageToContact(contactNumber, "Hello! This was sent by L.A.I.T.H.49, AKA Layth's Logical Assistant for Intelligent Task Handling 49!");

                            speechManager.SynthesizeTextToSpeech("en-US-AndrewMultilingualNeural", $"Okay! Introducing myself to {contactName}.");
                            speechManager.SpeechBubble(userResponse.Text, $"Okay! Introducing myself to {contactName}.");
                            break;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error: {ex.Message}");
                        }
                    }
                    else
                    {
                        speechManager.SynthesizeTextToSpeech("en-US-AndrewMultilingualNeural", $"You'd like to send {userResponse.Text} to {contactName}. Is that correct?");
                        speechManager.SpeechBubble(userResponse.Text, $"You'd like to send {userResponse.Text} to {contactName}. Is that correct?");

                        SpeechRecognizer confirmationSpeechRecognizer = new SpeechRecognizer(speechManager.speechConfig);
                        SpeechRecognitionResult confirmationResult = confirmationSpeechRecognizer.RecognizeOnceAsync().GetAwaiter().GetResult();
                        speechManager.ConvertSpeechToText(confirmationResult);

                        if (confirmationResult.Text.Contains("no"))
                        {
                            speechManager.SynthesizeTextToSpeech("en-US-AndrewMultilingualNeural", "Okay, message cancelled. ");
                            speechManager.SpeechBubble(confirmationResult.Text, "Okay, message cancelled.");
                        }
                        else
                        {

                            try
                            {
                                foreach (var p in Process.GetProcessesByName("Phone Link"))
                                    if (SetForegroundWindow(p.MainWindowHandle)) break;

                                SendMessageToContact(contactNumber, userResponse.Text);

                                speechManager.SynthesizeTextToSpeech("en-US-AndrewMultilingualNeural", $"Sending {userResponse.Text} to {contactName}.");
                                speechManager.SpeechBubble(userResponse.Text, $"Sending {userResponse.Text} to {contactName}.");
                                break;
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error: {ex.Message}");
                            }
                        }
                    }
                }
                
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
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
