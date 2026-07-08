using System;
using System.Collections.Generic;
using System.Linq;

namespace Personal_Assistant.Dispatch
{
    // Holds every VoiceCommand and provides the two lookups the dispatcher needs:
    //   * by tool name      -> for LLM-chosen tool calls
    //   * by keyword match  -> for the legacy fallback path
    // Registration order is preserved so keyword matching stays "first match wins",
    // exactly like the original if/else chain.
    public sealed class ToolRegistry
    {
        private readonly List<VoiceCommand> commands = new List<VoiceCommand>();
        private readonly Dictionary<string, VoiceCommand> byName =
            new Dictionary<string, VoiceCommand>(StringComparer.OrdinalIgnoreCase);

        public ToolRegistry Add(VoiceCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (byName.ContainsKey(command.Name))
                throw new ArgumentException($"Duplicate tool name: {command.Name}");

            commands.Add(command);
            byName[command.Name] = command;
            return this; // fluent so Program can chain .Add(...).Add(...)
        }

        // The schemas handed to the LLM.
        public IReadOnlyList<ToolDefinition> ToolDefinitions =>
            commands.Select(c => c.Tool).ToList();

        public VoiceCommand FindByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return byName.TryGetValue(name, out var cmd) ? cmd : null;
        }

        // First command whose keyword predicate matches the lowercased text, or
        // null if none — used only on the fallback path.
        public VoiceCommand MatchKeyword(string lowerText)
        {
            if (string.IsNullOrEmpty(lowerText)) return null;
            return commands.FirstOrDefault(c => c.Matches(lowerText));
        }
    }
}
