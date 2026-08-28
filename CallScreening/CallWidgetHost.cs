using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace Personal_Assistant.CallScreening
{
    /// <summary>
    /// The on-screen face of a screened call: who is calling, what stage the call
    /// is at, how long it has run, and the last thing said.
    /// </summary>
    /// <remarks>
    /// WHY THIS EXISTS AT ALL. On the Google Voice path the browser is headless,
    /// so there is no window to glance at — measured 2026-08-21, headless Chromium
    /// carries real audio, which is what makes hiding it viable in the first
    /// place. Without a widget a screened call would be completely invisible:
    /// something answers the phone, talks to a stranger, and the only evidence is
    /// a console line. This is the replacement for watching the browser.
    ///
    /// Modelled directly on TimerWidgetHost (TimerWidgetHost.cs:17) — its own STA
    /// WinForms message loop, so it does not depend on the app having a UI thread,
    /// and every public method marshals onto that loop. Same palette and shape, so
    /// a call card reads as the same family of object as a timer card.
    ///
    /// ONE DELIBERATE DIFFERENCE FROM TimerWidget: no WS_EX_TRANSPARENT. Timer
    /// cards are pure information and let clicks pass through to whatever is
    /// underneath; this one carries a Hang up button, because the moment you most
    /// want to end a screened call is the moment you are watching it go wrong, and
    /// "say the words out loud" is a poor answer when a stranger can hear you.
    /// WS_EX_NOACTIVATE stays: it must never steal focus, which would disturb the
    /// speech bubble.
    /// </remarks>
    public sealed class CallWidgetHost : IDisposable
    {
        // Hidden form that owns the widget UI thread and lets us BeginInvoke onto it.
        private sealed class Anchor : Form
        {
            protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);
        }

        private readonly ManualResetEventSlim ready = new ManualResetEventSlim(false);
        private Anchor anchor;
        private Thread uiThread;
        private CallWidget widget;

        /// <summary>
        /// Invoked when the Hang up button is pressed. Wired by
        /// CallScreeningService to the live call; null means the button is drawn
        /// disabled, because a button that does nothing is worse than no button.
        /// </summary>
        public Action OnHangUp { get; set; }

        public CallWidgetHost()
        {
            uiThread = new Thread(RunUi) { IsBackground = true, Name = "CallWidget" };
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();
            ready.Wait(); // block until the message loop + anchor handle exist
        }

        private void RunUi()
        {
            // NOTHING HERE SETS DPI AWARENESS, deliberately. The process is
            // already per-monitor DPI aware — SpeechBubble.py:17 calls
            // SetProcessDpiAwareness(2) at import, carefully ordered ahead of
            // pygame and PyAutoGUI — and ScreenshotService relies on the same
            // fact. A second, thread-scoped mechanism here would be a parallel
            // answer to a question the app has already answered once.
            //
            // The consequence for this widget is that its coordinates are REAL
            // device pixels, which is why CallWidget designs in logical units and
            // scales by DeviceDpi rather than trusting its own numbers.
            anchor = new Anchor();
            var _ = anchor.Handle;     // force handle creation so BeginInvoke works
            ready.Set();
            Application.Run(anchor);   // hidden; pumps the widget's messages
        }

        private void OnUi(Action action)
        {
            var a = anchor;
            if (a == null || !a.IsHandleCreated) return;
            try { a.BeginInvoke(action); }
            catch { /* thread tearing down */ }
        }

        /// <summary>A call is ringing. Shows the card with the caller on it.</summary>
        public void Ringing(IncomingCall call)
        {
            OnUi(() =>
            {
                if (widget == null)
                {
                    widget = new CallWidget(() => OnHangUp?.Invoke(), () => OnHangUp != null);
                    widget.Show();
                    // Placed AFTER Show, never before. WinForms rescales a form
                    // when its handle is created, so Width read beforehand is the
                    // designed width rather than the real one — and positioning
                    // against the wrong width put the card partly off the right
                    // edge of the screen, hang-up button first.
                    Place(widget);
                }

                widget.SetCaller(call?.Caller ?? "an unknown number", call?.Number);
                widget.SetStage(CallStage.Ringing);
            });
        }

        /// <summary>Moves the card through the stages of a screened call.</summary>
        public void Stage(CallStage stage) => OnUi(() => widget?.SetStage(stage));

        /// <summary>
        /// The most recent thing said, caller or assistant. Kept to one line: this
        /// is a glance-at-it overlay, not a transcript window — the whole
        /// conversation is already written to the call log.
        /// </summary>
        public void Said(bool byCaller, string text) =>
            OnUi(() => widget?.SetLastLine(byCaller, text));

        /// <summary>
        /// The call is over. Shows the outcome briefly, then closes — rather than
        /// vanishing the instant the line drops, which would leave you having
        /// missed the whole thing if you looked up a second too late.
        /// </summary>
        public void Ended(string outcome)
        {
            OnUi(() =>
            {
                if (widget == null) return;
                widget.SetStage(CallStage.Ended);
                widget.SetOutcome(outcome);
                widget.CloseAfter(TimeSpan.FromSeconds(6), () => widget = null);
            });
        }

        /// <summary>Top-right of the primary work area — timers stack bottom-right,
        /// so a call never fights a countdown for the same pixels.</summary>
        private static void Place(Form w)
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            // Device pixels, not logical — the process is DPI aware, so this is
            // already tighter than the same number would be unscaled.
            const int margin = 12;

            int x = area.Right - w.Width - margin;
            int y = area.Top + margin;

            // Clamped, because "top-right minus the width" is only correct if the
            // width is what you think it is. It was not, and a card whose hang-up
            // button sits past the edge of the screen is a card you cannot use.
            if (x < area.Left) x = area.Left;
            if (y < area.Top) y = area.Top;
            if (x + w.Width > area.Right) x = Math.Max(area.Left, area.Right - w.Width);
            if (y + w.Height > area.Bottom) y = Math.Max(area.Top, area.Bottom - w.Height);

            w.Location = new Point(x, y);
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

    /// <summary>Where a screened call has got to, in the order it gets there.</summary>
    public enum CallStage
    {
        Ringing,
        Answering,   // the button has been clicked; audio is not up yet
        Screening,   // connected, the assistant is talking to them
        Ended
    }

    // A single floating call card. Borderless, non-activating, rounded, and
    // repainted about four times a second so the duration ticks.
    internal sealed class CallWidget : Form
    {
        private readonly System.Windows.Forms.Timer ticker;
        private readonly Action hangUp;
        private readonly Func<bool> canHangUp;

        private string caller = "";
        private string number;
        // BOTH speakers are kept, not just the most recent line.
        //
        // Showing only "the last thing said" meant the caller's words were
        // painted and then overwritten microseconds later, because a turn flushes
        // the caller's line and the assistant's reply back to back — so in a real
        // call the caller's half was effectively never visible. Which defeats the
        // point: what you want to know at a glance is what THEY said.
        private string callerLine;
        private string assistantLine;
        private string outcome;
        private CallStage stage = CallStage.Ringing;
        private DateTime? connectedAt;

        private System.Windows.Forms.Timer closeTimer;

        // Same palette as TimerWidget, so the two read as one family.
        private static readonly Color BgColor = Color.FromArgb(30, 30, 46);
        private static readonly Color AccentColor = Color.FromArgb(137, 180, 250);
        private static readonly Color FgColor = Color.FromArgb(232, 232, 244);
        private static readonly Color MutedColor = Color.FromArgb(150, 152, 180);
        private static readonly Color DangerColor = Color.FromArgb(243, 139, 168);
        private static readonly Color LiveColor = Color.FromArgb(166, 227, 161);

        // THE CARD IS DESIGNED IN THESE UNITS AND ONLY THESE UNITS.
        //
        // Its thread is per-monitor DPI aware (see CallWidgetHost.RunUi), so the
        // window is sized in real device pixels while OnPaint scales up and draws
        // in this fixed logical space. That keeps the layout arithmetic readable
        // at one size and the rendering sharp at any.
        private const int LogicalW = 320;
        private const int LogicalH = 168;

        // Logical, converted on the way in from a mouse event.
        private static readonly Rectangle HangUpRect =
            new Rectangle(LogicalW - 100, LogicalH - 42, 84, 28);

        private float scale = 1f;

        public CallWidget(Action hangUp, Func<bool> canHangUp)
        {
            this.hangUp = hangUp;
            this.canHangUp = canHangUp;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            // Everything is drawn by hand at fixed coordinates in OnPaint, so
            // letting WinForms rescale the form only desynchronises the frame from
            // its contents — and moves the hang-up hit box away from the pill.
            AutoScaleMode = AutoScaleMode.None;
            Size = new Size(LogicalW, LogicalH);
            DoubleBuffered = true;
            BackColor = BgColor;
            Opacity = 0.92;

            ticker = new System.Windows.Forms.Timer { Interval = 250 };
            ticker.Tick += (s, e) => Invalidate();
            ticker.Start();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // DeviceDpi is only meaningful once there is a handle to ask about, so
            // the real size cannot be set in the constructor.
            scale = DeviceDpi / 96f;
            if (scale <= 0) scale = 1f;

            Size = new Size((int)Math.Round(LogicalW * scale),
                            (int)Math.Round(LogicalH * scale));
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOPMOST = 0x00000008;
                const int WS_EX_TOOLWINDOW = 0x00000080;   // no taskbar / alt-tab entry
                const int WS_EX_LAYERED = 0x00080000;      // required with Opacity
                const int WS_EX_NOACTIVATE = 0x08000000;   // never activate on click/show
                var cp = base.CreateParams;
                // Note the absence of WS_EX_TRANSPARENT — unlike TimerWidget this
                // card must receive clicks for its Hang up button. NOACTIVATE
                // still keeps it from stealing focus.
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public void SetCaller(string name, string num)
        {
            caller = name ?? "";
            number = Pretty(num);
            Invalidate();
        }

        public void SetStage(CallStage s)
        {
            // Stamped once, on the way in. Re-stamping on every repaint would
            // reset the duration four times a second.
            if (s == CallStage.Screening && connectedAt == null) connectedAt = DateTime.Now;
            stage = s;
            Invalidate();
        }

        public void SetLastLine(bool byCaller, string text)
        {
            if (byCaller) callerLine = text; else assistantLine = text;
            Invalidate();
        }

        public void SetOutcome(string text)
        {
            outcome = text;
            Invalidate();
        }

        public void CloseAfter(TimeSpan delay, Action then)
        {
            if (closeTimer != null) return;
            closeTimer = new System.Windows.Forms.Timer
            {
                Interval = Math.Max(1, (int)delay.TotalMilliseconds)
            };
            closeTimer.Tick += (s, e) =>
            {
                closeTimer.Stop();
                then?.Invoke();
                Close();
            };
            closeTimer.Start();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            using (var path = RoundedRect(ClientRectangle, (int)Math.Round(16 * scale)))
            {
                Region = new Region(path);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (stage == CallStage.Ended) return;
            if (canHangUp == null || !canHangUp()) return;

            // The click arrives in device pixels; the button is defined in logical
            // ones. Comparing the two directly is how a button comes to look right
            // and hit-test somewhere else entirely.
            var logical = new Point((int)(e.X / scale), (int)(e.Y / scale));
            if (!HangUpRect.Contains(logical)) return;

            try { hangUp?.Invoke(); } catch { /* the call is ending anyway */ }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // AntiAliasGridFit rather than ClearType: this is a LAYERED window
            // (WS_EX_LAYERED, for Opacity), and subpixel rendering on a layered
            // surface produces colour fringing on the glyph edges.
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            // From here on everything is in logical units; the window itself is
            // sized in device pixels.
            g.ScaleTransform(scale, scale);
            var card = new Rectangle(0, 0, LogicalW, LogicalH);

            using (var b = new SolidBrush(BgColor))
            using (var path = RoundedRect(card, 16))
            {
                g.FillPath(b, path);
            }

            // Stage strip: colour alone says at a glance whether this is still
            // ringing or someone is being talked to.
            //
            // INSET AND ROUNDED, not flush at x=0. Drawn against the edge it was
            // sliced by the window's own rounded Region, which left the ragged
            // pale edges — the clip curves inward exactly where the strip ran
            // straight down.
            using (var sb = new SolidBrush(StageColor()))
            using (var strip = RoundedRect(new Rectangle(12, 18, 4, LogicalH - 36), 2))
            {
                g.FillPath(sb, strip);
            }

            using (var nameFont = new Font("Segoe UI", 14f, FontStyle.Bold))
            using (var nb = new SolidBrush(FgColor))
            {
                g.DrawString(Ellipsize(g, caller, nameFont, LogicalW - 52), nameFont, nb, 28, 12);
            }

            using (var subFont = new Font("Segoe UI", 9f, FontStyle.Regular))
            using (var mb = new SolidBrush(MutedColor))
            {
                g.DrawString(number ?? "", subFont, mb, 29, 38);
            }

            using (var stageFont = new Font("Segoe UI", 10f, FontStyle.Bold))
            using (var sb2 = new SolidBrush(StageColor()))
            {
                g.DrawString(StageText(), stageFont, sb2, 29, 58);
            }

            // One line each, prefixed, so it is never ambiguous who is talking —
            // reading the assistant's words as the caller's would be actively
            // misleading. Single-line and ellipsized rather than wrapped: two
            // wrapped lines ran straight through the hang-up button, and the
            // gist is what a glance needs.
            using (var lineFont = new Font("Segoe UI", 9f, FontStyle.Italic))
            using (var fmt = new StringFormat
            {
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            })
            {
                float w = LogicalW - 58;
                if (!string.IsNullOrWhiteSpace(callerLine))
                    using (var b2 = new SolidBrush(FgColor))
                        g.DrawString("them: " + callerLine, lineFont, b2,
                            new RectangleF(29, 79, w, 16), fmt);

                if (!string.IsNullOrWhiteSpace(assistantLine))
                    using (var b3 = new SolidBrush(AccentColor))
                        g.DrawString("laith: " + assistantLine, lineFont, b3,
                            new RectangleF(29, 98, w, 16), fmt);
            }

            if (stage == CallStage.Ended && !string.IsNullOrWhiteSpace(outcome))
            {
                using (var outFont = new Font("Segoe UI", 9f, FontStyle.Regular))
                using (var ob = new SolidBrush(MutedColor))
                {
                    g.DrawString(outcome, outFont, ob, 29, LogicalH - 38);
                }
                return;
            }

            DrawHangUp(g);
        }

        private void DrawHangUp(Graphics g)
        {
            bool enabled = canHangUp != null && canHangUp();

            using (var path = RoundedRect(HangUpRect, HangUpRect.Height / 2))
            using (var b = new SolidBrush(enabled ? DangerColor : Color.FromArgb(60, 60, 80)))
            {
                g.FillPath(b, path);
            }

            using (var f = new Font("Segoe UI", 9f, FontStyle.Bold))
            using (var tb = new SolidBrush(enabled ? BgColor : MutedColor))
            using (var fmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            })
            {
                g.DrawString("Hang up", f, tb, HangUpRect, fmt);
            }
        }

        private Color StageColor()
        {
            switch (stage)
            {
                case CallStage.Ringing: return AccentColor;
                case CallStage.Answering: return AccentColor;
                case CallStage.Screening: return LiveColor;
                default: return MutedColor;
            }
        }

        private string StageText()
        {
            switch (stage)
            {
                case CallStage.Ringing: return "ringing";
                case CallStage.Answering: return "answering...";
                case CallStage.Screening:
                    return connectedAt == null
                        ? "screening"
                        : "screening  " + Format(DateTime.Now - connectedAt.Value);
                default: return "ended";
            }
        }

        private static string Format(TimeSpan t) =>
            ((int)t.TotalMinutes).ToString("00") + ":" + t.Seconds.ToString("00");

        /// <summary>(504) 345-6483 from 5043456483; anything else is shown as-is.</summary>
        private static string Pretty(string digits)
        {
            if (string.IsNullOrWhiteSpace(digits)) return null;
            string d = digits.Trim();
            if (d.Length == 11 && d[0] == '1') d = d.Substring(1);
            if (d.Length != 10) return digits;

            foreach (char c in d) if (!char.IsDigit(c)) return digits;
            return "(" + d.Substring(0, 3) + ") " + d.Substring(3, 3) + "-" + d.Substring(6);
        }

        private static string Ellipsize(Graphics g, string text, Font font, int maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (g.MeasureString(text, font).Width <= maxWidth) return text;

            for (int len = text.Length - 1; len > 1; len--)
            {
                string t = text.Substring(0, len) + "...";
                if (g.MeasureString(t, font).Width <= maxWidth) return t;
            }
            return text;
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ticker?.Stop();
                ticker?.Dispose();
                closeTimer?.Stop();
                closeTimer?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
