using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Personal_Assistant.GeminiClient
{
    public class GeminiService
    {
        public static readonly string geminiApiKey = Environment.GetEnvironmentVariable("GEMINIAPI_KEY");

        // Reused across the app's lifetime to avoid socket exhaustion / TLS handshake costs
        // (Microsoft guidance: do not new-up HttpClient per request on .NET Framework).
        private static readonly HttpClient httpClient = CreateHttpClient();

        private const string Endpoint =
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent";

        private const string SystemPrompt =
            "You are a concise voice assistant. Answer accurately and in as few words as possible. " +
            "Lead with the final answer; only expand if the user asks for detail. " +
            "Maintain a courteous, professional tone. Be transparent about limitations.";

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrEmpty(geminiApiKey))
            {
                client.DefaultRequestHeaders.Add("x-goog-api-key", geminiApiKey);
            }
            return client;
        }

        public static async Task<string> GenerateGeminiResponse(string inputText)
        {
            var requestBody = new
            {
                system_instruction = new { parts = new[] { new { text = SystemPrompt } } },
                contents = new[]
                {
                    new { role = "user", parts = new[] { new { text = inputText } } }
                },
                // Google Search grounding — Gemini will run a web search when it
                // helps answer the question, then cite the result. Lets the
                // assistant answer about current events / facts past the model
                // cutoff. Tool name is google_search (the snake_case form Gemini
                // 2.0+ uses; the older googleSearchRetrieval is for 1.5 only).
                tools = new[]
                {
                    new { google_search = new { } }
                },
                generationConfig = new
                {
                    temperature = 0.5,
                    topP = 0.5,
                    topK = 10,
                    maxOutputTokens = 200
                }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(requestBody, JsonOpts),
                Encoding.UTF8,
                "application/json");

            try
            {
                using (HttpResponseMessage response = await httpClient.PostAsync(Endpoint, content))
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

        private static string ExtractText(string json)
        {
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                if (!doc.RootElement.TryGetProperty("candidates", out var candidates) ||
                    candidates.GetArrayLength() == 0)
                {
                    return string.Empty;
                }

                if (!candidates[0].TryGetProperty("content", out var contentEl) ||
                    !contentEl.TryGetProperty("parts", out var parts))
                {
                    return string.Empty;
                }

                var sb = new StringBuilder();
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var text))
                    {
                        sb.Append(text.GetString());
                    }
                }
                return sb.ToString();
            }
        }
    }
}
