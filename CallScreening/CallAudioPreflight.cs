using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Personal_Assistant.CallScreening
{
    // THE WASAPI PLUMBING THE ROUTER NEEDS, SPLIT OUT FROM THE LIVE CALL LEGS.
    //
    // On main every one of these types lives in CallAudioBridge.cs, on the stated
    // principle that every WASAPI call in the feature should sit in one file and that
    // the preflight tone should travel the same encode path as real call audio. Both
    // reasons are good and neither survives the way this branch lands the feature:
    // CallAudioBridge is welded to the cloud model's streaming session and is being
    // rebuilt against the local stack in a later commit, while CallAudioRouter — which
    // decides which endpoints a call may use, and which put the machine's default audio
    // devices back after main's restore path once failed to — depends on it only for a
    // sine wave and an RMS.
    //
    // So the format helpers, the streaming resampler and the two preflight primitives
    // come across now, verbatim, and the conversation half builds its bridge on top of
    // them rather than the other way round. The tone still travels the same encode path
    // as call audio, because that path is CallAudioFormat and it is right here.
    //
    // The ONLY change from main is the home of PlayToneAsync/RecordAsync: they were
    // static members of CallAudioBridge and are now static members of CallAudioProbe,
    // so CallAudioRouter's two call sites read CallAudioProbe instead. Nothing else,
    // including the name GeminiRate — it is a 16000 that the local models want just as
    // much as the cloud one did, and renaming it would only make this file harder to
    // diff against the branch it came from.
    //
    // NOTE FOR THE CONVERSATION HALF: CallAudioFormat and MonoResampler are already
    // defined. Do not bring a second copy across with CallAudioBridge.cs.

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
    /// Plays a tone into one endpoint and records another, which is how
    /// <see cref="CallAudioRouter"/> decides whether a call leg actually carries
    /// audio before a stranger is answered on it.
    /// </summary>
    public static class CallAudioProbe
    {
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
        // INTERNAL rather than private, and the one change 6b made to this file.
        // CallAudioBridge's outbound leg is this class — it was a private nested
        // type of CallAudioBridge upstream, and moving the preflight primitives
        // here moved it with them. A second copy living in the bridge is exactly
        // what these headers warn against, so the bridge borrows this one.
        internal sealed class CableWriter : IDisposable
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
