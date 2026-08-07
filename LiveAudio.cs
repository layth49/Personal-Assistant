using NAudio.Wave;
using Personal_Assistant.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Personal_Assistant.LiveAudio
{
    // Audio plumbing for the Gemini Live session: microphone capture at the rate
    // the Live API wants in (16 kHz mono PCM16) and playback of what it sends
    // back (24 kHz mono PCM16).
    //
    // This is deliberately far smaller than local-laith's ContinuousListener,
    // because the Live API supplies the VAD, the endpointing and the turn taking.
    // There is no Silero here, no pythonnet, no sample-clock endpointing and no
    // chunk stitching — none of it is ours to do any more.
    //
    // What IS ours, and what the whole design turns on:
    //
    //   1. Half duplex. The microphone stops uploading entirely while assistant
    //      audio is playing, which is what makes speaker echo structurally
    //      impossible instead of something a threshold has to win an argument
    //      with. Five speaker runs on local-laith established that at loud
    //      volumes bleed and speech overlap in level and NO threshold separates
    //      them, and — the part that keeps catching people out — that you must
    //      never infer "the speakers are on" from the microphone: the VAD
    //      recognises the assistant's voice before the frame level has risen
    //      enough to tell it from room noise, so a mic-derived latch loses that
    //      race at every volume. The playback side knows exactly; it reports it.
    //      Barge-in during a reply stays on the wake word. Real AEC is a non-goal.
    //
    //   2. A silence upload gate. A session that streams while nothing is
    //      happening is the one thing that can actually burn the free tier — at
    //      32 input tokens/second, uploading pure silence costs ~115k tokens an
    //      hour. So an idle follow-up window uploads nothing.
    //
    //      That gate is a BANDWIDTH gate and nothing else. It is not a VAD, and
    //      it is emphatically not the barge-in energy gate that local-laith
    //      proved cannot work on loud speakers — it never has to tell the user
    //      from the assistant, because rule 1 means it is closed whenever the
    //      assistant is audible.

    // Microphone capture, gated. Frames the gate lets through are handed to
    // FrameReady; everything else is dropped on the audio thread and never
    // allocated onto the wire.
    public sealed class LiveAudioCapture : IDisposable
    {
        // What the Live API accepts as realtimeInput.audio.
        private static readonly WaveFormat CaptureFormat = new WaveFormat(16000, 16, 1);

        // 20 ms frames: small enough that gate transitions are prompt, large
        // enough that we aren't posting a WebSocket message every few samples.
        private const int BufferMs = 20;
        private const int FrameBytes = 16000 * 2 * BufferMs / 1000;

        // Kept before the gate opens so the flush can restore the speech onset.
        // Without it the first syllable is clipped and the model mishears short
        // commands, which presents as "it keeps asking me to repeat myself" and
        // not as an audio bug at all.
        private const int PreRollMs = 300;
        private const int PreRollBytes = 16000 * 2 * PreRollMs / 1000;

        // Consecutive frames over the floor before the gate opens. Two is ~40 ms
        // — enough to reject a click, fast enough that the pre-roll covers it.
        private const int OnsetFrames = 2;

        // How long the gate stays open after the level drops. This doubles as the
        // endpoint activityEnd hangs on, so it has to survive an ordinary pause
        // mid-sentence — when it doesn't, the model is handed half a sentence as
        // a COMPLETE turn and answers the fragment.
        //
        // 800 ms was local-laith's measured value (bakeoff/stt/tail_sweep.py), but
        // it was measured for a local STT endpointer, not as a conversational turn
        // boundary. Observed failing here: "tell me how jet engines work" split
        // into "some/how" + "works." across two turns, and a hesitant "I'm... set
        // our... message saying hello to" split three ways. Fluent speech survived
        // 800 ms fine; hesitation did not.
        //
        // Raised to 1200 ms, and tunable — this is a real trade, not a free win:
        // every extra millisecond here is added latency before EVERY reply, since
        // the model cannot start until activityEnd. Lower it if replies feel
        // sluggish, raise it if sentences keep getting cut in half.
        //
        // Public because it is "how long a silence ends the user's turn", which is
        // a question in BOTH endpointing modes. This gate answers it under client
        // endpointing; under server VAD the gate is bypassed entirely and
        // LiveSession applies the same span to the arriving transcripts instead.
        // One setting, one meaning, so a session behaves the same way when the
        // mode is flipped in App.config.
        public static readonly int HangoverMs =
            LaithConfig.Int("LiveHangoverMs", 1200, min: 300, max: 5000);
        private static readonly int HangoverFrames = Math.Max(1, HangoverMs / BufferMs);


        // Speakers are still sounding after the code thinks playback stopped.
        // Counted in FRAMES, not wall-clock: a frame count cannot drift from the
        // audio it describes, and it keeps the gate testable without a clock.
        private const int EchoTailMs = 400;
        private const int EchoTailFrames = EchoTailMs / BufferMs;

        // Room tone measurement. The floor is a MINIMUM tracker — never a peak.
        // A peak version on local-laith latched onto the wake word (spoken while
        // the listener is unarmed, so no exclusion catches it), drove the bar to
        // 0.4475 and locked out anything a human could say. A minimum ignores
        // everything loud by construction and so needs no exclusion at all.
        private const int AmbientSeedFrames = 1000 / BufferMs;   // ~1 s of room tone
        private const double AmbientRise = 1.0008;               // ~+4%/s, follows a room getting noisier
        private const double AmbientMinimum = 0.0005;

        // Derived floor = ambient x this, clamped. The clamp matters more than
        // the ratio: a bandwidth gate that uploads some room noise costs tokens,
        // a bandwidth gate that locks the user out costs the assistant.
        private const double UploadRatio = 3.0;
        private const double AbsoluteFloor = 0.006;
        private const double MaxDerivedFloor = 0.08;

        // ---- input gain -----------------------------------------------------
        //
        // Native-audio Gemini does transcription and reasoning in ONE pass, so
        // there is no recogniser to tune and no phrase list to bias — the only
        // lever left on transcription quality is the signal we hand it.
        //
        // Measured on Layth's mic 2026-08-05: leaning back in the chair drops the
        // mean frame level from ~0.055 to ~0.012, and the transcripts fall apart
        // across that range in the same session ("unpause my video" at mean
        // 0.0565, unintelligible garbage at 0.0133). `peak` barely moves between
        // the two — it catches one plosive — which is why MEAN is the number to
        // read on the utterance line.
        //
        // That 13 dB is unused range, not a signal-to-noise problem: ambient sits
        // at ~0.0005 with headroom over 400x, so there is nothing down there to
        // amplify along with the speech. So normalise toward a target instead of
        // shipping whatever the room happened to deliver.
        //
        // Two rules keep this from becoming a distortion pedal:
        //
        //   1. It only ADAPTS on frames that are actually speech (at or above the
        //      upload floor) and HOLDS its gain through silence. An AGC that
        //      chased silence would wind up to maximum during a pause and then
        //      hand the server's VAD a wall of amplified room tone — which under
        //      server endpointing is a turn boundary, not just noise.
        //   2. Gain falls fast and rises slowly. A syllable louder than the
        //      estimate must not clip while that estimate catches up; but a slow
        //      rise keeps a sentence at an even level rather than pumping between
        //      words, and pumping is itself something ASR mishears.
        //
        // The peak cap is what makes the RMS target safe. Speech has a crest
        // factor around 4x, so gain enough to put the MEAN at target would drive
        // the loudest syllables past full scale; capping on the decaying peak
        // envelope lands the level as high as it can go without the limiter
        // having to do the work.
        //
        // It is also what frees the LEVEL estimate to be a plain symmetric
        // average. The obvious AGC shape — fast attack, slow release — makes the
        // estimate track the top of the frame-RMS spread rather than its middle,
        // and the target here is a MEAN (0.05 is the measured mean of utterances
        // that transcribed correctly). Pairing the two would silently under-gain
        // by whatever the crest factor happens to be: ~1.7x instead of ~3.5x on
        // the leaned-back case. Clip protection does not need the estimate to be
        // fast, because peakEnvelope rises on the very first loud frame and the
        // gain cap follows it within a few frames.

        // 0 = adapt (the default). 1 = unity, i.e. off. Anything else is a fixed
        // gain, for pinning the variable while testing something else.
        private static readonly double GainSetting =
            LaithConfig.Double("LiveInputGain", 0.0, 0.0, 16.0);

        // Target mean level for adaptive mode — measured from the utterances that
        // transcribed correctly, not picked.
        private static readonly double GainTarget =
            LaithConfig.Double("LiveInputTarget", 0.05, 0.005, 0.5);

        private const double MaxAutoGain = 8.0;

        // What counts as "speech" for the LEVEL ESTIMATE — deliberately far below
        // the upload floor, and not the same test.
        //
        // Reusing the upload floor here was wrong and measurably so. At 0.006 it
        // sits inside quiet speech rather than under it: on the leaned-back
        // recording it excluded 43% of the frames, and precisely the quiet ones,
        // so the estimate came out ~1.55x the true mean and the gain settled at
        // 2.5x where 4x was needed. Meanwhile GainTarget was measured as a mean
        // over ALL frames of an utterance — the two have to be the same statistic
        // or the target is silently unreachable.
        //
        // Derived from ambient rather than fixed, so a genuinely noisy room
        // raises the bar and this declines to amplify the noise along with the
        // voice; the constant is only a floor under that for a very quiet room.
        private const double GainAdaptRatio = 4.0;
        private const double GainAdaptMinimum = 0.002;

        // Speech frames to observe before the gain is allowed to move. Without it
        // the very first syllable of a session sets the estimate on its own, and
        // the gain lurches before settling.
        private const int GainWarmupFrames = 25;   // 0.5 s of speech

        // Leaves ~1 dB below full scale, so the per-sample clamp is a backstop
        // rather than part of normal operation.
        private const double PeakCeiling = 0.90;

        // Per 20 ms frame, at 50 frames/s.
        private const double SpeechRmsAlpha = 0.03;    // symmetric — ~0.7 s of speech
        private const double GainFall = 0.25;          // ~80 ms, so a cap drop lands fast
        private const double GainRise = 0.04;          // ~0.5 s, slow enough not to pump
        private const double PeakDecay = 0.995;        // ~0.8x per second

        private double speechRms;
        private double peakEnvelope;
        private double inputGain = 1.0;
        private int gainWarmup;

        // One frame of 16 kHz mono PCM16, ready to go up as realtimeInput.audio.
        public event Action<byte[]> FrameReady;

        // Gate transitions, for Phase 3 to turn into activityStart / activityEnd.
        // Opened fires BEFORE the pre-roll flush, so the frames the session sees
        // after it are the whole utterance including its onset.
        public event Action UploadGateOpened;
        public event Action UploadGateClosed;

        private readonly object gate = new object();
        private WaveInEvent waveIn;
        private volatile bool disposed;

        // Gate state. Touched only on the audio thread.
        private bool gateOpen;
        private int onsetFrames;
        private int silentFrames;
        // Per-utterance signal stats, reported on gate close. Diagnostic only —
        // nothing gates on these. Measured BEFORE gain, so they keep describing
        // the room rather than what the gain stage made of it; the gain applied is
        // reported alongside.
        private double utterancePeak;
        private double utteranceSum;
        private int utteranceFrames;

        // The same, over SPEECH frames only.
        //
        // Under continuous upload a segment is mostly silence, so the plain mean
        // above is diluted by whatever the speech-to-silence ratio happened to be
        // — a 4460 ms segment and a 7480 ms one are not comparable measurements of
        // how loud somebody was, and reading them as if they were is how the gain
        // target came to be set from the wrong number. This one is comparable
        // across utterances, and it is what the gain stage actually works on.
        private double utteranceSpeechSum;
        private double utteranceSpeechGainSum;
        private int utteranceSpeechFrames;

        private readonly Queue<byte[]> preRoll = new Queue<byte[]>();
        private int preRollBytes;
        private double ambientFloor = 0.002;
        private int ambientSeeded;

        // Written by whichever thread drives playback, read on the audio thread.
        private volatile bool assistantSpeaking;
        private int echoTail;

        private readonly double floorOverride;

        private long framesUploaded;
        private long bytesUploaded;
        private long framesDroppedSilent;
        private long framesDroppedForPlayback;

        // With server-side VAD the SERVER decides where turns end, so the energy
        // gate must not also decide it. Leaving the gate in charge would hand the
        // same truncated audio to a better endpointer and change nothing — the
        // server would just see the silence the gate created.
        //
        // The half-duplex assistant-audio gate is NOT affected by this and still
        // drops every frame while the speakers are live. That gate is the echo
        // protection; this one is only bandwidth.
        public bool UploadContinuously { get; set; }

        public LiveAudioCapture()
        {
            floorOverride = LaithConfig.Double("LiveUploadFloor", 0.0, 0.0, 1.0);
            if (floorOverride > 0)
            {
                Console.WriteLine(
                    $"[live-audio] upload floor pinned to {floorOverride:F4} by LiveUploadFloor");
            }

            if (GainSetting > 0)
            {
                inputGain = GainSetting;
                Console.WriteLine(
                    $"[live-audio] input gain fixed at {GainSetting:F2}x by LiveInputGain " +
                    "(adaptive gain off)");
            }
            else
            {
                Console.WriteLine(
                    $"[live-audio] input gain adaptive, target mean {GainTarget:F3} " +
                    $"(max {MaxAutoGain:F0}x) — set LiveInputGain=1 to disable");
            }
        }

        public bool IsUploading { get { lock (gate) { return gateOpen; } } }
        public bool AssistantAudioPlaying => assistantSpeaking;

        public long FramesUploaded => Interlocked.Read(ref framesUploaded);
        public long BytesUploaded => Interlocked.Read(ref bytesUploaded);
        public long FramesDroppedSilent => Interlocked.Read(ref framesDroppedSilent);
        public long FramesDroppedForPlayback => Interlocked.Read(ref framesDroppedForPlayback);

        // Diagnostics — what the gate is currently measuring against.
        public double AmbientFloor => ambientFloor;
        public double UploadFloor
        {
            get
            {
                if (floorOverride > 0) return floorOverride;
                return Math.Min(Math.Max(ambientFloor * UploadRatio, AbsoluteFloor), MaxDerivedFloor);
            }
        }

        public void Start()
        {
            lock (gate)
            {
                if (disposed || waveIn != null) return;
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
                        Console.WriteLine("[live-audio] capture stopped: " + e.Exception.Message);
                    }
                };
                waveIn.StartRecording();
            }
            Console.WriteLine("[live-audio] mic capture started (16 kHz mono PCM16)");
        }

        public void Stop()
        {
            WaveInEvent device;
            lock (gate)
            {
                device = waveIn;
                waveIn = null;
            }
            if (device == null) return;
            try { device.StopRecording(); } catch { }
            try { device.Dispose(); } catch { }
        }

        // Closes the gate for the duration of assistant audio. Call this the
        // moment audio is HANDED to the output device, not when it becomes
        // audible: erring early is the safe direction, because it holds the mic
        // over a window where we know sound is coming, and the alternative is
        // exactly the race the microphone always loses.
        public void BeginAssistantAudio()
        {
            assistantSpeaking = true;
        }

        public void EndAssistantAudio()
        {
            assistantSpeaking = false;
            Volatile.Write(ref echoTail, EchoTailFrames);
        }

        // The same gate, for assistant audio this pipeline did NOT produce: a
        // fired timer, a prayer announcement, a standing rule. Those play through
        // SpeechService (a clip, or Azure TTS) on a completely separate output
        // path, so EnqueueAssistantAudio never sees them and the microphone stayed
        // wide open while the speakers were talking — on speakers, the Live model
        // then answered the assistant's own voice.
        //
        // Deliberately a SECOND flag rather than reusing assistantSpeaking. The
        // two sources overlap (a timer can fire mid-reply), and sharing one bool
        // would let whichever finished first reopen the gate while the other was
        // still audible. Sharing a counter instead would put the model's audio
        // path — the one that works — at the mercy of an unbalanced external
        // Begin/End. Two independent flags, OR'd at the single point of use,
        // cannot interfere with each other in either direction.
        private volatile bool externalAudio;

        public bool ExternalAudioPlaying => externalAudio;

        public void BeginExternalAudio()
        {
            externalAudio = true;
        }

        public void EndExternalAudio()
        {
            externalAudio = false;
            // Same speaker-tail guard the model's audio gets: the sound is still
            // leaving the room for a few frames after the last sample is handed over.
            Volatile.Write(ref echoTail, EchoTailFrames);
        }

        // Listen afresh from right now: no echo tail, no half-formed onset, no
        // stale pre-roll.
        //
        // Called when the wakeword cuts a reply, and it is the same intent as
        // local-laith's ContinuousListener.RestartCapture() pointed at the same
        // failure ("the barge-in was never picked up") — but the opposite
        // mechanism, because half duplex already solved the problem that one had.
        // There is no buffer full of the reply's own echo to throw away here;
        // those frames were dropped as they arrived. What there IS is the 400 ms
        // speaker-tail guard EndAssistantAudio just armed, and since the spotter
        // reports the keyword a few hundred ms after it was spoken, the user is
        // ALREADY saying the command that follows it. Left alone, the guard eats
        // exactly that. A barge-in is the one moment we know the speakers were
        // cut, so the tail it protects against isn't coming.
        public void RestartCapture()
        {
            assistantSpeaking = false;
            // Cleared too, so a barge-in can never be swallowed by an external
            // announcement whose End was missed. This is also the recovery path
            // if BeginExternalAudio were ever left latched on.
            externalAudio = false;
            Volatile.Write(ref echoTail, 0);
            DropPreRoll();
            if (gateOpen) CloseGate();
            silentFrames = 0;
        }

        private void OnData(object sender, WaveInEventArgs e)
        {
            if (disposed || e.BytesRecorded <= 0) return;
            var frame = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, frame, 0, e.BytesRecorded);
            ProcessFrame(frame);
        }

        // The whole gate, in one place. Live audio and the harness take the same
        // path through here, so what the harness asserts on is what runs.
        internal void ProcessFrame(byte[] frame)
        {
            if (disposed || frame == null || frame.Length == 0) return;

            // Half duplex. Note this also drops the pre-roll: banking bleed and
            // then flushing it the instant the gate reopens would hand the model
            // its own voice as if the user had said it.
            if (ConsumeAssistantAudioFrame())
            {
                DropPreRoll();
                if (gateOpen) CloseGate();
                Interlocked.Increment(ref framesDroppedForPlayback);
                return;
            }

            double rms = FrameRms(frame);
            TrackAmbientFloor(rms);
            bool loud = rms >= UploadFloor;

            // Before the gate decides anything, and on the raw level — the gate,
            // the floor and the stats all keep measuring the room, and only the
            // bytes that leave through Emit are amplified.
            TrackInputGain(frame, rms);

            // Server VAD owns endpointing: upload everything the assistant isn't
            // covering and let the server find the boundaries. The gate is still
            // opened/closed so activity events and the utterance stats keep
            // working, it just never withholds audio.
            if (UploadContinuously)
            {
                if (!gateOpen) OpenGate();
                TrackUtteranceStats(rms);
                Emit(frame);
                return;
            }

            if (!gateOpen)
            {
                // Always banking means the frames that opened the gate are
                // already in the ring when it opens, and go up in order.
                Bank(frame);
                onsetFrames = loud ? onsetFrames + 1 : 0;
                if (onsetFrames >= OnsetFrames)
                {
                    OpenGate();
                    return;
                }
                Interlocked.Increment(ref framesDroppedSilent);
                return;
            }

            TrackUtteranceStats(rms);

            if (loud)
            {
                silentFrames = 0;
            }
            else if (++silentFrames >= HangoverFrames)
            {
                CloseGate();
                Interlocked.Increment(ref framesDroppedSilent);
                return;
            }

            Emit(frame);
        }

        // One place, called from both the gated and the continuous path. These
        // used to be two identical copies at different indentation, which is
        // exactly how a field got added to one of them and not the other.
        private void TrackUtteranceStats(double rms)
        {
            if (rms > utterancePeak) utterancePeak = rms;
            utteranceSum += rms;
            utteranceFrames++;

            if (rms < AdaptFloor) return;
            utteranceSpeechSum += rms;
            utteranceSpeechGainSum += inputGain;
            utteranceSpeechFrames++;
        }

        // The level above which a frame is treated as speech for measurement and
        // for gain adaptation — NOT the upload floor, which sits high enough to
        // cut into quiet speech. Derived from ambient so a noisy room raises the
        // bar rather than having its noise amplified.
        private double AdaptFloor =>
            Math.Max(ambientFloor * GainAdaptRatio, GainAdaptMinimum);

        // True if assistant audio (or the tail of it still coming out of the
        // speakers) covers this frame. Decrements the tail, so it must be called
        // exactly once per frame.
        private bool ConsumeAssistantAudioFrame()
        {
            if (assistantSpeaking || externalAudio) return true;
            int remaining = Volatile.Read(ref echoTail);
            if (remaining <= 0) return false;
            Volatile.Write(ref echoTail, remaining - 1);
            return true;
        }

        private void OpenGate()
        {
            gateOpen = true;
            onsetFrames = 0;
            silentFrames = 0;
            utterancePeak = 0;
            utteranceSum = 0;
            utteranceFrames = 0;
            utteranceSpeechSum = 0;
            utteranceSpeechGainSum = 0;
            utteranceSpeechFrames = 0;
            Raise(UploadGateOpened, nameof(UploadGateOpened));

            // Flush after the event so the session has already opened its
            // activity window by the time the onset arrives.
            while (preRoll.Count > 0)
            {
                Emit(preRoll.Dequeue());
            }
            preRollBytes = 0;
        }

        private void CloseGate()
        {
            // Signal level for the utterance just uploaded. Transcription quality
            // is mostly a function of how loud the speech actually was relative to
            // the room, and that is not something you can judge by ear from the
            // other side of the mic — so measure it rather than guess.
            //
            // Rough reading: peak below ~0.05 is a quiet mic and will garble
            // similar-sounding words; a peak-to-ambient ratio under ~10x means the
            // room is competing with the speech.
            // Under continuous upload the gate tracks assistant-audio boundaries,
            // not utterances, so most of these segments are the silence while the
            // assistant talks — reporting them as "utterance peak=0.0000" is
            // noise that says nothing about the microphone. Only report segments
            // that actually contain sound.
            bool worthReporting = utteranceFrames > 0 &&
                (!UploadContinuously || utterancePeak >= UploadFloor);

            if (worthReporting)
            {
                double mean = utteranceSum / utteranceFrames;
                double headroom = ambientFloor > 0 ? utterancePeak / ambientFloor : 0;

                int voiced = utteranceSpeechFrames;
                double speech = voiced > 0 ? utteranceSpeechSum / voiced : 0;
                double gain = voiced > 0 ? utteranceSpeechGainSum / voiced : 1.0;
                double duty = 100.0 * voiced / utteranceFrames;

                // `speech` and `out` are the numbers to compare between a good
                // transcript and a bad one. `mean` is NOT comparable across
                // utterances under continuous upload — it falls simply because a
                // segment held more silence, which says nothing about how loud
                // anyone was. `out` is what the model actually received, and it
                // is what LiveInputTarget aims at.
                Console.WriteLine(
                    $"[live-audio] utterance {utteranceFrames * BufferMs}ms  " +
                    $"peak={utterancePeak:F4} mean={mean:F4} " +
                    $"speech={speech:F4} voiced={duty:F0}% " +
                    $"gain={gain:F2}x out={speech * gain:F4} " +
                    $"ambient={ambientFloor:F4} floor={UploadFloor:F4} " +
                    $"headroom={headroom:F0}x hangover={HangoverMs}ms");
            }

            gateOpen = false;
            onsetFrames = 0;
            silentFrames = 0;
            Raise(UploadGateClosed, nameof(UploadGateClosed));
        }

        private void Bank(byte[] frame)
        {
            preRoll.Enqueue(frame);
            preRollBytes += frame.Length;
            while (preRollBytes > PreRollBytes && preRoll.Count > 0)
            {
                preRollBytes -= preRoll.Dequeue().Length;
            }
        }

        private void DropPreRoll()
        {
            preRoll.Clear();
            preRollBytes = 0;
            onsetFrames = 0;
        }

        // The one exit from this class, which is why the gain is applied here:
        // the gated path, the continuous path and the pre-roll flush all pass
        // through it, and a banked frame is emitted exactly once so scaling it in
        // place cannot double up.
        private void Emit(byte[] frame)
        {
            ApplyInputGain(frame);

            Interlocked.Increment(ref framesUploaded);
            Interlocked.Add(ref bytesUploaded, frame.Length);
            var handler = FrameReady;
            if (handler == null) return;
            try { handler(frame); }
            catch (Exception ex) { Console.WriteLine("[live-audio] FrameReady handler: " + ex.Message); }
        }

        // Updates the level estimate and the working gain. Runs on every frame so
        // the peak envelope keeps decaying, but only ADAPTS on speech — see the
        // input-gain notes at the top of the class for why holding through
        // silence is the load-bearing half of this.
        private void TrackInputGain(byte[] frame, double rms)
        {
            if (GainSetting > 0) return;   // pinned by config; nothing to adapt

            double framePeak = FramePeak(frame);
            peakEnvelope = Math.Max(framePeak, peakEnvelope * PeakDecay);

            // Its own threshold, not the upload floor — see GainAdaptRatio.
            if (rms < AdaptFloor) return;

            // Symmetric, so this tracks the MEAN of the frame-RMS spread — the
            // same statistic GainTarget was measured in. See the notes above for
            // why it does not need an asymmetric attack.
            speechRms = speechRms <= 0
                ? rms
                : speechRms + SpeechRmsAlpha * (rms - speechRms);

            if (gainWarmup < GainWarmupFrames) { gainWarmup++; return; }

            double wanted = MaxAutoGain;
            if (speechRms > 0) wanted = Math.Min(wanted, GainTarget / speechRms);
            if (peakEnvelope > 0) wanted = Math.Min(wanted, PeakCeiling / peakEnvelope);

            // Never attenuate. Nothing observed on this mic comes close to full
            // scale, so turning speech DOWN could only ever lose information —
            // this is makeup gain, not a compressor.
            if (wanted < 1.0) wanted = 1.0;

            inputGain += (wanted < inputGain ? GainFall : GainRise) * (wanted - inputGain);
        }

        // Scales in place, clamping at full scale. The clamp should almost never
        // fire — PeakCeiling exists so the gain is already low enough — but a
        // transient that outruns the envelope has to be limited rather than
        // wrapped, because a wrapped sample is a click and a click is something
        // the model hears as a consonant.
        private void ApplyInputGain(byte[] frame)
        {
            double gain = inputGain;
            if (gain <= 1.0001 || frame == null) return;

            int n = frame.Length / 2;
            for (int i = 0; i < n; i++)
            {
                int s = (short)(frame[2 * i] | (frame[2 * i + 1] << 8));
                int scaled = (int)(s * gain);
                if (scaled > short.MaxValue) scaled = short.MaxValue;
                else if (scaled < short.MinValue) scaled = short.MinValue;

                frame[2 * i] = (byte)(scaled & 0xFF);
                frame[2 * i + 1] = (byte)((scaled >> 8) & 0xFF);
            }
        }

        // The room's noise floor. Seeded as a minimum over the first second, then
        // min-tracked with a slow rise so a room that genuinely gets noisier is
        // followed instead of leaving the gate stuck open.
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

        private static void Raise(Action handler, string name)
        {
            if (handler == null) return;
            try { handler(); }
            catch (Exception ex) { Console.WriteLine($"[live-audio] {name} handler: " + ex.Message); }
        }

        // RMS of one 16-bit mono frame, 0..1.
        internal static double FrameRms(byte[] pcm)
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

        // Largest absolute sample in one frame, 0..1. The gain stage caps on this
        // rather than on RMS because speech has a crest factor around 4x — gain
        // enough to put the MEAN on target would drive the loudest syllables past
        // full scale.
        internal static double FramePeak(byte[] pcm)
        {
            if (pcm == null || pcm.Length < 2) return 0.0;
            int n = pcm.Length / 2;
            int peak = 0;
            for (int i = 0; i < n; i++)
            {
                int s = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8));
                if (s < 0) s = -s;
                if (s > peak) peak = s;
            }
            return peak / 32768.0;
        }


        public void Dispose()
        {
            disposed = true;
            Stop();
        }
    }

    // Playback of the model's 24 kHz PCM16, following local-laith's TTSClient
    // streaming shape: one BufferedWaveProvider fed as bytes arrive, so a reply
    // plays continuously instead of stopping and restarting per chunk.
    public sealed class LiveAudioPlayback : IDisposable
    {
        private static readonly WaveFormat PlaybackFormat = new WaveFormat(24000, 16, 1);

        // Deeper than the 100 ms a one-shot WAV needs: audio trickles in here,
        // and a shallow device buffer underruns audibly over Bluetooth.
        private const int DesiredLatencyMs = 250;

        // Bank this much before starting, so a gap in the WebSocket isn't heard
        // as a gap in the voice. Released early by EndAudioInput when the turn
        // is shorter than the lead.
        private static readonly TimeSpan PlaybackLead = TimeSpan.FromMilliseconds(150);

        // Raised when the audio is AUDIBLE — when the device reports it has
        // actually played bytes, which is a device buffer later than Play().
        // Anchoring this to "bytes were enqueued" or "the turn began" is the
        // specific mistake that had local-laith calibrating against silence: a
        // reply starts playing 1-2 s after its turn opens, and playback precedes
        // sound at the speaker by another buffer.
        public event Action PlaybackStarted;

        // Raised when everything enqueued has left the device, or immediately on
        // Flush(). Either way it is what reopens the microphone gate, so it must
        // fire on BOTH paths — a Flush that skipped it would wedge the mic shut.
        public event Action PlaybackFinished;

        private readonly object gate = new object();
        private BufferedWaveProvider buffer;
        private WaveOutEvent output;
        private CancellationTokenSource monitorCts;
        private bool playing;
        private bool audible;
        private bool inputEnded;
        private long bytesEnqueued;
        private volatile bool disposed;

        public bool IsPlaying { get { lock (gate) { return output != null; } } }

        // Bytes still waiting to go to the device. Zero does NOT mean the room is
        // silent — the driver holds ~DesiredLatency more — but it does mean this
        // turn has stopped producing sound, which is the question the stalled-turn
        // watchdog actually needs answered. "No audio has ARRIVED for a while" is
        // a different question, and for a reply that downloads faster than it
        // plays the two disagree by however much is buffered.
        public int PendingBytes
        {
            get { lock (gate) { return buffer == null ? 0 : buffer.BufferedBytes; } }
        }

        // 24 kHz mono PCM16, straight off the wire.
        //
        // Returns false when the bytes were NOT accepted — no output device, or
        // a rejected AddSamples. That return value is load-bearing rather than
        // informational: PlaybackFinished is what reopens the microphone gate,
        // and it can only ever fire for audio this method took. A caller that
        // shut the gate for audio that was refused has to reopen it itself, or
        // the session goes deaf for the rest of its life. See
        // LiveAudioPipeline.EnqueueAssistantAudio.
        public bool Enqueue(byte[] pcm24)
        {
            if (pcm24 == null || pcm24.Length == 0) return false;
            return Enqueue(pcm24, 0, pcm24.Length);
        }

        public bool Enqueue(byte[] pcm24, int offset, int count)
        {
            if (disposed || pcm24 == null || count <= 0) return false;
            lock (gate)
            {
                EnsureOutput();
                if (output == null) return false;
                inputEnded = false;
                try { buffer.AddSamples(pcm24, offset, count); }
                catch (Exception ex)
                {
                    Console.WriteLine("[live-audio] playback enqueue failed: " + ex.Message);
                    return false;
                }
                // Counted only once the bytes are really in, or a rejected
                // AddSamples would leave the drain test permanently unsatisfied.
                bytesEnqueued += count;
                MaybeStart();
                return true;
            }
        }

        // The model's turn is over; nothing more is coming for it. Releases any
        // audio still being held back for the lead.
        public void EndAudioInput()
        {
            lock (gate)
            {
                if (output == null) return;
                inputEnded = true;
                MaybeStart();
            }
        }

        // serverContent.interrupted: the model's turn was cut, so buffered output
        // must be dropped rather than played out after the fact. Mirrors
        // local-laith's StopSpeaking().
        public void Flush()
        {
            WaveOutEvent stopping;
            CancellationTokenSource cts;
            bool wasActive;
            lock (gate)
            {
                wasActive = output != null;
                stopping = output;
                cts = monitorCts;
                if (buffer != null) { try { buffer.ClearBuffer(); } catch { } }
                output = null;
                buffer = null;
                monitorCts = null;
                playing = false;
                audible = false;
                inputEnded = false;
                bytesEnqueued = 0;
            }
            if (cts != null) { try { cts.Cancel(); } catch { } try { cts.Dispose(); } catch { } }
            if (stopping != null)
            {
                try { stopping.Stop(); } catch { }
                try { stopping.Dispose(); } catch { }
            }
            if (wasActive) Raise(PlaybackFinished, nameof(PlaybackFinished));
        }

        // Wakes the output device with ~250 ms of silence. Bluetooth headphones,
        // wireless speakers and sleep-enabled DACs suppress the first ~200 ms
        // after a period of quiet, which clips the start of the greeting.
        //
        // Deliberately outside the turn machinery: it must not raise
        // PlaybackStarted, or Phase 3 would bracket a speaking turn around it.
        public async Task WarmUpAsync()
        {
            try
            {
                var silence = new byte[PlaybackFormat.AverageBytesPerSecond / 4];
                var provider = new BufferedWaveProvider(PlaybackFormat)
                {
                    BufferDuration = TimeSpan.FromSeconds(1),
                    DiscardOnBufferOverflow = true,
                };
                provider.AddSamples(silence, 0, silence.Length);
                using (var warm = new WaveOutEvent { DesiredLatency = DesiredLatencyMs })
                {
                    warm.Init(provider);
                    warm.Play();
                    await Task.Delay(400).ConfigureAwait(false);
                    try { warm.Stop(); } catch { }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[live-audio] output warm-up failed (non-fatal): " + ex.Message);
            }
        }

        private void EnsureOutput()
        {
            if (output != null) return;
            try
            {
                buffer = new BufferedWaveProvider(PlaybackFormat)
                {
                    BufferDuration = TimeSpan.FromMinutes(5),
                    DiscardOnBufferOverflow = false,
                };
                output = new WaveOutEvent
                {
                    DesiredLatency = DesiredLatencyMs,
                    NumberOfBuffers = 3,
                };
                output.Init(buffer);
                playing = false;
                audible = false;
                inputEnded = false;
                bytesEnqueued = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[live-audio] playback init failed: " + ex.Message);
                output = null;
                buffer = null;
            }
        }

        // Caller holds the lock.
        private void MaybeStart()
        {
            if (playing || output == null) return;
            if (buffer.BufferedDuration < PlaybackLead && !inputEnded) return;
            try { output.Play(); }
            catch (Exception ex)
            {
                Console.WriteLine("[live-audio] playback start failed: " + ex.Message);
                return;
            }
            playing = true;
            monitorCts = new CancellationTokenSource();
            var token = monitorCts.Token;
            Task.Run(() => MonitorAsync(token));
        }

        // Polls the device rather than trusting the buffer, because the two
        // questions this answers — "has sound started?" and "has the last sample
        // left?" — are both about the device, and BufferedBytes hits zero while
        // the driver still holds ~DesiredLatency of audio.
        private async Task MonitorAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    bool nowAudible = false;
                    bool drained = false;
                    lock (gate)
                    {
                        if (output == null) return;
                        long position = 0;
                        try { position = output.GetPosition(); } catch { }

                        if (!audible && position > 0)
                        {
                            audible = true;
                            nowAudible = true;
                        }

                        // ReadFully keeps the provider handing out silence once
                        // it empties, so position runs past what we enqueued —
                        // which is exactly the signal that our last real sample
                        // has been played out.
                        drained = audible
                            && inputEnded
                            && buffer.BufferedBytes == 0
                            && position >= bytesEnqueued;
                    }

                    if (nowAudible) Raise(PlaybackStarted, nameof(PlaybackStarted));
                    if (drained) { Finish(); return; }

                    await Task.Delay(15, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine("[live-audio] playback monitor failed: " + ex.Message);
            }
        }

        private void Finish()
        {
            WaveOutEvent stopping;
            CancellationTokenSource cts;
            lock (gate)
            {
                stopping = output;
                cts = monitorCts;
                output = null;
                buffer = null;
                monitorCts = null;
                playing = false;
                audible = false;
                inputEnded = false;
                bytesEnqueued = 0;
            }
            if (stopping != null)
            {
                try { stopping.Stop(); } catch { }
                try { stopping.Dispose(); } catch { }
            }
            if (cts != null) { try { cts.Dispose(); } catch { } }
            Raise(PlaybackFinished, nameof(PlaybackFinished));
        }

        private static void Raise(Action handler, string name)
        {
            if (handler == null) return;
            try { handler(); }
            catch (Exception ex) { Console.WriteLine($"[live-audio] {name} handler: " + ex.Message); }
        }

        public void Dispose()
        {
            disposed = true;
            Flush();
        }
    }

    // Capture and playback wired half-duplex. This is the surface Phase 3
    // consumes: it hooks FrameReady to SendAudioAsync and the gate transitions to
    // activityStart/activityEnd.
    //
    // NOTE, corrected 2026-08-07: this comment used to claim it "routes
    // Begin/EndAssistantAudio through SpeechService.BeginSpeaking/EndSpeaking"
    // and that "every path that produces assistant audio goes through that
    // bracket". Neither was true — nothing here referenced SpeechService, and
    // LiveSession never took sayGate — so a timer or announcement firing during a
    // conversation played over the reply AND into the open microphone.
    //
    // The mic gate now really does close for both, but the wiring is the other
    // way round from what that claim described: LiveSession subscribes to
    // SpeechService.AssistantSpeechStarted/Ended and calls
    // Capture.Begin/EndExternalAudio. This class still knows nothing about
    // SpeechService, which is what keeps it testable without Azure.
    public sealed class LiveAudioPipeline : IDisposable
    {
        public LiveAudioCapture Capture { get; }
        public LiveAudioPlayback Playback { get; }

        public LiveAudioPipeline()
            : this(new LiveAudioCapture(), new LiveAudioPlayback())
        {
        }

        internal LiveAudioPipeline(LiveAudioCapture capture, LiveAudioPlayback playback)
        {
            Capture = capture;
            Playback = playback;
            Playback.PlaybackFinished += Capture.EndAssistantAudio;
        }

        // One frame of model audio. Closing the mic gate here rather than on
        // PlaybackStarted is deliberate: the gate must already be shut before the
        // first sound reaches the room.
        //
        // If playback REFUSES the frame there is no sound coming, so the gate has
        // to be reopened here — nothing else would. The only thing that normally
        // reopens it is PlaybackFinished, which the playback side can only raise
        // for audio it accepted, and every downstream rescue routes through the
        // same dead end: the watchdog's stall path calls EndAudioInput, which
        // returns immediately when there is no output device. So an output device
        // that fails to open (in use, none default, a Bluetooth drop) used to
        // latch the microphone shut, keep `userHasTheFloor` false so the idle
        // clock never advanced, and run the whole conversation to the 600s hard
        // cap in silence.
        public void EnqueueAssistantAudio(byte[] pcm24)
        {
            Capture.BeginAssistantAudio();
            if (!Playback.Enqueue(pcm24)) Capture.EndAssistantAudio();
        }

        public void EndAssistantAudio() => Playback.EndAudioInput();

        // serverContent.interrupted — drop what hasn't played and reopen the mic.
        public void Interrupt() => Playback.Flush();

        // Capture first, then warm up: the warm-up is silence, so it can't
        // pollute the room-tone measurement, and starting the mic first gives the
        // ambient floor its full seeding second while the device wakes.
        public async Task StartAsync()
        {
            Capture.Start();
            await Playback.WarmUpAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            try { Playback.PlaybackFinished -= Capture.EndAssistantAudio; } catch { }
            Capture.Dispose();
            Playback.Dispose();
        }
    }
}
