using NAudio.Wave;
using Python.Runtime;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Personal_Assistant.STTClient
{
    // Owns the microphone for the life of the app and turns it into a stream of
    // events, so the assistant can listen and speak at the same time.
    //
    // One WaveInEvent feeds Silero VAD (in Python — see VoiceActivity.py) frame
    // by frame. Two things come out:
    //   SpeechOnset    - the user has started talking. Raised within ~100ms, and
    //                    it's what makes speak-to-interrupt barge-in possible.
    //   UtteranceReady - a complete utterance was endpointed and transcribed.
    //
    // Replaces the old one-shot RecognizeOnceAsync capture, which opened the mic
    // per call and therefore couldn't hear anything while the assistant spoke.
    public sealed class ContinuousListener : IDisposable
    {
        private static readonly WaveFormat CaptureFormat = new WaveFormat(16000, 16, 1);

        // Small buffers keep onset latency low; each is ~1 VAD frame.
        private const int BufferMs = 30;

        // Hysteresis: it takes a clear signal to declare speech, but a weaker one
        // to keep it going, so ordinary dips mid-word don't end the utterance.
        private const double OnsetThreshold = 0.5;
        private const double SustainThreshold = 0.35;

        // Consecutive voiced frames needed before we believe it. Two frames is
        // ~64ms — enough to reject a click, fast enough for barge-in.
        private const int OnsetFrames = 2;

        // Endpointing. The old RMS gate waited 1500ms; the VAD is confident
        // enough to halve that, which takes a chunk out of every turn.
        //
        // These are measured in SAMPLES, not wall-clock: the clock that matters is
        // how much audio has gone by. A GC pause or a slow frame would stretch a
        // wall-clock timer and cut the user off mid-sentence; a sample count can't
        // drift from the audio it describes.
        // 800ms, measured rather than guessed. `bakeoff/stt/tail_sweep.py` re-cuts
        // the bake-off corpus to an exact tail length and scores each setting;
        // against the Parakeet service:
        //
        //     tail    WER    critWER   keyword recall
        //     1000   16.4     13.7       75.4
        //      800   17.3     14.3       75.4
        //      600   19.1     16.1       73.7
        //      400   19.5     17.9       71.9
        //
        // 800 costs about half a point of WER for 200ms off every single turn,
        // which is the trade this whole project exists to make. Below 800 it
        // falls off properly and the tail stops being worth cutting.
        //
        // (The 1000 here previously came from the belief that Whisper degrades
        // on hard-cropped clips. The same sweep says Whisper is flat across all
        // five settings — 18.6% at both 1000 and 200 — so that was never the
        // reason. Parakeet is the engine that actually cares.)
        private const int TrailingSilenceSamples = 16000 * 800 / 1000;
        // Chunk size for a long utterance, NOT the length of a turn. At the cap
        // the audio so far is sent for transcription and recording continues;
        // only silence ends a turn. Bounded by MaxContinuationSegments so a
        // stuck-open microphone can't buffer for ever.
        private const int MaxUtteranceSamples = 16000 * 20;
        private const int MaxContinuationSegments = 8;   // ~3 minutes of talking
        // Ignore blips: a cough or a door is not a turn.
        private const int MinSpeechSamples = 16000 * 200 / 1000;

        // Audio kept from before the onset was confirmed, so the first syllable
        // isn't clipped off the front of the utterance. Generous on purpose —
        // leading silence costs Whisper nothing, but a clipped first word costs
        // the whole transcription.
        private static readonly TimeSpan PreRoll = TimeSpan.FromMilliseconds(500);

        // How often the conversation-window wait re-checks whether the user is
        // mid-utterance, and how long it extends by when they are.
        private static readonly TimeSpan PollSlice = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan BusyGrace = TimeSpan.FromSeconds(5);

        // ── Echo suppression ────────────────────────────────────────────────
        // On speakers, Kokoro's output reaches this microphone, so without help
        // the assistant hears itself: the VAD fires, barge-in cuts the reply,
        // the echo gets transcribed, and answering it produces more audio to
        // hear. Two independent layers, because neither is sufficient alone:
        //
        //  1. An energy gate here — while the assistant is audible, an onset
        //     only counts as barge-in if it's clearly louder than the bleed.
        //     Cheap, immediate, and it's what keeps the reply from being cut.
        //  2. A text gate (EchoGuard) applied to the finished transcript, which
        //     is what guarantees an echo never becomes a turn.
        //
        // Layer 1 gates the SpeechOnset EVENT only — the utterance is still
        // captured and transcribed, so a genuine barge-in the energy gate was
        // too strict about is answered late rather than lost.
        //
        // LAITH_ECHO_GATE: auto (default) | on | off.
        //   auto  — gate only once bleed has actually been observed, so a
        //           headset keeps instant barge-in and never pays for this.
        private static readonly string EchoGateMode =
            (Environment.GetEnvironmentVariable("LAITH_ECHO_GATE") ?? "auto").Trim().ToLowerInvariant();

        // LAITH_ECHO_TEXT_GATE=off disables layer 2 (for debugging the gate
        // itself — you almost never want this off in normal use).
        private static readonly bool TextGateEnabled =
            !string.Equals(Environment.GetEnvironmentVariable("LAITH_ECHO_TEXT_GATE"),
                           "off", StringComparison.OrdinalIgnoreCase);

        // How far above the measured bleed level a frame must sit to be believed
        // as the user rather than the speakers — 1.5x, about 3.5 dB.
        //
        // That sounds slack but the level below is a decaying PEAK, so bleed can
        // only clear it by jumping 50% above its own running maximum, which
        // speech at a steady volume doesn't do. Measured against 2x on real
        // audio the assistant still never tripped itself, while 2x meant the
        // user had to shout — the peak of a whole reply sits well above its
        // typical frame, so 2x of it was asking for 4-8x the average bleed.
        //
        // `LAITH_ECHO_MARGIN` overrides it: this is the one number that really
        // depends on the room, the speakers and where the mic sits.
        private static readonly double BargeInMargin =
            ReadDouble("LAITH_ECHO_MARGIN", 1.5);

        // Bleed is "real" when the level while speaking stands clear of the
        // room's NOISE FLOOR. Note the units differ deliberately — a peak
        // against a minimum — so this is not a 4x difference in loudness.
        //
        // Deliberately generous, because the two errors are not symmetric. A
        // false positive on a headset is now harmless: the gate engages, but the
        // bar it engages with was measured from room noise, so any voice clears
        // it and the only cost is the 600ms warm-up hold-off. A false NEGATIVE
        // on speakers means no gate at all, i.e. the assistant cutting itself
        // off on every single turn. At volume 20 bleed is only ~7x the floor of
        // a normal room, so anything stricter simply switches the gate off at
        // exactly the volume it was reported broken at.
        private const double EchoDetectRatio = 4.0;
        private const double EchoAbsFloor = 0.002;

        // Frames of AUDIBLE assistant audio before the peak is trustworthy —
        // ~600ms, counted from when sound actually reaches the microphone, not
        // from when the code started speaking.
        //
        // Those are wildly different moments: a streamed reply starts playing
        // 1-2s after BeginAssistantSpeech (LLM, then synthesis, then the
        // playback lead). Counting from the latter meant the whole calibration
        // window elapsed in silence — it measured a peak of 0.0089 against a
        // room of 0.0010 and called that the bleed level — and the gate then
        // went live exactly as the audio attack began, with a floor built from
        // room noise that the first loud syllable cleared instantly. That is
        // what "keeps cutting itself off" was.
        private const int EchoWarmupFrames = 20;

        // How far above the quiet room a frame has to be before we believe the
        // speakers have actually started.
        private const double AudibleRatio = 2.0;

        // Consecutive frames over the bar before a barge-in is believed —
        // ~90ms. Long enough that one loud syllable of bleed doesn't count,
        // short enough to stay well inside the barge-in budget.
        private const int BargeInSustainFrames = 3;

        // Ambient is the room's NOISE FLOOR — a minimum tracker, not a peak.
        //
        // A peak was wrong twice over: it latched onto the tail of the
        // assistant's own reply, and worse, onto the user's wakeword. The
        // exclusion guarding it (`!inSpeech`) is meaningless before the listener
        // is armed, which is exactly when the wakeword is spoken — so "Hey 49"
        // was absorbed into the estimate of a quiet room, ambient jumped to
        // 0.22, and the barge-in bar (ambient x2) became 0.4475. Nothing the
        // user could say was ever going to clear that.
        //
        // A minimum ignores anything loud by construction, which is the whole
        // point: speech, doors and the speakers can only ever push a floor
        // estimate the wrong way.
        private const double AmbientRise = 1.002;    // ~7%/s, so a genuinely
                                                     // louder room is followed
                                                     // within ~20s, while a 3s
                                                     // utterance moves it 1.2x
        private const int AmbientSeedFrames = 20;
        private const double AmbientMinimum = 1e-4;  // a muted mic must not
                                                     // collapse every threshold

        // Per-reply fade on the carried-over bleed estimate.
        private const double SessionFloorDecay = 0.9;

        // While the assistant is audible, a frame has to clear a higher VAD bar
        // before its level is even considered. Kokoro's output IS speech, so
        // this proves nothing on its own — it just keeps the energy test off the
        // half-voiced frames at the edges of a word, where the level is
        // meaningless.
        private const double SpeakingOnsetThreshold = 0.7;

        // Audio already handed to the sound card is still coming out of the
        // speakers after the code thinks it stopped, and its echo endpoints
        // later still. Treat this window after playback as "still speaking".
        private static readonly TimeSpan EchoTail = TimeSpan.FromMilliseconds(400);

        // Roughly the last two replies' worth of spoken text — enough that an
        // echo arriving after the next reply has started still matches, short
        // enough that it ages out well before a user could repeat it innocently.
        private const int MaxReferenceChars = 800;

        public event Action SpeechOnset;
        public event Action<string> UtteranceReady;

        private readonly object gate = new object();
        private WaveInEvent waveIn;
        private dynamic vad;

        // Armed == a conversation is open, so speech should be captured and
        // transcribed. While idle we still run the VAD (it's ~0.5% of a core) but
        // never buffer or transcribe — the wakeword owns the idle state, and
        // there's no reason to ship everything said near the mic to Whisper.
        private volatile bool armed;
        private volatile bool disposed;

        // Utterances finished but not yet collected — a barge-in endpoints while
        // the responder is still tearing down, and that utterance is the next
        // turn, so it must not be dropped.
        private readonly Queue<string> pending = new Queue<string>();
        private TaskCompletionSource<string> waiter;

        private readonly MemoryStream utterance = new MemoryStream();
        private readonly Queue<byte[]> preRoll = new Queue<byte[]>();
        private int preRollBytes;
        private int voicedFrames;
        private bool inSpeech;
        // Monotonic audio clock: total samples handed to the VAD so far.
        private long samplesSeen;
        private long speechStartSample;
        private long lastVoiceSample;

        // Echo state. The levels and frame counter are touched only on the audio
        // thread; the speaking flags are written by whichever thread is driving
        // TTS, so they're volatile / Volatile.Read'd rather than locked — the
        // audio callback must never wait on anything.
        private volatile bool assistantSpeaking;
        private long assistantSpeechEndedTicks;
        private volatile string spokenReference = string.Empty;
        private volatile bool levelResetPending;
        private int speakingFrames;
        private double bleedFloor;
        private double sessionBleedFloor;
        private double ambientFloor = 0.002;
        private int ambientSeeded;
        private bool echoDetected;
        // Set when the utterance being captured started (or continued) while the
        // assistant was audible — the flag that decides whether the finished
        // transcript gets echo-checked.
        private bool capturedDuringSpeech;
        // An utterance is being captured during assistant audio but hasn't yet
        // produced a frame clearly loud enough to be the user. Stays true until
        // one does (deferred barge-in) or the utterance ends.
        private bool bargeInPending;
        private int bargeInQualifying;
        // Transcripts of the earlier chunks of an utterance that is still going,
        // and the gate that keeps them in spoken order.
        private readonly StringBuilder continuationText = new StringBuilder();
        private readonly SemaphoreSlim transcribeGate = new SemaphoreSlim(1, 1);
        private int continuationSegments;
        // Frames since the reply started vs frames since it became AUDIBLE. The
        // gap between them is the LLM + synthesis + playback lead, routinely
        // 1-2 seconds, and conflating the two broke the calibration entirely.
        private int segmentFrames;
        private bool audioAudible;
        private volatile bool audioStarted;

        public bool IsArmed => armed;

        // True while the user is mid-utterance or an utterance is still being
        // transcribed. The conversation window must not expire in either state:
        // otherwise starting to speak just before the timeout gets your sentence
        // recognised and then thrown away.
        private int transcribing;
        public bool IsBusy
        {
            get
            {
                if (Volatile.Read(ref transcribing) > 0) return true;
                lock (gate) { return inSpeech; }
            }
        }

        // Loads the Silero session. Split out from Start() so the frame pipeline
        // can be exercised without opening a microphone.
        internal void LoadVad()
        {
            if (vad != null) return;
            using (Py.GIL())
            {
                vad = Py.Import("VoiceActivity");
                vad.load();
            }
        }

        public void Start()
        {
            LoadVad();

            lock (gate)
            {
                if (waveIn != null) return;
                waveIn = new WaveInEvent
                {
                    WaveFormat = CaptureFormat,
                    BufferMilliseconds = BufferMs,
                };
                waveIn.DataAvailable += OnData;
                waveIn.RecordingStopped += (s, e) =>
                {
                    if (e.Exception != null)
                    {
                        Console.WriteLine("[listen] capture stopped: " + e.Exception.Message);
                    }
                };
                waveIn.StartRecording();
            }
            Console.WriteLine("[listen] continuous capture started");
        }

        // Opens a listening window: speech from here on becomes utterances.
        public void Arm()
        {
            lock (gate)
            {
                ResetSpeechState();
                pending.Clear();
            }
            using (Py.GIL()) { vad.reset(); }
            armed = true;
        }

        public void Disarm()
        {
            armed = false;
            lock (gate) { ResetSpeechState(); }
        }

        // Throws away whatever is mid-capture and listens afresh from here.
        //
        // Called when the wakeword cuts a reply. On speakers the buffer at that
        // moment is mostly the assistant's own voice — the mic has been hearing
        // it continuously with no gap to endpoint on — with the wakeword tacked
        // on the end. Kept, it would be scored as echo and dropped wholesale,
        // taking the command that follows the wakeword down with it. That is
        // exactly how a barge-in ends up "never picked up".
        public void RestartCapture()
        {
            lock (gate)
            {
                inSpeech = false;
                voicedFrames = 0;
                capturedDuringSpeech = false;
                bargeInPending = false;
                bargeInQualifying = 0;
                utterance.SetLength(0);
                // The pre-roll is deliberately KEPT. The user is most likely
                // mid-sentence ("49, what time is it"), and the spotter reports
                // the keyword a few hundred ms after it was spoken, so the next
                // words are already in the air.
            }
        }

        // ── Assistant-speech reference ──────────────────────────────────────
        // Everything that produces assistant audio brackets it with these, so the
        // listener knows both that it should expect bleed and exactly what words
        // that bleed would transcribe to.

        // Called by the TTS the instant it starts playing. This is the only
        // trustworthy "the speakers are on" signal: derived from the microphone
        // it always loses to the VAD, which recognises the assistant's voice
        // before the level has risen far enough to be distinguishable from room
        // noise — so the gate was still inactive when the echo's onset fired and
        // the reply cut itself off, at any volume.
        public void NotifyAudioStarted()
        {
            audioStarted = true;
        }

        public void BeginAssistantSpeech(string text)
        {
            // Cleared here rather than on the audio thread so it can't be wiped
            // by a level reset that lands after playback has already begun.
            audioStarted = false;
            // Deliberately appends rather than replaces. An echo of the PREVIOUS
            // reply is routinely still mid-capture when the next one starts —
            // the trailing-silence timer only expires ~800ms after the audio
            // stops, by which time the answer to the last turn is already
            // speaking. Wiping the reference there meant that echo got compared
            // against a reply that hadn't produced any text yet, so it escaped
            // and the assistant answered itself one step delayed. That's how a
            // verbatim "Thanks for the update, I've got that for you." got
            // through a gate that catches paraphrases.
            AppendAssistantSpeech(text);
            // The levels themselves belong to the audio thread; this only asks
            // for a reset, so there's no cross-thread write to a double.
            levelResetPending = true;
            Volatile.Write(ref assistantSpeechEndedTicks, 0);
            assistantSpeaking = true;
        }

        // Streamed replies grow a sentence at a time; each one is another thing
        // the microphone might hear back. Keeps a rolling window rather than
        // just the current reply, so a late echo still has something to match
        // against; bounded so an old reply can't sit there for ever and swallow
        // a command that happens to reuse its words.
        public void AppendAssistantSpeech(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            string current = spokenReference;
            string combined = current.Length == 0 ? text : current + " " + text;
            if (combined.Length > MaxReferenceChars)
            {
                combined = combined.Substring(combined.Length - MaxReferenceChars);
            }
            spokenReference = combined;
        }

        // The reference text is deliberately NOT cleared here: the tail of the
        // reply is still in the air, and its echo endpoints after this point.
        public void EndAssistantSpeech()
        {
            assistantSpeaking = false;
            Volatile.Write(ref assistantSpeechEndedTicks, DateTime.UtcNow.Ticks);
        }

        // True while assistant audio could still be reaching the microphone.
        private bool AssistantAudible()
        {
            if (assistantSpeaking) return true;
            long ended = Volatile.Read(ref assistantSpeechEndedTicks);
            if (ended == 0) return false;
            return DateTime.UtcNow.Ticks - ended < EchoTail.Ticks;
        }

        private bool EchoGateActive()
        {
            if (EchoGateMode == "off") return false;
            // Playback hasn't started, so there's nothing to mistake for the
            // user — talking into the gap before a reply begins should cut it
            // instantly. Keyed off the TTS rather than the microphone, so it
            // can't be beaten to the punch by the VAD.
            if (!audioStarted) return false;
            // Forced-on still requires audio to have actually reached the mic,
            // otherwise a headset — where it never does — would leave the gate
            // permanently active and warm-up permanently incomplete, blocking
            // barge-in outright.
            if (EchoGateMode == "on") return audioAudible;
            return echoDetected;
        }

        // Maintains the two numbers the gate compares: the room's noise floor
        // while nothing is playing, and the bleed level measured over the first
        // frames of each reply's audible audio.
        private void TrackLevels(double rms)
        {
            if (levelResetPending)
            {
                levelResetPending = false;
                speakingFrames = 0;
                segmentFrames = 0;
                audioAudible = false;
                bleedFloor = 0;
                // Fades the session estimate one reply at a time, so turning the
                // speakers down is followed within a few turns instead of
                // leaving the bar stuck at the loudest level ever seen.
                sessionBleedFloor *= SessionFloorDecay;
            }

            if (!AssistantAudible())
            {
                TrackAmbientFloor(rms);
                return;
            }

            segmentFrames++;

            // The gate goes active as soon as the TTS says it has started
            // playing, even though the sound takes another device-buffer to
            // reach the microphone. Erring early is the safe direction: it
            // holds barge-in over a window where we KNOW audio is coming.
            if (!audioStarted) return;

            if (!audioAudible)
            {
                // The warm-up count and the bleed measurement, though, must wait
                // for sound to actually arrive — otherwise they'd be taken over
                // the silent device latency.
                if (rms < AudibleThreshold()) return;
                audioAudible = true;
                Console.WriteLine(
                    $"[echo] speakers audible {segmentFrames * BufferMs}ms after the reply started");
            }

            int n = ++speakingFrames;
            if (n > EchoWarmupFrames) return;

            // Establish the bar over the first frames of real audio, and nowhere
            // else. Every attempt at continuing to learn during the reply has
            // ended up learning the USER instead: their quieter frames sit below
            // the bar, look like bleed, and ratchet it up out of their own reach.
            bleedFloor = Math.Max(bleedFloor, rms);
            if (n < EchoWarmupFrames) return;

            // Carried across replies, because bleed level is a property of the
            // volume knob, not of one reply — so a reply that happens to open
            // quietly still gets a bar informed by the ones before it.
            sessionBleedFloor = Math.Max(sessionBleedFloor, bleedFloor);
            Console.WriteLine(
                $"[echo] bleed floor {bleedFloor:F4} (session {sessionBleedFloor:F4},"
                + $" barge-in above {EffectiveFloor() * BargeInMargin:F4})");

            if (!echoDetected &&
                bleedFloor >= Math.Max(ambientFloor * EchoDetectRatio, EchoAbsFloor))
            {
                echoDetected = true;
                Console.WriteLine(
                    $"[echo] bleed detected (floor {bleedFloor:F4} vs room noise {ambientFloor:F4})"
                    + " — gating barge-in on level");
            }
        }

        // The room's noise floor: a minimum, so nothing loud can ever raise it.
        // Only reached while the assistant is silent, but it deliberately does
        // NOT exclude the user talking — a minimum tracker doesn't need to, and
        // the exclusion that used to guard this (`!inSpeech`) was inert before
        // the listener was armed, which is precisely when the wakeword is said.
        private void TrackAmbientFloor(double rms)
        {
            if (ambientSeeded < AmbientSeedFrames)
            {
                ambientSeeded++;
                ambientFloor = ambientSeeded == 1 ? rms : Math.Min(ambientFloor, rms);
            }
            else if (rms < ambientFloor)
            {
                ambientFloor = rms;
            }
            else
            {
                ambientFloor *= AmbientRise;
            }
            if (ambientFloor < AmbientMinimum) ambientFloor = AmbientMinimum;
        }

        private double AudibleThreshold()
        {
            return Math.Max(ambientFloor * AudibleRatio, EchoAbsFloor);
        }

        // The double-talk test: is this frame loud enough, and speech-like
        // enough, to be the user rather than the assistant's own voice coming
        // back? Evaluated on EVERY frame of a candidate utterance, not just its
        // onset — see the deferred barge-in note in HandleFrame.
        private bool BargeInAllowed(double prob, double rms)
        {
            if (speakingFrames < EchoWarmupFrames) { bargeInQualifying = 0; return false; }

            if (prob >= SpeakingOnsetThreshold && rms >= EffectiveFloor() * BargeInMargin)
            {
                // Sustained, not instantaneous. One frame over the bar is what a
                // single loud syllable of the assistant's own voice looks like;
                // someone actually talking holds it. Cheap insurance now that
                // the bar is fixed for the reply — against a moving bar a
                // sustain count is meaningless, because the bar moves with you.
                bargeInQualifying++;
                return bargeInQualifying >= BargeInSustainFrames;
            }

            bargeInQualifying = 0;
            return false;
        }

        // The bar a frame has to clear: the bleed peak measured over the first
        // `EchoWarmupFrames` of AUDIBLE audio, then FIXED for the reply and
        // carried across replies via `sessionBleedFloor`. Nothing learns it after
        // warm-up — see TrackLevels, which returns once the count is past.
        //
        // Three attempts at learning it during the reply all shipped and all
        // failed, in different ways. Don't re-derive them:
        //   - snapshotted when the utterance began — can't follow a reply that
        //     gets louder afterwards. Measured: a floor of 0.1666 against bleed
        //     that went on to hit 0.2596, read as a barge-in. Self-cut on every
        //     turn.
        //   - tracking every frame live — chases the user's own attack up.
        //     Speech ramps over a few frames, the peak follows each one, they
        //     never get ahead of it, and barge-in stops working entirely.
        //   - taught only by frames BELOW the bar, meant to get the first
        //     without the second — chased the user anyway, because the quiet
        //     leading edge of their speech is below the bar by definition.
        //     GateSim caught it: a floor of 0.352 against bleed of 0.170.
        // A fixed bar is also what makes `BargeInSustainFrames` meaningful; a
        // sustain count against a moving bar is nothing, since the bar moves
        // with you. Bleed level is a property of the volume knob, not of one
        // reply, which is why carrying it across replies is the right shape.
        private double EffectiveFloor()
        {
            return Math.Max(sessionBleedFloor, AudibleThreshold());
        }

        private static double ReadDouble(string name, double fallback)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            double parsed;
            if (!string.IsNullOrWhiteSpace(raw) &&
                double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) &&
                parsed > 0)
            {
                return parsed;
            }
            return fallback;
        }

        // RMS of one 16-bit mono chunk, 0..1.
        private static double FrameRms(byte[] pcm)
        {
            if (pcm == null || pcm.Length < 2) return 0.0;
            int n = pcm.Length / 2;
            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                short s = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8));
                double v = s / 32768.0;
                sum += v * v;
            }
            return Math.Sqrt(sum / n);
        }

        private void OnData(object sender, WaveInEventArgs e)
        {
            if (disposed || e.BytesRecorded <= 0) return;
            var chunk = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);
            ProcessAudio(chunk);
        }

        // The whole frame pipeline: VAD, onset detection, endpointing. Live audio
        // and tests take the same path through here.
        internal void ProcessAudio(byte[] chunk)
        {
            try
            {
                // Audio time advances whether or not we're listening.
                Interlocked.Add(ref samplesSeen, chunk.Length / 2);

                // Levels track every chunk, including while idle and while the
                // VAD is still short of a full frame — the ambient estimate is
                // only useful if it has seen the quiet.
                double rms = FrameRms(chunk);
                TrackLevels(rms);

                double prob;
                using (Py.GIL())
                {
                    prob = (double)vad.push(chunk);
                }
                if (prob < 0) return;   // not a whole VAD frame yet

                if (!armed)
                {
                    // Keep the pre-roll warm so arming mid-sentence still catches
                    // the words already in the air.
                    lock (gate) { PushPreRoll(chunk); }
                    return;
                }

                HandleFrame(chunk, prob, rms);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[listen] frame error: " + ex.Message);
            }
        }

        private void HandleFrame(byte[] chunk, double prob, double rms)
        {
            bool fireOnset = false;
            bool suppressedOnset = false;
            long deferredSamples = -1;
            double allowedRms = 0;
            byte[] finished = null;
            string echoReference = null;
            bool isContinuation = false;
            int segmentIndex = 0;
            long now = Interlocked.Read(ref samplesSeen);

            lock (gate)
            {
                if (!inSpeech)
                {
                    PushPreRoll(chunk);

                    if (prob >= OnsetThreshold)
                    {
                        voicedFrames++;
                        if (voicedFrames >= OnsetFrames)
                        {
                            inSpeech = true;
                            speechStartSample = now;
                            lastVoiceSample = now;
                            utterance.SetLength(0);
                            // Start the recording from before we were sure.
                            foreach (byte[] b in preRoll) utterance.Write(b, 0, b.Length);

                            // Capture regardless; only the barge-in signal is
                            // gated. A real interruption the energy test judged
                            // too quiet still gets transcribed and answered — it
                            // just doesn't cut the reply mid-word.
                            bool duringSpeech = AssistantAudible();
                            if (duringSpeech) capturedDuringSpeech = true;

                            bool gated = duringSpeech && EchoGateActive();
                            fireOnset = !gated || BargeInAllowed(prob, rms);
                            bargeInPending = gated && !fireOnset;
                            suppressedOnset = bargeInPending;
                        }
                    }
                    else
                    {
                        voicedFrames = 0;
                    }
                }
                else
                {
                    utterance.Write(chunk, 0, chunk.Length);
                    if (prob >= SustainThreshold) lastVoiceSample = now;
                    if (AssistantAudible()) capturedDuringSpeech = true;

                    // Deferred barge-in. The onset frame is the worst possible
                    // place to judge someone: it's the first 60ms of their first
                    // syllable, so the level is still rising and the VAD is only
                    // half-convinced (measured p≈0.5-0.6 against a 0.7 bar —
                    // most suppressions were failing on THAT, not on volume, so
                    // talking louder couldn't have helped). Keep testing as the
                    // utterance develops and cut as soon as any frame is
                    // clearly the user; a few frames later is still well inside
                    // the ~300ms barge-in budget.
                    if (bargeInPending && AssistantAudible() && BargeInAllowed(prob, rms))
                    {
                        bargeInPending = false;
                        fireOnset = true;
                        deferredSamples = now - speechStartSample;
                        allowedRms = rms;
                    }

                    bool silentLongEnough = (now - lastVoiceSample) >= TrailingSilenceSamples;
                    bool tooLong = (now - speechStartSample) >= MaxUtteranceSamples;

                    if (silentLongEnough || tooLong)
                    {
                        // The blip filter is for stray coughs at the START of an
                        // utterance. Once chunks have been banked, even a very
                        // short tail is the end of a real sentence and has to be
                        // transcribed — otherwise it's discarded as a blip and
                        // everything banked behind it goes with it.
                        bool longEnough = (lastVoiceSample - speechStartSample) >= MinSpeechSamples
                                          || continuationSegments > 0;
                        if (longEnough) finished = BuildWav(utterance.ToArray());
                        else Console.WriteLine("[listen] ignoring blip");
                        // Snapshot before the reset clears it — and take the
                        // reference NOW rather than at onset, so a reply that
                        // grew another sentence while the user was talking is
                        // still compared in full.
                        if (capturedDuringSpeech) echoReference = spokenReference;

                        // The length cap is a CHUNK boundary, not a turn
                        // boundary. Hitting it means the user is still going, so
                        // ending the turn there cuts them off mid-sentence,
                        // answers the fragment, and then answers the rest as a
                        // second turn — reading a long list of numbers produced
                        // three replies and an interruption. Keep the utterance
                        // open and stitch the transcripts back together; only
                        // silence ends a turn.
                        isContinuation = tooLong && !silentLongEnough && longEnough
                                         && continuationSegments < MaxContinuationSegments;

                        if (isContinuation)
                        {
                            continuationSegments++;
                            segmentIndex = continuationSegments;
                            utterance.SetLength(0);
                            speechStartSample = now;   // restart the chunk clock
                            // inSpeech stays true: they haven't stopped talking.
                        }
                        else
                        {
                            continuationSegments = 0;
                            ResetSpeechState();
                        }
                    }
                }
            }

            if (suppressedOnset)
            {
                Console.WriteLine(
                    $"[echo] onset held as bleed (rms {rms:F4} vs floor {EffectiveFloor():F4}, p={prob:F2})"
                    + " — still listening for a clear frame");
            }

            if (deferredSamples >= 0)
            {
                Console.WriteLine(
                    $"[echo] barge-in allowed {deferredSamples * 1000 / 16000}ms into the utterance"
                    + $" (rms {allowedRms:F4} vs floor {EffectiveFloor():F4})");
            }

            if (fireOnset)
            {
                Console.WriteLine("[listen] speech onset");
                var handler = SpeechOnset;
                if (handler != null)
                {
                    // Off the audio thread: a barge-in handler tears down TTS and
                    // must never stall capture.
                    Task.Run(() => { try { handler(); } catch (Exception ex) { Console.WriteLine("[listen] onset handler: " + ex.Message); } });
                }
            }

            if (isContinuation)
            {
                Console.WriteLine(
                    $"[listen] still talking at the {MaxUtteranceSamples / 16000}s cap"
                    + $" — chunk {segmentIndex}, holding the turn open");
            }

            if (finished != null)
            {
                // Counted before the task starts, so IsBusy is already true by the
                // time the caller's timeout could next be evaluated.
                Interlocked.Increment(ref transcribing);
                string reference = echoReference;
                bool continuation = isContinuation;
                Task.Run(() => TranscribeAndPublishAsync(finished, reference, continuation));
            }
        }

        private async Task TranscribeAndPublishAsync(byte[] wav, string echoReference, bool isContinuation)
        {
            // Serialised so a long utterance's chunks are stitched back together
            // in the order they were spoken — two transcriptions in flight can
            // otherwise finish out of order.
            await transcribeGate.WaitAsync().ConfigureAwait(false);
            string text;
            var sw = Stopwatch.StartNew();
            try
            {
                text = await SpeechToTextService.TranscribeAsync(wav).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[listen] transcription failed: " + ex.Message);
                transcribeGate.Release();
                return;
            }
            finally
            {
                Interlocked.Decrement(ref transcribing);
            }

            try
            {
                LastTranscribeElapsed = sw.Elapsed;

                if (string.IsNullOrWhiteSpace(text))
                {
                    // A silent tail chunk must not strand what was already
                    // banked — that would swallow the whole long utterance.
                    if (isContinuation || continuationText.Length == 0) return;
                    text = continuationText.ToString().Trim();
                    continuationText.Length = 0;
                    // Each banked chunk was echo-checked as it arrived.
                    echoReference = null;
                    if (text.Length == 0) return;
                }

                // Layer 2: this utterance overlapped assistant audio, so check it
                // isn't just the assistant. Dropping it here — before the queue —
                // is what stops the assistant answering itself in a loop.
                if (TextGateEnabled && echoReference != null)
                {
                    if (EchoGuard.IsEcho(text, echoReference))
                    {
                        Console.WriteLine($"[echo] dropped self-heard utterance: \"{text}\"");
                        return;
                    }
                    // Heard over assistant audio and kept. Usually right — that's a
                    // real barge-in — but it's also the only way an echo becomes a
                    // turn, so show the scores that let it through.
                    Console.WriteLine($"[echo] kept (heard over speech): {EchoGuard.Describe(text, echoReference)}");
                }

                // A chunk of a still-running utterance: bank it and wait for the
                // rest rather than answering half a sentence.
                if (isContinuation)
                {
                    if (continuationText.Length > 0) continuationText.Append(' ');
                    continuationText.Append(text.Trim());
                    Console.WriteLine($"[listen] holding: \"{text}\"");
                    return;
                }

                if (continuationText.Length > 0)
                {
                    text = continuationText + " " + text.Trim();
                    continuationText.Length = 0;
                }

                Console.WriteLine($"RECOGNIZED: {text}");

                TaskCompletionSource<string> pendingWaiter = null;
                lock (gate)
                {
                    if (waiter != null) { pendingWaiter = waiter; waiter = null; }
                    else pending.Enqueue(text);
                }
                pendingWaiter?.TrySetResult(text);

                UtteranceReady?.Invoke(text);
            }
            finally
            {
                transcribeGate.Release();
            }
        }

        public TimeSpan LastTranscribeElapsed { get; private set; }

        // Waits for the next complete utterance. Returns "" once the user has been
        // quiet for `timeout` — that's how a conversation window closes.
        //
        // The timeout only counts SILENCE. If it expires while speech is still
        // being captured or transcribed, we keep waiting: closing the window on
        // someone who is mid-sentence loses the sentence they just said.
        public async Task<string> NextUtteranceAsync(TimeSpan timeout)
        {
            Task<string> wait;
            lock (gate)
            {
                if (pending.Count > 0) return pending.Dequeue();
                waiter = new TaskCompletionSource<string>();
                wait = waiter.Task;
            }

            DateTime deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    if (IsBusy)
                    {
                        // Mid-utterance: give it room to finish and be transcribed.
                        deadline = DateTime.UtcNow + BusyGrace;
                        continue;
                    }
                    lock (gate) { waiter = null; }
                    return string.Empty;
                }

                // Wake up regularly so IsBusy is re-checked near the deadline.
                var slice = remaining < PollSlice ? remaining : PollSlice;
                var finished = await Task.WhenAny(wait, Task.Delay(slice)).ConfigureAwait(false);
                if (finished == wait) return wait.Result;
            }
        }

        private void PushPreRoll(byte[] chunk)
        {
            preRoll.Enqueue(chunk);
            preRollBytes += chunk.Length;
            int limit = (int)(CaptureFormat.AverageBytesPerSecond * PreRoll.TotalSeconds);
            while (preRollBytes > limit && preRoll.Count > 0)
            {
                preRollBytes -= preRoll.Dequeue().Length;
            }
        }

        private void ResetSpeechState()
        {
            inSpeech = false;
            voicedFrames = 0;
            capturedDuringSpeech = false;
            bargeInPending = false;
            bargeInQualifying = 0;
            continuationSegments = 0;
            utterance.SetLength(0);
            preRoll.Clear();
            preRollBytes = 0;
        }

        private static byte[] BuildWav(byte[] pcm)
        {
            var mem = new MemoryStream();
            using (var writer = new WaveFileWriter(new NonClosingStream(mem), CaptureFormat))
            {
                writer.Write(pcm, 0, pcm.Length);
                writer.Flush();
            }
            return mem.ToArray();
        }

        public void Dispose()
        {
            disposed = true;
            armed = false;
            lock (gate)
            {
                if (waveIn != null)
                {
                    try { waveIn.StopRecording(); } catch { }
                    try { waveIn.Dispose(); } catch { }
                    waveIn = null;
                }
            }
        }

        // WaveFileWriter disposes what it wraps, but we need the MemoryStream
        // afterwards to read the finished WAV back out.
        private sealed class NonClosingStream : Stream
        {
            private readonly Stream inner;
            public NonClosingStream(Stream inner) { this.inner = inner; }
            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => inner.CanSeek;
            public override bool CanWrite => inner.CanWrite;
            public override long Length => inner.Length;
            public override long Position { get => inner.Position; set => inner.Position = value; }
            public override void Flush() => inner.Flush();
            public override int Read(byte[] b, int o, int c) => inner.Read(b, o, c);
            public override long Seek(long o, SeekOrigin s) => inner.Seek(o, s);
            public override void SetLength(long v) => inner.SetLength(v);
            public override void Write(byte[] b, int o, int c) => inner.Write(b, o, c);
            protected override void Dispose(bool disposing) { }
        }
    }
}