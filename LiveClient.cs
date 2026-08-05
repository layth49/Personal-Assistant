using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Personal_Assistant.Dispatch;
using Personal_Assistant.GeminiClient;

namespace Personal_Assistant.Live
{
    // One function call the model wants executed. Args are flattened to strings
    // to match IntentDispatcher.TryValidate / RunToolByNameAsync, which are
    // provider-independent and carry over from the turn-based path unchanged.
    //
    // Id is what makes this different from the unary API: Live matches tool
    // results to calls by id, not by position or name, so it must round-trip.
    public sealed class LiveFunctionCall
    {
        public string Id { get; }
        public string Name { get; }
        public IReadOnlyDictionary<string, string> Args { get; }

        public LiveFunctionCall(string id, string name, IReadOnlyDictionary<string, string> args)
        {
            Id = id;
            Name = name;
            Args = args ?? new Dictionary<string, string>();
        }

        public override string ToString() => $"{Name}#{Id}({string.Join(", ", Args.Keys)})";
    }

    // The result of running one tool, sent back as a `functionResponses` entry.
    // Id must be the id from the LiveFunctionCall that triggered it.
    public sealed class LiveFunctionResult
    {
        public string Id { get; }
        public string Name { get; }

        // Free-form payload the model reads. Handlers on this project are
        // fire-and-forget actions, so "done" is the usual value.
        public IReadOnlyDictionary<string, string> Response { get; }

        public LiveFunctionResult(string id, string name, IReadOnlyDictionary<string, string> response = null)
        {
            Id = id;
            Name = name;
            Response = response ?? new Dictionary<string, string> { ["result"] = "done" };
        }

        public static LiveFunctionResult Done(LiveFunctionCall call) =>
            new LiveFunctionResult(call.Id, call.Name);

        public static LiveFunctionResult Error(LiveFunctionCall call, string message) =>
            new LiveFunctionResult(call.Id, call.Name,
                new Dictionary<string, string> { ["error"] = message });
    }

    // Everything the `setup` message needs. Defaults are the working
    // configuration for this project; a caller only has to supply Tools.
    public sealed class LiveSessionOptions
    {
        // Both current Live models are preview and the ids move, so this is
        // env-overridable without a rebuild.
        public const string DefaultModel = "gemini-2.5-flash-native-audio-preview-12-2025";

        // The turn-based SystemPrompt minus its "responses are converted to
        // speech / never use markdown" clauses — a native-audio model speaks
        // directly and needs no text-shaping rules. The persona and the
        // "call the tool rather than describing it" line are load-bearing and
        // stay.
        public const string DefaultSystemInstruction =
            "You are L.A.I.T.H., Layth's personal voice assistant running on his computer. " +
            "Default to one short sentence. Only give more detail if the user asks for it, " +
            "asks a multi-part question, or the answer genuinely requires it (e.g. instructions, comparisons). " +
            "Lead with the answer or result first, then explain if needed — never bury the answer at the end. " +
            "If a tool/function is available that matches what the user wants, call it directly rather than " +
            "describing what you would do. Only respond conversationally when no tool fits or the user is " +
            "just chatting. " +
            "You have Google Search built in — use it to answer questions about the world (news, release " +
            "dates, facts, how things work) and just say the answer. The `open_web_search` tool is a " +
            "separate thing: it opens a results page in the browser and tells you nothing, so only call it " +
            "when the user actually wants a browser window opened. Never call it to research an answer. " +
            "If voice input is garbled, ambiguous, or doesn't clearly match a command or question, briefly " +
            "ask for clarification instead of guessing. " +
            "Tone is direct and casual, like a capable assistant who knows Layth well — not stiff or overly formal. " +
            "Never fabricate information; if you don't know or aren't sure, say so plainly.";

        public string Model { get; set; } =
            // Whitespace-tolerant, not just null-tolerant: `setx VAR ""` is how
            // you undo a setx, and it leaves an EMPTY value rather than removing
            // the variable. `??` would have accepted that and sent an empty model
            // id, failing in a way that looks nothing like "you unset the model".
            Blank(Environment.GetEnvironmentVariable("LAITH_LIVE_MODEL")) ? DefaultModel
                : Environment.GetEnvironmentVariable("LAITH_LIVE_MODEL").Trim();

        public string ApiKey { get; set; } = GeminiService.geminiApiKey;

        public string SystemInstruction { get; set; } = DefaultSystemInstruction;

        // Prebuilt voice name (Kore, Puck, Charon, ...). Null leaves the
        // model's default.
        public string Voice { get; set; } =
            Environment.GetEnvironmentVariable("LAITH_LIVE_VOICE");

        public IReadOnlyList<ToolDefinition> Tools { get; set; }

        // Keeps the Google Search grounding the turn-based path has today.
        // Live supports it in-session alongside function calling.
        public bool EnableGoogleSearch { get; set; } = true;

        // Echo is settled: streaming the mic into a server-side VAD means the
        // model hears itself through the speakers, and five speaker runs on
        // local-laith established that no level threshold separates bleed from
        // speech. So the client owns turn boundaries via activityStart /
        // activityEnd, and mic frames stop uploading while the assistant talks.
        // Do not flip this on without real AEC.
        //
        // Switchable, because the settled conclusion above was reached on a
        // pipeline WITHOUT the half-duplex gate. That gate drops every mic frame
        // while assistant audio plays, so the model cannot hear itself and the
        // server's VAD only ever sees Layth — which is what makes server-side
        // endpointing safe to try at all. It is still UNPROVEN on speakers, where
        // the echo tail is the only thing covering the gap between playback
        // ending and sound stopping. Set LAITH_LIVE_SERVER_VAD=0 to revert.
        public bool ManualActivityDetection { get; set; } =
            Environment.GetEnvironmentVariable("LAITH_LIVE_SERVER_VAD") == "0";

        // BCP-47 code for the input ASR, e.g. "en-US". Unset by default: the
        // native-audio models this normally runs on do not accept it. Set
        // LAITH_LIVE_LANGUAGE only alongside a half-cascade model such as
        // gemini-3.1-flash-live-preview, whose separate ASR can be pinned.
        public string LanguageCode { get; set; } =
            Blank(Environment.GetEnvironmentVariable("LAITH_LIVE_LANGUAGE")) ? null
                : Environment.GetEnvironmentVariable("LAITH_LIVE_LANGUAGE").Trim();

        private static bool Blank(string s) => string.IsNullOrWhiteSpace(s);

        public bool InputAudioTranscription { get; set; } = true;
        public bool OutputAudioTranscription { get; set; } = true;

        // How long to wait for `setupComplete` before giving up. The handshake
        // is normally sub-second; a long stall means a bad key or model id.
        public TimeSpan SetupTimeout { get; set; } = TimeSpan.FromSeconds(20);
    }

    // Hand-rolled WebSocket client for the Gemini Live API's BidiGenerateContent
    // endpoint. Hand-rolled because every C# Gemini Live SDK targets net6.0+ and
    // this project is net481 — System.Net.WebSockets.ClientWebSocket ships in the
    // framework and is the whole transport.
    //
    // Scope is protocol only: connect, configure, exchange audio/text/tool
    // messages, and surface typed events. Audio capture and playback live in
    // LiveAudio.cs; session lifecycle and the watchdog live in LiveSession.cs.
    public sealed class LiveClient : IDisposable
    {
        private const string EndpointBase =
            "wss://generativelanguage.googleapis.com/ws/" +
            "google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

        // Model audio comes back as 24 kHz mono PCM16 little-endian. The actual
        // rate is parsed off each chunk's mimeType (see OutputSampleRate) rather
        // than trusted blindly, but this is the documented default.
        public const int DefaultOutputSampleRate = 24000;

        // Mic audio must go up as 16 kHz mono PCM16 little-endian.
        public const int InputSampleRate = 16000;

        private const int ReceiveBufferSize = 16 * 1024;

        // Field names are already the exact wire spellings (function_declarations,
        // google_search, ...), so no naming policy may be applied or those keys
        // would be mangled — same reason GeminiService keeps RawJsonOpts.
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions();

        private readonly LiveSessionOptions options;
        private readonly ClientWebSocket socket = new ClientWebSocket();

        // ClientWebSocket permits only one outstanding SendAsync. Audio frames
        // race against tool responses and activity markers, so every send funnels
        // through here.
        private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);

        private readonly CancellationTokenSource pumpCts = new CancellationTokenSource();
        private TaskCompletionSource<bool> setupTcs;
        private Task receivePump;
        private int disposed;
        private int closedRaised;

        public LiveClient(LiveSessionOptions options)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
        }

        // Sample rate of the most recent audio chunk, read off its mimeType.
        // Phase 2's playback should use this rather than assuming 24 kHz — the
        // docs' own examples disagree with their spec on this value.
        public int OutputSampleRate { get; private set; } = DefaultOutputSampleRate;

        public bool IsOpen => socket.State == WebSocketState.Open;

        /// <summary>Handshake finished; the session is ready for input.</summary>
        public event Action SetupComplete;

        /// <summary>A chunk of model audio (PCM16 LE mono, see OutputSampleRate).</summary>
        public event Action<byte[]> AudioReceived;

        /// <summary>Model output was cut off — discard buffered playback.</summary>
        public event Action Interrupted;

        /// <summary>The model finished its turn.</summary>
        public event Action TurnComplete;

        /// <summary>The model wants these tools run; reply with SendToolResponseAsync.</summary>
        public event Action<IReadOnlyList<LiveFunctionCall>> ToolCallReceived;

        /// <summary>Abandon results for these call ids — do not send them.</summary>
        public event Action<IReadOnlyList<string>> ToolCallCancelled;

        /// <summary>Incremental transcript of what the user said.</summary>
        public event Action<string> InputTranscript;

        /// <summary>Incremental transcript of what the model said.</summary>
        public event Action<string> OutputTranscript;

        /// <summary>Server is about to close the session (15-minute audio cap).</summary>
        public event Action<TimeSpan?> GoingAway;

        /// <summary>Socket closed for any reason. Trips Phase 5's fallback.</summary>
        public event Action<WebSocketCloseStatus?, string> Closed;

        // Opens the socket, sends `setup`, and returns once the server has
        // acknowledged with `setupComplete`. Nothing else may be sent before
        // that — the server rejects it.
        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(options.ApiKey))
                throw new InvalidOperationException("GEMINIAPI_KEY is not set — cannot open a Live session.");

            setupTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // The Developer API takes the key as a query parameter here; header
            // auth is not the documented path for this endpoint. This URI must
            // never reach a log — see LogSafeEndpoint.
            var uri = new Uri(EndpointBase + "?key=" + Uri.EscapeDataString(options.ApiKey));
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);

            // The pump has to be running before the setup message is sent, since
            // it is what completes setupTcs.
            receivePump = Task.Run(() => ReceiveLoopAsync(pumpCts.Token));

            await SendJsonAsync(BuildSetupMessage(), cancellationToken).ConfigureAwait(false);

            using (var timeout = new CancellationTokenSource(options.SetupTimeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken))
            using (linked.Token.Register(() => setupTcs.TrySetCanceled()))
            {
                try
                {
                    await setupTcs.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // A failed handshake must not leave the socket open — that is
                    // exactly the leak the quota risk is about.
                    socket.Abort();
                    pumpCts.Cancel();

                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException(
                        $"Live setup was not acknowledged within {options.SetupTimeout.TotalSeconds:0}s " +
                        $"(model '{options.Model}').");
                }
                catch
                {
                    socket.Abort();
                    pumpCts.Cancel();
                    throw;
                }
            }
        }

        // Streams mic audio. Safe to call continuously — it does not interrupt
        // model generation on its own.
        public Task SendAudioAsync(byte[] pcm16, int count, CancellationToken cancellationToken = default)
        {
            if (pcm16 == null) throw new ArgumentNullException(nameof(pcm16));
            if (count <= 0) return Task.CompletedTask;
            if (count > pcm16.Length) throw new ArgumentOutOfRangeException(nameof(count));

            var message = new Dictionary<string, object>
            {
                ["realtimeInput"] = new Dictionary<string, object>
                {
                    ["audio"] = new Dictionary<string, object>
                    {
                        ["mimeType"] = "audio/pcm;rate=" + InputSampleRate,
                        ["data"] = Convert.ToBase64String(pcm16, 0, count)
                    }
                }
            };
            return SendJsonAsync(message, cancellationToken);
        }

        // Manual turn boundaries. Only legal while automaticActivityDetection is
        // disabled — and in that mode the model waits forever if these never
        // arrive, so both halves must always be sent.
        public Task SendActivityStartAsync(CancellationToken cancellationToken = default) =>
            SendActivityMarkerAsync("activityStart", cancellationToken);

        public Task SendActivityEndAsync(CancellationToken cancellationToken = default) =>
            SendActivityMarkerAsync("activityEnd", cancellationToken);

        private Task SendActivityMarkerAsync(string marker, CancellationToken cancellationToken)
        {
            if (!options.ManualActivityDetection)
            {
                // Sending these with server-side VAD enabled is a protocol error;
                // swallow rather than kill the session.
                Console.WriteLine($"[live] ignoring {marker} — automatic activity detection is enabled");
                return Task.CompletedTask;
            }

            var message = new Dictionary<string, object>
            {
                ["realtimeInput"] = new Dictionary<string, object>
                {
                    [marker] = new Dictionary<string, object>()
                }
            };
            return SendJsonAsync(message, cancellationToken);
        }

        // Results must be matched to calls by id — unlike the unary API, which
        // exchanges calls inside Content parts and matches by name.
        public Task SendToolResponseAsync(
            IReadOnlyList<LiveFunctionResult> results,
            CancellationToken cancellationToken = default)
        {
            if (results == null || results.Count == 0) return Task.CompletedTask;

            var responses = new List<object>();
            foreach (var r in results)
            {
                var entry = new Dictionary<string, object>
                {
                    ["name"] = r.Name,
                    ["response"] = ToObjectMap(r.Response)
                };
                // Omit rather than send null: the server matches on presence.
                if (!string.IsNullOrEmpty(r.Id)) entry["id"] = r.Id;
                responses.Add(entry);
            }

            var message = new Dictionary<string, object>
            {
                ["toolResponse"] = new Dictionary<string, object>
                {
                    ["functionResponses"] = responses
                }
            };
            return SendJsonAsync(message, cancellationToken);
        }

        // A complete text turn. Used by the harness and for mic-free debugging;
        // the real path is audio.
        public Task SendTextAsync(string text, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;

            var message = new Dictionary<string, object>
            {
                ["clientContent"] = new Dictionary<string, object>
                {
                    ["turns"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["role"] = "user",
                            ["parts"] = new object[]
                            {
                                new Dictionary<string, object> { ["text"] = text }
                            }
                        }
                    },
                    ["turnComplete"] = true
                }
            };
            return SendJsonAsync(message, cancellationToken);
        }

        public async Task CloseAsync(string reason = "client closing")
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    {
                        await socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure, reason, cts.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                // Closing a socket that is already Aborted/Closed is not a
                // failure — it means something else got there first. On exit that
                // is routine: Dispose and the ProcessExit handler both close, and
                // whichever loses the race used to report an error for doing
                // exactly what it was supposed to do. Only a socket still claiming
                // to be Open is genuinely unexpected.
                if (socket.State == WebSocketState.Open)
                {
                    Console.WriteLine($"[live] close failed: {ex.Message}");
                }
            }
            finally
            {
                pumpCts.Cancel();
                RaiseClosed(socket.CloseStatus, reason);
            }
        }

        // ---- setup ---------------------------------------------------------

        private object BuildSetupMessage()
        {
            var generationConfig = new Dictionary<string, object>
            {
                ["responseModalities"] = new[] { "AUDIO" }
            };

            if (!string.IsNullOrEmpty(options.Voice) || !string.IsNullOrEmpty(options.LanguageCode))
            {
                var speechConfig = new Dictionary<string, object>();

                if (!string.IsNullOrEmpty(options.Voice))
                {
                    speechConfig["voiceConfig"] = new Dictionary<string, object>
                    {
                        ["prebuiltVoiceConfig"] = new Dictionary<string, object>
                        {
                            ["voiceName"] = options.Voice
                        }
                    };
                }

                // Opt-in only. Native-audio models choose the language themselves
                // and reject an explicit code, so sending this unasked would break
                // the default configuration. It exists for the half-cascade models
                // (gemini-3.1-flash-live-preview), which run a separate ASR that
                // DOES take a language and can therefore be pinned to English.
                if (!string.IsNullOrEmpty(options.LanguageCode))
                {
                    speechConfig["languageCode"] = options.LanguageCode;
                }

                generationConfig["speechConfig"] = speechConfig;
            }

            var setup = new Dictionary<string, object>
            {
                // The `models/` prefix is required here even though the REST
                // endpoint takes the bare id.
                ["model"] = "models/" + options.Model,
                ["generationConfig"] = generationConfig
            };

            if (!string.IsNullOrEmpty(options.SystemInstruction))
            {
                setup["systemInstruction"] = new Dictionary<string, object>
                {
                    ["parts"] = new object[]
                    {
                        new Dictionary<string, object> { ["text"] = options.SystemInstruction }
                    }
                };
            }

            var tools = new List<object>();
            if (options.Tools != null && options.Tools.Count > 0)
            {
                // Exactly one tool serialiser in the codebase — see
                // GeminiService.BuildFunctionDeclarations.
                tools.Add(new Dictionary<string, object>
                {
                    ["function_declarations"] = GeminiService.BuildFunctionDeclarations(options.Tools)
                });
            }
            if (options.EnableGoogleSearch)
            {
                tools.Add(new Dictionary<string, object> { ["google_search"] = new Dictionary<string, object>() });
            }
            if (tools.Count > 0) setup["tools"] = tools;

            if (options.ManualActivityDetection)
            {
                setup["realtimeInputConfig"] = new Dictionary<string, object>
                {
                    ["automaticActivityDetection"] = new Dictionary<string, object>
                    {
                        ["disabled"] = true
                    }
                };
            }

            // Both transcriptions on: Phase 5's bubble needs them, and they make
            // this client debuggable without listening to it.
            if (options.InputAudioTranscription)
                setup["inputAudioTranscription"] = new Dictionary<string, object>();
            if (options.OutputAudioTranscription)
                setup["outputAudioTranscription"] = new Dictionary<string, object>();

            return new Dictionary<string, object> { ["setup"] = setup };
        }

        // ---- transport -----------------------------------------------------

        private async Task SendJsonAsync(object message, CancellationToken cancellationToken)
        {
            byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, JsonOpts));

            await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (socket.State != WebSocketState.Open)
                    throw new InvalidOperationException($"Live socket is {socket.State}, cannot send.");

                await socket.SendAsync(
                    new ArraySegment<byte>(payload),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                sendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[ReceiveBufferSize];
            WebSocketCloseStatus? closeStatus = null;
            string closeReason = "receive loop ended";

            try
            {
                while (!cancellationToken.IsCancellationRequested &&
                       socket.State == WebSocketState.Open)
                {
                    // Messages arrive fragmented across frames — accumulate until
                    // EndOfMessage or JSON parsing fails intermittently under
                    // audio load. This is the single likeliest source of flaky
                    // bugs in this client; do not "optimise" it back to a single
                    // ReceiveAsync.
                    using (var accumulated = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await socket.ReceiveAsync(
                                new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);

                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                closeStatus = result.CloseStatus;
                                closeReason = result.CloseStatusDescription ?? "server closed";
                                return;
                            }

                            accumulated.Write(buffer, 0, result.Count);
                        }
                        while (!result.EndOfMessage);

                        if (accumulated.Length == 0) continue;

                        // The server sends JSON in binary frames as often as text
                        // ones; both decode as UTF-8.
                        HandleMessage(Encoding.UTF8.GetString(accumulated.ToArray()));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                closeReason = "cancelled";
            }
            catch (Exception ex)
            {
                closeReason = ex.Message;
                Console.WriteLine($"[live] receive loop failed: {ex.Message}");

                // Unblock ConnectAsync if this died during the handshake, rather
                // than letting it sit until SetupTimeout.
                setupTcs?.TrySetException(ex);
            }
            finally
            {
                RaiseClosed(closeStatus, closeReason);
            }
        }

        // ---- parsing -------------------------------------------------------

        private void HandleMessage(string json)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"[live] unparseable message ({ex.Message}): {Truncate(json, 200)}");
                return;
            }

            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    Console.WriteLine($"[live] unexpected root {root.ValueKind}");
                    return;
                }

                bool handled = false;

                if (TryGet(root, "setupComplete", "setup_complete", out _))
                {
                    handled = true;
                    setupTcs?.TrySetResult(true);
                    Raise(SetupComplete);
                }

                if (TryGet(root, "serverContent", "server_content", out var serverContent))
                {
                    handled = true;
                    HandleServerContent(serverContent);
                }

                if (TryGet(root, "toolCall", "tool_call", out var toolCall))
                {
                    handled = true;
                    HandleToolCall(toolCall);
                }

                if (TryGet(root, "toolCallCancellation", "tool_call_cancellation", out var cancellation))
                {
                    handled = true;
                    HandleToolCallCancellation(cancellation);
                }

                if (TryGet(root, "goAway", "go_away", out var goAway))
                {
                    handled = true;
                    TimeSpan? left = null;
                    if (TryGet(goAway, "timeLeft", "time_left", out var timeLeft) &&
                        timeLeft.ValueKind == JsonValueKind.String)
                    {
                        // Serialised as a proto Duration, e.g. "42.5s".
                        string raw = timeLeft.GetString();
                        if (!string.IsNullOrEmpty(raw) &&
                            double.TryParse(raw.TrimEnd('s'),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out double seconds))
                        {
                            left = TimeSpan.FromSeconds(seconds);
                        }
                    }
                    Console.WriteLine($"[live] server going away, time left: {left?.ToString() ?? "unknown"}");
                    Raise(GoingAway, left);
                }

                // usageMetadata rides along with other messages and is not a
                // message type of its own — never let it mark one as handled.
                if (!handled && !IsOnly(root, "usageMetadata", "usage_metadata"))
                {
                    // Preview models: the schema will drift. Log and keep the
                    // session alive rather than throwing on something new.
                    Console.WriteLine($"[live] unhandled message: {Truncate(json, 300)}");
                }
            }
        }

        private void HandleServerContent(JsonElement serverContent)
        {
            if (TryGet(serverContent, "inputTranscription", "input_transcription", out var inputTx) &&
                TryGetString(inputTx, "text", out string inputText))
            {
                Raise(InputTranscript, inputText);
            }

            if (TryGet(serverContent, "outputTranscription", "output_transcription", out var outputTx) &&
                TryGetString(outputTx, "text", out string outputText))
            {
                Raise(OutputTranscript, outputText);
            }

            if (TryGet(serverContent, "modelTurn", "model_turn", out var modelTurn) &&
                modelTurn.TryGetProperty("parts", out var parts) &&
                parts.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement part in parts.EnumerateArray())
                {
                    if (!TryGet(part, "inlineData", "inline_data", out var inlineData)) continue;
                    if (!TryGetString(inlineData, "data", out string base64) ||
                        string.IsNullOrEmpty(base64)) continue;

                    if (TryGetString(inlineData, "mimeType", out string mimeType) ||
                        TryGetString(inlineData, "mime_type", out mimeType))
                    {
                        int rate = ParseRate(mimeType);
                        if (rate > 0) OutputSampleRate = rate;
                    }

                    byte[] pcm;
                    try
                    {
                        pcm = Convert.FromBase64String(base64);
                    }
                    catch (FormatException ex)
                    {
                        Console.WriteLine($"[live] bad audio base64: {ex.Message}");
                        continue;
                    }
                    Raise(AudioReceived, pcm);
                }
            }

            if (TryGetBool(serverContent, "interrupted") == true)
            {
                Raise(Interrupted);
            }

            // generationComplete means "model finished generating"; turnComplete
            // means "the turn is over". Only the latter ends a turn.
            if (TryGetBool(serverContent, "turnComplete") == true ||
                TryGetBool(serverContent, "turn_complete") == true)
            {
                Raise(TurnComplete);
            }
        }

        private void HandleToolCall(JsonElement toolCall)
        {
            if (!TryGet(toolCall, "functionCalls", "function_calls", out var functionCalls) ||
                functionCalls.ValueKind != JsonValueKind.Array)
            {
                Console.WriteLine("[live] toolCall with no functionCalls array");
                return;
            }

            var calls = new List<LiveFunctionCall>();
            foreach (JsonElement call in functionCalls.EnumerateArray())
            {
                if (!TryGetString(call, "name", out string name) || string.IsNullOrEmpty(name)) continue;
                TryGetString(call, "id", out string id);

                var args = new Dictionary<string, string>();
                if (call.TryGetProperty("args", out var argsEl) &&
                    argsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty arg in argsEl.EnumerateObject())
                    {
                        // Strings unquoted, everything else raw — same shape the
                        // turn-based path produces, so TryValidate is unchanged.
                        args[arg.Name] = arg.Value.ValueKind == JsonValueKind.String
                            ? arg.Value.GetString()
                            : arg.Value.GetRawText();
                    }
                }

                calls.Add(new LiveFunctionCall(id, name, args));
            }

            if (calls.Count > 0) Raise(ToolCallReceived, (IReadOnlyList<LiveFunctionCall>)calls);
        }

        private void HandleToolCallCancellation(JsonElement cancellation)
        {
            if (!cancellation.TryGetProperty("ids", out var ids) ||
                ids.ValueKind != JsonValueKind.Array) return;

            var cancelled = new List<string>();
            foreach (JsonElement id in ids.EnumerateArray())
            {
                if (id.ValueKind == JsonValueKind.String) cancelled.Add(id.GetString());
            }

            if (cancelled.Count > 0) Raise(ToolCallCancelled, (IReadOnlyList<string>)cancelled);
        }

        // ---- helpers -------------------------------------------------------

        // Proto JSON is lowerCamelCase, but these are preview surfaces and the
        // casing has moved before. Accepting both costs nothing.
        private static bool TryGet(JsonElement el, string camel, string snake, out JsonElement value)
        {
            if (el.TryGetProperty(camel, out value)) return true;
            return el.TryGetProperty(snake, out value);
        }

        private static bool TryGetString(JsonElement el, string name, out string value)
        {
            value = null;
            if (el.ValueKind != JsonValueKind.Object) return false;
            if (!el.TryGetProperty(name, out var prop)) return false;
            if (prop.ValueKind != JsonValueKind.String) return false;
            value = prop.GetString();
            return true;
        }

        private static bool? TryGetBool(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var prop)) return null;
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
            return null;
        }

        // True when the object carries nothing but the named property — used so a
        // bare usageMetadata heartbeat isn't reported as an unknown message.
        private static bool IsOnly(JsonElement el, string camel, string snake)
        {
            bool sawNamed = false;
            foreach (JsonProperty p in el.EnumerateObject())
            {
                if (p.Name == camel || p.Name == snake) { sawNamed = true; continue; }
                return false;
            }
            return sawNamed;
        }

        // "audio/pcm;rate=24000" -> 24000, or 0 when absent/unparseable.
        private static int ParseRate(string mimeType)
        {
            if (string.IsNullOrEmpty(mimeType)) return 0;
            const string marker = "rate=";
            int at = mimeType.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return 0;

            int start = at + marker.Length;
            int end = start;
            while (end < mimeType.Length && char.IsDigit(mimeType[end])) end++;
            if (end == start) return 0;

            return int.TryParse(mimeType.Substring(start, end - start), out int rate) ? rate : 0;
        }

        private static Dictionary<string, object> ToObjectMap(IReadOnlyDictionary<string, string> map)
        {
            var result = new Dictionary<string, object>();
            if (map != null)
            {
                foreach (KeyValuePair<string, string> kv in map) result[kv.Key] = kv.Value;
            }
            return result;
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

        // A throwing handler must not take down the receive pump — one bad
        // subscriber would otherwise close the session.
        private static void Raise(Action handler)
        {
            try { handler?.Invoke(); }
            catch (Exception ex) { Console.WriteLine($"[live] handler threw: {ex.Message}"); }
        }

        private static void Raise<T>(Action<T> handler, T arg)
        {
            try { handler?.Invoke(arg); }
            catch (Exception ex) { Console.WriteLine($"[live] handler threw: {ex.Message}"); }
        }

        // Closed fires exactly once no matter how many paths reach it — Phase 5's
        // fallback must not be triggered twice for one session.
        private void RaiseClosed(WebSocketCloseStatus? status, string reason)
        {
            if (Interlocked.Exchange(ref closedRaised, 1) != 0) return;

            // Anything still awaiting the handshake needs to stop waiting.
            setupTcs?.TrySetException(
                new WebSocketException($"Live session closed before setup completed: {reason}"));

            try { Closed?.Invoke(status, reason); }
            catch (Exception ex) { Console.WriteLine($"[live] Closed handler threw: {ex.Message}"); }
        }

        // Phase 3's watchdog depends on this being reliable: one leaked socket
        // streaming silence burns ~115k tokens/hour against an unpublished free
        // -tier quota.
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;

            try { pumpCts.Cancel(); } catch { /* already disposed */ }

            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    socket.Abort(); // CloseAsync needs an await; Dispose cannot
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[live] abort failed: {ex.Message}");
            }

            RaiseClosed(socket.CloseStatus, "disposed");

            socket.Dispose();
            pumpCts.Dispose();
            sendLock.Dispose();
        }
    }
}
