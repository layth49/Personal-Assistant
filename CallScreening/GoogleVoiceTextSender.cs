using Personal_Assistant.Configuration;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Personal_Assistant.CallScreening
{
    /// <summary>What happened when a text was sent.</summary>
    public sealed class TextSendResult
    {
        public bool Sent { get; }

        /// <summary>Why not, for the log. Null when it went.</summary>
        public string Detail { get; }

        private TextSendResult(bool sent, string detail)
        {
            Sent = sent;
            Detail = detail;
        }

        public static TextSendResult Ok() => new TextSendResult(true, null);
        public static TextSendResult Failed(string why) => new TextSendResult(false, why);
    }

    /// <summary>
    /// Sends an SMS through the Google Voice web client, so a message a screened
    /// call took reaches Layth while he is still out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY GOOGLE VOICE AND NOT SMSController. The Phone Link route drove the UI
    /// of the phone sitting on the desk, which meant the phone texted itself —
    /// useful only to someone already in the room, which is exactly the person
    /// who did not need telling. Google Voice sends over the internet to the
    /// handset he is actually carrying. It is also the transport screening
    /// already runs on, with a signed-in, health-checked browser attached to it,
    /// so this is one more page in a session that is already open rather than a
    /// second automation stack to keep alive.
    /// </para>
    /// <para>
    /// EVERY SELECTOR BELOW WAS MEASURED on the live client (GvMessageProbe,
    /// 2026-08-23), never guessed — the same discipline, and the same reason, as
    /// GoogleVoiceCallTransport. Google Voice is an Angular app inside shadow
    /// roots, so every walk here pierces shadowRoot, and:
    /// </para>
    /// <list type="bullet">
    /// <item>the composer opens from <c>aria-label="Send new message"</c>;</item>
    /// <item>the recipient box is <c>placeholder="Type a name or phone number"</c>;</item>
    /// <item>typing digits raises a CDK overlay holding
    /// <c>button#send-to-button</c> — "Send to (504) 881-0943" — and that is what
    /// turns a typed number into a real recipient;</item>
    /// <item>the body is <c>placeholder="Type a message"</c>;</item>
    /// <item><c>aria-label="Send message"</c> is DISABLED until the client has both
    /// a resolved recipient and a body, which is why this waits for it to enable
    /// rather than trusting its own typing.</item>
    /// </list>
    /// <para>
    /// TYPING IS DONE THROUGH Input.insertText, NOT BY SETTING .value. Measured:
    /// assigning the value (even through the native setter, with input and change
    /// events) fills the box and the suggestion panel never opens, so the
    /// recipient never resolves and Send stays dark with nothing to explain it.
    /// Real browser-level text entry works.
    /// </para>
    /// </remarks>
    public sealed class GoogleVoiceTextSender
    {
        private const string MessagesUrl = "https://voice.google.com/u/0/messages";

        private readonly GoogleVoiceBrowserHost browser;

        /// <param name="browser">
        /// Where a scratch tab comes from. May be null, in which case only
        /// <see cref="SendOnPageAsync"/> works — which is how GvMessageProbe
        /// exercises THIS code against the live client rather than a copy of it
        /// that can drift away from what ships.
        /// </param>
        public GoogleVoiceTextSender(GoogleVoiceBrowserHost browser = null)
        {
            this.browser = browser;
        }

        /// <summary>
        /// The number messages are sent to, from config. Null when it is unset or
        /// unusable, which disables the whole text channel.
        /// </summary>
        public static string NotifyNumber => Normalise(LaithConfig.Text("CallNotifyNumber", ""));

        /// <summary>
        /// Sends one text. Never throws — a failed delivery must not take down
        /// the teardown it runs inside, and the spoken channel is still there to
        /// catch what this drops.
        /// </summary>
        public async Task<TextSendResult> SendAsync(
            string number, string body, CancellationToken cancel = default)
        {
            if (browser == null) return TextSendResult.Failed("no browser to send through");

            CdpPage tab = null;
            try
            {
                tab = await browser.OpenScratchTabAsync(MessagesUrl, cancel).ConfigureAwait(false);
                return await SendOnPageAsync(tab, number, body, cancel).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return TextSendResult.Failed(ex.Message);
            }
            finally
            {
                // Closes the scratch tab. Also discards an unsent draft, which is
                // the right outcome for every failure below.
                try { tab?.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// Drives the composer on a page somebody else opened and owns. The whole
        /// of the send lives here so that a test can run the real thing.
        /// </summary>
        public async Task<TextSendResult> SendOnPageAsync(
            CdpPage tab, string number, string body, CancellationToken cancel = default)
        {
            string to = Normalise(number);
            if (to == null) return TextSendResult.Failed("no usable number to text");
            if (string.IsNullOrWhiteSpace(body)) return TextSendResult.Failed("nothing to say");

            try
            {
                if (!await SettleAsync(tab, cancel).ConfigureAwait(false))
                    return TextSendResult.Failed("the Messages view never finished loading");

                if (!await ClickAsync(tab, OpenComposer, cancel).ConfigureAwait(false))
                    return TextSendResult.Failed("could not find the 'Send new message' button");

                if (!await WaitForAsync(tab, HasField(Recipient), TimeSpan.FromSeconds(10), cancel)
                        .ConfigureAwait(false))
                    return TextSendResult.Failed("the composer never opened");

                string typed = await TypeAsync(tab, Recipient, to, cancel).ConfigureAwait(false);
                if (typed != null)
                    return TextSendResult.Failed("could not type the recipient — " + typed);

                // The suggestion panel is asynchronous, so this waits for the
                // button rather than assuming it is already there.
                if (!await WaitForAsync(tab, HasSendTo, TimeSpan.FromSeconds(10), cancel)
                        .ConfigureAwait(false))
                    return TextSendResult.Failed(
                        "Google Voice offered no 'Send to' option for " + to);

                // THE ONE CHECK THAT MATTERS MOST, and the reason this is not
                // simply four clicks in a row. A stranger's words are about to be
                // sent to whoever this ends up addressed to, so the digits get
                // confirmed against a real element before anything is committed.
                //
                // CHECKED ON THE BUTTON ITSELF, BEFORE IT IS CLICKED, because
                // that button is what sets the recipient and its label states the
                // number: "Send to (504) 881-0943". Two earlier attempts at this
                // read the composer AFTERWARDS instead and both were wrong — the
                // page renders no recipient chip at all, so the first found
                // nothing, and the second matched .phone-number and read back
                // "(504) 722-4259", the first row of the contact sidebar. That is
                // this codebase's oldest trap: a list full of elements that look
                // exactly like the one you want. Verifying the control you are
                // about to press cannot pick up a bystander.
                string offer = await TextAsync(tab, ReadSendTo, cancel).ConfigureAwait(false);
                if (offer == null || !DigitsOf(offer).EndsWith(DigitsOf(to), StringComparison.Ordinal))
                    return TextSendResult.Failed(
                        "the 'Send to' option offers '" + (offer ?? "nothing") + "', not " + to +
                        " — not sending");

                if (!await ClickAsync(tab, ClickSendTo, cancel).ConfigureAwait(false))
                    return TextSendResult.Failed("the 'Send to' option vanished before it was clicked");

                // WAIT FOR THE RECIPIENT TO ACTUALLY LAND before typing the body.
                //
                // This is where the first real send went wrong. The click was
                // registered and the code went straight on, so the body was typed
                // into a composer still resolving its recipient. Send lit up, the
                // click was accepted, and the draft was quietly discarded — no
                // conversation, no message, no error. A generously-delayed run of
                // the same steps worked first time, which is what pointed at the
                // timing.
                //
                // The overlay closing IS the recipient landing, and it is a state
                // rather than the sleep that diagnosed it — a fixed delay is only
                // ever a guess about how long a thing takes.
                if (!await WaitForAsync(tab, SendToGone, TimeSpan.FromSeconds(10), cancel)
                        .ConfigureAwait(false))
                    return TextSendResult.Failed(
                        "the recipient list never closed after picking 'Send to'");

                string wrote = await TypeAsync(tab, Body, body, cancel).ConfigureAwait(false);
                if (wrote != null)
                    return TextSendResult.Failed("could not type the message — " + wrote);

                // Enabled means Google Voice itself agrees it has a resolved
                // recipient and a body. It is a far better gate than anything we
                // could assert about our own typing.
                if (!await WaitForAsync(tab, SendIsEnabled, TimeSpan.FromSeconds(10), cancel)
                        .ConfigureAwait(false))
                    return TextSendResult.Failed("Send never became available");

                if (!await ClickAsync(tab, ClickSend, cancel).ConfigureAwait(false))
                    return TextSendResult.Failed("the Send button would not click");

                if (!await WaitForAsync(tab, SentEvidence(to, body), TimeSpan.FromSeconds(30), cancel)
                        .ConfigureAwait(false))
                    return TextSendResult.Failed(
                        "Send was clicked but the message never left 'Sending...'");

                // One last look for a refusal. Google Voice puts the message in
                // the thread first and marks it afterwards, so a message being
                // ON the page is not yet proof it went.
                string refused = await TextAsync(tab, Refusal, cancel).ConfigureAwait(false);
                if (refused != null)
                    return TextSendResult.Failed("Google Voice would not send it: " + refused);

                // A breath before the tab closes. The status chip is the
                // server's acknowledgement, so this is not load-bearing — but
                // closing the tab is what killed the two sends this method got
                // wrong before, and a second is a very cheap way to stop being
                // the thing that races Google's own client to the finish.
                await Task.Delay(1000, cancel).ConfigureAwait(false);

                return TextSendResult.Ok();
            }
            catch (Exception ex)
            {
                return TextSendResult.Failed(ex.Message);
            }
        }

        /// <summary>
        /// Ten digits, or null. Accepts what a person would type — +1, dashes,
        /// brackets, spaces — and refuses anything that is not a US number,
        /// because sending a stranger's words to a mistyped one is worse than not
        /// sending at all.
        /// </summary>
        public static string Normalise(string raw)
        {
            string digits = DigitsOf(raw);
            if (digits.Length == 11 && digits[0] == '1') digits = digits.Substring(1);
            return digits.Length == 10 ? digits : null;
        }

        private static string DigitsOf(string s) =>
            string.IsNullOrEmpty(s) ? string.Empty : new string(s.Where(char.IsDigit).ToArray());

        // --- the steps, each verified ------------------------------------------

        private async Task<bool> SettleAsync(CdpPage tab, CancellationToken cancel)
        {
            // Element count, not location.href. A page one instant after
            // navigation has a URL and nothing else — the same lesson
            // WaitUntilReadyAsync learned on the call path, where "now on
            // voice.google.com" was logged for a page whose client had not
            // booted and a real call rang through to voicemail.
            const string booted =
                "(() => { const all = [];" +
                "  (function walk(r){ for (const el of r.querySelectorAll('*')) {" +
                "      all.push(el); if (el.shadowRoot) walk(el.shadowRoot); } })(document);" +
                "  return all.length > 500; })()";

            return await WaitForAsync(tab, booted, TimeSpan.FromSeconds(30), cancel)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Types into the field with this placeholder. Null on success; otherwise
        /// what went wrong, in words fit for a log line.
        /// </summary>
        /// <remarks>
        /// RETRIED, because a single attempt lost a real message on 2026-08-23:
        /// "the text did not go: could not type the recipient", on a call whose
        /// message had been taken perfectly well. The probe could not reproduce
        /// it, which was the clue — the probe waits two seconds after opening the
        /// composer and the sender only waits for the field to EXIST, which it
        /// does within a few hundred milliseconds. Angular autofocuses that field
        /// itself a beat later, and an autofocus landing after our text wipes it.
        ///
        /// So the fix is not another sleep. Selecting the field's contents before
        /// each attempt makes insertText REPLACE rather than append, which makes
        /// a retry idempotent; the read-back then decides. Three goes across two
        /// seconds beats any single delay guessed in advance, and it also covers
        /// the whole family of "the page moved under us" rather than this one
        /// instance of it.
        ///
        /// The reason is returned rather than logged here so the caller can say
        /// WHICH field failed and what it actually contained. "Could not type the
        /// recipient" with no detail is what made this cost a round trip.
        /// </remarks>
        private async Task<string> TypeAsync(
            CdpPage tab, string placeholder, string text, CancellationToken cancel)
        {
            string last = "it never became typeable";

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (attempt > 1) await Task.Delay(700, cancel).ConfigureAwait(false);

                string focused = await TextAsync(tab, FocusField(placeholder), cancel)
                    .ConfigureAwait(false);

                if (focused == "no field")
                {
                    last = "the field was not on the page";
                    continue;
                }

                // Not gated on the focus report. Focus is unreliable to observe in
                // a background tab and the read-back below is the real test —
                // refusing to type because a check said "not focused" would fail
                // on a page that would have accepted the text perfectly well.
                await tab.CallAsync("Input.insertText", new { text }, cancel).ConfigureAwait(false);

                string got = await TextAsync(tab, ReadField(placeholder), cancel)
                    .ConfigureAwait(false);
                if (got == text) return null;

                last = got == null ? "it reads back as nothing"
                     : got.Length == 0 ? "it came back empty"
                     : "it reads '" + Clip(got) + "'";
            }

            return last + " after 3 attempts";
        }

        private static string Clip(string s) =>
            s.Length <= 60 ? s : s.Substring(0, 60) + "...";

        private async Task<bool> ClickAsync(CdpPage tab, string js, CancellationToken cancel)
        {
            JsonElement v = await tab.EvalAsync(js, cancel).ConfigureAwait(false);
            return v.ValueKind == JsonValueKind.True;
        }

        private async Task<bool> WaitForAsync(
            CdpPage tab, string predicateJs, TimeSpan within, CancellationToken cancel)
        {
            DateTime deadline = DateTime.Now + within;
            while (DateTime.Now < deadline && !cancel.IsCancellationRequested)
            {
                try
                {
                    JsonElement v = await tab.EvalAsync(predicateJs, cancel).ConfigureAwait(false);
                    if (v.ValueKind == JsonValueKind.True) return true;
                }
                catch
                {
                    // Mid-render the execution context can be replaced and the
                    // eval throws. That is "not yet", not a failure.
                }

                await Task.Delay(300, cancel).ConfigureAwait(false);
            }
            return false;
        }

        private async Task<string> TextAsync(CdpPage tab, string js, CancellationToken cancel)
        {
            JsonElement v = await tab.EvalAsync(js, cancel).ConfigureAwait(false);
            return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        // --- the measured selectors ---------------------------------------------

        private const string Recipient = "Type a name or phone number";
        private const string Body = "Type a message";

        // Collects every element in the document INCLUDING inside shadow roots.
        // Shared by every step below so they can never disagree about what is on
        // the page — the same arrangement, for the same reason, as the call
        // transport's Walker.
        private const string Walker = @"
  const all = [];
  (function walk(root) {
    for (const el of root.querySelectorAll('*')) {
      all.push(el);
      if (el.shadowRoot) walk(el.shadowRoot);
    }
  })(document);";

        private static string Quote(string s) => JsonSerializer.Serialize(s);

        private const string OpenComposer = @"
(() => {" + Walker + @"
  const el = all.find(e => e.getAttribute &&
    e.getAttribute('aria-label') === 'Send new message' && !e.disabled);
  if (!el) return false;
  el.click();
  return true;
})()";

        private static string HasField(string placeholder) => @"
(() => {" + Walker + @"
  return all.some(e =>
    (e.tagName === 'INPUT' || e.tagName === 'TEXTAREA') &&
    e.getAttribute && e.getAttribute('placeholder') === " + Quote(placeholder) + @" &&
    e.getBoundingClientRect().width > 0);
})()";

        // Focuses the field AND SELECTS WHAT IS IN IT, so the Input.insertText
        // that follows replaces rather than appends. That is what makes a retry
        // safe: without it, a second attempt after a half-written field turns
        // "5048810943" into "50489104935048810943" and texts a stranger.
        private static string FocusField(string placeholder) => @"
(() => {" + Walker + @"
  const el = all.find(e =>
    (e.tagName === 'INPUT' || e.tagName === 'TEXTAREA') &&
    e.getAttribute && e.getAttribute('placeholder') === " + Quote(placeholder) + @" &&
    e.getBoundingClientRect().width > 0);
  if (!el) return 'no field';

  el.focus();
  // A click is the reliable way in when a programmatic focus does not take,
  // which happens while the composer overlay is still animating.
  if (document.activeElement !== el) { el.click(); el.focus(); }
  try { el.select(); } catch (e) { }

  return document.activeElement === el || el.matches(':focus') ? 'ok' : 'not focused';
})()";

        private static string ReadField(string placeholder) => @"
(() => {" + Walker + @"
  const el = all.find(e =>
    (e.tagName === 'INPUT' || e.tagName === 'TEXTAREA') &&
    e.getAttribute && e.getAttribute('placeholder') === " + Quote(placeholder) + @");
  return el ? el.value : null;
})()";

        // The CDK overlay row that turns typed digits into a real recipient.
        // Anchored on the button's id, which exists only while that panel is up —
        // never on the words "Send to", which is prose and would match a stale
        // panel or a rendered message just as happily.
        private const string HasSendTo = @"
(() => {" + Walker + @"
  return all.some(e => e.id === 'send-to-button' &&
    e.getBoundingClientRect().width > 0);
})()";

        private const string ClickSendTo = @"
(() => {" + Walker + @"
  const el = all.find(e => e.id === 'send-to-button' &&
    e.getBoundingClientRect().width > 0);
  if (!el) return false;
  el.click();
  return true;
})()";

        // What the 'Send to' row is offering, so the digits can be checked before
        // it is pressed. Its label reads "Send to (504) 881-0943", with the same
        // digits spelled out again for screen readers.
        //
        // The bidi control characters Google wraps every number in are stripped,
        // exactly as the call path strips them. Left in, they corrupt the digits
        // and any comparison against them.
        private const string ReadSendTo = @"
(() => {" + Walker + @"
  const clean = (s) => (s || '').replace(/[‪-‮‎‏]/g, '').trim();

  const el = all.find(e => e.id === 'send-to-button' &&
    e.getBoundingClientRect().width > 0);

  return el ? clean(el.textContent) : null;
})()";

        // The overlay has closed, which is how the composer says it has taken the
        // recipient. Waiting on this rather than on a chip, because the page
        // renders no chip — see the guard above.
        private const string SendToGone = @"
(() => {" + Walker + @"
  return !all.some(e => e.id === 'send-to-button' &&
    e.getBoundingClientRect().width > 0);
})()";

        private const string SendIsEnabled = @"
(() => {" + Walker + @"
  const el = all.find(e => e.getAttribute &&
    e.getAttribute('aria-label') === 'Send message');
  return !!el && !el.disabled && el.getAttribute('aria-disabled') !== 'true';
})()";

        private const string ClickSend = @"
(() => {" + Walker + @"
  const el = all.find(e => e.getAttribute &&
    e.getAttribute('aria-label') === 'Send message' && !e.disabled);
  if (!el) return false;
  el.click();
  return true;
})()";

        /// <summary>
        /// The message exists as a real, sent message in a real conversation.
        /// </summary>
        /// <remarks>
        /// THIS REPLACED A CHECK THAT WAS SIMPLY WRONG, and the way it was wrong
        /// is worth keeping. It used to ask whether the body box had emptied and
        /// the URL had left "itemId=draft" — both of which are equally true when
        /// Google Voice DISCARDS a draft. The first real send passed that check,
        /// reported success, and nothing was sent: the Messages list still read
        /// "No messages" afterwards and no text ever arrived.
        ///
        /// Four pieces of evidence, all measured on a send that genuinely went:
        /// the address bar carries a real thread id — itemId=t. followed by the
        /// number, where a draft says itemId=draft and a discarded one says
        /// neither — that thread id is for the RIGHT number, the body is on the
        /// page inside the thread's own .content element, and THE ROW'S STATUS
        /// HAS SETTLED FROM "Sending..." TO A TIMESTAMP.
        ///
        /// THAT LAST ONE IS THE WHOLE THING, and leaving it out cost two real
        /// messages that were reported as sent and never arrived. Google Voice
        /// renders a sent message into the thread OPTIMISTICALLY and only then
        /// talks to the server: at t+0 the body is on the page and the status
        /// reads "Sending...", and the server does not acknowledge until about
        /// t+1.5s. Every earlier version of this check was satisfied at t+0, so
        /// the scratch tab was closed roughly a second before the request
        /// completed — and closing the tab killed the send. Nothing anywhere said
        /// so; the message simply vanished from the thread afterwards.
        /// </remarks>
        private static string SentEvidence(string to, string body) => @"
(() => {" + Walker + @"
  const m = /itemId=([^&]+)/.exec(location.href);
  if (!m) return false;

  const item = decodeURIComponent(m[1]);
  if (item.indexOf('t.') !== 0) return false;              // still a draft, or gone

  const digits = item.replace(/\D/g, '');
  if (!digits.endsWith(" + Quote(to) + @")) return false;   // a thread, but the wrong one

  const norm = (s) => (s || '').replace(/\s+/g, ' ').trim();
  const want = " + Quote(body.Trim()) + @";

  const bubble = all.find(e => e.classList &&
    e.classList.contains('content') && norm(e.textContent) === want);
  if (!bubble) return false;

  // The status chip lives alongside the bubble in the same message row.
  let p = bubble.parentElement || (bubble.parentNode && bubble.parentNode.host);
  for (let d = 0; p && d < 6; d++) {
    const s = p.querySelector && p.querySelector('.status');
    if (s) {
      const t = norm(s.textContent);
      // A timestamp means the server took it. 'Sending...' means it has not
      // yet, and an empty chip means the row is still being built.
      return t.length > 0 && !/sending/i.test(t);
    }
    p = p.parentElement || (p.parentNode && p.parentNode.host);
  }

  return false;
})()";

        // An explicit refusal, if Google Voice put one up. Prose, unavoidably —
        // there is no state flag for "we would not send this" — so it is used
        // only to EXPLAIN a failure, never to decide success. Scoped to short
        // leaf nodes so a conversation quoting the word "failed" cannot trip it.
        private const string Refusal = @"
(() => {" + Walker + @"
  const bad = /message not sent|not sent|failed to send|couldn't send|could not send/i;

  for (const e of all) {
    if (e.childElementCount !== 0) continue;
    if (e.getBoundingClientRect().width < 1) continue;
    const t = (e.textContent || '').replace(/\s+/g, ' ').trim();
    if (t && t.length < 120 && bad.test(t)) return t;
  }
  return null;
})()";
    }
}
