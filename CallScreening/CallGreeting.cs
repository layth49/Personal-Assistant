using Personal_Assistant.Configuration;
using Personal_Assistant.TTSClient;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Personal_Assistant.CallScreening
{
    /// <summary>
    /// Named greeting clips on disk, keyed by voice AND text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Upstream this is <c>VoiceClipCache</c>, which exists because every spoken
    /// line on that branch has to be rendered through the Live API to stay in the
    /// session's voice. Nothing like that is needed here — Kokoro synthesises
    /// everything the assistant says, on demand, in the configured voice. This
    /// cache exists for exactly one reason: the greeting has to start playing the
    /// moment the line is answered, and synthesis is not instant.
    /// </para>
    /// <para>
    /// KEYED BY VOICE AND TEXT, both. Changing either must MISS. A greeting cached
    /// under the old wording or the old voice would otherwise keep playing after
    /// the template was reworded or <c>KOKORO_VOICE</c> changed — and because the
    /// miss path falls back silently to the stock recording, a key that never
    /// matches looks identical to a contact who simply has no clip. So the voice is
    /// a directory and the text is a hash of the sentence, and a change to either
    /// invalidates every clip it should.
    /// </para>
    /// <para>
    /// Its own folder, <c>callclips</c>, next to the exe like <c>keyword.table</c>
    /// so it survives both the bin\Debug and the deploy-folder layouts.
    /// </para>
    /// </remarks>
    public static class CallClipCache
    {
        public static string Root =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "callclips");

        // Hashed rather than named after the sentence, because these contain
        // punctuation that is not path-legal.
        public static string PathFor(string voice, string text)
        {
            if (string.IsNullOrEmpty(voice) || string.IsNullOrEmpty(text)) return null;

            using (var sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text.Trim()));
                var name = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) name.Append(b.ToString("x2"));
                return Path.Combine(Root, Safe(voice), name.ToString() + ".wav");
            }
        }

        // A Kokoro voice id can be a blend expression ("af_heart+af_bella"), and
        // '+' is legal in a path while other things are not. Folded rather than
        // rejected: an unusable directory name would make every clip a miss.
        private static string Safe(string voice)
        {
            var clean = new StringBuilder(voice.Length);
            foreach (char c in voice)
                clean.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return clean.ToString();
        }

        public static bool TryGet(string voice, string text, out string path)
        {
            path = PathFor(voice, text);
            // A truncated render — header only, or an interrupted write — is worse
            // than a miss, because it plays as silence down a phone line and looks
            // from the caller's end exactly like nobody being there.
            return path != null && File.Exists(path) && new FileInfo(path).Length > 1024;
        }

        /// <summary>
        /// Renders one line through Kokoro and caches it. False on any failure —
        /// the cost of which is one call greeted by the stock recording.
        /// </summary>
        public static async Task<bool> TryRenderAsync(string voice, string text)
        {
            string path = PathFor(voice, text);
            if (path == null) return false;

            try
            {
                var sw = Stopwatch.StartNew();

                // Through SynthesizeWavAsync, which routes through the same
                // RequestWavAsync everything else does — so StripUnspeakable runs
                // and the RIFF sizes are fixed. A clip that NAudio cannot open is
                // a clip that plays as an exception mid-answer.
                byte[] wav = await KokoroTTSService.SynthesizeWavAsync(text, voice)
                    .ConfigureAwait(false);
                if (wav == null || wav.Length <= 1024)
                {
                    Console.WriteLine($"[callclips] nothing came back for \"{Preview(text)}\"");
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path));

                // Written beside the target and moved into place, so a crash
                // mid-write can never leave a half-file TryGet would treat as a hit.
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temp, wav);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);

                Console.WriteLine($"[callclips] rendered in {sw.ElapsedMilliseconds}ms: {Preview(text)}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[callclips] could not render \"{Preview(text)}\": {ex.Message}");
                return false;
            }
        }

        /// <summary>Renders a set of lines, skipping the ones already cached.</summary>
        public static async Task<int> RenderAsync(IReadOnlyList<string> lines, string voice)
        {
            if (string.IsNullOrEmpty(voice))
            {
                Console.WriteLine("[callclips] no voice configured — nothing to render");
                return 0;
            }

            int written = 0, skipped = 0, failed = 0;
            Console.WriteLine($"[callclips] rendering {lines.Count} line(s) as '{voice}' into {Root}");

            foreach (string line in lines)
            {
                if (TryGet(voice, line, out _)) { skipped++; continue; }
                if (await TryRenderAsync(voice, line).ConfigureAwait(false)) written++;
                else failed++;
            }

            Console.WriteLine($"[callclips] wrote {written}, skipped {skipped}, failed {failed}");
            return failed;
        }

        private static string Preview(string s) =>
            s == null ? "(null)" : (s.Length <= 60 ? s : s.Substring(0, 60) + "...");
    }

    /// <summary>
    /// The first thing a screened caller hears — by name, when we know it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY BY NAME. The stock greeting is one fixed WAV, and a fixed WAV is
    /// indistinguishable from an answering machine: the caller assumes they are
    /// leaving a voicemail and talks accordingly, or hangs up. Hearing their own
    /// name in the first sentence says two things at once — this is live, and it
    /// understood who you are — which is the difference between a caller who
    /// answers questions and one who waits for a beep.
    /// </para>
    /// <para>
    /// IT IS STILL PRE-RENDERED. Upstream that was because a Live-API render costs
    /// the better part of ten seconds. Kokoro is far quicker than that, but the
    /// greeting is the very first thing on the line and it has to start
    /// IMMEDIATELY — every hundred milliseconds here is a caller listening to
    /// silence on a call that has just been answered, which is the exact thing the
    /// greeting exists to prevent. So named greetings come off disk, and a name
    /// with no cached clip quietly falls back to the stock file. The cache is
    /// filled after the call, so the second call from anyone is greeted properly
    /// even if the first was not.
    /// </para>
    /// <para>
    /// The clip is keyed on the CALL voice, not the assistant's. Those are allowed
    /// to differ (CallVoice), and greeting a caller in one voice before answering
    /// them in another is worse than not using their name at all.
    /// </para>
    /// </remarks>
    public static class CallGreeting
    {
        /// <summary>
        /// The voice a screened call speaks in. THE ONE ACCESSOR for it — a second
        /// copy of this expression that drifted would render every greeting clip
        /// under a key nothing ever looks up, and the stock-WAV fallback would hide
        /// it. <see cref="CallSession"/> reads the conversation's voice from here
        /// for the same reason.
        /// </summary>
        /// <remarks>
        /// Defaults to whatever the assistant itself speaks in, through
        /// <c>KokoroTTSService.ConfiguredVoice</c> rather than a second reading of
        /// KOKORO_VOICE — that setting has an env override and a fallback chain,
        /// and re-deriving it here is precisely how the two get out of step.
        /// </remarks>
        public static string Voice =>
            LaithConfig.Text("CallVoice", KokoroTTSService.ConfiguredVoice);

        /// <summary>
        /// The named greeting, with <c>{name}</c> where the caller's name goes.
        /// </summary>
        /// <remarks>
        /// Configurable because the stock greeting.wav was recorded to wording
        /// Layth chose, and this has to sit beside it without contradicting it.
        /// Changing it invalidates every cached clip — they are keyed by text —
        /// exactly as changing the voice does.
        /// </remarks>
        private static string Template => LaithConfig.Text(
            "CallGreetingNamed",
            "Hi {name}, you've reached Layth's assistant. He can't get to the " +
            "phone right now, but I can take a message.");

        /// <summary>
        /// The greeting for this caller, or null when there is no usable name and
        /// the stock recording should play instead.
        /// </summary>
        public static string LineFor(string caller)
        {
            string name = SpeakableName(caller);
            return name == null ? null : Template.Replace("{name}", name);
        }

        /// <summary>
        /// A pre-rendered named greeting for this caller, or null — in which case
        /// the caller hears the stock WAV.
        /// </summary>
        public static string ClipFor(string caller)
        {
            string line = LineFor(caller);
            if (line == null) return null;

            string voice = Voice;
            if (string.IsNullOrEmpty(voice)) return null;

            return CallClipCache.TryGet(voice, line, out string path) ? path : null;
        }

        /// <summary>True when this caller deserves a clip and has not got one yet.</summary>
        public static bool NeedsRender(string caller) =>
            LineFor(caller) != null && ClipFor(caller) == null && !string.IsNullOrEmpty(Voice);

        /// <summary>
        /// Named greetings for a set of contacts, to be rendered up front.
        /// Deduplicated, because two contacts sharing a first name share a greeting
        /// and rendering it twice is a wasted round trip.
        /// </summary>
        public static IReadOnlyList<string> LinesFor(IEnumerable<string> callers)
        {
            if (callers == null) return Array.Empty<string>();

            return callers
                .Select(LineFor)
                .Where(l => l != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Relationship words that sit where a first name would.
        /// </summary>
        /// <remarks>
        /// Much of this address book is filed by relation — "عمي Bashar",
        /// "Khalte Gadir", "Sitee Monera" — and the transport reports Google's
        /// display name VERBATIM, so the first word of it is a title rather than
        /// a name. Taking it literally greets four uncles as "Hi عمي" and, worse,
        /// looks up a clip nothing ever rendered, so they drop to the stock
        /// recording with nothing in the log to say why.
        /// </remarks>
        private static readonly HashSet<string> Titles = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "عمي", "عمتي", "خالي", "خالتي", "جدي", "جدتي",
            "Amo", "Amto", "Amtee", "Abu", "Khalee", "Khalte", "Sitee", "Sido",
        };

        /// <summary>
        /// Contacts whose whole name is a relationship word, and what to say
        /// instead. Layth's parents are filed in Arabic with no given name.
        /// </summary>
        private static readonly Dictionary<string, string> TitleOnly =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "أبوي", "Dad" },
            { "أمي", "Mom" },
        };

        /// <summary>
        /// Google's spelling of a name on the left, how it should be SAID on
        /// the right.
        /// </summary>
        /// <remarks>
        /// Not cosmetic — the clip cache is keyed on the exact text, so two
        /// spellings of one person are two renders, two cache entries, and two
        /// chances for one of them to be the stale one. Folding them here means
        /// one clip per human being. "Rayy🦇" reaches this table because the
        /// emoji is stripped above; both Mohameds are the same name as the
        /// Mohammad already rendered; Chanc is a truncated Chance.
        /// </remarks>
        private static readonly Dictionary<string, string> Aliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Mohamed", "Mohammad" },
            { "Rayy", "Rayvon" },
            { "Chanc", "Chance" },

            // Transliterations, where Google's spelling and the one that gets
            // SAID better are both defensible. The renderer is reading English
            // letters aloud, so the spelling is the pronunciation.
            { "Intisar", "Intasar" },
            { "Moshahel", "Mashahel" },
            { "Ibraheem", "Ibrahim" },
            { "Abduallah", "Abdullah" },
        };

        /// <summary>
        /// The part of a caller's name worth saying out loud, or null.
        /// </summary>
        /// <remarks>
        /// FIRST NAME ONLY. "Hi Bara Hammad" is how a cold-calling machine talks;
        /// "Hi Bara" is how a person does, and sounding like a person is the
        /// entire point of the feature.
        ///
        /// Null for anything that is not really a name — the transport reports an
        /// unrecognised caller as "an unknown number", and a caller ID that came
        /// through as bare digits would otherwise be read out one at a time as a
        /// greeting. Both should get the stock recording instead.
        /// </remarks>
        public static string SpeakableName(string caller)
        {
            if (string.IsNullOrWhiteSpace(caller)) return null;

            string trimmed = caller.Trim();

            // What GoogleVoiceCallTransport reports when the live region carried
            // no name. It is a description, not a name.
            if (trimmed.StartsWith("an unknown", StringComparison.OrdinalIgnoreCase))
                return null;

            // Filed as nothing but a relationship word, with no given name to
            // fall back to. Both parents are stored this way, so without the
            // map the two likeliest callers of all get the stock recording.
            if (TitleOnly.TryGetValue(trimmed, out string mapped)) return mapped;

            string[] words = trimmed.Split(new[] { ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries);

            // Skip a leading relationship word, so "Khalte Gadir" greets Gadir.
            // Exactly one, and never the last word standing: a contact filed as
            // only "Abu" is a title with nobody behind it, and greeting them
            // "Hi Abu" is the same mistake as "Hi عمي".
            int at = Titles.Contains(words[0]) ? 1 : 0;
            if (at >= words.Length) return null;
            string first = words[at];

            // "Ibraheem's Mom" describes someone by their relation to somebody
            // else, so the first word of it is a possessive rather than a name
            // and "Hi Ibraheem's" is worse than the stock recording. Only the
            // possessive ENDING is refused, so an O'Brien is still greeted.
            if (first.EndsWith("'s", StringComparison.OrdinalIgnoreCase) ||
                first.EndsWith("’s", StringComparison.OrdinalIgnoreCase))
                return null;

            // An emoji in the display name would be handed to the renderer as
            // text. Kokoro reads one out loud as its CLDR name, so "Rayy🦇"
            // becomes "Rayy bat" down a phone line. Stripped rather than refused,
            // because it is decoration ON a name rather than part of one.
            first = new string(first.Where(c => !char.IsSurrogate(c) &&
                CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.OtherSymbol)
                .ToArray());

            // Must actually contain letters. A phone number, a name rendered as
            // digits by the caller-ID, or a contact filed as nothing but an
            // emoji is not something to greet by.
            if (!first.Any(char.IsLetter)) return null;

            // How Layth says the name, when that is not how Google spells it.
            if (Aliases.TryGetValue(first, out string preferred)) first = preferred;

            // Long enough to be a name, short enough not to be a sentence that
            // wandered in from a live region.
            return first.Length >= 2 && first.Length <= 24 ? first : null;
        }
    }
}
