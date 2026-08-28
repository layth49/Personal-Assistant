using System;
using System.Collections.Generic;

namespace Personal_Assistant.CallScreening
{
    // WHAT A CALL LEFT BEHIND, SPLIT OUT FROM WHAT RAN IT.
    //
    // On main these types sit at the top of CallSession.cs and CallScreeningService.cs,
    // above the classes that produce them. That works there because the whole feature
    // arrives in one piece. It does not work here: this branch lands call screening in
    // two halves, the transport/logging half first, and the conversation half — the
    // local rebuild of CallSession and CallScreeningService — after it. CallLog and the
    // ICallTransport interface need these five types and nothing else from those files,
    // so keeping them hostage to a class that talks to a model would mean either
    // shipping nothing or shipping a stub of the model half, and a stub is the one thing
    // a fail-closed feature must not have lying around.
    //
    // They are plain data with no stack of their own — no audio, no model, no browser —
    // which is exactly why they were separable. Copied from main verbatim apart from one
    // doc comment on CallEnding.SessionLost, which said "the Live socket" and cannot,
    // since there is no Live socket on this branch.
    //
    // NOTE FOR THE CONVERSATION HALF: these are already defined. Do not bring a second
    // copy across with CallSession.cs or CallScreeningService.cs.

    /// <summary>Who said a line of a screened call.</summary>
    public enum CallSpeaker { Caller, Assistant }

    public sealed class CallTranscriptLine
    {
        public CallSpeaker Speaker { get; }
        public string Text { get; }
        public TimeSpan At { get; }

        public CallTranscriptLine(CallSpeaker speaker, string text, TimeSpan at)
        {
            Speaker = speaker;
            Text = (text ?? string.Empty).Trim();
            At = at;
        }

        public override string ToString() =>
            $"[{At.TotalSeconds,5:F1}s] {(Speaker == CallSpeaker.Caller ? "them" : "laith")}: {Text}";
    }

    /// <summary>Why a screened call stopped.</summary>
    public enum CallEnding
    {
        /// <summary>The assistant decided the call was over and called hang_up.</summary>
        Wrapped,
        /// <summary>The caller put the phone down.</summary>
        CallerLeft,
        /// <summary>CallMaxSeconds ran out.</summary>
        TimeCap,
        /// <summary>Nobody said anything for long enough that the line was dead.</summary>
        Silence,
        /// <summary>The model session went away mid-call.</summary>
        SessionLost,
        /// <summary>Torn down from outside — end_call, process exit.</summary>
        Cancelled,
        /// <summary>The session could not be opened at all.</summary>
        NeverStarted,
    }

    /// <summary>What a screened call produced.</summary>
    public sealed class CallOutcome
    {
        public string Caller { get; set; }
        public DateTime StartedAt { get; set; }
        public TimeSpan Duration { get; set; }
        public CallEnding Ending { get; set; }
        public string Message { get; set; }           // what take_message recorded, or null
        public IReadOnlyList<CallTranscriptLine> Transcript { get; set; } =
            new List<CallTranscriptLine>();
        public string Failure { get; set; }           // set only when Ending is NeverStarted

        public string Summary()
        {
            string line = $"{Caller}, {Duration.TotalSeconds:F0}s, ended: {Ending}";
            if (!string.IsNullOrWhiteSpace(Message)) line += $", message: \"{Message}\"";
            return line;
        }
    }

    // Why an arm request was refused, in words the assistant can say. Same shape
    // and same reasoning as TriggerRejection (VoiceTriggers.cs:17): a one-sentence
    // refusal Layth can act on beats silently arming into a path that cannot work.
    public sealed class ArmRefusal
    {
        public string Spoken { get; }   // what the user hears
        public string Reason { get; }   // for the model and the log

        public ArmRefusal(string spoken, string reason)
        {
            Spoken = spoken;
            Reason = reason;
        }
    }
}
