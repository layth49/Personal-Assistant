using System;

namespace Personal_Assistant.Diagnostics
{
    // Per-turn latency breakdown: STT (understanding only — excludes however
    // long the user spent talking / recording silence), LLM (all model calls
    // made for the turn: intent-detection plus, on a miss, the conversational
    // call), and TTS (synthesis only — excludes audio playback time, which
    // scales with reply length rather than being a bottleneck).
    //
    // A single instance is shared across SpeechService and IntentDispatcher for
    // the lifetime of the app; Program.cs calls Reset() right before each
    // recognition attempt and prints Summary() after the turn completes. TTS can
    // be recorded multiple times per turn (a multi-tool dispatch speaks several
    // confirmations), so it accumulates; LLM also accumulates for the same
    // reason (detect call + a possible conversational fallback call).
    public sealed class LatencyTracker
    {
        private readonly object gate = new object();
        private TimeSpan stt;
        private TimeSpan llm;
        private TimeSpan tts;
        private TimeSpan? firstAudio;

        public void Reset()
        {
            lock (gate) { stt = TimeSpan.Zero; llm = TimeSpan.Zero; tts = TimeSpan.Zero; firstAudio = null; }
        }

        public void RecordStt(TimeSpan elapsed) { lock (gate) { stt += Clamp(elapsed); } }
        public void RecordLlm(TimeSpan elapsed) { lock (gate) { llm += Clamp(elapsed); } }
        public void RecordTts(TimeSpan elapsed) { lock (gate) { tts += Clamp(elapsed); } }

        // Time from starting the reply to the first sound leaving the speakers —
        // what the user actually experiences as "how long until it answers".
        // Only the streamed reply path reports it; the first one wins, since a
        // turn's perceived latency is set by its first audio, not its last.
        public void RecordFirstAudio(TimeSpan elapsed)
        {
            lock (gate) { if (firstAudio == null) firstAudio = Clamp(elapsed); }
        }

        private static TimeSpan Clamp(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

        // e.g. "[latency] stt=180ms llm=850ms tts=620ms first-audio=610ms -- slowest: llm"
        public string Summary()
        {
            TimeSpan s, l, t;
            TimeSpan? fa;
            lock (gate) { s = stt; l = llm; t = tts; fa = firstAudio; }

            string slowest = "stt";
            TimeSpan max = s;
            if (l > max) { max = l; slowest = "llm"; }
            if (t > max) { max = t; slowest = "tts"; }

            string firstAudioPart = fa.HasValue
                ? $"first-audio={fa.Value.TotalMilliseconds:F0}ms "
                : string.Empty;

            return $"[latency] stt={s.TotalMilliseconds:F0}ms llm={l.TotalMilliseconds:F0}ms " +
                   $"tts={t.TotalMilliseconds:F0}ms {firstAudioPart}-- slowest: {slowest}";
        }
    }
}
