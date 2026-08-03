using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Personal_Assistant.STTClient
{
    // Decides whether a transcript is the assistant hearing itself.
    //
    // Full duplex on speakers means Kokoro's output reaches the microphone, so
    // the listener happily endpoints and transcribes the assistant's own reply.
    // The energy gate in ContinuousListener stops most of that from CUTTING the
    // reply; this is the backstop that stops it from becoming a TURN — which is
    // the failure that actually spirals, because answering your own last
    // sentence produces another reply to hear, and so on.
    //
    // Text, not audio: we know exactly what was just spoken, so comparing words
    // is both cheaper and more reliable than trying to recognise the waveform.
    internal static class EchoGuard
    {
        // How much of the transcript has to come from the reply before we call it
        // an echo. Not 1.0 because the STT mangles a word or two of anything it
        // hears through a speaker — but close to it, because an echo is a
        // transcription of clean synthesised speech and comes back near-verbatim,
        // whereas a user correcting the assistant ("set a timer for TEN minutes"
        // after it confirmed five) reuses almost every word but one.
        //
        // Erring low is the expensive direction: a missed echo costs one spurious
        // reply, a false positive silently ignores something the user said.
        private const double ContainmentThreshold = 0.8;

        // A one- or two-word utterance is far more likely to be someone cutting
        // the assistant off than an echo fragment, so these words are never
        // treated as echo no matter what the assistant happened to be saying.
        private static readonly HashSet<string> BargeInWords =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "stop", "wait", "no", "nope", "cancel", "quiet", "shush", "enough",
                "shut", "pause", "hold", "hey", "laith", "nevermind", "49",
            };

        // Scored on content words when there are enough of them, because the
        // function words are what a command and the reply it follows have in
        // common. "Turn off the bedroom light" said right after "the bedroom
        // light is on now" shares three of five words — but only two of four
        // once `the`/`is` stop counting, and that gap is the whole difference
        // between answering the user and ignoring them.
        //
        // Deliberately excludes on/off/up/down/no: they're short and look like
        // filler, but in this app they carry the entire meaning of a command.
        private static readonly HashSet<string> StopWords =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "a", "an", "the", "and", "or", "but", "is", "are", "was", "were",
                "be", "been", "am", "it", "its", "this", "that", "these", "those",
                "i", "you", "your", "my", "me", "we", "he", "she", "they", "them",
                "of", "to", "in", "at", "for", "with", "from", "as", "by",
                "so", "if", "do", "does", "did", "will", "would", "can", "could",
                "there", "here", "then", "right", "now", "just", "well", "okay",
                "ok", "yeah", "yes", "s", "t", "ll", "re", "ve", "m", "d",
                // Clock debris. "11:03 PM" and what the mic hears back ("eleven
                // oh three P M") disagree about how the meridiem and the leading
                // zero are spelled, and none of it distinguishes anything.
                "oh", "am", "pm", "p", "o", "clock",
            };

        // Below this there aren't enough content words to judge on, so the
        // comparison falls back to every word.
        private const int MinContentWords = 2;

        // The second, independent test: a run of words repeated in the same
        // ORDER. Word overlap alone missed "Mohsin, your own personal assistant"
        // against "Hi! I'm L.A.I.T.H.49, your own personal assistant!" — one
        // garbled word dropped it to 0.75 — but four words in a row, verbatim,
        // is not something a user says by coincidence.
        //
        // Four, not three, because "the bedroom light" is a three-word run that
        // any light command shares with the reply that confirmed the last one.
        private const int MinEchoRun = 4;
        private const double RunCoverage = 0.6;

        // True if `transcript` looks like it was produced by the microphone
        // picking up `spoken` (what the assistant said during that utterance).
        //
        // Tried against both hyphen readings, because the STT and the reply text
        // disagree about where a word divides and one extra boundary breaks both
        // tests at once. Measured: the reply said "today", Parakeet wrote
        // "to-day", and "Mohsin I assist you to-day." scored run=3 overlap=0.33
        // against "How can I assist you today?" — an echo the assistant then
        // answered. Joined, it's a 4-word run.
        //
        // Both directions have to be covered, so neither reading can be the only
        // one: "It's twelve forty-two A. M." only matches "It's 12:42 AM" while
        // the hyphen still SPLITS, since the digits expand to two words.
        public static bool IsEcho(string transcript, string spoken)
        {
            if (IsEchoCore(Tokenize(transcript, false), Tokenize(spoken, false))) return true;

            // Costs nothing on text without hyphens: both readings are identical.
            if (!HasHyphen(transcript) && !HasHyphen(spoken)) return false;
            return IsEchoCore(Tokenize(transcript, true), Tokenize(spoken, true));
        }

        private static bool HasHyphen(string text)
        {
            return !string.IsNullOrEmpty(text) && text.IndexOf('-') >= 0;
        }

        private static bool IsEchoCore(List<string> heard, List<string> said)
        {
            if (heard.Count == 0) return false;
            if (said.Count == 0) return false;

            if (heard.Count >= 3)
            {
                // Either test is enough on its own: they fail on different
                // things. Overlap survives the STT mangling word order or
                // dropping a word; the run survives a garbled word that tanks
                // the overlap score.
                int run = LongestRun(heard, said);
                if (run >= MinEchoRun && (double)run / heard.Count >= RunCoverage)
                {
                    return true;
                }

                List<string> heardContent = ContentWords(heard);
                if (heardContent.Count >= MinContentWords)
                {
                    return Containment(heardContent, ContentWords(said)) >= ContainmentThreshold;
                }
                return Containment(heard, said) >= ContainmentThreshold;
            }

            // Short utterance: only an echo if every word came from the reply and
            // none of them is something you'd say to interrupt.
            if (Containment(heard, said) < 1.0) return false;
            foreach (string w in heard)
            {
                if (BargeInWords.Contains(w)) return false;
            }
            return true;
        }

        // Why IsEcho decided what it did, for the log. An utterance heard over
        // assistant audio and NOT dropped is the interesting case — that's the
        // one that becomes a spurious turn — so this makes the scores visible
        // rather than leaving the next escape to be reasoned about from the
        // transcript alone.
        public static string Describe(string transcript, string spoken)
        {
            string best = Score(Tokenize(transcript, false), Tokenize(spoken, false));
            if (!HasHyphen(transcript) && !HasHyphen(spoken)) return best;

            // Report whichever hyphen reading came closest to calling it an
            // echo, since that's the one IsEcho would have acted on.
            string joined = Score(Tokenize(transcript, true), Tokenize(spoken, true));
            return string.CompareOrdinal(joined, best) > 0 ? joined + " (hyphens joined)" : best;
        }

        private static string Score(List<string> heard, List<string> said)
        {
            if (heard.Count == 0 || said.Count == 0)
            {
                return $"words={heard.Count} reference={said.Count}";
            }

            List<string> heardContent = ContentWords(heard);
            double overlap = heardContent.Count >= MinContentWords
                ? Containment(heardContent, ContentWords(said))
                : Containment(heard, said);

            return $"run={LongestRun(heard, said)}/{heard.Count} overlap={overlap:F2}"
                 + $" (need run>={MinEchoRun} or overlap>={ContainmentThreshold:F2})";
        }

        // Fraction of `heard` explained by `said`. Multiset, so each spoken word
        // only accounts for one heard word — "the the the" isn't explained by a
        // single "the".
        private static double Containment(List<string> heard, List<string> said)
        {
            if (heard.Count == 0) return 0.0;

            var bag = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string w in said)
            {
                int n;
                bag.TryGetValue(w, out n);
                bag[w] = n + 1;
            }

            int matched = 0;
            foreach (string w in heard)
            {
                int n;
                if (bag.TryGetValue(w, out n) && n > 0)
                {
                    bag[w] = n - 1;
                    matched++;
                }
            }
            return (double)matched / heard.Count;
        }

        // Longest run of tokens appearing consecutively in both, i.e. the
        // classic longest-common-substring DP over words. Both sides are short
        // (one utterance against one reply), so the table costs nothing.
        private static int LongestRun(List<string> heard, List<string> said)
        {
            int best = 0;
            var prev = new int[said.Count + 1];
            var cur = new int[said.Count + 1];

            for (int i = 1; i <= heard.Count; i++)
            {
                for (int j = 1; j <= said.Count; j++)
                {
                    cur[j] = heard[i - 1] == said[j - 1] ? prev[j - 1] + 1 : 0;
                    if (cur[j] > best) best = cur[j];
                }
                var swap = prev; prev = cur; cur = swap;
                Array.Clear(cur, 0, cur.Length);
            }
            return best;
        }

        private static List<string> ContentWords(List<string> tokens)
        {
            var kept = new List<string>();
            foreach (string w in tokens)
            {
                if (!StopWords.Contains(w)) kept.Add(w);
            }
            return kept;
        }

        // Lowercase word-ish tokens, with digits expanded into the words they're
        // spoken as. Punctuation and emoji fall out on their own, which matters
        // because the bubble text carries emoji the voice never spoke and the
        // STT never heard.
        //
        // The digit expansion is the point. Numbers reach the two sides of this
        // comparison in different alphabets: the reply text says "11:03 PM" and
        // "23 degrees", while Kokoro pronounces them and the STT writes back
        // "eleven oh three" and "twenty three". Compared as written, every
        // numeric tool — the clock, timers, the thermometer, prayer times — was
        // unprotected: "It's eleven." scored 0.50 against "It's 11:03 PM" and
        // became a turn the assistant then answered.
        private static List<string> Tokenize(string text, bool joinHyphens)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(text)) return tokens;

            var current = new StringBuilder();
            bool currentIsDigits = false;

            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c))
                {
                    bool isDigit = char.IsDigit(c);
                    // A letter/digit boundary ends the token: "49th" is "forty
                    // nine" + "th", not one unmatchable blob.
                    if (current.Length > 0 && isDigit != currentIsDigits)
                    {
                        Flush(tokens, current, currentIsDigits);
                    }
                    currentIsDigits = isDigit;
                    current.Append(char.ToLower(c, CultureInfo.InvariantCulture));
                }
                else if (c == '\'' || (joinHyphens && c == '-'))
                {
                    // Keep contractions whole: "it's" shouldn't split into a
                    // spurious one-letter token that matches everything. Under
                    // `joinHyphens` a hyphen is swallowed the same way, so
                    // "to-day" reads as "today".
                    continue;
                }
                else if (current.Length > 0)
                {
                    Flush(tokens, current, currentIsDigits);
                }
            }
            if (current.Length > 0) Flush(tokens, current, currentIsDigits);
            return tokens;
        }

        private static void Flush(List<string> tokens, StringBuilder buffer, bool isDigits)
        {
            string token = buffer.ToString();
            buffer.Length = 0;
            if (isDigits) AppendNumber(tokens, token);
            else tokens.Add(token);
        }

        private static readonly string[] Ones =
        {
            "zero", "one", "two", "three", "four", "five", "six", "seven", "eight",
            "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen",
            "sixteen", "seventeen", "eighteen", "nineteen",
        };

        private static readonly string[] Tens =
        {
            "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy",
            "eighty", "ninety",
        };

        private static void AppendNumber(List<string> tokens, string digits)
        {
            // Long runs are read out digit by digit — phone numbers, PINs —
            // rather than as one quantity.
            if (digits.Length > 4)
            {
                foreach (char d in digits) tokens.Add(Ones[d - '0']);
                return;
            }

            // A leading zero means it's a sequence, not a quantity: the "03" in
            // a clock time is "oh three", never "three".
            if (digits.Length > 1 && digits[0] == '0')
            {
                foreach (char d in digits) tokens.Add(d == '0' ? "oh" : Ones[d - '0']);
                return;
            }

            int value;
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            {
                tokens.Add(digits);
                return;
            }
            AppendValue(tokens, value);
        }

        private static void AppendValue(List<string> tokens, int value)
        {
            if (value >= 1000)
            {
                AppendValue(tokens, value / 1000);
                tokens.Add("thousand");
                value %= 1000;
                if (value == 0) return;
            }
            if (value >= 100)
            {
                tokens.Add(Ones[value / 100]);
                tokens.Add("hundred");
                value %= 100;
                if (value == 0) return;
            }
            if (value >= 20)
            {
                tokens.Add(Tens[value / 10]);
                value %= 10;
                if (value == 0) return;
            }
            tokens.Add(Ones[value]);
        }
    }
}