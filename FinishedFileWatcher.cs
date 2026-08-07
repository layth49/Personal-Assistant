using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Personal_Assistant.Triggers
{
    // Raises an event when a file has FINISHED arriving in a folder.
    //
    // Event-driven rather than polled: FileSystemWatcher is the OS telling us,
    // where a directory scan on the trigger ticker would mean enumerating
    // Downloads once a second to hear about something Windows was willing to
    // announce. That matters more the bigger the folder gets.
    //
    // The hard part is not noticing the file, it is knowing it is done. A
    // browser creates the destination file when the download STARTS, so a bare
    // Created event fires at the beginning of a 4 GB download, not the end. Two
    // things fix that, and both are needed:
    //
    //   * ignore the partial-download extensions browsers use while writing
    //     (.crdownload, .part, ...), and
    //   * treat a file as done only once its size has stopped changing.
    //
    // The settle check is a short poll, but it is per-file and lives only for
    // the seconds after an event — not a standing scan of the whole directory.
    public sealed class FinishedFileWatcher : IDisposable
    {
        // While a browser is writing, the real name is one of these. The rename
        // to the final name IS the completion signal, and it arrives as a
        // Renamed/Created event on the name we actually care about.
        private static readonly string[] PartialExtensions =
        {
            ".crdownload", ".part", ".partial", ".download", ".opdownload",
            ".tmp", ".temp", ".!ut"
        };

        // How long a file's size must hold steady before it counts as finished.
        private static readonly TimeSpan SettleFor = TimeSpan.FromSeconds(2);

        private readonly FileSystemWatcher watcher;
        private readonly string pattern;
        private readonly Action<string> onFinished;
        private readonly Timer settleTimer;

        // Files seen but not yet settled: path -> (last size, when it last changed).
        private readonly Dictionary<string, KeyValuePair<long, DateTime>> pending =
            new Dictionary<string, KeyValuePair<long, DateTime>>(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new object();
        private bool disposed;

        public string Folder { get; }

        /// <param name="pattern">A glob like "*.pdf", or null/"*" for anything.</param>
        /// <param name="onFinished">Called with the full path, off the watcher thread.</param>
        public FinishedFileWatcher(string folder, string pattern, Action<string> onFinished)
        {
            Folder = folder ?? throw new ArgumentNullException(nameof(folder));
            this.pattern = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern.Trim();
            this.onFinished = onFinished ?? throw new ArgumentNullException(nameof(onFinished));

            watcher = new FileSystemWatcher(folder)
            {
                // Not filtered to `pattern` here on purpose: a download in
                // progress is named "report.pdf.crdownload", which a "*.pdf"
                // filter would never show us — so we would miss the rename that
                // is the completion signal. Filtering happens in Matches instead.
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                IncludeSubdirectories = false
            };

            watcher.Created += (s, e) => Notice(e.FullPath);
            watcher.Changed += (s, e) => Notice(e.FullPath);
            watcher.Renamed += (s, e) => Notice(e.FullPath);
            watcher.Error += (s, e) =>
                Console.WriteLine($"[filewatch] {Folder}: {e.GetException().Message}");

            watcher.EnableRaisingEvents = true;

            // Only checks files an event has already told us about, so it does no
            // work at all in the ordinary case of nothing downloading.
            settleTimer = new Timer(_ => CheckSettled(), null, 1000, 1000);
        }

        private void Notice(string path)
        {
            if (disposed) return;
            if (!Matches(path)) return;

            lock (gate)
            {
                // Reset the clock on every event: a file still being written keeps
                // announcing itself, and each one means it is not done yet.
                pending[path] = new KeyValuePair<long, DateTime>(-1, DateTime.UtcNow);
            }
        }

        private bool Matches(string path)
        {
            string name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) return false;

            // Still being written under a temporary name.
            string ext = Path.GetExtension(name);
            foreach (string partial in PartialExtensions)
            {
                if (string.Equals(ext, partial, StringComparison.OrdinalIgnoreCase)) return false;
            }

            if (pattern == "*") return true;
            return GlobMatches(name, pattern);
        }

        private void CheckSettled()
        {
            if (disposed) return;

            List<string> finished = null;
            lock (gate)
            {
                if (pending.Count == 0) return;

                foreach (string path in pending.Keys.ToList())
                {
                    long size;
                    try
                    {
                        var info = new FileInfo(path);
                        if (!info.Exists) { pending.Remove(path); continue; }
                        size = info.Length;
                    }
                    catch (Exception)
                    {
                        // Locked by the writer, or gone. Either way, not settled;
                        // leave it and look again next second.
                        continue;
                    }

                    KeyValuePair<long, DateTime> seen = pending[path];
                    if (seen.Key != size)
                    {
                        // Still growing.
                        pending[path] = new KeyValuePair<long, DateTime>(size, DateTime.UtcNow);
                        continue;
                    }

                    if (DateTime.UtcNow - seen.Value < SettleFor) continue;

                    pending.Remove(path);
                    (finished ?? (finished = new List<string>())).Add(path);
                }
            }

            if (finished == null) return;
            foreach (string path in finished)
            {
                Console.WriteLine($"[filewatch] finished: {Path.GetFileName(path)}");
                try { onFinished(path); }
                catch (Exception ex) { Console.WriteLine($"[filewatch] handler threw: {ex.Message}"); }
            }
        }

        // A deliberately small glob: `*` and `?`, which is all a spoken pattern
        // ("any PDF") ever amounts to. Written out rather than translated to a
        // regex so a pattern the user says cannot become a pathological one.
        public static bool GlobMatches(string name, string glob)
        {
            int n = 0, g = 0, starAt = -1, matchAt = 0;
            while (n < name.Length)
            {
                if (g < glob.Length &&
                    (glob[g] == '?' || char.ToLowerInvariant(glob[g]) == char.ToLowerInvariant(name[n])))
                {
                    n++; g++;
                }
                else if (g < glob.Length && glob[g] == '*')
                {
                    starAt = g++;
                    matchAt = n;
                }
                else if (starAt >= 0)
                {
                    g = starAt + 1;
                    n = ++matchAt;
                }
                else
                {
                    return false;
                }
            }
            while (g < glob.Length && glob[g] == '*') g++;
            return g == glob.Length;
        }

        public void Dispose()
        {
            disposed = true;
            try { settleTimer?.Dispose(); } catch { }
            try
            {
                if (watcher != null)
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
            }
            catch { }
        }
    }
}
