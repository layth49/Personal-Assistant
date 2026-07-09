using System.Collections.Generic;

namespace Personal_Assistant.Dispatch
{
    // One entry of conversation history. Role is "user" or "model" for spoken
    // turns; "tool" marks a tool the assistant executed, carried STRUCTURALLY
    // (name + args) rather than as text. Providers render tool entries into the
    // model's native function-calling format (OpenAI tool_calls + tool result /
    // Gemini functionCall + functionResponse). This matters because any *text*
    // stand-in for a tool call gets imitated by small models — as a fake tool
    // call if it looks tool-shaped, or as a no-op reply if it looks like a plain
    // acknowledgment. The native format is what the model is trained on, so it
    // pattern-matches prior calls correctly for follow-ups ("turn it back off").
    public sealed class ConversationTurn
    {
        public string Role { get; }
        public string Text { get; }
        public string ToolName { get; }
        public IReadOnlyDictionary<string, string> ToolArgs { get; }

        public ConversationTurn(string role, string text)
        {
            Role = role;
            Text = text;
        }

        public ConversationTurn(string toolName, IReadOnlyDictionary<string, string> toolArgs)
        {
            Role = "tool";
            ToolName = toolName;
            ToolArgs = toolArgs ?? new Dictionary<string, string>();
        }

        public bool IsTool => Role == "tool";
    }

    // Rolling in-memory conversation history shared across turns within a single
    // run of the app. Not persisted — cleared on restart. Fed into the
    // tool-detection and conversational calls so follow-ups like "what about
    // tomorrow?" or "turn it back off" resolve against prior context.
    public sealed class ConversationMemory
    {
        // Capped in entries (user/model/tool), not tokens — keeps the request
        // small and recent-context-only rather than growing unbounded.
        private const int MaxTurns = 16;

        private readonly LinkedList<ConversationTurn> turns = new LinkedList<ConversationTurn>();

        public void AddUser(string text) => AddText("user", text);

        public void AddModel(string text) => AddText("model", text);

        // Records an executed tool as a structured prior call. Rendered by each
        // provider as a native function call + result, which both preserves
        // strict user/assistant alternation and gives the model a real example to
        // follow — without any imitable text in the assistant role.
        public void AddToolCall(string toolName, IReadOnlyDictionary<string, string> args)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return;
            // A single entry renders to BOTH the call and its result, so trimming
            // can never orphan a tool result from its call.
            turns.AddLast(new ConversationTurn(toolName, args));
            Trim();
        }

        private void AddText(string role, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            turns.AddLast(new ConversationTurn(role, text));
            Trim();
        }

        private void Trim()
        {
            while (turns.Count > MaxTurns) turns.RemoveFirst();
        }

        // Snapshot taken BEFORE the current turn's user input is recorded, so
        // callers can pass "everything before this" as history and append the
        // current input as the final entry themselves.
        public IReadOnlyList<ConversationTurn> Snapshot() => new List<ConversationTurn>(turns);

        public void Clear() => turns.Clear();
    }
}
