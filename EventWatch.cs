using Personal_Assistant.Configuration;
using Personal_Assistant.LLMClient;
using Personal_Assistant.Resume;
using Personal_Assistant.Suggestions;
using Personal_Assistant.Triggers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Personal_Assistant.Events
{
    public enum WatchState
    {
        Waiting,   // still checking
        Confirmed, // the event happened and the user was told
        Abandoned  // checked for long enough; gave up and said so
    }

    // Something the user is waiting on that the world decides, not the clock.
    //
    // The distinction this whole file exists for: a timer knows when it is done,
    // a watch only knows when it is worth ASKING. "Set a timer for the Re:Zero
    // release in nineteen hours" produces a countdown that expires at a moment
    // somebody guessed — and a countdown that expires proves nothing. The episode
    // may have shipped early, slipped a week, or been pulled. A watch treats its
    // deadline as the first moment worth checking rather than the answer.
    public sealed class EventWatch
    {
        public int Id { get; set; }

        // What is being waited on, in words a search engine can use.
        public string Subject { get; set; }

        // The user's own wording, kept for reading the watch back to them. The
        // subject is written for a search; this is written for a person.
        public string Label { get; set; }

        // When it was expected. Never treated as when it happened.
        public DateTime Deadline { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime? LastCheckedAt { get; set; }
        public DateTime NextCheckAt { get; set; }

        // How many checks this watch has spent. Drives the backoff, and is the
        // number to look at when a watch has been quietly monopolising the model.
        public int Checks { get; set; }

        public WatchState State { get; set; }

        // Where to go, learned from the check that confirmed it. Only ever set on
        // confirmation: offering to open a link for something unreleased is
        // offering to open a 404.
        public string Url { get; set; }

        public string Describe() =>
            !string.IsNullOrWhiteSpace(Label) ? Label : Subject;

        /// <summary>
        /// How long to wait before asking again, given how many times we already
        /// have. Doubling from an hour, capped at twelve.
        ///
        /// Backoff rather than a fixed interval because the cost of asking is
        /// real (see EventWatchBudget) and the value decays: the hour after a
        /// release window opens is when a check is most likely to change the
        /// answer, and by day three the thing has almost certainly slipped rather
        /// than being minutes away.
        /// </summary>
        public TimeSpan Backoff()
        {
            switch (Checks)
            {
                case 0:
                case 1: return TimeSpan.FromHours(1);
                case 2: return TimeSpan.FromHours(2);
                case 3: return TimeSpan.FromHours(4);
                case 4: return TimeSpan.FromHours(8);
                default: return TimeSpan.FromHours(12);
            }
        }
    }

    // How many checks a day the assistant may spend.
    //
    // On `main` this exists to protect a hard quota: verification there runs on
    // gemini-2.5-flash, the one model the free tier rations to 20 requests a day.
    // Nothing here is metered — SearxNG is self-hosted and the model is on the
    // GPU under the desk — so that reason does not survive the port.
    //
    // A different one does, and it is the reason the cap is kept rather than
    // deleted. There is exactly ONE local model, and it is the same one the user
    // is talking to. A check costs a SearxNG round trip plus a generation that
    // occupies LM Studio for seconds, during which the assistant cannot answer
    // anything — on this box a cold 200-token response is 10-20s. A watch
    // re-checking on a tight loop would not run up a bill, it would make the
    // assistant feel broken, which is harder to diagnose and easier to blame on
    // the STT. Hence a cap, but a far more generous one than main's.
    public sealed class EventWatchBudget
    {
        private readonly object gate = new object();
        private DateTime countingSince = DateTime.Today;
        private int spentToday;

        public int PerDay => LaithConfig.Int("EventVerifyChecksPerDay", 48, 0, 500);

        public bool Allows(out string why)
        {
            lock (gate)
            {
                if (countingSince.Date != DateTime.Today)
                {
                    countingSince = DateTime.Today;
                    spentToday = 0;
                }

                int cap = PerDay;
                if (spentToday >= cap)
                {
                    why = $"{spentToday} checks already today (cap {cap})";
                    return false;
                }
            }
            why = null;
            return true;
        }

        public void Record()
        {
            lock (gate) { spentToday++; }
        }
    }

    // Keeps track of what the user is waiting on, across restarts.
    //
    // One sweep on the trigger engine rather than a trigger per watch, for the
    // reasons SuggestionService gives for the same choice: one place where the
    // presence gate applies, one place where the budget is consulted, and no
    // per-watch re-arming to get wrong. It also means a watch that outlives its
    // grace window is not lost — the sweep simply finds it again next minute,
    // where a one-shot trigger dropped after grace would have gone quiet until
    // the next restart.
    public sealed class EventWatchService
    {
        private const string SweepTrigger = "watch:tick";

        private readonly TriggerService triggers;
        private readonly SuggestionService suggestions;
        private readonly Func<string, Task> announce;
        private readonly Func<string, CancellationToken, Task<EventVerdict>> verify;
        private readonly Action<string> openUrl;

        private readonly List<EventWatch> watches = new List<EventWatch>();
        private readonly object gate = new object();
        private readonly EventWatchBudget budget = new EventWatchBudget();
        private readonly bool persist;

        private string lastHeldReason;

        public EventWatchService(
            TriggerService triggers,
            Func<string, Task> announce,
            SuggestionService suggestions = null,
            Func<string, CancellationToken, Task<EventVerdict>> verify = null,
            Action<string> openUrl = null,
            bool persist = false)
        {
            this.triggers = triggers ?? throw new ArgumentNullException(nameof(triggers));
            this.announce = announce ?? throw new ArgumentNullException(nameof(announce));
            this.suggestions = suggestions;
            // SearxNG search -> local model judges only what came back. Same
            // search-first shape as main's Gemini grounding; see
            // LocalLLMService.VerifyEventAsync for why an empty result set is a
            // refusal here rather than a fallback to the model's own knowledge.
            this.verify = verify ?? LocalLLMService.VerifyEventAsync;
            // Injected so a harness can assert what would have been opened rather
            // than opening it. The default is the same UseShellExecute launch the
            // open_web_search tool uses.
            this.openUrl = openUrl ?? DefaultOpen;
            this.persist = persist;
        }

        private static void DefaultOpen(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Console.WriteLine($"[watch] could not open {url}: {ex.Message}"); }
        }

        /// <summary>How many days a watch keeps asking before it gives up.</summary>
        private static TimeSpan AbandonAfter =>
            TimeSpan.FromDays(LaithConfig.Int("EventWatchAbandonDays", 14, 1, 365));

        /// <summary>
        /// Loads what was being waited on when the assistant last stopped.
        ///
        /// Nothing is announced here and nothing is checked here: a watch whose
        /// deadline passed during downtime simply has a NextCheckAt in the past,
        /// which the first sweep picks up a minute later, behind the presence
        /// gate like everything else. Checking inline at startup would fire a
        /// network call and possibly a spoken announcement into a machine that is
        /// still bringing its desktop up.
        /// </summary>
        public ResumeSummary Restore()
        {
            var summary = new ResumeSummary();
            if (!persist) return summary;

            List<EventWatch> loaded = EventWatchStore.Load();
            if (loaded.Count == 0) return summary;

            DateTime now = DateTime.Now;
            var live = new List<EventWatch>();

            foreach (EventWatch w in loaded)
            {
                // A confirmed or abandoned watch has already had its say. Kept
                // out of the live list rather than kept and skipped, so the store
                // does not grow forever with things nobody is waiting for.
                if (w.State != WatchState.Waiting) continue;

                if (now - w.Deadline > AbandonAfter)
                {
                    summary.Dropped.Add($"waiting on {w.Describe()}");
                    continue;
                }

                live.Add(w);
                summary.Resumed.Add(w.Deadline <= now
                    ? $"still checking on {w.Describe()}"
                    : $"waiting on {w.Describe()} until {w.Deadline:ddd HH:mm}");
            }

            lock (gate)
            {
                watches.Clear();
                watches.AddRange(live);
                PersistLocked();
            }

            if (live.Count > 0) Console.WriteLine($"[watch] resumed {live.Count} event watch(es)");
            foreach (string s in summary.Dropped) Console.WriteLine($"[watch] gave up on: {s}");

            return summary;
        }

        /// <summary>Starts the once-a-minute sweep.</summary>
        public void Start()
        {
            triggers.AddEvery(
                SweepTrigger,
                TimeSpan.FromMinutes(1),
                SweepAsync,
                // A confirmed release is worth telling someone about, but not
                // worth waking them for — unlike a prayer time, this can wait
                // until morning.
                respectQuietHours: true);
        }

        /// <summary>
        /// Begins waiting on something. Called when an anchored reminder comes
        /// due — either live, or because its moment passed while the assistant
        /// was off. The first check happens on the next sweep.
        /// </summary>
        public EventWatch Begin(string subject, string label, DateTime deadline)
        {
            if (string.IsNullOrWhiteSpace(subject)) return null;

            var watch = new EventWatch
            {
                Subject = subject.Trim(),
                Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
                Deadline = deadline,
                CreatedAt = DateTime.Now,
                // Now, not deadline: Begin is only called once the deadline has
                // arrived, and a NextCheckAt in the past is exactly what tells
                // the sweep this one is ready.
                NextCheckAt = DateTime.Now,
                State = WatchState.Waiting
            };

            lock (gate)
            {
                watch.Id = watches.Count == 0 ? 1 : watches.Max(w => w.Id) + 1;
                watches.Add(watch);
                PersistLocked();
            }

            Console.WriteLine($"[watch] now waiting on '{watch.Subject}'");
            return watch;
        }

        public IReadOnlyList<EventWatch> Snapshot()
        {
            lock (gate) { return watches.Where(w => w.State == WatchState.Waiting).OrderBy(w => w.Id).ToList(); }
        }

        /// <summary>Stops waiting on everything; returns how many went.</summary>
        public int CancelAll()
        {
            int count;
            lock (gate)
            {
                count = watches.Count;
                watches.Clear();
                PersistLocked();
            }
            return count;
        }

        // MUST be called with `gate` held — same rule, and the same reason, as
        // VoiceTriggers.PersistLocked: a snapshot taken outside the lock can land
        // after a newer write and resurrect a watch that has already resolved.
        private void PersistLocked()
        {
            if (!persist) return;
            EventWatchStore.Save(watches.OrderBy(w => w.Id).ToList());
        }

        /// <summary>
        /// Checks at most ONE due watch. Public so it can be driven directly in a
        /// harness rather than a minute at a time.
        ///
        /// One per sweep on purpose. Each check is a rationed network call, and
        /// each confirmation is something spoken out loud — three watches coming
        /// due in the same minute is three sentences over the top of each other,
        /// and the next sweep is only sixty seconds away.
        /// </summary>
        public async Task SweepAsync()
        {
            EventWatch due;
            DateTime now = DateTime.Now;

            lock (gate)
            {
                due = watches
                    .Where(w => w.State == WatchState.Waiting && w.NextCheckAt <= now)
                    .OrderBy(w => w.Deadline)
                    .FirstOrDefault();
            }
            if (due == null) return;

            // Given up on before spending a lookup on it.
            if (now - due.Deadline > AbandonAfter)
            {
                await AbandonAsync(due).ConfigureAwait(false);
                return;
            }

            if (!budget.Allows(out string why))
            {
                // Logged once per reason rather than once a minute for the rest
                // of the day.
                if (lastHeldReason != why)
                {
                    lastHeldReason = why;
                    Console.WriteLine($"[watch] holding off on '{due.Subject}' — {why}");
                }

                // Pushed out rather than left due, so a capped-out day does not
                // re-evaluate the same watch sixty times an hour.
                lock (gate)
                {
                    due.NextCheckAt = now.AddHours(1);
                    PersistLocked();
                }
                return;
            }
            lastHeldReason = null;

            budget.Record();
            Console.WriteLine($"[watch] checking '{due.Subject}' (check #{due.Checks + 1})");

            EventVerdict verdict;
            try
            {
                verdict = await verify(due.Subject, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // VerifyEventAsync does not throw, but it is injected, so a
                // harness or a future implementation might.
                verdict = EventVerdict.Unknown(ex.Message);
            }

            lock (gate)
            {
                due.Checks++;
                due.LastCheckedAt = now;
                PersistLocked();
            }

            switch (verdict.Result)
            {
                case EventVerdict.Outcome.Happened:
                    await ConfirmAsync(due, verdict).ConfigureAwait(false);
                    break;

                case EventVerdict.Outcome.NotYet:
                    await NotYetAsync(due, verdict).ConfigureAwait(false);
                    break;

                default:
                    // Silent. An unresolved lookup is not news, and saying "I
                    // couldn't tell" every hour is worse than saying nothing.
                    Console.WriteLine($"[watch] '{due.Subject}' unresolved: {verdict.Detail}");
                    Reschedule(due);
                    break;
            }
        }

        private async Task ConfirmAsync(EventWatch watch, EventVerdict verdict)
        {
            lock (gate)
            {
                watch.State = WatchState.Confirmed;
                watch.Url = verdict.Url;
                // Dropped from the live list: it is answered, and a resolved
                // watch left in the store is one the next restart re-reads,
                // re-arms and re-announces.
                watches.Remove(watch);
                PersistLocked();
            }

            string what = watch.Describe();
            string detail = string.IsNullOrWhiteSpace(verdict.Detail)
                ? $"{what} is out."
                : verdict.Detail.TrimEnd('.') + ".";

            Console.WriteLine($"[watch] confirmed '{watch.Subject}' — {detail}");

            // With somewhere to go, this becomes an offer rather than an
            // announcement, and the browser only opens if the user says yes.
            if (!string.IsNullOrWhiteSpace(verdict.Url) && suggestions != null)
            {
                string url = verdict.Url;
                await suggestions.OfferNow(
                    $"watch:{watch.Id}",
                    $"{detail} Want me to open it?",
                    () =>
                    {
                        openUrl(url);
                        return Task.FromResult("Opening it now.");
                    }).ConfigureAwait(false);
                return;
            }

            // No link, or nowhere to put the offer: say it plainly. Being told
            // the thing you were waiting for has happened is most of the value.
            await announce(detail).ConfigureAwait(false);
        }

        private async Task NotYetAsync(EventWatch watch, EventVerdict verdict)
        {
            Reschedule(watch);

            // Said ONCE, on the first check after the deadline, and never again.
            // The user set an expectation and it turned out to be wrong, which is
            // worth one sentence; every re-check after that is the assistant
            // quietly doing its job, which is worth none.
            if (watch.Checks != 1)
            {
                Console.WriteLine($"[watch] '{watch.Subject}' still not out — next check {watch.NextCheckAt:ddd HH:mm}");
                return;
            }

            string what = watch.Describe();
            string detail = string.IsNullOrWhiteSpace(verdict.Detail)
                ? $"{what} isn't out yet — I'll keep an eye on it."
                : verdict.Detail.TrimEnd('.') + ". I'll keep an eye on it.";

            Console.WriteLine($"[watch] '{watch.Subject}' not yet — {detail}");
            await announce(detail).ConfigureAwait(false);
        }

        private async Task AbandonAsync(EventWatch watch)
        {
            lock (gate)
            {
                watch.State = WatchState.Abandoned;
                watches.Remove(watch);
                PersistLocked();
            }

            Console.WriteLine($"[watch] giving up on '{watch.Subject}'");
            await announce(
                $"I've been checking on {watch.Describe()} for a while now and it still hasn't happened, " +
                "so I'll stop watching it.").ConfigureAwait(false);
        }

        private void Reschedule(EventWatch watch)
        {
            lock (gate)
            {
                watch.NextCheckAt = DateTime.Now.Add(watch.Backoff());
                PersistLocked();
            }
        }
    }

    // Where event watches live between runs. Same discipline as TriggerStore and
    // ReminderStore: AppData, per-write temp plus File.Replace, one save lock,
    // every failure non-fatal and one bad entry costing one watch.
    public static class EventWatchStore
    {
        public static string Path
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("LAITH_WATCHES_PATH");
                if (!string.IsNullOrWhiteSpace(over)) return over.Trim();

                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LAITH",
                    "watches.json");
            }
        }

        private static readonly object saveGate = new object();

        public static List<EventWatch> Load()
        {
            var loaded = new List<EventWatch>();
            try
            {
                if (!File.Exists(Path)) return loaded;
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path)))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) return loaded;
                    foreach (JsonElement el in doc.RootElement.EnumerateArray())
                    {
                        EventWatch w = ReadOne(el);
                        if (w != null) loaded.Add(w);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[watch] could not read {Path}: {ex.Message}");
            }
            return loaded;
        }

        public static void Save(IEnumerable<EventWatch> items)
        {
            List<EventWatch> list = items?.ToList() ?? new List<EventWatch>();

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
                        foreach (EventWatch w in list) WriteOne(writer, w);
                        writer.WriteEndArray();
                    }

                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                    temp = null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[watch] could not save to {Path}: {ex.Message}");
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

        private static void WriteOne(Utf8JsonWriter w, EventWatch x)
        {
            w.WriteStartObject();
            w.WriteNumber("id", x.Id);
            w.WriteString("subject", x.Subject ?? string.Empty);
            if (x.Label != null) w.WriteString("label", x.Label);
            w.WriteString("deadline", x.Deadline.ToString("o", CultureInfo.InvariantCulture));
            w.WriteString("next_check_at", x.NextCheckAt.ToString("o", CultureInfo.InvariantCulture));
            if (x.LastCheckedAt.HasValue)
                w.WriteString("last_checked_at", x.LastCheckedAt.Value.ToString("o", CultureInfo.InvariantCulture));
            w.WriteNumber("checks", x.Checks);
            w.WriteString("state", x.State.ToString());
            if (x.Url != null) w.WriteString("url", x.Url);
            w.WriteString("created_at", x.CreatedAt.ToString("o", CultureInfo.InvariantCulture));
            w.WriteEndObject();
        }

        private static EventWatch ReadOne(JsonElement el)
        {
            try
            {
                if (el.ValueKind != JsonValueKind.Object) return null;

                string subject = GetString(el, "subject");
                // Nothing to search for is nothing to wait on.
                if (string.IsNullOrWhiteSpace(subject)) return null;

                DateTime? deadline = GetDate(el, "deadline");
                if (!deadline.HasValue) return null;

                return new EventWatch
                {
                    Id = GetInt(el, "id", 0),
                    Subject = subject,
                    Label = GetString(el, "label"),
                    Deadline = deadline.Value,
                    NextCheckAt = GetDate(el, "next_check_at") ?? deadline.Value,
                    LastCheckedAt = GetDate(el, "last_checked_at"),
                    Checks = GetInt(el, "checks", 0),
                    State = GetEnum(el, "state", WatchState.Waiting),
                    Url = GetString(el, "url"),
                    CreatedAt = GetDate(el, "created_at") ?? DateTime.Now
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[watch] skipping unreadable entry: {ex.Message}");
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

    // What a grounded check found out about a real-world event.
    public sealed class EventVerdict
    {
        public enum Outcome
        {
            Happened, // the search says it has
            NotYet,   // the search says it hasn't
            Unknown   // the search didn't settle it, or the lookup failed
        }

        public Outcome Result { get; private set; }

        // One speakable sentence, or the failure reason when Result is Unknown.
        public string Detail { get; private set; }

        // Where to go to see it, or null. Only ever populated on Happened —
        // offering to open a link for something that has not been released is
        // offering to open a 404.
        public string Url { get; private set; }

        public static EventVerdict Unknown(string reason) =>
            new EventVerdict { Result = Outcome.Unknown, Detail = reason };

        /// <summary>
        /// Reads the one-line "STATUS | sentence | url" reply. Lenient about
        /// everything except the status word: a model that wraps the line in
        /// quotes, adds a full stop, or omits the url is still understood, but
        /// one whose status cannot be read is Unknown rather than assumed — the
        /// whole point of this type is that "it happened" is never a default.
        /// </summary>
        public static EventVerdict Parse(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply))
            {
                return Unknown("the check came back empty");
            }

            // Models sometimes prepend a courtesy line. The one that matters is
            // the first containing a status word.
            string line = reply
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(l => l.IndexOf("HAPPENED", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     l.IndexOf("NOT_YET", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     l.IndexOf("UNKNOWN", StringComparison.OrdinalIgnoreCase) >= 0);

            if (line == null) return Unknown("the check didn't answer in the expected form");

            string[] parts = line.Split('|');
            string status = parts[0].Trim().Trim('"', '*', '`', '.').ToUpperInvariant();
            string detail = parts.Length > 1 ? parts[1].Trim().Trim('"', '*', '`') : null;
            string url = parts.Length > 2 ? parts[2].Trim().Trim('"', '*', '`', '.', ',') : null;

            if (string.IsNullOrWhiteSpace(url) ||
                url.Equals("NONE", StringComparison.OrdinalIgnoreCase) ||
                !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                  url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                // Anything that is not plainly an http(s) URL is discarded rather
                // than repaired. This value ends up in Process.Start, and guessing
                // at a half-formed one is how a search result becomes a command.
                url = null;
            }

            // NOT_YET is checked before HAPPENED: the string "NOT_YET" contains
            // neither as a substring of the other, but a sentence like "has not
            // happened yet" trips a naive Contains("HAPPENED") on the whole line.
            if (status.Contains("NOT_YET") || status.Contains("NOT YET"))
            {
                return new EventVerdict { Result = Outcome.NotYet, Detail = detail };
            }
            if (status.Contains("UNKNOWN"))
            {
                return Unknown(detail ?? "the search didn't settle it");
            }
            if (status.Contains("HAPPENED"))
            {
                return new EventVerdict { Result = Outcome.Happened, Detail = detail, Url = url };
            }

            return Unknown("the check didn't answer in the expected form");
        }
    }
}
