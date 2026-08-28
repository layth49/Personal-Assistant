using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;

namespace Personal_Assistant.Configuration
{
    // One place for every tuning knob.
    //
    // These used to be nine separate environment variables read ad hoc from five
    // files, with three private copies of "parse an int with a fallback" and three
    // different ideas of what a boolean looks like. That happened because each was
    // added mid-debugging, one at a time, and it cost real time: `setx` does not
    // reach a running process and Visual Studio snapshots the environment when it
    // starts, so a changed setting silently did nothing until VS was restarted.
    //
    // Settings now live in App.config (deployed as `Personal Assistant.exe.config`,
    // editable next to the exe with no rebuild). An environment variable of the
    // same name still wins, for one-off experiments — and because that precedence
    // is exactly the trap above, Dump() prints every resolved value AND where it
    // came from at startup.
    //
    // Secrets stay in the environment where they belong: GEMINIAPI_KEY, SPEECH_KEY,
    // SPEECH_REGION, CONTACTS_PATH. Those are not settings and are not listed here.
    public static class LaithConfig
    {
        // key -> "key=value (source)", recorded as each setting resolves so Dump()
        // can report what actually took effect rather than re-deriving it.
        //
        // Keyed and sorted rather than appended, for two reasons. Most of these
        // are read from INSTANCE initialisers (PresenceGate's thresholds, say),
        // so every conversation re-resolves the same handful of settings — an
        // append-only list grew for the life of the process and would have made a
        // later Dump() print each setting once per session. And it is locked
        // because those constructions happen on session threads, not just at
        // startup. Sorted keeps the startup line in a stable, scannable order.
        private static readonly SortedDictionary<string, string> resolved =
            new SortedDictionary<string, string>(StringComparer.Ordinal);

        private static void Note(string key, string line)
        {
            lock (resolved) { resolved[key] = line; }
        }

        private const string EnvPrefix = "LAITH_";

        // App.config key "LiveHangoverMs" -> environment variable
        // "LAITH_LIVE_HANGOVER_MS". One name per setting, two spellings.
        internal static string EnvNameFor(string key)
        {
            var sb = new System.Text.StringBuilder(EnvPrefix, key.Length + 8);
            for (int i = 0; i < key.Length; i++)
            {
                if (i > 0 && char.IsUpper(key[i]) && !char.IsUpper(key[i - 1])) sb.Append('_');
                sb.Append(char.ToUpperInvariant(key[i]));
            }
            return sb.ToString();
        }

        // Raw lookup: environment first, then App.config, then nothing. Blank is
        // treated as absent — `setx VAR ""` is how a setx is undone, and it leaves
        // the variable present but empty rather than removing it.
        private static string Raw(string key, out string source)
        {
            string env = Environment.GetEnvironmentVariable(EnvNameFor(key));
            if (!string.IsNullOrWhiteSpace(env)) { source = "env"; return env.Trim(); }

            try
            {
                string cfg = ConfigurationManager.AppSettings[key];
                if (!string.IsNullOrWhiteSpace(cfg)) { source = "config"; return cfg.Trim(); }
            }
            catch (ConfigurationErrorsException ex)
            {
                Console.WriteLine($"[config] App.config unreadable ({ex.Message}); using defaults.");
            }

            source = "default";
            return null;
        }

        private static T Record<T>(string key, T value, string source)
        {
            Note(key, $"{key}={value} ({source})");
            return value;
        }

        public static string Text(string key, string fallback)
        {
            string raw = Raw(key, out string source);
            return Record(key, raw ?? fallback, source);
        }

        public static int Int(string key, int fallback, int min, int max)
        {
            string raw = Raw(key, out string source);
            if (raw != null &&
                int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                int clamped = Math.Min(Math.Max(parsed, min), max);
                if (clamped != parsed) source += $", clamped from {parsed}";
                return Record(key, clamped, source);
            }
            if (raw != null) source = $"default — '{raw}' is not a whole number";
            return Record(key, fallback, source);
        }

        public static double Double(string key, double fallback, double min, double max)
        {
            string raw = Raw(key, out string source);
            if (raw != null &&
                double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                double clamped = Math.Min(Math.Max(parsed, min), max);
                if (clamped != parsed) source += $", clamped from {parsed}";
                return Record(key, clamped, source);
            }
            if (raw != null) source = $"default — '{raw}' is not a number";
            return Record(key, fallback, source);
        }

        public static TimeSpan Seconds(string key, double fallback, double min, double max) =>
            TimeSpan.FromSeconds(Double(key, fallback, min, max));

        // Generous about spelling, and LOUD about anything it doesn't understand.
        // The previous form was a bare `== "0"`, so LAITH_LIVE_SERVER_VAD=false
        // silently meant *enabled* — the documented way to escape an unproven mode
        // didn't work, and nothing said so.
        public static bool Bool(string key, bool fallback)
        {
            bool? parsed = TriState(key, out string source);
            return Record(key, parsed ?? fallback, source);
        }

        // For settings with three meaningful states: force on, force off, or "let
        // the code decide" (grounding, which depends on the model).
        public static bool? TriState(string key, out string source)
        {
            string raw = Raw(key, out source);
            if (raw == null) return null;

            switch (raw.ToLowerInvariant())
            {
                case "1": case "true": case "yes": case "on": return true;
                case "0": case "false": case "no": case "off": return false;
                default:
                    Console.WriteLine(
                        $"[config] {key}='{raw}' is not a yes/no value — ignoring it. " +
                        "Use true/false, yes/no, on/off, or 1/0.");
                    source = $"default — '{raw}' unrecognised";
                    return null;
            }
        }

        public static bool? TriState(string key)
        {
            bool? v = TriState(key, out string source);
            Note(key, $"{key}={(v.HasValue ? v.ToString() : "auto")} ({source})");
            return v;
        }

        /// <summary>
        /// Prints every setting resolved so far and where each came from. Called
        /// once at startup — this line is what turns "I changed it and nothing
        /// happened" into a five-second diagnosis.
        /// </summary>
        public static void Dump()
        {
            string line;
            lock (resolved)
            {
                if (resolved.Count == 0)
                {
                    Console.WriteLine("[config] all settings at their defaults");
                    return;
                }
                line = string.Join("  ", resolved.Values);
            }
            Console.WriteLine("[config] " + line);
        }
    }
}
