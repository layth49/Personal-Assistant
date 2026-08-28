using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Personal_Assistant.AppLaunching;
using Personal_Assistant.Arduino;
using Personal_Assistant.AudioControl;
using Personal_Assistant.Geolocator;
using Personal_Assistant.LightAutomator;
using Personal_Assistant.MediaControl;
using Personal_Assistant.PlaystationController;
using Personal_Assistant.ProcessControl;
using Personal_Assistant.Reminders;
using Personal_Assistant.ScreenCapture;
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

    // What a handler produces: the sentence to voice, plus whatever structured
    // facts that sentence was built from.
    //
    // Handlers used to speak for themselves. That worked only because the
    // turn-based path was the only caller — the moment a model is also holding
    // the conversation, a handler that swallows its own answer leaves the model
    // nothing to speak FROM. It doesn't fall silent; it invents. A smoke run on
    // main had the handler compute 2:20 AM local while the model announced
    // "It's 7:20 AM UTC", because the tool result it received was
    // {"result":"done"} with no payload.
    //
    // So a result carries both, for two consumers that need different things:
    //
    //   Speech — the finished sentence. IntentDispatcher speaks this on the
    //            turn-based path, which is what keeps that path sounding exactly
    //            as it did before.
    //   Data   — the facts, for a model that has to answer FROM a tool rather
    //            than relay it, and can then take a follow-up like "and in
    //            Celsius?" without calling the tool again.
    //
    // Pure actions return None: nothing to say, nothing to report.
    public sealed class ToolResult
    {
        public static readonly ToolResult None = new ToolResult(null, null);

        // The sentence to say, or null for silence.
        public string Speech { get; }

        // Structured facts behind the sentence. Never null.
        public IReadOnlyDictionary<string, string> Data { get; }

        // Optional SSML to synthesise INSTEAD of Speech. Kept so that handler
        // code written on main drops in here unedited — but NOTHING ON THIS
        // BRANCH READS IT. Kokoro takes plain text: no SSML, no <phoneme>, no
        // IPA, so the local dispatcher deliberately ignores this field and
        // speaks Speech. Pronunciation control here is spelled-out
        // transliteration instead — see PrayerTimesCalculator.PrayerSpoken and
        // the editing notes in CLAUDE.md. If you want SSML, reword Speech.
        public string Ssml { get; }

        private ToolResult(string speech, IReadOnlyDictionary<string, string> data, string ssml = null)
        {
            Speech = speech;
            // Allocated, never read from a shared static empty instance. A shared
            // one declared after None left None.Data null — static initialisers
            // run in declaration order, so None's constructor read the field
            // before it was assigned. An allocation here cannot be got wrong by
            // moving a line.
            Data = data ?? new Dictionary<string, string>();
            Ssml = ssml;
        }

        /// <summary>A spoken answer.</summary>
        public static ToolResult Speak(string speech) => new ToolResult(speech, null);

        /// <summary>A spoken answer whose pronunciation needs SSML. `speech` is the
        /// plain-text equivalent, and on this branch it is the only half anyone
        /// ever hears — see Ssml.</summary>
        public static ToolResult SpeakSsml(string speech, string ssml) =>
            new ToolResult(speech, null, ssml);

        /// <summary>An action that succeeded with nothing to announce.</summary>
        public static ToolResult Done() => None;

        /// <summary>Something went wrong: said out loud, and reported to the model.</summary>
        public static ToolResult Failed(string speech, string reason = null) =>
            new ToolResult(speech, new Dictionary<string, string>
            {
                ["error"] = reason ?? speech ?? "failed"
            });

        // Attaches one fact. Chained off Speak, so a handler reads as the sentence
        // it says followed by what that sentence was built from.
        public ToolResult With(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return this;
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kv in Data) merged[kv.Key] = kv.Value;
            merged[key] = value ?? string.Empty;
            return new ToolResult(Speech, merged, Ssml);
        }

        // What the model receives. The sentence rides along under `speech` so the
        // model can simply relay a well-formed answer, while Data is there for
        // when it needs to reason rather than repeat.
        public IReadOnlyDictionary<string, string> ToResponse()
        {
            var response = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kv in Data) response[kv.Key] = kv.Value;
            if (!string.IsNullOrWhiteSpace(Speech)) response["speech"] = Speech;
            if (response.Count == 0) response["result"] = "done";
            return response;
        }
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
        public AudioController Audio { get; init; }
        public ScreenshotService Screenshot { get; init; }
        public ProcessController Processes { get; init; }
        public AppLauncher Apps { get; init; }
        public MediaController Media { get; init; }
        public NowPlayingReader NowPlaying { get; init; }
        public ReminderService Reminders { get; init; }
        public Personal_Assistant.ClipboardControl.ClipboardController Clipboard { get; init; }
        public Personal_Assistant.FileFinding.FileFinder Files { get; init; }
        public Personal_Assistant.WindowControl.WindowController Windows { get; init; }
        public Personal_Assistant.Notes.NotesService Notes { get; init; }

        // Standing rules the user created by voice. Null when the trigger engine
        // isn't wired, and BuildRegistry then simply doesn't offer
        // set_trigger/list_triggers/cancel_trigger.
        public Personal_Assistant.Triggers.VoiceTriggers VoiceTriggers { get; init; }

        // Things the assistant has offered on its own initiative. Null when
        // suggestions are off or in a harness.
        public Personal_Assistant.Suggestions.SuggestionService Suggestions { get; init; }

        // Real-world events the user is waiting on. Null in a harness, and the
        // reminder-listing tools simply say nothing about watches when it is.
        public Personal_Assistant.Events.EventWatchService Watches { get; init; }

        // Answering the phone. Null whenever CallScreening is off in App.config —
        // which is the default — and BuildRegistry then does not offer
        // screen_calls/end_call/list_calls AT ALL, rather than offering tools that
        // refuse politely. A tool the model can see is a tool it will reach for,
        // and "call screening is disabled in App.config" is not an answer anyone
        // wants spoken back to them mid-sentence.
        public Personal_Assistant.CallScreening.CallScreeningService CallScreening { get; init; }

        // Reads the battery. Defaulted rather than injected because get_battery
        // and the battery_below rules both want one and neither cares which.
        public Personal_Assistant.Power.BatteryReader Battery { get; init; }
            = new Personal_Assistant.Power.BatteryReader();

        public IReadOnlyDictionary<string, string> Contacts { get; init; }
        public string IpAddressPlug { get; init; }
        public string IpAddressSwitch { get; init; }

        // Runs another tool by name (validated) — how the `repeat` tool executes
        // the actions it loops over. Wired from the dispatcher after construction.
        // Voices whatever the tool returns, because on the turn-based path the
        // repeated actions are the only thing the user hears.
        public Func<string, IReadOnlyDictionary<string, string>, Task<ToolResult>> RunTool { get; set; }

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
        // Returns what to say and what was found — see ToolResult for why a
        // handler no longer speaks for itself.
        public Func<CommandContext, IReadOnlyDictionary<string, string>, Task<ToolResult>> Handler { get; }

        // Ephemeral tools (wait, repeat) are control-flow primitives, not real
        // actions — they're skipped from conversation memory to avoid cluttering
        // it (a flash = many wait/light calls).
        public bool Ephemeral { get; }

        public VoiceCommand(
            ToolDefinition tool,
            Func<string, bool> matches,
            Func<CommandContext, IReadOnlyDictionary<string, string>, Task<ToolResult>> handler,
            Func<string, IReadOnlyDictionary<string, string>> extractArgs = null,
            bool ephemeral = false)
        {
            Tool = tool ?? throw new ArgumentNullException(nameof(tool));
            Name = tool.Name;
            Matches = matches ?? throw new ArgumentNullException(nameof(matches));
            Handler = handler ?? throw new ArgumentNullException(nameof(handler));
            ExtractArgs = extractArgs ?? (_ => EmptyArgs);
            Ephemeral = ephemeral;
        }

        // For tools that just do a thing: flipping a switch, opening an app,
        // waiting. There is no answer to voice and nothing for the model to
        // reason over, so requiring them to write `return ToolResult.None` would
        // be ceremony. They keep returning Task and this adapts them.
        //
        // It is also what makes the ToolResult change additive: every handler
        // written before it keeps compiling through this overload, and gets
        // converted when there is a reason to, not all at once.
        public VoiceCommand(
            ToolDefinition tool,
            Func<string, bool> matches,
            Func<CommandContext, IReadOnlyDictionary<string, string>, Task> handler,
            Func<string, IReadOnlyDictionary<string, string>> extractArgs = null,
            bool ephemeral = false)
            : this(
                tool,
                matches,
                handler == null
                    ? (Func<CommandContext, IReadOnlyDictionary<string, string>, Task<ToolResult>>)null
                    : async (ctx, args) => { await handler(ctx, args); return ToolResult.None; },
                extractArgs,
                ephemeral)
        {
        }

        public static readonly IReadOnlyDictionary<string, string> EmptyArgs =
            new Dictionary<string, string>();
    }
}