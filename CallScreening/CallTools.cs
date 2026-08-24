using Personal_Assistant.Dispatch;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Personal_Assistant.CallScreening
{
    /// <summary>
    /// Everything a person on the other end of a screened call is allowed to make
    /// the assistant do. It is a very short list, and that is the point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE CALLER IS AN UNTRUSTED SPEAKER. Every other surface in this app is
    /// driven by Layth, in his own house, at his own machine; this one is driven
    /// by whoever dialled the number. Handing a Live session
    /// <c>registry.ToolDefinitions</c> would put the lights, the PlayStation, the
    /// PC's power state, the browser and — worst — outbound SMS behind a stranger's
    /// voice. "Send a text to Mum saying I'll pay you tomorrow" must do NOTHING.
    /// </para>
    /// <para>
    /// So the allow-list is built by NAME and by hand. Two read-only tools are
    /// borrowed from the main registry, and two exist only for the duration of the
    /// call. Nothing outward-facing, nothing that touches the house, the PC, or
    /// SMS. Adding to <see cref="Borrowed"/> is a security decision, not a
    /// convenience one: the test for a candidate is "would I be happy for a cold
    /// caller to trigger this at 3am?", and for everything currently in the
    /// registry the answer is no.
    /// </para>
    /// <para>
    /// Fails CLOSED. A session handed <see cref="None"/> — which is what a
    /// misconfigured startup produces — can still take a message and hang up, and
    /// can do nothing else. It never degrades to "all tools" on any path.
    /// </para>
    /// </remarks>
    public sealed class CallTools
    {
        /// <summary>
        /// Tools taken from the main registry, by name. Read-only, local, and
        /// incapable of doing anything a stranger could exploit.
        /// </summary>
        public static readonly IReadOnlyList<string> Borrowed = new[] { "get_time", "get_date" };

        public const string TakeMessageTool = "take_message";
        public const string HangUpTool = "hang_up";

        /// <summary>Nothing borrowed — just the two call-local tools.</summary>
        /// <remarks>
        /// A property, not a static readonly field. As a field it ran BEFORE the
        /// two ToolDefinitions further down this file were assigned — static
        /// initialisers run in declaration order — so <c>None.Definitions</c> held
        /// two nulls and the first thing that read them threw. The same trap
        /// ToolResult.None documents (VoiceCommand.cs:85), hit the same way, and
        /// caught here only because the smoke asks what the fail-closed case
        /// actually offers.
        /// </remarks>
        public static CallTools None => new CallTools(
            new Dictionary<string, VoiceCommand>(StringComparer.OrdinalIgnoreCase), null);

        private readonly Dictionary<string, VoiceCommand> borrowed;
        private readonly CommandContext context;

        private CallTools(Dictionary<string, VoiceCommand> borrowed, CommandContext context)
        {
            this.borrowed = borrowed;
            this.context = context;

            var definitions = new List<ToolDefinition>();
            foreach (string name in Borrowed)
            {
                if (borrowed.TryGetValue(name, out VoiceCommand command)) definitions.Add(command.Tool);
            }
            definitions.Add(TakeMessageSchema());
            definitions.Add(HangUpSchema());
            Definitions = definitions;
        }

        /// <summary>The schemas the call session sends, and the only ones it sends.</summary>
        public IReadOnlyList<ToolDefinition> Definitions { get; }

        /// <summary>
        /// Picks the allow-listed tools out of the real registry.
        /// </summary>
        /// <remarks>
        /// Takes the registry rather than being handed a list, so the borrowed
        /// tools stay the same objects the assistant itself runs — a copy would
        /// drift, and a drifted copy of get_time is a call telling somebody the
        /// wrong time. A name that is not in the registry is logged and skipped
        /// rather than throwing: a renamed tool should cost the call one
        /// capability, not the whole feature.
        /// </remarks>
        public static CallTools From(ToolRegistry registry, CommandContext context)
        {
            if (registry == null) return None;

            var found = new Dictionary<string, VoiceCommand>(StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();

            foreach (string name in Borrowed)
            {
                VoiceCommand command = registry.FindByName(name);
                if (command == null) missing.Add(name);
                else found[name] = command;
            }

            if (missing.Count > 0)
            {
                Console.WriteLine(
                    $"[call] these call-safe tools are not in the registry and will not be " +
                    $"offered to callers: {string.Join(", ", missing)}");
            }

            return new CallTools(found, context);
        }

        public static bool IsHangUp(string name) =>
            string.Equals(name, HangUpTool, StringComparison.OrdinalIgnoreCase);

        public static bool IsTakeMessage(string name) =>
            string.Equals(name, TakeMessageTool, StringComparison.OrdinalIgnoreCase);

        public string Describe() =>
            Definitions.Count == 0 ? "(none)" : string.Join(", ", Definitions.Select(d => d.Name));

        /// <summary>
        /// Runs a borrowed tool. Anything not on the list is refused out loud.
        /// </summary>
        /// <remarks>
        /// The refusal is the backstop, not the barrier — the barrier is that the
        /// model was never shown these tools. Reaching this line means the model
        /// invented a name, and on a call that is worth a console line, because it
        /// is also what an attempt to talk the assistant into something looks like
        /// from in here.
        /// </remarks>
        public async Task<ToolResult> RunAsync(string name, IReadOnlyDictionary<string, string> args)
        {
            if (!borrowed.TryGetValue(name ?? string.Empty, out VoiceCommand command))
            {
                Console.WriteLine($"[call] REFUSED tool '{name}' — not on the call allow-list.");
                return ToolResult.Failed(
                    "I can't do that from a call.",
                    $"'{name}' is not available to a caller");
            }

            ToolResult result = await command.Handler(
                context ?? new CommandContext(), args ?? VoiceCommand.EmptyArgs).ConfigureAwait(false);
            return result ?? ToolResult.None;
        }

        // --- the two tools that only exist during a call -------------------------

        // Methods rather than static fields, so no amount of moving lines about
        // can reintroduce the initialisation-order bug None's remarks describe.
        //
        // take_message is deliberately the ONLY way anything a caller says leaves
        // the call. There is no "text this to Layth" and no "put it in his
        // calendar": the message goes to the call log and he reads it when he gets
        // back.
        private static ToolDefinition TakeMessageSchema() => ToolDefinition.Create(
            TakeMessageTool,
            "Write down what the caller wants Layth to know. Call this once you have " +
            "their name and their reason for calling, and again if they add something. " +
            "It is the only way anything from this call reaches him, so record it in " +
            "their words rather than summarising it away.",
            new ToolParameter("message", "string",
                "The message, including who is calling and what they want. Write it as " +
                "you would leave it on a notepad: \"Sarah from the garage — the car is " +
                "ready, collect before 6.\""));

        // The model's own exit. Everything else that ends a call is something
        // happening TO it — the caller leaving, the cap, a dead socket.
        private static ToolDefinition HangUpSchema() => ToolDefinition.Create(
            HangUpTool,
            "End the call. Use it once the conversation is genuinely finished — you have " +
            "taken a message and said goodbye, or the caller has rung off. Say your " +
            "goodbye BEFORE calling this: the line closes immediately and anything you " +
            "were about to say is lost.");
    }
}
