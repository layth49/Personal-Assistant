using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Personal_Assistant.AppLaunching
{
    // Opens desktop apps by spoken name. Resolution order:
    //   1. A tiny alias table — only for system utilities that have no obvious
    //      Start Menu entry (Task Manager, Settings, ...) and a couple of spoken
    //      abbreviations (e.g. "vs code"). Deliberately NOT a list of every app.
    //   2. Fuzzy match against every Start Menu shortcut. This is what makes it
    //      "just work" for apps we never hardcoded — it covers essentially
    //      anything the user could launch from the Start menu, and stays current
    //      as apps are installed/removed (the folders are re-scanned each call).
    //   3. Raw ShellExecute (PATH + "App Paths" registry) as a final fallback,
    //      which also catches system apps that have no Start Menu shortcut
    //      (notepad, calc, mspaint, ...).
    public class AppLauncher
    {
        private static readonly Dictionary<string, string> Aliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["browser"] = "",                 // whatever the default browser is
                ["file explorer"] = "explorer",
                ["explorer"] = "explorer",
                ["files"] = "explorer",
                ["task manager"] = "taskmgr",
                ["control panel"] = "control",
                ["settings"] = "ms-settings:",
                ["command prompt"] = "cmd",
                ["terminal"] = "wt",
                ["calculator"] = "calc",
                // Spoken abbreviations Start-Menu matching can't infer on its own.
                ["vs code"] = "code",
                ["vscode"] = "code",
            };

        // Attempts to launch the named app. Returns true on success; `launched`
        // is set to the resolved display name that was started (e.g. the real
        // Start Menu name), so the caller can confirm what it opened.
        public bool TryLaunch(string appName, out string launched)
        {
            launched = (appName ?? string.Empty).Trim();
            if (launched.Length == 0) return false;

            // 1. Alias (exact spoken phrase).
            if (Aliases.TryGetValue(launched, out string alias) && TryStart(alias))
            {
                return true;
            }

            // 2. Start Menu fuzzy match — the "don't hardcode every app" path.
            var match = FindStartMenuMatch(launched);
            if (match != null && TryStart(match.Path))
            {
                launched = match.DisplayName; // report the real app name
                return true;
            }

            // 3. Raw ShellExecute (App Paths / PATH), incl. stripping a .exe.
            if (TryStart(launched)) return true;
            if (launched.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                TryStart(launched.Substring(0, launched.Length - 4)))
            {
                return true;
            }

            return false;
        }

        private sealed class Shortcut
        {
            public string DisplayName; // e.g. "Visual Studio Code"
            public string Norm;        // normalized for matching
            public string Path;        // full .lnk path
        }

        // The two Start Menu "Programs" trees (per-user and all-users). Every
        // installed app that shows in the Start menu has a .lnk here.
        private static IEnumerable<Shortcut> EnumerateShortcuts()
        {
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            };

            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories); }
                catch { continue; }

                foreach (var f in files)
                {
                    string name;
                    try { name = Path.GetFileNameWithoutExtension(f); }
                    catch { continue; }
                    yield return new Shortcut { DisplayName = name, Norm = Normalize(name), Path = f };
                }
            }
        }

        private sealed class Match { public string DisplayName; public string Path; }

        private static Match FindStartMenuMatch(string query)
        {
            string q = Normalize(query);
            if (q.Length == 0) return null;
            string[] qWords = q.Split(' ');

            Shortcut best = null;
            int bestScore = 0;
            foreach (var sc in EnumerateShortcuts())
            {
                int score = Score(q, qWords, sc.Norm);
                if (score == 0) continue;
                // Higher score wins; on a tie prefer the shorter (more specific)
                // shortcut name, e.g. "Spotify" over "Spotify Web Helper".
                if (score > bestScore ||
                    (score == bestScore && sc.Norm.Length < best.Norm.Length))
                {
                    bestScore = score;
                    best = sc;
                }
            }
            return best == null ? null : new Match { DisplayName = best.DisplayName, Path = best.Path };
        }

        private static int Score(string q, string[] qWords, string name)
        {
            if (name == q) return 1000; // exact

            // Match tier. Prefix and whole-word are treated equally on purpose:
            // otherwise "chrome" prefers "Chrome Remote Desktop" (a prefix) over
            // "Google Chrome" (a word). The coverage bonus below then breaks that
            // tie toward the name the query accounts for most of.
            int tier;
            if (name.StartsWith(q, StringComparison.Ordinal) || IsWholeWord(name, q)) tier = 500;
            else if (name.Contains(q)) tier = 300;
            else if (qWords.Length > 0 && qWords.All(name.Contains)) tier = 200;
            else return 0;

            // 0..200 for how much of the shortcut name the query covers — so
            // "chrome"/"Google Chrome" (46%) beats "chrome"/"Chrome Remote
            // Desktop" (29%), and short exact-ish names win in general.
            int coverage = (int)(200.0 * q.Length / Math.Max(1, name.Length));
            return tier + coverage;
        }

        // True if `word` appears in `text` bounded by spaces / string ends.
        private static bool IsWholeWord(string text, string word)
        {
            int i = 0;
            while ((i = text.IndexOf(word, i, StringComparison.Ordinal)) >= 0)
            {
                bool leftOk = i == 0 || text[i - 1] == ' ';
                int end = i + word.Length;
                bool rightOk = end == text.Length || text[end] == ' ';
                if (leftOk && rightOk) return true;
                i = end;
            }
            return false;
        }

        // Lowercase, turn separators into spaces, drop other punctuation, and
        // collapse runs of spaces — so "Node.js", "Node-JS" and "node js" all
        // normalize alike.
        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c) || c == ' ') sb.Append(c);
                else if (c == '-' || c == '_' || c == '.' || c == '+') sb.Append(' ');
                // other punctuation is dropped
            }
            return string.Join(" ",
                sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static bool TryStart(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[applauncher] could not start '{target}': {ex.Message}");
                return false;
            }
        }
    }
}
