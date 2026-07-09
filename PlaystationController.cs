using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Personal_Assistant.SpeechManager;
using Python.Runtime;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Personal_Assistant.PlaystationController
{
    public class PlaystationControl
    {
        private readonly SpeechService speechManager = new SpeechService();

        public async Task TurnOnPlaystation()
        {
            await speechManager.Say(Program.recognizedText,
                "Okay! Turning on your PlayStation 5 now. What game would you like to play?");

            Console.WriteLine("Waiting for the user to respond with a game title...");
            string userResponse = (await speechManager.RecognizeOnceAsync()).TrimEnd('.');
            Console.WriteLine($"User wants to play: {userResponse}");

            using (var automation = new UIA3Automation())
            {
                // 1. Trigger the launcher
                Process.Start(@"C:\Program Files (x86)\Sony\PS Remote Play\RemotePlay.exe");

                // 2. Wait for the launcher to spawn the real app and exit
                Console.WriteLine("Waiting for Remote Play to initialize...");
                await Task.Delay(2000);

                // 3. Find the actual running process
                var processes = Process.GetProcessesByName("RemotePlay");
                if (processes.Length == 0)
                {
                    Console.WriteLine("Could not find the running Remote Play process.");
                    return;
                }

                Process realRemotePlay = processes[0];

                // Apply high priority to the actual app
                try
                {
                    realRemotePlay.PriorityClass = ProcessPriorityClass.High;
                }
                catch
                {
                    Console.WriteLine("Failed to set High priority. Ignoring.");
                }

                // 4. Attach FlaUI to the surviving process ID
                var app = Application.Attach(realRemotePlay.Id);

                // 5. FlaUI's native retry logic
                var window = Retry.WhileNull(() => app.GetMainWindow(automation), TimeSpan.FromSeconds(30)).Result;

                if (window == null)
                {
                    Console.WriteLine("Remote Play window did not appear within 30s.");
                    return;
                }

                window.Focus();
                await Task.Delay(500);

                var conditionFactory = new ConditionFactory(new UIA3PropertyLibrary());

                // Try to find the specific console button. 
                var connectButton = window.FindFirstDescendant(conditionFactory.ByName("PS5-900"))?.AsButton();

                if (connectButton != null)
                {
                    Console.WriteLine("Found the PS5 connect button. Invoking...");
                    connectButton.Invoke();
                    Wait.UntilInputIsProcessed();
                }
                else
                {
                    Console.WriteLine("Connect button not found via FlaUI! Falling back to Tab/Enter macro...");
                    Keyboard.Press(VirtualKeyShort.TAB);
                    Keyboard.Press(VirtualKeyShort.ENTER);
                }

                

                window.Focus();

                await speechManager.Say(userResponse, $"Okay! Loading up {userResponse} now");

                try
                {
                    window.Focus();
                    // Hand off to the Python visual macro
                    NavigateToGame(userResponse);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }

                // Clean UI teardown
                window.Close();
                Keyboard.Press(VirtualKeyShort.ENTER); // Confirm close if the disconnect dialog pops up

                await speechManager.Say(userResponse, $"{userResponse} is ready! Have fun!");
            }
        }

        public void NavigateToGame(string gameName)
        {
            using (Py.GIL())
            {
                try
                {
                    var autoRemotePlayModule = Py.Import("AutoRemotePlay");
                    using (var gameTitlePyStr = new PyString(gameName))
                    {
                        autoRemotePlayModule.InvokeMethod("navigator", new PyObject[] { gameTitlePyStr });
                    }
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