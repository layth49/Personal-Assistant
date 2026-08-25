using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Personal_Assistant.Configuration;
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

        // gemini-2.5-flash-lite 404s — "no longer available to new users" — which
        // silently broke this path, and this path is the fallback that only runs
        // when the Live socket is already failing.
        //
        // Declared, not parsed back out of the URL: the grounding check below
        // needs the model id, and recovering it with string-slicing meant any
        // change to the URL shape would silently yield a wrong id and fail OPEN,
        // re-enabling grounding on a model that 429s for it.
        public const string ModelId = "gemini-3.1-flash-lite";

        private const string Endpoint =
            "https://generativelanguage.googleapis.com/v1beta/models/" + ModelId + ":generateContent";

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
        //
        // This budget covers the retry below as well, deliberately: an empty
        // candidate should cost one more round trip, not a second full 15s.
        private static readonly TimeSpan DetectTimeout = TimeSpan.FromSeconds(15);

        // Thinking-token allowance for the router. -1 leaves it to the model,
        // which is the default and today's behaviour; 0 suppresses thinking.
        //
        // Measured on gemini-3.1-flash-lite 2026-08-25: `thinkingBudget` IS a
        // real, validated field here — 0 returns 200, and -5 is rejected with
        // "thinking_budget must be in the range [-1, 65535]" (and an unknown
        // field name 400s too, so acceptance is meaningful, not the API
        // shrugging). What could NOT be shown is that setting 0 helps: this
        // model reports no `thoughtsTokenCount` at all, and a budget of 0 still
        // came back carrying a `thoughtSignature`.
        //
        // So it ships OFF. Suppressing thinking on a 47-tool AUTO router is a
        // change that could quietly degrade tool choice, and the empty-candidate
        // cause is still a hypothesis. The plumbing is here to make the
        // experiment one config edit; it is not a fix being applied blind.
        private static int ThinkingBudget => LaithConfig.Int("ThinkingBudget", -1, -1, 65535);

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

        // Whether Google Search grounding can be requested for a given model on
        // THIS project. Measured 2026-08-04: every Gemini 3.x request carrying
        // `google_search` returns 429 "exceeded your current quota" — the same
        // request without it succeeds, and 2.5 grounding works fine. So the 3.x
        // grounding allowance (documented as 5,000/month shared across 3.x) is
        // not granted here.
        //
        // This matters more than it looks: asking for grounding a model cannot do
        // is not a degraded answer, it is a hard failure of the whole request. It
        // took out the Live session at setup AND the turn-based fallback, so an
        // outage would have found no safety net. The `Grounding` setting forces it
        // either way if the allowance ever appears.
        public static bool GroundingAvailableFor(string model)
        {
            bool? forced = LaithConfig.TriState("Grounding");
            if (forced.HasValue) return forced.Value;
            return !(model ?? string.Empty).StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase);
        }

        // Appended to a system prompt whenever grounding is unavailable. Without
        // it the model still answers questions about the world — from training
        // data, confidently, with nothing to show the answer wasn't looked up.
        // That is how a wrong Re:Zero release date got stated as fact.
        public const string NoSearchCaveat =
            " You do NOT have web search available. For anything that depends on current or recent " +
            "information — news, release dates, prices, scores, \"latest\" anything — say you can't " +
            "look it up right now instead of answering from memory. Answering from memory and " +
            "sounding certain is the worst thing you can do here.";

        public static async Task<string> GenerateGeminiResponse(
            string inputText,
            IReadOnlyList<ConversationTurn> history)
        {
            var requestBody = new Dictionary<string, object>
            {
                ["system_instruction"] = new
                {
                    parts = new[]
                    {
                        new { text = GroundingAvailableFor(ModelId) ? SystemPrompt : SystemPrompt + NoSearchCaveat }
                    }
                },
                ["contents"] = BuildContents(inputText, history),
                // Google Search grounding — Gemini will run a web search when it
                // helps answer the question, then cite the result. Lets the
                // assistant answer about current events / facts past the model
                // cutoff. Tool name is google_search (the snake_case form Gemini
                // 2.0+ uses; the older googleSearchRetrieval is for 1.5 only).
                // Omitted entirely when the configured model has no grounding
                // allowance — see GroundingAvailableFor.
                ["generationConfig"] = new
                {
                    temperature = 0.5,
                    topP = 0.5,
                    topK = 10,
                    maxOutputTokens = 200
                }
            };

            if (GroundingAvailableFor(ModelId))
            {
                requestBody["tools"] = new[] { new { google_search = new { } } };
            }

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

        // Rewrites or answers about a block of text under an explicit
        // instruction, and returns the result verbatim.
        //
        // Deliberately does NOT use SystemPrompt: that one is built for speech
        // ("never use markdown", "default to one short sentence", 200 output
        // tokens), which is exactly wrong for reorganising a markdown note —
        // it would strip the formatting and truncate anything substantial.
        public static async Task<string> TransformTextAsync(
            string instruction, string content, int maxOutputTokens = 4096)
        {
            var requestBody = new Dictionary<string, object>
            {
                ["system_instruction"] = new
                {
                    parts = new[]
                    {
                        new { text =
                            "You transform text exactly as instructed and return ONLY the " +
                            "result — no preamble, no commentary, no markdown code fences " +
                            "around the whole answer. Preserve the author's meaning, wording " +
                            "and any facts; you are reorganising, not rewriting from scratch, " +
                            "and you never invent content that wasn't there." }
                    }
                },
                ["contents"] = new object[]
                {
                    new
                    {
                        role = "user",
                        parts = new object[]
                        {
                            new { text = instruction + "\n\n---\n" + (content ?? string.Empty) }
                        }
                    }
                },
                ["generationConfig"] = new
                {
                    temperature = 0.2,
                    maxOutputTokens = maxOutputTokens
                }
            };

            var body = new StringContent(
                JsonSerializer.Serialize(requestBody, JsonOpts),
                Encoding.UTF8,
                "application/json");

            try
            {
                using (HttpResponseMessage response = await httpClient.PostAsync(Endpoint, body))
                {
                    string json = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"[gemini] transform HTTP {(int)response.StatusCode}: {json}");
                        return null;
                    }
                    return ExtractText(json);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[gemini] transform failed: {ex.Message}");
                return null;
            }
        }

        // Answers a question about a single image (e.g. a screenshot) using the
        // same model and endpoint as the text path, with an inlineData part
        // alongside the question. No history, no tools, no grounding — this is a
        // one-shot "what am I looking at" call.
        public static async Task<string> AskAboutImageAsync(string question, byte[] pngBytes)
        {
            var requestBody = new Dictionary<string, object>
            {
                ["contents"] = new object[]
                {
                    new
                    {
                        role = "user",
                        parts = new object[]
                        {
                            new { text = question },
                            new
                            {
                                inlineData = new
                                {
                                    mimeType = "image/png",
                                    data = Convert.ToBase64String(pngBytes)
                                }
                            }
                        }
                    }
                },
                ["generationConfig"] = new
                {
                    temperature = 0.2,
                    maxOutputTokens = 300
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
                        Console.WriteLine($"[gemini] vision call HTTP {(int)response.StatusCode}: {body}");
                        return null;
                    }

                    return ExtractText(body);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[gemini] vision call failed: {ex.Message}");
                return null;
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

            using (var cts = new CancellationTokenSource(DetectTimeout))
            {
                try
                {
                    // Two attempts, because the empty candidate is intermittent:
                    // a byte-identical request has come back empty twice and then
                    // answered correctly on the third try. One cheap retry turns
                    // most of those into a working turn instead of a silent drop
                    // to the keyword matcher.
                    for (int attempt = 1; ; attempt++)
                    {
                        // Rebuilt per attempt: a StringContent is consumed by the
                        // send and cannot be posted twice.
                        var content = new StringContent(
                            JsonSerializer.Serialize(requestBody, RawJsonOpts),
                            Encoding.UTF8,
                            "application/json");

                        using (HttpResponseMessage response =
                            await httpClient.PostAsync(Endpoint, content, cts.Token))
                        {
                            string body = await response.Content.ReadAsStringAsync();

                            if (!response.IsSuccessStatusCode)
                            {
                                Console.WriteLine(
                                    $"[gemini] tool detect HTTP {(int)response.StatusCode}: {DescribeError(body)}");
                                return LlmDecision.Failure();
                            }

                            LlmDecision decision = ParseDecision(body, out bool emptyCandidate);
                            if (!emptyCandidate) return decision;

                            if (attempt >= 2)
                            {
                                Console.WriteLine("[gemini] tool detect empty on retry too -> keyword fallback");
                                return LlmDecision.Failure();
                            }
                            Console.WriteLine("[gemini] tool detect retrying once");
                        }
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

        // Serialises tool schemas into Gemini `function_declarations` entries.
        // Shared by the turn-based path below and the Live API session's `setup`
        // message (LiveClient.cs) so there is exactly one tool serialiser — the
        // two transports must never disagree about what a tool looks like.
        public static List<object> BuildFunctionDeclarations(IReadOnlyList<ToolDefinition> tools)
        {
            var functionDeclarations = new List<object>();
            if (tools == null) return functionDeclarations;

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

            return functionDeclarations;
        }

        private static object BuildToolRequest(
            string inputText,
            IReadOnlyList<ToolDefinition> tools,
            IReadOnlyList<ConversationTurn> history)
        {
            List<object> functionDeclarations = BuildFunctionDeclarations(tools);

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

            var request = new Dictionary<string, object>
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

            // Only sent when explicitly configured. -1 is the model's own
            // default, and omitting the field entirely is not the same request
            // as sending -1, so the default path stays byte-identical to what
            // shipped before this was added.
            if (ThinkingBudget >= 0)
            {
                ((Dictionary<string, object>)request["generationConfig"])["thinkingConfig"] =
                    new Dictionary<string, object> { ["thinkingBudget"] = ThinkingBudget };
            }

            return request;
        }

        // `emptyCandidate` reports the specific 200-but-nothing-generated case —
        // HTTP 200, finishReason STOP, a candidate whose `content` has no `parts`
        // key at all. That shape used to return a bare Failure() with no logging,
        // so the turn silently fell through to the keyword matcher and the only
        // console line came from the HTTP-error branch, which a 200 never takes.
        // It is reported separately from a parse error because it is the one
        // failure worth retrying.
        private static LlmDecision ParseDecision(string json, out bool emptyCandidate)
        {
            emptyCandidate = false;
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    if (!doc.RootElement.TryGetProperty("candidates", out var candidates) ||
                        candidates.GetArrayLength() == 0 ||
                        !candidates[0].TryGetProperty("content", out var contentEl) ||
                        !contentEl.TryGetProperty("parts", out var parts))
                    {
                        emptyCandidate = true;
                        Console.WriteLine($"[gemini] tool detect got 200 with no parts — {DescribeEmptyResponse(json)}");
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

        // The model used to check whether a real-world event has actually
        // happened. Pinned SEPARATELY from ModelId, and deliberately not allowed
        // to inherit it.
        //
        // ModelId is gemini-3.1-flash-lite, and GroundingAvailableFor documents
        // that every 3.x request carrying google_search 429s on this project. A
        // verification that quietly ran without search would not be a degraded
        // check — it would be the model answering from training data and sounding
        // certain, which is precisely how a wrong Re:Zero release date got stated
        // as fact (see NoSearchCaveat). For this call, no search means no answer.
        public static string VerifyModelId =>
            LaithConfig.Text("EventVerifyModel", "gemini-2.5-flash");

        /// <summary>
        /// Asks, with web search, whether <paramref name="subject"/> has happened
        /// yet. Never throws: every failure becomes an Unknown carrying the
        /// reason, because the caller's job is to decide whether to re-check and
        /// "the lookup broke" and "the event hasn't happened" must not look the
        /// same to it.
        /// </summary>
        public static async Task<EventVerdict> VerifyEventAsync(
            string subject, CancellationToken cancel = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(subject))
            {
                return EventVerdict.Unknown("no subject to check");
            }

            string model = VerifyModelId;

            // The one refusal that matters. Everything below assumes the answer
            // came from a search; without grounding this call can only launder
            // training data into something that reads like a fact-check.
            if (!GroundingAvailableFor(model))
            {
                return EventVerdict.Unknown(
                    $"grounding is unavailable for {model}, and an unsearched answer is worthless here");
            }

            string endpoint =
                "https://generativelanguage.googleapis.com/v1beta/models/" + model + ":generateContent";

            // A rigid one-line format rather than JSON mode: structured output and
            // the google_search tool cannot be requested together, and losing
            // search to gain a schema is the wrong half of that trade. One line
            // with two pipes is something a parser can be lenient about.
            string instruction =
                "You check whether a specific real-world event has already happened. " +
                $"Today is {DateTime.Now:dddd d MMMM yyyy}, local time {DateTime.Now:HH:mm}. " +
                "You have Google Search: use it, and answer only from what you find. " +
                "If the search results do not settle it, say UNKNOWN — never guess from memory.";

            string question =
                $"Event: {subject}\n\n" +
                "Has it happened yet? Reply with EXACTLY one line, nothing else, in this format:\n" +
                "STATUS | one short sentence | url\n" +
                "STATUS is HAPPENED, NOT_YET, or UNKNOWN. " +
                "The sentence is spoken aloud, so: under 20 words, no markdown, no asterisks. " +
                "The url is a single direct link to where it can be watched or read, or the word NONE.";

            var requestBody = new Dictionary<string, object>
            {
                ["system_instruction"] = new { parts = new[] { new { text = instruction } } },
                ["contents"] = new[]
                {
                    new { role = "user", parts = new[] { new { text = question } } }
                },
                ["tools"] = new[] { new { google_search = new { } } },
                ["generationConfig"] = new
                {
                    // Low but not zero: this is a lookup, and creative rephrasing
                    // of a release date is the failure mode.
                    temperature = 0.1,
                    // Roomy on purpose. 2.5 spends part of this budget thinking
                    // before it writes, and a cap tight enough for one line of
                    // output comes back empty rather than short.
                    maxOutputTokens = 800
                }
            };

            try
            {
                var content = new StringContent(
                    JsonSerializer.Serialize(requestBody, RawJsonOpts), Encoding.UTF8, "application/json");

                using (HttpResponseMessage response =
                    await httpClient.PostAsync(endpoint, content, cancel).ConfigureAwait(false))
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        return EventVerdict.Unknown($"{(int)response.StatusCode} {response.ReasonPhrase}");
                    }
                    return EventVerdict.Parse(ExtractText(body));
                }
            }
            catch (OperationCanceledException)
            {
                return EventVerdict.Unknown("the check was cancelled");
            }
            catch (Exception ex)
            {
                return EventVerdict.Unknown(ex.Message);
            }
        }

        // Turns a 200 that produced nothing into one greppable line: why the model
        // stopped, whether the prompt was blocked, and how the token budget was
        // actually spent. Thinking tokens count against maxOutputTokens, so
        // `thoughts` alone consuming the allowance is a real and otherwise
        // invisible cause of an empty candidate — note that gemini-3.1-flash-lite
        // does not currently report thoughtsTokenCount, so that field reads 0
        // here rather than being absent.
        private static string DescribeEmptyResponse(string json)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    var sb = new StringBuilder();

                    if (root.TryGetProperty("candidates", out var candidates) &&
                        candidates.GetArrayLength() > 0)
                    {
                        var c = candidates[0];
                        sb.Append("finishReason=");
                        sb.Append(c.TryGetProperty("finishReason", out var fr) ? fr.GetString() : "(none)");
                        if (c.TryGetProperty("content", out var contentEl))
                        {
                            sb.Append(contentEl.TryGetProperty("parts", out _)
                                ? ", parts present but unusable"
                                : ", content has no parts");
                        }
                        else
                        {
                            sb.Append(", candidate has no content");
                        }
                    }
                    else
                    {
                        sb.Append("no candidates");
                    }

                    if (root.TryGetProperty("promptFeedback", out var pf) &&
                        pf.TryGetProperty("blockReason", out var br))
                    {
                        sb.Append($", blockReason={br.GetString()}");
                    }

                    if (root.TryGetProperty("usageMetadata", out var um))
                    {
                        sb.Append(", tokens: ");
                        sb.Append($"prompt={ReadInt(um, "promptTokenCount")} ");
                        sb.Append($"thoughts={ReadInt(um, "thoughtsTokenCount")} ");
                        sb.Append($"output={ReadInt(um, "candidatesTokenCount")}");
                    }

                    return sb.ToString();
                }
            }
            catch (JsonException)
            {
                return "unparseable body: " + Truncate(json, 300);
            }
        }

        private static string ReadInt(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
                ? el.GetInt32().ToString()
                : "0";

        // Pulls the human-readable message out of an API error body, and calls out
        // quota exhaustion by name with its retry delay — the free tier is small
        // enough that a 429 is an ordinary, expected failure, not an anomaly, and
        // the quotaId says WHICH limit was hit.
        private static string DescribeError(string json)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    if (!doc.RootElement.TryGetProperty("error", out var err))
                        return Truncate(json, 300);

                    string status = err.TryGetProperty("status", out var s) ? s.GetString() : null;
                    string message = err.TryGetProperty("message", out var m) ? m.GetString() : "";

                    if (status == "RESOURCE_EXHAUSTED")
                    {
                        string quota = null, retry = null;
                        if (err.TryGetProperty("details", out var details))
                        {
                            foreach (var d in details.EnumerateArray())
                            {
                                if (d.TryGetProperty("violations", out var violations))
                                {
                                    foreach (var v in violations.EnumerateArray())
                                    {
                                        if (v.TryGetProperty("quotaId", out var qid)) quota = qid.GetString();
                                    }
                                }
                                if (d.TryGetProperty("retryDelay", out var rd)) retry = rd.GetString();
                            }
                        }
                        return $"QUOTA EXHAUSTED ({quota ?? "unknown quota"})" +
                               (retry != null ? $", retry in {retry}" : "");
                    }

                    return $"{status ?? "error"}: {Truncate(message, 300)}";
                }
            }
            catch (JsonException)
            {
                return Truncate(json, 300);
            }
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "...";

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

    // What a grounded check found out about a real-world event.
    public sealed class EventVerdict
    {
        public enum Outcome
        {
            Happened, // the search says it has
            NotYet,   // the search says it hasn't
            Unknown   // the search didn't settle it, or the lookup failed
        }

        public Outcome Result { get; private set; }

        // One speakable sentence, or the failure reason when Result is Unknown.
        public string Detail { get; private set; }

        // Where to go to see it, or null. Only ever populated on Happened —
        // offering to open a link for something that has not been released is
        // offering to open a 404.
        public string Url { get; private set; }

        public static EventVerdict Unknown(string reason) =>
            new EventVerdict { Result = Outcome.Unknown, Detail = reason };

        /// <summary>
        /// Reads the one-line "STATUS | sentence | url" reply. Lenient about
        /// everything except the status word: a model that wraps the line in
        /// quotes, adds a full stop, or omits the url is still understood, but
        /// one whose status cannot be read is Unknown rather than assumed — the
        /// whole point of this type is that "it happened" is never a default.
        /// </summary>
        public static EventVerdict Parse(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply))
            {
                return Unknown("the check came back empty");
            }

            // Models sometimes prepend a courtesy line. The one that matters is
            // the first containing a status word.
            string line = reply
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(l => l.IndexOf("HAPPENED", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     l.IndexOf("NOT_YET", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     l.IndexOf("UNKNOWN", StringComparison.OrdinalIgnoreCase) >= 0);

            if (line == null) return Unknown("the check didn't answer in the expected form");

            string[] parts = line.Split('|');
            string status = parts[0].Trim().Trim('"', '*', '`', '.').ToUpperInvariant();
            string detail = parts.Length > 1 ? parts[1].Trim().Trim('"', '*', '`') : null;
            string url = parts.Length > 2 ? parts[2].Trim().Trim('"', '*', '`', '.', ',') : null;

            if (string.IsNullOrWhiteSpace(url) ||
                url.Equals("NONE", StringComparison.OrdinalIgnoreCase) ||
                !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                  url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                // Anything that is not plainly an http(s) URL is discarded rather
                // than repaired. This value ends up in Process.Start, and guessing
                // at a half-formed one is how a search result becomes a command.
                url = null;
            }

            // NOT_YET is checked before HAPPENED: the string "NOT_YET" contains
            // neither as a substring of the other, but a sentence like "has not
            // happened yet" trips a naive Contains("HAPPENED") on the whole line.
            if (status.Contains("NOT_YET") || status.Contains("NOT YET"))
            {
                return new EventVerdict { Result = Outcome.NotYet, Detail = detail };
            }
            if (status.Contains("UNKNOWN"))
            {
                return Unknown(detail ?? "the search didn't settle it");
            }
            if (status.Contains("HAPPENED"))
            {
                return new EventVerdict { Result = Outcome.Happened, Detail = detail, Url = url };
            }

            return Unknown("the check didn't answer in the expected form");
        }
    }
}
