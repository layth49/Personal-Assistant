using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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
        // The one running instance — never `new SpeechService()`. A second one
        // owns none of the audio state the rest of the app reads, so everything
        // it says escapes the echo gate and everything it hears times out.
        // A property, not a field, so there is no construction-order trap.
        private static SpeechService speechManager => SpeechService.Current;

        // Launches Remote Play and navigates to `game`. Returns whether Remote Play
        // came up far enough to drive.
        //
        // This used to ask "what game would you like to play?" and open its own
        // SpeechRecognizer to hear the answer — the fourth blocking sub-dialog, and
        // the one Phase 4a's brief did not list. A Gemini Live session owns the
        // microphone, so that recognizer would deadlock it exactly like the other
        // three. The title now arrives as a tool argument: the model asks the
        // question in-session, which it is already good at, and calls once it knows.
        public async Task<bool> TurnOnPlaystation(string game)
        {
            game = (game ?? string.Empty).Trim().TrimEnd('.');
            if (game.Length == 0)
            {
                Console.WriteLine("No game title given; not launching Remote Play.");
                return false;
            }

            Process remoteplay = Process.Start(@"C:\Program Files (x86)\Sony\PS Remote Play\RemotePlay.exe");
            if (remoteplay == null)
            {
                Console.WriteLine("Failed to launch Remote Play.");
                return false;
            }
            remoteplay.PriorityClass = ProcessPriorityClass.High;

            // Wait for the Remote Play window to actually appear and become
            // visible before sending input — the process starts but the window
            // may take several seconds to render.
            IntPtr handle = await WaitForWindowAsync(remoteplay, timeoutSeconds: 30);
            if (handle == IntPtr.Zero)
            {
                Console.WriteLine("Remote Play window did not appear within 30s.");
                return false;
            }

            SetForegroundWindow(handle);
            await Task.Delay(500); // let the window settle before sending keys

            simulator.Keyboard.KeyPress(VirtualKeyCode.TAB);
            simulator.Keyboard.KeyPress(VirtualKeyCode.RETURN);
            SetForegroundWindow(handle);

            // The console's stream has to be up before the navigator can steer it.
            // The old dictation dialog paid for this wait by accident — a recognizer
            // plus a spoken read-back is several seconds — so removing the dialog
            // means buying the settle time back explicitly.
            await Task.Delay(6000);

            try
            {
                SetForegroundWindow(handle);
                NavigateToGame(game);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }

            remoteplay.CloseMainWindow();
            simulator.Keyboard.KeyPress(VirtualKeyCode.RETURN);
            return true;
        }

        // Polls until the process has a valid, visible main window handle,
        // or the timeout elapses. Returns IntPtr.Zero on timeout.
        private static async Task<IntPtr> WaitForWindowAsync(Process process, int timeoutSeconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                process.Refresh(); // flush cached handle / module info
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
