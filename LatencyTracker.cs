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
    //
    // It also carries the Live session accounting, because the alternative — a
    // second, parallel "SessionTracker" — would leave the two halves of the same
    // question (what does a turn cost, what does a conversation cost) printed by
    // two mechanisms with two lifetimes.
    public sealed class LatencyTracker
    {
        private readonly object gate = new object();
        private TimeSpan stt;
        private TimeSpan llm;
        private TimeSpan tts;

        // ---- session accounting -------------------------------------------
        //
        // Google does not publish the free-tier Live API quota — the rate-limit
        // page, the pricing page and the Firebase limits page all defer to the
        // per-project AI Studio dashboard. So there is no number to check against
        // and nothing here tries to enforce one. What these counters are for is
        // MEASURING real use, so the ceiling gets discovered from data rather
        // than guessed at, and so the one failure mode that can actually exhaust
        // the tier — a session stuck open streaming silence — is visible at a
        // glance: every open has a matching close line, so a session that never
        // closed shows up as an open with no close and a cumulative uploaded
        // total that keeps climbing while nobody is talking.
        private readonly object sessionGate = new object();
        private readonly DateTime processStartLocal = DateTime.Now;
        private int sessionCount;
        private TimeSpan sessionDuration;
        private double uploadedSeconds;
        private double receivedSeconds;
        private int sessionToolCalls;

        public void Reset()
        {
            lock (gate) { stt = TimeSpan.Zero; llm = TimeSpan.Zero; tts = TimeSpan.Zero; }
        }

        public void RecordStt(TimeSpan elapsed) { lock (gate) { stt += Clamp(elapsed); } }
        public void RecordLlm(TimeSpan elapsed) { lock (gate) { llm += Clamp(elapsed); } }
        public void RecordTts(TimeSpan elapsed) { lock (gate) { tts += Clamp(elapsed); } }

        private static TimeSpan Clamp(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

        // e.g. "[latency] stt=180ms llm=850ms tts=620ms -- slowest: llm"
        public string Summary()
        {
            TimeSpan s, l, t;
            lock (gate) { s = stt; l = llm; t = tts; }

            string slowest = "stt";
            TimeSpan max = s;
            if (l > max) { max = l; slowest = "llm"; }
            if (t > max) { max = t; slowest = "tts"; }

            return $"[latency] stt={s.TotalMilliseconds:F0}ms llm={l.TotalMilliseconds:F0}ms " +
                   $"tts={t.TotalMilliseconds:F0}ms -- slowest: {slowest}";
        }

        // One finished Live conversation. Called from LiveSession's close path,
        // which is the single place that runs on every exit — clean end,
        // cancellation, hard cap and mid-turn exception alike — so a session
        // cannot end without being counted.
        //
        // A handshake that never opened is still recorded, with a near-zero
        // duration. Those are the sessions that trip the fallback, and a run
        // full of them is exactly what the totals should make obvious.
        public void RecordSession(
            DateTime openedLocal,
            DateTime closedLocal,
            TimeSpan duration,
            double uploadedAudioSeconds,
            double receivedAudioSeconds,
            int toolCalls)
        {
            lock (sessionGate)
            {
                sessionCount++;
                sessionDuration += Clamp(duration);
                uploadedSeconds += ClampSeconds(uploadedAudioSeconds);
                receivedSeconds += ClampSeconds(receivedAudioSeconds);
                sessionToolCalls += toolCalls < 0 ? 0 : toolCalls;
            }
        }

        private static double ClampSeconds(double s) =>
            double.IsNaN(s) || double.IsInfinity(s) || s < 0 ? 0 : s;

        // The per-session half of the close log. Wall-clock timestamps rather
        // than a duration alone: a duration tells you a session was long, a
        // start time tells you WHICH session was still open when the assistant
        // stopped responding, which is the question actually being asked when
        // somebody goes looking for a stuck one.
        //
        // e.g. "[session] #3 02:41:07->02:41:49 duration=42.1s turns=3 tools=1
        //       up=0.35min down=0.21min outcome=Idle"
        public static string SessionSummary(
            int index,
            DateTime openedLocal,
            DateTime closedLocal,
            TimeSpan duration,
            int turns,
            int toolCalls,
            double uploadedAudioSeconds,
            double receivedAudioSeconds,
            string outcome)
        {
            return $"[session] #{index} {openedLocal:HH:mm:ss}->{closedLocal:HH:mm:ss} " +
                   $"duration={duration.TotalSeconds:F1}s turns={turns} tools={toolCalls} " +
                   $"up={uploadedAudioSeconds / 60.0:F2}min down={receivedAudioSeconds / 60.0:F2}min " +
                   $"outcome={outcome}";
        }

        /// <summary>How many Live conversations have been recorded this process run.</summary>
        public int SessionCount { get { lock (sessionGate) { return sessionCount; } } }

        // The cumulative half. Audio is in MINUTES because that is the unit the
        // quota question is asked in, and because seconds stop being readable
        // once a day's use is in them.
        //
        // "open" is wall-clock time since the process started, so the ratio of
        // session time to open time answers "is something holding a socket while
        // nobody is here" without needing to read the individual lines.
        //
        // e.g. "[session] totals -- sessions=3 duration=2.10min up=0.94min
        //       down=0.55min tools=4 (process open 41.3min)"
        public string SessionTotals()
        {
            int count, tools;
            TimeSpan duration;
            double up, down;
            lock (sessionGate)
            {
                count = sessionCount;
                duration = sessionDuration;
                up = uploadedSeconds;
                down = receivedSeconds;
                tools = sessionToolCalls;
            }

            double openMinutes = (DateTime.Now - processStartLocal).TotalMinutes;
            return $"[session] totals -- sessions={count} duration={duration.TotalMinutes:F2}min " +
                   $"up={up / 60.0:F2}min down={down / 60.0:F2}min tools={tools} " +
                   $"(process open {openMinutes:F1}min)";
        }
    }
}
