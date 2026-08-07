using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Personal_Assistant.Reminders
{
    // A pending timer / alarm / reminder.
    public sealed class ScheduledItem
    {
        public int Id { get; set; }
        public DateTime FireAt { get; set; }
        public string Label { get; set; }
        public ReminderKind Kind { get; set; }
    }

    public enum ReminderKind
    {
        Timer, // countdown ("in 5 minutes")
        Alarm  // wall-clock time ("at 7 AM")
    }

    // Optional on-screen visualization sink. Implemented by the WinForms widget
    // host; the scheduler drives it but stays the source of truth for firing.
    // All calls may arrive from the scheduler's background thread, so the
    // implementation is responsible for marshalling to its own UI thread.
    public interface IReminderVisual
    {
        void Show(int id, string label, DateTime fireAt, ReminderKind kind);
        void Fired(int id);   // flash + dismiss the widget
        void Remove(int id);  // cancelled individually
        void Clear();         // all cancelled
    }

    // In-memory scheduler for timers, alarms, and reminders. A single background
    // ticker checks once a second for due items and hands each to an injected
    // announce callback (which speaks it). An optional visual sink mirrors each
    // item as an on-screen countdown widget. Not persisted — pending items are
    // lost on restart, matching the in-memory design used elsewhere.
    public class ReminderService : IDisposable
    {
        private readonly List<ScheduledItem> items = new List<ScheduledItem>();
        private readonly object gate = new object();
        private readonly Func<string, Task> announce;
        private readonly Func<string, Task> prepare;
        private readonly IReminderVisual visual;
        private readonly Timer ticker;
        private int nextId = 1;

        public ReminderService(
            Func<string, Task> announce,
            IReminderVisual visual = null,
            Func<string, Task> prepare = null)
        {
            this.announce = announce ?? throw new ArgumentNullException(nameof(announce));
            this.visual = visual;
            this.prepare = prepare;
            // Check every second; first check after one second.
            ticker = new Timer(_ => Tick(), null, 1000, 1000);
        }

        // Schedules a countdown timer/reminder. Returns the resulting fire time.
        public DateTime AddTimer(int durationSeconds, string label)
        {
            if (durationSeconds < 1) durationSeconds = 1;
            var item = new ScheduledItem
            {
                FireAt = DateTime.Now.AddSeconds(durationSeconds),
                Label = Clean(label),
                Kind = ReminderKind.Timer
            };
            lock (gate)
            {
                item.Id = nextId++;
                items.Add(item);
            }
            visual?.Show(item.Id, item.Label, item.FireAt, item.Kind);
            Prepare(item);
            return item.FireAt;
        }

        // Schedules an alarm/reminder for a wall-clock time. `timeText` is parsed
        // leniently (24-hour "HH:mm", "h:mm tt", "7 AM", etc.); a time already
        // past today rolls to tomorrow. Returns the fire time, or null if the
        // time couldn't be understood.
        public DateTime? AddAlarm(string timeText, string label)
        {
            if (!TryParseNextOccurrence(timeText, out DateTime fireAt))
            {
                return null;
            }
            var item = new ScheduledItem
            {
                FireAt = fireAt,
                Label = Clean(label),
                Kind = ReminderKind.Alarm
            };
            lock (gate)
            {
                item.Id = nextId++;
                items.Add(item);
            }
            visual?.Show(item.Id, item.Label, item.FireAt, item.Kind);
            Prepare(item);
            return fireAt;
        }

        // Snapshot of pending items, soonest first.
        public IReadOnlyList<ScheduledItem> Pending()
        {
            lock (gate)
            {
                return items.OrderBy(i => i.FireAt).ToList();
            }
        }

        // Cancels all pending items; returns how many were removed.
        public int CancelAll()
        {
            int count;
            lock (gate)
            {
                count = items.Count;
                items.Clear();
            }
            visual?.Clear();
            return count;
        }

        private void Tick()
        {
            List<ScheduledItem> due = null;
            lock (gate)
            {
                var now = DateTime.Now;
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    if (items[i].FireAt <= now)
                    {
                        (due ?? (due = new List<ScheduledItem>())).Add(items[i]);
                        items.RemoveAt(i);
                    }
                }
            }

            if (due == null) return;
            foreach (var item in due.OrderBy(i => i.FireAt))
            {
                _ = Fire(item);
            }
        }

        // The exact words Fire will speak. Shared with the scheduling path so the
        // announcement can be prepared ahead of time — the two must not drift, or
        // the preparation is done against a string that is never spoken.
        internal static string AnnouncementFor(string label, ReminderKind kind)
        {
            if (!string.IsNullOrWhiteSpace(label)) return $"Reminder: {label}.";
            return kind == ReminderKind.Alarm
                ? "Your alarm is going off."
                : "Your timer is done.";
        }

        // Called when an item is scheduled, with the line it will eventually say.
        // Rendering that line takes ~5-7s, which is far too long to do when the
        // timer actually fires — but a timer is set minutes before it goes off, so
        // doing it here means the clip is ready and waiting.
        private void Prepare(ScheduledItem item)
        {
            if (prepare == null) return;
            string message = AnnouncementFor(item.Label, item.Kind);

            // Deliberately not awaited: scheduling a timer must stay instant, and
            // a failed preparation is not an error — Fire falls back on its own.
            Task.Run(async () =>
            {
                try { await prepare(message).ConfigureAwait(false); }
                catch (Exception ex) { Console.WriteLine($"[reminder] prepare failed: {ex.Message}"); }
            });
        }

        private async Task Fire(ScheduledItem item)
        {
            // Flash + dismiss the on-screen widget in step with the announcement.
            visual?.Fired(item.Id);

            string message = AnnouncementFor(item.Label, item.Kind);

            try
            {
                await announce(message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[reminder] announce failed: {ex.Message}");
            }
        }

        // Parses a spoken time into the next future occurrence of that clock time.
        internal static bool TryParseNextOccurrence(string timeText, out DateTime fireAt)
        {
            fireAt = default(DateTime);
            return TryParseTimeOfDay(timeText, out TimeSpan tod) && ToNextOccurrence(tod, out fireAt);
        }

        /// <summary>
        /// Parses a spoken or written clock time into a time of day, leniently:
        /// "17:30", "5:30 PM", "7 AM", "07:00". Split out from
        /// TryParseNextOccurrence because standing rules need the time of day
        /// itself, not the next moment it comes round — a daily rule is stored as
        /// "08:00", and which 08:00 it means is decided every day.
        /// </summary>
        public static bool TryParseTimeOfDay(string timeText, out TimeSpan timeOfDay)
        {
            timeOfDay = default(TimeSpan);
            if (string.IsNullOrWhiteSpace(timeText)) return false;

            string t = timeText.Trim();

            // Preferred: 24-hour "HH:mm" or "H:mm" (what the LLM is asked to give).
            if (TimeSpan.TryParseExact(t, new[] { @"hh\:mm", @"h\:mm" }, CultureInfo.InvariantCulture, out TimeSpan tod) ||
                TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out tod))
            {
                if (tod >= TimeSpan.Zero && tod < TimeSpan.FromDays(1))
                {
                    timeOfDay = tod;
                    return true;
                }
            }

            // Lenient clock formats, e.g. "7 AM", "7:30 PM", "07:00".
            string[] formats =
            {
                "h:mm tt", "h:mmtt", "htt", "h tt", "hh:mm tt", "HH:mm", "H:mm", "h tt"
            };
            if (DateTime.TryParseExact(t, formats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime parsed) ||
                DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            {
                timeOfDay = parsed.TimeOfDay;
                return true;
            }

            return false;
        }

        private static bool ToNextOccurrence(TimeSpan timeOfDay, out DateTime fireAt)
        {
            fireAt = DateTime.Today.Add(timeOfDay);
            // If that moment already passed today, schedule for tomorrow.
            if (fireAt <= DateTime.Now) fireAt = fireAt.AddDays(1);
            return true;
        }

        private static string Clean(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return null;
            return label.Trim().TrimEnd('.', '!', '?');
        }

        public void Dispose()
        {
            ticker?.Dispose();
        }
    }
}
