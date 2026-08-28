using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Personal_Assistant.Triggers
{
    // What makes a standing rule fire. A closed vocabulary on purpose: the model
    // picks one of these, and everything after that is evaluated locally. An
    // open-ended "condition" string would mean either asking the model on every
    // tick (86,400 times a day) or writing an interpreter for natural language,
    // and neither is a thing this should be.
    //
    // Adding a kind is the intended way to grow: a new enum value, a case in
    // VoiceTriggers.Bind, and a line in the set_trigger schema.
    public enum TriggerWhen
    {
        AtTime,       // a clock time, once or repeating
        Every,        // an interval, optionally bounded by a time of day
        AppStarts,    // a named process appears
        AppStops,     // a named process goes away
        FileAppears,  // a finished file lands in a folder
        IdleFor,      // the user has been away this long
        OnReturn,     // ...and has just come back
        BatteryBelow  // running on battery, at or under this percentage
    }

    public enum TriggerRepeat
    {
        Once,
        Daily,
        Weekdays,
        Weekends
    }

    // One standing rule, as created by voice and as written to disk.
    //
    // Deliberately a plain data record with no behaviour: it is the thing that
    // survives a restart, so it must be describable entirely by values. Anything
    // needing a delegate (the predicate, the action) is rebuilt from these fields
    // by VoiceTriggers.Bind on load.
    public sealed class TriggerSpec
    {
        public int Id { get; set; }
        public TriggerWhen When { get; set; }
        public TriggerRepeat Repeat { get; set; }

        public TimeSpan TimeOfDay { get; set; }      // AtTime
        public int IntervalMinutes { get; set; }     // Every
        public TimeSpan? Until { get; set; }         // Every — stop after this time of day
        public string App { get; set; }              // AppStarts / AppStops
        public string Folder { get; set; }           // FileAppears — null = Downloads
        public string Pattern { get; set; }          // FileAppears — null = anything
        public int Percent { get; set; }             // BatteryBelow
        public int MinutesLeft { get; set; }         // BatteryBelow — time, not charge

        // IdleFor / OnReturn reuse IntervalMinutes as "how many minutes away",
        // rather than adding a fourth number to a schema the model already has to
        // read conditionally. Nothing uses both meanings at once.
        public int AwayMinutes
        {
            get { return IntervalMinutes; }
            set { IntervalMinutes = value; }
        }

        // The exact moment a ONE-SHOT is due. Repeating rules don't have one:
        // "every day at 08:00" is a time of day, and which 08:00 it means is
        // decided afresh each day.
        //
        // A one-shot needs the absolute moment because a time of day cannot say
        // which day it meant. "Remind me at 00:10", said at 23:50, is tomorrow —
        // but stored as 00:10 it reads as this morning, i.e. already past, and
        // was dropped as spent by every restart before it fired.
        public DateTime? FireAt { get; set; }

        public string Message { get; set; }          // what to say, or null
        public string RunTool { get; set; }          // a tool to run, or null
        public Dictionary<string, string> RunToolArgs { get; set; }

        public DateTime CreatedAt { get; set; }

        // The engine key. Prefixed so the prayer planner's RemoveWithPrefix and
        // this one can never collide.
        public string TriggerName => NamePrefix + Id;
        public const string NamePrefix = "voice:";

        // How this reads back to the user, and to the model in list_triggers.
        // One sentence, no ids — "the second one" is how people refer to these.
        public string Describe()
        {
            string what =
                !string.IsNullOrWhiteSpace(Message) ? $"say \"{Message}\"" :
                !string.IsNullOrWhiteSpace(RunTool) ? $"run {RunTool}" :
                // Only battery rules are allowed to carry neither; they announce
                // the live reading at fire time.
                "tell you";

            switch (When)
            {
                case TriggerWhen.AtTime:
                    string at = DateTime.Today.Add(TimeOfDay).ToString("h:mm tt");
                    switch (Repeat)
                    {
                        case TriggerRepeat.Daily: return $"{what} every day at {at}";
                        case TriggerRepeat.Weekdays: return $"{what} on weekdays at {at}";
                        case TriggerRepeat.Weekends: return $"{what} at weekends at {at}";
                        default:
                            // A one-shot says which day, because the one it means
                            // is exactly what a bare time cannot convey — "at
                            // 00:10" said near midnight has to read back as
                            // tomorrow or the user cannot tell it was understood.
                            DateTime due = DueAt();
                            if (due.Date == DateTime.Today) return $"{what} at {at}";
                            if (due.Date == DateTime.Today.AddDays(1)) return $"{what} tomorrow at {at}";
                            return $"{what} on {due:ddd d MMM} at {at}";
                    }
                case TriggerWhen.Every:
                    string bound = Until.HasValue
                        ? $" until {DateTime.Today.Add(Until.Value):h:mm tt}"
                        : "";
                    return $"{what} every {DescribeInterval(IntervalMinutes)}{bound}";
                case TriggerWhen.AppStarts:
                    return $"{what} when {App} starts";
                case TriggerWhen.AppStops:
                    return $"{what} when {App} closes";
                case TriggerWhen.FileAppears:
                    string where = string.IsNullOrWhiteSpace(Folder) ? "Downloads" : Folder;
                    string which = string.IsNullOrWhiteSpace(Pattern) || Pattern == "*"
                        ? "a file"
                        : Pattern;
                    return $"{what} when {which} finishes downloading to {where}";
                case TriggerWhen.IdleFor:
                    return $"{what} once I've been away {DescribeInterval(AwayMinutes)}";
                case TriggerWhen.OnReturn:
                    return $"{what} when I get back after {DescribeInterval(AwayMinutes)} away";
                case TriggerWhen.BatteryBelow:
                    if (MinutesLeft > 0 && Percent > 0)
                        return $"{what} when the battery drops below {Percent}% or {DescribeInterval(MinutesLeft)} left";
                    if (MinutesLeft > 0)
                        return $"{what} when there's less than {DescribeInterval(MinutesLeft)} of battery left";
                    return $"{what} when the battery drops below {Percent}%";
                default:
                    return what;
            }
        }

        private static string DescribeInterval(int minutes)
        {
            if (minutes % 60 == 0)
            {
                int hours = minutes / 60;
                return hours == 1 ? "hour" : $"{hours} hours";
            }
            return minutes == 1 ? "minute" : $"{minutes} minutes";
        }

        /// <summary>
        /// The moment a one-shot is due, resolving one stored without an explicit
        /// FireAt (a rule written by an older build, or hand-edited) to the next
        /// occurrence of its time of day.
        /// </summary>
        public DateTime DueAt()
        {
            if (FireAt.HasValue) return FireAt.Value;
            DateTime at = DateTime.Today.Add(TimeOfDay);
            if (at <= DateTime.Now) at = at.AddDays(1);
            return at;
        }

        // A one-shot whose moment has been and gone is spent, and must not come
        // back at the same time tomorrow after a restart. Repeating rules never
        // expire — that is what makes them standing.
        public bool IsExpired(DateTime now) =>
            When == TriggerWhen.AtTime &&
            Repeat == TriggerRepeat.Once &&
            FireAt.HasValue &&
            FireAt.Value < now;
    }

    // Where standing rules live between runs.
    //
    // In AppData rather than next to the exe: the app runs from both bin\Debug
    // and C:\Users\layth\LAITH\main, and rules the user set by voice are their
    // data, not a build output. A rebuild must not wipe them and a second install
    // must see the same ones.
    //
    // Every failure here is non-fatal. A corrupt or unreadable store costs the
    // user their standing rules, which is bad; taking the assistant down at
    // startup because a JSON file has a stray brace in it is worse.
    public static class TriggerStore
    {
        // LAITH_TRIGGERS_PATH relocates the store. Mostly so tests can run against
        // a scratch file instead of the real one — a harness that exercised the
        // save path would otherwise rewrite the user's actual standing rules — but
        // it also makes a portable install possible.
        public static string Path
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("LAITH_TRIGGERS_PATH");
                if (!string.IsNullOrWhiteSpace(over)) return over.Trim();

                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LAITH",
                    "triggers.json");
            }
        }

        public static List<TriggerSpec> Load()
        {
            var specs = new List<TriggerSpec>();
            try
            {
                if (!File.Exists(Path)) return specs;
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path)))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) return specs;
                    foreach (JsonElement el in doc.RootElement.EnumerateArray())
                    {
                        TriggerSpec spec = ReadOne(el);
                        if (spec != null) specs.Add(spec);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[triggers] could not read {Path}: {ex.Message}");
            }
            return specs;
        }

        // Saves are serialised across the process.
        //
        // Rules retire themselves as they fire, and the trigger ticker dispatches
        // every due action on its own thread — so "two rules at 8:00" means two
        // concurrent saves. Unsynchronised, they collide on the temp file: one
        // truncates what the other is mid-write, or File.Move races File.Delete,
        // and the failure lands on the file the user's standing rules live in.
        //
        // A plain lock is right here: saves are small, infrequent, and already
        // off the hot path. The temp name is per-write as well, so even a second
        // process (a harness, a second instance) cannot clobber a write in flight.
        private static readonly object saveGate = new object();

        public static void Save(IEnumerable<TriggerSpec> specs)
        {
            // Materialise BEFORE taking the lock: the caller's snapshot is already
            // a copy, but enumerating a live collection under the lock would hold
            // it for as long as the caller's enumerator takes.
            List<TriggerSpec> items = specs?.ToList() ?? new List<TriggerSpec>();

            lock (saveGate)
            {
                string temp = null;
                try
                {
                    string path = Path;
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));

                    // Write beside the target and move into place, so an
                    // interrupted write leaves the previous rules intact rather
                    // than a truncated file that Load then refuses.
                    temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                    {
                        writer.WriteStartArray();
                        foreach (TriggerSpec s in items) WriteOne(writer, s);
                        writer.WriteEndArray();
                    }

                    // Replace in one step where possible. File.Delete followed by
                    // File.Move leaves a window with no store at all, which is
                    // exactly when a reader sees zero rules.
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                    temp = null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[triggers] could not save to {Path}: {ex.Message}");
                }
                finally
                {
                    // Never leave a stray temp behind for a failed write.
                    if (temp != null)
                    {
                        try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    }
                }
            }
        }

        private static void WriteOne(Utf8JsonWriter w, TriggerSpec s)
        {
            w.WriteStartObject();
            w.WriteNumber("id", s.Id);
            w.WriteString("when", s.When.ToString());
            w.WriteString("repeat", s.Repeat.ToString());
            w.WriteString("time_of_day", s.TimeOfDay.ToString(@"hh\:mm"));
            w.WriteNumber("interval_minutes", s.IntervalMinutes);
            if (s.Until.HasValue) w.WriteString("until", s.Until.Value.ToString(@"hh\:mm"));
            // Round-trip precision, unlike time_of_day's hh:mm — a one-shot's
            // moment has to survive exactly, including which day it falls on.
            if (s.FireAt.HasValue)
            {
                w.WriteString("fire_at", s.FireAt.Value.ToString("o", CultureInfo.InvariantCulture));
            }
            if (s.App != null) w.WriteString("app", s.App);
            if (s.Folder != null) w.WriteString("folder", s.Folder);
            if (s.Pattern != null) w.WriteString("pattern", s.Pattern);
            if (s.Percent != 0) w.WriteNumber("percent", s.Percent);
            if (s.MinutesLeft != 0) w.WriteNumber("minutes_left", s.MinutesLeft);
            if (s.Message != null) w.WriteString("message", s.Message);
            if (s.RunTool != null) w.WriteString("run_tool", s.RunTool);
            if (s.RunToolArgs != null && s.RunToolArgs.Count > 0)
            {
                w.WriteStartObject("run_tool_args");
                foreach (KeyValuePair<string, string> kv in s.RunToolArgs)
                {
                    w.WriteString(kv.Key, kv.Value ?? string.Empty);
                }
                w.WriteEndObject();
            }
            w.WriteString("created_at", s.CreatedAt.ToString("o", CultureInfo.InvariantCulture));
            w.WriteEndObject();
        }

        private static TriggerSpec ReadOne(JsonElement el)
        {
            try
            {
                if (el.ValueKind != JsonValueKind.Object) return null;

                var spec = new TriggerSpec
                {
                    Id = GetInt(el, "id", 0),
                    When = GetEnum(el, "when", TriggerWhen.AtTime),
                    Repeat = GetEnum(el, "repeat", TriggerRepeat.Once),
                    TimeOfDay = GetTime(el, "time_of_day") ?? TimeSpan.Zero,
                    IntervalMinutes = GetInt(el, "interval_minutes", 0),
                    Until = GetTime(el, "until"),
                    FireAt = GetOptionalDate(el, "fire_at"),
                    App = GetString(el, "app"),
                    Folder = GetString(el, "folder"),
                    Pattern = GetString(el, "pattern"),
                    Percent = GetInt(el, "percent", 0),
                    MinutesLeft = GetInt(el, "minutes_left", 0),
                    Message = GetString(el, "message"),
                    RunTool = GetString(el, "run_tool"),
                    CreatedAt = GetDate(el, "created_at")
                };

                if (el.TryGetProperty("run_tool_args", out JsonElement argsEl) &&
                    argsEl.ValueKind == JsonValueKind.Object)
                {
                    spec.RunToolArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (JsonProperty p in argsEl.EnumerateObject())
                    {
                        spec.RunToolArgs[p.Name] = p.Value.ValueKind == JsonValueKind.String
                            ? p.Value.GetString()
                            : p.Value.GetRawText();
                    }
                }

                // One bad entry loses one rule, not the file.
                return spec;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[triggers] skipping unreadable entry: {ex.Message}");
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

        private static TimeSpan? GetTime(JsonElement el, string name)
        {
            string raw = GetString(el, name);
            if (raw == null) return null;
            return TimeSpan.TryParseExact(raw, new[] { @"hh\:mm", @"h\:mm" },
                CultureInfo.InvariantCulture, out TimeSpan parsed)
                ? parsed
                : (TimeSpan?)null;
        }

        private static DateTime? GetOptionalDate(JsonElement el, string name)
        {
            string raw = GetString(el, name);
            if (raw == null) return null;
            return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTime parsed)
                ? parsed
                : (DateTime?)null;
        }

        private static DateTime GetDate(JsonElement el, string name)
        {
            string raw = GetString(el, name);
            return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTime parsed)
                ? parsed
                : DateTime.Now;
        }
    }
}
