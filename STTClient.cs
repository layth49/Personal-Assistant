using NAudio.Wave;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace Personal_Assistant.STTClient
{
    public class WhisperSTTService
    {
        private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        private static readonly string whisperUrl =
            Environment.GetEnvironmentVariable("WHISPER_URL") ?? "http://localhost:8000";
        private static readonly string model =
            Environment.GetEnvironmentVariable("WHISPER_MODEL") ?? "Systran/faster-whisper-large-v3";

        // 16kHz mono 16-bit PCM — matches Whisper's native sample rate.
        private static readonly WaveFormat captureFormat = new WaveFormat(16000, 16, 1);

        // RMS threshold (in 16-bit signed sample units) below which a chunk counts as silence.
        private const double SilenceThresholdRms = 500.0;

        // Trailing silence required to consider speech finished.
        private static readonly TimeSpan TrailingSilence = TimeSpan.FromMilliseconds(1500);

        // How long to wait for the user to start speaking before giving up.
        private static readonly TimeSpan InitialSilenceTimeout = TimeSpan.FromSeconds(5);

        public async Task<string> RecognizeOnceAsync(int maxSeconds = 15)
        {
            byte[] wavBytes;
            try
            {
                wavBytes = await CaptureAudioAsync(maxSeconds).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Whisper STT capture failed: " + ex.Message);
                return string.Empty;
            }

            if (wavBytes == null || wavBytes.Length <= 44)
            {
                return string.Empty;
            }

            try
            {
                string text = await TranscribeAsync(wavBytes).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    Console.WriteLine($"RECOGNIZED: {text}");
                }
                return text;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Whisper STT transcription failed: " + ex.Message);
                return string.Empty;
            }
        }

        private static Task<byte[]> CaptureAudioAsync(int maxSeconds)
        {
            var tcs = new TaskCompletionSource<byte[]>();
            var memStream = new MemoryStream();

            WaveFileWriter writer;
            WaveInEvent waveIn;
            try
            {
                writer = new WaveFileWriter(new IgnoreCloseStream(memStream), captureFormat);
                waveIn = new WaveInEvent
                {
                    WaveFormat = captureFormat,
                    BufferMilliseconds = 50,
                };
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
                return tcs.Task;
            }

            DateTime captureStart = DateTime.UtcNow;
            DateTime lastVoiceTime = DateTime.MinValue;
            bool stopRequested = false;

            waveIn.DataAvailable += (s, e) =>
            {
                if (stopRequested) return;
                try
                {
                    writer.Write(e.Buffer, 0, e.BytesRecorded);

                    DateTime now = DateTime.UtcNow;
                    if (ChunkHasVoice(e.Buffer, e.BytesRecorded))
                    {
                        lastVoiceTime = now;
                    }

                    TimeSpan elapsed = now - captureStart;
                    bool maxReached = elapsed.TotalSeconds >= maxSeconds;
                    bool trailingSilenceDone =
                        lastVoiceTime != DateTime.MinValue &&
                        (now - lastVoiceTime) >= TrailingSilence;
                    bool initialSilenceDone =
                        lastVoiceTime == DateTime.MinValue &&
                        elapsed >= InitialSilenceTimeout;

                    if (maxReached || trailingSilenceDone || initialSilenceDone)
                    {
                        stopRequested = true;
                        waveIn.StopRecording();
                    }
                }
                catch (Exception ex)
                {
                    stopRequested = true;
                    try { waveIn.StopRecording(); } catch { }
                    tcs.TrySetException(ex);
                }
            };

            waveIn.RecordingStopped += (s, e) =>
            {
                try { writer.Flush(); } catch { }
                try { writer.Dispose(); } catch { }
                try { waveIn.Dispose(); } catch { }

                if (e.Exception != null)
                {
                    tcs.TrySetException(e.Exception);
                    return;
                }

                // Only return the captured bytes if there was at least one voiced chunk.
                if (lastVoiceTime == DateTime.MinValue)
                {
                    tcs.TrySetResult(Array.Empty<byte>());
                }
                else
                {
                    tcs.TrySetResult(memStream.ToArray());
                }
            };

            try
            {
                waveIn.StartRecording();
            }
            catch (Exception ex)
            {
                try { writer.Dispose(); } catch { }
                try { waveIn.Dispose(); } catch { }
                tcs.TrySetException(ex);
            }

            return tcs.Task;
        }

        private static bool ChunkHasVoice(byte[] buffer, int count)
        {
            int sampleCount = count / 2;
            if (sampleCount == 0) return false;

            long sumSquares = 0;
            for (int i = 0; i + 1 < count; i += 2)
            {
                short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                sumSquares += (long)sample * sample;
            }
            double rms = Math.Sqrt(sumSquares / (double)sampleCount);
            return rms >= SilenceThresholdRms;
        }

        private static async Task<string> TranscribeAsync(byte[] wavBytes)
        {
            string url = whisperUrl.TrimEnd('/') + "/v1/audio/transcriptions";

            using (var form = new MultipartFormDataContent())
            {
                var fileContent = new ByteArrayContent(wavBytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                form.Add(fileContent, "file", "audio.wav");
                form.Add(new StringContent(model), "model");
                form.Add(new StringContent("en"), "language");
                form.Add(new StringContent("json"), "response_format");

                using (var resp = await http.PostAsync(url, form).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    using (var doc = JsonDocument.Parse(body))
                    {
                        if (doc.RootElement.TryGetProperty("text", out var t))
                        {
                            return (t.GetString() ?? string.Empty).Trim();
                        }
                    }
                    return string.Empty;
                }
            }
        }

        // WaveFileWriter closes its underlying stream on Dispose, but we need the
        // MemoryStream alive afterwards to read back the finalized WAV bytes.
        private sealed class IgnoreCloseStream : Stream
        {
            private readonly Stream inner;
            public IgnoreCloseStream(Stream inner) { this.inner = inner; }
            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => inner.CanSeek;
            public override bool CanWrite => inner.CanWrite;
            public override long Length => inner.Length;
            public override long Position { get => inner.Position; set => inner.Position = value; }
            public override void Flush() => inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
            public override void SetLength(long value) => inner.SetLength(value);
            public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
            protected override void Dispose(bool disposing) { }
        }
    }
}
