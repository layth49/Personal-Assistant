using NAudio.Wave;
using System;
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
            Environment.GetEnvironmentVariable("KOKORO_VOICE") ?? "am_onyx";

        private readonly object playbackLock = new object();
        private WaveOutEvent activeOutput;
        private CancellationTokenSource activeCts;

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
            try
            {
                var payload = new
                {
                    model = "kokoro",
                    input = text,
                    voice = voice,
                    response_format = "wav"
                };
                var json = JsonSerializer.Serialize(payload);
                using (var req = new HttpRequestMessage(HttpMethod.Post, kokoroUrl.TrimEnd('/') + "/v1/audio/speech"))
                {
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false))
                    {
                        resp.EnsureSuccessStatusCode();
                        wavBytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    }
                }
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

            if (cts.IsCancellationRequested) return;

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
                await tcs.Task.ConfigureAwait(false);
            }

            lock (playbackLock)
            {
                if (activeOutput == output) activeOutput = null;
                if (activeCts == cts) activeCts = null;
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
        }

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
