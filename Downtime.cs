using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace Personal_Assistant.Resume
{
    // When the assistant was last alive, and therefore how long it was off.
    //
    // This one fact is the thing every kind of resume needs and nothing in the
    // app used to know. A timer cannot be resumed "from where it was left off"
    // without knowing when it was left off. A missed alarm cannot be judged
    // stale without knowing whether the machine was off for ten minutes or ten
    // days. A one-shot standing rule that came due during downtime is
    // indistinguishable from one that simply expired, unless something recorded
    // that there WAS downtime.
    //
    // A heartbeat rather than a clean-exit marker, because the shutdowns that
    // matter are exactly the ones nobody gets to write a marker for: a power
    // cut, a Windows update reboot, the process being killed from Task Manager.
    // A clean exit writes one final beat as well, which only makes the reading
    // more precise — it never becomes the thing correctness depends on.
    //
    // The file is a single ISO-8601 local timestamp. Not JSON: it is written
    // every minute for the life of the process, it holds one value, and a torn
    // write of one line costs at worst one interval of precision. Every failure
    // here is non-fatal — losing the heartbeat costs a good resume, and taking
    // the assistant down at startup because AppData was briefly unwritable is
    // very much worse.
    public sealed class Downtime : IDisposable
    {
        // LAITH_HEARTBEAT_PATH relocates it, for the same reason
        // LAITH_TRIGGERS_PATH exists: a harness that exercised this must not
        // overwrite the real last-seen and make the next real start think the
        // app never stopped.
        public static string Path
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("LAITH_HEARTBEAT_PATH");
                if (!string.IsNullOrWhiteSpace(over)) return over.Trim();

                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LAITH",
                    "heartbeat");
            }
        }

        // How often the beat is written. A minute is the resolution of every
        // decision made from it (was this timer due while we were off?), and
        // writing more often buys precision nothing asks for at the cost of a
        // disk write on a loop that runs forever.
        public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

        private Timer ticker;
        private int disposed;

        /// <summary>
        /// When the assistant was last known to be running, or null if there is
        /// no usable heartbeat — a first run, a wiped AppData, an unreadable
        /// file.
        ///
        /// MUST be called before <see cref="Start"/>. The heartbeat is a single
        /// value that Start immediately overwrites, so reading after starting
        /// destroys the very fact this class exists to preserve and quietly
        /// reports "no downtime" for every restart.
        /// </summary>
        public static DateTime? ReadLastSeen()
        {
            try
            {
                string path = Path;
                if (!File.Exists(path)) return null;

                string raw = File.ReadAllText(path).Trim();
                if (raw.Length == 0) return null;

                if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out DateTime parsed))
                {
                    return null;
                }

                // A heartbeat from the future is a clock that moved — a timezone
                // change, an NTP correction, a dual boot with a different idea of
                // UTC. Treating it as a real last-seen would make every pending
                // item look like it still has hours to run. Unknown is honest and
                // degrades to the old behaviour; a confident wrong answer does not.
                if (parsed > DateTime.Now.AddMinutes(5))
                {
                    Console.WriteLine(
                        $"[resume] heartbeat is {(parsed - DateTime.Now).TotalHours:F1}h in the future — " +
                        "clock changed, ignoring it");
                    return null;
                }

                return parsed;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[resume] could not read heartbeat: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// How long the assistant was off, given a last-seen. Zero when there is
        /// no heartbeat to compare against.
        ///
        /// Deliberately measured from the last beat rather than from a shutdown
        /// time nobody recorded, so it is an UNDER-estimate by up to one
        /// interval. That is the right direction to be wrong in: consumers use
        /// the gap to decide how much time a paused item still owes, and
        /// crediting the user a few extra seconds is invisible where charging
        /// them minutes they were never running is not.
        /// </summary>
        public static TimeSpan GapSince(DateTime? lastSeen)
        {
            if (!lastSeen.HasValue) return TimeSpan.Zero;
            TimeSpan gap = DateTime.Now - lastSeen.Value;
            return gap > TimeSpan.Zero ? gap : TimeSpan.Zero;
        }

        /// <summary>Human wording for a downtime gap, for the startup line.</summary>
        public static string Describe(TimeSpan gap)
        {
            if (gap < TimeSpan.FromMinutes(1)) return "under a minute";
            if (gap < TimeSpan.FromHours(1)) return $"{gap.TotalMinutes:F0}m";
            if (gap < TimeSpan.FromDays(1)) return $"{gap.TotalHours:F1}h";
            return $"{gap.TotalDays:F1} days";
        }

        /// <summary>Begins recording that the assistant is alive.</summary>
        public void Start()
        {
            Beat();
            ticker = new Timer(_ => Beat(), null, Interval, Interval);
        }

        private static void Beat()
        {
            try
            {
                string path = Path;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));

                // Written in place rather than through a temp file. The atomic
                // write TriggerStore does is right for a store whose loss costs
                // the user their standing rules; here a torn write costs at most
                // one interval of precision on a value refreshed a minute later,
                // and a temp-and-replace every minute forever is churn bought for
                // nothing.
                File.WriteAllText(path, DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
            }
            catch
            {
                // Silent by design. This runs once a minute for the life of the
                // process; a locked or full disk would otherwise produce 1,440
                // identical console lines a day about a best-effort convenience.
            }
        }

        /// <summary>
        /// Writes one last beat and stops. Idempotent — the process-exit hook and
        /// an ordinary Dispose can both run without the second racing a disposed
        /// timer.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { ticker?.Dispose(); } catch { }
            ticker = null;
            Beat();
        }
    }

    // What a restart found waiting for it.
    //
    // Returned by each thing that resumes rather than announced by it, because
    // the announcement has to be made ONCE for the whole app. Five services each
    // speaking their own catch-up line is how "what did I miss" becomes a
    // monologue at the moment the user sits down — and the machine has just
    // booted, so they are definitely there to hear all of it.
    public sealed class ResumeSummary
    {
        // Still pending, re-armed, nothing to say. Logged, not spoken.
        public List<string> Resumed { get; } = new List<string>();

        // Came due while the assistant was off and is still worth mentioning.
        // These are what gets spoken, together, in one sentence.
        public List<string> Missed { get; } = new List<string>();

        // Came due while off and is too stale to be worth a word. Logged so the
        // user can see it existed — silently discarding something they asked for
        // is the failure this whole feature is about — but never spoken.
        public List<string> Dropped { get; } = new List<string>();

        public bool AnythingHappened =>
            Resumed.Count > 0 || Missed.Count > 0 || Dropped.Count > 0;

        /// <summary>Folds another service's findings into this one.</summary>
        public void Absorb(ResumeSummary other)
        {
            if (other == null) return;
            Resumed.AddRange(other.Resumed);
            Missed.AddRange(other.Missed);
            Dropped.AddRange(other.Dropped);
        }

        /// <summary>
        /// The one sentence to speak, or null when nothing missed is worth
        /// saying. Says how many rather than listing beyond three: a spoken list
        /// stops being information somewhere around the fourth item, and this
        /// arrives unprompted while the user is still finding their chair.
        /// </summary>
        public string SpokenLine()
        {
            if (Missed.Count == 0) return null;

            string body;
            if (Missed.Count == 1)
            {
                body = Missed[0];
            }
            else if (Missed.Count <= 3)
            {
                body = string.Join(", ", Missed.Take(Missed.Count - 1)) +
                       " and " + Missed[Missed.Count - 1];
            }
            else
            {
                body = $"{Missed[0]}, {Missed[1]}, and {Missed.Count - 2} other things";
            }

            return $"While I was off, {body}.";
        }

        /// <summary>The console line, which does list everything.</summary>
        public string LogLine()
        {
            var parts = new List<string>();
            if (Resumed.Count > 0) parts.Add($"resumed {Resumed.Count}");
            if (Missed.Count > 0) parts.Add($"missed {Missed.Count}");
            if (Dropped.Count > 0) parts.Add($"dropped {Dropped.Count} stale");
            return parts.Count == 0 ? "nothing pending" : string.Join(", ", parts);
        }
    }
}
