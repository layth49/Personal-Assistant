using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Personal_Assistant.Dispatch
{
    // The outcome of asking the LLM what to do with a turn of user input.
    // Exactly one of three states:
    //   ToolCall  - the model picked a tool and gave arguments
    //   Reply     - the model answered conversationally (no tool)
    //   Failure   - the call timed out / errored / returned malformed JSON
    // Failure is what triggers the keyword-matching fallback.
    public sealed class LlmDecision
    {
        public bool IsToolCall { get; private set; }
        public bool Failed { get; private set; }
        public string ToolName { get; private set; }
        public IReadOnlyDictionary<string, string> Arguments { get; private set; }
        public string Text { get; private set; }

        public static LlmDecision ToolCall(string name, IReadOnlyDictionary<string, string> args) =>
            new LlmDecision
            {
                IsToolCall = true,
                ToolName = name,
                Arguments = args ?? VoiceCommand.EmptyArgs
            };

        public static LlmDecision Reply(string text) =>
            new LlmDecision { Text = text ?? string.Empty };

        public static LlmDecision Failure() =>
            new LlmDecision { Failed = true };
    }

    // Asks the LLM to map input -> tool call or conversational reply, given the
    // available tool schemas. Implemented per branch (Gemini on main, LM Studio
    // on local) and injected, so this dispatcher stays provider-agnostic.
    public delegate Task<LlmDecision> ToolDetector(string input, IReadOnlyList<ToolDefinition> tools);

    // Produces a plain conversational answer (the existing grounded chat path).
    public delegate Task<string> Conversationalist(string input);

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

        public IntentDispatcher(
            ToolRegistry registry,
            CommandContext context,
            ToolDetector detector,
            Conversationalist conversationalist)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.detector = detector ?? throw new ArgumentNullException(nameof(detector));
            this.conversationalist = conversationalist ?? throw new ArgumentNullException(nameof(conversationalist));
        }

        public async Task DispatchAsync(string userInput)
        {
            if (string.IsNullOrWhiteSpace(userInput)) return;

            context.RecognizedText = userInput;
            string lower = userInput.ToLower();

            LlmDecision decision;
            try
            {
                decision = await detector(userInput, registry.ToolDefinitions)
                           ?? LlmDecision.Failure();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[dispatch] detector threw, falling back to keywords: {ex.Message}");
                decision = LlmDecision.Failure();
            }

            // Malformed / timeout / error -> legacy keyword matcher.
            if (decision.Failed)
            {
                Console.WriteLine("[dispatch] LLM unavailable/malformed -> keyword fallback");
                await FallbackAsync(userInput, lower);
                return;
            }

            // The LLM chose a tool.
            if (decision.IsToolCall)
            {
                var command = registry.FindByName(decision.ToolName);
                if (command == null)
                {
                    Console.WriteLine($"[dispatch] LLM picked unknown tool '{decision.ToolName}' -> fallback");
                    await FallbackAsync(userInput, lower);
                    return;
                }

                if (!TryValidate(command.Tool, decision.Arguments, out var cleanArgs, out string error))
                {
                    Console.WriteLine($"[dispatch] invalid args for '{command.Name}': {error} -> fallback");
                    await FallbackAsync(userInput, lower);
                    return;
                }

                Console.WriteLine($"[dispatch] tool '{command.Name}' args=[{Describe(cleanArgs)}]");
                await command.Handler(context, cleanArgs);
                return;
            }

            // The LLM chose no tool. Fall through to the grounded conversational
            // path (which keeps Google Search grounding) rather than speaking the
            // classifier's ungrounded text — preserving the app's original
            // answer quality for chit-chat and current-events questions.
            await ConversationalAsync(userInput);
        }

        // Legacy keyword path: first matching command wins, else conversational AI.
        private async Task FallbackAsync(string userInput, string lower)
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
                    return;
                }
                Console.WriteLine($"[dispatch] keyword match '{command.Name}' had invalid args: {error}");
            }

            await ConversationalAsync(userInput);
        }

        private async Task ConversationalAsync(string userInput)
        {
            string response = await conversationalist(userInput);
            await context.Speech.Say(userInput, response);
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
