using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace Personal_Assistant.SearxNGClient
{
    public class SearchHit
    {
        public string Title { get; set; }
        public string Url { get; set; }
        public string Snippet { get; set; }
    }

    public class SearxNGService
    {
        public static readonly string searxNGUrl =
            Environment.GetEnvironmentVariable("SEARXNG_URL") ?? "http://localhost:8080";

        // Reused across the app's lifetime to avoid socket exhaustion / TLS handshake costs
        // (Microsoft guidance: do not new-up HttpClient per request on .NET Framework).
        private static readonly HttpClient httpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            // SearxNG botdetection requires X-Real-IP or X-Forwarded-For to identify
            // the caller. Without one of these it immediately returns 403, regardless
            // of the limiter setting. Sending 127.0.0.1 marks us as localhost, which
            // the limiter.toml pass_ip list then lets through unconditionally.
            client.DefaultRequestHeaders.Add("X-Real-IP", "127.0.0.1");
            client.DefaultRequestHeaders.Add("X-Forwarded-For", "127.0.0.1");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            return client;
        }

        // Best-effort search. SearxNG outage / network blip returns an empty list
        // so the LLM still answers from its own knowledge instead of failing.
        public static async Task<List<SearchHit>> SearchAsync(string query, int topN = 3)
        {
            var hits = new List<SearchHit>();
            if (string.IsNullOrWhiteSpace(query))
            {
                return hits;
            }

            string endpoint = $"{searxNGUrl.TrimEnd('/')}/search?q={Uri.EscapeDataString(query)}&format=json";

            try
            {
                using (HttpResponseMessage response = await httpClient.GetAsync(endpoint))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"SearxNG search returned {(int)response.StatusCode} {response.ReasonPhrase}");
                        return hits;
                    }

                    string body = await response.Content.ReadAsStringAsync();
                    return ParseHits(body, topN);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SearxNG search failed (non-fatal): {ex.Message}");
                return hits;
            }
        }

        private static List<SearchHit> ParseHits(string json, int topN)
        {
            var hits = new List<SearchHit>();
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                if (!doc.RootElement.TryGetProperty("results", out var results) ||
                    results.ValueKind != JsonValueKind.Array)
                {
                    return hits;
                }

                foreach (var result in results.EnumerateArray())
                {
                    if (hits.Count >= topN) break;
                    hits.Add(new SearchHit
                    {
                        Title = result.TryGetProperty("title", out var t) ? t.GetString() : string.Empty,
                        Url = result.TryGetProperty("url", out var u) ? u.GetString() : string.Empty,
                        // SearxNG calls the snippet "content" in its JSON output.
                        Snippet = result.TryGetProperty("content", out var c) ? c.GetString() : string.Empty
                    });
                }
            }
            return hits;
        }
    }
}
