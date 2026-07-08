using Personal_Assistant.SearxNGClient;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Personal_Assistant.LLMClient
{
    public class LocalLLMService
    {
        public static readonly string lmStudioUrl =
            Environment.GetEnvironmentVariable("LMSTUDIO_URL") ?? "http://localhost:1234/v1";

        // Reused across the app's lifetime to avoid socket exhaustion / TLS handshake costs
        // (Microsoft guidance: do not new-up HttpClient per request on .NET Framework).
        // Longer timeout than Gemini's 30s because a local llama.cpp on a 4050 can
        // take ~10–20s for a 200-token response, and the very first call after model
        // load is much slower.
        private static readonly HttpClient httpClient = CreateHttpClient();

        private const string BaseSystemPrompt =
            "You are L.A.I.T.H., Layth's personal voice assistant running on his computer. " +
            "Your responses are converted to speech, so: never use markdown, bullet points, " +
            "asterisks, or headers — plain spoken sentences only. " +
            "Default to one short sentence. Only give more detail if the user asks for it, " +
            "asks a multi-part question, or the answer genuinely requires it (e.g. instructions, comparisons). " +
            "Lead with the answer or result first, then explain if needed — never bury the answer at the end. " +
            "If a tool/function is available that matches what the user wants, call it directly rather than " +
            "describing what you would do. Only respond conversationally when no tool fits or the user is " +
            "just chatting. " +
            "If voice input is garbled, ambiguous, or doesn't clearly match a command or question, briefly " +
            "ask for clarification instead of guessing. " +
            "Tone is direct and casual, like a capable assistant who knows Layth well — not stiff or overly formal. " +
            "Never fabricate information; if you don't know or aren't sure, say so plainly.";

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }

        // Drop-in replacement for GeminiService.GenerateGeminiResponse. Keeps the
        // same signature so the Program.cs call site is a one-line change.
        public static async Task<string> GenerateResponse(string inputText)
        {
            // Ground the LLM with SearxNG results. Best-effort — a SearxNG outage
            // returns an empty list, and the LLM still answers from its own knowledge.
            List<SearchHit> hits = await SearxNGService.SearchAsync(inputText);

            string systemPrompt = BuildSystemPrompt(hits);

            // OpenAI-compatible chat-completions payload. LM Studio ignores the
            // model field and serves whichever model is currently loaded in its
            // server tab, so we leave it off.
            var requestBody = new
            {
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = inputText }
                },
                max_tokens = 200,
                temperature = 0.5,
                top_p = 0.5,
                stream = false
            };

            var content = new StringContent(
                JsonSerializer.Serialize(requestBody, JsonOpts),
                Encoding.UTF8,
                "application/json");

            string endpoint = $"{lmStudioUrl.TrimEnd('/')}/chat/completions";

            try
            {
                using (HttpResponseMessage response = await httpClient.PostAsync(endpoint, content))
                {
                    string body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        return $"Error: {(int)response.StatusCode} {response.ReasonPhrase}";
                    }

                    return ExtractText(body);
                }
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        private static string BuildSystemPrompt(List<SearchHit> hits)
        {
            if (hits == null || hits.Count == 0)
            {
                return BaseSystemPrompt;
            }

            var sb = new StringBuilder(BaseSystemPrompt);
            sb.Append("\n\nUse these search results if relevant:\n");
            for (int i = 0; i < hits.Count; i++)
            {
                var h = hits[i];
                sb.Append($"[{i + 1}] {h.Title}\n{h.Snippet}\n({h.Url})\n\n");
            }
            return sb.ToString();
        }

        private static string ExtractText(string json)
        {
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                    choices.GetArrayLength() == 0)
                {
                    return string.Empty;
                }

                if (!choices[0].TryGetProperty("message", out var message) ||
                    !message.TryGetProperty("content", out var contentEl))
                {
                    return string.Empty;
                }

                return contentEl.GetString() ?? string.Empty;
            }
        }
    }
}
