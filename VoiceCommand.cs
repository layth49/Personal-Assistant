using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Personal_Assistant.Arduino;
using Personal_Assistant.Geolocator;
using Personal_Assistant.LightAutomator;
using Personal_Assistant.PlaystationController;
using Personal_Assistant.SMSController;
using Personal_Assistant.SpeechManager;
using Personal_Assistant.WeatherService;
using Microsoft.CognitiveServices.Speech;

namespace Personal_Assistant.Dispatch
{
    // Describes one parameter a command expects, mirroring an OpenAI / Gemini
    // function-calling parameter schema. AllowedValues, when non-null, becomes the
    // JSON-Schema `enum` and is also what the dispatcher validates against before
    // any handler runs (e.g. room must be "LED" or "bedroom").
    public sealed record ToolParameter(
        string Name,
        string Type,
        string Description,
        bool Required = true,
        IReadOnlyList<string> AllowedValues = null);

    // The schema for a single callable tool. Serialised into both the Gemini
    // `function_declarations` block (main branch) and the OpenAI `tools` array
    // (local branch), so it is deliberately provider-agnostic.
    public sealed record ToolDefinition(
        string Name,
        string Description,
        IReadOnlyList<ToolParameter> Parameters)
    {
        public static ToolDefinition Create(
            string name,
            string description,
            params ToolParameter[] parameters) =>
            new ToolDefinition(name, description, parameters ?? Array.Empty<ToolParameter>());
    }

    // Shared dependencies a command handler may need. Built once in Program.Main
    // and handed to every handler so the existing service instances are reused
    // rather than reconstructed per call.
    public sealed class CommandContext
    {
        public SpeechService Speech { get; init; }
        public LightControl Lights { get; init; }
        public PlaystationControl Playstation { get; init; }
        public SMSControl Sms { get; init; }
        public ArduinoService Arduino { get; init; }
        public GetWeather Weather { get; init; }
        public GetLocation Location { get; init; }
        public IReadOnlyDictionary<string, string> Contacts { get; init; }
        public string IpAddressPlug { get; init; }
        public string IpAddressSwitch { get; init; }

        // The raw text the user actually said for this turn. Handlers pass it to
        // SpeechService.Say so the on-screen bubble shows what was heard.
        public string RecognizedText { get; set; }
    }

    // A single voice command. It carries both:
    //   * the LLM-facing schema (Tool) used for intent dispatch, and
    //   * the legacy keyword path (Matches + ExtractArgs) used as a fallback when
    //     the LLM is unavailable / malformed — preserving the original
    //     "first match wins, fall through to AI" behaviour.
    // Handlers themselves are unchanged logic, just wrapped here.
    public sealed class VoiceCommand
    {
        // Tool name; must equal Tool.Name and is what the LLM returns.
        public string Name { get; }

        // LLM schema for this command. Never null — every command is exposed as a tool.
        public ToolDefinition Tool { get; }

        // Keyword-fallback predicate over the lowercased recognised text.
        public Func<string, bool> Matches { get; }

        // Extracts handler arguments from the raw text on the keyword-fallback
        // path (e.g. the search query after "search up"). Returns an empty map
        // for parameterless commands.
        public Func<string, IReadOnlyDictionary<string, string>> ExtractArgs { get; }

        // Executes the command. Args are already validated against Tool.Parameters.
        public Func<CommandContext, IReadOnlyDictionary<string, string>, Task> Handler { get; }

        public VoiceCommand(
            ToolDefinition tool,
            Func<string, bool> matches,
            Func<CommandContext, IReadOnlyDictionary<string, string>, Task> handler,
            Func<string, IReadOnlyDictionary<string, string>> extractArgs = null)
        {
            Tool = tool ?? throw new ArgumentNullException(nameof(tool));
            Name = tool.Name;
            Matches = matches ?? throw new ArgumentNullException(nameof(matches));
            Handler = handler ?? throw new ArgumentNullException(nameof(handler));
            ExtractArgs = extractArgs ?? (_ => EmptyArgs);
        }

        public static readonly IReadOnlyDictionary<string, string> EmptyArgs =
            new Dictionary<string, string>();
    }
}
