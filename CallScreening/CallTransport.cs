using System;

namespace Personal_Assistant.CallScreening
{
    // THE SEAM BETWEEN "A CALL IS HAPPENING" AND "WHICH THING IS RINGING".
    //
    // Everything below used to live in PhoneLinkCallController.cs, because Phone
    // Link was the only way a call could reach the PC. It is not any more: a
    // Google Voice number rings in a browser, which — unlike Phone Link — does
    // not care whether the phone is within Bluetooth range of the desk. That is
    // the whole reason this file exists, so read CallScreening/GoogleVoice*.cs
    // before assuming these types are Phone Link's.
    //
    // They moved rather than being duplicated, and nothing else changed: they were
    // already plain data in the same namespace, so no other file needed touching.
    //
    // The transports are deliberately NOT unified any further than this. Phone
    // Link is proven on real calls and is still the better route when the phone
    // IS at home (no forwarding, no browser, no Google in the path), so it keeps
    // working exactly as it did and Google Voice is added alongside it.

    /// <summary>
    /// Trigger names shared by every transport. One name, because only ONE
    /// transport runs per process — VoiceTriggers and the standing-rule guards
    /// are wired to this string, and a second spelling would silently mean
    /// nothing answers.
    /// </summary>
    public static class CallTriggers
    {
        public const string Incoming = "call.incoming";
    }

    // Which of the two mutually exclusive first actions the ringing toast offers.
    //
    // This is not a preference — it is a fact about the machine, read at ring
    // time. Phone Link refuses to put call audio on the PC while a Bluetooth
    // headset is connected to the PC, and swaps the button rather than disabling
    // it. The two buttons occupy the same slot and do opposite things, so the
    // route has to be READ, never assumed.
    //
    // Google Voice has no equivalent: a browser has no opinion about headsets and
    // answering always lands the audio on this machine. Its calls are therefore
    // always AcceptOnPc, which is a statement of fact rather than a default.
    public enum CallRoute
    {
        AcceptOnPc,      // no headset on the PC — answering lands the audio here
        UseMobileDevice, // a headset is on the PC — answering lands it on the handset
        Unknown          // neither name present; do not improvise (see Answer)
    }

    // A ringing call, as the ringing UI describes it. Deliberately plain data and
    // not UIA elements or DOM nodes: the watcher finds this on its poll thread and
    // the answer runs a moment later off the trigger ticker, by which time any
    // element handle is stale (or the call has stopped ringing, which is itself
    // the answer).
    public sealed class IncomingCall
    {
        public string Caller { get; }

        /// <summary>
        /// The caller's number, when the transport reports it SEPARATELY from the
        /// display name. Null on Phone Link, which only ever gave one string.
        ///
        /// Google Voice resolves the caller against Google Contacts and shows both
        /// — "Hamood" above "mobile (504) 345-6483" — so the persona can greet a
        /// known caller by name and still log the number. Measured 2026-08-21 on
        /// a real call; the name survives carrier forwarding too.
        /// </summary>
        public string Number { get; }

        public CallRoute Route { get; }
        public DateTime DetectedAt { get; }

        public IncomingCall(string caller, CallRoute route, string number = null)
        {
            Caller = string.IsNullOrWhiteSpace(caller) ? "an unknown number" : caller.Trim();
            Number = string.IsNullOrWhiteSpace(number) ? null : number.Trim();
            Route = route;
            DetectedAt = DateTime.Now;
        }

        public string Describe() =>
            Number == null ? $"{Caller} (route: {Route})"
                           : $"{Caller} {Number} (route: {Route})";
    }

    // Where the audio of a connected call currently is.
    //
    // On Phone Link this is read from TitleTextBlock, which is the ONLY thing that
    // says — the window looks identical either way. On Google Voice OnMobile
    // cannot occur: there is no handset in the path to hand the call back to.
    public enum CallLocation
    {
        None,     // no call in progress
        OnPc,     // audio is on this machine; this is what screening needs
        OnMobile, // audio is on the handset (Phone Link only)
        Unknown   // a call is up but its state could not be read
    }

    public enum AnswerOutcome
    {
        OnPc,               // connected, audio on the laptop — safe to speak
        EndedTransferFailed,// route B answered but never transferred; hung up cleanly
        NoToast,            // the ringing UI went away before we got to it
        NoKnownAction,      // the expected control was not there — nothing was clicked
        Failed              // the automation itself broke; state unknown, see Detail
    }

    public sealed class AnswerResult
    {
        public AnswerOutcome Outcome { get; }
        public IncomingCall Call { get; }     // null when there was nothing to read
        public string Detail { get; }         // one line, for the log

        public AnswerResult(AnswerOutcome outcome, IncomingCall call, string detail)
        {
            Outcome = outcome;
            Call = call;
            Detail = detail;
        }
    }

    /// <summary>
    /// One way for a call to reach this PC. Implementations own BOTH halves of
    /// that job — noticing a call (what PhoneLinkCallWatcher does) and operating
    /// its buttons (what PhoneLinkCallController does) — because the two are
    /// inseparable per transport: the thing that knows how to spot a ringing
    /// Google Voice tab is the same thing that knows how to click its Answer
    /// button, and neither half is meaningful with the other transport's partner.
    ///
    /// CallScreeningService talks only to this. It stays responsible for arm
    /// state, audio routing and the conversation, none of which differ by
    /// transport — the audio layer in particular is device-level and was measured
    /// to work unchanged for a browser (2026-08-21: the browser's render AND
    /// capture sessions both sit on the DEFAULT endpoints, no svchost involved,
    /// unlike Phone Link).
    /// </summary>
    public interface ICallTransport : IDisposable
    {
        /// <summary>Short lower-case name for log lines: "phone link", "google voice".</summary>
        string Name { get; }

        /// <summary>
        /// True when this transport refuses to put call audio on the PC while a
        /// Bluetooth headset is connected to it, so the headset must be dropped
        /// while the phone is still RINGING (dropping it after answering is too
        /// late — Phone Link has already committed the audio to the handset).
        ///
        /// A capability rather than a transport-name check, because that is what
        /// the surrounding code actually needs to know. Phone Link: true, and the
        /// whole BluetoothHeadset apparatus exists for it. Google Voice: false —
        /// a browser has no opinion about headsets, it just renders to whatever
        /// the default endpoint is, so none of that dance must run on this path.
        /// </summary>
        bool RequiresHeadsetDisconnect { get; }

        /// <summary>
        /// True when every call this transport sees has ALREADY failed to reach
        /// Layth — i.e. it can only ever answer something that was heading for
        /// voicemail anyway.
        /// </summary>
        /// <remarks>
        /// This is what decides whether screening may sit armed by default, and
        /// the two transports genuinely differ:
        ///
        ///   Phone Link answers a phone that is RINGING IN HIS HAND. Leaving that
        ///   armed around the clock is the thing the service comment warns about —
        ///   an assistant that silently picks up whenever the app happens to be
        ///   running. False.
        ///
        ///   Google Voice only receives calls the carrier already forwarded
        ///   BECAUSE he did not answer. There is nothing to intercept; the choice
        ///   is between the assistant and voicemail. True.
        ///
        /// The old model required arming out loud, for thirty minutes at a time.
        /// On the Google Voice path that is inverted: the whole point is calls that
        /// arrive while he is out, which is exactly when he cannot speak to the
        /// machine — so the window was guaranteed to be closed when it mattered.
        /// </remarks>
        bool AnswersOnlyMissedCalls { get; }

        /// <summary>
        /// Why this transport could not answer a call right now, or null when it
        /// is ready. Checked before arming, so the refusal can be spoken.
        /// </summary>
        /// <remarks>
        /// Each transport owns its own preconditions, because they share none.
        /// This existed as a flat "is PhoneExperienceHost running?" test in the
        /// service, which refused to arm on the GOOGLE VOICE path too — on a
        /// machine without Phone Link installed, screening could never be armed
        /// at all, and the refusal blamed a component that path does not use.
        /// </remarks>
        ArmRefusal NotReady();

        /// <summary>
        /// Begins watching for incoming calls. Must be cheap while disarmed —
        /// screening is armed only some of the time, and a transport that burns
        /// CPU (or holds a browser open) around the clock is not acceptable.
        /// </summary>
        void Start();

        /// <summary>
        /// Answers whatever is ringing. Synchronous: both implementations wrap
        /// blocking APIs, and callers already dispatch this with Task.Run.
        /// </summary>
        AnswerResult Answer();

        /// <summary>Rejects the ringing call outright. False if there was nothing to reject.</summary>
        bool Decline();

        /// <summary>
        /// Ends a connected call. <paramref name="attempts"/> exists because
        /// hanging up is not reliably one click — Phone Link raises a confirmation
        /// dialog, and a browser can lose the click to a re-render.
        /// </summary>
        bool HangUp(int attempts = 2);

        /// <summary>Where the call's audio is right now, re-read rather than remembered.</summary>
        CallLocation CurrentLocation();

        /// <summary>Human-readable form of a location, for the log and the widget.</summary>
        string Describe(CallLocation location);
    }
}
