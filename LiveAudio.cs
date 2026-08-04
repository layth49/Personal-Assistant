using NAudio.Wave;
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

        // How long the gate stays open after the level drops. This doubles as
        // the endpoint Phase 3 hangs activityEnd on, so it has to survive an
        // ordinary pause mid-sentence. 800 ms is local-laith's measured value
        // (bakeoff/stt/tail_sweep.py) rather than a guess.
        private const int HangoverMs = 800;
        private const int HangoverFrames = HangoverMs / BufferMs;

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
        // nothing gates on these.
        private double utterancePeak;
        private double utteranceSum;
        private int utteranceFrames;

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

        public LiveAudioCapture()
        {
            floorOverride = ReadDouble("LAITH_LIVE_UPLOAD_FLOOR", 0.0);
            if (floorOverride > 0)
            {
                Console.WriteLine(
                    $"[live-audio] upload floor pinned to {floorOverride:F4} by LAITH_LIVE_UPLOAD_FLOOR");
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

            if (rms > utterancePeak) utterancePeak = rms;
            utteranceSum += rms;
            utteranceFrames++;

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

        // True if assistant audio (or the tail of it still coming out of the
        // speakers) covers this frame. Decrements the tail, so it must be called
        // exactly once per frame.
        private bool ConsumeAssistantAudioFrame()
        {
            if (assistantSpeaking) return true;
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
            if (utteranceFrames > 0)
            {
                double mean = utteranceSum / utteranceFrames;
                double headroom = ambientFloor > 0 ? utterancePeak / ambientFloor : 0;
                Console.WriteLine(
                    $"[live-audio] utterance {utteranceFrames * BufferMs}ms  " +
                    $"peak={utterancePeak:F4} mean={mean:F4} " +
                    $"ambient={ambientFloor:F4} floor={UploadFloor:F4} " +
                    $"headroom={headroom:F0}x");
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

        private void Emit(byte[] frame)
        {
            Interlocked.Increment(ref framesUploaded);
            Interlocked.Add(ref bytesUploaded, frame.Length);
            var handler = FrameReady;
            if (handler == null) return;
            try { handler(frame); }
            catch (Exception ex) { Console.WriteLine("[live-audio] FrameReady handler: " + ex.Message); }
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

        // 24 kHz mono PCM16, straight off the wire.
        public void Enqueue(byte[] pcm24)
        {
            if (pcm24 == null || pcm24.Length == 0) return;
            Enqueue(pcm24, 0, pcm24.Length);
        }

        public void Enqueue(byte[] pcm24, int offset, int count)
        {
            if (disposed || pcm24 == null || count <= 0) return;
            lock (gate)
            {
                EnsureOutput();
                if (output == null) return;
                inputEnded = false;
                try { buffer.AddSamples(pcm24, offset, count); }
                catch (Exception ex)
                {
                    Console.WriteLine("[live-audio] playback enqueue failed: " + ex.Message);
                    return;
                }
                // Counted only once the bytes are really in, or a rejected
                // AddSamples would leave the drain test permanently unsatisfied.
                bytesEnqueued += count;
                MaybeStart();
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
    // consumes: it hooks FrameReady to SendAudioAsync, the gate transitions to
    // activityStart/activityEnd, and routes Begin/EndAssistantAudio through
    // SpeechService.BeginSpeaking/EndSpeaking — there is exactly one
    // SpeechService (SpeechService.Current) and every path that produces
    // assistant audio goes through that bracket.
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
        public void EnqueueAssistantAudio(byte[] pcm24)
        {
            Capture.BeginAssistantAudio();
            Playback.Enqueue(pcm24);
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
