using Personal_Assistant.Configuration;
using Personal_Assistant.Diagnostics;
using Personal_Assistant.Dispatch;
using Personal_Assistant.LLMClient;
using Personal_Assistant.STTClient;
using Personal_Assistant.TTSClient;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Personal_Assistant.CallScreening
{
    /// <summary>One message in a screened call, in the shape LM Studio wants.</summary>
    /// <remarks>
    /// NOT <c>ConversationTurn</c>, and the difference is load-bearing. That type
    /// records a tool by name and arguments only, and <c>BuildMessages</c> renders
    /// its result as the literal string "done" — which is correct for the desktop,
    /// where the answer was already spoken by the time the turn is remembered. On a
    /// call the tool result IS the next thing said: <c>take_message</c> hands back
    /// the message exactly as recorded so the model can read THAT back rather than
    /// what it meant to write down, and "done" would throw away the only thing
    /// making the read-back honest.
    /// </remarks>
    internal sealed class CallMessage
    {
        public string Role;            // user | assistant | tool
        public string Content;
        public string ToolCallId;      // assistant tool_calls, and the matching tool
        public string ToolName;
        public string ToolArgsJson;

        public static CallMessage User(string text) =>
            new CallMessage { Role = "user", Content = text ?? string.Empty };

        public static CallMessage Assistant(string text) =>
            new CallMessage { Role = "assistant", Content = text ?? string.Empty };

        public static CallMessage AssistantCall(string id, string name, string argsJson) =>
            new CallMessage
            {
                Role = "assistant",
                Content = string.Empty,
                ToolCallId = id,
                ToolName = name,
                ToolArgsJson = string.IsNullOrEmpty(argsJson) ? "{}" : argsJson
            };

        public static CallMessage ToolReply(string id, string name, string content) =>
            new CallMessage
            {
                Role = "tool",
                ToolCallId = id,
                ToolName = name,
                Content = content ?? "{}"
            };
    }

    /// <summary>What one model turn produced: what to say, and what to run.</summary>
    internal sealed class CallTurnResult
    {
        public string Text = string.Empty;
        public List<ToolInvocation> Calls = new List<ToolInvocation>();
        public List<string> CallIds = new List<string>();
        public List<string> CallArgsJson = new List<string>();
        public bool Failed;
        public string Failure;

        public bool HasCalls => Calls.Count > 0;
    }

    /// <summary>
    /// One streamed turn against LM Studio, with the call's four tools attached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// STREAMED, WITH TOOLS, IN ONE ROUND TRIP — and that is the whole reason this
    /// exists rather than reusing <c>LocalLLMService.DetectToolAsync</c> followed by
    /// <c>StreamResponse</c>. That pair is the right shape for the desktop, where a
    /// second round trip costs a moment of a conversation the user is already
    /// watching. On a phone line it costs a whole model load-and-generate cycle in
    /// the gap between the caller finishing a sentence and hearing anything back,
    /// and that gap is the one number this phase exists to measure.
    /// </para>
    /// <para>
    /// So content deltas and tool_call deltas are accumulated from the SAME stream:
    /// the first speakable sentence goes to Kokoro while the rest is still
    /// generating, and a turn that both says goodbye and calls hang_up — the
    /// commonest shape at the end of a call — arrives whole.
    /// </para>
    /// </remarks>
    internal static class CallModel
    {
        // Its own client, and its own timeout. The shared one is 60s, which is
        // right for a desktop answer and far too long here: a caller listening to
        // a minute of nothing has hung up, and the honest thing to do is give up
        // and let the silence watchdog end the call rather than answer into a
        // line nobody is on any more.
        private static readonly HttpClient http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(LaithConfig.Int("CallModelTimeoutSeconds", 25, 5, 120))
        };

        private static readonly JsonSerializerOptions Raw = new JsonSerializerOptions();

        public static async Task<CallTurnResult> TurnAsync(
            string systemInstruction,
            IReadOnlyList<CallMessage> history,
            IReadOnlyList<ToolDefinition> tools,
            Func<string, Task> onSentence,
            CancellationToken ct)
        {
            var result = new CallTurnResult();

            var messages = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["role"] = "system",
                    ["content"] = systemInstruction ?? string.Empty
                }
            };

            foreach (CallMessage m in history) messages.Add(Encode(m));

            var body = new Dictionary<string, object>
            {
                ["messages"] = messages,
                // NEVER registry.ToolDefinitions. See CallTools: this list is the
                // whole of the containment, and it is built by name and by hand.
                ["tools"] = LocalLLMService.ToolSchemas(tools),
                ["tool_choice"] = "auto",
                // Warm enough not to sound like a form, cold enough that it does
                // not invent facts about somebody it has never met.
                ["temperature"] = LaithConfig.Double("CallTemperature", 0.4, 0.0, 1.5),
                // Short on purpose. The persona already says one or two sentences;
                // this is what makes that true even when the model disagrees, and
                // every token past the second sentence is latency a caller pays
                // for in silence.
                ["max_tokens"] = LaithConfig.Int("CallMaxTokens", 160, 32, 512),
                // Non-negotiable here for the same reason as everywhere else on
                // this branch: a model that spends its budget thinking returns
                // empty content, and on a call empty content is dead air. See
                // LocalLLMService.BuildToolRequest for the measurements — "/no
                // think" in a prompt does NOT do this reliably.
                ["reasoning_effort"] = "none",
                ["stream"] = true
            };

            var content = new StringContent(
                JsonSerializer.Serialize(body, Raw), Encoding.UTF8, "application/json");

            string endpoint = $"{LocalLLMService.lmStudioUrl.TrimEnd('/')}/chat/completions";

            var spoken = new StringBuilder();   // everything, for the transcript
            var pending = new StringBuilder();  // not yet cut into a sentence
            var partials = new SortedDictionary<int, PartialCall>();
            bool first = true;

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content })
                using (HttpResponseMessage response = await http
                    .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        result.Failed = true;
                        result.Failure = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                        return result;
                    }

                    using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
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

                            string delta = Apply(data, partials);
                            if (string.IsNullOrEmpty(delta)) continue;

                            spoken.Append(delta);
                            pending.Append(delta);

                            // A small model sometimes emits a tool call as plain
                            // TEXT rather than through the tool_calls channel.
                            // Down a phone line that is read out as
                            // "...hang up, arguments, slash tool call", which is
                            // both nonsense and a tell that the model wanted a
                            // tool. Everything from the marker on is protocol.
                            int cut = LocalLLMService.ToolCallStart(spoken);
                            if (cut >= 0)
                            {
                                Console.WriteLine(
                                    "[call] the model wrote a tool call as text — not speaking it");
                                spoken.Length = cut;
                                pending.Clear();
                                break;
                            }

                            string sentence;
                            while (SentenceChunker.TryTake(pending, false, first, out sentence))
                            {
                                first = false;
                                await onSentence(sentence).ConfigureAwait(false);
                            }
                        }
                    }
                }

                string tail;
                while (SentenceChunker.TryTake(pending, true, first, out tail))
                {
                    first = false;
                    await onSentence(tail).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Barge-in, or the call ending. Whatever was already said stands.
                result.Text = spoken.ToString().Trim();
                return result;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                {
                    result.Text = spoken.ToString().Trim();
                    return result;
                }

                result.Failed = true;
                result.Failure = $"{ex.GetType().Name}: {ex.Message}";
                return result;
            }

            result.Text = spoken.ToString().Trim();

            foreach (PartialCall c in partials.Values)
            {
                if (string.IsNullOrWhiteSpace(c.Name)) continue;
                result.Calls.Add(new ToolInvocation(c.Name, ParseArgs(c.Args.ToString())));
                result.CallIds.Add(c.Id ?? "call_" + result.Calls.Count);
                result.CallArgsJson.Add(
                    c.Args.Length == 0 ? "{}" : c.Args.ToString());
            }

            return result;
        }

        private sealed class PartialCall
        {
            public string Id;
            public string Name;
            public readonly StringBuilder Args = new StringBuilder();
        }

        // One SSE frame. Returns the content delta (possibly empty) and folds any
        // tool_call delta into `partials`.
        //
        // Tool calls arrive SPLIT ACROSS FRAMES: the first carries the id and the
        // name with empty arguments, and the arguments dribble in afterwards as
        // fragments of a JSON string keyed by `index`. Treating each frame as a
        // whole call is how you end up with take_message("{\"mess") .
        private static string Apply(string data, SortedDictionary<int, PartialCall> partials)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(data))
                {
                    if (!doc.RootElement.TryGetProperty("choices", out JsonElement choices) ||
                        choices.GetArrayLength() == 0) return string.Empty;
                    if (!choices[0].TryGetProperty("delta", out JsonElement delta)) return string.Empty;

                    if (delta.TryGetProperty("tool_calls", out JsonElement calls) &&
                        calls.ValueKind == JsonValueKind.Array)
                    {
                        int fallback = 0;
                        foreach (JsonElement call in calls.EnumerateArray())
                        {
                            int index = call.TryGetProperty("index", out JsonElement idx) &&
                                        idx.ValueKind == JsonValueKind.Number
                                ? idx.GetInt32()
                                : fallback;
                            fallback++;

                            if (!partials.TryGetValue(index, out PartialCall partial))
                            {
                                partial = new PartialCall();
                                partials[index] = partial;
                            }

                            if (call.TryGetProperty("id", out JsonElement id) &&
                                id.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrEmpty(id.GetString()))
                            {
                                partial.Id = id.GetString();
                            }

                            if (!call.TryGetProperty("function", out JsonElement fn)) continue;

                            if (fn.TryGetProperty("name", out JsonElement name) &&
                                name.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrEmpty(name.GetString()))
                            {
                                partial.Name = name.GetString();
                            }

                            if (fn.TryGetProperty("arguments", out JsonElement args) &&
                                args.ValueKind == JsonValueKind.String)
                            {
                                partial.Args.Append(args.GetString());
                            }
                        }
                    }

                    if (delta.TryGetProperty("content", out JsonElement text) &&
                        text.ValueKind == JsonValueKind.String)
                    {
                        return text.GetString() ?? string.Empty;
                    }

                    return string.Empty;
                }
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }

        private static Dictionary<string, string> ParseArgs(string json)
        {
            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(json)) return args;

            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) return args;
                    foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                    {
                        args[p.Name] = p.Value.ValueKind == JsonValueKind.String
                            ? p.Value.GetString()
                            : p.Value.GetRawText();
                    }
                }
            }
            catch (JsonException)
            {
                // A truncated arguments string means the model ran out of budget
                // mid-call. Named rather than swallowed: an empty take_message is
                // a message that never reaches him.
                Console.WriteLine($"[call] could not parse tool arguments: {Preview(json)}");
            }

            return args;
        }

        private static string Preview(string s) =>
            s == null ? "(null)" : (s.Length <= 80 ? s : s.Substring(0, 80) + "...");

        private static Dictionary<string, object> Encode(CallMessage m)
        {
            if (m.Role == "tool")
            {
                return new Dictionary<string, object>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = m.ToolCallId,
                    ["name"] = m.ToolName,
                    ["content"] = m.Content
                };
            }

            if (m.Role == "assistant" && m.ToolName != null)
            {
                return new Dictionary<string, object>
                {
                    ["role"] = "assistant",
                    ["content"] = m.Content ?? string.Empty,
                    ["tool_calls"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["id"] = m.ToolCallId,
                            ["type"] = "function",
                            ["function"] = new Dictionary<string, object>
                            {
                                ["name"] = m.ToolName,
                                ["arguments"] = m.ToolArgsJson
                            }
                        }
                    }
                };
            }

            return new Dictionary<string, object>
            {
                ["role"] = m.Role,
                ["content"] = m.Content ?? string.Empty
            };
        }
    }

    /// <summary>
    /// One screened call's conversation, run entirely on this machine: the
    /// caller's audio into Parakeet, the turn into LM Studio, the reply out of
    /// Kokoro and down the line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHAT CHANGED FROM THE BRANCH THIS CAME FROM. Upstream drives a Gemini Live
    /// socket: audio goes up, audio comes back, the server does the endpointing and
    /// the barge-in and the turn-taking, and this class is mostly plumbing around
    /// an event stream. None of that exists here. There is no server to end a turn,
    /// so this class owns the endpointer; no server VAD, so it owns the barge-in;
    /// no continuous audio channel, so a turn is four discrete steps with a
    /// measurable seam between each.
    /// </para>
    /// <para>
    /// The STRUCTURE is upstream's and deliberately so — the wrap-up warning, the
    /// debounced disconnect check, the one-shot silence nudge, the hard cap, the
    /// drain before teardown, the read-back loop on take_message. Every one of
    /// those was paid for by a real call going wrong, and none of them is about
    /// which model is answering.
    /// </para>
    /// <para>
    /// THE MICROPHONE IS NOT INVOLVED. <c>ContinuousListener</c> owns the mic for
    /// the life of the process and its <c>NextUtteranceAsync</c> has a single
    /// waiter slot; a call that reached for either would be fighting the desktop
    /// assistant for a device it does not need. The caller arrives through a WASAPI
    /// loopback in <see cref="CallAudioBridge"/>, so the two never meet. What they
    /// DO share is the room: the speakers are the call's inbound leg, so anything
    /// the desktop assistant says out loud goes down the phone and comes back as
    /// the caller's next sentence. That is handled where it belongs — the wake word
    /// is ignored for the duration (Program.Main) and PresenceGate is muted (the
    /// hush in CallScreeningService).
    /// </para>
    /// <para>
    /// THE CALLER IS UNTRUSTED. This session is never given
    /// <c>registry.ToolDefinitions</c>; it gets <see cref="CallTools"/>, which is
    /// two borrowed read-only tools plus two that only exist for the duration of
    /// the call. A stranger saying "send a text to Mum" must do nothing at all.
    /// </para>
    /// </remarks>
    public sealed class CallSession : IDisposable
    {
        private readonly object gate = new object();

        private readonly CallAudioBridge bridge;
        private readonly CallTools tools;
        private readonly string systemInstruction;
        private readonly string voice;
        private readonly TimeSpan maxCall;
        private readonly TimeSpan silenceLimit;
        private readonly TimeSpan wrapUpAt;
        private readonly int disconnectChecks;
        private readonly bool bargeInEnabled;

        // How long a goodbye may take to reach the caller before the call is ended
        // anyway. Long enough for a farewell sentence, short enough that a model
        // which called hang_up and then kept talking cannot hold the line open.
        private static readonly TimeSpan GoodbyeGrace = TimeSpan.FromSeconds(6);

        // Complete caller utterances, handed from the bridge's timer thread to the
        // conversation loop. Bounded and non-blocking on the producer side: a
        // stalled loop is a DROPPED utterance rather than a stalled audio thread,
        // and the one thing the frame handler must never do is block.
        private readonly BlockingCollection<byte[]> utterances =
            new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(), 4);
        private int dropped;

        private readonly List<CallTranscriptLine> transcript = new List<CallTranscriptLine>();
        private readonly List<CallMessage> history = new List<CallMessage>();

        private readonly Stopwatch clock = new Stopwatch();
        private readonly TaskCompletionSource<CallEnding> finished =
            new TaskCompletionSource<CallEnding>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Last moment anything was heard from either end. The dead-line detector.
        private DateTime lastSound = DateTime.Now;

        private string message;
        private CancellationTokenSource life;

        // Cancels the turn in flight. Replaced per turn; a barge-in cancels it.
        private CancellationTokenSource turn;
        private int bargeIns;
        private int modelFailures;

        // Per-call latency, reusing the everyday tracker rather than a second one:
        // the numbers mean the same thing (understanding, the model, synthesis,
        // and time to first audio) and the summary line is already the shape
        // anybody debugging this would want. Reset per turn, printed per turn.
        private readonly LatencyTracker latency = new LatencyTracker();

        public CallSession(
            CallAudioBridge bridge,
            CallTools tools,
            string systemInstruction,
            TimeSpan maxCall)
        {
            this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            this.tools = tools ?? CallTools.None;
            this.systemInstruction = systemInstruction;
            this.maxCall = maxCall;

            // Through CallGreeting, which is the one accessor for this setting —
            // the greeting clips are cached under it, and a second copy of the
            // expression here could drift and greet the caller in a voice the
            // conversation then does not use.
            voice = CallGreeting.Voice;

            silenceLimit = TimeSpan.FromSeconds(
                LaithConfig.Int("CallSilenceSeconds", 15, 5, 120));

            // How many CONSECUTIVE reads must say the line is gone before it is
            // believed. See WatchAsync — one is not enough.
            disconnectChecks = LaithConfig.Int("CallDisconnectChecks", 2, 1, 10);

            // Clamped against the budget rather than merely configured. On a short
            // call a fixed 45s warning would fire on the first tick and have the
            // model saying goodbye before the caller had finished saying hello.
            wrapUpAt = TimeSpan.FromSeconds(Math.Min(
                LaithConfig.Int("CallWrapUpSeconds", 45, 0, 300),
                maxCall.TotalSeconds / 3));

            bargeInEnabled = LaithConfig.Bool("CallBargeIn", true);
        }

        /// <summary>What take_message recorded, if anything.</summary>
        public string Message { get { lock (gate) return message; } }

        /// <summary>
        /// Raised as each transcript line is finalised, for anything that wants to
        /// watch the call as it happens rather than read it afterwards.
        /// </summary>
        /// <remarks>
        /// Fires on whatever thread finished the line, and it fires while a
        /// stranger is on the line: a handler that blocks stalls the call. The
        /// widget's handler only marshals onto its own UI thread, which is the
        /// right shape for anything else that subscribes.
        /// </remarks>
        public event Action<CallTranscriptLine> LineRecorded;

        /// <summary>
        /// Holds the conversation until somebody ends it.
        /// </summary>
        /// <param name="call">Who is on the phone, as the transport described them.</param>
        /// <param name="stillConnected">
        /// Asked every couple of seconds. False means the caller hung up — the one
        /// ending nothing else can detect, because a caller who leaves says
        /// nothing, which is indistinguishable from a caller who is thinking.
        /// </param>
        public async Task<CallOutcome> RunAsync(
            IncomingCall call, Func<bool> stillConnected, CancellationToken cancel = default)
        {
            var outcome = new CallOutcome
            {
                Caller = call?.Caller ?? "an unknown number",
                StartedAt = DateTime.Now,
            };

            clock.Start();
            life = CancellationTokenSource.CreateLinkedTokenSource(cancel);

            if (!bridge.IsRunning)
            {
                // The one failure that has to be classified before anything is
                // said. A caller listening to a session that never opened is the
                // worst outcome available, so this is loud and the service hangs
                // up on it rather than hoping.
                outcome.Ending = CallEnding.NeverStarted;
                outcome.Failure = "the audio bridge is not running";
                outcome.Duration = clock.Elapsed;
                Console.WriteLine($"[call] cannot converse — {outcome.Failure}");
                return outcome;
            }

            Console.WriteLine(
                $"[call] conversing locally (parakeet -> lm studio -> kokoro '{voice}', " +
                $"{tools.Definitions.Count} tools: {tools.Describe()})");

            bridge.FrameCaptured += OnCallerFrame;
            Task talking = Task.Run(() => ConverseAsync(life.Token));
            Task watching = Task.Run(() => WatchAsync(stillConnected, life.Token));

            try
            {
                outcome.Ending = await finished.Task.ConfigureAwait(false);
            }
            finally
            {
                // TRACED STEP BY STEP, because upstream a real call hung somewhere
                // in the equivalent block and the log simply stopped — every one of
                // these is an await that can, in principle, never come back, and
                // nothing distinguished them from outside.
                Console.WriteLine("[call/teardown] unhooking the capture");
                bridge.FrameCaptured -= OnCallerFrame;
                try { utterances.CompleteAdding(); } catch { }
                try { turn?.Cancel(); } catch { }
                life.Cancel();

                Console.WriteLine("[call/teardown] waiting for the conversation loop");
                try
                {
                    await Task.WhenAny(Task.WhenAll(talking, watching), Task.Delay(2000))
                        .ConfigureAwait(false);
                }
                catch { }
                Console.WriteLine("[call/teardown] conversation loop stopped");
            }

            outcome.Duration = clock.Elapsed;
            lock (gate)
            {
                outcome.Message = message;
                outcome.Transcript = transcript.ToList();
            }
            Console.WriteLine("[call/teardown] outcome assembled");

            if (Volatile.Read(ref dropped) > 0)
            {
                // Named because it is the tell for "it ignored me": the
                // conversation loop was still on the previous turn when the caller
                // finished another sentence.
                Console.WriteLine($"[call] {dropped} caller utterance(s) were dropped unheard.");
            }

            return outcome;
        }

        // --- hearing the caller ---------------------------------------------------

        // Below this a frame is the line idling, not somebody speaking. The same
        // floor the preflight uses for "something rather than nothing", about
        // -46 dBFS, and it sits above the 0.0001–0.0015 a real line was measured
        // idling at.
        private static readonly double SpeechFloor =
            LaithConfig.Double("CallSpeechFloor", 0.005, 0.0001, 0.5);

        // A barge-in has to clear a HIGHER bar than "somebody is talking", because
        // the cost of getting it wrong is asymmetric: a missed barge-in is a caller
        // waiting out one sentence, while a false one cuts the assistant off
        // mid-word and leaves a transcript line with no speech behind it. The
        // inbound gain aims the mean at 0.05, so this is roughly "as loud as
        // speech, after the gain stage has settled".
        private static readonly double BargeInFloor =
            LaithConfig.Double("CallBargeInFloor", 0.04, 0.001, 1.0);

        // 20 ms per frame, so these are milliseconds divided by twenty.
        private const int OnsetFrames = 6;        // 120 ms of sound starts an utterance
        private const int BargeInFrames = 15;     // 300 ms of LOUD sound interrupts
        private const int HangoverFrames = 35;    // 700 ms of quiet ends one
        private const int PreRollFrames = 10;     // 200 ms kept from before the onset
        private const int MaxUtteranceFrames = 20 * 50;   // 20s: a monologue, not a turn

        private readonly Queue<byte[]> preRoll = new Queue<byte[]>();
        private readonly List<byte[]> utterance = new List<byte[]>();
        private int voiced;
        private int quiet;
        private int loud;
        private bool inSpeech;

        /// <summary>
        /// The bridge's timer thread: 50 frames a second, whether or not anybody is
        /// talking. Hands off and returns.
        /// </summary>
        /// <remarks>
        /// <para>
        /// THIS IS THE ENDPOINTER, and it is a departure worth naming. The desktop
        /// runs Silero through <c>ContinuousListener</c>, which is better at this
        /// than an energy threshold — but it is welded to the microphone capture it
        /// owns, it holds Python's GIL to run, and its VAD state is a single global
        /// that <c>Arm()</c> resets. Driving it from a second, concurrent audio
        /// source would have the call and the desktop assistant resetting each
        /// other's speech state, which is a far worse failure than a threshold that
        /// occasionally cuts a quiet caller short.
        /// </para>
        /// <para>
        /// Energy is a fair substitute HERE specifically, and would not be on the
        /// microphone: this leg has an AGC in front of it aiming the mean at a
        /// known target, and the line's idle floor was measured two decades below
        /// speech. What it cannot do is tell speech from a television in the
        /// caller's room, which is why the hangover is generous and the barge-in
        /// bar is high.
        /// </para>
        /// </remarks>
        private void OnCallerFrame(byte[] frame)
        {
            double level = CallAudioFormat.Rms(frame);

            // LEVEL, not arrival. The bridge emits a frame every 20 ms whether the
            // caller is talking or not, so "a frame arrived" says nothing at all
            // about whether anybody is there — and treating arrival as sound would
            // make the dead-line detector unable to ever fire.
            if (level >= SpeechFloor) lastSound = DateTime.Now;

            // HALF DUPLEX WHILE WE ARE TALKING.
            //
            // Nothing is accumulated while there is audio queued for the caller.
            // The two legs are different endpoints and should never meet, but "should
            // never" is exactly the assumption that produced a call transcribing its
            // own greeting as the caller's first sentence — and the cost of being
            // wrong here is not a glitch, it is the assistant holding a conversation
            // with itself while a stranger listens.
            bool speaking = bridge.OutboundPending > TimeSpan.FromMilliseconds(80);
            if (speaking)
            {
                if (!bargeInEnabled) return;

                // Barge-in: loud enough, for long enough, to be a person talking
                // over us rather than a burst of line noise.
                if (level >= BargeInFloor) loud++;
                else loud = 0;

                if (loud < BargeInFrames) return;

                loud = 0;
                Interlocked.Increment(ref bargeIns);
                Console.WriteLine("[call] the caller talked over the reply — stopping it.");

                // Both halves, or it is cosmetic. Cancelling the turn stops the
                // model and the synthesis; clearing the outbound buffer stops the
                // sentences already queued, which are what the caller would
                // otherwise keep hearing for as long as the synthesis had run
                // ahead of playback.
                try { turn?.Cancel(); } catch { }
                bridge.ClearOutbound();

                // Fall through and start capturing THIS frame: whatever the caller
                // interrupted with is the next thing to answer.
                Reset();
            }
            else
            {
                loud = 0;
            }

            if (!inSpeech)
            {
                preRoll.Enqueue(frame);
                while (preRoll.Count > PreRollFrames) preRoll.Dequeue();

                if (level < SpeechFloor) { voiced = 0; return; }
                if (++voiced < OnsetFrames) return;

                // Started. The pre-roll goes in first, or the first phoneme of
                // every utterance is the one that got it over the threshold.
                inSpeech = true;
                quiet = 0;
                utterance.Clear();
                utterance.AddRange(preRoll);
                preRoll.Clear();
                return;
            }

            utterance.Add(frame);
            quiet = level < SpeechFloor ? quiet + 1 : 0;

            if (quiet < HangoverFrames && utterance.Count < MaxUtteranceFrames) return;

            if (utterance.Count >= MaxUtteranceFrames)
                Console.WriteLine("[call] the caller has been talking for 20s — taking the turn.");

            Emit();
        }

        private void Reset()
        {
            inSpeech = false;
            voiced = 0;
            quiet = 0;
            utterance.Clear();
            preRoll.Clear();
        }

        private void Emit()
        {
            // The hangover is trailing silence by definition, so it is trimmed
            // rather than sent — it is up to 700 ms of nothing that the recogniser
            // would otherwise be paid to listen to.
            int keep = Math.Max(0, utterance.Count - (HangoverFrames - OnsetFrames));
            var pcm = new byte[keep * CallAudioFormat.FrameBytes];
            for (int i = 0; i < keep; i++)
                Buffer.BlockCopy(utterance[i], 0, pcm, i * CallAudioFormat.FrameBytes,
                    CallAudioFormat.FrameBytes);

            Reset();

            if (pcm.Length == 0) return;
            if (!utterances.IsAddingCompleted && utterances.TryAdd(pcm)) return;
            Interlocked.Increment(ref dropped);
        }

        // --- the conversation -----------------------------------------------------

        private async Task ConverseAsync(CancellationToken cancel)
        {
            try
            {
                foreach (byte[] pcm in utterances.GetConsumingEnumerable(cancel))
                {
                    await TakeTurnAsync(pcm, cancel).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[call] the conversation loop stopped: {ex.Message}");
                finished.TrySetResult(CallEnding.SessionLost);
            }
        }

        private async Task TakeTurnAsync(byte[] pcm, CancellationToken cancel)
        {
            latency.Reset();
            var turnClock = Stopwatch.StartNew();

            string said;
            var sttClock = Stopwatch.StartNew();
            try
            {
                said = await SpeechToTextService
                    .TranscribeAsync(CallAudioBridge.ToWav(pcm)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[call] could not transcribe the caller: {ex.Message}");
                NoteModelFailure();
                return;
            }
            latency.RecordStt(sttClock.Elapsed);

            if (string.IsNullOrWhiteSpace(said))
            {
                // Not a failure and not silence: the recogniser heard something and
                // made nothing of it. Logged because a call full of these is a
                // level problem, not a model one.
                Console.WriteLine(
                    $"[call] {pcm.Length / 32}ms of audio transcribed to nothing " +
                    $"(inbound level {bridge.InboundLevel:F4}, gain {bridge.InboundGain:F1}x)");
                return;
            }

            lastSound = DateTime.Now;
            Record(CallSpeaker.Caller, said);
            lock (gate) history.Add(CallMessage.User(said));

            await RespondAsync(turnClock, cancel).ConfigureAwait(false);

            Console.WriteLine($"[call] turn took {turnClock.ElapsedMilliseconds}ms — {latency.Summary()}");
        }

        // One reply, and however many tool round trips it takes to finish it.
        //
        // Bounded at three, which is one more than the deepest legitimate sequence
        // (take_message, read it back, hang_up). Unbounded, a model that keeps
        // calling a tool instead of speaking holds a stranger on a silent line for
        // the whole cap.
        private const int MaxToolRounds = 3;

        private async Task RespondAsync(Stopwatch turnClock, CancellationToken cancel)
        {
            using (var mine = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                Interlocked.Exchange(ref turn, mine)?.Dispose();

                var spoken = new StringBuilder();

                for (int round = 0; round < MaxToolRounds; round++)
                {
                    List<CallMessage> snapshot;
                    lock (gate) snapshot = history.ToList();

                    // The whole round trip, synthesis excluded — LatencyTracker's
                    // llm figure is "all model calls made for the turn", and on a
                    // call there can be several of them before anything is said.
                    // How long until the caller HEARS something is a different
                    // question and is answered by RecordFirstAudio in SayAsync.
                    //
                    // The subtraction is not cosmetic: the sentence callback is
                    // AWAITED inside the stream reader, so every millisecond Kokoro
                    // spends sits inside this stopwatch. Left in, tts would be
                    // counted twice and llm would be blamed for it — and "which
                    // stage is the slow one" is the entire question this phase is
                    // here to answer.
                    Interlocked.Exchange(ref speakingMs, 0);
                    var llmClock = Stopwatch.StartNew();
                    CallTurnResult result = await CallModel.TurnAsync(
                        systemInstruction,
                        snapshot,
                        tools.Definitions,
                        async sentence =>
                        {
                            spoken.Append(sentence).Append(' ');
                            await SayAsync(sentence, turnClock, mine.Token).ConfigureAwait(false);
                        },
                        mine.Token).ConfigureAwait(false);
                    latency.RecordLlm(llmClock.Elapsed -
                        TimeSpan.FromMilliseconds(Interlocked.Read(ref speakingMs)));

                    if (result.Failed)
                    {
                        Console.WriteLine($"[call] the model turn failed: {result.Failure}");
                        NoteModelFailure();
                        break;
                    }

                    if (mine.IsCancellationRequested) break;

                    lock (gate)
                    {
                        if (!string.IsNullOrWhiteSpace(result.Text))
                            history.Add(CallMessage.Assistant(result.Text));
                    }

                    if (!result.HasCalls) break;

                    modelFailures = 0;
                    bool wrappingUp = await RunToolsAsync(result).ConfigureAwait(false);
                    if (wrappingUp) break;
                }

                string line = spoken.ToString().Trim();
                if (line.Length > 0) Record(CallSpeaker.Assistant, line);
            }
        }

        // Synthesis and routing for one sentence.
        //
        // NOT through SpeechService: that ends at a WaveOutEvent on the machine's
        // default output — the speakers — which is both the wrong ear entirely and,
        // since the speakers are this call's INBOUND leg, a straight path from the
        // assistant's mouth back into its own recogniser.
        private long speakingMs;

        private async Task SayAsync(string sentence, Stopwatch turnClock, CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(sentence)) return;

            var cost = Stopwatch.StartNew();
            try
            {
                var ttsClock = Stopwatch.StartNew();
                byte[] wav;
                try
                {
                    wav = await KokoroTTSService
                        .SynthesizeWavAsync(sentence, voice, cancel).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[call] could not synthesise the reply: {ex.Message}");
                    return;
                }
                latency.RecordTts(ttsClock.Elapsed);

                // Null means nothing speakable survived StripUnspeakable — an emoji
                // on its own, most likely. Silence is the right answer, not a crash.
                if (wav == null || cancel.IsCancellationRequested) return;

                byte[] pcm = CallAudioBridge.ReadWavBytesAs16kMono(wav);
                if (pcm.Length == 0) return;

                // Written rather than paced, so the model can keep generating while
                // the caller is still hearing sentence one — the whole point of
                // streaming. The cable writer holds ten seconds and DISCARDS on
                // overflow, so a long reply waits for room rather than losing its
                // tail silently.
                while (bridge.OutboundPending > TimeSpan.FromSeconds(6) &&
                       !cancel.IsCancellationRequested)
                {
                    await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                }

                if (cancel.IsCancellationRequested) return;

                bridge.Send(pcm);
                lastSound = DateTime.Now;

                // The number this phase exists to produce: the caller stopped
                // talking -> the first sound of the answer leaves for the line.
                // First one wins, since a turn's perceived latency is set by its
                // first audio, not its last.
                latency.RecordFirstAudio(turnClock.Elapsed);
            }
            finally
            {
                Interlocked.Add(ref speakingMs, cost.ElapsedMilliseconds);
            }
        }

        // --- tools ----------------------------------------------------------------

        /// <summary>Runs a turn's tool calls. True when the call is wrapping up.</summary>
        private async Task<bool> RunToolsAsync(CallTurnResult result)
        {
            bool wrappingUp = false;

            for (int i = 0; i < result.Calls.Count; i++)
            {
                ToolInvocation call = result.Calls[i];
                string id = result.CallIds[i];

                lock (gate)
                    history.Add(CallMessage.AssistantCall(id, call.Name, result.CallArgsJson[i]));

                string reply;
                try
                {
                    if (CallTools.IsHangUp(call.Name))
                    {
                        Console.WriteLine("[call] the assistant is wrapping the call up.");
                        reply = "{\"result\":\"the line is closing\"}";
                        wrappingUp = true;

                        // LET THE GOODBYE LAND FIRST.
                        //
                        // Measured upstream 2026-08-17: the model called hang_up
                        // and said "Goodbye Amin" in the same turn, the session was
                        // torn down the instant the tool came back, and the caller
                        // never heard it. It is worse here, not better: synthesis
                        // writes a whole sentence into a ten-second buffer in one
                        // go, so at the moment hang_up arrives the farewell has
                        // almost certainly not been played at all.
                        //
                        // Detached, so the rest of this loop still finishes.
                        _ = Task.Run(async () =>
                        {
                            await bridge.DrainOutboundAsync(GoodbyeGrace).ConfigureAwait(false);
                            finished.TrySetResult(CallEnding.Wrapped);
                        });
                    }
                    else if (CallTools.IsTakeMessage(call.Name))
                    {
                        reply = Json(TakeMessage(call.Arguments));
                    }
                    else
                    {
                        ToolResult ran = await tools.RunAsync(call.Name, call.Arguments)
                            .ConfigureAwait(false);
                        reply = Json(ran.ToResponse());
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[call] tool '{call.Name}' failed: {ex.Message}");
                    reply = Json(new Dictionary<string, string> { ["error"] = ex.Message });
                }

                lock (gate) history.Add(CallMessage.ToolReply(id, call.Name, reply));
            }

            return wrappingUp;
        }

        private static string Json(IReadOnlyDictionary<string, string> map)
        {
            var plain = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> kv in map) plain[kv.Key] = kv.Value;
            return JsonSerializer.Serialize(plain);
        }

        /// <summary>
        /// Writes down what the caller said, and hands back what is now on the
        /// notepad so it can be read to them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// THE RETURN VALUE IS THE POINT, not a receipt. A model asked to read a
        /// message back reads back what it MEANT to record, which confirms
        /// nothing — it will happily recite a message it never wrote down, and
        /// this class has three separate rules in the persona because it kept
        /// doing exactly that. Returning the stored text means the read-back is of
        /// the record itself, so a caller who says "yes, that's right" is agreeing
        /// to the thing Layth will actually receive.
        /// </para>
        /// <para>
        /// And a read-back is only worth asking for if the answer can change
        /// something, which is what <c>mode</c> is for. Appending is the default
        /// and the safe one — "oh, and tell him..." must not overwrite what came
        /// first — but appending a CORRECTION produces a message containing both
        /// the wrong version and the right one, which is worse than not asking.
        /// So replace exists, and it is the only path that can lose text: it is
        /// taken on an exact match and nothing else, because a misread mode that
        /// appends is untidy while a misread mode that replaces is a message
        /// destroyed.
        /// </para>
        /// </remarks>
        private IReadOnlyDictionary<string, string> TakeMessage(
            IReadOnlyDictionary<string, string> args)
        {
            args.TryGetValue("message", out string text);
            if (string.IsNullOrWhiteSpace(text))
            {
                return new Dictionary<string, string>
                {
                    ["error"] = "no message text was given — pass what the caller actually said."
                };
            }

            args.TryGetValue("mode", out string mode);
            bool replace = string.Equals(mode?.Trim(), "replace", StringComparison.OrdinalIgnoreCase);

            string before, now;
            lock (gate)
            {
                before = message;
                message = replace || string.IsNullOrWhiteSpace(message)
                    ? text.Trim()
                    : message + " " + text.Trim();
                now = message;
            }

            if (replace && !string.IsNullOrWhiteSpace(before))
                Console.WriteLine($"[call] message corrected, was: {before}");

            Console.WriteLine($"[call] message {(replace ? "corrected to" : "taken")}: {now}");

            return new Dictionary<string, string>
            {
                ["result"] = replace ? "corrected" : "written down",
                ["message_as_recorded"] = now,
                ["next"] =
                    "Read this back to the caller now, in one line, and ask whether it is right. " +
                    "If they change anything, call take_message again with mode \"replace\" and the " +
                    "whole corrected message."
            };
        }

        // --- endings --------------------------------------------------------------

        // The model layer failing twice running is the local equivalent of the
        // socket going away: LM Studio has been evicted, or is loading a model, or
        // is gone. One failure is a hiccup a caller repeats themselves through; two
        // is a call that cannot be held, and holding a stranger on a silent line
        // until the cap is worse than saying goodbye.
        private void NoteModelFailure()
        {
            if (Interlocked.Increment(ref modelFailures) < 2) return;
            Console.WriteLine("[call] the local model stack failed twice running — ending the call.");
            finished.TrySetResult(CallEnding.SessionLost);
        }

        // Six ways a call ends, and the ones that need watching for.
        private async Task WatchAsync(Func<bool> stillConnected, CancellationToken cancel)
        {
            DateTime deadline = DateTime.Now.Add(maxCall);
            bool nudged = false;
            DateTime nudgedAt = DateTime.MinValue;
            bool warnedWrapUp = false;

            // Consecutive reads that said the line was gone. NOT a bool, because
            // upstream one bad read used to end the call — see the check below.
            int gone = 0;

            // A heartbeat of what the inbound leg is actually carrying. Added
            // upstream after a first real call that produced a perfect log and no
            // conversation: nothing distinguished "the caller said nothing" from
            // "no audio reached us at all". `silent` counts frames the bridge
            // PADDED — if it tracks the total, no audio is arriving and the inbound
            // leg is dead, whatever the caller was doing.
            int tick = 0;
            long silentAtStart = bridge.SilenceFrames;

            using (cancel.Register(() => finished.TrySetResult(CallEnding.Cancelled)))
            {
                while (!finished.Task.IsCompleted)
                {
                    Task done = await Task.WhenAny(
                        finished.Task, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None))
                        .ConfigureAwait(false);
                    if (done == finished.Task) return;

                    if (++tick % 5 == 0)
                    {
                        long silent = bridge.SilenceFrames - silentAtStart;
                        long total = bridge.FramesEmitted;
                        Console.WriteLine(
                            $"[call] inbound level {bridge.InboundLevel:F4} gain {bridge.InboundGain:F1}x, " +
                            $"{total} frames ({silent} of them padded silence), " +
                            $"barge-ins {Volatile.Read(ref bargeIns)}, " +
                            $"dropped {Volatile.Read(ref dropped)}");
                    }

                    TimeSpan remaining = deadline - DateTime.Now;

                    // TELL IT THE CAP IS COMING, rather than guillotining it.
                    //
                    // The cap is enforced out here and the model has never been
                    // told it exists, so a call that reached it simply stopped —
                    // mid-word, with no goodbye and, worse, with whatever the
                    // caller was saying never written down. One warning turns "the
                    // line died" into "the assistant wrapped up", and gives the
                    // read-back a last chance to happen.
                    //
                    // Injected as a user-role note rather than sent down a socket:
                    // there is no channel here to speak into a turn that is not
                    // happening, and it is picked up on the caller's next sentence.
                    if (!warnedWrapUp && wrapUpAt > TimeSpan.Zero && remaining <= wrapUpAt)
                    {
                        warnedWrapUp = true;
                        Console.WriteLine(
                            $"[call] {remaining.TotalSeconds:F0}s of the cap left — asking it to wrap up.");
                        lock (gate)
                        {
                            history.Add(CallMessage.User(
                                $"(System note, not spoken by the caller: about " +
                                $"{remaining.TotalSeconds:F0} seconds of line time remain, and then the " +
                                "call ends automatically. Wrap up NOW. If they have told you anything " +
                                "for Layth, make sure take_message is holding it, read it back to them " +
                                "in one line, then say goodbye and call hang_up.)"));
                        }
                    }

                    if (remaining <= TimeSpan.Zero)
                    {
                        Console.WriteLine($"[call] the {maxCall.TotalSeconds:F0}s cap ran out.");
                        finished.TrySetResult(CallEnding.TimeCap);
                        return;
                    }

                    // The caller putting the phone down. Nothing on the audio path
                    // says so — a line that has gone dead sounds exactly like a
                    // caller who has stopped to think — so the transport is the only
                    // honest source.
                    if (stillConnected != null)
                    {
                        bool connected;
                        try { connected = stillConnected(); }
                        catch (Exception ex)
                        {
                            // Unknown is not "hung up": ending a live call because
                            // a DOM read threw would cut off a real conversation.
                            Console.WriteLine($"[call] could not read the call state: {ex.Message}");
                            connected = true;
                        }

                        // CONFIRMED, NOT ASSUMED.
                        //
                        // A single false used to end the call outright. But "the
                        // line is gone" is inferred from a DOM read against a page
                        // that re-renders under us, and every transient way that
                        // read can fail arrives here as exactly the same false a
                        // real hangup does. So calls died mid-sentence for no
                        // reason anybody could see afterwards, because this branch
                        // also logged NOTHING — the most common ending in the whole
                        // log was the one that left no trace at all.
                        //
                        // A caller who has really gone stays gone, so asking twice
                        // costs a genuine hangup two seconds and costs a blink
                        // nothing.
                        if (connected)
                        {
                            gone = 0;
                        }
                        else if (++gone >= disconnectChecks)
                        {
                            Console.WriteLine(
                                $"[call] the line read as gone {gone} checks running — the caller has left.");
                            finished.TrySetResult(CallEnding.CallerLeft);
                            return;
                        }
                        else
                        {
                            Console.WriteLine(
                                $"[call] the line read as gone ({gone} of {disconnectChecks}) — " +
                                "waiting for a second look before ending the call.");
                        }
                    }

                    TimeSpan silence = DateTime.Now - lastSound;
                    if (silence < silenceLimit) { nudged = false; continue; }

                    if (!nudged)
                    {
                        // One prompt, then give up. A caller who dialled and then
                        // walked away would otherwise hold the line open to the cap.
                        //
                        // `lastSound` is deliberately NOT bumped here. Upstream it
                        // was, and that made the very next tick see a fresh line,
                        // which cleared the flag and re-armed the nudge — so the
                        // assistant asked "are you still there?" on a loop and never
                        // once concluded the line was dead.
                        nudged = true;
                        nudgedAt = DateTime.Now;
                        Console.WriteLine($"[call] {silence.TotalSeconds:F0}s of silence — prompting once.");
                        await NudgeAsync(cancel).ConfigureAwait(false);
                        continue;
                    }

                    // Anything the caller says moves lastSound, which trips the
                    // branch above and clears the flag — so reaching here means a
                    // whole further window of nothing since the question was asked.
                    if (DateTime.Now - nudgedAt < silenceLimit) continue;

                    Console.WriteLine("[call] still nothing — treating the line as dead.");
                    finished.TrySetResult(CallEnding.Silence);
                    return;
                }
            }
        }

        // The nudge is SPOKEN DIRECTLY rather than asked of the model, which is the
        // other place a socket used to do the work. Upstream sent a system turn and
        // let the model phrase it; here that would cost a whole generate-and-
        // synthesise cycle to produce a sentence that is the same every time, on the
        // one path where the caller has already been waiting fifteen seconds.
        private async Task NudgeAsync(CancellationToken cancel)
        {
            const string ask = "Sorry, are you still there?";
            try
            {
                byte[] wav = await KokoroTTSService.SynthesizeWavAsync(ask, voice, cancel)
                    .ConfigureAwait(false);
                if (wav == null) return;

                bridge.Send(CallAudioBridge.ReadWavBytesAs16kMono(wav));
                Record(CallSpeaker.Assistant, ask);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[call] the nudge did not play: {ex.Message}");
            }
        }

        /// <summary>Ends the call from outside — end_call, or process teardown.</summary>
        public void Stop(CallEnding why = CallEnding.Cancelled) => finished.TrySetResult(why);

        private void Record(CallSpeaker speaker, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            CallTranscriptLine line;
            lock (gate)
            {
                line = new CallTranscriptLine(speaker, text, clock.Elapsed);
                transcript.Add(line);
                Console.WriteLine($"[call] {line}");
            }

            // Raised OUTSIDE the lock. Handlers are arbitrary code, and holding
            // `gate` across one would let a subscriber deadlock the audio path.
            try { LineRecorded?.Invoke(line); }
            catch (Exception ex) { Console.WriteLine($"[call] transcript listener threw: {ex.Message}"); }
        }

        public void Dispose()
        {
            Stop();
            try { turn?.Cancel(); } catch { }
            try { life?.Cancel(); } catch { }
            try { utterances.Dispose(); } catch { }
            try { turn?.Dispose(); } catch { }
            try { life?.Dispose(); } catch { }
        }
    }
}
