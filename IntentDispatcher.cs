using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Personal_Assistant.Diagnostics;

namespace Personal_Assistant.Dispatch
{
    // A single tool the model chose, with its extracted arguments. A decision can
    // carry several of these when the user asked for multiple things at once
    // ("turn off the lights and close the door").
    public sealed class ToolInvocation
    {
        public string Name { get; }
        public IReadOnlyDictionary<string, string> Arguments { get; }

        public ToolInvocation(string name, IReadOnlyDictionary<string, string> args)
        {
            Name = name;
            Arguments = args ?? VoiceCommand.EmptyArgs;
        }
    }

    // The outcome of asking the LLM what to do with a turn of user input.
    // Exactly one of three states:
    //   ToolCall(s) - the model picked one or more tools and gave arguments
    //   Reply       - the model answered conversationally (no tool)
    //   Failure     - the call timed out / errored / returned malformed JSON
    // Failure is what triggers the keyword-matching fallback.
    public sealed class LlmDecision
    {
        public bool Failed { get; private set; }
        public IReadOnlyList<ToolInvocation> ToolCalls { get; private set; }
        public string Text { get; private set; }

        public bool IsToolCall => ToolCalls != null && ToolCalls.Count > 0;

        // Convenience for the common single-tool case.
        public static LlmDecision ToolCall(string name, IReadOnlyDictionary<string, string> args) =>
            new LlmDecision { ToolCalls = new[] { new ToolInvocation(name, args) } };

        public static LlmDecision Tools(IReadOnlyList<ToolInvocation> calls) =>
            new LlmDecision { ToolCalls = calls };

        public static LlmDecision Reply(string text) =>
            new LlmDecision { Text = text ?? string.Empty };

        public static LlmDecision Failure() =>
            new LlmDecision { Failed = true };
    }

    // Asks the LLM to map input -> tool call or conversational reply, given the
    // available tool schemas and prior conversation turns. Implemented per branch
    // (Gemini on main, LM Studio on local) and injected, so this dispatcher stays
    // provider-agnostic.
    public delegate Task<LlmDecision> ToolDetector(
        string input,
        IReadOnlyList<ToolDefinition> tools,
        IReadOnlyList<ConversationTurn> history);

    // Produces a plain conversational answer (the existing grounded chat path),
    // given prior conversation turns for context.
    public delegate Task<string> Conversationalist(string input, IReadOnlyList<ConversationTurn> history);

    // LLM-first intent dispatch.
    //
    // Flow for a turn of recognised text:
    //   1. Ask the LLM (with the tool list) what to do.
    //   2. If it picked a tool: validate the extracted args, then run the
    //      matching handler. Invalid/unknown -> treat as a miss (step 4).
    //   3. If it replied conversationally: speak that answer.
    //   4. Hybrid fallback (only on LLM failure/timeout/malformed, or a miss):
    //      run the legacy keyword matcher; if nothing matches, fall through to
    //      the conversational AI response.
    public sealed class IntentDispatcher
    {
        private readonly ToolRegistry registry;
        private readonly CommandContext context;
        private readonly ToolDetector detector;
        private readonly Conversationalist conversationalist;
        private readonly ConversationMemory memory;
        private readonly LatencyTracker latency;

        public IntentDispatcher(
            ToolRegistry registry,
            CommandContext context,
            ToolDetector detector,
            Conversationalist conversationalist,
            ConversationMemory memory = null,
            LatencyTracker latency = null)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.detector = detector ?? throw new ArgumentNullException(nameof(detector));
            this.conversationalist = conversationalist ?? throw new ArgumentNullException(nameof(conversationalist));
            this.memory = memory ?? new ConversationMemory();
            this.latency = latency;
        }

        // Returns true if the assistant's spoken reply was interrupted by the
        // wakeword (barge-in), so the caller can listen again without requiring
        // the wakeword. Tool actions and their short confirmations aren't
        // interruptible, so those paths return false.
        public async Task<bool> DispatchAsync(string userInput)
        {
            if (string.IsNullOrWhiteSpace(userInput)) return false;

            context.RecognizedText = userInput;
            string lower = userInput.ToLower();

            // Snapshot BEFORE recording this turn's input, so history never
            // includes the very input it's meant to give context for.
            var history = memory.Snapshot();
            memory.AddUser(userInput);

            LlmDecision decision;
            var detectSw = Stopwatch.StartNew();
            try
            {
                decision = await detector(userInput, registry.ToolDefinitions, history)
                           ?? LlmDecision.Failure();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[dispatch] detector threw, falling back to keywords: {ex.Message}");
                decision = LlmDecision.Failure();
            }
            finally
            {
                latency?.RecordLlm(detectSw.Elapsed);
            }

            // Malformed / timeout / error -> legacy keyword matcher.
            if (decision.Failed)
            {
                Console.WriteLine("[dispatch] LLM unavailable/malformed -> keyword fallback");
                return await FallbackAsync(userInput, lower, history);
            }

            // The LLM chose one or more tools.
            if (decision.IsToolCall)
            {
                return await RunToolCallsAsync(decision.ToolCalls, userInput, lower, history);
            }

            // The LLM chose no tool. Fall through to the grounded conversational
            // path (which keeps Google Search grounding) rather than speaking the
            // classifier's ungrounded text — preserving the app's original
            // answer quality for chit-chat and current-events questions.
            return await ConversationalAsync(userInput, history);
        }

        // Runs each tool the model asked for, in order (compound requests like
        // "turn off the lights and close the door" arrive as several calls).
        // Unknown/invalid calls are skipped rather than aborting the batch. If
        // NOTHING ran — a lone bogus call — we fall back to keyword matching,
        // preserving the single-tool miss behaviour.
        private async Task<bool> RunToolCallsAsync(
            IReadOnlyList<ToolInvocation> calls,
            string userInput,
            string lower,
            IReadOnlyList<ConversationTurn> history)
        {
            int ran = 0;
            foreach (var call in calls)
            {
                var command = registry.FindByName(call.Name);
                if (command == null)
                {
                    Console.WriteLine($"[dispatch] LLM picked unknown tool '{call.Name}' -> skip");
                    continue;
                }
                if (!TryValidate(command.Tool, call.Arguments, out var cleanArgs, out string error))
                {
                    Console.WriteLine($"[dispatch] invalid args for '{command.Name}': {error} -> skip");
                    continue;
                }

                Console.WriteLine($"[dispatch] tool '{command.Name}' args=[{Describe(cleanArgs)}]");
                await command.Handler(context, cleanArgs);
                if (!command.Ephemeral) memory.AddToolCall(command.Name, cleanArgs);
                ran++;
            }

            if (ran == 0) return await FallbackAsync(userInput, lower, history);
            return false;
        }

        // Runs a single tool by name with the given args, validated — used by the
        // `repeat` tool to execute the actions it loops over. Does not touch
        // conversation memory (the top-level call already records what ran).
        public async Task RunToolByNameAsync(string name, IReadOnlyDictionary<string, string> args)
        {
            var command = registry.FindByName(name);
            if (command == null)
            {
                Console.WriteLine($"[dispatch] RunTool: unknown tool '{name}'");
                return;
            }
            if (!TryValidate(command.Tool, args, out var cleanArgs, out string error))
            {
                Console.WriteLine($"[dispatch] RunTool: invalid args for '{name}': {error}");
                return;
            }
            Console.WriteLine($"[dispatch] RunTool '{name}' args=[{Describe(cleanArgs)}]");
            await command.Handler(context, cleanArgs);
        }

        // Legacy keyword path: first matching command wins, else conversational AI.
        private async Task<bool> FallbackAsync(string userInput, string lower, IReadOnlyList<ConversationTurn> history)
        {
            var command = registry.MatchKeyword(lower);
            if (command != null)
            {
                var args = command.ExtractArgs(userInput);
                // Validate even on the keyword path so handlers get clean inputs.
                if (TryValidate(command.Tool, args, out var cleanArgs, out string error))
                {
                    Console.WriteLine($"[dispatch] keyword match '{command.Name}' args=[{Describe(cleanArgs)}]");
                    await command.Handler(context, cleanArgs);
                    memory.AddToolCall(command.Name, cleanArgs);
                    return false;
                }
                Console.WriteLine($"[dispatch] keyword match '{command.Name}' had invalid args: {error}");
            }

            return await ConversationalAsync(userInput, history);
        }

        // Speaks the AI answer interruptibly — long replies are exactly when the
        // user wants to be able to barge in with the wakeword.
        private async Task<bool> ConversationalAsync(string userInput, IReadOnlyList<ConversationTurn> history)
        {
            var sw = Stopwatch.StartNew();
            string response = await conversationalist(userInput, history);
            latency?.RecordLlm(sw.Elapsed);
            memory.AddModel(response);
            return await context.Speech.SayInterruptible(userInput, response);
        }

        // Validates raw args against a tool's parameter schema: required params must
        // be present and non-empty, and any param with AllowedValues must match one
        // (case-insensitively, canonicalised to the declared casing). Unknown args
        // are dropped. This is the guard the task requires before handlers run.
        private static bool TryValidate(
            ToolDefinition tool,
            IReadOnlyDictionary<string, string> raw,
            out IReadOnlyDictionary<string, string> clean,
            out string error)
        {
            // Case-insensitive view of whatever the LLM (or extractor) produced.
            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (raw != null)
            {
                foreach (var kv in raw) lookup[kv.Key] = kv.Value;
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in tool.Parameters)
            {
                lookup.TryGetValue(p.Name, out string value);
                value = value?.Trim();

                if (string.IsNullOrWhiteSpace(value))
                {
                    if (p.Required)
                    {
                        clean = null;
                        error = $"missing required parameter '{p.Name}'";
                        return false;
                    }
                    continue; // optional + absent -> skip
                }

                if (p.AllowedValues != null && p.AllowedValues.Count > 0)
                {
                    string canonical = null;
                    foreach (var allowed in p.AllowedValues)
                    {
                        if (string.Equals(allowed, value, StringComparison.OrdinalIgnoreCase))
                        {
                            canonical = allowed;
                            break;
                        }
                    }
                    if (canonical == null)
                    {
                        clean = null;
                        error = $"'{value}' is not a valid value for '{p.Name}'";
                        return false;
                    }
                    value = canonical;
                }

                result[p.Name] = value;
            }

            clean = result;
            error = null;
            return true;
        }

        private static string Describe(IReadOnlyDictionary<string, string> args)
        {
            if (args == null || args.Count == 0) return "";
            return string.Join(", ", System.Linq.Enumerable.Select(args, kv => $"{kv.Key}={kv.Value}"));
        }
    }
}
