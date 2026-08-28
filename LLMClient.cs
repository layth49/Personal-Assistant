using Personal_Assistant.Configuration;
using Personal_Assistant.Events;
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

    /// <summary>
    /// What a vision call came back with: either the model's text, or a reason
    /// the assistant can say out loud.
    ///
    /// main's AskAboutImageAsync returns a bare string and null on failure, which
    /// works there because there is exactly one way for it to fail (Gemini said
    /// no). Locally there are four that mean four different things to whoever is
    /// standing in front of the machine — the model isn't downloaded, LM Studio
    /// isn't running, the load is still going, the request was rejected — and
    /// collapsing them into "sorry, I couldn't look at the screen" is how a
    /// one-line fix turns into an evening. So the reason travels with the result.
    /// </summary>
    public sealed class VisionAnswer
    {
        /// <summary>The model's reply. Null when <see cref="Ok"/> is false.</summary>
        public string Text { get; }

        /// <summary>A speakable sentence explaining the failure, or null on success.</summary>
        public string Error { get; }

        /// <summary>The underlying detail, for the log and the model's grounding.</summary>
        public string Detail { get; }

        public bool Ok => Error == null;

        private VisionAnswer(string text, string error, string detail)
        {
            Text = text;
            Error = error;
            Detail = detail;
        }

        public static VisionAnswer Success(string text) => new VisionAnswer(text, null, null);

        public static VisionAnswer Failure(string spoken, string detail) =>
            new VisionAnswer(null, spoken, detail ?? spoken);
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

        // TransformTextAsync gets its own client purely for the timeout. The
        // shared one is bounded at 60s, which is right for a 200-token spoken
        // answer and far too short for a note-length rewrite: reformat_note asks
        // for up to 4096 tokens, and at the tens-of-tokens-a-second this box
        // manages that is minutes rather than seconds. A rewrite aborted halfway
        // is indistinguishable here from a refusal, so the note would be left
        // alone every single time and nothing would say why.
        private static readonly HttpClient transformClient =
            CreateHttpClient(TimeSpan.FromMinutes(5));

        // ===== Vision =====
        //
        // The vision model is NOT the router model, and AskAboutImageAsync is the
        // only request in this file that names a model at all. Everything else
        // omits `model`, so LM Studio answers with whatever is currently loaded —
        // which is the router, and the router cannot see. Naming it here is what
        // makes LM Studio load the VLM just-in-time, and that is also the whole
        // cost of it: this box has ~6 GB of VRAM, which is the same reason a true
        // speech-to-speech model was ruled out, and a VLM sitting alongside a 4B
        // router does not comfortably fit. Expect LM Studio to EVICT one to load
        // the other — which is why the first look_at_screen in a while is slow,
        // and why the first spoken answer after it can be slow too.
        //
        // These resolve when the type is first touched (WarmUpAsync, early in
        // Program.Main), so all three appear in the startup [config] line like
        // every other setting. If a vision tool behaves oddly, read that line
        // FIRST: a stale LAITH_LOCAL_VISION_MODEL in HKCU:\Environment beats
        // App.config, and that exact trap has silently run this app on the wrong
        // model for weeks.
        private static readonly string VisionModel =
            LaithConfig.Text("LocalVisionModel", "qwen/qwen3-vl-4b");

        // Longest edge, in pixels, a screenshot may reach before it is resampled
        // down. See DownscaleForVision for why the default is 1920.
        private static readonly int VisionMaxDimension =
            LaithConfig.Int("LocalVisionMaxDimension", 1920, 480, 4096);

        // Bounded, but generously. A cold just-in-time load has to page both the
        // weights and the vision tower in, and a screenshot is a far larger
        // prompt than a spoken turn, so a first call over a minute is normal here
        // while a warm one is seconds. It is bounded AT ALL because
        // ContinuousListener is sitting on this turn: an unbounded wait is dead
        // air with the microphone held open, which is worse than a spoken
        // failure. Whatever this is set to is what the failure sentence quotes.
        private static readonly TimeSpan VisionTimeout =
            LaithConfig.Seconds("LocalVisionTimeoutSeconds", 180, 15, 600);

        // Declared after VisionTimeout on purpose: static field initialisers run
        // in textual order, so reading it from above would hand CreateHttpClient
        // a zero TimeSpan, which HttpClient rejects at construction.
        private static readonly HttpClient visionClient = CreateHttpClient(VisionTimeout);

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

        private static HttpClient CreateHttpClient(TimeSpan? timeout = null)
        {
            var client = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(60) };
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
                // See BuildToolRequest. Matters even more here than there: the
                // answer budget is 200 tokens, and a model that spends them
                // thinking returns empty `content` — the assistant says nothing
                // at all, which is exactly how gemma-4-e2b behaved.
                reasoning_effort = "none",
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
                reasoning_effort = "none",   // see BuildToolRequest
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

        // The OpenAI `tools` array for a set of schemas.
        //
        // Split out of BuildToolRequest and made internal for the screened-call
        // path, which builds its own request — a different message shape, a
        // fail-closed four-tool list, and streamed rather than one-shot — but which
        // must encode the SAME schemas. A second copy of this loop is a copy that
        // can drift, and a drifted `required` list is a tool called with the wrong
        // arguments on a call nobody is watching.
        internal static List<object> ToolSchemas(IReadOnlyList<ToolDefinition> tools)
        {
            var toolList = new List<object>();
            if (tools == null) return toolList;

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

            return toolList;
        }

        private static object BuildToolRequest(
            string inputText,
            IReadOnlyList<ToolDefinition> tools,
            IReadOnlyList<ConversationTurn> history)
        {
            return new Dictionary<string, object>
            {
                // Reuse the same message builder (tool actions -> system note,
                // never assistant messages) as the conversational path.
                ["messages"] = BuildMessages(ToolSystemPrompt, history, inputText),
                ["tools"] = ToolSchemas(tools),
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
                // Reasoning is pure latency on the router: it happens before the
                // user has heard anything, and nobody ever sees the thinking.
                //
                // "/no think" in a system prompt is a QWEN convention and does
                // nothing for other families — and measured 2026-08-10 it doesn't
                // even work for all Qwens: qwen3.5-4b and nemotron-3-nano-4b
                // ignore it (and ignore chat_template_kwargs) and keep thinking.
                // reasoning_effort is the only method that silenced every model
                // that could be silenced at all, and it is inert on models with
                // no reasoning mode, so it is applied unconditionally rather than
                // per-family.
                ["reasoning_effort"] = "none",
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

        // One-shot text transformation: reformat_note's rewrite, and the
        // question read_notes answers FROM a note instead of reading it out.
        // The local twin of GeminiService.TransformTextAsync, and deliberately
        // NOT built on AnswerAsync:
        //
        //   * no history — the note is the whole input, and a prior turn about
        //     the weather is one more thing a 4B model can drag into a rewrite;
        //   * no BaseSystemPrompt — that one is written for SPEECH ("never use
        //     markdown, default to one short sentence"), which is the exact
        //     opposite of what a markdown note being reorganised needs;
        //   * no search hits — this is a transformation of text the caller
        //     already has, not a question about the world;
        //   * not streamed — nothing here is spoken as it arrives, and the
        //     result has to be checked for truncation before it is written over
        //     a file the user typed by hand.
        //
        // Returns null on any failure, which every caller treats as "leave the
        // note alone".
        public static async Task<string> TransformTextAsync(
            string instruction, string content, int maxOutputTokens = 4096)
        {
            var requestBody = new Dictionary<string, object>
            {
                ["messages"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "system",
                        ["content"] =
                            "You transform text exactly as instructed and return ONLY the " +
                            "result — no preamble, no commentary, no markdown code fences " +
                            "around the whole answer. Preserve the author's meaning, wording " +
                            "and any facts; you are reorganising, not rewriting from scratch, " +
                            "and you never invent content that wasn't there."
                    },
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = instruction + "\n\n---\n" + (content ?? string.Empty)
                    }
                },
                // Matches main's 0.2. This is a reorganisation, and the failure
                // mode to avoid is the model improving the user's wording.
                ["temperature"] = 0.2,
                ["max_tokens"] = maxOutputTokens,
                // Non-negotiable here for the same reason it is on the router and
                // the event judge: a model that spends its budget thinking
                // returns empty `content`. That matters more in this method than
                // anywhere else, because the caller's next move is to overwrite a
                // note the user wrote by hand. "/no think" in the prompt does not
                // do this reliably; reasoning_effort is the only thing that does.
                // See BuildToolRequest for the measurements.
                ["reasoning_effort"] = "none",
                ["stream"] = false
            };

            try
            {
                var body = new StringContent(
                    JsonSerializer.Serialize(requestBody, RawJsonOpts),
                    Encoding.UTF8,
                    "application/json");

                using (HttpResponseMessage response = await transformClient
                    .PostAsync($"{lmStudioUrl.TrimEnd('/')}/chat/completions", body)
                    .ConfigureAwait(false))
                {
                    string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"[llm] transform HTTP {(int)response.StatusCode}: {json}");
                        return null;
                    }
                    return StripWholeAnswerFence(ExtractText(json));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[llm] transform failed: {ex.Message}");
                return null;
            }
        }

        // Small local models fence their whole answer in ```markdown ... ```
        // however firmly the system prompt asks them not to. Left in place that
        // fence is WRITTEN INTO the note by reformat_note, or read out a
        // backtick at a time by read_notes, so it is removed here rather than at
        // each call site. Only a fence wrapping the ENTIRE answer is touched — a
        // code block inside a note is content and stays.
        private static string StripWholeAnswerFence(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            string trimmed = text.Trim();
            if (trimmed.Length < 6 ||
                !trimmed.StartsWith("```", StringComparison.Ordinal) ||
                !trimmed.EndsWith("```", StringComparison.Ordinal))
            {
                return text;
            }

            int firstBreak = trimmed.IndexOf('\n');
            if (firstBreak < 0) return text;

            // A language tag is the only thing allowed on the opening line;
            // anything else means the ``` was part of the content.
            string tag = trimmed.Substring(3, firstBreak - 3).Trim();
            if (tag.IndexOf(' ') >= 0) return text;

            string inner = trimmed.Substring(firstBreak + 1, trimmed.Length - firstBreak - 4);

            // A third fence in between means these two were opening and closing
            // DIFFERENT blocks rather than wrapping the answer.
            return inner.IndexOf("```", StringComparison.Ordinal) >= 0 ? text : inner.Trim();
        }

        /// <summary>
        /// Answers a question about a single image (a screenshot), the local twin
        /// of GeminiService.AskAboutImageAsync. One-shot: no history, no tools,
        /// no search hits, not streamed.
        ///
        /// NEVER THROWS AND NEVER HANGS. Every failure comes back as a
        /// <see cref="VisionAnswer"/> carrying a sentence the handler can say out
        /// loud, because the alternatives here are both worse than a spoken "no":
        /// an exception out of a tool handler is a dropped turn, and a silent
        /// empty string makes the assistant announce that it looked at a screen
        /// it never saw. The turn-based loop is holding the microphone while this
        /// runs, so the bounded timeout is part of the contract, not a detail.
        /// </summary>
        public static async Task<VisionAnswer> AskAboutImageAsync(
            string question, byte[] pngBytes, int maxOutputTokens = 512)
        {
            if (pngBytes == null || pngBytes.Length == 0)
            {
                return VisionAnswer.Failure(
                    "I couldn't get a picture of the screen.", "empty capture");
            }

            byte[] image;
            string sizeNote;
            try
            {
                image = DownscaleForVision(pngBytes, out sizeNote);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[vision] downscale failed: {ex.Message}");
                return VisionAnswer.Failure(
                    "I couldn't prepare the screenshot for the vision model.", ex.Message);
            }

            Console.WriteLine(
                $"[vision] {VisionModel}, {sizeNote}, {image.Length / 1024}KB, " +
                $"timeout {VisionTimeout.TotalSeconds:F0}s");

            var requestBody = new Dictionary<string, object>
            {
                // The one place a model is named. See the VisionModel field.
                ["model"] = VisionModel,
                ["messages"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        // OpenAI-compatible multimodal shape: `content` is an
                        // ARRAY of parts rather than a string. LM Studio accepts
                        // a data: URI in image_url, so nothing is written to disk
                        // and nothing has to be served over HTTP.
                        ["content"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["type"] = "text",
                                ["text"] = question ?? "Describe what's on screen."
                            },
                            new Dictionary<string, object>
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new Dictionary<string, object>
                                {
                                    ["url"] = "data:image/png;base64," +
                                              Convert.ToBase64String(image)
                                }
                            }
                        }
                    }
                },
                // main's value. This is a reading task, not a creative one, and
                // copy_from_screen's whole contract is that the words come back
                // unchanged.
                ["temperature"] = 0.2,
                ["max_tokens"] = maxOutputTokens,
                // Same reason as the router, the event judge and TransformTextAsync:
                // a model that spends its budget thinking returns empty `content`,
                // and "/no think" in the prompt does not stop it on half of these
                // models. See BuildToolRequest for the measurements.
                ["reasoning_effort"] = "none",
                ["stream"] = false
            };

            try
            {
                var body = new StringContent(
                    JsonSerializer.Serialize(requestBody, RawJsonOpts),
                    Encoding.UTF8,
                    "application/json");

                using (HttpResponseMessage response = await visionClient
                    .PostAsync($"{lmStudioUrl.TrimEnd('/')}/chat/completions", body)
                    .ConfigureAwait(false))
                {
                    string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine(
                            $"[vision] HTTP {(int)response.StatusCode}: {Truncate(json, 400)}");

                        // 404 from LM Studio means "I have never heard of that
                        // model id", which is by far the likeliest failure here
                        // and the only one the user can fix. Say the id out loud
                        // — a typo in App.config and a model that was never
                        // downloaded look identical from in here, and both are
                        // answered by looking at what LM Studio actually lists.
                        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            return VisionAnswer.Failure(
                                $"L M Studio doesn't have a model called {SpokenModelName(VisionModel)}. " +
                                "It needs downloading before I can look at the screen.",
                                $"404 for model '{VisionModel}': {Truncate(json, 400)}");
                        }

                        return VisionAnswer.Failure(
                            $"The vision model returned an error, {(int)response.StatusCode}.",
                            Truncate(json, 400));
                    }

                    // Fenced for the same reason a rewritten note is: small local
                    // models wrap a whole answer in ``` however firmly they are
                    // told not to, and copy_from_screen would put those backticks
                    // straight onto the clipboard.
                    string text = StripWholeAnswerFence(ExtractText(json));
                    return VisionAnswer.Success(text ?? string.Empty);
                }
            }
            catch (TaskCanceledException)
            {
                // HttpClient surfaces its own timeout as a cancellation, and
                // nothing here passes a token, so this is always the timeout.
                Console.WriteLine($"[vision] timed out after {VisionTimeout.TotalSeconds:F0}s");
                return VisionAnswer.Failure(
                    $"The vision model didn't answer within {VisionTimeout.TotalSeconds:F0} seconds. " +
                    "It's probably still loading — try again in a moment.",
                    $"timeout after {VisionTimeout.TotalSeconds:F0}s");
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"[vision] request failed: {ex.Message}");
                return VisionAnswer.Failure(
                    "I couldn't reach L M Studio to look at the screen.", ex.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[vision] failed: {ex.Message}");
                return VisionAnswer.Failure(
                    "Something went wrong looking at the screen.", ex.Message);
            }
        }

        // A model id read out loud. "qwen/qwen3-vl-4b" as written is a slash and
        // a string of hyphens; the slash in particular is silence, so the two
        // halves run together into one nonsense word.
        private static string SpokenModelName(string id) =>
            (id ?? string.Empty).Replace("/", " ").Replace("-", " ").Replace("_", " ");

        private static string Truncate(string text, int max) =>
            string.IsNullOrEmpty(text) || text.Length <= max
                ? text
                : text.Substring(0, max) + "…";

        // Resamples a screenshot down so its longest edge is at most
        // VisionMaxDimension, preserving aspect ratio. Returns the original bytes
        // untouched when it already fits.
        //
        // WHY THERE IS A CAP AT ALL. main hands Gemini the raw PNG, which is fine
        // against a hosted model. A local VLM on ~6 GB is a different
        // proposition: image tokens scale with area, so a 4K capture is several
        // times the prompt of a 1080p one, and the failure mode is not "slower",
        // it is the model spilling out of VRAM or the server refusing the
        // request outright.
        //
        // WHY 1920, AND THE TENSION. copy_from_screen exists to return text
        // VERBATIM, and downscaling is exactly what destroys small text — a cap
        // chosen for token cost alone would quietly corrupt the one tool whose
        // whole contract is that it doesn't. 1920 is picked so the common case is
        // the IDENTITY transform: a 1080p monitor (which is what
        // ScreenshotService.CaptureBytes hands over by default, since it captures
        // the focused monitor rather than the desktop) passes through with no
        // resampling at all, so this cap cannot be blamed for a misread character
        // there. A 4K capture halves to 1920x1080, which is roughly what that
        // display shows at its usual 150% scaling anyway — legible, but this is
        // the case to suspect first if extraction starts dropping characters.
        //
        // The bad case is monitor:"all": two 1080p screens side by side are
        // 3840x1080, and capping the LONGEST edge takes that to 1920x540, i.e.
        // half-height text. That is a further reason "focused" is the default and
        // "all" is documented as only-when-the-user-means-it. If verbatim
        // extraction matters more than the load, raise LocalVisionMaxDimension.
        private static byte[] DownscaleForVision(byte[] png, out string sizeNote)
        {
            using (var input = new MemoryStream(png))
            using (var original = System.Drawing.Image.FromStream(input))
            {
                int width = original.Width;
                int height = original.Height;
                int longest = Math.Max(width, height);

                if (longest <= VisionMaxDimension)
                {
                    sizeNote = $"{width}x{height}";
                    return png;
                }

                double scale = (double)VisionMaxDimension / longest;
                int newWidth = Math.Max(1, (int)Math.Round(width * scale));
                int newHeight = Math.Max(1, (int)Math.Round(height * scale));

                using (var resized = new System.Drawing.Bitmap(newWidth, newHeight))
                {
                    using (var g = System.Drawing.Graphics.FromImage(resized))
                    {
                        // Bicubic, not the default: nearest-neighbour on UI text
                        // at these ratios drops whole stems off letters, which is
                        // the difference between an "l" and a "1" on a clipboard.
                        g.InterpolationMode =
                            System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode =
                            System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        g.SmoothingMode =
                            System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                        g.CompositingQuality =
                            System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                        g.DrawImage(original, 0, 0, newWidth, newHeight);
                    }

                    using (var output = new MemoryStream())
                    {
                        // PNG, not JPEG: JPEG's ringing around high-contrast
                        // edges is worst on exactly the thing being read here.
                        resized.Save(output, System.Drawing.Imaging.ImageFormat.Png);
                        sizeNote = $"{width}x{height} -> {newWidth}x{newHeight}";
                        return output.ToArray();
                    }
                }
            }
        }

        /// <summary>
        /// Asks whether <paramref name="subject"/> has happened yet, from live
        /// search results rather than from what the model remembers.
        ///
        /// This is the local half of what `main` gets from Gemini's Google Search
        /// grounding, and the shape is deliberately the same: SEARCH FIRST, then
        /// judge only what came back. It is arguably the stronger arrangement of
        /// the two, because here the retrieval step is ours — the model is never
        /// trusted to decide whether it looked something up.
        ///
        /// Never throws: every failure becomes an Unknown carrying the reason.
        /// The caller decides whether to re-check, and "the lookup broke" and
        /// "the event hasn't happened" must never look the same to it.
        /// </summary>
        public static async Task<EventVerdict> VerifyEventAsync(
            string subject, CancellationToken cancel = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(subject))
            {
                return EventVerdict.Unknown("no subject to check");
            }

            List<SearchHit> hits;
            try
            {
                hits = await SearxNGService.SearchAsync(subject, topN: 5).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return EventVerdict.Unknown($"the search failed: {ex.Message}");
            }

            // THE refusal, and the reason this method exists rather than a call to
            // AnswerWithSearchResults.
            //
            // SearxNGService is deliberately best-effort everywhere else: an
            // outage returns an empty list and the model answers from its own
            // knowledge, which for ordinary chat is the right trade. Here it is
            // exactly wrong. With no results, "has this happened yet" can only be
            // answered from training data — confidently, about a release date,
            // which is the one thing this whole feature exists to stop. No search,
            // no answer.
            if (hits == null || hits.Count == 0)
            {
                return EventVerdict.Unknown("no search results came back, and I won't guess at this");
            }

            return await JudgeEventAsync(subject, hits, cancel).ConfigureAwait(false);
        }

        /// <summary>
        /// The judging half, over results someone else fetched.
        ///
        /// Split from the retrieval half because they fail for different reasons
        /// and are worth exercising apart: a SearxNG outage and a model that
        /// answers in the wrong shape both surface as Unknown, and telling them
        /// apart is most of debugging a watch that never resolves. It is also the
        /// only way to test the prompt without the search stack up.
        /// </summary>
        public static async Task<EventVerdict> JudgeEventAsync(
            string subject, List<SearchHit> hits, CancellationToken cancel = default(CancellationToken))
        {
            if (hits == null || hits.Count == 0)
            {
                return EventVerdict.Unknown("nothing to judge from");
            }

            var sources = new StringBuilder();
            for (int i = 0; i < hits.Count; i++)
            {
                sources.AppendLine($"[{i + 1}] {hits[i].Title}");
                if (!string.IsNullOrWhiteSpace(hits[i].Snippet)) sources.AppendLine($"    {hits[i].Snippet}");
            }

            string instruction =
                "You decide whether a real-world event has already happened, using ONLY the search " +
                $"results given to you. Today is {DateTime.Now:dddd d MMMM yyyy}, local time {DateTime.Now:HH:mm}. " +
                "If the results do not clearly settle it, answer UNKNOWN. Never answer from your own " +
                "knowledge — your training data is older than these results and release dates move.";

            string question =
                $"Event: {subject}\n\n" +
                $"Search results:\n{sources}\n" +
                "Has it happened yet? Reply with EXACTLY one line, nothing else, in this format:\n" +
                "STATUS | one short sentence | source number\n" +
                "STATUS is HAPPENED, NOT_YET, or UNKNOWN. " +
                "The sentence is spoken aloud, so: under 20 words, no markdown, no asterisks. " +
                "The source number is the [n] of the best result to open, or NONE. " +
                "Do not write a URL — just the number.";

            var requestBody = new Dictionary<string, object>
            {
                ["messages"] = new List<object>
                {
                    new Dictionary<string, object> { ["role"] = "system", ["content"] = instruction },
                    new Dictionary<string, object> { ["role"] = "user", ["content"] = question }
                },
                // Low, not zero: this is a judgement over text, and the failure
                // mode to avoid is creative rephrasing of a date.
                ["temperature"] = 0.1,
                ["max_tokens"] = 200,
                // Non-negotiable on this branch. A model that spends its budget
                // thinking returns empty `content`, which parses as Unknown — so
                // the watch would re-check forever and never resolve, and the log
                // would blame the search. "/no think" in the prompt does NOT do
                // this reliably; reasoning_effort is the only thing that does.
                // See BuildToolRequest for the measurements.
                ["reasoning_effort"] = "none",
                ["stream"] = false
            };

            try
            {
                var content = new StringContent(
                    JsonSerializer.Serialize(requestBody, RawJsonOpts), Encoding.UTF8, "application/json");

                using (HttpResponseMessage response = await httpClient
                    .PostAsync($"{lmStudioUrl.TrimEnd('/')}/chat/completions", content, cancel)
                    .ConfigureAwait(false))
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        return EventVerdict.Unknown($"{(int)response.StatusCode} {response.ReasonPhrase}");
                    }

                    // The model names a source NUMBER and we resolve it against
                    // the hits, so the URL that eventually reaches Process.Start
                    // always came from SearxNG and never from the model. A 4B
                    // model asked for a URL will happily invent a plausible one.
                    return EventVerdict.Parse(ResolveSource(ExtractText(body), hits));
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

        // Swaps a trailing "[n]"/"n" source reference for that hit's real URL, so
        // the shared EventVerdict.Parse — including its http(s)-only check — sees
        // the same shape it sees on main. Anything that isn't a number in range is
        // left alone, which means NONE stays NONE and an invented URL still has to
        // survive Parse's validation.
        private static string ResolveSource(string reply, List<SearchHit> hits)
        {
            if (string.IsNullOrWhiteSpace(reply)) return reply;

            int lastBar = reply.LastIndexOf('|');
            if (lastBar < 0) return reply;

            string tail = reply.Substring(lastBar + 1).Trim().Trim('[', ']', '.', '"', '*', '`');
            if (!int.TryParse(tail, out int index) || index < 1 || index > hits.Count) return reply;

            string url = hits[index - 1].Url;
            if (string.IsNullOrWhiteSpace(url)) return reply.Substring(0, lastBar + 1) + " NONE";
            return reply.Substring(0, lastBar + 1) + " " + url;
        }
    }
}