using Personal_Assistant.Configuration;
using Personal_Assistant.Live;
using Personal_Assistant.VoiceClips;
using System;
using System.Collections.Generic;
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

            string first = trimmed.Split(new[] { ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrEmpty(first)) return null;

            // Must actually contain letters. A phone number, or a name rendered
            // as digits by the caller-ID, is not something to greet by.
            if (!first.Any(char.IsLetter)) return null;

            // Long enough to be a name, short enough not to be a sentence that
            // wandered in from a live region.
            return first.Length >= 2 && first.Length <= 24 ? first : null;
        }
    }
}
