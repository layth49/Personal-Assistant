using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace Personal_Assistant.STTClient
{
    // Speech-to-text over the OpenAI transcription API.
    //
    // Points at the Parakeet service (stt-server/, :8001) by default — the
    // bake-off winner: better on contact names and critical WER than
    // faster-whisper-large-v3-turbo, ~40% faster, and it runs on the CPU, which
    // returns whisper's ~1.7 GB of VRAM to the LLM and Kokoro.
    //
    // The request below is deliberately whisper-shaped and is sent unchanged to
    // either engine: the Parakeet service ignores the fields that mean nothing
    // to it (prompt, beam_size, temperature) and biases decoding with a boosting
    // tree instead. So going back to whisper is one environment variable:
    //     STT_URL=http://localhost:8000
    //
    // Capture lives in ContinuousListener, which owns the mic for the life of the
    // app; this class only turns finished WAV bytes into text.
    public class SpeechToTextService
    {
        private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // WHISPER_URL / WHISPER_MODEL still work: they're what's set on machines
        // configured before the engine swap.
        //
        // 127.0.0.1, deliberately, not "localhost": that name resolves to ::1
        // first on Windows, and the Parakeet service binds IPv4 only, so every
        // transcription would spend ~2s waiting for the IPv6 connect to be
        // refused before falling back. Measured 2247ms vs 205ms for the same
        // 264ms of inference.
        private static readonly string sttUrl =
            Environment.GetEnvironmentVariable("STT_URL")
            ?? Environment.GetEnvironmentVariable("WHISPER_URL")
            ?? "http://127.0.0.1:8001";
        private static readonly string model =
            Environment.GetEnvironmentVariable("STT_MODEL")
            ?? Environment.GetEnvironmentVariable("WHISPER_MODEL")
            ?? "everyscribe/faster-whisper-large-v3-turbo-ct2";

        // Cached so the contacts file isn't re-read per utterance.
        private static Dictionary<string, string> _cachedContacts;
        private static string _dynamicPrompt;

        // WAV bytes in, transcript out. Empty string on failure — callers check
        // for that rather than for an exception.
        internal static async Task<string> TranscribeAsync(byte[] wavBytes)
        {
            string url = sttUrl.TrimEnd('/') + "/v1/audio/transcriptions";

            using (var form = new MultipartFormDataContent())
            {
                var fileContent = new ByteArrayContent(wavBytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                form.Add(fileContent, "file", "audio.wav");
                form.Add(new StringContent(model), "model");
                form.Add(new StringContent("en"), "language");
                form.Add(new StringContent("json"), "response_format");

                // Force greedy decoding for minimum latency
                form.Add(new StringContent("1"), "beam_size");
                form.Add(new StringContent("0"), "temperature");

                // Contact names as a decoder prompt. This is what carries
                // whisper's contact-name accuracy (it more than halved its WER on
                // names in the bake-off); Parakeet ignores the field and biases
                // decoding with a boosting tree built from the same file instead.
                if (_dynamicPrompt == null)
                {
                    _cachedContacts = Program.LoadContacts();

                    string jargon = "Arduino, Home Assistant.";

                    if (_cachedContacts != null && _cachedContacts.Count > 0)
                    {
                        _dynamicPrompt = string.Join(", ", _cachedContacts.Keys) + ", " + jargon;
                    }
                    else
                    {
                        _dynamicPrompt = jargon;
                    }
                }

                form.Add(new StringContent(_dynamicPrompt), "prompt");

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
    }
}