using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
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
        private static readonly string SaveDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "L.A.I.T.H. Screenshots");

        // Captures the full virtual screen and saves it. Returns the file path.
        public string Capture()
        {
            Rectangle bounds = SystemInformation.VirtualScreen;

            using (var bitmap = new Bitmap(bounds.Width, bounds.Height))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                }

                Directory.CreateDirectory(SaveDir);
                string path = Path.Combine(
                    SaveDir,
                    $"screenshot-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
                bitmap.Save(path, ImageFormat.Png);
                return path;
            }
        }

        // Opens a saved screenshot in the default image viewer.
        public void Open(string path)
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }
}
