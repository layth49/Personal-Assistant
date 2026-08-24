using Personal_Assistant.Configuration;
using Personal_Assistant.Resume;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
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

        public DateTime CreatedAt { get; set; }

        // The countdown this was set for, in seconds. Zero for an alarm, which
        // was never a duration. Kept because it is the only evidence of what the
        // user actually asked for: "ten minutes" and "nineteen hours" are the
        // same pending item once FireAt is computed, and they deserve very
        // different treatment after a two-day shutdown.
        public int DurationSeconds { get; set; }

        // Whether this is pinned to a moment in the world rather than to a
        // stretch of time.
        //
        // THE distinction that makes resume work. A plain "timer for 20 minutes"
        // is about twenty minutes of the user's attention, so shutting the
        // machine down pauses it and it resumes with whatever was left. A timer
        // set for a release at 09:00 tomorrow is about 09:00 tomorrow, and no
        // amount of downtime moves that. Pausing the second kind announces a
        // Wednesday release on Friday; running the first kind on wall-clock
        // means a pasta timer that "finished" some time during the night.
        //
        // Alarms are always anchored: "wake me at 7" was never a duration.
        public bool Anchored { get; set; }

        // The real-world event this is waiting on, if any — "Re:Zero Season 4
        // part release". Set only when the user tied the timer to something that
        // either happens or doesn't, which is what lets it be checked rather
        // than merely announced. Null for an ordinary timer.
        public string Subject { get; set; }

        /// <summary>
        /// How late this may be and still be worth mentioning after a restart.
        ///
        /// Per item, because staleness is per item. A ten-minute timer that
        /// elapsed at 3am is noise by breakfast; a reminder tied to something
        /// that happened is still news hours later, and the longer the original
        /// wait the longer it stays news — nobody sets a nineteen-hour timer
        /// about something that expires in twenty minutes.
        /// </summary>
        public TimeSpan CatchUpWindow(TimeSpan baseWindow)
        {
            // An unanchored timer is paused rather than raced against the clock,
            // so the only way it can be late at all is the sliver between the
            // last heartbeat and the process actually dying. That is a minute at
            // most, and always worth saying.
            if (!Anchored) return baseWindow;

            TimeSpan proportional = TimeSpan.FromSeconds(DurationSeconds * 0.25);
            TimeSpan window = proportional > baseWindow ? proportional : baseWindow;

            // A day is the ceiling regardless. Past that the user has restarted,
            // slept, and moved on, and an announcement is archaeology.
            TimeSpan cap = TimeSpan.FromHours(24);
            return window > cap ? cap : window;
        }

        /// <summary>How this reads in a catch-up sentence, without a leading capital.</summary>
        public string DescribeMissed(DateTime now)
        {
            string late = DescribeLateness(now - FireAt);
            if (!string.IsNullOrWhiteSpace(Subject)) return $"your reminder about {Subject} came due {late}";
            if (!string.IsNullOrWhiteSpace(Label)) return $"your reminder to {Label} came due {late}";
            return Kind == ReminderKind.Alarm ? $"your alarm went off {late}" : $"your timer finished {late}";
        }

        private static string DescribeLateness(TimeSpan late)
        {
            if (late < TimeSpan.FromMinutes(2)) return "just now";
            if (late < TimeSpan.FromHours(1)) return $"{late.TotalMinutes:F0} minutes ago";
            if (late < TimeSpan.FromHours(24)) return $"{late.TotalHours:F0} hours ago";
            return $"{late.TotalDays:F0} days ago";
        }
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

    // Scheduler for timers, alarms, and reminders. A single background ticker
    // checks once a second for due items and hands each to an injected announce
    // callback (which speaks it). An optional visual sink mirrors each item as an
    // on-screen countdown widget.
    //
    // Pending items SURVIVE A RESTART (see ReminderStore). They did not use to:
    // this was the one scheduling path in the app with no store behind it, so
    // "set a timer for the release in nineteen hours" was silently destroyed by
    // the next reboot — with nothing logged, because from the process's point of
    // view nothing had gone wrong.
    public class ReminderService : IDisposable
    {
        private readonly List<ScheduledItem> items = new List<ScheduledItem>();
        private readonly object gate = new object();
        private readonly Func<string, Task> announce;
        private readonly Func<string, Task> prepare;
        private readonly Func<ScheduledItem, Task> onEventDue;
        private readonly IReminderVisual visual;
        private readonly Timer ticker;
        private readonly bool persist;
        private int nextId = 1;

        /// <param name="persist">
        /// Whether pending items are written to disk and restored on the next
        /// run. Default false, and only Program.Main passes true — every harness
        /// gets a scheduler that cannot write over the user's real timers, the
        /// same guarantee ConversationMemory makes for the real conversation.
        /// </param>
        /// <param name="onEventDue">
        /// Where an item carrying a Subject goes when its moment arrives, instead
        /// of being announced. A deadline someone guessed is not evidence the
        /// thing happened, so these are handed to something that can go and find
        /// out. Null leaves them announcing like any other reminder, which is the
        /// honest degradation when no watcher is wired.
        /// </param>
        public ReminderService(
            Func<string, Task> announce,
            IReminderVisual visual = null,
            Func<string, Task> prepare = null,
            bool persist = false,
            Func<ScheduledItem, Task> onEventDue = null)
        {
            this.announce = announce ?? throw new ArgumentNullException(nameof(announce));
            this.visual = visual;
            this.prepare = prepare;
            this.persist = persist;
            this.onEventDue = onEventDue;
            // Check every second; first check after one second.
            ticker = new Timer(_ => Tick(), null, 1000, 1000);
        }

        /// <summary>
        /// Schedules a countdown timer/reminder. Returns the resulting fire time.
        /// </summary>
        /// <param name="subject">
        /// The real-world event this is waiting on, if the user tied it to one.
        /// Supplying it also anchors the timer — see ScheduledItem.Anchored.
        /// </param>
        public DateTime AddTimer(int durationSeconds, string label, string subject = null)
        {
            if (durationSeconds < 1) durationSeconds = 1;
            var item = new ScheduledItem
            {
                FireAt = DateTime.Now.AddSeconds(durationSeconds),
                Label = Clean(label),
                Kind = ReminderKind.Timer,
                CreatedAt = DateTime.Now,
                DurationSeconds = durationSeconds,
                Subject = Clean(subject),
                // A timer is a duration unless it was tied to something that
                // happens on its own schedule.
                Anchored = !string.IsNullOrWhiteSpace(subject)
            };
            lock (gate)
            {
                item.Id = nextId++;
                items.Add(item);
                PersistLocked();
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
                Kind = ReminderKind.Alarm,
                CreatedAt = DateTime.Now,
                // "Wake me at 7" is a moment, not a stretch of time. Pausing it
                // over a shutdown would move the alarm, which is the one thing an
                // alarm may never do.
                Anchored = true
            };
            lock (gate)
            {
                item.Id = nextId++;
                items.Add(item);
                PersistLocked();
            }
            visual?.Show(item.Id, item.Label, item.FireAt, item.Kind);
            Prepare(item);
            return fireAt;
        }

        /// <summary>
        /// Restores pending items from the last run and re-arms them, returning
        /// what it found. Call once at startup, before the first Tick can matter.
        ///
        /// <paramref name="lastSeen"/> is when the assistant was last alive (see
        /// Downtime). It is what makes pause/resume possible at all: without it
        /// there is no way to tell time the user spent waiting from time the
        /// machine spent switched off, and every unanchored timer degrades to
        /// wall-clock — the honest fallback, not a silent one.
        /// </summary>
        public ResumeSummary Restore(DateTime? lastSeen)
        {
            var summary = new ResumeSummary();
            if (!persist) return summary;

            List<ScheduledItem> loaded = ReminderStore.Load();
            if (loaded.Count == 0) return summary;

            DateTime now = DateTime.Now;
            TimeSpan baseWindow = TimeSpan.FromMinutes(
                LaithConfig.Int("ReminderCatchUpMinutes", 60, 0, 10080));

            var live = new List<ScheduledItem>();
            var eventDue = new List<ScheduledItem>();

            foreach (ScheduledItem item in loaded)
            {
                // Anchored items keep the moment they were given. Unanchored ones
                // are shifted forward by however long the assistant was off, so
                // what is preserved is the REMAINING time rather than the deadline.
                if (!item.Anchored && lastSeen.HasValue)
                {
                    TimeSpan remaining = item.FireAt - lastSeen.Value;
                    if (remaining > TimeSpan.Zero) item.FireAt = now + remaining;
                }

                if (item.FireAt > now)
                {
                    live.Add(item);
                    summary.Resumed.Add(Describe(item));
                    continue;
                }

                // Came due while we were off.

                // Something tied to a real event skips the staleness test
                // entirely. "Did it happen" stays worth knowing for far longer
                // than "your timer went off" — a release you were waiting for is
                // still news three days later — and the watcher has its own
                // horizon for giving up, which is the right place to decide it.
                if (!string.IsNullOrWhiteSpace(item.Subject) && onEventDue != null)
                {
                    eventDue.Add(item);
                    summary.Resumed.Add($"picking back up on {item.Subject}");
                    continue;
                }

                if (now - item.FireAt <= item.CatchUpWindow(baseWindow))
                {
                    summary.Missed.Add(item.DescribeMissed(now));
                }
                else
                {
                    summary.Dropped.Add(Describe(item));
                }
            }

            lock (gate)
            {
                items.Clear();
                items.AddRange(live);
                nextId = live.Count == 0 ? 1 : live.Max(i => i.Id) + 1;
                // Written back immediately: the missed and dropped items are gone
                // now, and a crash before the next save would resurrect every one
                // of them on the following start.
                PersistLocked();
            }

            foreach (ScheduledItem item in live)
            {
                visual?.Show(item.Id, item.Label, item.FireAt, item.Kind);
                Prepare(item);
            }

            // Handed over outside the lock and off the startup path: picking one
            // of these up may mean a network lookup, and Main must not sit behind
            // it while the wake word is still unarmed.
            foreach (ScheduledItem item in eventDue)
            {
                ScheduledItem captured = item;
                Task.Run(async () =>
                {
                    try { await onEventDue(captured).ConfigureAwait(false); }
                    catch (Exception ex) { Console.WriteLine($"[reminders] resuming '{captured.Subject}' failed: {ex.Message}"); }
                });
            }

            Console.WriteLine($"[reminders] {summary.LogLine()}");
            foreach (string s in summary.Dropped) Console.WriteLine($"[reminders] too stale to mention: {s}");

            return summary;
        }

        private static string Describe(ScheduledItem item)
        {
            string what = !string.IsNullOrWhiteSpace(item.Subject) ? item.Subject
                : !string.IsNullOrWhiteSpace(item.Label) ? item.Label
                : item.Kind == ReminderKind.Alarm ? "alarm" : "timer";
            return $"{what} at {item.FireAt:ddd HH:mm}";
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
                PersistLocked();
            }
            visual?.Clear();
            return count;
        }

        // Writes the current items to disk. MUST be called with `gate` held.
        //
        // Snapshot and write are one step for the reason TriggerStore learned the
        // hard way: serialising the writes alone still lets a thread holding an
        // older snapshot land last and put back an item that has already fired.
        private void PersistLocked()
        {
            if (!persist) return;
            ReminderStore.Save(items.OrderBy(i => i.Id).ToList());
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
                // Inside the lock, and only when something actually went: a save
                // on every idle second would rewrite the file 86,400 times a day.
                if (due != null) PersistLocked();
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

            // An item tied to a real event does not announce itself. Its deadline
            // is when to go and LOOK, not what to say — the countdown expiring is
            // a fact about a guess somebody made, and "Reminder: Re:Zero Season 4
            // part release" said at a moment nothing was verified is exactly the
            // confident-and-wrong failure the whole watch path exists to avoid.
            if (!string.IsNullOrWhiteSpace(item.Subject) && onEventDue != null)
            {
                try
                {
                    await onEventDue(item).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[reminder] handing '{item.Subject}' over failed: {ex.Message}");
                }
                return;
            }

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

    // Where pending timers and alarms live between runs.
    //
    // Deliberately the same shape, the same discipline and the same failure
    // policy as TriggerStore, which already solved this: AppData rather than next
    // to the exe (a rebuild must not wipe the user's timers), a per-write temp
    // name plus File.Replace so an interrupted write cannot leave no store at
    // all, one process-wide save lock, and every failure non-fatal.
    public static class ReminderStore
    {
        // LAITH_REMINDERS_PATH relocates the store, mostly so a harness can run
        // against a scratch file instead of the user's real pending timers.
        public static string Path
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("LAITH_REMINDERS_PATH");
                if (!string.IsNullOrWhiteSpace(over)) return over.Trim();

                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LAITH",
                    "reminders.json");
            }
        }

        private static readonly object saveGate = new object();

        public static List<ScheduledItem> Load()
        {
            var loaded = new List<ScheduledItem>();
            try
            {
                if (!File.Exists(Path)) return loaded;
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path)))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) return loaded;
                    foreach (JsonElement el in doc.RootElement.EnumerateArray())
                    {
                        ScheduledItem item = ReadOne(el);
                        if (item != null) loaded.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[reminders] could not read {Path}: {ex.Message}");
            }
            return loaded;
        }

        public static void Save(IEnumerable<ScheduledItem> items)
        {
            List<ScheduledItem> list = items?.ToList() ?? new List<ScheduledItem>();

            lock (saveGate)
            {
                string temp = null;
                try
                {
                    string path = Path;
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));

                    temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                    {
                        writer.WriteStartArray();
                        foreach (ScheduledItem i in list) WriteOne(writer, i);
                        writer.WriteEndArray();
                    }

                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                    temp = null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[reminders] could not save to {Path}: {ex.Message}");
                }
                finally
                {
                    if (temp != null)
                    {
                        try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    }
                }
            }
        }

        private static void WriteOne(Utf8JsonWriter w, ScheduledItem i)
        {
            w.WriteStartObject();
            w.WriteNumber("id", i.Id);
            // Round-trip precision: which day a pending item falls on is the
            // whole point, and "HH:mm" cannot say it.
            w.WriteString("fire_at", i.FireAt.ToString("o", CultureInfo.InvariantCulture));
            w.WriteString("kind", i.Kind.ToString());
            w.WriteBoolean("anchored", i.Anchored);
            if (i.DurationSeconds > 0) w.WriteNumber("duration_seconds", i.DurationSeconds);
            if (i.Label != null) w.WriteString("label", i.Label);
            if (i.Subject != null) w.WriteString("subject", i.Subject);
            w.WriteString("created_at", i.CreatedAt.ToString("o", CultureInfo.InvariantCulture));
            w.WriteEndObject();
        }

        private static ScheduledItem ReadOne(JsonElement el)
        {
            try
            {
                if (el.ValueKind != JsonValueKind.Object) return null;

                DateTime? fireAt = GetDate(el, "fire_at");
                // Without a moment there is no item. Everything else has a
                // sensible default; this does not.
                if (!fireAt.HasValue) return null;

                return new ScheduledItem
                {
                    Id = GetInt(el, "id", 0),
                    FireAt = fireAt.Value,
                    Kind = GetEnum(el, "kind", ReminderKind.Timer),
                    Anchored = GetBool(el, "anchored", false),
                    DurationSeconds = GetInt(el, "duration_seconds", 0),
                    Label = GetString(el, "label"),
                    Subject = GetString(el, "subject"),
                    CreatedAt = GetDate(el, "created_at") ?? DateTime.Now
                };
            }
            catch (Exception ex)
            {
                // One bad entry loses one reminder, not the file.
                Console.WriteLine($"[reminders] skipping unreadable entry: {ex.Message}");
                return null;
            }
        }

        private static string GetString(JsonElement el, string name) =>
            el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        private static int GetInt(JsonElement el, string name, int fallback) =>
            el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number &&
            v.TryGetInt32(out int parsed)
                ? parsed
                : fallback;

        private static bool GetBool(JsonElement el, string name, bool fallback) =>
            el.TryGetProperty(name, out JsonElement v) &&
            (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
                ? v.GetBoolean()
                : fallback;

        private static T GetEnum<T>(JsonElement el, string name, T fallback) where T : struct =>
            Enum.TryParse(GetString(el, name) ?? string.Empty, ignoreCase: true, out T parsed)
                ? parsed
                : fallback;

        private static DateTime? GetDate(JsonElement el, string name)
        {
            string raw = GetString(el, name);
            if (raw == null) return null;
            return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTime parsed)
                ? parsed
                : (DateTime?)null;
        }
    }
}
