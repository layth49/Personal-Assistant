using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Personal_Assistant.SpeechManager;
using Python.Runtime;
using WindowsInput;
using WindowsInput.Native;

namespace Personal_Assistant.PlaystationController
{
    public class PlaystationControl
    {
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        private readonly InputSimulator simulator = new InputSimulator();
        private readonly SpeechService speechManager = new SpeechService();

        public async Task TurnOnPlaystation()
        {
            Process remoteplay = Process.Start(@"C:\Program Files (x86)\Sony\PS Remote Play\RemotePlay.exe");
            if (remoteplay == null)
            {
                Console.WriteLine("Failed to launch Remote Play.");
                return;
            }
            remoteplay.PriorityClass = ProcessPriorityClass.High;

            await speechManager.Say(Program.recognizedText,
                "Okay! Turning on your PlayStation 5 now. What game would you like to play?");

            // Wait for the Remote Play window to actually appear and become
            // visible before sending input — the process starts but the window
            // may take several seconds to render.
            IntPtr handle = await WaitForWindowAsync(remoteplay, timeoutSeconds: 30);
            if (handle == IntPtr.Zero)
            {
                Console.WriteLine("Remote Play window did not appear within 30s.");
                return;
            }

            SetForegroundWindow(handle);
            await Task.Delay(500);

            simulator.Keyboard.KeyPress(VirtualKeyCode.TAB);
            simulator.Keyboard.KeyPress(VirtualKeyCode.RETURN);
            SetForegroundWindow(handle);

            string userResponse = (await speechManager.RecognizeOnceAsync()).TrimEnd('.');

            SetForegroundWindow(handle);

            await speechManager.Say(userResponse, $"Okay! Loading up {userResponse} now");

            try
            {
                SetForegroundWindow(handle);
                NavigateToGame(userResponse);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }

            remoteplay.CloseMainWindow();
            simulator.Keyboard.KeyPress(VirtualKeyCode.RETURN);

            await speechManager.Say(userResponse, $"{userResponse} is ready! Have fun!");
        }

        // Polls until the process has a valid, visible main window handle,
        // or the timeout elapses. Returns IntPtr.Zero on timeout.
        private static async Task<IntPtr> WaitForWindowAsync(Process process, int timeoutSeconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                process.Refresh();
                IntPtr hwnd = process.MainWindowHandle;
                if (hwnd != IntPtr.Zero && IsWindowVisible(hwnd))
                {
                    Console.WriteLine($"Remote Play window ready (hwnd={hwnd}).");
                    return hwnd;
                }
                await Task.Delay(500);
            }
            return IntPtr.Zero;
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
