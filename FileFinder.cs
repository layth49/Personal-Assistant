using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Personal_Assistant.FileFinding
{
    // One candidate file, with the score that got it there.
    public sealed class FileMatch
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public DateTime Modified { get; set; }
        public double Score { get; set; }

        // How many of the description's meaningful words actually appear in this
        // file's name or path. Zero means the only reason this file ranked at
        // all is that it is recent — see FileSearchResult.Confident.
        public int TermHits { get; set; }

        // Where the file lives, in words rather than a full path — a spoken
        // answer reading out "C colon backslash users backslash..." is useless.
        public string Where { get; set; }
    }

    // The outcome of one search, carrying enough context for the caller to tell
    // "here is your file" apart from "here is a recent file, but nothing matched
    // what you said". Without that distinction the finder answers a request for
    // a resume with whatever was downloaded most recently, stated as fact.
    public sealed class FileSearchResult
    {
        public IReadOnlyList<FileMatch> Matches { get; set; }

        // The description contained at least one word worth matching on (as
        // opposed to "open the newest one", which is pure recency).
        public bool HadTerms { get; set; }

        // The words that were searched for, for a spoken "nothing matched X".
        public string[] Terms { get; set; }

        public FileMatch Best =>
            Matches != null && Matches.Count > 0 ? Matches[0] : null;

        // True when the top hit is there because it matched, not merely because
        // it is recent. A pure-recency request is trivially confident.
        public bool Confident =>
            Best != null && (!HadTerms || Best.TermHits > 0);
    }

    // Finds a file from a loose spoken description ("that PDF I downloaded
    // earlier") and opens it.
    //
    // Read-only by construction: this class enumerates and shells out to open.
    // It never moves, renames or deletes anything.
    public class FileFinder
    {
        // Searched in order, best-guess-first. These are deliberately hardcoded
        // local paths instead of Environment.SpecialFolder: on this machine
        // MyDocuments resolves into OneDrive and MyPictures resolves to the
        // EMPTY STRING (its registry entry points at a OneDrive folder that no
        // longer exists), which would silently make Path.Combine produce a
        // relative path. OneDrive\Documents is still listed last because that
        // is where the user's real documents currently live.
        private static readonly string[] Roots =
        {
            @"C:\Users\layth\Downloads",
            @"C:\Users\layth\Desktop",
            @"C:\Users\layth\Pictures",
            @"C:\Users\layth\Documents",
            @"C:\Users\layth\Videos",
            @"C:\Users\layth\Music",
            @"C:\Users\layth\OneDrive\Documents",
        };

        // Depth is capped because this runs inside a voice interaction. A full
        // recursive walk of the profile takes seconds and the user is waiting.
        private const int MaxDepth = 3;
        private const int MaxFilesScanned = 20000;

        // Directory names that are never what someone means by "my file" and
        // which are where all the scan time goes.
        private static readonly HashSet<string> SkipDirs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AppData", "node_modules", ".git", "obj", "bin", "packages",
                ".vs", "$RECYCLE.BIN", "System Volume Information", "venv",
                "__pycache__", ".next", "dist", "target"
            };

        // Words that carry no signal about WHICH file is meant, so matching on
        // them would score every file equally.
        private static readonly HashSet<string> Filler =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "the","that","this","my","a","an","i","it","file","open","find",
                "please","laith","some","of","in","on","from","for","me","up",
                "downloaded","download","saved","save","earlier","ago","recent",
                "recently","newest","latest","last","yesterday","today","was",
                "is","and","to","with","show","get","pull","bring","one","thing",
                "took","take","taken","made","make","put","about","there","here"
            };

        // Spoken type words mapped to the extensions they mean.
        private static readonly Dictionary<string, string[]> TypeWords =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["pdf"] = new[] { ".pdf" },
                ["image"] = new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" },
                ["picture"] = new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" },
                ["photo"] = new[] { ".png", ".jpg", ".jpeg", ".heic", ".webp" },
                ["screenshot"] = new[] { ".png", ".jpg", ".jpeg" },
                ["video"] = new[] { ".mp4", ".mkv", ".mov", ".avi", ".webm" },
                ["movie"] = new[] { ".mp4", ".mkv", ".mov", ".avi" },
                ["clip"] = new[] { ".mp4", ".mkv", ".mov", ".webm" },
                ["song"] = new[] { ".mp3", ".flac", ".wav", ".m4a" },
                ["music"] = new[] { ".mp3", ".flac", ".wav", ".m4a" },
                ["audio"] = new[] { ".mp3", ".wav", ".flac", ".m4a", ".ogg" },
                ["doc"] = new[] { ".docx", ".doc", ".odt", ".rtf" },
                ["document"] = new[] { ".docx", ".doc", ".pdf", ".odt", ".txt" },
                ["word"] = new[] { ".docx", ".doc" },
                ["spreadsheet"] = new[] { ".xlsx", ".xls", ".csv" },
                ["excel"] = new[] { ".xlsx", ".xls" },
                ["csv"] = new[] { ".csv" },
                ["presentation"] = new[] { ".pptx", ".ppt" },
                ["powerpoint"] = new[] { ".pptx", ".ppt" },
                ["slides"] = new[] { ".pptx", ".ppt" },
                ["zip"] = new[] { ".zip", ".rar", ".7z" },
                ["archive"] = new[] { ".zip", ".rar", ".7z", ".tar", ".gz" },
                ["text"] = new[] { ".txt", ".md" },
                ["installer"] = new[] { ".exe", ".msi" },
            };

        // Ranks candidates for a spoken description. Never throws for a bad
        // description — an unmatchable one simply returns the most recent files,
        // which is the right answer for "open that thing from earlier".
        public FileSearchResult Find(string description, int take = 5)
        {
            string desc = description ?? string.Empty;
            string[] terms = Tokenize(desc);
            HashSet<string> wantedExts = ExtensionsFor(desc);
            string[] typeWords = TypeWords.Keys.Where(k => ContainsWord(desc, k)).ToArray();
            bool recencyAsked = MentionsRecency(desc);

            var matches = new List<FileMatch>();
            int scanned = 0;

            foreach (string root in Roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (string path in Enumerate(root, 0, ref scanned))
                {
                    string name = Path.GetFileName(path);
                    string ext = Path.GetExtension(path);

                    // A stated type is a filter, not a hint: "the PDF" should
                    // never come back with a .png just because the name matched.
                    if (wantedExts != null && !wantedExts.Contains(ext)) continue;

                    double score = 0;
                    int hits = 0;
                    string stem = Path.GetFileNameWithoutExtension(name);
                    foreach (string term in terms)
                    {
                        if (stem.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) { score += 10; hits++; }
                        else if (path.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) { score += 3; hits++; }
                    }

                    // A type word is a filter, but it is also a weak name hint:
                    // "screenshot" should prefer a file actually called
                    // Screenshot over a same-day IMG_7312.jpeg that merely has
                    // an image extension. Deliberately does NOT count as a term
                    // hit — it says nothing about WHICH file is meant.
                    foreach (string tw in typeWords)
                        if (stem.IndexOf(tw, StringComparison.OrdinalIgnoreCase) >= 0) score += 4;

                    // Everything gets a recency component, because "that file I
                    // downloaded" is overwhelmingly a recent one. It is the sole
                    // ranking signal when the description had no usable terms.
                    DateTime modified;
                    try { modified = File.GetLastWriteTime(path); }
                    catch { continue; }

                    double ageDays = Math.Max(0, (DateTime.Now - modified).TotalDays);
                    double recency = 12.0 / (1.0 + ageDays);
                    score += recencyAsked ? recency * 3.0 : recency;

                    if (score <= 0) continue;

                    matches.Add(new FileMatch
                    {
                        Path = path,
                        Name = name,
                        Modified = modified,
                        Score = score,
                        TermHits = hits,
                        Where = DescribeFolder(path)
                    });
                }
            }

            // Term hits outrank score so a genuine name match always beats a
            // merely-recent file: "laith screenshot" was ranking a same-day
            // IMG_7312.jpeg above the actual Screenshot .pngs on score alone.
            return new FileSearchResult
            {
                HadTerms = terms.Length > 0,
                Terms = terms,
                Matches = matches
                    .OrderByDescending(m => m.TermHits > 0)
                    .ThenByDescending(m => m.Score)
                    .ThenByDescending(m => m.Modified)
                    .Take(take)
                    .ToList()
            };
        }

        public void Open(string path)
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        // Depth-capped, noise-skipping enumeration that survives a folder it
        // cannot read (an unreadable directory anywhere under a root would
        // otherwise abort the whole search).
        private static IEnumerable<string> Enumerate(string dir, int depth, ref int scanned)
        {
            var results = new List<string>();
            if (depth > MaxDepth || scanned >= MaxFilesScanned) return results;

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { return results; }

            foreach (string f in files)
            {
                if (scanned >= MaxFilesScanned) return results;
                try
                {
                    var attrs = File.GetAttributes(f);
                    if ((attrs & FileAttributes.Hidden) != 0) continue;
                    if ((attrs & FileAttributes.System) != 0) continue;
                }
                catch { continue; }
                results.Add(f);
                scanned++;
            }

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { return results; }

            foreach (string sub in subs)
            {
                string leaf = Path.GetFileName(sub);
                if (SkipDirs.Contains(leaf)) continue;
                if (leaf.StartsWith(".", StringComparison.Ordinal)) continue;
                results.AddRange(Enumerate(sub, depth + 1, ref scanned));
            }

            return results;
        }

        private static string[] Tokenize(string description)
        {
            return description
                .Split(new[] { ' ', ',', '.', '!', '?', '"', '\'', ':', ';', '/', '\\', '(', ')', '-', '_' },
                       StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2 && !Filler.Contains(w) && !TypeWords.ContainsKey(w))
                .Select(w => w.ToLowerInvariant())
                .Distinct()
                .ToArray();
        }

        // The extensions a stated type implies, or null when no type was named.
        private static HashSet<string> ExtensionsFor(string description)
        {
            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in TypeWords)
            {
                if (ContainsWord(description, pair.Key))
                    foreach (string e in pair.Value) exts.Add(e);
            }

            // A bare extension spoken as a word ("the docx one").
            foreach (string word in description.Split(new[] { ' ', '.', ',' },
                                                      StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = "." + word.ToLowerInvariant();
                if (TypeWords.Values.Any(v => v.Contains(candidate))) exts.Add(candidate);
            }

            return exts.Count == 0 ? null : exts;
        }

        private static bool MentionsRecency(string description)
        {
            foreach (string w in new[] { "recent", "recently", "newest", "latest", "last",
                                         "yesterday", "today", "earlier", "just" })
                if (ContainsWord(description, w)) return true;
            return false;
        }

        private static bool ContainsWord(string haystack, string word)
        {
            int i = (haystack ?? string.Empty).IndexOf(word, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            bool leftOk = i == 0 || !char.IsLetter(haystack[i - 1]);
            int end = i + word.Length;
            bool rightOk = end >= haystack.Length || !char.IsLetter(haystack[end]);
            return leftOk && rightOk;
        }

        // "Downloads", "Desktop", "Documents / Invoices" — enough for a spoken
        // answer to be actionable without reading out a full path.
        private static string DescribeFolder(string path)
        {
            string dir = Path.GetDirectoryName(path) ?? string.Empty;
            foreach (string root in Roots)
            {
                if (!dir.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                string leaf = Path.GetFileName(root);
                string rest = dir.Substring(root.Length).Trim('\\');
                return string.IsNullOrEmpty(rest) ? leaf : leaf + " / " + rest.Replace("\\", " / ");
            }
            return Path.GetFileName(dir);
        }
    }
}
