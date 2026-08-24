using Microsoft.Win32;
using Personal_Assistant.Configuration;
using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Personal_Assistant.Presence
{
    // The answer to "may LAITH speak right now, unprompted?".
    public struct PresenceVerdict
    {
        public bool Ready;
        public string Reason; // why not, for the log. Null when Ready.

        public static PresenceVerdict Yes => new PresenceVerdict { Ready = true };
        public static PresenceVerdict No(string reason) =>
            new PresenceVerdict { Ready = false, Reason = reason };
    }

    // Decides whether an unprompted announcement should actually be made.
    //
    // Everything the assistant says on its own initiative goes through here
    // first. Without it, proactive features are the reason people turn proactive
    // features off: the machine talks to an empty room, or at 3am, or over the
    // top of something you were concentrating on.
    //
    // Three independent reasons to stay quiet, checked cheaply enough to run on
    // a one-second ticker:
    //
    //   away    — no keyboard or mouse input for a while. The strongest signal,
    //             and the only one that is really about presence.
    //   locked  — the workstation is locked. Redundant with `away` most of the
    //             time (a locked session accrues idle time normally) but it is
    //             immediate, where idle takes minutes to build up.
    //   quiet   — inside the configured quiet hours, or explicitly muted.
    //
    // This gate only ever says "not now". It does not decide what happens next —
    // whether a held announcement is retried or dropped belongs to the caller,
    // because that depends on whether the thing being announced goes stale.
    public sealed class PresenceGate : IDisposable
    {
        // How long without input before the user counts as away. Fifteen minutes
        // is deliberately generous: the cost of speaking to an empty room is low,
        // and the cost of swallowing something you were waiting for is high.
        private readonly TimeSpan idleThreshold =
            TimeSpan.FromMinutes(LaithConfig.Int("PresenceIdleMinutes", 15, 1, 240));

        private readonly TimeSpan? quietStart;
        private readonly TimeSpan? quietEnd;

        // Set by SessionSwitch. Volatile because it is written from the
        // SystemEvents thread and read from the trigger ticker.
        private volatile bool locked;

        private DateTime? mutedUntil;
        private readonly object gate = new object();

        // "Is the user mid-conversation with the assistant right now?"
        //
        // The one busy signal that has to be here rather than in a trigger: an
        // announcement during a Live conversation plays over the model's reply,
        // because SayClip takes SpeechService.sayGate and LiveSession never does,
        // so there is nothing for it to wait on. On speakers it is worse — the
        // Live microphone gate is driven only by audio arriving down the socket
        // (LiveAudioPipeline.EnqueueAssistantAudio) and knows nothing about clip
        // playback, so the announcement lands in an open mic and the model
        // answers the assistant's own voice.
        private readonly Func<bool> isBusy;

        // Where "how long since the user touched anything" comes from. Injectable
        // so tests are deterministic: harnesses that relied on the real reading
        // passed while the machine was in use and failed later the same session
        // once it had been sitting idle for longer than the threshold — the
        // assertions were quietly about the machine, not the code.
        private readonly Func<TimeSpan> idleSource;

        /// <param name="isBusy">
        /// Optional: true while the assistant is in a conversation. Null means
        /// "never busy", which is the right default for a gate constructed
        /// without a Live session to ask.
        /// </param>
        /// <param name="idleSource">
        /// Optional: how long since the last keyboard or mouse input. Defaults to
        /// the real system reading.
        /// </param>
        public PresenceGate(Func<bool> isBusy = null, Func<TimeSpan> idleSource = null)
        {
            this.isBusy = isBusy;
            this.idleSource = idleSource ?? IdleTime;

            quietStart = ParseTimeOfDay(LaithConfig.Text("QuietHoursStart", "23:00"));
            quietEnd = ParseTimeOfDay(LaithConfig.Text("QuietHoursEnd", "07:00"));

            // Lock state is an optimisation, not a requirement: if this never
            // fires (SystemEvents needs a message pump, which this process has
            // via the timer widget's UI thread, but that is not something to
            // depend on) the idle check still catches an absent user a few
            // minutes later. Degrading to "slower" rather than "wrong" is why
            // the two checks are independent.
            try
            {
                SystemEvents.SessionSwitch += OnSessionSwitch;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[presence] session lock detection unavailable: {ex.Message}");
            }

            // These settings resolve here, which is after LaithConfig.Dump() has
            // already printed the startup line — so they would not appear in it.
            // Since "I changed it and nothing happened" is exactly what that line
            // exists to answer, the gate reports its own.
            Console.WriteLine(
                $"[presence] idle threshold {idleThreshold.TotalMinutes:F0}m, quiet hours " +
                (quietStart.HasValue && quietEnd.HasValue && quietStart != quietEnd
                    ? $"{quietStart:hh\\:mm}-{quietEnd:hh\\:mm}"
                    : "off"));
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                case SessionSwitchReason.SessionLogoff:
                case SessionSwitchReason.ConsoleDisconnect:
                case SessionSwitchReason.RemoteDisconnect:
                    locked = true;
                    break;
                case SessionSwitchReason.SessionUnlock:
                case SessionSwitchReason.SessionLogon:
                case SessionSwitchReason.ConsoleConnect:
                case SessionSwitchReason.RemoteConnect:
                    locked = false;
                    break;
            }
            Console.WriteLine($"[presence] session {e.Reason} — locked={locked}");
        }

        /// <param name="respectQuietHours">
        /// False for announcements whose whole point is the time they happen at —
        /// a prayer time inside quiet hours is not noise, it is the feature. The
        /// presence checks still apply either way, so an announcement exempted
        /// here still won't be made to an empty desk.
        /// </param>
        public PresenceVerdict Check(bool respectQuietHours = true)
        {
            lock (gate)
            {
                if (mutedUntil.HasValue)
                {
                    if (DateTime.Now < mutedUntil.Value)
                        return PresenceVerdict.No($"muted until {mutedUntil.Value:HH:mm}");
                    mutedUntil = null;
                }
            }

            if (locked) return PresenceVerdict.No("workstation locked");

            // Checked before presence: someone mid-conversation is emphatically
            // present, so the idle test would wave this straight through.
            if (isBusy != null)
            {
                bool busy;
                try { busy = isBusy(); }
                catch (Exception ex)
                {
                    // Unknown is not "free to talk over". A conversation is the
                    // one state where speaking anyway actively breaks something.
                    Console.WriteLine($"[presence] busy check threw: {ex.Message}");
                    busy = true;
                }
                if (busy) return PresenceVerdict.No("in a conversation");
            }

            TimeSpan idle;
            try { idle = idleSource(); }
            catch (Exception ex)
            {
                // Same direction as IdleTime's own failure mode: an unreadable
                // presence check should make the assistant talk too much, not go
                // silent for reasons nobody can see.
                Console.WriteLine($"[presence] idle check threw: {ex.Message}");
                idle = TimeSpan.Zero;
            }
            if (idle >= idleThreshold)
                return PresenceVerdict.No($"idle for {idle.TotalMinutes:F0}m");

            if (respectQuietHours && InQuietHours(DateTime.Now.TimeOfDay))
                return PresenceVerdict.No("quiet hours");

            return PresenceVerdict.Yes;
        }

        // Silences every unprompted announcement for a while. Wired to call
        // screening (nothing may speak into a live phone call — the caller's audio
        // arrives by loopback on the speakers, so an announcement would be heard by
        // the caller AND fed back to the model as though they had said it), and
        // this is also the API a "not now, be quiet" command would call.
        public void MuteFor(TimeSpan duration)
        {
            lock (gate) { mutedUntil = DateTime.Now.Add(duration); }
            Console.WriteLine($"[presence] muted for {duration.TotalMinutes:F0}m");
        }

        public void Unmute() => MuteUntil(null);

        /// <summary>When the current mute ends, or null when nothing is muted.</summary>
        public DateTime? MutedUntil
        {
            get
            {
                lock (gate)
                {
                    return mutedUntil.HasValue && DateTime.Now < mutedUntil.Value ? mutedUntil : null;
                }
            }
        }

        /// <summary>
        /// Sets the mute deadline outright — null unmutes.
        /// </summary>
        /// <remarks>
        /// The primitive behind MuteFor/Unmute, exposed so a temporary hold can put
        /// back what it found rather than clearing it. A screened call mutes for its
        /// own duration; if Layth had already asked for quiet until midnight, ending
        /// that early because a stranger rang is not what he asked for.
        /// </remarks>
        public void MuteUntil(DateTime? until)
        {
            lock (gate) { mutedUntil = until; }
            Console.WriteLine(until.HasValue
                ? $"[presence] muted until {until.Value:HH:mm}"
                : "[presence] unmuted");
        }

        // Time since the last keyboard or mouse input anywhere on the desktop.
        // Zero if the call fails, which reads as "present" — the failure mode of
        // an unavailable presence check should be an assistant that talks too
        // much, not one that has gone silent for reasons nobody can see.
        public static TimeSpan IdleTime()
        {
            var info = new LASTINPUTINFO();
            info.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;

            // Both values are milliseconds-since-boot in the 32-bit TickCount
            // domain, which wraps every ~49 days. Unchecked unsigned subtraction
            // is correct across that wrap; comparing them as signed values is not.
            uint elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
            return TimeSpan.FromMilliseconds(elapsed);
        }

        public bool IsLocked => locked;

        // Quiet hours normally wrap midnight (23:00 -> 07:00), so "inside" is the
        // union of two ranges rather than one interval.
        private bool InQuietHours(TimeSpan now)
        {
            if (!quietStart.HasValue || !quietEnd.HasValue) return false;
            TimeSpan start = quietStart.Value, end = quietEnd.Value;
            if (start == end) return false;                 // zero-length = disabled
            if (start < end) return now >= start && now < end;
            return now >= start || now < end;               // wraps midnight
        }

        private static TimeSpan? ParseTimeOfDay(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (TimeSpan.TryParseExact(text.Trim(), new[] { @"hh\:mm", @"h\:mm" },
                    CultureInfo.InvariantCulture, out TimeSpan parsed))
            {
                return parsed;
            }
            Console.WriteLine($"[presence] '{text}' is not an HH:mm time — quiet hours disabled.");
            return null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        public void Dispose()
        {
            try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
        }
    }
}
