using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Personal_Assistant.CallScreening
{
    /// <summary>One screened call, as it will be read back later.</summary>
    public sealed class CallRecord
    {
        public DateTime At { get; set; }
        public string Caller { get; set; }
        public double Seconds { get; set; }
        public CallEnding Ending { get; set; }

        /// <summary>What take_message wrote down, or null.</summary>
        public string Message { get; set; }

        /// <summary>Every line either side said, in order.</summary>
        public IReadOnlyList<string> Transcript { get; set; } = new List<string>();

        // HAS LAYTH ACTUALLY BEEN TOLD? Not "was a message taken" — that is
        // Message above, and a message sitting in a file nobody opens is the hole
        // this flag exists to close.
        //
        // Two channels race to deliver each message: a text sent the moment the
        // call ends, and a spoken line when he next sits down. Whichever gets
        // there first sets this, and the other then stays quiet. WITHOUT IT the
        // two duplicate each other and he is read a message he already read on
        // his phone an hour ago — which is how a useful feature becomes one you
        // switch off.
        //
        // Persisted, because the spoken channel has to know across a restart.
        public bool Delivered { get; set; }

        /// <summary>Which channel got there first — "text" or "spoken". Null until then.</summary>
        public string DeliveredHow { get; set; }

        public DateTime? DeliveredAt { get; set; }

        /// <summary>
        /// The identity of a record, for marking one delivered later.
        /// </summary>
        /// <remarks>
        /// The start time, because it is the only field already written to disk
        /// that is unique in practice: the service refuses to screen two calls at
        /// once (see the `handling` interlock), so no two records can share a
        /// start instant. Round-tripped as "o" and compared as a string so a
        /// parse that loses sub-second precision cannot make two calls look like
        /// one.
        /// </remarks>
        public string Key => At.ToString("o", CultureInfo.InvariantCulture);

        /// <summary>True when there is something worth telling him that he has not been told.</summary>
        public bool HasUndeliveredMessage =>
            !Delivered && !string.IsNullOrWhiteSpace(Deliverable);

        /// <summary>
        /// What actually gets passed on: the message the model wrote down, or
        /// failing that the caller's own words.
        /// </summary>
        /// <remarks>
        /// THE FALLBACK EXISTS BECAUSE THE MODEL DOES NOT ALWAYS CALL THE TOOL.
        /// Measured on a real call, 2026-08-23: the caller said the PC parts
        /// would cost around $800, the assistant replied "I've noted the PC parts
        /// will be around $800" — the exact phrasing CallPersona forbids unless
        /// take_message has already been called — and take_message was never
        /// called. The call log recorded no message and delivery correctly
        /// reported there was nothing to pass on. Correct, and useless: the whole
        /// point of screening is that the message reaches him.
        ///
        /// The persona is where that gets FIXED; this is where it stops being
        /// costly in the meantime. A message the assistant fumbled is still
        /// sitting in the transcript, and sending the caller's own words is
        /// strictly better than sending nothing. It is also the more honest
        /// artefact — they are the words the caller actually said.
        ///
        /// Only for calls that WRAPPED. A cancelled or failed call is one where
        /// nobody agreed the conversation was over, and a hostile or automated
        /// call is one the persona is told to take no message from — neither
        /// should be relayed just because somebody made noise down the line.
        /// </remarks>
        public string Deliverable =>
            !string.IsNullOrWhiteSpace(Message) ? Message.Trim() : Salvaged();

        /// <summary>True when there was no taken message and we fell back to the transcript.</summary>
        public bool IsSalvaged =>
            string.IsNullOrWhiteSpace(Message) && Salvaged() != null;

        private string Salvaged()
        {
            if (Ending != CallEnding.Wrapped || Transcript == null) return null;

            // The store writes each line already tagged with who said it.
            var theirs = Transcript
                .Where(l => l != null && l.StartsWith("them: ", StringComparison.Ordinal))
                .Select(l => l.Substring("them: ".Length).Trim())
                .Where(l => l.Length > 0)
                .ToList();

            if (theirs.Count == 0) return null;

            string joined = string.Join(" ", theirs).Trim();

            // A caller who only managed "hello?" before hanging up has not left a
            // message, and texting that is worse than silence.
            return joined.Length >= 15 ? joined : null;
        }

        public static CallRecord From(CallOutcome outcome) => new CallRecord
        {
            At = outcome.StartedAt,
            Caller = outcome.Caller,
            Seconds = Math.Round(outcome.Duration.TotalSeconds, 1),
            Ending = outcome.Ending,
            Message = outcome.Message,
            Transcript = outcome.Transcript
                .Select(l => (l.Speaker == CallSpeaker.Caller ? "them: " : "laith: ") + l.Text)
                .ToList(),
        };

        /// <summary>One line, for reading a list of calls back out loud.</summary>
        public string Describe() =>
            $"{At:ddd h:mm tt} — {Caller}" +
            (string.IsNullOrWhiteSpace(Message) ? " (no message)" : $": {Message}");

        // How long a caller's words are allowed to be when they leave the
        // machine. A message is a note, not a transcript — and a text that runs
        // to several segments costs more and reads worse than one that says the
        // gist and lets him ring back.
        private const int MessageCap = 300;

        /// <summary>
        /// The text message, as it will arrive on his phone.
        /// </summary>
        /// <remarks>
        /// ATTRIBUTED AND SHORT, deliberately. The body is a STRANGER'S WORDS,
        /// relayed verbatim, and a bare line of text arriving out of nowhere
        /// reads like it came from the assistant itself. Naming the caller first
        /// makes the provenance unmissable, which matters most when the message
        /// is something like "tell him to send the money today".
        ///
        /// The whole transcript is never sent. It is long, it is duplicated in
        /// the call log he can ask for, and every line of it is somebody else's
        /// speech going out over the network.
        /// </remarks>
        public string TextMessage()
        {
            string who = string.IsNullOrWhiteSpace(Caller) ? "Someone" : Caller.Trim();
            string what = Clip(Deliverable, MessageCap);

            if (string.IsNullOrWhiteSpace(what))
                return $"{who} called at {At:h:mm tt} and did not leave a message.";

            // Flagged when it came from the transcript rather than from a message
            // the assistant took. He should be able to tell the difference between
            // "here is the message" and "here is what I could make out", because
            // the second is worth ringing back over.
            return IsSalvaged
                ? $"{who} called at {At:h:mm tt}. No clear message was taken — they said: {what}"
                : $"{who} called at {At:h:mm tt}: {what}";
        }

        /// <summary>
        /// The same thing said out loud, phrased to sit inside ResumeSummary's
        /// one sentence ("While I was off, ...").
        /// </summary>
        public string SpokenMessage()
        {
            string who = string.IsNullOrWhiteSpace(Caller) ? "someone" : Caller.Trim();
            // Shorter out loud than in a text. A spoken paragraph is not something
            // you can skim back over, and he can always ask for the call log.
            return IsSalvaged
                ? $"{who} called — I didn't get a clear message, but they said: {Clip(Deliverable, 200)}"
                : $"{who} called and left a message: {Clip(Deliverable, 200)}";
        }

        private static string Clip(string s, int cap)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            return s.Length <= cap ? s : s.Substring(0, cap).TrimEnd() + "...";
        }
    }

    // Where screened calls live between runs. Same discipline as EventWatchStore
    // (EventWatch.cs:497) and CallScreeningStore: AppData, a per-write temp plus
    // File.Replace, one save lock, every failure non-fatal and one bad entry
    // costing one record rather than the file.
    //
    // A separate file from callscreening.json on purpose. That one holds a single
    // question — is the assistant allowed to pick up the phone right now — and it
    // is read at startup before anything else; burying it under a growing history
    // of calls would put the arm state behind whatever went wrong in a transcript.
    //
    // THESE FILES CONTAIN WHAT STRANGERS SAID DOWN THE PHONE, in plain text, along
    // with their names. That is the whole point of taking a message, but it is
    // worth knowing before this path is ever copied anywhere.
    public static class CallLogStore
    {
        // Enough to answer "who called while I was out?" for a few days, and
        // bounded so a transcript file cannot grow without limit on a machine
        // nobody is watching.
        private const int Keep = 50;

        public static string Path
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("LAITH_CALLLOG_PATH");
                if (!string.IsNullOrWhiteSpace(over)) return over.Trim();

                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LAITH",
                    "calls.json");
            }
        }

        private static readonly object saveGate = new object();

        public static List<CallRecord> Load()
        {
            var loaded = new List<CallRecord>();
            try
            {
                if (!File.Exists(Path)) return loaded;
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path)))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) return loaded;
                    foreach (JsonElement el in doc.RootElement.EnumerateArray())
                    {
                        CallRecord record = ReadOne(el);
                        if (record != null) loaded.Add(record);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[call] could not read {Path}: {ex.Message}");
            }
            return loaded;
        }

        /// <summary>Adds one call to the log, newest last.</summary>
        public static void Append(CallRecord record)
        {
            if (record == null) return;

            lock (saveGate)
            {
                List<CallRecord> all = Load();
                all.Add(record);
                if (all.Count > Keep) all.RemoveRange(0, all.Count - Keep);
                Save(all);
            }
        }

        /// <summary>
        /// Records that somebody has now been told about this call, so the other
        /// channel does not tell him again. Returns false when the record was
        /// already marked, or is no longer in the log.
        /// </summary>
        /// <remarks>
        /// READ-MODIFY-WRITE UNDER THE SAME LOCK Append takes, and that is the
        /// point of doing it here rather than in the caller. The two delivery
        /// channels genuinely can run at once — a text going out as the call ends
        /// while the spoken catch-up fires because he just sat down — and the
        /// obvious version of this (load, set, save, from each side) loses one of
        /// them and speaks the message anyway.
        ///
        /// The "already marked" answer is a RESULT, not a failure: it is exactly
        /// how the loser of that race learns to stay quiet.
        /// </remarks>
        public static bool MarkDelivered(string key, string how)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;

            lock (saveGate)
            {
                List<CallRecord> all = Load();
                CallRecord match = all.FirstOrDefault(r => r.Key == key);

                if (match == null || match.Delivered) return false;

                match.Delivered = true;
                match.DeliveredHow = how;
                match.DeliveredAt = DateTime.Now;
                Save(all);
                return true;
            }
        }

        /// <summary>
        /// Calls that took a message he has not been told about yet, oldest first.
        /// </summary>
        /// <param name="within">
        /// How far back to look. Anything older is left alone — see the remarks.
        /// </param>
        /// <remarks>
        /// THE WINDOW IS NOT AN OPTIMISATION. A message is worth interrupting
        /// someone for while it is still actionable and worth nothing once it is
        /// not: "the car is ready, collect before 6" said at ten the next morning
        /// is noise, and being told six of them at once is how the feature earns
        /// itself a "stop doing that". Anything past the window stays in the log
        /// for `list_calls` to read back on request, which is the right place for
        /// history.
        ///
        /// It is also what makes the first run after an upgrade safe. Every
        /// record written before this flag existed reads back as undelivered, and
        /// without a window the assistant would open with a week of them.
        /// </remarks>
        public static List<CallRecord> Undelivered(TimeSpan within)
        {
            DateTime cutoff = DateTime.Now - within;

            return Load()
                .Where(r => r.HasUndeliveredMessage && r.At >= cutoff)
                .OrderBy(r => r.At)
                .ToList();
        }

        public static void Save(IEnumerable<CallRecord> items)
        {
            List<CallRecord> list = items?.ToList() ?? new List<CallRecord>();

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
                        foreach (CallRecord record in list) WriteOne(writer, record);
                        writer.WriteEndArray();
                    }

                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                    temp = null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[call] could not save to {Path}: {ex.Message}");
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

        private static void WriteOne(Utf8JsonWriter w, CallRecord x)
        {
            w.WriteStartObject();
            w.WriteString("at", x.At.ToString("o", CultureInfo.InvariantCulture));
            w.WriteString("caller", x.Caller ?? string.Empty);
            w.WriteNumber("seconds", x.Seconds);
            w.WriteString("ending", x.Ending.ToString());
            if (x.Message != null) w.WriteString("message", x.Message);

            // Only written once true, so an old log file reads back as
            // "not delivered" rather than as unreadable.
            if (x.Delivered)
            {
                w.WriteBoolean("delivered", true);
                if (x.DeliveredHow != null) w.WriteString("deliveredHow", x.DeliveredHow);
                if (x.DeliveredAt.HasValue)
                    w.WriteString("deliveredAt",
                        x.DeliveredAt.Value.ToString("o", CultureInfo.InvariantCulture));
            }

            w.WriteStartArray("transcript");
            foreach (string line in x.Transcript ?? new List<string>()) w.WriteStringValue(line);
            w.WriteEndArray();

            w.WriteEndObject();
        }

        private static CallRecord ReadOne(JsonElement el)
        {
            try
            {
                if (el.ValueKind != JsonValueKind.Object) return null;

                DateTime? at = GetDate(el, "at");
                if (!at.HasValue) return null;   // a call with no time is not a record of anything

                var transcript = new List<string>();
                if (el.TryGetProperty("transcript", out JsonElement lines) &&
                    lines.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement line in lines.EnumerateArray())
                    {
                        if (line.ValueKind == JsonValueKind.String) transcript.Add(line.GetString());
                    }
                }

                return new CallRecord
                {
                    At = at.Value,
                    Caller = GetString(el, "caller") ?? "an unknown number",
                    Seconds = el.TryGetProperty("seconds", out JsonElement s) &&
                              s.ValueKind == JsonValueKind.Number && s.TryGetDouble(out double parsed)
                              ? parsed : 0,
                    Ending = GetEnum(el, "ending", CallEnding.Wrapped),
                    Message = GetString(el, "message"),
                    Transcript = transcript,

                    // Absent means false, which is what every record written
                    // before delivery existed should mean. That leaves a whole
                    // existing log reading as undelivered on the first run after
                    // an upgrade; the staleness window in Undelivered is what
                    // stops that becoming a monologue about last week.
                    Delivered = el.TryGetProperty("delivered", out JsonElement d) &&
                                d.ValueKind == JsonValueKind.True,
                    DeliveredHow = GetString(el, "deliveredHow"),
                    DeliveredAt = GetDate(el, "deliveredAt"),
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[call] skipping an unreadable call record: {ex.Message}");
                return null;
            }
        }

        private static string GetString(JsonElement el, string name) =>
            el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

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
