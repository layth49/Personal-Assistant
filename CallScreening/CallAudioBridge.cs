using Personal_Assistant.Configuration;
using NAudio.CoreAudioApi;
// AudioSessionState lives under Interfaces, unlike the rest of CoreAudioApi.
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Personal_Assistant.CallScreening
{
    /// <summary>
    /// The two audio legs of a screened call.
    ///
    /// Inbound: WASAPI <b>loopback</b> capture on the endpoint the call is played
    /// into (the machine's default render device on the Google Voice path),
    /// resampled to 16 kHz mono PCM16 and cut into 20 ms frames.
    ///
    /// Outbound: 16 kHz mono PCM16 rendered to <c>CABLE Input</c>, which VB-CABLE
    /// presents to the browser as <c>CABLE Output</c> — the capture device the
    /// call is using as its microphone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHAT THIS FILE IS, ON THIS BRANCH. Upstream this class also held
    /// <c>CallAudioFormat</c>, <c>MonoResampler</c>, <c>CableWriter</c> and the two
    /// preflight primitives. All five landed here first, in
    /// <see cref="CallAudioProbe"/>/<c>CallAudioPreflight.cs</c>, because
    /// <see cref="CallAudioRouter"/> needed them a commit before the conversation
    /// half existed. So this file is what remains: the live streaming path only. It
    /// borrows <c>CallAudioProbe.CableWriter</c> rather than declaring a second
    /// copy — two encoders that could drift is exactly the fault a silent leg is
    /// hardest to diagnose from.
    /// </para>
    /// <para>
    /// The other departure from upstream is <see cref="SendPcmAsync"/>. There the
    /// only thing ever spoken down the line was audio that arrived from a socket,
    /// and the greeting was the one WAV on disk. Here every reply is WAV bytes that
    /// Kokoro just synthesised and nothing wrote to a file, so the paced writer is
    /// split out of <see cref="SendWavAsync"/> and both go through it.
    /// </para>
    /// <para>
    /// Everything here targets endpoints by WASAPI device id, never by WinMM
    /// index. <c>WaveOut</c>/<c>WaveIn</c> select by index against names WinMM
    /// truncates to 31 characters, which clips
    /// <c>CABLE Input (VB-Audio Virtual Cable)</c> to something that no longer
    /// matches what the config says — and picking the wrong virtual device on a
    /// machine that also has Voicemod and Steam ones fails silently, because
    /// silence is also what success sounds like from the outside.
    /// </para>
    /// </remarks>
    public sealed class CallAudioBridge : IDisposable
    {
        private readonly string monitorEndpointId;
        private readonly string cableEndpointId;

        private readonly object gate = new object();

        private MMDevice monitorDevice;
        private WasapiLoopbackCapture loopback;
        private MonoResampler downsampler;
        private WaveFormat captureFormat;

        private CallAudioProbe.CableWriter writer;

        // Captured audio waiting to be cut into 20 ms frames. Written by the WASAPI
        // callback, drained by the pacer.
        private readonly Queue<byte[]> captured = new Queue<byte[]>();
        private byte[] partial;
        private int partialAt;

        private Timer pacer;
        private readonly System.Diagnostics.Stopwatch uptime = new System.Diagnostics.Stopwatch();

        private long framesEmitted;
        private long silenceFrames;
        private double inboundRms;
        private double inboundGain = 1.0;

        // Tuned for a phone line, not for this machine's microphone. 16 kHz audio
        // at a mean of ~0.05 is what the everyday mic path settled on as reliably
        // transcribable, so the target matches; the cap is higher because the
        // starting point is far quieter than a room mic, and the floor is low for
        // the same reason — a 0.0001 line would never clear a floor set for a
        // microphone.
        private const double GainTarget = 0.05;

        // MEASURED ON THIS LINE UPSTREAM, and the first values were badly wrong.
        //
        // A real call (2026-08-17) reported silence at 0.0001–0.0015 and speech at
        // 0.0387. The floor was 0.0002 — below the room noise — so the stage
        // adapted on NOISE, wound itself to the 24x cap through every pause, and
        // then clamped the first real word flat at 0.0387 x 24. The model
        // transcribed the caller correctly and told him the line was choppy in the
        // same breath, which is what heavy clipping sounds like from the far end.
        //
        // The floor now sits between the two measured populations, so a pause
        // holds the gain where speech last put it instead of winding it up. And
        // since speech at 0.0387 needs about 1.3x to reach target, a 24x ceiling
        // was never buying anything except headroom to distort with; 8x matches
        // what the everyday mic stage allows itself.
        //
        // These are the numbers a cloud model was fed. Parakeet is a different
        // recogniser and may want a different level — but the failure they encode
        // (an AGC floor UNDER the room noise) is a property of the line, not of
        // the recogniser, so they are the right starting point rather than a
        // number to re-derive from nothing.
        private const double GainFloor = 0.008;
        private const double MaxGain = 8.0;
        private const double GainStep = 0.15;
        private volatile bool running;
        private volatile bool disposed;

        /// <summary>
        /// One 20 ms / 640-byte frame of 16 kHz mono PCM16 from the caller,
        /// EVERY 20 ms, padded with digital silence when the caller is quiet.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The unbroken cadence is not tidiness — it is what makes a conversation
        /// possible at all, and its absence produced a call that could listen and
        /// never answer.
        /// </para>
        /// <para>
        /// WASAPI loopback delivers a packet only while something is rendering to
        /// the endpoint. Not silent packets: NOTHING. So when the caller stopped
        /// talking the stream simply stopped, mid-utterance. Upstream that starved
        /// a server-side VAD which ends a turn by HEARING silence rather than by
        /// noticing an absence of packets, and the model waited out the whole call
        /// cap for an end of turn that could never arrive.
        /// </para>
        /// <para>
        /// It matters at least as much here, where the endpointing is local and in
        /// <see cref="CallSession"/>: a detector that measures "how long has it
        /// been quiet" from the frames it is handed can only ever conclude the
        /// caller is still talking if the frames stop arriving when they stop
        /// talking. Silence has to be DELIVERED, not inferred.
        /// </para>
        /// <para>
        /// Raised on a timer thread. Handlers must hand off and return: a handler
        /// that blocks here delays every later frame.
        /// </para>
        /// </remarks>
        public event Action<byte[]> FrameCaptured;

        public CallAudioBridge(string monitorEndpointId, string cableEndpointId)
        {
            if (string.IsNullOrWhiteSpace(monitorEndpointId))
                throw new ArgumentException("monitor endpoint id is required", nameof(monitorEndpointId));
            if (string.IsNullOrWhiteSpace(cableEndpointId))
                throw new ArgumentException("cable endpoint id is required", nameof(cableEndpointId));

            this.monitorEndpointId = monitorEndpointId;
            this.cableEndpointId = cableEndpointId;
        }

        public bool IsRunning => running;

        /// <summary>Level of the last inbound buffer, 0..1. Diagnostics only.</summary>
        public double InboundLevel { get { lock (gate) return inboundRms; } }

        /// <summary>
        /// The multiplier currently being applied to the caller's audio. Reported
        /// because a level reading alone cannot distinguish a quiet caller from a
        /// gain stage that has not moved.
        /// </summary>
        public double InboundGain { get { lock (gate) return inboundGain; } }

        public long FramesEmitted { get { lock (gate) return framesEmitted; } }

        /// <summary>
        /// How many emitted frames were padding rather than captured audio.
        /// </summary>
        /// <remarks>
        /// The drift test, now that the emitter is paced. A resampler that loses a
        /// fraction of a sample per callback runs slightly short of real time, and
        /// with a fixed 50 fps emitter that shortfall has to come from somewhere:
        /// it shows up here as padding creeping in WHILE the caller is talking
        /// continuously. Padding during silence is simply the feature working.
        /// </remarks>
        public long SilenceFrames { get { lock (gate) return silenceFrames; } }

        public void Start()
        {
            StartRecording();

            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(CallAudioBridge));
                if (running) return;

                monitorDevice = CallAudioProbe.Enumerate().GetDevice(monitorEndpointId);
                loopback = new WasapiLoopbackCapture(monitorDevice);

                // The mix format, not a format we asked for: loopback has no say.
                // 48 kHz stereo float here, which is exactly why the resampler is
                // mandatory rather than an optimisation.
                captureFormat = loopback.WaveFormat;
                downsampler = new MonoResampler(captureFormat.SampleRate, CallAudioFormat.GeminiRate);

                loopback.DataAvailable += OnData;
                loopback.RecordingStopped += OnRecordingStopped;

                writer = new CallAudioProbe.CableWriter(cableEndpointId);

                loopback.StartRecording();
                running = true;

                // Started after the capture, so the first tick has something to
                // drain if anything is already playing. Asked for half a frame
                // because the tick lands where the system clock allows, not where
                // it was asked to — the frames owed are worked out from `uptime`.
                framesEmitted = 0;
                silenceFrames = 0;
                uptime.Restart();
                pacer = new Timer(
                    Emit, null, CallAudioFormat.FrameMs / 2, CallAudioFormat.FrameMs / 2);

                Console.WriteLine(
                    $"[call-audio] bridge up: loopback {captureFormat.SampleRate}Hz " +
                    $"{captureFormat.Channels}ch {captureFormat.BitsPerSample}-bit -> " +
                    $"{CallAudioFormat.GeminiRate}Hz mono at {1000 / CallAudioFormat.FrameMs}fps, " +
                    $"out -> {writer.Describe()}");
            }
        }

        public void Stop()
        {
            // Closed here rather than in Dispose: Stop is what every path runs,
            // and a WAV whose header was never finalised will not open.
            StopRecording();

            WasapiLoopbackCapture stopping;
            CallAudioProbe.CableWriter closing;
            MMDevice device;
            Timer ticker;

            lock (gate)
            {
                if (!running && loopback == null) return;
                running = false;
                stopping = loopback;
                closing = writer;
                device = monitorDevice;
                ticker = pacer;
                loopback = null;
                writer = null;
                monitorDevice = null;
                pacer = null;
                captured.Clear();
                partial = null;
                partialAt = 0;
            }

            if (ticker != null) { try { ticker.Dispose(); } catch { } }
            uptime.Stop();

            if (stopping != null)
            {
                try { stopping.StopRecording(); } catch { }
                try { stopping.Dispose(); } catch { }
            }
            if (closing != null) { try { closing.Dispose(); } catch { } }
            if (device != null) { try { device.Dispose(); } catch { } }
        }

        /// <summary>
        /// Sends 16 kHz mono PCM16 to the caller. False when there is nowhere to
        /// send it, which the caller should treat as "the line is dead", not as a
        /// dropped buffer.
        /// </summary>
        public bool Send(byte[] pcm16Mono16k)
        {
            if (pcm16Mono16k == null || pcm16Mono16k.Length == 0) return false;

            CallAudioProbe.CableWriter target;
            lock (gate) target = writer;
            if (target == null) return false;

            try
            {
                target.Write(pcm16Mono16k, pcm16Mono16k.Length);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[call-audio] could not write to the cable: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Throws away outbound audio that has been queued but not yet played.
        /// </summary>
        /// <remarks>
        /// This is what makes barge-in real rather than cosmetic. The cable writer
        /// buffers up to ten seconds, and a synthesised sentence is written far
        /// faster than it plays out — so when the caller interrupts, everything
        /// already queued would otherwise keep talking over them for as long as
        /// the synthesis had run ahead.
        /// </remarks>
        public void ClearOutbound()
        {
            CallAudioProbe.CableWriter target;
            lock (gate) target = writer;
            if (target == null) return;
            try { target.Clear(); } catch { /* nothing to drop is not an error */ }
        }

        /// <summary>
        /// Waits until something is actively capturing <paramref name="captureEndpointId"/> —
        /// i.e. the call stack has opened our cable as its microphone.
        /// </summary>
        /// <returns>True if a consumer appeared, false if the wait ran out.</returns>
        //
        // WAIT FOR A LISTENER, DO NOT GUESS AT A DELAY.
        //
        // The caller reliably heard only the last two thirds of the greeting, and
        // a 2s run-up of silence did not fix it. It is not our end: the probe
        // captures the whole 5.4s file off the far side of the cable. The greeting
        // simply starts before anything downstream is reading.
        //
        // That moment is observable rather than guessable. An ACTIVE audio session
        // on the capture endpoint means somebody has the cable open and is reading
        // it, which is exactly the precondition the greeting needs.
        public static async Task<bool> WaitForCaptureConsumerAsync(
            string captureEndpointId, TimeSpan limit, CancellationToken cancel = default)
        {
            if (string.IsNullOrWhiteSpace(captureEndpointId)) return false;

            DateTime deadline = DateTime.Now.Add(limit);
            while (DateTime.Now < deadline && !cancel.IsCancellationRequested)
            {
                if (SomethingIsCapturing(captureEndpointId)) return true;
                try { await Task.Delay(100, cancel).ConfigureAwait(false); }
                catch (OperationCanceledException) { return false; }
            }
            return false;
        }

        private static bool SomethingIsCapturing(string captureEndpointId)
        {
            try
            {
                using (var enumerator = CallAudioProbe.Enumerate())
                using (MMDevice device = enumerator.GetDevice(captureEndpointId))
                {
                    SessionCollection sessions = device.AudioSessionManager.Sessions;
                    if (sessions == null) return false;

                    for (int i = 0; i < sessions.Count; i++)
                    {
                        if (sessions[i].State == AudioSessionState.AudioSessionStateActive)
                            return true;
                    }
                }
            }
            catch
            {
                // Never let a diagnostic query stop a greeting — the lead-in
                // silence is still there as the fallback.
                return false;
            }
            return false;
        }

        /// <summary>
        /// Pushes <paramref name="duration"/> of digital silence down the cable,
        /// in real time, to wake the path up before anything worth hearing.
        /// </summary>
        public async Task SendSilenceAsync(TimeSpan duration, CancellationToken cancel = default)
        {
            if (duration <= TimeSpan.Zero) return;

            const int chunkMs = 100;
            int chunkBytes = CallAudioFormat.GeminiRate / 1000 * chunkMs * 2;
            var silence = new byte[chunkBytes];

            for (double sent = 0; sent < duration.TotalMilliseconds; sent += chunkMs)
            {
                cancel.ThrowIfCancellationRequested();
                if (!Send(silence)) throw new InvalidOperationException("the outbound leg is not open.");
                await Task.Delay(chunkMs, cancel).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// How much audio is queued for the caller but not yet played.
        /// </summary>
        //
        // Exposed because "the reply has finished synthesising" and "the caller has
        // finished hearing it" are different moments, and the gap between them is
        // where a goodbye goes missing. CallSession also reads it as its
        // half-duplex gate: audio arriving on the inbound leg while this is
        // non-zero is, on any endpoint where the two legs can meet, at risk of
        // being our own voice.
        public TimeSpan OutboundPending
        {
            get
            {
                CallAudioProbe.CableWriter target;
                lock (gate) target = writer;
                return target?.Pending ?? TimeSpan.Zero;
            }
        }

        /// <summary>
        /// Waits until the caller has actually heard everything queued for them,
        /// or <paramref name="limit"/> passes.
        /// </summary>
        public async Task DrainOutboundAsync(TimeSpan limit, CancellationToken cancel = default)
        {
            DateTime deadline = DateTime.Now.Add(limit);
            while (DateTime.Now < deadline && !cancel.IsCancellationRequested)
            {
                if (OutboundPending <= TimeSpan.FromMilliseconds(60)) return;
                try { await Task.Delay(50, cancel).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>Plays a WAV file down the phone line — to the caller, not the speakers.</summary>
        public Task SendWavAsync(string path, CancellationToken cancel = default)
        {
            byte[] pcm = ReadWavAs16kMono(path);
            if (pcm.Length == 0)
                throw new InvalidOperationException($"'{path}' contained no audio.");
            return SendPcmAsync(pcm, cancel);
        }

        /// <summary>
        /// Plays WAV bytes that were never written to disk — one Kokoro synthesis —
        /// down the phone line.
        /// </summary>
        /// <remarks>
        /// The everyday reply path ends at a <c>WaveOutEvent</c> on the machine's
        /// DEFAULT output, which is the speakers. A screened call must never do
        /// that: its mouth is a virtual cable, and the caller hearing silence while
        /// Layth hears the reply is the exact failure upstream recorded. So the
        /// call path takes bytes from <c>KokoroTTSService.SynthesizeWavAsync</c>
        /// and routes them itself.
        /// </remarks>
        public Task SendWavBytesAsync(byte[] wav, CancellationToken cancel = default)
        {
            byte[] pcm = ReadWavBytesAs16kMono(wav);
            if (pcm.Length == 0) return Task.CompletedTask;
            return SendPcmAsync(pcm, cancel);
        }

        /// <summary>
        /// Writes 16 kHz mono PCM16 to the caller in paced chunks and returns once
        /// it has been heard.
        /// </summary>
        /// <remarks>
        /// Paced rather than written in one shot. The cable writer discards on
        /// overflow, so a clip longer than its buffer would lose its tail silently
        /// — and pacing is also what lets a cancel actually stop the audio, which
        /// matters when the caller interrupts or hangs up mid-sentence.
        /// </remarks>
        public async Task SendPcmAsync(byte[] pcm16Mono16k, CancellationToken cancel = default)
        {
            if (pcm16Mono16k == null || pcm16Mono16k.Length == 0) return;

            const int chunkMs = 200;
            int chunkBytes = CallAudioFormat.GeminiRate / 1000 * chunkMs * 2;

            for (int at = 0; at < pcm16Mono16k.Length; at += chunkBytes)
            {
                cancel.ThrowIfCancellationRequested();

                int take = Math.Min(chunkBytes, pcm16Mono16k.Length - at);
                var chunk = new byte[take];
                Buffer.BlockCopy(pcm16Mono16k, at, chunk, 0, take);

                if (!Send(chunk))
                    throw new InvalidOperationException("the outbound leg is not open.");

                // Roughly real time, staying one chunk ahead so the driver never
                // runs dry between writes.
                await Task.Delay(chunkMs, cancel).ConfigureAwait(false);
            }

            // The last chunk is queued, not played. Returning here would let the
            // conversation start listening over the end of its own sentence.
            await Task.Delay(chunkMs + 250, cancel).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads a WAV into 16 kHz mono PCM16, whatever it was recorded at.
        /// </summary>
        public static byte[] ReadWavAs16kMono(string path)
        {
            using (var reader = new WaveFileReader(path)) return ReadAll(reader);
        }

        /// <summary>The same, for a WAV that only ever existed in memory.</summary>
        public static byte[] ReadWavBytesAs16kMono(byte[] wav)
        {
            if (wav == null || wav.Length == 0) return new byte[0];
            using (var stream = new MemoryStream(wav, writable: false))
            using (var reader = new WaveFileReader(stream)) return ReadAll(reader);
        }

        private static byte[] ReadAll(WaveFileReader reader)
        {
            WaveFormat format = reader.WaveFormat;

            var raw = new byte[reader.Length];
            int read = reader.Read(raw, 0, raw.Length);
            if (read <= 0) return new byte[0];

            byte[] pcm = CallAudioFormat.ToPcm16(raw, read, format);

            // The whole clip is in hand, which is the case the one-shot resampler
            // was written for — no phase to carry.
            return CallAudioFormat.Resample16BitMono(
                pcm, 0, pcm.Length, format.SampleRate, format.Channels,
                CallAudioFormat.GeminiRate);
        }

        /// <summary>
        /// Wraps 16 kHz mono PCM16 in a RIFF header, so the caller's utterance can
        /// be handed to the transcription endpoint as a file.
        /// </summary>
        /// <remarks>
        /// Built by hand rather than through <c>WaveFileWriter</c>: this runs once
        /// per caller turn on the latency-critical path, and a temp file per
        /// utterance would put the disk between the caller finishing a sentence
        /// and the recogniser starting on it.
        /// </remarks>
        public static byte[] ToWav(byte[] pcm16Mono16k)
        {
            int data = pcm16Mono16k?.Length ?? 0;
            var wav = new byte[44 + data];

            void Ascii(int at, string s) { for (int i = 0; i < s.Length; i++) wav[at + i] = (byte)s[i]; }
            void Int32At(int at, int v)
            {
                wav[at] = (byte)v; wav[at + 1] = (byte)(v >> 8);
                wav[at + 2] = (byte)(v >> 16); wav[at + 3] = (byte)(v >> 24);
            }
            void Int16At(int at, int v) { wav[at] = (byte)v; wav[at + 1] = (byte)(v >> 8); }

            const int rate = CallAudioFormat.GeminiRate;

            Ascii(0, "RIFF");
            Int32At(4, 36 + data);
            Ascii(8, "WAVE");
            Ascii(12, "fmt ");
            Int32At(16, 16);            // PCM chunk size
            Int16At(20, 1);             // PCM
            Int16At(22, 1);             // mono
            Int32At(24, rate);
            Int32At(28, rate * 2);      // byte rate
            Int16At(32, 2);             // block align
            Int16At(34, 16);            // bits
            Ascii(36, "data");
            Int32At(40, data);

            if (data > 0) Buffer.BlockCopy(pcm16Mono16k, 0, wav, 44, data);
            return wav;
        }

        private void OnData(object sender, WaveInEventArgs e)
        {
            // Loopback on an endpoint nothing is rendering to delivers nothing at
            // all — no packets, not silent ones. So "no data" is the normal state
            // of a quiet line, and it is the PACER, not this method, that keeps the
            // frame stream running through it.
            if (e.BytesRecorded <= 0) return;

            try
            {
                byte[] pcm = CallAudioFormat.ToPcm16(e.Buffer, e.BytesRecorded, captureFormat);
                short[] mono = CallAudioFormat.Downmix(pcm, pcm.Length, captureFormat.Channels);
                short[] resampled = downsampler.Process(mono, mono.Length);
                if (resampled.Length == 0) return;

                byte[] bytes = CallAudioFormat.ToBytes(resampled, resampled.Length);
                double level = CallAudioFormat.Rms(bytes);

                lock (gate)
                {
                    inboundRms = level;
                    captured.Enqueue(bytes);

                    // A backlog means the pacer is not keeping up, which cannot
                    // happen from a real-time source unless something stalled the
                    // timer thread. Dropping the oldest half-second keeps the call
                    // live and current rather than falling further behind; a call
                    // that is two seconds late is worse than one with a gap.
                    int queued = QueuedBytes();
                    if (queued <= MaxQueuedBytes) return;

                    while (QueuedBytes() > MaxQueuedBytes && captured.Count > 0) captured.Dequeue();
                    Console.WriteLine(
                        $"[call-audio] inbound backlog of {queued / 32} ms — dropped the oldest.");
                }
            }
            catch (Exception ex)
            {
                // A throw out of a WASAPI callback takes the process down with no
                // console output at all. One line beats a silent exit mid-call.
                Console.WriteLine($"[call-audio] inbound conversion failed: {ex.Message}");
            }
        }

        // Half a second, at 16 kHz mono PCM16.
        private const int MaxQueuedBytes = CallAudioFormat.GeminiRate * 2 / 2;

        private int QueuedBytes()
        {
            int total = partial == null ? 0 : partial.Length - partialAt;
            foreach (byte[] block in captured) total += block.Length;
            return total;
        }

        // How many frames a tick may emit at once. A second's worth: enough to
        // absorb a stalled thread pool, small enough that a much longer stall
        // resyncs to the present instead of replaying a burst of stale audio at a
        // caller who has moved on.
        private const int MaxFramesPerTick = 1000 / CallAudioFormat.FrameMs;

        // Frames are owed against a CLOCK, not counted per tick.
        //
        // A System.Threading.Timer asked for 20 ms fires on the system tick, which
        // is ~15.6 ms by default and therefore lands at ~31 ms — so one frame per
        // tick is about 32 fps against a source producing 50. Measured upstream
        // 2026-08-17: that ran a permanent half-second backlog and dropped the
        // oldest audio every single tick while the caller was talking.
        //
        // Deriving the count from elapsed time makes the cadence real-time whatever
        // the timer does: a late tick emits the frames it owes, silence is padded
        // for the right DURATION rather than the right number of ticks, and the
        // queue drains instead of growing. The endpointer in CallSession counts
        // quiet frames, so this is also what makes its silence window mean
        // milliseconds rather than "however often the timer happened to fire".
        private void Emit(object _)
        {
            long due;
            lock (gate)
            {
                if (!running) return;
                due = (long)(uptime.Elapsed.TotalMilliseconds / CallAudioFormat.FrameMs);
            }

            for (int burst = 0; burst < MaxFramesPerTick; burst++)
            {
                byte[] frame;
                lock (gate)
                {
                    if (!running || framesEmitted >= due) return;
                    frame = NextFrame();
                }

                // GROUND TRUTH FOR "CAN IT ACTUALLY HEAR THE CALLER?".
                //
                // Written here and nowhere else, because this is the exact byte
                // stream the recogniser receives: post-resample, post-gain, 16 kHz
                // mono PCM16. Levels and transcripts have both been misleading, and
                // no number in a log settles what audio SOUNDS like. Off by
                // default; costs one config key and answers the question in one
                // call.
                Record(frame);

                Action<byte[]> handler = FrameCaptured;
                if (handler == null) continue;
                try { handler(frame); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[call-audio] frame handler threw: {ex.Message}");
                }
            }
        }

        private WaveFileWriter recorder;
        private readonly object recorderGate = new object();

        /// <summary>
        /// Starts capturing everything the recogniser is fed into a WAV, when
        /// <c>CallRecordInbound</c> names a folder. One file per call.
        /// </summary>
        public void StartRecording()
        {
            string dir = LaithConfig.Text("CallRecordInbound", "");
            if (string.IsNullOrWhiteSpace(dir)) return;

            try
            {
                Directory.CreateDirectory(dir);
                string path = Path.Combine(
                    dir, "inbound-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".wav");

                lock (recorderGate)
                {
                    recorder = new WaveFileWriter(
                        path, new WaveFormat(CallAudioFormat.GeminiRate, 16, 1));
                }

                Console.WriteLine("[call-audio] recording what the recogniser hears to " + path);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[call-audio] could not start the inbound recording: " + ex.Message);
            }
        }

        private void Record(byte[] frame)
        {
            lock (recorderGate)
            {
                if (recorder == null) return;
                try { recorder.Write(frame, 0, frame.Length); }
                catch { /* a diagnostic must never break a live call */ }
            }
        }

        private void StopRecording()
        {
            lock (recorderGate)
            {
                if (recorder == null) return;
                try { recorder.Dispose(); } catch { }
                recorder = null;
            }
        }

        // One 20 ms frame off the queue, padded with digital silence if the queue
        // runs dry. Caller holds the lock.
        private byte[] NextFrame()
        {
            var frame = new byte[CallAudioFormat.FrameBytes];
            int at = 0;
            bool anyRealAudio = false;

            while (at < frame.Length)
            {
                if (partial == null || partialAt >= partial.Length)
                {
                    if (captured.Count == 0) break;
                    partial = captured.Dequeue();
                    partialAt = 0;
                    continue;
                }

                int take = Math.Min(frame.Length - at, partial.Length - partialAt);
                Buffer.BlockCopy(partial, partialAt, frame, at, take);
                partialAt += take;
                at += take;
                anyRealAudio = true;
            }

            // The tail of the array is already zeroed, which IS 16-bit silence.
            framesEmitted++;
            if (!anyRealAudio) silenceFrames++;
            if (anyRealAudio) ApplyGain(frame);
            return frame;
        }

        // ADAPTIVE GAIN ON THE INBOUND LEG — the caller arrives far too quiet.
        //
        // Measured on a real screened call upstream (2026-08-17): `inbound level
        // 0.0001` for most of the call, and the transcript to match. A loopback
        // capture takes the endpoint's mix AFTER the volume slider, so the speakers
        // being at a civil volume is itself an attenuator — which is also why the
        // hush in CallScreeningService pins the volume up for the duration.
        //
        // The everyday microphone path has run adaptive gain for the same reason
        // and with the same shape: aim the MEAN at a target, cap the multiplier,
        // and — the part that matters — only adapt on frames that contain speech.
        // An AGC that adapts on silence winds itself to maximum during a pause and
        // then clips the first word after it, which is the fault the constants
        // above were re-measured to fix.
        //
        // Deliberately NOT shared with the mic's stage: that one is tuned against
        // this machine's microphone array and is wired into the utterance tracker.
        // Borrowing the reasoning is right; borrowing the constants would tune a
        // phone line to a room.
        private void ApplyGain(byte[] frame)
        {
            double mean = 0;
            int samples = frame.Length / 2;
            if (samples == 0) return;

            for (int i = 0; i < frame.Length; i += 2)
                mean += Math.Abs((short)(frame[i] | (frame[i + 1] << 8)));
            mean /= samples * 32768.0;

            // Adapt only on frames loud enough to be speech rather than line
            // noise, and move slowly enough that one loud syllable does not reset
            // the call's gain.
            if (mean > GainFloor)
            {
                double wanted = Math.Min(GainTarget / mean, MaxGain);
                inboundGain += (wanted - inboundGain) * GainStep;
                inboundGain = Math.Max(1.0, Math.Min(MaxGain, inboundGain));
            }

            if (inboundGain <= 1.0001) return;

            for (int i = 0; i < frame.Length; i += 2)
            {
                int scaled = (int)((short)(frame[i] | (frame[i + 1] << 8)) * inboundGain);

                // Clamp rather than let it wrap. A wrapped sample is a click, and
                // a click every few frames reads as a broken line, not a loud one.
                if (scaled > short.MaxValue) scaled = short.MaxValue;
                else if (scaled < short.MinValue) scaled = short.MinValue;

                frame[i] = (byte)(scaled & 0xFF);
                frame[i + 1] = (byte)((scaled >> 8) & 0xFF);
            }
        }

        private void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            if (e?.Exception == null) return;

            // The endpoint disappearing mid-call is the everyday breakage this
            // topology has: the monitor is switched off and its endpoint goes
            // inactive, taking the inbound leg with it.
            Console.WriteLine($"[call-audio] loopback capture stopped: {e.Exception.Message}");
            running = false;
        }

        public void Dispose()
        {
            disposed = true;
            Stop();
        }
    }
}
