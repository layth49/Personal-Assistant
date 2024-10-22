using System;
using System.Diagnostics;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.CognitiveServices.Speech;
using Personal_Assistant.SpeechManager;
using Python.Runtime;
using WindowsInput;

namespace Personal_Assistant
{
    public class Playstation
    {
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);
        InputSimulator simulator = new InputSimulator();

        SpeechService speechManager = new SpeechService();

        async public void TurnOnPlaystation()
        {

            Process remoteplay = Process.Start(@"C:\Program Files (x86)\Sony\PS Remote Play\RemotePlay.exe");
            remoteplay.PriorityClass = ProcessPriorityClass.High;
            Console.WriteLine("Assistant: Ok! Turning on your PlayStation 5 now. What game would you like to play?\n");
            await speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", "Okay! Turning on your PlayStation 5 now. What game would you like to play?");

            IntPtr handle = remoteplay.MainWindowHandle;
            SetForegroundWindow(handle);

            // Turn on
            simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.TAB);
            simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.RETURN);
            SetForegroundWindow(handle);

            SpeechRecognizer playstationConfirmationRecognizer = new SpeechRecognizer(speechManager.speechConfig, speechManager.audioConfig);
            SpeechRecognitionResult parsedResponse = await playstationConfirmationRecognizer.RecognizeOnceAsync();
            speechManager.ConvertSpeechToText(parsedResponse);
            string userResponse = parsedResponse.Text.TrimEnd('.');

            SetForegroundWindow(handle);

            Console.WriteLine($"Assistant: Ok! Loading up {userResponse} now\n");
            speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", $"Okay! Loading up {userResponse} now");


            try
            {
                SetForegroundWindow(handle);
                // Execute the Python script
                SendData(userResponse);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
            
            // Send a request to close Remote Play
            remoteplay.CloseMainWindow();
            // Confirm request to close Remote Play
            simulator.Keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.RETURN);

            Console.WriteLine($"Assistant: {userResponse} is ready. Have fun!\n");
            speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", $"{userResponse} is ready! Have fun!");
        }

        public void SendData(string data)
        {
            Runtime.PythonDLL = @"C:\Users\15048\AppData\Local\Programs\Python\Python312\python312.dll";
            PythonEngine.Initialize();
            using (Py.GIL())
            {
                var pythonScript = Py.Import("AutoRemotePlay");
                var message = new PyString(data);
                var result = pythonScript.InvokeMethod("navigator", new PyObject[] { message });
                
            }
        }
    }
}