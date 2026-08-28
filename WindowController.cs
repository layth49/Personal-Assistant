using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Personal_Assistant.WindowControl
{
    // One top-level window that could be acted on.
    public sealed class WindowInfo
    {
        public IntPtr Handle { get; set; }
        public string Title { get; set; }
        public string ProcessName { get; set; }
        public int Area { get; set; }

        // Position in the Z-order at the time of enumeration; 0 is topmost.
        // This is how "the Chrome window" gets resolved to the one the user was
        // last actually using rather than an arbitrary one.
        public int ZOrder { get; set; }
    }

    public sealed class WindowActionResult
    {
        public bool Succeeded { get; set; }
        public string MatchedApp { get; set; }
        public string Title { get; set; }
        public int Candidates { get; set; }
        public string Detail { get; set; }
    }

    // Focus / minimize / maximize / restore / snap top-level windows by loose
    // app name.
    //
    // Deliberately separate from ProcessController: that class resolves an exact
    // image name for Process.GetProcessesByName, which is wrong here because
    // people name windows by what they see ("VS Code", "the Chrome window"),
    // and because a window action must pick ONE window out of the several an
    // app may own.
    public class WindowController
    {
        #region interop

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private const int SW_MINIMIZE = 6;
        private const int SW_MAXIMIZE = 3;
        private const int SW_RESTORE = 9;

        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        // ~300ms total, which is well inside the turnaround of a spoken reply.
        private const int ForegroundChecks = 15;
        private const int ForegroundCheckMs = 20;

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        #endregion

        // Spoken names whose window/process name doesn't contain the spoken word.
        private static readonly Dictionary<string, string[]> Aliases =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["vs code"] = new[] { "code" },
                ["vscode"] = new[] { "code" },
                ["visual studio code"] = new[] { "code" },
                ["visual studio"] = new[] { "devenv" },
                ["explorer"] = new[] { "explorer" },
                ["file explorer"] = new[] { "explorer" },
                ["terminal"] = new[] { "windowsterminal", "wt" },
                ["browser"] = new[] { "chrome", "msedge", "firefox" },
            };

        // Every visible, titled, non-tool top-level window, topmost first.
        public IReadOnlyList<WindowInfo> Enumerate()
        {
            var windows = new List<WindowInfo>();
            int z = 0;

            // EnumWindows walks in Z-order, so the counter doubles as "how
            // recently was this in front".
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                if (GetWindowTextLength(hWnd) == 0) return true;

                // Tool windows are palettes and tray helpers, never what someone
                // means by "the Spotify window".
                if ((GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;

                if (!GetWindowRect(hWnd, out RECT r)) return true;
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0) return true;

                var sb = new StringBuilder(512);
                GetWindowText(hWnd, sb, sb.Capacity);

                GetWindowThreadProcessId(hWnd, out uint pid);
                string procName;
                try
                {
                    using (var p = Process.GetProcessById((int)pid)) procName = p.ProcessName;
                }
                catch { procName = string.Empty; }

                windows.Add(new WindowInfo
                {
                    Handle = hWnd,
                    Title = sb.ToString(),
                    ProcessName = procName,
                    Area = w * h,
                    ZOrder = z++
                });
                return true;
            }, IntPtr.Zero);

            return windows;
        }

        // Best window for a spoken app name, or null. Matching prefers the
        // process name (stable) over the title (which carries document names),
        // then the topmost such window.
        public WindowInfo Resolve(string spokenName, out int candidateCount)
        {
            candidateCount = 0;
            string name = (spokenName ?? string.Empty).Trim();
            if (name.Length == 0) return null;

            var needles = new List<string> { name };
            foreach (var pair in Aliases)
                if (name.IndexOf(pair.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    needles.AddRange(pair.Value);
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                needles.Add(name.Substring(0, name.Length - 4));

            WindowInfo bestByProcess = null, bestByTitle = null;

            foreach (WindowInfo w in Enumerate())
            {
                bool procHit = false, titleHit = false;
                foreach (string needle in needles)
                {
                    if (needle.Length == 0) continue;
                    if (!string.IsNullOrEmpty(w.ProcessName) &&
                        w.ProcessName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                        procHit = true;
                    if (!string.IsNullOrEmpty(w.Title) &&
                        w.Title.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                        titleHit = true;
                }

                if (!procHit && !titleHit) continue;
                candidateCount++;

                // Enumerate() is Z-ordered, so the first hit in each bucket is
                // already the most recently fronted one.
                if (procHit && bestByProcess == null) bestByProcess = w;
                if (titleHit && bestByTitle == null) bestByTitle = w;
            }

            return bestByProcess ?? bestByTitle;
        }

        public WindowActionResult Focus(string app) => Act(app, w =>
        {
            if (IsIconic(w.Handle)) ShowWindow(w.Handle, SW_RESTORE);
            return ForceForeground(w.Handle);
        });

        public WindowActionResult Minimize(string app) =>
            Act(app, w => ShowWindow(w.Handle, SW_MINIMIZE));

        public WindowActionResult Maximize(string app) => Act(app, w =>
        {
            ShowWindow(w.Handle, SW_MAXIMIZE);
            return ForceForeground(w.Handle);
        });

        public WindowActionResult Restore(string app) => Act(app, w =>
        {
            ShowWindow(w.Handle, SW_RESTORE);
            return ForceForeground(w.Handle);
        });

        // Snaps to half of the working area of the monitor the window is
        // currently on — not the primary monitor, and not full monitor bounds,
        // so it respects the taskbar on a multi-monitor desktop.
        public WindowActionResult Snap(string app, string side) => Act(app, w =>
        {
            IntPtr monitor = MonitorFromWindow(w.Handle, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(monitor, ref mi)) return false;

            RECT work = mi.rcWork;
            int fullW = work.Right - work.Left;
            int fullH = work.Bottom - work.Top;
            int halfW = fullW / 2;
            int halfH = fullH / 2;

            int x = work.Left, y = work.Top, cx = halfW, cy = fullH;
            switch ((side ?? "left").Trim().ToLowerInvariant())
            {
                case "right": x = work.Left + halfW; break;
                case "top": cx = fullW; cy = halfH; break;
                case "bottom": y = work.Top + halfH; cx = fullW; cy = halfH; break;
                default: break; // left
            }

            // A maximized window ignores SetWindowPos, so restore it first.
            ShowWindow(w.Handle, SW_RESTORE);
            return SetWindowPos(w.Handle, IntPtr.Zero, x, y, cx, cy, SWP_NOZORDER | SWP_NOACTIVATE);
        });

        private WindowActionResult Act(string app, Func<WindowInfo, bool> action)
        {
            WindowInfo w = Resolve(app, out int candidates);
            if (w == null)
            {
                return new WindowActionResult
                {
                    Succeeded = false,
                    MatchedApp = app,
                    Candidates = 0,
                    Detail = "no visible window"
                };
            }

            bool ok;
            try { ok = action(w); }
            catch (Exception ex)
            {
                return new WindowActionResult
                {
                    Succeeded = false, MatchedApp = w.ProcessName,
                    Title = w.Title, Candidates = candidates, Detail = ex.Message
                };
            }

            return new WindowActionResult
            {
                Succeeded = ok,
                MatchedApp = string.IsNullOrEmpty(w.ProcessName) ? app : w.ProcessName,
                Title = w.Title,
                Candidates = candidates,
                Detail = ok ? null : "the window refused the change"
            };
        }

        // SetForegroundWindow is refused when the calling process does not own
        // the current foreground window — it returns false and nothing moves,
        // which is exactly the "said okay, nothing happened" failure this whole
        // tool has to avoid. Attaching our input queue to the current foreground
        // thread makes Windows treat the call as coming from the active app and
        // lifts the restriction. The attach is always detached again: leaving it
        // in place would tie this process's input state to another app's.
        private static bool ForceForeground(IntPtr hWnd)
        {
            SetForegroundWindow(hWnd);
            if (WaitForForeground(hWnd)) return true;

            IntPtr fg = GetForegroundWindow();
            uint fgThread = GetWindowThreadProcessId(fg, out _);
            uint ourThread = GetCurrentThreadId();

            bool attached = fgThread != 0 && fgThread != ourThread &&
                            AttachThreadInput(ourThread, fgThread, true);
            try
            {
                SetForegroundWindow(hWnd);
                return WaitForForeground(hWnd);
            }
            finally
            {
                if (attached) AttachThreadInput(ourThread, fgThread, false);
            }
        }

        // The foreground change is asynchronous: SetForegroundWindow returns
        // before the switch lands, and GetForegroundWindow checked immediately
        // still names the OLD window. Measured on this machine — the attach
        // trick moved GitHub Desktop to the front while the immediate check
        // said it had failed. Reporting that failure would have had the
        // assistant apologise for something it had just successfully done, so
        // the result is polled briefly instead of sampled once.
        private static bool WaitForForeground(IntPtr hWnd)
        {
            for (int i = 0; i < ForegroundChecks; i++)
            {
                if (GetForegroundWindow() == hWnd) return true;
                System.Threading.Thread.Sleep(ForegroundCheckMs);
            }
            return GetForegroundWindow() == hWnd;
        }
    }
}
