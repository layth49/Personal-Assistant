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
    // Sample-format plumbing for the two call legs.
    //
    // Both legs need conversion because neither end gets to pick its format.
    // WASAPI hands us the endpoint's *mix* format on the way in — 48 kHz stereo
    // 32-bit float on this machine, and whatever the driver says elsewhere — and
    // Gemini Live wants 16 kHz mono PCM16. Nothing negotiates; the conversion is
    // the interface.
    public static class CallAudioFormat
    {
        public const int GeminiRate = 16000;
        public const int FrameMs = 20;

        // 320 samples of 16-bit mono = 640 bytes. The frame size Gemini Live is
        // fed in; the accumulator in CallAudioBridge exists to guarantee it,
        // because WASAPI callback sizes have nothing to do with it.
        public const int FrameBytes = GeminiRate / 1000 * FrameMs * 2;

        /// <summary>
        /// Linear-interpolating resampler with channel downmix, for a clip that is
        /// entirely in hand.
        /// </summary>
        /// <remarks>
        /// Promoted verbatim from <c>LiveTextModeCheck.Resample16BitMono</c>
        /// (bakeoff/calltext), where it was exercised on real 24 kHz speech before
        /// this feature existed. Kept for whole-clip work — preflight capture, the
        /// harness — where every sample is available at once.
        ///
        /// The live capture leg deliberately does NOT call this in a loop. Each
        /// call starts its read head at zero and truncates <c>frames / ratio</c>,
        /// so per-callback use throws away up to one output sample every callback:
        /// at ~100 callbacks a second that is a drift of roughly half a percent,
        /// not a click, which is the kind of fault you find three phases later.
        /// <see cref="MonoResampler"/> carries the phase across buffers instead.
        /// </remarks>
        public static byte[] Resample16BitMono(
            byte[] src, int offset, int length, int sourceRate, int channels, int targetRate)
        {
            int frames = length / (2 * channels);
            if (frames == 0) return new byte[0];

            var mono = new short[frames];
            for (int i = 0; i < frames; i++)
            {
                int sum = 0;
                for (int c = 0; c < channels; c++)
                    sum += BitConverter.ToInt16(src, offset + (i * channels + c) * 2);
                mono[i] = (short)(sum / channels);
            }

            if (sourceRate == targetRate)
            {
                var same = new byte[frames * 2];
                Buffer.BlockCopy(mono, 0, same, 0, same.Length);
                return same;
            }

            double ratio = (double)sourceRate / targetRate;
            int outFrames = (int)(frames / ratio);
            var outBytes = new byte[outFrames * 2];

            for (int i = 0; i < outFrames; i++)
            {
                double exact = i * ratio;
                int a = (int)exact;
                int b = Math.Min(a + 1, frames - 1);
                double t = exact - a;
                short value = (short)(mono[a] * (1 - t) + mono[b] * t);
                outBytes[i * 2] = (byte)(value & 0xFF);
                outBytes[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
            }

            return outBytes;
        }

        // A mix format arrives as WAVE_FORMAT_EXTENSIBLE, whose Encoding reads
        // "Extensible" rather than "IeeeFloat" — so a naive `Encoding == Pcm`
        // test decides wrong on every modern endpoint. Unwrap it where possible;
        // ToPcm16 falls back on bit depth where it isn't.
        public static WaveFormat Standardise(WaveFormat format)
        {
            var extensible = format as WaveFormatExtensible;
            if (extensible == null) return format;
            try { return extensible.ToStandardWaveFormat(); }
            catch { return format; }
        }

        /// <summary>
        /// Normalises a raw WASAPI buffer to interleaved 16-bit PCM, keeping the
        /// channel count and sample rate as they are.
        /// </summary>
        public static byte[] ToPcm16(byte[] raw, int count, WaveFormat format)
        {
            if (raw == null || count <= 0) return new byte[0];

            WaveFormat f = Standardise(format);
            int bits = f.BitsPerSample;

            // Extensible that would not unwrap: on Windows a 32-bit endpoint
            // format is float in every case that reaches here, and guessing PCM
            // would turn speech into full-scale noise rather than something
            // subtly wrong, which at least fails loudly.
            bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat ||
                           (bits == 32 && f.Encoding != WaveFormatEncoding.Pcm);

            if (bits == 16 && !isFloat)
            {
                var copy = new byte[count - (count % 2)];
                Buffer.BlockCopy(raw, 0, copy, 0, copy.Length);
                return copy;
            }

            int bytesPerSample = bits / 8;
            if (bytesPerSample <= 0) return new byte[0];

            int samples = count / bytesPerSample;
            var pcm = new byte[samples * 2];

            for (int i = 0; i < samples; i++)
            {
                int at = i * bytesPerSample;
                short value;

                if (isFloat)
                {
                    float sample = BitConverter.ToSingle(raw, at);
                    if (sample > 1f) sample = 1f;
                    else if (sample < -1f) sample = -1f;
                    value = (short)(sample * 32767f);
                }
                else if (bits == 32)
                {
                    value = (short)(BitConverter.ToInt32(raw, at) >> 16);
                }
                else if (bits == 24)
                {
                    value = (short)(raw[at + 1] | (raw[at + 2] << 8));
                }
                else
                {
                    // 8-bit endpoints do not happen on this path, but silence is
                    // a better answer than an index out of range mid-call.
                    value = 0;
                }

                pcm[i * 2] = (byte)(value & 0xFF);
                pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
            }

            return pcm;
        }

        // Interleaved PCM16 -> mono samples.
        public static short[] Downmix(byte[] pcm16, int count, int channels)
        {
            if (channels < 1) channels = 1;
            int frames = count / (2 * channels);
            var mono = new short[frames];
            for (int i = 0; i < frames; i++)
            {
                int sum = 0;
                for (int c = 0; c < channels; c++)
                    sum += BitConverter.ToInt16(pcm16, (i * channels + c) * 2);
                mono[i] = (short)(sum / channels);
            }
            return mono;
        }

        public static byte[] ToBytes(short[] samples, int count)
        {
            var bytes = new byte[count * 2];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        /// <summary>Level of a PCM16 buffer, 0..1. The whole preflight verdict.</summary>
        public static double Rms(byte[] pcm16, int offset, int count)
        {
            int frames = count / 2;
            if (frames == 0) return 0;

            double sum = 0;
            for (int i = 0; i < frames; i++)
            {
                double s = BitConverter.ToInt16(pcm16, offset + i * 2) / 32768.0;
                sum += s * s;
            }
            return Math.Sqrt(sum / frames);
        }

        public static double Rms(byte[] pcm16) => Rms(pcm16, 0, pcm16?.Length ?? 0);

        /// <summary>
        /// Spreads mono PCM16 across a device format's channels, converting the
        /// sample encoding. Rate conversion is the caller's job — it needs the
        /// carried phase, which this cannot have.
        /// </summary>
        public static byte[] FromMono(short[] mono, int count, WaveFormat target)
        {
            WaveFormat f = Standardise(target);
            bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat ||
                           (f.BitsPerSample == 32 && f.Encoding != WaveFormatEncoding.Pcm);

            int channels = Math.Max(1, target.Channels);

            if (isFloat && f.BitsPerSample == 32)
            {
                var outBytes = new byte[count * channels * 4];
                for (int i = 0; i < count; i++)
                {
                    byte[] sample = BitConverter.GetBytes(mono[i] / 32768f);
                    for (int c = 0; c < channels; c++)
                        Buffer.BlockCopy(sample, 0, outBytes, (i * channels + c) * 4, 4);
                }
                return outBytes;
            }

            if (f.BitsPerSample == 16)
            {
                var outBytes = new byte[count * channels * 2];
                for (int i = 0; i < count; i++)
                {
                    byte lo = (byte)(mono[i] & 0xFF), hi = (byte)((mono[i] >> 8) & 0xFF);
                    for (int c = 0; c < channels; c++)
                    {
                        outBytes[(i * channels + c) * 2] = lo;
                        outBytes[(i * channels + c) * 2 + 1] = hi;
                    }
                }
                return outBytes;
            }

            // Named rather than swallowed: a silent leg is this feature's worst
            // failure, and a caller hearing nothing because the cable reported an
            // unhandled mix format should say so in one line.
            throw new NotSupportedException(
                $"unsupported endpoint format {f.Encoding} {f.BitsPerSample}-bit " +
                $"{f.SampleRate}Hz — the call leg cannot encode to it.");
        }
    }

    /// <summary>
    /// Linear resampler over a continuous mono PCM16 stream, carrying its read
    /// head and last sample across buffers.
    /// </summary>
    /// <remarks>
    /// The carry is the entire point. WASAPI callback sizes are not multiples of
    /// the 3:1 ratio, so a resampler restarted per buffer silently loses a
    /// fraction of a sample each time and the leg slowly falls behind. See the
    /// remarks on <see cref="CallAudioFormat.Resample16BitMono"/>.
    /// </remarks>
    public sealed class MonoResampler
    {
        private readonly double ratio;  // source frames consumed per output frame
        private double position;        // read head, may be negative into `previous`
        private short previous;         // last sample of the previous buffer

        public MonoResampler(int sourceRate, int targetRate)
        {
            if (sourceRate <= 0 || targetRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sourceRate));
            ratio = (double)sourceRate / targetRate;
        }

        public short[] Process(short[] mono, int count)
        {
            if (mono == null || count <= 0) return new short[0];

            // Interpolation always reads a pair, so the head stops one sample
            // short of the buffer end and the remainder is carried. The +1 in the
            // capacity covers the carried head starting up to one sample BEHIND
            // this buffer, which upsampling (ratio < 1) turns into several extra
            // output samples.
            int capacity = (int)((count + 1) / ratio) + 2;
            var output = new short[capacity];
            int written = 0;

            while (position + 1 < count && written < capacity)
            {
                int a = (int)Math.Floor(position);
                double t = position - a;
                short s0 = a < 0 ? previous : mono[a];
                short s1 = (a + 1) < 0 ? previous : mono[a + 1];
                output[written++] = (short)(s0 * (1 - t) + s1 * t);
                position += ratio;
            }

            previous = mono[count - 1];
            position -= count;

            if (written == capacity) return output;
            var trimmed = new short[written];
            Array.Copy(output, trimmed, written);
            return trimmed;
        }
    }

    /// <summary>
    /// The two audio legs of a screened call.
    ///
    /// Inbound: WASAPI <b>loopback</b> capture on the monitor endpoint (which is
    /// the Communications-role render default during a call, so Phone Link plays
    /// the caller into it), resampled to 16 kHz mono PCM16 and cut into 20 ms
    /// frames for Gemini Live.
    ///
    /// Outbound: 16 kHz mono PCM16 rendered to <c>CABLE Input</c>, which VB-CABLE
    /// presents to Phone Link as <c>CABLE Output</c> — the Communications-role
    /// capture default during a call.
    /// </summary>
    /// <remarks>
    /// Everything here targets endpoints by WASAPI device id, never by WinMM
    /// index. <c>WaveOut</c>/<c>WaveIn</c> select by index against names WinMM
    /// truncates to 31 characters, which clips
    /// <c>CABLE Input (VB-Audio Virtual Cable)</c> to something that no longer
    /// matches what the config says — and picking the wrong virtual device on a
    /// machine that also has Voicemod and Steam ones fails silently, because
    /// silence is also what success sounds like from the outside.
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

        private CableWriter writer;

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

        // Tuned for a phone line, not for this machine's microphone. GeminiRate
        // audio at a mean of ~0.05 is what LiveAudio's own measurements settled on
        // as reliably transcribable, so the target matches; the cap is higher
        // because the starting point is far quieter than a room mic, and the floor
        // is low for the same reason — a 0.0001 line would never clear a floor set
        // for a microphone.
        private const double GainTarget = 0.05;

        // MEASURED ON THIS LINE, and the first values were badly wrong.
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
        // what LiveAudio allows its own mic.
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
        /// talking the stream to Gemini simply stopped, mid-utterance — and
        /// server-side VAD ends a turn by hearing silence, not by noticing an
        /// absence of packets. Measured 2026-08-17 with
        /// bakeoff/callsession/CallSessionProbe: the model transcribed the caller
        /// perfectly ("Hope you're having a peaceful night. How") and then waited
        /// out the whole call cap for an end of turn that could never arrive.
        /// </para>
        /// <para>
        /// A real microphone always delivers, which is why nothing in LiveAudio
        /// needed this and why the shape of the bug is easy to miss. Phone Link
        /// might well render continuously during a call and paper over it — that is
        /// exactly the assumption a real-call test should not be spent discovering.
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

                monitorDevice = Enumerate().GetDevice(monitorEndpointId);
                loopback = new WasapiLoopbackCapture(monitorDevice);

                // The mix format, not a format we asked for: loopback has no say.
                // 48 kHz stereo float here, which is exactly why the resampler is
                // mandatory rather than an optimisation.
                captureFormat = loopback.WaveFormat;
                downsampler = new MonoResampler(captureFormat.SampleRate, CallAudioFormat.GeminiRate);

                loopback.DataAvailable += OnData;
                loopback.RecordingStopped += OnRecordingStopped;

                writer = new CableWriter(cableEndpointId);

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
            CableWriter closing;
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

            CableWriter target;
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
        /// buffers up to ten seconds, and the model streams a reply far faster
        /// than it plays out — so when the caller interrupts, everything already
        /// queued would otherwise keep talking over them for as long as the model
        /// had run ahead. Gemini's `interrupted` message means "discard what you
        /// have"; without a flush there is nowhere to discard it to.
        /// </remarks>
        public void ClearOutbound()
        {
            CableWriter target;
            lock (gate) target = writer;
            if (target == null) return;
            try { target.Clear(); } catch { /* nothing to drop is not an error */ }
        }

        /// <summary>
        /// Plays a WAV file down the outbound leg — to the caller, not to the
        /// speakers — and returns once it has been heard.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The greeting cannot go through <c>VoiceClipCache.PlayAsync</c>: that
        /// opens a <c>WaveOutEvent</c> on the machine's DEFAULT output, which is
        /// the laptop speakers. Phase 2 did exactly that, which is why a screened
        /// caller heard silence while Layth heard the greeting.
        /// </para>
        /// <para>
        /// Written in paced chunks rather than one shot. The cable writer discards
        /// on overflow, so a greeting longer than its buffer would lose its tail
        /// silently — and pacing is also what lets a cancel actually stop the
        /// audio, which matters when the caller hangs up mid-greeting.
        /// </para>
        /// </remarks>
        /// <summary>
        /// Waits until something is actively capturing <paramref name="captureEndpointId"/> —
        /// i.e. the telephony stack has opened our cable as its microphone.
        /// </summary>
        /// <returns>True if a consumer appeared, false if the wait ran out.</returns>
        //
        // WAIT FOR A LISTENER, DO NOT GUESS AT A DELAY.
        //
        // The caller reliably heard only the last two thirds of the greeting, and
        // a 2s run-up of silence did not fix it. It is not our end: the probe
        // captures the whole 5.4s file off the far side of the cable. The greeting
        // simply starts before anything downstream is reading — Phone Link's audio
        // is carried by svchost, which opens its microphone on its own schedule
        // after the call lands on the PC.
        //
        // That moment is observable rather than guessable. An ACTIVE audio session
        // on the capture endpoint means somebody has the cable open and is reading
        // it, which is exactly the precondition the greeting needs — the same
        // session enumeration that found svchost holding the wrong microphone in
        // the first place.
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
                using (var enumerator = Enumerate())
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
        // Exposed because "the model has finished generating" and "the caller has
        // finished hearing it" are different moments, and the gap between them is
        // where a goodbye goes missing.
        public TimeSpan OutboundPending
        {
            get
            {
                CableWriter target;
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

        public async Task SendWavAsync(string path, CancellationToken cancel = default)
        {
            byte[] pcm16Mono16k = ReadWavAs16kMono(path);
            if (pcm16Mono16k.Length == 0)
                throw new InvalidOperationException($"'{path}' contained no audio.");

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
            // conversation start talking over the end of its own greeting.
            await Task.Delay(chunkMs + 250, cancel).ConfigureAwait(false);
        }

        /// <summary>
        /// Plays a WAV straight into a render endpoint, with no bridge attached.
        /// </summary>
        /// <remarks>
        /// The companion to <see cref="PlayToneAsync"/>, and it exists for the
        /// bakeoff harness: injecting real speech into the endpoint the inbound leg
        /// listens to is how a whole call conversation gets tested with no phone
        /// ringing. A tone proves the wires; only speech proves the model hears
        /// anything.
        /// </remarks>
        public static async Task PlayWavAsync(
            string renderEndpointId, string path, CancellationToken cancel = default)
        {
            byte[] pcm = ReadWavAs16kMono(path);
            if (pcm.Length == 0) throw new InvalidOperationException($"'{path}' contained no audio.");

            using (var writer = new CableWriter(renderEndpointId))
            {
                writer.Write(pcm, pcm.Length);

                // Queued is not played — the device holds its own buffer, so
                // returning early would cut the speech off inside the driver.
                double seconds = pcm.Length / 2.0 / CallAudioFormat.GeminiRate;
                await Task.Delay(TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(400), cancel)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Reads a WAV into 16 kHz mono PCM16, whatever it was recorded at.
        /// </summary>
        public static byte[] ReadWavAs16kMono(string path)
        {
            using (var reader = new WaveFileReader(path))
            {
                WaveFormat format = reader.WaveFormat;

                var raw = new byte[reader.Length];
                int read = reader.Read(raw, 0, raw.Length);
                if (read <= 0) return new byte[0];

                byte[] pcm = CallAudioFormat.ToPcm16(raw, read, format);

                // The whole clip is in hand, which is the case the promoted
                // one-shot resampler was written for — no phase to carry.
                return CallAudioFormat.Resample16BitMono(
                    pcm, 0, pcm.Length, format.SampleRate, format.Channels,
                    CallAudioFormat.GeminiRate);
            }
        }

        private void OnData(object sender, WaveInEventArgs e)
        {
            // Loopback on an endpoint nothing is rendering to delivers nothing at
            // all — no packets, not silent ones. So "no data" is the normal state
            // of a quiet line, and it is the PACER, not this method, that keeps the
            // stream to Gemini running through it.
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
                // console output at all (Program.cs says the same thing about the
                // Live pumps). One line beats a silent exit mid-call.
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
        // tick is about 32 fps against a source producing 50. Measured 2026-08-17:
        // that ran a permanent half-second backlog and dropped the oldest audio
        // every single tick while the caller was talking. The transcript still came
        // out right, which is exactly why this needed a log line to notice.
        //
        // Deriving the count from elapsed time makes the cadence real-time whatever
        // the timer does: a late tick emits the frames it owes, silence is padded
        // for the right DURATION rather than the right number of ticks, and the
        // queue drains instead of growing.
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
                // stream the model receives: post-resample, post-gain, 16 kHz mono
                // PCM16. Levels and transcripts have both been misleading — a
                // healthy 0.0456 level still produced a turn transcribed as
                // non-Latin script from a caller speaking English — and no number
                // in a log settles what audio SOUNDS like. Off by default; costs
                // one config key and answers the question in one call.
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
        /// Starts capturing everything sent to the model into a WAV, when
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

                Console.WriteLine("[call-audio] recording what the model hears to " + path);
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
        // Measured on a real screened call (2026-08-17): `inbound level 0.0001`
        // for most of the call, and the transcript to match — "to leave you know
        // you worked and you picked up my co." for a caller who was speaking
        // clearly. Two stages of attenuation stack up on this path and neither is
        // ours to fix: Phone Link renders a Bluetooth HFP stream that is already
        // mono and band-limited, and a loopback capture takes the endpoint's mix
        // AFTER the volume slider, so the speakers being at a civil volume is
        // itself an attenuator.
        //
        // LiveAudio has run adaptive gain on the real mic since long before this
        // feature existed (`LiveAudio.cs:119`) for the same reason, with the same
        // shape: aim the MEAN at a target, cap the multiplier, and — the part that
        // matters — only adapt on frames that contain speech. An AGC that adapts
        // on silence winds itself to maximum during a pause and then clips the
        // first word after it.
        //
        // Deliberately NOT shared with LiveAudio's stage: that one is tuned
        // against this machine's microphone array and carries a warmup and a
        // speech gate wired into the utterance tracker. Borrowing the reasoning is
        // right; borrowing the constants would tune a phone line to a room.
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

        internal static MMDeviceEnumerator Enumerate() => new MMDeviceEnumerator();

        // --- Preflight primitives ----------------------------------------------------
        //
        // Both live here rather than in CallAudioRouter so that every WASAPI call
        // in the feature is in one file, and — more usefully — so the preflight
        // tone travels the same encode path as real call audio. A tone that took a
        // shortcut would pass while the thing it is standing in for could not.

        /// <summary>
        /// Plays <paramref name="leadIn"/> of digital silence and then a short
        /// sine into a render endpoint, through the outbound leg's own conversion
        /// code.
        /// </summary>
        /// <remarks>
        /// THE SILENCE IS NOT PADDING — it is what makes the inbound leg's noise
        /// floor measurable at all, and leaving it out is a bug that took a day
        /// to see because it produced a plausible number rather than an obvious
        /// failure.
        ///
        /// WASAPI loopback delivers a packet only while something is *rendering*
        /// to the endpoint. On an idle machine that means the capture does not
        /// start when StartRecording is called; it starts when the tone does. The
        /// preflight used to record for 250 ms before playing anything and then
        /// treat the first 250 ms of the captured buffer as the noise floor — but
        /// on an idle endpoint those 250 ms of "floor" were the first 250 ms of
        /// the TONE, so the verdict compared the tone against itself. Measured
        /// 2026-08-17: floor=0.2375 tone=0.3550, 800 ms captured out of a 1400 ms
        /// window (800 ms = exactly the render, silence never having existed), and
        /// the leg was refused on hardware that works.
        ///
        /// It passed on 2026-08-16 only because something else happened to be
        /// rendering to the speakers at the time, which kept the loopback
        /// delivering. That is not a property of the machine anyone can rely on.
        ///
        /// Rendering the lead-in makes the capture start with the lead-in on every
        /// leg, so the floor window is genuinely the floor and the alignment no
        /// longer depends on what else is playing.
        /// </remarks>
        public static async Task PlayToneAsync(
            string renderEndpointId, TimeSpan duration, int hz = 660, float amplitude = 0.3f,
            TimeSpan leadIn = default, CancellationToken cancel = default)
        {
            int quiet = Math.Max(0, (int)(CallAudioFormat.GeminiRate * leadIn.TotalSeconds));
            int samples = (int)(CallAudioFormat.GeminiRate * duration.TotalSeconds);

            var signal = new short[quiet + samples];
            for (int i = 0; i < samples; i++)
            {
                double angle = 2 * Math.PI * hz * i / CallAudioFormat.GeminiRate;
                signal[quiet + i] = (short)(Math.Sin(angle) * amplitude * 32767);
            }

            using (var out16 = new CableWriter(renderEndpointId))
            {
                out16.Write(CallAudioFormat.ToBytes(signal, signal.Length), signal.Length * 2);

                // The device holds its own buffer after the last byte is queued;
                // returning at `duration` would cut the tone off inside the driver
                // and hand the preflight a quieter signal than it played.
                await Task.Delay(leadIn + duration + TimeSpan.FromMilliseconds(400), cancel)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Records an endpoint for <paramref name="duration"/> and returns 16 kHz
        /// mono PCM16.
        /// </summary>
        /// <param name="loopbackCapture">
        /// true to loopback-capture a *render* endpoint (the inbound leg), false
        /// to capture a real recording endpoint (the outbound leg's far end).
        /// </param>
        public static async Task<byte[]> RecordAsync(
            string endpointId, bool loopbackCapture, TimeSpan duration,
            CancellationToken cancel = default)
        {
            using (var enumerator = Enumerate())
            using (MMDevice device = enumerator.GetDevice(endpointId))
            {
                IWaveIn capture = loopbackCapture
                    ? (IWaveIn)new WasapiLoopbackCapture(device)
                    : new WasapiCapture(device);

                var collected = new System.IO.MemoryStream();
                WaveFormat format = capture.WaveFormat;
                var done = new TaskCompletionSource<bool>();

                capture.DataAvailable += (s, e) =>
                {
                    if (e.BytesRecorded > 0) collected.Write(e.Buffer, 0, e.BytesRecorded);
                };
                capture.RecordingStopped += (s, e) => done.TrySetResult(true);

                try
                {
                    capture.StartRecording();
                    await Task.Delay(duration, cancel).ConfigureAwait(false);
                }
                finally
                {
                    try { capture.StopRecording(); } catch { }
                }

                await Task.WhenAny(done.Task, Task.Delay(1000)).ConfigureAwait(false);
                try { capture.Dispose(); } catch { }

                byte[] raw = collected.ToArray();
                if (raw.Length == 0) return new byte[0];

                byte[] pcm = CallAudioFormat.ToPcm16(raw, raw.Length, format);

                // The whole clip is in hand here, which is the case the promoted
                // one-shot resampler was written for.
                return CallAudioFormat.Resample16BitMono(
                    pcm, 0, pcm.Length, format.SampleRate, format.Channels,
                    CallAudioFormat.GeminiRate);
            }
        }

        // Renders 16 kHz mono PCM16 into an endpoint at whatever format the
        // endpoint's mix wants.
        //
        // The conversion is done here rather than left to WasapiOut on purpose.
        // NAudio will resample an unsupported format for you, but which path it
        // takes depends on its internals and on what the driver reports — and
        // this leg cannot be tested on a machine without the cable installed, so
        // a deterministic encoder that is obviously right beats a shorter one
        // whose behaviour has to be discovered on the day of a real call.
        private sealed class CableWriter : IDisposable
        {
            private readonly MMDeviceEnumerator enumerator = Enumerate();
            private readonly MMDevice device;
            private readonly WaveFormat mixFormat;
            private readonly MonoResampler upsampler;
            private readonly BufferedWaveProvider buffer;
            private readonly WasapiOut output;

            // What is queued for the caller and not yet played. The buffer is the
            // authority here, not how much has been Send()ed: WasapiOut is fed
            // from it in real time, so this is the only measure of what the caller
            // still has coming.
            public TimeSpan Pending => buffer?.BufferedDuration ?? TimeSpan.Zero;

            public CableWriter(string renderEndpointId)
            {
                // A fresh MMDevice per writer: MMDevice caches the AudioClient it
                // hands to WasapiOut, and WasapiOut disposes it. A shared device
                // would work once and then hand out a dead client.
                device = enumerator.GetDevice(renderEndpointId);
                mixFormat = device.AudioClient.MixFormat;
                upsampler = new MonoResampler(CallAudioFormat.GeminiRate, mixFormat.SampleRate);

                buffer = new BufferedWaveProvider(mixFormat)
                {
                    BufferDuration = TimeSpan.FromSeconds(10),
                    // On a live call, dropping audio that arrived too late to
                    // matter beats throwing out of the send path.
                    DiscardOnBufferOverflow = true,
                };

                output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
                output.Init(buffer);
                output.Play();
            }

            public string Describe() =>
                $"{device.FriendlyName} ({mixFormat.SampleRate}Hz {mixFormat.Channels}ch)";

            public void Clear() => buffer.ClearBuffer();

            public void Write(byte[] pcm16Mono16k, int count)
            {
                short[] mono = CallAudioFormat.Downmix(pcm16Mono16k, count, 1);
                short[] resampled = upsampler.Process(mono, mono.Length);
                if (resampled.Length == 0) return;

                byte[] encoded = CallAudioFormat.FromMono(resampled, resampled.Length, mixFormat);
                buffer.AddSamples(encoded, 0, encoded.Length);
            }

            public void Dispose()
            {
                try { output.Stop(); } catch { }
                try { output.Dispose(); } catch { }
                try { device.Dispose(); } catch { }
                try { enumerator.Dispose(); } catch { }
            }
        }
    }
}
