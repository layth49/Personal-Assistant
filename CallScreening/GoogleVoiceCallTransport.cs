using Personal_Assistant.Configuration;
using Personal_Assistant.Triggers;
using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Personal_Assistant.CallScreening
{
    /// <summary>
    /// A call arriving on a Google Voice number, which rings in a browser rather
    /// than on a Bluetooth-tethered phone — so it works with the phone anywhere.
    /// </summary>
    /// <remarks>
    /// EVERY SELECTOR BELOW WAS MEASURED, not guessed, on a real ringing call
    /// (2026-08-21, GvProbe watch). Google Voice is a Polymer app: the call UI
    /// lives inside shadow roots, so document.querySelector finds NONE of it and
    /// a naive probe reports an empty page. Every walk here pierces shadowRoot.
    ///
    /// THE TRAP THAT COST NOTHING ONLY BECAUSE IT WAS DUMPED FIRST: the page also
    /// contains CALL HISTORY rows reading "Incoming call from Hamood. Friday,
    /// August 21 2026, 11:57 PM". Two of those were on screen seven seconds
    /// BEFORE the phone rang. Detecting a call by matching text like "incoming
    /// call from" therefore fires on calls that happened hours ago, forever, and
    /// would have had the assistant answering a line nobody was on. Detection is
    /// anchored on the ANSWER BUTTON instead — aria-label "Answer call" — which
    /// exists only while a call is actually ringing.
    ///
    /// The caller's name appears a beat AFTER the buttons do; in the measured
    /// capture the live region was missing from the first sample of the ringing
    /// state and present in the next one, same second. So a ring with no name yet
    /// is normal and must not be reported as an unknown caller — see ReadCall.
    /// </remarks>
    public sealed class GoogleVoiceCallTransport : ICallTransport
    {
        private readonly GoogleVoiceBrowserHost browser;
        private readonly bool ownsBrowser;
        private readonly TriggerService triggers;
        private readonly Func<bool> isArmed;
        private readonly TimeSpan pollInterval;

        // Set by the poll thread, taken by the trigger action — the same handover
        // PhoneLinkCallWatcher does, and for the same reason: by the time the
        // ticker runs the action, any live DOM state is stale.
        private IncomingCall pending;

        // Edge latch. A call rings for about twenty seconds and the poll runs
        // twice a second, so the same ring is seen forty times; only its ARRIVAL
        // is an event.
        private bool wasRinging;

        // When the current ring started, so a nameless first sample can be given a
        // moment to grow a name. MinValue means "not ringing".
        private DateTime ringingSince = DateTime.MinValue;

        // What the last SUCCESSFUL read said, so a dropped devtools socket can be
        // told apart from a browser that simply is not running. Detached with a
        // call up is a broken connection; detached with nothing up is the ordinary
        // idle state, and those two must not give the same answer — see
        // CurrentLocation.
        private volatile string lastReadState = "none";

        // The last answer CurrentLocation gave, so the moment a live call stops
        // reading as live can be logged ONCE, with a reason. Idle polling sits on
        // None indefinitely and must stay silent.
        private CallLocation lastLocation = CallLocation.None;

        // Long enough for the live region to render, short enough that a caller
        // with genuinely no name is still answered promptly. A ring lasts ~20s.
        private static readonly TimeSpan NameGrace = TimeSpan.FromMilliseconds(1500);

        private Thread thread;
        private volatile bool stopping;

        // Logged once per spell rather than twice a second — the browser can be
        // restarting, signed out, or simply not up yet, and a screenful of
        // identical lines hides the one that matters.
        private string lastFailure;

        // Separate from lastFailure, which the loop clears after every successful
        // poll — and a wrong page IS a successful poll, so sharing the field would
        // reprint the warning twice a second forever.
        private string lastWrongHost;

        public GoogleVoiceCallTransport(
            TriggerService triggers,
            Func<bool> isArmed,
            Func<IncomingCall, Task> onIncomingCall,
            GoogleVoiceBrowserHost browser = null,
            TimeSpan? pollInterval = null)
        {
            this.triggers = triggers ?? throw new ArgumentNullException(nameof(triggers));
            this.isArmed = isArmed ?? throw new ArgumentNullException(nameof(isArmed));
            if (onIncomingCall == null) throw new ArgumentNullException(nameof(onIncomingCall));

            this.ownsBrowser = browser == null;
            this.browser = browser ?? new GoogleVoiceBrowserHost();
            this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(
                LaithConfig.Int("CallGvPollMs", 500, 200, 5000));

            // Registered exactly as the Phone Link watcher registers it, including
            // the two deliberate exceptions: this is the one feature whose whole
            // point is to fire while nobody is there, so it neither requires
            // presence nor respects quiet hours. A phone that rings at 2am is
            // precisely the call worth screening.
            triggers.AddSignal(
                CallTriggers.Incoming,
                async () =>
                {
                    IncomingCall call = Interlocked.Exchange(ref pending, null);
                    // Latched, held, and by the time the ticker got to it the call
                    // had already been dealt with. Better than answering a line
                    // that stopped ringing.
                    if (call == null) return;
                    await onIncomingCall(call).ConfigureAwait(false);
                },
                minInterval: TimeSpan.FromSeconds(5),
                grace: TimeSpan.FromSeconds(10),
                respectQuietHours: false,
                requiresPresence: false);
        }

        public string Name => "google voice";

        // A browser has no opinion about headsets: it renders to whatever the
        // default endpoint is and never hands the call back to a handset. None of
        // the BluetoothHeadset apparatus must run on this path.
        public bool RequiresHeadsetDisconnect => false;

        // A call only reaches Google Voice because the carrier forwarded it after
        // Layth did not answer. The choice is the assistant or voicemail, never
        // the assistant or Layth — so this may sit armed by default.
        public bool AnswersOnlyMissedCalls => true;

        /// <summary>
        /// Nothing to check here. The browser is launched lazily by the poll and
        /// heals itself — and refusing to arm because a browser has not started
        /// yet would refuse the very thing that starts it.
        /// </summary>
        public ArmRefusal NotReady() => null;

        public void Start()
        {
            if (thread != null) return;

            thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "gv-call-poll"
            };
            thread.Start();
        }

        private void Loop()
        {
            while (!stopping)
            {
                try
                {
                    // Cheap while disarmed, and deliberately so: this holds a
                    // browser process open, which is not something to do around
                    // the clock for a feature that is armed for thirty minutes at
                    // a time.
                    if (isArmed())
                    {
                        EnsureBrowser();
                        PollOnce();
                    }
                    else
                    {
                        wasRinging = false;
                    }

                    lastFailure = null;
                }
                catch (Exception ex)
                {
                    if (ex.Message != lastFailure)
                    {
                        Console.WriteLine("[call/gv] poll failed: " + ex.Message);
                        lastFailure = ex.Message;
                    }
                }

                Thread.Sleep(pollInterval);
            }
        }

        private void EnsureBrowser()
        {
            if (browser.IsAttached) return;
            browser.StartAsync().GetAwaiter().GetResult();
        }

        private void PollOnce()
        {
            CallState state = Read();

            // SELF-HEAL A PAGE THAT IS NOT GOOGLE VOICE. Attaching to a browser is
            // not the same as watching the right tab: a stale instance on the same
            // profile once left a chrome://newtab page, the poll ran against it
            // without error for a whole call, and the caller went to voicemail
            // with nothing in the log to explain it.
            if (state.Host != null &&
                state.Host.IndexOf("voice.google.com", StringComparison.OrdinalIgnoreCase) < 0)
            {
                if (lastWrongHost != state.Host)
                {
                    Console.WriteLine(
                        "[call/gv] watching '" + state.Host + "', which is not Google Voice — " +
                        "navigating back. Nothing can ring until this clears.");
                    lastWrongHost = state.Host;
                }

                browser.EnsureOnVoiceAsync().GetAwaiter().GetResult();
                return;
            }

            lastWrongHost = null;

            bool ringing = state.State == "ringing";
            if (!ringing) ringingSince = DateTime.MinValue;
            else if (ringingSince == DateTime.MinValue) ringingSince = DateTime.Now;

            if (ringing && !wasRinging)
            {
                // WAIT FOR THE NAME BEFORE COMMITTING TO "unknown number".
                //
                // The Answer button appears a beat before the live region carrying
                // "Incoming call from <name>", so the FIRST ringing sample almost
                // always has a number and no name. Signalling on it produced a real
                // screened call (2026-08-22) where the caller was announced as an
                // unknown number and the persona — correctly, given what it was
                // told — opened by saying it could not see who was calling. The
                // name was on the page a fraction of a second later.
                //
                // So a nameless ring is held briefly rather than published. The
                // deadline matters as much as the wait: some callers genuinely have
                // no name, and holding out for one forever would mean never
                // answering them at all.
                if (state.Caller == null &&
                    DateTime.Now - ringingSince < NameGrace)
                {
                    return; // still ringing; try again on the next poll
                }

                var call = new IncomingCall(state.Caller, CallRoute.AcceptOnPc, state.Number);
                Interlocked.Exchange(ref pending, call);

                Console.WriteLine("[call/gv] ringing — " + call.Describe());
                if (!triggers.Signal(CallTriggers.Incoming))
                {
                    // Registration happens in the constructor, so this can only
                    // mean something removed the trigger underneath us.
                    Console.WriteLine("[call/gv] no '" + CallTriggers.Incoming +
                                      "' trigger is registered — nothing will answer.");
                }
            }

            wasRinging = ringing;
        }

        public AnswerResult Answer()
        {
            try
            {
                EnsureBrowser();

                CallState before = Read();
                if (before.State != "ringing")
                    return new AnswerResult(AnswerOutcome.NoToast, null,
                        "nothing was ringing by the time the answer ran");

                var call = new IncomingCall(before.Caller, CallRoute.AcceptOnPc, before.Number);

                bool clicked = Click("Answer call");
                if (!clicked)
                    return new AnswerResult(AnswerOutcome.NoKnownAction, call,
                        "the Answer call button was not there — nothing was clicked");

                // Verify rather than assume. The click is asynchronous inside the
                // page and a connected call is the ONLY thing that makes it safe
                // to start talking; Phase 3 learned this the expensive way on
                // Phone Link, where speaking before the title read "Call on PC"
                // meant the greeting went nowhere.
                if (WaitForState("connected", TimeSpan.FromSeconds(8)))
                    return new AnswerResult(AnswerOutcome.OnPc, call, "connected in the browser");

                return new AnswerResult(AnswerOutcome.Failed, call,
                    "Answer call was clicked but the call never reported connected");
            }
            catch (Exception ex)
            {
                return new AnswerResult(AnswerOutcome.Failed, null, ex.Message);
            }
        }

        /// <summary>
        /// Rejects a ringing call. Google Voice has no separate decline control —
        /// the same "Hang up call" button serves both, which is why this and
        /// HangUp click the same thing.
        /// </summary>
        public bool Decline()
        {
            try
            {
                EnsureBrowser();
                if (Read().State != "ringing") return false;
                return Click("Hang up call");
            }
            catch { return false; }
        }

        public bool HangUp(int attempts = 2)
        {
            try
            {
                EnsureBrowser();

                for (int i = 0; i < Math.Max(1, attempts); i++)
                {
                    if (Read().State == "none") return true;

                    Click("Hang up call");

                    // A browser re-render can swallow a click, which is exactly
                    // what `attempts` is for. Unlike Phone Link there is no
                    // confirmation dialog to chase.
                    if (WaitForState("none", TimeSpan.FromSeconds(3))) return true;
                }

                return Read().State == "none";
            }
            catch { return false; }
        }

        /// <summary>
        /// Where the call is, as the page reads right now.
        /// </summary>
        /// <remarks>
        /// <para>
        /// THE CALLER-LEFT DECISION HANGS OFF THIS. CallSession polls it every two
        /// seconds and treats <see cref="CallLocation.None"/> as the caller having
        /// put the phone down, so every path that returns None here can end a live
        /// conversation. It used to return None for a dropped devtools socket as
        /// well, and said nothing about which of several quite different
        /// situations it was reporting — so a call that ended because the page
        /// blinked was indistinguishable, in the log, from one where the caller
        /// genuinely hung up. Both simply stopped.
        /// </para>
        /// <para>
        /// Two rules now. A detached browser with a call up is UNKNOWN, not gone:
        /// the poll re-attaches within a beat, and a socket is not a phone line.
        /// Detached with nothing up stays None — that is just a browser which is
        /// not running, and answering "I couldn't hang up" to "hang up" when there
        /// was never a call would be worse than useless. And every transition out
        /// of a live call is logged with the reason the page gave, which is the
        /// line that was missing when this last had to be diagnosed.
        /// </para>
        /// </remarks>
        public CallLocation CurrentLocation()
        {
            try
            {
                if (!browser.IsAttached)
                {
                    return lastReadState == "none"
                        ? Settle(CallLocation.None, "no browser is attached and no call was up")
                        : Settle(CallLocation.Unknown, "the devtools connection dropped while a call was up");
                }

                CallState now = Read();
                switch (now.State)
                {
                    // A ringing call is not yet anywhere, but it IS on this
                    // machine and nowhere else — there is no handset in the path
                    // to hand it to, so OnMobile can never occur here.
                    case "connected": return Settle(CallLocation.OnPc, null);
                    case "ringing": return Settle(CallLocation.OnPc, null);
                    case "none": return Settle(CallLocation.None, now.Why);
                    default: return Settle(CallLocation.Unknown, "the page reported state '" + now.State + "'");
                }
            }
            catch (Exception ex)
            {
                return Settle(CallLocation.Unknown, "the page could not be read: " + ex.Message);
            }
        }

        /// <summary>
        /// Records the answer, and logs the one transition worth a line: a call
        /// that was live and now is not.
        /// </summary>
        /// <remarks>
        /// Deliberately silent about everything else. This runs twice a second
        /// while a call is up, and from two idle guards besides, so logging every
        /// answer would bury the one that matters under a screenful of None.
        /// </remarks>
        private CallLocation Settle(CallLocation now, string why)
        {
            CallLocation was = lastLocation;
            lastLocation = now;

            if (was == CallLocation.OnPc && now != CallLocation.OnPc)
            {
                Console.WriteLine(
                    "[call/gv] the call stopped reading as live (" + Describe(now) + "): " +
                    (why ?? "no reason given") + ".");
            }

            return now;
        }

        public string Describe(CallLocation location)
        {
            switch (location)
            {
                case CallLocation.None: return "not connected";
                case CallLocation.OnPc: return "in the browser on this PC";
                case CallLocation.OnMobile: return "on the handset";
                default: return "in an unreadable state";
            }
        }

        private bool WaitForState(string want, TimeSpan within)
        {
            DateTime deadline = DateTime.Now + within;
            while (DateTime.Now < deadline)
            {
                if (Read().State == want) return true;
                Thread.Sleep(200);
            }
            return false;
        }

        private CallState Read()
        {
            JsonElement v = browser.EvalAsync(ReadJs).GetAwaiter().GetResult();
            string raw = v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            // NOT the same "none" an idle page produces, and worth saying so. The
            // detection JS always returns a string; anything else means the
            // evaluate came back with no value at all, which is a broken read
            // dressed up as a quiet line.
            if (string.IsNullOrEmpty(raw))
            {
                return Latch(new CallState
                {
                    State = "none",
                    Why = "the page returned no state at all (" + v.ValueKind + ")"
                });
            }

            using (JsonDocument doc = JsonDocument.Parse(raw))
            {
                JsonElement r = doc.RootElement;
                return Latch(new CallState
                {
                    State = Str(r, "state") ?? "none",
                    Caller = Str(r, "caller"),
                    Number = Str(r, "number"),
                    Host = Str(r, "host"),
                    Why = Str(r, "why")
                });
            }
        }

        // Every successful read passes through here — from the poll thread and
        // from CurrentLocation alike — so "what did we last actually see" has one
        // home rather than one per caller.
        private CallState Latch(CallState state)
        {
            lastReadState = state.State ?? "none";
            return state;
        }

        private static string Str(JsonElement o, string name) =>
            o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        /// <summary>
        /// Clicks the first enabled element carrying this aria-label, anywhere in
        /// the page including inside shadow roots. Returns false when there was
        /// no such element — which is a real answer, not a failure: it is how
        /// "the call stopped ringing before we got to it" presents.
        /// </summary>
        private bool Click(string ariaLabel)
        {
            string js =
                "(() => {\n" + Walker + "\n" +
                "  for (const el of all) {\n" +
                "    if (!el.getAttribute) continue;\n" +
                "    if (el.getAttribute('aria-label') !== " + Quote(ariaLabel) + ") continue;\n" +
                "    if (el.disabled) continue;\n" +
                "    el.click();\n" +
                "    return true;\n" +
                "  }\n" +
                "  return false;\n" +
                "})()";

            JsonElement v = browser.EvalAsync(js).GetAwaiter().GetResult();
            return v.ValueKind == JsonValueKind.True;
        }

        public void Dispose()
        {
            stopping = true;
            try { thread?.Join(TimeSpan.FromSeconds(2)); } catch { }

            // The browser is ours to close only when we made it. One passed in by
            // a harness belongs to the harness, and killing it would take that
            // harness's own session down.
            if (ownsBrowser) browser?.Dispose();
        }

        private static string Quote(string s) => JsonSerializer.Serialize(s);

        private sealed class CallState
        {
            public string State;
            public string Caller;
            public string Number;
            public string Host;

            // Only set when State is "none", and only to say WHICH of several
            // quite different situations produced it. See CurrentLocation.
            public string Why;
        }

        // Collects every element in the document INCLUDING inside shadow roots.
        // Shared by the read and the click so they can never disagree about what
        // is on the page.
        private const string Walker = @"
  const all = [];
  (function walk(root) {
    for (const el of root.querySelectorAll('*')) {
      all.push(el);
      if (el.shadowRoot) walk(el.shadowRoot);
    }
  })(document);";

        /// <summary>
        /// Public so GvProbe can exercise the REAL detection against a live call
        /// rather than a copy that can drift away from it.
        /// </summary>
        // Returns a JSON STRING rather than an object: Runtime.evaluate with
        // returnByValue serialises objects unevenly across Chromium versions,
        // and a string round-trips identically everywhere.
        public const string ReadJs = @"
(() => {
  const all = [];
  (function walk(root) {
    for (const el of root.querySelectorAll('*')) {
      all.push(el);
      if (el.shadowRoot) walk(el.shadowRoot);
    }
  })(document);

  const byLabel = (name) => all.find(el =>
    el.getAttribute && el.getAttribute('aria-label') === name);

  const answer = byLabel('Answer call');
  const hangup = byLabel('Hang up call');

  // STATE. Anchored on the buttons, never on text — the page carries call
  // HISTORY rows whose text reads 'Incoming call from <name>' and which are
  // present long before and long after any live call.
  // WHY, not just what. 'none' is four different situations wearing one name,
  // and screening can end a live call on it — so each one says which it is. The
  // element count rides along because a walk that returns almost nothing is the
  // documented shape of a broken shadow-DOM traversal, and from out here that
  // reads exactly like a call which ended.
  let state = 'none', why = null;
  if (answer && !answer.disabled) state = 'ringing';
  else if (hangup && !hangup.disabled) state = 'connected';
  else if (hangup) why = 'the Hang up call button is present but disabled';
  else if (answer) why = 'the Answer call button is present but disabled';
  else why = 'neither call button is in the DOM (' + all.length + ' elements walked)';

  // The hostname rides along on EVERY read, including the idle one. A page
  // that has drifted off Google Voice reads exactly like a quiet line — which
  // is how a stale chrome://newtab tab once absorbed a whole call unnoticed.
  if (state === 'none')
    return JSON.stringify({ state: 'none', host: location.hostname, why });

  // Bidi control characters wrap every number Google renders; left in, they
  // corrupt the digits and any comparison against a contact list.
  const clean = (s) => (s || '').replace(/[‪-‮‎‏]/g, '').trim();

  // The live region, which reads 'Incoming call from Hamood 5 0 4 3 4 5 6 4 8 3'
  // — name in plain words, number spelled out one digit at a time for screen
  // readers. Distinguished from a history row by having NO date and NO period
  // after the name.
  let caller = null, number = null;
  for (const el of all) {
    if (el.childElementCount !== 0) continue;
    const t = clean(el.textContent);
    const m = /^Incoming call from (.+?)\s+((?:\d\s*){7,})$/.exec(t);
    if (m) { caller = m[1].trim(); number = m[2].replace(/\s+/g, ''); break; }
  }

  // Fallback: the panel's own 'mobile (504) 345-6483' line. Used when the live
  // region has not rendered yet, which happens for a beat after the buttons
  // appear.
  if (!number) {
    for (const el of all) {
      const t = clean(el.textContent);
      const m = /\((\d{3})\)\s*(\d{3})-(\d{4})/.exec(t);
      if (m) { number = m[1] + m[2] + m[3]; break; }
    }
  }

  return JSON.stringify({ state, caller, number, host: location.hostname });
})()";
    }
}
