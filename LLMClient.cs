using Personal_Assistant.Dispatch;
using Personal_Assistant.SearxNGClient;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Personal_Assistant.LLMClient
{
    // Cuts a growing token buffer into speakable pieces. Kokoro sounds best when
    // handed whole sentences (it gets the prosody right and the seams between
    // chunks land where a speaker would naturally pause), so we buffer tokens
    // until a sentence actually closes rather than synthesising per token.
    internal static class SentenceChunker
    {
        // Only the FIRST chunk needs to be short — it sets time-to-first-audio.
        // After that, short chunks are actively harmful: each one is its own
        // Kokoro round trip (GPU work) and leaves less synthesis lead time before
        // the previous chunk finishes playing, which is what makes streamed audio
        // stutter on a loaded machine. So later chunks are deliberately bigger.
        private const int FirstMinChars = 30;
        private const int FirstMaxChars = 220;
        private const int LaterMinChars = 140;
        private const int LaterMaxChars = 400;

        // Removes and returns the next speakable chunk. `flush` drains whatever
        // is left once the model has stopped emitting. `isFirst` selects the
        // low-latency sizing for the opening chunk.
        public static bool TryTake(StringBuilder buffer, bool flush, bool isFirst, out string sentence)
        {
            int minChars = isFirst ? FirstMinChars : LaterMinChars;
            int maxChars = isFirst ? FirstMaxChars : LaterMaxChars;
            sentence = null;
            while (buffer.Length > 0)
            {
                string text = buffer.ToString();
                int cut;

                if (flush)
                {
                    cut = text.Length;
                }
                else
                {
                    cut = FindSentenceEnd(text, minChars);
                    if (cut < 0) cut = FindOverflowCut(text, minChars, maxChars);
                    if (cut < 0) return false;
                }

                buffer.Remove(0, cut);
                string candidate = Normalize(text.Substring(0, cut));
                if (candidate.Length > 0)
                {
                    sentence = candidate;
                    return true;
                }
                // Whitespace-only slice — consume it and keep looking.
            }
            return false;
        }

        // Index just past a sentence terminator that is genuinely followed by
        // whitespace, so "72.5 degrees" and "3. Turn" aren't mistaken for ends.
        private static int FindSentenceEnd(string text, int minChars)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (c == '\n')
                {
                    if (i + 1 >= minChars) return i + 1;
                    continue;
                }

                if (c != '.' && c != '!' && c != '?') continue;
                if (c == '.' && IsAbbreviation(text, i)) continue;

                // Let closing quotes/brackets ride along with the sentence.
                int j = i + 1;
                while (j < text.Length && (text[j] == '"' || text[j] == '\'' || text[j] == ')')) j++;

                // Terminator is the last thing we have so far — wait for the next
                // token before deciding, so we can see what follows it.
                if (j >= text.Length) return -1;
                if (!char.IsWhiteSpace(text[j])) continue;
                if (j >= minChars) return j;
            }
            return -1;
        }

        // Abbreviations that end in a period without ending a sentence. Splitting
        // on these puts an audible pause inside a name ("Dr. | Smith"), which is
        // worse than the opposite mistake of running two sentences together.
        private static readonly string[] Abbreviations =
        {
            "mr", "mrs", "ms", "dr", "prof", "st", "jr", "sr",
            "vs", "etc", "inc", "ltd", "fig", "approx"
        };

        private static bool IsAbbreviation(string text, int dotIndex)
        {
            int start = dotIndex;
            while (start > 0 && char.IsLetter(text[start - 1])) start--;
            int len = dotIndex - start;
            if (len == 0) return false;

            // "J. Smith" — a lone capital is an initial, not a sentence end.
            if (len == 1 && char.IsUpper(text[start])) return true;
            // A letter already preceded by a dot: the tail of "e.g." / "a.m." / "U.S.A."
            if (len == 1 && start > 0 && text[start - 1] == '.') return true;
            if (len > 6) return false;

            string word = text.Substring(start, len).ToLowerInvariant();
            foreach (string a in Abbreviations)
            {
                if (a == word) return true;
            }
            return false;
        }

        private static int FindOverflowCut(string text, int minChars, int maxChars)
        {
            if (text.Length < maxChars) return -1;
            int space = text.LastIndexOf(' ', maxChars - 1);
            return space > minChars ? space + 1 : maxChars;
        }

        // Collapses newlines and runs of spaces to single spaces: the model's line
        // breaks mean nothing to Kokoro, and the bubble wraps on spaces only.
        private static string Normalize(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool pendingSpace = false;
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) { pendingSpace = true; continue; }
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append(c);
            }
            return sb.ToString();
        }
    }

    public class LocalLLMService
    {
        // 127.0.0.1, not "localhost" — the same trap the STT client documents, and
        // LM Studio is the only other service it applies to: it's a GUI process on
        // the host binding 127.0.0.1 only, so it has no IPv6 socket, while the
        // Docker-published services (SearxNG, Kokoro) do and are unaffected.
        // "localhost" resolves to ::1 first on Windows, so the connect waits for
        // that refusal. Measured on this box: 2125ms for the first call against
        // localhost vs 14ms against 127.0.0.1. It's paid once per process rather
        // than per request — the connection pool reuses the socket afterwards, and
        // WarmUpAsync eats it — but it's ~2s of startup for a one-word fix.
        public static readonly string lmStudioUrl =
            Environment.GetEnvironmentVariable("LMSTUDIO_URL") ?? "http://127.0.0.1:1234/v1";

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
            "An occasional emoji is fine (it shows in the on-screen bubble and isn't spoken), " +
            "but don't overuse them. " +
            "Default to one short sentence. Only give more detail if the user asks for it, " +
            "asks a multi-part question, or the answer genuinely requires it (e.g. instructions, comparisons). " +
            "Lead with the answer or result first, then explain if needed — never bury the answer at the end. " +
            "If a tool/function is available that matches what the user wants, call it directly rather than " +
            "describing what you would do. Only respond conversationally when no tool fits or the user is " +
            "just chatting. " +
            "If voice input is garbled, ambiguous, or doesn't clearly match a command or question, briefly " +
            "ask for clarification instead of guessing. " +
            "Tone is direct and casual, like a capable assistant who knows Layth well — not stiff or overly formal. " +
            "Never fabricate information; if you don't know or aren't sure, say so plainly. /no think";

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        // Tool-detection requests are built from dictionaries with already-correct
        // OpenAI field names (tools, function, parameters, ...), so no naming
        // policy must be applied or those keys would be mangled.
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

        // Pokes LM Studio with a throwaway one-token completion at startup.
        //
        // The first request after a model load pays for weights being paged in
        // and the KV cache being allocated; on the 4050 that's seconds the very
        // first real turn would otherwise spend, on top of an already-cold STT
        // and TTS. Same idea as SpeechService.WarmUpAudioAsync, and equally
        // best-effort: LM Studio being down at launch is not a startup failure,
        // it just means the first answer is slow (or an error, as before).
        public static async Task WarmUpAsync()
        {
            var requestBody = new
            {
                messages = new List<object>
                {
                    new Dictionary<string, object> { ["role"] = "user", ["content"] = "hi" }
                },
                max_tokens = 1,
                temperature = 0,
                stream = false
            };

            var sw = Stopwatch.StartNew();
            try
            {
                var content = new StringContent(
                    JsonSerializer.Serialize(requestBody, JsonOpts),
                    Encoding.UTF8,
                    "application/json");

                using (var response = await httpClient
                    .PostAsync($"{lmStudioUrl.TrimEnd('/')}/chat/completions", content)
                    .ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine(
                            $"[llm] warm-up skipped: {(int)response.StatusCode} {response.ReasonPhrase}");
                        return;
                    }
                    // Drain the body so the connection goes back to the pool warm.
                    await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
                Console.WriteLine($"[llm] warm-up done in {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[llm] warm-up failed (non-fatal): {ex.Message}");
            }
        }

        // Conversational answer, matching the Conversationalist delegate
        // signature so IntentDispatcher can call it. No web search here — that
        // used to run unconditionally on every miss (including things that
        // should've been a tool call, e.g. a misrouted "turn off my lights"),
        // paying a SearxNG round trip whether or not it was needed. Grounding
        // now goes through the explicit `web_search` tool the router calls when
        // IT decides the request needs current/external info.
        public static Task<string> GenerateResponse(
            string inputText,
            IReadOnlyList<ConversationTurn> history) =>
            AnswerAsync(inputText, null, history);

        // Answers using search hits the caller already fetched — what the
        // `web_search` tool handler calls after running a SearxNG search.
        public static Task<string> AnswerWithSearchResults(
            string inputText,
            List<SearchHit> hits,
            IReadOnlyList<ConversationTurn> history) =>
            AnswerAsync(inputText, hits, history);

        // Streaming twins of the two methods above. They invoke `onSentence` for
        // each speakable chunk the moment it is complete, so TTS can start on
        // sentence one instead of waiting out the whole generation, and return the
        // full text for the bubble / conversation memory. Tool detection stays
        // non-streamed — it needs the entire tool_calls JSON before it means
        // anything.
        public static Task<string> StreamResponse(
            string inputText,
            IReadOnlyList<ConversationTurn> history,
            Func<string, Task> onSentence,
            CancellationToken ct) =>
            StreamAnswerAsync(inputText, null, history, onSentence, ct);

        public static Task<string> StreamWithSearchResults(
            string inputText,
            List<SearchHit> hits,
            IReadOnlyList<ConversationTurn> history,
            Func<string, Task> onSentence,
            CancellationToken ct) =>
            StreamAnswerAsync(inputText, hits, history, onSentence, ct);

        private static async Task<string> StreamAnswerAsync(
            string inputText,
            List<SearchHit> hits,
            IReadOnlyList<ConversationTurn> history,
            Func<string, Task> onSentence,
            CancellationToken ct)
        {
            var requestBody = new
            {
                messages = BuildMessages(BuildSystemPrompt(hits), history, inputText),
                max_tokens = 200,
                temperature = 0.5,
                top_p = 0.5,
                stream = true
            };

            string endpoint = $"{lmStudioUrl.TrimEnd('/')}/chat/completions";
            var full = new StringBuilder();
            var pending = new StringBuilder();
            bool firstChunk = true;
            bool toolCallSuppressed = false;

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, endpoint))
                {
                    req.Content = new StringContent(
                        JsonSerializer.Serialize(requestBody, JsonOpts),
                        Encoding.UTF8,
                        "application/json");

                    // ResponseHeadersRead is what makes this a stream — without it
                    // HttpClient buffers the whole response and we're back to
                    // waiting for the last token.
                    using (var response = await httpClient
                        .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            return $"Error: {(int)response.StatusCode} {response.ReasonPhrase}";
                        }

                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            while (!reader.EndOfStream)
                            {
                                ct.ThrowIfCancellationRequested();

                                string line = await reader.ReadLineAsync().ConfigureAwait(false);
                                if (line == null) break;
                                if (line.Length == 0) continue;
                                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                                string data = line.Substring(5).Trim();
                                if (data == "[DONE]") break;

                                string delta = ExtractDelta(data);
                                if (string.IsNullOrEmpty(delta)) continue;

                                full.Append(delta);
                                pending.Append(delta);

                                // A small model sometimes emits a tool call as
                                // plain TEXT rather than through the tool_calls
                                // channel — most often when the tool it wants
                                // isn't registered at all, as `send_sms` isn't
                                // whenever CONTACTS_PATH fails to resolve.
                                // Streamed straight to Kokoro, that gets read
                                // out loud: "...send text, arguments,
                                // recipient, message, slash tool call".
                                // Everything from the marker on is protocol,
                                // not speech, so stop here.
                                int cut = ToolCallStart(full);
                                if (cut >= 0)
                                {
                                    Console.WriteLine(
                                        "[llm] tool call emitted as text — suppressed from speech");
                                    full.Length = cut;
                                    pending.Clear();
                                    toolCallSuppressed = true;
                                    break;
                                }

                                string sentence;
                                while (SentenceChunker.TryTake(pending, false, firstChunk, out sentence))
                                {
                                    firstChunk = false;
                                    await onSentence(sentence).ConfigureAwait(false);
                                }
                            }
                        }
                    }
                }

                // Whatever is left over after [DONE] is the final (possibly
                // unterminated) sentence.
                string tail;
                while (SentenceChunker.TryTake(pending, true, firstChunk, out tail))
                {
                    firstChunk = false;
                    await onSentence(tail).ConfigureAwait(false);
                }

                // A reply that was ONLY a tool call leaves nothing to say, and
                // saying nothing is the worst outcome: the user gets no audio,
                // no bubble, and no idea whether they were even heard. Say so
                // instead. This is what a missing tool sounds like — most often
                // `send_sms`, which isn't registered at all when CONTACTS_PATH
                // fails to resolve, leaving the model nothing real to call.
                if (toolCallSuppressed && full.ToString().Trim().Length == 0)
                {
                    const string fallback = "Sorry, I don't have a way to do that one.";
                    await onSentence(fallback).ConfigureAwait(false);
                    return fallback;
                }

                return full.ToString();
            }
            catch (OperationCanceledException)
            {
                // Barge-in / shutdown: keep whatever was already spoken so the
                // bubble and conversation memory stay consistent with the audio.
                return full.ToString();
            }
            catch (Exception ex)
            {
                // Cancelling mid-read surfaces as an IOException on the response
                // stream rather than OperationCanceledException, so a barge-in
                // lands here too — that's expected, not a failure worth logging.
                if (ct.IsCancellationRequested) return full.ToString();

                Console.WriteLine($"[llm] stream failed: {ex.Message}");
                return full.Length > 0 ? full.ToString() : $"Error: {ex.Message}";
            }
        }

        // Openers various local models use when they hand back a tool call as
        // text. Matched case-insensitively and without their closing bracket, so
        // a half-arrived marker still counts.
        private static readonly string[] ToolCallMarkers =
        {
            "<tool_call", "</tool_call", "<|tool_call", "<function_call",
            "<function=", "<tools>", "[TOOL_CALL", "<|python_tag|>",
        };

        // Index where a text-emitted tool call starts, or -1. Internal so the
        // case set in bakeoff/echo can exercise it.
        internal static int ToolCallStart(StringBuilder buffer)
        {
            string s = buffer.ToString();
            int best = -1;

            foreach (string marker in ToolCallMarkers)
            {
                int i = s.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (i >= 0 && (best < 0 || i < best)) best = i;
            }

            // Bare JSON with no marker at all — `{"name": ..., "arguments": {...}}`.
            // Anchoring on "arguments" rather than "name" avoids cutting an
            // ordinary sentence that happens to quote a name.
            int args = s.IndexOf("\"arguments\"", StringComparison.OrdinalIgnoreCase);
            if (args >= 0)
            {
                int brace = s.LastIndexOf('{', args);
                int cut = brace >= 0 ? brace : args;
                if (best < 0 || cut < best) best = cut;
            }

            return best;
        }

        // Pulls choices[0].delta.content out of one SSE payload. Chunks without a
        // content delta (role announcements, finish_reason) are normal and yield "".
        private static string ExtractDelta(string json)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                        choices.GetArrayLength() == 0)
                    {
                        return string.Empty;
                    }
                    if (!choices[0].TryGetProperty("delta", out var delta) ||
                        !delta.TryGetProperty("content", out var contentEl) ||
                        contentEl.ValueKind != JsonValueKind.String)
                    {
                        return string.Empty;
                    }
                    return contentEl.GetString() ?? string.Empty;
                }
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }

        private static async Task<string> AnswerAsync(
            string inputText,
            List<SearchHit> hits,
            IReadOnlyList<ConversationTurn> history)
        {
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

        // Builds the OpenAI `messages` array: system prompt, prior turns, then the
        // current user input. Spoken turns map "model" -> "assistant"; executed
        // tools render as a native assistant `tool_calls` message plus a matching
        // `tool` result. That keeps strict user/assistant alternation AND gives
        // the model a real prior call to follow, without any imitable text.
        private static List<object> BuildMessages(
            string systemPrompt,
            IReadOnlyList<ConversationTurn> history,
            string inputText)
        {
            var messages = new List<object>
            {
                new Dictionary<string, object> { ["role"] = "system", ["content"] = systemPrompt }
            };

            int callId = 0;
            if (history != null)
            {
                foreach (var turn in history)
                {
                    if (turn.IsTool)
                    {
                        string id = "call_" + (++callId);
                        string argsJson = JsonSerializer.Serialize(
                            turn.ToolArgs ?? new Dictionary<string, string>());

                        messages.Add(new Dictionary<string, object>
                        {
                            ["role"] = "assistant",
                            ["content"] = "",
                            ["tool_calls"] = new object[]
                            {
                                new Dictionary<string, object>
                                {
                                    ["id"] = id,
                                    ["type"] = "function",
                                    ["function"] = new Dictionary<string, object>
                                    {
                                        ["name"] = turn.ToolName,
                                        ["arguments"] = argsJson
                                    }
                                }
                            }
                        });
                        messages.Add(new Dictionary<string, object>
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = id,
                            ["content"] = "done"
                        });
                        continue;
                    }

                    messages.Add(new Dictionary<string, object>
                    {
                        ["role"] = turn.Role == "model" ? "assistant" : "user",
                        ["content"] = turn.Text
                    });
                }
            }

            messages.Add(new Dictionary<string, object> { ["role"] = "user", ["content"] = inputText });
            return messages;
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
                // A little warmth so the router isn't rigidly literal and can
                // reason about composing tools for requests with no direct tool
                // (e.g. "flash the light" -> on then off). Still low enough to
                // keep ordinary routing stable.
                ["temperature"] = 0.3,
                // Headroom for several tool calls in one compound request.
                ["max_tokens"] = 400,
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

                    // Tool call(s) take precedence over any text content. Collect
                    // every call so compound requests run all their actions.
                    if (message.TryGetProperty("tool_calls", out var toolCalls) &&
                        toolCalls.ValueKind == JsonValueKind.Array &&
                        toolCalls.GetArrayLength() > 0)
                    {
                        var calls = new List<ToolInvocation>();
                        foreach (var tc in toolCalls.EnumerateArray())
                        {
                            if (tc.TryGetProperty("function", out var fn) &&
                                fn.TryGetProperty("name", out var nameEl))
                            {
                                calls.Add(new ToolInvocation(nameEl.GetString(), ParseArguments(fn)));
                            }
                        }
                        if (calls.Count > 0) return LlmDecision.Tools(calls);
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