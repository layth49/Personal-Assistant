using Personal_Assistant.Dispatch;
using Personal_Assistant.SearxNGClient;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
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

        // Tool-detection requests are built from dictionaries with already-correct
        // OpenAI field names (tools, function, parameters, ...), so no naming
        // policy must be applied or those keys would be mangled.
        private static readonly JsonSerializerOptions RawJsonOpts = new JsonSerializerOptions();

        private const string ToolSystemPrompt =
            "You are L.A.I.T.H., a voice assistant intent router. " +
            "If the user's request matches one of the provided tools, call that tool " +
            "with the correct arguments extracted from what they said. " +
            "If no tool fits, just answer briefly without calling a tool. " +
            "Never invent argument values that the user did not provide.";

        // Detection timeout. Generous because a cold local model can be slow, but
        // bounded so the dispatcher can fall back to keyword matching if the
        // server is hung.
        private static readonly TimeSpan DetectTimeout = TimeSpan.FromSeconds(30);

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }

        // Conversational answer (grounded via SearxNG), matching the
        // Conversationalist delegate signature so IntentDispatcher can call it.
        public static async Task<string> GenerateResponse(
            string inputText,
            IReadOnlyList<ConversationTurn> history)
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
                messages = BuildMessages(systemPrompt, history, inputText),
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

        // Builds the OpenAI `messages` array: system prompt (with a note about any
        // tools already run this session), prior spoken turns (mapping our "model"
        // role to OpenAI's "assistant"), then the current user input. Tool-action
        // entries are deliberately NOT emitted as assistant messages — small
        // models imitate that text and start writing fake tool calls as prose.
        private static List<object> BuildMessages(
            string systemPrompt,
            IReadOnlyList<ConversationTurn> history,
            string inputText)
        {
            var messages = new List<object>
            {
                new { role = "system", content = AppendRecentActions(systemPrompt, history) }
            };
            if (history != null)
            {
                foreach (var turn in history)
                {
                    if (turn.IsTool) continue; // surfaced via the system note instead
                    string role = turn.Role == "model" ? "assistant" : "user";
                    messages.Add(new { role, content = turn.Text });
                }
            }
            messages.Add(new { role = "user", content = inputText });
            return messages;
        }

        // Folds any executed-tool entries into a system-prompt context note so the
        // model has follow-up context ("turn it back off") without ever seeing
        // tool-call-shaped text in the assistant role.
        private static string AppendRecentActions(
            string systemPrompt,
            IReadOnlyList<ConversationTurn> history)
        {
            if (history == null) return systemPrompt;
            var actions = new List<string>();
            foreach (var t in history)
            {
                if (t.IsTool) actions.Add(t.Text);
            }
            if (actions.Count == 0) return systemPrompt;
            return systemPrompt +
                "\n\nActions you already performed earlier this session (context only — do NOT " +
                "re-run, announce, or narrate them, and never write text in this format yourself): " +
                string.Join("; ", actions) + ".";
        }

        // Intent router for LLM-first dispatch on the local stack. Sends the tool
        // schemas as OpenAI `tools` and parses `tool_calls` from the response into
        // an LlmDecision, mirroring GeminiService.DetectToolAsync. Returns Failure
        // on timeout / transport / parse error so the dispatcher falls back to the
        // keyword matcher.
        public static async Task<LlmDecision> DetectToolAsync(
            string inputText,
            IReadOnlyList<ToolDefinition> tools,
            IReadOnlyList<ConversationTurn> history)
        {
            object requestBody = BuildToolRequest(inputText, tools, history);

            var content = new StringContent(
                JsonSerializer.Serialize(requestBody, RawJsonOpts),
                Encoding.UTF8,
                "application/json");

            string endpoint = $"{lmStudioUrl.TrimEnd('/')}/chat/completions";

            using (var cts = new CancellationTokenSource(DetectTimeout))
            {
                try
                {
                    using (HttpResponseMessage response =
                        await httpClient.PostAsync(endpoint, content, cts.Token))
                    {
                        string body = await response.Content.ReadAsStringAsync();

                        if (!response.IsSuccessStatusCode)
                        {
                            Console.WriteLine($"[llm] tool detect HTTP {(int)response.StatusCode}: {body}");
                            return LlmDecision.Failure();
                        }

                        return ParseDecision(body);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[llm] tool detect failed: {ex.Message}");
                    return LlmDecision.Failure();
                }
            }
        }

        private static object BuildToolRequest(
            string inputText,
            IReadOnlyList<ToolDefinition> tools,
            IReadOnlyList<ConversationTurn> history)
        {
            var toolList = new List<object>();
            foreach (var tool in tools)
            {
                var properties = new Dictionary<string, object>();
                var required = new List<string>();

                foreach (var p in tool.Parameters)
                {
                    var prop = new Dictionary<string, object>
                    {
                        ["type"] = p.Type,
                        ["description"] = p.Description
                    };
                    if (p.AllowedValues != null && p.AllowedValues.Count > 0)
                    {
                        prop["enum"] = p.AllowedValues;
                    }
                    properties[p.Name] = prop;
                    if (p.Required) required.Add(p.Name);
                }

                var parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = properties
                };
                if (required.Count > 0) parameters["required"] = required;

                toolList.Add(new Dictionary<string, object>
                {
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object>
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = parameters
                    }
                });
            }

            return new Dictionary<string, object>
            {
                // Reuse the same message builder (tool actions -> system note,
                // never assistant messages) as the conversational path.
                ["messages"] = BuildMessages(ToolSystemPrompt, history, inputText),
                ["tools"] = toolList,
                // "auto" lets the model pick a tool OR answer in text — the
                // LLM-first behaviour we want (not forced to call a tool).
                ["tool_choice"] = "auto",
                ["temperature"] = 0.0,
                ["max_tokens"] = 200,
                ["stream"] = false
            };
        }

        private static LlmDecision ParseDecision(string json)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                        choices.GetArrayLength() == 0 ||
                        !choices[0].TryGetProperty("message", out var message))
                    {
                        return LlmDecision.Failure();
                    }

                    // Tool call takes precedence over any text content.
                    if (message.TryGetProperty("tool_calls", out var toolCalls) &&
                        toolCalls.ValueKind == JsonValueKind.Array &&
                        toolCalls.GetArrayLength() > 0)
                    {
                        var first = toolCalls[0];
                        if (first.TryGetProperty("function", out var fn) &&
                            fn.TryGetProperty("name", out var nameEl))
                        {
                            string name = nameEl.GetString();
                            var args = ParseArguments(fn);
                            return LlmDecision.ToolCall(name, args);
                        }
                    }

                    // No tool -> plain reply.
                    if (message.TryGetProperty("content", out var contentEl) &&
                        contentEl.ValueKind == JsonValueKind.String)
                    {
                        return LlmDecision.Reply(contentEl.GetString());
                    }

                    return LlmDecision.Reply(string.Empty);
                }
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"[llm] tool detect parse error: {ex.Message}");
                return LlmDecision.Failure();
            }
        }

        // OpenAI returns function arguments as a JSON STRING; some servers return
        // an object directly. Handle both into a flat string map.
        private static Dictionary<string, string> ParseArguments(JsonElement fn)
        {
            var args = new Dictionary<string, string>();
            if (!fn.TryGetProperty("arguments", out var argsEl)) return args;

            JsonElement obj;
            if (argsEl.ValueKind == JsonValueKind.String)
            {
                string raw = argsEl.GetString();
                if (string.IsNullOrWhiteSpace(raw)) return args;
                try
                {
                    using (var argsDoc = JsonDocument.Parse(raw))
                    {
                        return FlattenArgs(argsDoc.RootElement);
                    }
                }
                catch (JsonException)
                {
                    return args;
                }
            }
            if (argsEl.ValueKind == JsonValueKind.Object)
            {
                obj = argsEl;
                return FlattenArgs(obj);
            }
            return args;
        }

        private static Dictionary<string, string> FlattenArgs(JsonElement obj)
        {
            var args = new Dictionary<string, string>();
            if (obj.ValueKind != JsonValueKind.Object) return args;
            foreach (var prop in obj.EnumerateObject())
            {
                args[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : prop.Value.GetRawText();
            }
            return args;
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
