using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Personal_Assistant.ProcessControl
{
    public sealed class KillResult
    {
        public int Killed { get; }
        public string MatchedName { get; }

        public KillResult(int killed, string matchedName)
        {
            Killed = killed;
            MatchedName = matchedName;
        }
    }

    // Terminates running processes by (image) name on the user's request.
    public class ProcessController
    {
        // Never kill these — L.A.I.T.H. itself or core OS processes. Killing a
        // system process would just throw AccessDenied anyway, but skipping them
        // avoids confusing "killed 0" reports and protects the assistant.
        private static readonly HashSet<string> Protected =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "personal assistant", "system", "idle", "csrss", "wininit",
                "winlogon", "services", "lsass", "smss", "explorer"
            };

        // Whether any process with this (image) name is running. Used by standing
        // triggers to notice an app starting or stopping, so it runs on the
        // trigger ticker once a second: GetProcessesByName is a single filtered
        // snapshot rather than a full enumeration, which is cheap enough for that,
        // but keep anything heavier out of this path.
        // Virtual so a harness can drive the app_starts / app_stops triggers off a
        // flag instead of a real program starting and stopping on cue.
        public virtual bool IsRunning(string name)
        {
            string bare = Normalize(name);
            if (bare.Length == 0) return false;
            try
            {
                Process[] found = Process.GetProcessesByName(bare);
                foreach (Process p in found) p.Dispose();
                return found.Length > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[process] could not check '{bare}': {ex.Message}");
                return false;
            }
        }

        // Trims and drops a trailing ".exe" — users say "discord", Task Manager
        // says "Discord.exe", and Process.GetProcessesByName wants neither the
        // extension nor the whitespace.
        private static string Normalize(string name)
        {
            string bare = (name ?? string.Empty).Trim();
            if (bare.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                bare = bare.Substring(0, bare.Length - 4);
            }
            return bare;
        }

        // Kills every process whose name matches `name` (with or without a
        // trailing ".exe"). Returns how many were terminated and the normalized
        // name that was matched against.
        public KillResult KillByName(string name)
        {
            string bare = Normalize(name);

            if (bare.Length == 0 || Protected.Contains(bare))
            {
                return new KillResult(0, bare);
            }

            int killed = 0;
            foreach (var process in Process.GetProcessesByName(bare))
            {
                using (process)
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(3000);
                        killed++;
                    }
                    catch (Exception ex)
                    {
                        // Access denied (elevated/system process) or the process
                        // already exited — count only the ones we actually took down.
                        Console.WriteLine($"[process] could not kill {bare} (pid {SafePid(process)}): {ex.Message}");
                    }
                }
            }

            return new KillResult(killed, bare);
        }

        private static string SafePid(Process p)
        {
            try { return p.Id.ToString(); }
            catch { return "?"; }
        }
    }
}
