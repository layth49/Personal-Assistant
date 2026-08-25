using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Personal_Assistant.Configuration;

namespace Personal_Assistant.Notes
{
    public sealed class NoteFile
    {
        public string Name { get; set; }          // "groceries"
        public string Path { get; set; }
        public DateTime Modified { get; set; }
        public int Lines { get; set; }
    }

    // Plain markdown files in one folder, read and written by voice.
    //
    // Deliberately not a database: the whole point is that these stay editable
    // by hand in any editor, and a note the assistant mangles must be
    // recoverable without this app. Hence RewriteWithBackup below.
    public class NotesService
    {
        // Local by construction. Documents is OneDrive-redirected on this
        // machine and MyPictures resolves to the empty string, so
        // Environment.SpecialFolder is not trustworthy here — see
        // ScreenshotService for the same reasoning. Overridable via App.config
        // ("NotesDir") for anyone who wants them elsewhere.
        private static readonly string DefaultDir = @"C:\Users\layth\Notes";

        private readonly string dir;

        public NotesService()
        {
            string configured = LaithConfig.Text("NotesDir", DefaultDir);
            dir = string.IsNullOrWhiteSpace(configured) ? DefaultDir : configured;
        }

        public string Directory_ => dir;

        // Where a note called `name` lives. Note names come from speech, so the
        // filename is sanitised rather than trusted: without this, "my ../../
        // notes" would write outside the notes folder entirely.
        private string PathFor(string name)
        {
            string bare = (name ?? string.Empty).Trim();
            if (bare.Length == 0) bare = "notes";

            // Keep only the last segment: a spoken name should never be a path,
            // and flattening one produces a junk filename even when the guard
            // below correctly keeps it inside the folder.
            int lastSep = bare.LastIndexOfAny(new[] { '/', '\\' });
            if (lastSep >= 0 && lastSep < bare.Length - 1) bare = bare.Substring(lastSep + 1);

            foreach (char c in Path.GetInvalidFileNameChars())
                bare = bare.Replace(c, ' ');
            bare = bare.Replace("..", " ").Trim();
            if (bare.Length == 0) bare = "notes";

            if (!bare.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) bare += ".md";

            string full = Path.GetFullPath(Path.Combine(dir, bare));

            // Belt and braces: after sanitising, the result must still be inside
            // the notes folder.
            string root = Path.GetFullPath(dir);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("note path escaped the notes folder");

            return full;
        }

        public IReadOnlyList<NoteFile> List()
        {
            if (!System.IO.Directory.Exists(dir)) return new List<NoteFile>();

            return System.IO.Directory.GetFiles(dir, "*.md")
                .Select(p => new NoteFile
                {
                    Name = Path.GetFileNameWithoutExtension(p),
                    Path = p,
                    Modified = File.GetLastWriteTime(p),
                    Lines = SafeLineCount(p)
                })
                .OrderByDescending(n => n.Modified)
                .ToList();
        }

        // Best match for a spoken note name, or null. Exact first, then a
        // contains match, so "grocery" finds "groceries".
        public NoteFile Resolve(string name)
        {
            var all = List();
            if (all.Count == 0) return null;

            string want = (name ?? string.Empty).Trim();
            if (want.Length == 0) return all[0];   // most recently touched

            var exact = all.FirstOrDefault(n =>
                string.Equals(n.Name, want, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            var contains = all.FirstOrDefault(n =>
                               n.Name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
                           ?? all.FirstOrDefault(n =>
                               want.IndexOf(n.Name, StringComparison.OrdinalIgnoreCase) >= 0);
            if (contains != null) return contains;

            // Singular/plural: people say "my grocery list" for groceries.md,
            // and a substring test alone never matches those two.
            string stem = Stem(want);
            return all.FirstOrDefault(n => Stem(n.Name) == stem);
        }

        // Crude, deliberately: enough to fold grocery/groceries and note/notes
        // onto one key without pulling in a real stemmer.
        private static string Stem(string word)
        {
            string w = (word ?? string.Empty).Trim().ToLowerInvariant();
            if (w.EndsWith("s")) w = w.Substring(0, w.Length - 1);
            if (w.EndsWith("ie")) return w.Substring(0, w.Length - 2) + "i";
            if (w.EndsWith("y")) return w.Substring(0, w.Length - 1) + "i";
            return w;
        }

        public string Read(string name)
        {
            NoteFile note = Resolve(name);
            return note == null ? null : File.ReadAllText(note.Path);
        }

        // Appends a line. Creates the note if it doesn't exist, which is what
        // makes "add milk to my groceries" work the first time.
        public string Append(string name, string line)
        {
            System.IO.Directory.CreateDirectory(dir);

            NoteFile existing = Resolve(name);
            string path = existing?.Path ?? PathFor(name);
            bool isNew = !File.Exists(path);

            var sb = new StringBuilder();
            if (isNew)
            {
                sb.AppendLine("# " + Title(name));
                sb.AppendLine();
            }
            else
            {
                // Only add a separating newline when the file doesn't already
                // end with one, or repeated appends run together on one line.
                string current = File.ReadAllText(path);
                if (current.Length > 0 && !current.EndsWith("\n")) sb.AppendLine();
            }

            sb.AppendLine("- " + (line ?? string.Empty).Trim());
            File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        // Replaces a note's contents, keeping a timestamped backup alongside it.
        //
        // The backup is not optional. This is the one operation that can destroy
        // something the user wrote by hand, and it is driven by a model
        // rewriting text it may have misread — so the previous version has to
        // survive somewhere the user can find without this app.
        public string RewriteWithBackup(string path, string newContent, out string backupPath)
        {
            string original = File.ReadAllText(path);
            backupPath = path + "." + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".bak";
            File.WriteAllText(backupPath, original, new UTF8Encoding(false));

            File.WriteAllText(path, newContent, new UTF8Encoding(false));
            return path;
        }

        private static string Title(string name)
        {
            string bare = (name ?? "Notes").Trim();
            if (bare.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                bare = bare.Substring(0, bare.Length - 3);
            if (bare.Length == 0) return "Notes";
            return char.ToUpperInvariant(bare[0]) + bare.Substring(1);
        }

        private static int SafeLineCount(string path)
        {
            try { return File.ReadAllLines(path).Length; }
            catch { return 0; }
        }
    }
}
