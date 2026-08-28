using Personal_Assistant.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Personal_Assistant.Dispatch
{
    // One entry of conversation history. Role is "user" or "model" for spoken
    // turns; "tool" marks a tool the assistant executed, carried STRUCTURALLY
    // (name + args) rather than as text. Providers render tool entries into the
    // model's native function-calling format (OpenAI tool_calls + tool result /
    // Gemini functionCall + functionResponse). This matters because any *text*
    // stand-in for a tool call gets imitated by small models — as a fake tool
    // call if it looks tool-shaped, or as a no-op reply if it looks like a plain
    // acknowledgment. The native format is what the model is trained on, so it
    // pattern-matches prior calls correctly for follow-ups ("turn it back off").
    public sealed class ConversationTurn
    {
        public string Role { get; }
        public string Text { get; }
        public string ToolName { get; }
        public IReadOnlyDictionary<string, string> ToolArgs { get; }

        // When this was said. Only used to decide what is still worth restoring
        // after a restart — nothing renders it into a prompt, because a timestamp
        // in the assistant role is exactly the kind of imitable text the comment
        // above warns about.
        public DateTime RecordedAt { get; }

        public ConversationTurn(string role, string text, DateTime? recordedAt = null)
        {
            Role = role;
            Text = text;
            RecordedAt = recordedAt ?? DateTime.Now;
        }

        public ConversationTurn(
            string toolName,
            IReadOnlyDictionary<string, string> toolArgs,
            DateTime? recordedAt = null)
        {
            Role = "tool";
            ToolName = toolName;
            ToolArgs = toolArgs ?? new Dictionary<string, string>();
            RecordedAt = recordedAt ?? DateTime.Now;
        }

        public bool IsTool => Role == "tool";
    }

    // Rolling conversation history, fed into the tool-detection and
    // conversational calls so follow-ups like "what about tomorrow?" or "turn it
    // back off" resolve against prior context.
    //
    // Persisted since 2026-08-07, so closing the app mid-thought no longer loses
    // it. Two things make that safe rather than just possible:
    //
    //   * Tool entries are written STRUCTURALLY, the same reason they are held
    //     that way in memory. A tool call flattened to text on the way to disk
    //     would come back as text and get imitated.
    //   * Restoring is bounded by AGE, not just by count. A buffer from three
    //     days ago handed to the model as "the conversation so far" is worse than
    //     no history at all — "turn it back off" would resolve against a light
    //     someone switched on last Tuesday.
    public sealed class ConversationMemory
    {
        // Capped in entries (user/model/tool), not tokens — keeps the request
        // small and recent-context-only rather than growing unbounded.
        private const int MaxTurns = 16;

        private readonly LinkedList<ConversationTurn> turns = new LinkedList<ConversationTurn>();
        private readonly object gate = new object();
        private readonly bool persist;

        /// <param name="persist">
        /// False for the many places that build a memory just to satisfy a
        /// constructor — a harness, a smoke test — which must not write over the
        /// real conversation.
        /// </param>
        public ConversationMemory(bool persist = false)
        {
            this.persist = persist;
        }

        // How far back a restored conversation is still "the conversation". Two
        // hours by default: long enough to survive a restart or a lunch break,
        // short enough that this morning is not treated as this moment. 0 turns
        // persistence off entirely.
        public static TimeSpan MaxRestoreAge =>
            TimeSpan.FromHours(LaithConfig.Double("ConversationMemoryHours", 2, 0, 72));

        public static string Path
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("LAITH_CONVERSATION_PATH");
                if (!string.IsNullOrWhiteSpace(over)) return over.Trim();

                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LAITH",
                    "conversation.json");
            }
        }

        public void AddUser(string text) => AddText("user", text);

        public void AddModel(string text) => AddText("model", text);

        // Records an executed tool as a structured prior call. Rendered by each
        // provider as a native function call + result, which both preserves
        // strict user/assistant alternation and gives the model a real example to
        // follow — without any imitable text in the assistant role.
        public void AddToolCall(string toolName, IReadOnlyDictionary<string, string> args)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return;
            lock (gate)
            {
                // A single entry renders to BOTH the call and its result, so
                // trimming can never orphan a tool result from its call.
                turns.AddLast(new ConversationTurn(toolName, args));
                TrimLocked();
                SaveLocked();
            }
        }

        private void AddText(string role, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            lock (gate)
            {
                turns.AddLast(new ConversationTurn(role, text));
                TrimLocked();
                SaveLocked();
            }
        }

        private void TrimLocked()
        {
            while (turns.Count > MaxTurns) turns.RemoveFirst();
        }

        // Snapshot taken BEFORE the current turn's user input is recorded, so
        // callers can pass "everything before this" as history and append the
        // current input as the final entry themselves.
        public IReadOnlyList<ConversationTurn> Snapshot()
        {
            lock (gate) { return new List<ConversationTurn>(turns); }
        }

        public void Clear()
        {
            lock (gate)
            {
                turns.Clear();
                SaveLocked();
            }
        }

        /// <summary>
        /// Loads whatever is still recent enough to count as context. Returns how
        /// many turns came back. Never throws — starting with no history is a
        /// perfectly good outcome, and much better than not starting.
        /// </summary>
        public int Restore()
        {
            TimeSpan maxAge = MaxRestoreAge;
            if (maxAge <= TimeSpan.Zero) return 0;

            try
            {
                if (!File.Exists(Path)) return 0;

                var loaded = new List<ConversationTurn>();
                DateTime cutoff = DateTime.Now - maxAge;
                int stale = 0;

                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path)))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) return 0;
                    foreach (JsonElement el in doc.RootElement.EnumerateArray())
                    {
                        ConversationTurn turn = ReadTurn(el);
                        if (turn == null) continue;
                        if (turn.RecordedAt < cutoff) { stale++; continue; }
                        loaded.Add(turn);
                    }
                }

                lock (gate)
                {
                    turns.Clear();
                    foreach (ConversationTurn t in loaded) turns.AddLast(t);
                    TrimLocked();
                }

                if (loaded.Count > 0 || stale > 0)
                {
                    Console.WriteLine(
                        $"[memory] restored {loaded.Count} turn(s)" +
                        (stale > 0 ? $", dropped {stale} older than {maxAge.TotalHours:0.#}h" : ""));
                }
                return loaded.Count;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[memory] could not restore {Path}: {ex.Message}");
                return 0;
            }
        }

        // Called with `gate` held, so the written file always matches the list it
        // was taken from — the lost-update shape that bit TriggerStore, where
        // snapshotting outside the lock let an older writer land last.
        private void SaveLocked()
        {
            if (!persist || MaxRestoreAge <= TimeSpan.Zero) return;

            string temp = null;
            try
            {
                string path = Path;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));

                temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartArray();
                    foreach (ConversationTurn t in turns) WriteTurn(writer, t);
                    writer.WriteEndArray();
                }

                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                temp = null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[memory] could not save: {ex.Message}");
            }
            finally
            {
                if (temp != null)
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
            }
        }

        private static void WriteTurn(Utf8JsonWriter w, ConversationTurn t)
        {
            w.WriteStartObject();
            w.WriteString("role", t.Role);
            w.WriteString("at", t.RecordedAt.ToString("o", CultureInfo.InvariantCulture));

            if (t.IsTool)
            {
                // Structured, exactly as it is held in memory. Flattening a tool
                // call to a string here would put imitable text back into the
                // assistant role the moment it was restored.
                w.WriteString("tool", t.ToolName);
                w.WriteStartObject("args");
                if (t.ToolArgs != null)
                {
                    foreach (KeyValuePair<string, string> kv in t.ToolArgs)
                    {
                        w.WriteString(kv.Key, kv.Value ?? string.Empty);
                    }
                }
                w.WriteEndObject();
            }
            else
            {
                w.WriteString("text", t.Text ?? string.Empty);
            }
            w.WriteEndObject();
        }

        private static ConversationTurn ReadTurn(JsonElement el)
        {
            try
            {
                if (el.ValueKind != JsonValueKind.Object) return null;

                DateTime at = el.TryGetProperty("at", out JsonElement atEl) &&
                              atEl.ValueKind == JsonValueKind.String &&
                              DateTime.TryParse(atEl.GetString(), CultureInfo.InvariantCulture,
                                  DateTimeStyles.RoundtripKind, out DateTime parsed)
                    ? parsed
                    : DateTime.MinValue; // unreadable date -> treated as stale

                string role = el.TryGetProperty("role", out JsonElement roleEl)
                    ? roleEl.GetString()
                    : null;

                if (role == "tool")
                {
                    if (!el.TryGetProperty("tool", out JsonElement toolEl)) return null;
                    var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (el.TryGetProperty("args", out JsonElement argsEl) &&
                        argsEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty p in argsEl.EnumerateObject())
                        {
                            args[p.Name] = p.Value.ValueKind == JsonValueKind.String
                                ? p.Value.GetString()
                                : p.Value.GetRawText();
                        }
                    }
                    return new ConversationTurn(toolEl.GetString(), args, at);
                }

                if (role != "user" && role != "model") return null;
                string text = el.TryGetProperty("text", out JsonElement textEl)
                    ? textEl.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(text)) return null;
                return new ConversationTurn(role, text, at);
            }
            catch
            {
                return null; // one bad entry costs one turn, not the file
            }
        }
    }
}
