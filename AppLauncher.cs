using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Personal_Assistant.AppLaunching
{
    // Opens desktop apps by spoken name. Resolves a friendly name to a launch
    // target and starts it via ShellExecute, which honours PATH and the
    // "App Paths" registry (so things like chrome, spotify, discord launch by
    // their bare name even though they aren't on PATH).
    public class AppLauncher
    {
        // Spoken name -> launch target. Values are either executables that
        // ShellExecute can resolve, or shell/URI targets (ms-settings:, shell:).
        // Keys are matched case-insensitively and by longest-key-first so
        // "task manager" wins over a hypothetical "task".
        private static readonly Dictionary<string, string> Aliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["chrome"] = "chrome",
                ["google chrome"] = "chrome",
                ["firefox"] = "firefox",
                ["browser"] = "",
                ["spotify"] = "spotify",
                ["discord"] = "discord",
                ["steam"] = "steam",
                ["notepad"] = "notepad",
                ["calculator"] = "calc",
                ["file explorer"] = "explorer",
                ["explorer"] = "explorer",
                ["files"] = "explorer",
                ["task manager"] = "taskmgr",
                ["control panel"] = "control",
                ["settings"] = "ms-settings:",
                ["command prompt"] = "cmd",
                ["terminal"] = "wt",
                ["outlook"] = "outlook",
                ["visual studio"] = "devenv",
                ["vs code"] = "code",
                ["visual studio code"] = "code",
                ["vscode"] = "code",
                ["snipping tool"] = "snippingtool",
            };

        // Attempts to launch the named app. Returns true on success; `launched`
        // is set to the resolved display name that was started.
        public bool TryLaunch(string appName, out string launched)
        {
            launched = (appName ?? string.Empty).Trim();
            if (launched.Length == 0) return false;

            string target = ResolveTarget(launched);

            // First try the resolved target as-is. ShellExecute resolves PATH +
            // App Paths + registered protocols.
            if (TryStart(target)) return true;

            // If the caller gave something with a .exe already, also try stripping it.
            if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                TryStart(target.Substring(0, target.Length - 4)))
            {
                return true;
            }

            return false;
        }

        private static string ResolveTarget(string appName)
        {
            if (Aliases.TryGetValue(appName, out string mapped))
            {
                return mapped;
            }
            // Not a known alias — let ShellExecute try the raw spoken name (works
            // for anything registered under App Paths or on PATH).
            return appName;
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
