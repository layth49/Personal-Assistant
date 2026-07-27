using NAudio.Wave;
using Python.Runtime;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        // 800ms proved too tight in practice: Whisper transcribes noticeably worse
        // from clips cropped hard against the speech, so the tail is worth more
        // than the latency it costs. Still well under the old RMS gate's 1500ms.
        private const int TrailingSilenceSamples = 16000 * 1000 / 1000;
        private const int MaxUtteranceSamples = 16000 * 20;
        // Ignore blips: a cough or a door is not a turn.
        private const int MinSpeechSamples = 16000 * 200 / 1000;

        // Audio kept from before the onset was confirmed, so the first syllable
        // isn't clipped off the front of the utterance. Generous on purpose —
        // leading silence costs Whisper nothing, but a clipped first word costs
        // the whole transcription.
        private static readonly TimeSpan PreRoll = TimeSpan.FromMilliseconds(500);

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

        public bool IsArmed => armed;

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

                HandleFrame(chunk, prob);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[listen] frame error: " + ex.Message);
            }
        }

        private void HandleFrame(byte[] chunk, double prob)
        {
            bool fireOnset = false;
            byte[] finished = null;
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
                            fireOnset = true;
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

                    bool silentLongEnough = (now - lastVoiceSample) >= TrailingSilenceSamples;
                    bool tooLong = (now - speechStartSample) >= MaxUtteranceSamples;

                    if (silentLongEnough || tooLong)
                    {
                        bool longEnough = (lastVoiceSample - speechStartSample) >= MinSpeechSamples;
                        if (longEnough) finished = BuildWav(utterance.ToArray());
                        else Console.WriteLine("[listen] ignoring blip");
                        ResetSpeechState();
                    }
                }
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

            if (finished != null)
            {
                Task.Run(() => TranscribeAndPublishAsync(finished));
            }
        }

        private async Task TranscribeAndPublishAsync(byte[] wav)
        {
            string text;
            var sw = Stopwatch.StartNew();
            try
            {
                text = await WhisperSTTService.TranscribeAsync(wav).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[listen] transcription failed: " + ex.Message);
                return;
            }
            LastTranscribeElapsed = sw.Elapsed;

            if (string.IsNullOrWhiteSpace(text)) return;
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

        public TimeSpan LastTranscribeElapsed { get; private set; }

        // Waits for the next complete utterance. Returns "" if the user stayed
        // quiet for `timeout` — that's how a conversation window closes.
        public async Task<string> NextUtteranceAsync(TimeSpan timeout)
        {
            Task<string> wait;
            lock (gate)
            {
                if (pending.Count > 0) return pending.Dequeue();
                waiter = new TaskCompletionSource<string>();
                wait = waiter.Task;
            }

            var finished = await Task.WhenAny(wait, Task.Delay(timeout)).ConfigureAwait(false);
            if (finished == wait) return wait.Result;

            lock (gate) { waiter = null; }
            return string.Empty;
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
