using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace Personal_Assistant.Reminders
{
    // On-screen visualization for timers/alarms: an always-on-top floating
    // countdown per pending item, stacked at the top-right of the primary
    // screen. Runs its own dedicated STA WinForms message loop so it doesn't
    // depend on the app having a UI thread; every public method marshals onto
    // that loop. The scheduler stays the source of truth for firing — this only
    // shows/flashes/dismisses.
    public sealed class TimerWidgetHost : IReminderVisual, IDisposable
    {
        // Hidden form that owns the widget UI thread and lets us BeginInvoke onto it.
        private sealed class Anchor : Form
        {
            protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);
        }

        private readonly Dictionary<int, TimerWidget> byId = new Dictionary<int, TimerWidget>();
        private readonly List<TimerWidget> order = new List<TimerWidget>();
        private readonly ManualResetEventSlim ready = new ManualResetEventSlim(false);
        private Anchor anchor;
        private Thread uiThread;

        public TimerWidgetHost()
        {
            uiThread = new Thread(RunUi) { IsBackground = true, Name = "TimerWidgets" };
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();
            ready.Wait(); // block until the message loop + anchor handle exist
        }

        private void RunUi()
        {
            anchor = new Anchor();
            var _ = anchor.Handle;     // force handle creation so BeginInvoke works
            ready.Set();
            Application.Run(anchor);   // hidden; pumps the widgets' messages
        }

        private void OnUi(Action action)
        {
            var a = anchor;
            if (a == null || !a.IsHandleCreated) return;
            try { a.BeginInvoke(action); }
            catch { /* thread tearing down */ }
        }

        public void Show(int id, string label, DateTime fireAt, ReminderKind kind)
        {
            OnUi(() =>
            {
                if (byId.ContainsKey(id)) return;
                var w = new TimerWidget(id, label, fireAt, kind);
                byId[id] = w;
                order.Add(w);
                w.Show();
                Relayout();
            });
        }

        public void Fired(int id)
        {
            OnUi(() =>
            {
                if (!byId.TryGetValue(id, out var w)) return;
                w.FlashAndClose(() => RemoveInternal(id));
            });
        }

        public void Remove(int id)
        {
            OnUi(() =>
            {
                if (byId.TryGetValue(id, out var w)) { w.Close(); RemoveInternal(id); }
            });
        }

        public void Clear()
        {
            OnUi(() =>
            {
                foreach (var w in order.ToArray()) w.Close();
                byId.Clear();
                order.Clear();
            });
        }

        private void RemoveInternal(int id)
        {
            if (byId.TryGetValue(id, out var w))
            {
                byId.Remove(id);
                order.Remove(w);
                Relayout();
            }
        }

        // Stack the widgets up from the bottom-right of the primary work area —
        // away from the top-right window caption buttons (min/max/close), and
        // above the taskbar (WorkingArea already excludes it).
        private void Relayout()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            const int margin = 16, gap = 12;
            int y = area.Bottom - margin;
            foreach (var w in order)
            {
                y -= w.Height;
                w.Location = new Point(area.Right - w.Width - margin, y);
                y -= gap;
            }
        }

        public void Dispose()
        {
            var a = anchor;
            if (a != null && a.IsHandleCreated)
            {
                try { a.BeginInvoke((Action)(() => Application.ExitThread())); }
                catch { }
            }
        }
    }

    // A single floating countdown card. Borderless, non-activating (never steals
    // focus — important given the app's focus-sensitive speech bubble), rounded,
    // updates ~4x/second, and flashes when told it fired.
    internal sealed class TimerWidget : Form
    {
        public int Id { get; }

        private readonly string title;
        private readonly DateTime fireAt;
        private readonly System.Windows.Forms.Timer ticker;

        private bool flashing;
        private bool flashOn;
        private int flashCount;
        private System.Windows.Forms.Timer flashTimer;
        private Action onFlashDone;

        private static readonly Color BgColor = Color.FromArgb(30, 30, 46);
        private static readonly Color AccentColor = Color.FromArgb(137, 180, 250);
        private static readonly Color FgColor = Color.FromArgb(232, 232, 244);

        public TimerWidget(int id, string label, DateTime fireAt, ReminderKind kind)
        {
            Id = id;
            this.fireAt = fireAt;
            title = string.IsNullOrWhiteSpace(label)
                ? (kind == ReminderKind.Alarm ? "Alarm" : "Timer")
                : label;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(248, 96);
            DoubleBuffered = true;
            BackColor = BgColor;
            // Slightly see-through so it reads as an overlay, not a solid window.
            Opacity = 0.85;

            ticker = new System.Windows.Forms.Timer { Interval = 250 };
            ticker.Tick += (s, e) => Invalidate();
            ticker.Start();
        }

        // Don't take focus when shown.
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOPMOST = 0x00000008;
                const int WS_EX_TRANSPARENT = 0x00000020;  // mouse clicks pass through
                const int WS_EX_TOOLWINDOW = 0x00000080;   // no taskbar / alt-tab entry
                const int WS_EX_LAYERED = 0x00080000;      // required with transparent + Opacity
                const int WS_EX_NOACTIVATE = 0x08000000;   // never activate on click/show
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW
                            | WS_EX_LAYERED | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            using (var path = RoundedRect(ClientRectangle, 16))
            {
                Region = new Region(path);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            bool lit = flashing && flashOn;
            Color bg = lit ? AccentColor : BgColor;
            Color labelColor = lit ? BgColor : AccentColor;
            Color timeColor = lit ? BgColor : FgColor;

            using (var b = new SolidBrush(bg))
            using (var path = RoundedRect(ClientRectangle, 16))
            {
                g.FillPath(b, path);
            }

            using (var lf = new Font("Segoe UI", 10.5f, FontStyle.Regular))
            using (var lb = new SolidBrush(labelColor))
            {
                g.DrawString(title, lf, lb, 16, 11);
            }

            string timeText = flashing ? "Time's up!" : Format(Remaining());
            using (var tf = new Font("Consolas", 26f, FontStyle.Bold))
            using (var tb = new SolidBrush(timeColor))
            {
                g.DrawString(timeText, tf, tb, 13, 35);
            }
        }

        private TimeSpan Remaining()
        {
            TimeSpan r = fireAt - DateTime.Now;
            return r < TimeSpan.Zero ? TimeSpan.Zero : r;
        }

        private static string Format(TimeSpan t)
        {
            if (t.TotalHours >= 1)
                return string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
            return string.Format("{0:00}:{1:00}", (int)t.TotalMinutes, t.Seconds);
        }

        // Pulse the card a few times, then invoke onDone and close.
        public void FlashAndClose(Action onDone)
        {
            if (flashing) return;
            onFlashDone = onDone;
            flashing = true;
            flashCount = 0;
            ticker.Stop();

            flashTimer = new System.Windows.Forms.Timer { Interval = 320 };
            flashTimer.Tick += (s, e) =>
            {
                flashOn = !flashOn;
                flashCount++;
                Invalidate();
                if (flashCount >= 8)
                {
                    flashTimer.Stop();
                    var done = onFlashDone;
                    onFlashDone = null;
                    if (done != null) done();
                    Close();
                }
            };
            flashTimer.Start();
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ticker?.Dispose();
                flashTimer?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
