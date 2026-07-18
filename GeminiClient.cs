using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Personal_Assistant.Dispatch;

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
            "You are L.A.I.T.H., Layth's personal voice assistant running on his computer. " +
            "Your responses are converted to speech, so: never use markdown, emojis, bullet points, " +
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
        // Gemini field names (function_declarations, enum, required, ...), so no
        // naming policy must be applied or those keys would be mangled.
        private static readonly JsonSerializerOptions RawJsonOpts = new JsonSerializerOptions();

        private const string ToolSystemPrompt =
            "You are L.A.I.T.H., a voice assistant. Work out how to accomplish the user's " +
            "request using the provided tools, then emit the tool call(s) that achieve it.\n" +
            "- If one tool matches, call it.\n" +
            "- If the request needs several actions, call all the matching tools, one call per action.\n" +
            "- If NO single tool directly matches but the request can be accomplished by combining " +
            "the tools you have, figure out the sequence of calls that achieves it and emit them. " +
            "For example, there is no 'flash the light' tool, but you can turn the light on, use the " +
            "wait tool to pause briefly, then turn it off — and use the repeat tool to loop that " +
            "sequence to flash several times. Think about which primitive actions add up to what the " +
            "user asked for.\n" +
            "- Only if the request genuinely can't be done with any combination of the tools, don't " +
            "call a tool — just answer briefly.\n" +
            "Never invent tools, and never invent argument values the user did not provide.";

        // How long to wait for the tool-detection call before giving up so the
        // dispatcher can fall back to keyword matching. Shorter than the general
        // HttpClient timeout because intent routing should feel instant.
        private static readonly TimeSpan DetectTimeout = TimeSpan.FromSeconds(15);

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

        public static async Task<string> GenerateGeminiResponse(
            string inputText,
            IReadOnlyList<ConversationTurn> history)
        {
            var requestBody = new
            {
                system_instruction = new { parts = new[] { new { text = SystemPrompt } } },
                contents = BuildContents(inputText, history),
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

        // Intent router for LLM-first dispatch. Sends the user input plus the tool
        // schemas (as Gemini function_declarations) and returns either a tool call
        // the model chose, a plain reply (no tool fit), or a Failure the dispatcher
        // treats as "fall back to keyword matching".
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

            using (var cts = new CancellationTokenSource(DetectTimeout))
            {
                try
                {
                    using (HttpResponseMessage response =
                        await httpClient.PostAsync(Endpoint, content, cts.Token))
                    {
                        string body = await response.Content.ReadAsStringAsync();

                        if (!response.IsSuccessStatusCode)
                        {
                            Console.WriteLine($"[gemini] tool detect HTTP {(int)response.StatusCode}: {body}");
                            return LlmDecision.Failure();
                        }

                        return ParseDecision(body);
                    }
                }
                catch (Exception ex)
                {
                    // Timeout (TaskCanceledException) or any transport/parse error
                    // -> let the dispatcher fall back to the keyword matcher.
                    Console.WriteLine($"[gemini] tool detect failed: {ex.Message}");
                    return LlmDecision.Failure();
                }
            }
        }

        // Renders history + the current input as Gemini `contents`, oldest first,
        // current user turn last. Spoken turns are {role, parts:[text]}; an
        // executed tool renders as its native pair — a model `functionCall` part
        // followed by a user `functionResponse` part — so the model sees a real
        // prior call to follow (not imitable text) while alternation holds.
        private static object[] BuildContents(string inputText, IReadOnlyList<ConversationTurn> history)
        {
            var contents = new List<object>();
            if (history != null)
            {
                foreach (var turn in history)
                {
                    if (turn.IsTool)
                    {
                        contents.Add(new
                        {
                            role = "model",
                            parts = new object[] { new { functionCall = new { name = turn.ToolName, args = turn.ToolArgs } } }
                        });
                        contents.Add(new
                        {
                            role = "user",
                            parts = new object[] { new { functionResponse = new { name = turn.ToolName, response = new { result = "done" } } } }
                        });
                        continue;
                    }
                    contents.Add(new { role = turn.Role, parts = new[] { new { text = turn.Text } } });
                }
            }
            contents.Add(new { role = "user", parts = new[] { new { text = inputText } } });
            return contents.ToArray();
        }

        private static object BuildToolRequest(
            string inputText,
            IReadOnlyList<ToolDefinition> tools,
            IReadOnlyList<ConversationTurn> history)
        {
            var functionDeclarations = new List<object>();
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

                functionDeclarations.Add(new Dictionary<string, object>
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = parameters
                });
            }

            var contents = new List<object>();
            if (history != null)
            {
                foreach (var turn in history)
                {
                    if (turn.IsTool)
                    {
                        contents.Add(new Dictionary<string, object>
                        {
                            ["role"] = "model",
                            ["parts"] = new object[]
                            {
                                new Dictionary<string, object>
                                {
                                    ["functionCall"] = new Dictionary<string, object>
                                    {
                                        ["name"] = turn.ToolName,
                                        ["args"] = turn.ToolArgs
                                    }
                                }
                            }
                        });
                        contents.Add(new Dictionary<string, object>
                        {
                            ["role"] = "user",
                            ["parts"] = new object[]
                            {
                                new Dictionary<string, object>
                                {
                                    ["functionResponse"] = new Dictionary<string, object>
                                    {
                                        ["name"] = turn.ToolName,
                                        ["response"] = new Dictionary<string, object> { ["result"] = "done" }
                                    }
                                }
                            }
                        });
                        continue;
                    }
                    contents.Add(new Dictionary<string, object>
                    {
                        ["role"] = turn.Role,
                        ["parts"] = new[] { new Dictionary<string, object> { ["text"] = turn.Text } }
                    });
                }
            }
            contents.Add(new Dictionary<string, object>
            {
                ["role"] = "user",
                ["parts"] = new[] { new Dictionary<string, object> { ["text"] = inputText } }
            });

            return new Dictionary<string, object>
            {
                ["system_instruction"] = new Dictionary<string, object>
                {
                    ["parts"] = new[] { new Dictionary<string, object> { ["text"] = ToolSystemPrompt } }
                },
                ["contents"] = contents.ToArray(),
                ["tools"] = new[]
                {
                    new Dictionary<string, object> { ["function_declarations"] = functionDeclarations }
                },
                // AUTO lets the model pick a tool OR answer in text — exactly the
                // LLM-first behaviour we want (it isn't forced to call a tool).
                ["tool_config"] = new Dictionary<string, object>
                {
                    ["function_calling_config"] = new Dictionary<string, object> { ["mode"] = "AUTO" }
                },
                ["generationConfig"] = new Dictionary<string, object>
                {
                    // A little warmth so the router can reason about composing
                    // tools for requests with no direct tool (e.g. "flash the
                    // light" -> on then off), while staying stable for ordinary
                    // routing.
                    ["temperature"] = 0.3,
                    // Headroom for several tool calls in one compound request
                    // (plus Gemini's hidden thinking tokens).
                    ["maxOutputTokens"] = 512
                }
            };
        }

        private static LlmDecision ParseDecision(string json)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    if (!doc.RootElement.TryGetProperty("candidates", out var candidates) ||
                        candidates.GetArrayLength() == 0 ||
                        !candidates[0].TryGetProperty("content", out var contentEl) ||
                        !contentEl.TryGetProperty("parts", out var parts))
                    {
                        return LlmDecision.Failure();
                    }

                    var textBuilder = new StringBuilder();
                    var calls = new List<ToolInvocation>();

                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("functionCall", out var fc) &&
                            fc.TryGetProperty("name", out var nameEl))
                        {
                            string name = nameEl.GetString();
                            var args = new Dictionary<string, string>();

                            if (fc.TryGetProperty("args", out var argsEl) &&
                                argsEl.ValueKind == JsonValueKind.Object)
                            {
                                foreach (var arg in argsEl.EnumerateObject())
                                {
                                    args[arg.Name] = arg.Value.ValueKind == JsonValueKind.String
                                        ? arg.Value.GetString()
                                        : arg.Value.GetRawText();
                                }
                            }

                            // Collect every functionCall part — Gemini emits one
                            // per action for a compound request.
                            calls.Add(new ToolInvocation(name, args));
                            continue;
                        }

                        if (part.TryGetProperty("text", out var textEl))
                        {
                            textBuilder.Append(textEl.GetString());
                        }
                    }

                    if (calls.Count > 0) return LlmDecision.Tools(calls);
                    return LlmDecision.Reply(textBuilder.ToString());
                }
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"[gemini] tool detect parse error: {ex.Message}");
                return LlmDecision.Failure();
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
