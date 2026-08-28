using NAudio.Wave;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Personal_Assistant.TTSClient
{
    public class KokoroTTSService
    {
        private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        private static readonly string kokoroUrl =
            Environment.GetEnvironmentVariable("KOKORO_URL") ?? "http://localhost:8880";
        private static readonly string voice =
            Environment.GetEnvironmentVariable("KOKORO_VOICE") ?? "am_onyx+am_puck";

        private readonly object playbackLock = new object();
        private WaveOutEvent activeOutput;
        private CancellationTokenSource activeCts;

        // ── Streaming playback state ────────────────────────────────────────
        // One WaveOutEvent fed by a single BufferedWaveProvider for the whole
        // reply: sentences are appended as they finish synthesising, so playback
        // runs continuously instead of stopping and restarting per chunk.
        private BlockingCollection<string> streamQueue;
        private CancellationTokenSource streamCts;
        private Task streamPump;
        private BufferedWaveProvider streamBuffer;
        private WaveOutEvent streamOutput;
        private Stopwatch streamClock;
        private volatile bool inputEnded;
        private bool playbackStarted;

        // How much audio to bank before playback starts. Buys synthesis a head
        // start so a slow chunk doesn't become an audible gap; also the ceiling on
        // what this adds to time-to-first-audio (usually nothing, since the first
        // chunk is normally longer than this on its own).
        private static readonly TimeSpan PlaybackLead = TimeSpan.FromMilliseconds(700);

        // Raised the moment playback actually starts, on every path.
        //
        // The listener's echo gate needs to know when sound begins coming out of
        // the speakers, and it must not try to work that out from the microphone:
        // the VAD recognises the assistant's voice before the frame level has
        // risen far enough to be sure it isn't room noise, so a level-based latch
        // loses the race and the gate is still inactive when the echo's onset
        // fires. This side of the pipeline knows the answer exactly.
        public event Action PlaybackStarted;

        private void RaisePlaybackStarted()
        {
            var handler = PlaybackStarted;
            if (handler == null) return;
            try { handler(); } catch (Exception ex) { Console.WriteLine("[tts] PlaybackStarted handler: " + ex.Message); }
        }

        // Time from BeginStream() to the instant the first chunk started playing —
        // the number Session D exists to shrink. Null if nothing played.
        public TimeSpan? FirstAudioLatency { get; private set; }

        // Wall-clock time of the last synthesis request only — the network round
        // trip to Kokoro to get WAV bytes back. Deliberately excludes playback
        // (started after this point), which scales with reply length and isn't a
        // latency bottleneck the way generation time is.
        public TimeSpan LastSynthesisElapsed { get; private set; }

        public async Task SpeakAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            CancellationTokenSource cts;
            lock (playbackLock)
            {
                StopSpeakingInternal();
                cts = new CancellationTokenSource();
                activeCts = cts;
            }

            byte[] wavBytes;
            var sw = Stopwatch.StartNew();
            try
            {
                wavBytes = await RequestWavAsync(text, cts.Token).ConfigureAwait(false);
                LastSynthesisElapsed = sw.Elapsed;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Kokoro TTS request failed: " + ex.Message);
                return;
            }

            if (cts.IsCancellationRequested || wavBytes == null) return;

            FixWavHeaderSizes(wavBytes);

            var tcs = new TaskCompletionSource<bool>();
            WaveOutEvent output;
            WaveFileReader reader;
            try
            {
                reader = new WaveFileReader(new MemoryStream(wavBytes));
                output = new WaveOutEvent { DesiredLatency = 100 };
                output.Init(reader);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Kokoro audio init failed: " + ex.Message);
                return;
            }

            output.PlaybackStopped += (s, e) =>
            {
                try { reader.Dispose(); } catch { }
                try { output.Dispose(); } catch { }
                tcs.TrySetResult(true);
            };

            lock (playbackLock)
            {
                if (cts.IsCancellationRequested)
                {
                    try { output.Dispose(); } catch { }
                    try { reader.Dispose(); } catch { }
                    return;
                }
                activeOutput = output;
            }

            using (cts.Token.Register(() =>
            {
                try { output.Stop(); } catch { }
            }))
            {
                output.Play();
                RaisePlaybackStarted();
                await tcs.Task.ConfigureAwait(false);
            }

            lock (playbackLock)
            {
                if (activeOutput == output) activeOutput = null;
                if (activeCts == cts) activeCts = null;
            }
        }

        // Emoji reach the bubble but must never reach the voice: the system
        // prompt tells the model they're shown and not spoken, and Kokoro has no
        // idea — hand it "😊" and it says "smiling face with smiling eyes" out
        // loud. That also poisoned the echo gate, since those invented words
        // come back through the microphone as text no reply ever contained.
        //
        // Everything above the BMP goes, which is where essentially all emoji
        // live, plus the BMP symbol/dingbat blocks and the joiners that glue
        // sequences together.
        internal static string StripUnspeakable(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
                    continue;
                }
                if (IsSymbolBlock(c)) continue;
                sb.Append(c);
            }

            // Removing a trailing emoji tends to leave " ." or a double space.
            var outp = new StringBuilder(sb.Length);
            bool lastWasSpace = false;
            foreach (char c in sb.ToString())
            {
                bool isSpace = char.IsWhiteSpace(c);
                if (isSpace && lastWasSpace) continue;
                outp.Append(c);
                lastWasSpace = isSpace;
            }
            return outp.ToString().Trim();
        }

        private static bool IsSymbolBlock(char c)
        {
            return (c >= '←' && c <= '⇿')   // arrows
                || (c >= '⌀' && c <= '⏿')   // misc technical (⏰, ⌚)
                || (c >= '■' && c <= '➿')   // shapes, misc symbols, dingbats
                || (c >= '⬀' && c <= '⯿')   // more arrows/shapes
                || (c >= '︀' && c <= '️')   // variation selectors
                || (c >= '⃐' && c <= '⃿')   // combining marks (keycaps)
                || c == '‍';                     // zero-width joiner
        }

        // True if there's anything left for a voice to pronounce.
        private static bool HasSpeakable(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c)) return true;
            }
            return false;
        }

        // One Kokoro synthesis round trip. Shared by the one-shot and streaming
        // paths so the payload/endpoint live in exactly one place. Returns null
        // if nothing speakable survives the strip.
        private static async Task<byte[]> RequestWavAsync(string text, CancellationToken ct) =>
            await RequestWavAsync(text, null, ct).ConfigureAwait(false);

        private static async Task<byte[]> RequestWavAsync(
            string text, string voiceOverride, CancellationToken ct)
        {
            string spoken = StripUnspeakable(text);
            if (!HasSpeakable(spoken)) return null;

            var payload = new
            {
                model = "kokoro",
                input = spoken,
                voice = string.IsNullOrWhiteSpace(voiceOverride) ? voice : voiceOverride,
                response_format = "wav"
            };
            var json = JsonSerializer.Serialize(payload);
            using (var req = new HttpRequestMessage(HttpMethod.Post, kokoroUrl.TrimEnd('/') + "/v1/audio/speech"))
            {
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using (var resp = await http
                    .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
        }

        // ── Streaming API ───────────────────────────────────────────────────
        // Usage: BeginStream() -> EnqueueSentence(..) xN -> EndStreamInput() ->
        // await CompleteStreamAsync(). StopSpeaking() aborts the whole thing at
        // any point.

        public void BeginStream()
        {
            lock (playbackLock)
            {
                StopSpeakingInternal();
                streamQueue = new BlockingCollection<string>();
                streamCts = new CancellationTokenSource();
                streamBuffer = null;
                streamOutput = null;
                inputEnded = false;
                playbackStarted = false;
                FirstAudioLatency = null;
                streamClock = Stopwatch.StartNew();

                var queue = streamQueue;
                var cts = streamCts;
                // GetConsumingEnumerable blocks, so the pump owns a threadpool
                // thread for the duration of the reply rather than an async slot.
                streamPump = Task.Run(() => PumpAsync(queue, cts));
            }
        }

        // Hands one sentence to the synthesiser. Returns immediately — synthesis
        // of the next sentence overlaps playback of the current one.
        public void EnqueueSentence(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            BlockingCollection<string> queue;
            lock (playbackLock) { queue = streamQueue; }
            if (queue == null) return;
            try { queue.Add(text); }
            catch (Exception) { /* completed or disposed by a concurrent stop */ }
        }

        // No more sentences are coming; the pump may finish once it drains.
        public void EndStreamInput()
        {
            inputEnded = true;
            BlockingCollection<string> queue;
            lock (playbackLock) { queue = streamQueue; }
            if (queue == null) return;
            try { queue.CompleteAdding(); } catch (ObjectDisposedException) { }

            // Nothing more is coming, so there's no reason to keep banking audio
            // for a head start — release whatever is held.
            bool justStarted = false;
            lock (playbackLock)
            {
                if (!playbackStarted && streamBuffer != null && streamOutput != null)
                {
                    playbackStarted = true;
                    streamOutput.Play();
                    FirstAudioLatency = streamClock.Elapsed;
                    justStarted = true;
                }
            }
            // Outside the lock: handlers are someone else's code.
            if (justStarted) RaisePlaybackStarted();
        }

        // Waits for every queued sentence to be synthesised AND played out.
        public async Task CompleteStreamAsync()
        {
            Task pump;
            CancellationTokenSource cts;
            lock (playbackLock) { pump = streamPump; cts = streamCts; }

            EndStreamInput();
            if (pump != null)
            {
                try { await pump.ConfigureAwait(false); } catch { }
            }

            // Then let the audio already handed to NAudio drain.
            while (cts != null && !cts.IsCancellationRequested)
            {
                BufferedWaveProvider buffer;
                lock (playbackLock) { buffer = streamBuffer; }
                if (buffer == null || buffer.BufferedBytes == 0) break;
                await Task.Delay(30).ConfigureAwait(false);
            }

            // BufferedBytes hits zero while the driver still holds ~DesiredLatency
            // of audio; without this the last syllable gets clipped.
            if (cts != null && !cts.IsCancellationRequested)
            {
                bool played;
                lock (playbackLock) { played = streamBuffer != null; }
                if (played) await Task.Delay(150).ConfigureAwait(false);
            }

            TeardownStream();
        }

        private async Task PumpAsync(BlockingCollection<string> queue, CancellationTokenSource cts)
        {
            var synthTotal = TimeSpan.Zero;
            try
            {
                foreach (string sentence in queue.GetConsumingEnumerable(cts.Token))
                {
                    if (cts.IsCancellationRequested) return;

                    byte[] wav;
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        wav = await RequestWavAsync(sentence, cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        // Skip the sentence rather than dropping the rest of the reply.
                        Console.WriteLine("Kokoro chunk synth failed: " + ex.Message);
                        continue;
                    }
                    synthTotal += sw.Elapsed;
                    LastSynthesisElapsed = synthTotal;

                    if (wav == null || cts.IsCancellationRequested) continue;
                    AppendChunk(wav, cts);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine("Kokoro stream pump failed: " + ex.Message);
            }
        }

        // Decodes one synthesised chunk and appends its PCM to the shared output.
        private void AppendChunk(byte[] wav, CancellationTokenSource cts)
        {
            FixWavHeaderSizes(wav);
            bool justStarted = false;
            try
            {
                using (var reader = new WaveFileReader(new MemoryStream(wav)))
                {
                    var pcm = new byte[reader.Length];
                    int read = reader.Read(pcm, 0, pcm.Length);
                    if (read <= 0) return;

                    lock (playbackLock)
                    {
                        if (cts.IsCancellationRequested || streamCts != cts) return;

                        if (streamBuffer == null)
                        {
                            // First chunk decides the format — no need to hardcode
                            // Kokoro's sample rate, and a server-side voice change
                            // can't desync us.
                            streamBuffer = new BufferedWaveProvider(reader.WaveFormat)
                            {
                                BufferDuration = TimeSpan.FromMinutes(5),
                                DiscardOnBufferOverflow = false
                            };
                            // Bluetooth/wireless output needs a deeper device
                            // buffer than the 100ms the one-shot path used: there
                            // audio arrived as one finished WAV, whereas here it
                            // trickles in and a short buffer underruns audibly.
                            streamOutput = new WaveOutEvent
                            {
                                DesiredLatency = 250,
                                NumberOfBuffers = 3,
                            };
                            streamOutput.Init(streamBuffer);
                        }

                        streamBuffer.AddSamples(pcm, 0, read);

                        // Hold a little audio back before starting, so synthesis
                        // has a head start on playback. Without this the very
                        // first chunk starts playing immediately and any hiccup
                        // synthesising the second one is heard as a gap.
                        if (!playbackStarted &&
                            (streamBuffer.BufferedDuration >= PlaybackLead || inputEnded))
                        {
                            playbackStarted = true;
                            streamOutput.Play();
                            FirstAudioLatency = streamClock.Elapsed;
                            justStarted = true;
                            Console.WriteLine(
                                $"[tts] first audio at {streamClock.ElapsedMilliseconds}ms "
                                + $"({streamBuffer.BufferedDuration.TotalMilliseconds:F0}ms buffered)");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Kokoro chunk decode failed: " + ex.Message);
            }
            // Outside the lock: handlers are someone else's code.
            if (justStarted) RaisePlaybackStarted();
        }

        private void TeardownStream()
        {
            WaveOutEvent output;
            lock (playbackLock)
            {
                output = streamOutput;
                streamOutput = null;
                streamBuffer = null;
                if (streamQueue != null) { try { streamQueue.Dispose(); } catch { } }
                streamQueue = null;
                streamPump = null;
                streamCts = null;
            }
            if (output != null)
            {
                try { output.Stop(); } catch { }
                try { output.Dispose(); } catch { }
            }
        }

        public void StopSpeaking()
        {
            lock (playbackLock) { StopSpeakingInternal(); }
        }

        private void StopSpeakingInternal()
        {
            if (activeCts != null)
            {
                try { activeCts.Cancel(); } catch { }
            }
            if (activeOutput != null)
            {
                try { activeOutput.Stop(); } catch { }
            }

            // Streaming path: cancel in-flight synthesis, drop everything still
            // queued, and clear audio already buffered but not yet played — so a
            // barge-in leaves no "ghost" sentences trailing after the cut.
            if (streamCts != null)
            {
                try { streamCts.Cancel(); } catch { }
            }
            if (streamQueue != null)
            {
                try { streamQueue.CompleteAdding(); } catch { }
                string discarded;
                while (streamQueue.TryTake(out discarded)) { }
            }
            if (streamBuffer != null)
            {
                try { streamBuffer.ClearBuffer(); } catch { }
            }
            if (streamOutput != null)
            {
                try { streamOutput.Stop(); } catch { }
            }
        }

        // ── Synthesis without playback ──────────────────────────────────────
        //
        // Everything above ends at a WaveOutEvent, which plays on the machine's
        // DEFAULT output — the speakers. A screened call must never do that: its
        // mouth is a virtual cable, and audio that reaches the speakers instead
        // is the exact failure main recorded (the caller heard silence while
        // Layth heard the greeting). So the call path takes the WAV bytes and
        // does its own routing.
        //
        // Deliberately routed through RequestWavAsync rather than around it, so
        // StripUnspeakable still runs at the one chokepoint: Kokoro has no SSML
        // and reads an emoji out loud as its CLDR name, and a caller hearing
        // "smiling face with smiling eyes" down a phone line is worse than on
        // speakers because there is nobody to explain it to them.
        //
        // The header fix is applied here too — Kokoro-FastAPI leaves the RIFF
        // sizes as placeholders, and NAudio's WaveFileReader rejects that — so
        // callers get bytes a WaveFileReader will actually open.
        internal static async Task<byte[]> SynthesizeWavAsync(
            string text, string voiceOverride = null, CancellationToken ct = default)
        {
            byte[] wav = await RequestWavAsync(text, voiceOverride, ct).ConfigureAwait(false);
            if (wav == null) return null;
            FixWavHeaderSizes(wav);
            return wav;
        }

        // The voice this process synthesises in. THE ONE ACCESSOR for it outside
        // this class: the call greeting cache is keyed on it, so a second copy of
        // the KOKORO_VOICE expression that drifted would render every clip under
        // a key nothing ever looks up, and the stock-WAV fallback would hide it.
        internal static string ConfiguredVoice => voice;

        // Kokoro-FastAPI streams the WAV and leaves the RIFF/data chunk size
        // fields as placeholders, which NAudio's WaveFileReader rejects. Rewrite
        // them from the actual byte length so the file parses cleanly.
        private static void FixWavHeaderSizes(byte[] wav)
        {
            if (wav == null || wav.Length < 44) return;
            if (wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') return;
            if (wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E') return;

            WriteUInt32LE(wav, 4, (uint)(wav.Length - 8));

            int i = 12;
            while (i + 8 <= wav.Length)
            {
                bool isData = wav[i] == 'd' && wav[i + 1] == 'a' && wav[i + 2] == 't' && wav[i + 3] == 'a';
                uint chunkSize = ReadUInt32LE(wav, i + 4);

                if (isData)
                {
                    WriteUInt32LE(wav, i + 4, (uint)(wav.Length - i - 8));
                    return;
                }

                long next = (long)i + 8L + chunkSize + (chunkSize % 2);
                if (next <= i || next > wav.Length) return;
                i = (int)next;
            }
        }

        private static uint ReadUInt32LE(byte[] b, int o)
        {
            return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        private static void WriteUInt32LE(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v & 0xFF);
            b[o + 1] = (byte)((v >> 8) & 0xFF);
            b[o + 2] = (byte)((v >> 16) & 0xFF);
            b[o + 3] = (byte)((v >> 24) & 0xFF);
        }
    }
}