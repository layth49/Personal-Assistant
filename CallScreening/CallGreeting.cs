using Personal_Assistant.Configuration;
using Personal_Assistant.Live;
using Personal_Assistant.VoiceClips;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Personal_Assistant.CallScreening
{
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
    /// IT STILL CANNOT BE RENDERED ON DEMAND. The greeting exists to cover the
    /// Live session's connect handshake, so it has to start playing immediately;
    /// a render costs the better part of ten seconds (measured: 7.8s for one
    /// line), which is most of the twenty a caller will wait. So named greetings
    /// are PRE-RENDERED into the same clip cache the wake greeting uses, and a
    /// name with no cached clip quietly falls back to the stock file. The cache
    /// is filled two ways: `--render-clips` does every contact up front, and a
    /// call from a name with no clip renders one afterwards, so the second call
    /// from anyone is greeted properly even if the first was not.
    /// </para>
    /// <para>
    /// The clip is keyed on the CALL voice, not the assistant's. Those are
    /// allowed to differ (CallVoice), and greeting a caller in one voice before
    /// answering them in another is worse than not using their name at all.
    /// </para>
    /// </remarks>
    public static class CallGreeting
    {
        /// <summary>
        /// The voice a screened call speaks in. THE ONE ACCESSOR for it, the way
        /// LiveSessionOptions.ConfiguredVoice is for the assistant's — a second
        /// copy of this expression that drifted would render every greeting clip
        /// under a key nothing ever looks up, and the fallback would hide it.
        /// </summary>
        public static string Voice =>
            LaithConfig.Text("CallVoice", LiveSessionOptions.ConfiguredVoice);

        /// <summary>
        /// The named greeting, with <c>{name}</c> where the caller's name goes.
        /// </summary>
        /// <remarks>
        /// Configurable because the stock greeting.wav was recorded to wording
        /// Layth chose, and this has to sit beside it without contradicting it.
        /// Changing it invalidates every cached clip — they are keyed by text —
        /// so it needs a re-run of --render-clips, exactly like changing the
        /// voice does.
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

            return VoiceClipCache.TryGet(voice, line, out string path) ? path : null;
        }

        /// <summary>True when this caller deserves a clip and has not got one yet.</summary>
        public static bool NeedsRender(string caller) =>
            LineFor(caller) != null && ClipFor(caller) == null && !string.IsNullOrEmpty(Voice);

        /// <summary>
        /// Named greetings for a set of contacts, for --render-clips to render up
        /// front. Deduplicated, because two contacts sharing a first name share a
        /// greeting and rendering it twice is a wasted round trip.
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
        /// one clip per human being. “Rayy🦇” reaches this table because the
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
            // letters aloud, so the spelling is the pronunciation — these three
            // point at clips that already existed under the older spelling.
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

            // "Ibraheem’s Mom" describes someone by their relation to somebody
            // else, so the first word of it is a possessive rather than a name
            // and "Hi Ibraheem’s" is worse than the stock recording. Only the
            // possessive ENDING is refused, so an O'Brien is still greeted.
            if (first.EndsWith("'s", StringComparison.OrdinalIgnoreCase) ||
                first.EndsWith("’s", StringComparison.OrdinalIgnoreCase))
                return null;

            // An emoji in the display name would be handed to the renderer as
            // text and spoken as whatever the model makes of it. Stripped
            // rather than refused, because it is decoration ON a name rather
            // than part of one: “Rayy🦇” is still Rayy.
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
