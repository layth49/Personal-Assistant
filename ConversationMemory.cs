using System.Collections.Generic;

namespace Personal_Assistant.Dispatch
{
    // One entry of conversation history. Role is "user" or "model" for spoken
    // turns; "tool" marks an action the assistant executed (e.g. a light toggle).
    // Tool entries are deliberately NOT rendered as assistant messages — small
    // models imitate any tool-ish text they see in the assistant role and start
    // emitting fake tool calls as prose. Providers surface tool entries as a
    // system-prompt context note instead. See ConversationMemory.
    public sealed class ConversationTurn
    {
        public string Role { get; }
        public string Text { get; }

        public ConversationTurn(string role, string text)
        {
            Role = role;
            Text = text;
        }

        public bool IsTool => Role == "tool";
    }

    // Rolling in-memory conversation history shared across turns within a single
    // run of the app. Not persisted — cleared on restart. Fed into the
    // tool-detection and conversational calls so follow-ups like "what about
    // tomorrow?" or "send them that instead" resolve against prior context.
    public sealed class ConversationMemory
    {
        // Capped in entries (one user/model/tool entry each), not tokens — keeps
        // the request small and recent-context-only rather than growing unbounded
        // over a long session.
        private const int MaxTurns = 12;

        private readonly LinkedList<ConversationTurn> turns = new LinkedList<ConversationTurn>();

        public void AddUser(string text) => Add("user", text);

        public void AddModel(string text) => Add("model", text);

        // Records that the assistant executed a tool, as a short factual phrase
        // (e.g. "control_lights (state=on, room=bedroom)"). Kept out of the
        // assistant message stream to avoid teaching the model to fake tool calls;
        // providers fold these into a system-prompt "actions already performed"
        // note purely for follow-up context.
        public void AddToolAction(string description) => Add("tool", description);

        private void Add(string role, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            turns.AddLast(new ConversationTurn(role, text));
            while (turns.Count > MaxTurns) turns.RemoveFirst();
        }

        // Snapshot taken BEFORE the current turn's user input is recorded, so
        // callers can pass "everything before this" as history and append the
        // current input as the final content entry themselves.
        public IReadOnlyList<ConversationTurn> Snapshot() => new List<ConversationTurn>(turns);

        public void Clear() => turns.Clear();
    }
}
