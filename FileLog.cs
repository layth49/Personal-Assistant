using Personal_Assistant.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Personal_Assistant.Diagnostics
{
    /// <summary>
    /// Mirrors everything written to the console into a file, one file per run.
    /// </summary>
    /// <remarks>
    /// WHY THIS EXISTS. The app is built as a WinExe, so it has no console
    /// attached and every Console.WriteLine goes into a void. That is invisible
    /// until something has to be diagnosed after the fact — and call screening is
    /// exactly that feature, because it runs while nobody is at the machine and
    /// each retry costs a real phone call.
    ///
    /// Through 2026-08-22 the only way to see anything was to launch the app with
    /// the shell redirecting stdout, which failed in three separate ways: it was
    /// forgotten, so a whole call produced no record; a stale cmd window kept the
    /// previous log locked, so the app would not start; and the redirect was tied
    /// to how the app happened to be launched rather than to the app. A feature
    /// that runs unattended cannot have its only record depend on that.
    ///
    /// DESIGN NOTES, each one load-bearing:
    ///
    ///   * A TEE, not a replacement. Whatever Console.Out already was keeps
    ///     working, so a redirected launch still writes to its file and a real
    ///     console still prints.
    ///
    ///   * AutoFlush, always. The bugs this was built to catch are HANGS and
    ///     deadlocks — the process is still alive, nothing has crashed, and the
    ///     last line before the stall is the whole answer. A buffered writer loses
    ///     precisely that line.
    ///
    ///   * UTF-8. The console codepage mangled every em dash into a replacement
    ///     character, which made the transcripts of screened calls harder to read
    ///     than they needed to be.
    ///
    ///   * One file per RUN, pruned to the newest few, rather than one rolling
    ///     file. It matches how these get read — "the log for that call" — and
    ///     needs no rotation logic to get wrong.
    ///
    ///   * Failure here is never fatal. If the file cannot be opened the app
    ///     carries on with console output exactly as before; losing the log is bad,
    ///     losing the assistant because of the log would be worse.
    /// </remarks>
    public static class FileLog
    {
        private static Tee tee;

        /// <summary>The file being written, or null when logging is off.</summary>
        public static string Path { get; private set; }

        /// <summary>
        /// Starts mirroring the console to a file. Call once, as early in Main as
        /// possible — anything written before this is not captured, which is why
        /// it goes ahead of even the config dump.
        /// </summary>
        public static void Start()
        {
            if (tee != null) return;                       // already running
            if (!LaithConfig.Bool("LogToFile", true)) return;

            try
            {
                string dir = LaithConfig.Text("LogDir", "");
                if (string.IsNullOrWhiteSpace(dir))
                {
                    dir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "LAITH", "logs");
                }

                Directory.CreateDirectory(dir);
                Prune(dir, LaithConfig.Int("LogKeepRuns", 20, 1, 500));

                string path = System.IO.Path.Combine(
                    dir, "laith-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");

                // FileShare.ReadWrite so the file can be tailed, or read by whoever
                // is diagnosing, WHILE the app is still running — which is the
                // normal case when the thing being diagnosed is a hang.
                var stream = new FileStream(
                    path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);

                var writer = new StreamWriter(stream, new UTF8Encoding(false))
                {
                    AutoFlush = true
                };

                tee = new Tee(Console.Out, writer);
                Console.SetOut(tee);
                Path = path;

                Console.WriteLine($"[log] writing this session to {path}");
            }
            catch (Exception ex)
            {
                // Console only — the file is exactly what is not working.
                Console.WriteLine($"[log] could not start file logging: {ex.Message}");
                tee = null;
                Path = null;
            }
        }

        /// <summary>Flushes and closes the log. Safe to call more than once.</summary>
        public static void Stop()
        {
            Tee t = tee;
            if (t == null) return;
            tee = null;

            try { Console.SetOut(t.Console); } catch { }
            try { t.CloseFile(); } catch { }
        }

        /// <summary>
        /// Keeps the newest <paramref name="keep"/> logs and deletes the rest, so
        /// an assistant that runs every day does not quietly fill a disk.
        /// </summary>
        private static void Prune(string dir, int keep)
        {
            try
            {
                List<FileInfo> old = new DirectoryInfo(dir)
                    .GetFiles("laith-*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Skip(Math.Max(0, keep - 1))     // -1: this run is about to add one
                    .ToList();

                foreach (FileInfo f in old)
                {
                    // A log still held open by another running instance is not ours
                    // to remove; skipping it is the whole handling.
                    try { f.Delete(); } catch { }
                }
            }
            catch { /* pruning is housekeeping, never a reason to fail startup */ }
        }

        /// <summary>
        /// Writes to the original console AND the file. Every override forwards to
        /// both, and the lock exists because Console.WriteLine is called from the
        /// poll threads, the audio callbacks and the UI thread at once — an
        /// interleaved log is worse than none when it is being read for ordering.
        /// </summary>
        private sealed class Tee : TextWriter
        {
            public TextWriter Console { get; }
            private TextWriter file;
            private readonly object gate = new object();

            public Tee(TextWriter console, TextWriter file)
            {
                Console = console;
                this.file = file;
            }

            public override Encoding Encoding => Encoding.UTF8;

            public void CloseFile()
            {
                lock (gate)
                {
                    TextWriter f = file;
                    file = null;
                    try { f?.Flush(); } catch { }
                    try { f?.Dispose(); } catch { }
                }
            }

            // Whether the next file character begins a line and therefore wants a
            // timestamp. Tracked rather than assumed, because stamping per CALL
            // instead of per LINE puts the time in the middle of anything written
            // with Write() and then finished with WriteLine():
            //     "[test] partial 00:56:39.326  completed"
            // Nothing in the app writes that way today, which is exactly why it
            // would go unnoticed until something did.
            private bool atLineStart = true;

            /// <summary>
            /// Writes to the file, stamping each LINE once. Timestamps go in the
            /// file only: on screen the lines are watched live and the time is
            /// noise, while in a file read hours later "when" is half the
            /// question — and for the hangs this was built for, the gap between
            /// two lines IS the finding.
            /// </summary>
            private void ToFile(string text)
            {
                if (file == null || string.IsNullOrEmpty(text)) return;

                foreach (char c in text)
                {
                    if (atLineStart && c != '\n' && c != '\r')
                    {
                        file.Write(DateTime.Now.ToString("HH:mm:ss.fff") + "  ");
                        atLineStart = false;
                    }

                    file.Write(c);
                    if (c == '\n') atLineStart = true;
                }
            }

            public override void Write(char value)
            {
                lock (gate)
                {
                    try { Console?.Write(value); } catch { }
                    try { ToFile(value.ToString()); } catch { }
                }
            }

            public override void Write(string value)
            {
                lock (gate)
                {
                    try { Console?.Write(value); } catch { }
                    try { ToFile(value); } catch { }
                }
            }

            public override void WriteLine(string value)
            {
                lock (gate)
                {
                    try { Console?.WriteLine(value); } catch { }
                    try { ToFile(value + Environment.NewLine); } catch { }
                }
            }

            public override void WriteLine()
            {
                lock (gate)
                {
                    try { Console?.WriteLine(); } catch { }
                    try { ToFile(Environment.NewLine); } catch { }
                }
            }

            public override void Flush()
            {
                lock (gate)
                {
                    try { Console?.Flush(); } catch { }
                    try { file?.Flush(); } catch { }
                }
            }
        }
    }
}
