using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Personal_Assistant.ScreenCapture
{
    // Captures the whole desktop (all monitors) to a PNG under the user's
    // Pictures folder and can open it in the default image viewer.
    //
    // The process is per-monitor DPI aware (SpeechBubble.py sets that at import),
    // so SystemInformation.VirtualScreen reports physical pixel bounds and the
    // capture comes out at true resolution across the multi-monitor layout.
    public class ScreenshotService
    {
        // Hardcoded, not Environment.SpecialFolder.MyPictures — MyPictures
        // resolves through OneDrive's folder redirection on this machine, and
        // every screenshot was silently being synced there.
        private static readonly string SaveDir =
            @"C:\Users\layth\Pictures\L.A.I.T.H. Screenshots";

        // Captures the full virtual screen and saves it. Returns the file path.
        public string Capture()
        {
            using (var bitmap = CaptureBitmap())
            {
                Directory.CreateDirectory(SaveDir);
                string path = Path.Combine(
                    SaveDir,
                    $"screenshot-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
                bitmap.Save(path, ImageFormat.Png);
                return path;
            }
        }

        // Captures without writing anything to disk, for callers (e.g. vision
        // tool calls) that just need the pixels.
        //
        // `monitor` scopes the capture, and defaults to the monitor the user is
        // actually looking at rather than the whole desktop. That default is the
        // point: a vision model downscales whatever it is given, so on a
        // two-monitor desktop a full-desktop capture halves the effective
        // resolution of the thing being asked about — and the questions this
        // serves ("what does that small text say") are exactly the ones that
        // then fail. Accepts "focused" (default), "all", or a 1-based index.
        public byte[] CaptureBytes(string monitor = "focused")
        {
            using (var bitmap = CaptureBitmap(BoundsFor(monitor)))
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }

        // Which monitors exist, for a spoken "I looked at your left screen".
        public static int MonitorCount => Screen.AllScreens.Length;

        // Resolves a monitor selector to pixel bounds. Anything unrecognised
        // falls back to the focused monitor rather than throwing — a bad
        // selector should still get the user an answer.
        public static Rectangle BoundsFor(string monitor)
        {
            string want = (monitor ?? string.Empty).Trim();

            if (want.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                want.Equals("both", StringComparison.OrdinalIgnoreCase) ||
                want.Equals("desktop", StringComparison.OrdinalIgnoreCase))
            {
                return SystemInformation.VirtualScreen;
            }

            Screen[] screens = Screen.AllScreens;

            if (int.TryParse(want, out int index) && index >= 1 && index <= screens.Length)
            {
                return screens[index - 1].Bounds;
            }

            if (want.Equals("primary", StringComparison.OrdinalIgnoreCase))
            {
                return (Screen.PrimaryScreen ?? screens[0]).Bounds;
            }

            // "focused", empty, or anything unrecognised.
            IntPtr fg = GetForegroundWindow();
            Screen target = fg != IntPtr.Zero
                ? Screen.FromHandle(fg)
                : Screen.PrimaryScreen;
            return (target ?? screens[0]).Bounds;
        }

        private static Bitmap CaptureBitmap()
        {
            return CaptureBitmap(SystemInformation.VirtualScreen);
        }

        private static Bitmap CaptureBitmap(Rectangle bounds)
        {
            var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            }
            return bitmap;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        // Opens a saved screenshot in the default image viewer.
        public void Open(string path)
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }
}
