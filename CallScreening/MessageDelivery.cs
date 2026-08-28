using Personal_Assistant.Configuration;
using Personal_Assistant.Resume;
using Personal_Assistant.Triggers;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Personal_Assistant.CallScreening
{
    /// <summary>
    /// Gets a message a screened call took in front of Layth, rather than
    /// leaving it in a file he has to think to open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE HOLE THIS FILLS. Screening worked end to end and then stopped: the
    /// caller was answered, a message was taken, "message taken: PC tower will
    /// cost around $600" went into the call log, and nothing else happened.
    /// <c>list_calls</c> could read it back, but only if he thought to ask — and
    /// a message you have to remember to check is barely better than a missed
    /// call.
    /// </para>
    /// <para>
    /// TWO CHANNELS, BECAUSE THEY COVER DIFFERENT SITUATIONS:
    /// </para>
    /// <list type="number">
    /// <item><b>A text, immediately.</b> This is the one that reaches him while
    /// he is out, which is the whole situation screening exists for. It goes
    /// through Google Voice — over the internet, to the handset he is carrying —
    /// rather than through Phone Link, which used to mean the phone on the desk
    /// texting itself.</item>
    /// <item><b>Spoken, when he comes back.</b> For the times the text did not
    /// go, or he was at the machine all along, or the phone was face-down. It
    /// waits for the presence gate rather than talking to an empty room.</item>
    /// </list>
    /// <para>
    /// AND ONE FLAG BETWEEN THEM. <see cref="CallRecord.Delivered"/> is set by
    /// whichever channel gets there first, and the other then stays quiet.
    /// Without it the two duplicate each other and he is read out a message he
    /// read on his phone an hour ago — which is how a feature earns itself a
    /// "stop doing that". The flag is persisted, so a restart does not undo the
    /// knowledge that he has already been told.
    /// </para>
    /// <para>
    /// NOTHING HERE MAY THROW INTO A CALL TEARDOWN. It runs at the end of
    /// <c>OnIncomingCallAsync</c>, after the audio route has been put back but
    /// while the process is still unwinding a real phone call. A failed
    /// delivery is a logged line and a message that stays undelivered — which
    /// the spoken channel then picks up — never an exception that escapes into
    /// the path responsible for hanging up the phone.
    /// </para>
    /// </remarks>
    public sealed class MessageDelivery
    {
        // How long an undelivered message stays worth saying out loud. Long
        // enough to cover a day out, short enough that coming back to the machine
        // after a weekend is not a monologue. Anything older stays in the log for
        // list_calls, which is the right home for history.
        private static readonly TimeSpan SpeakWithin = TimeSpan.FromHours(
            LaithConfig.Int("CallMessageSpeakWithinHours", 12, 1, 168));

        // HOW LONG THE SPOKEN CHANNEL WAITS BEFORE IT IS ALLOWED TO WIN.
        //
        // The text is the primary channel and it takes ten to fifteen seconds to
        // drive a browser through the composer. The spoken one is due the moment
        // the call ends, so if Layth happens to be sitting at the machine the
        // trigger fires on the next one-second tick, marks the message delivered,
        // and the text — already in flight, and unstoppable by then — arrives
        // anyway. He gets both, which is the exact duplicate this whole design
        // exists to prevent.
        //
        // A minute's head start settles it in the text's favour without giving up
        // what arming early buys: a send that HANGS still has the fallback armed
        // behind it, and a message he hears a minute after a call he watched
        // being screened is not late.
        private static readonly TimeSpan SpokenHeadStart = TimeSpan.FromSeconds(
            LaithConfig.Int("CallMessageSpokenDelaySeconds", 60, 0, 1800));

        private readonly TriggerService triggers;
        private readonly GoogleVoiceTextSender sms;      // null when texting is off
        private readonly Func<string, Task> announce;    // null in the harnesses

        /// <param name="sms">
        /// Null disables the text channel outright, leaving only the spoken one.
        /// That is the correct state on the Phone Link transport, where there is
        /// no Google Voice session to send through.
        /// </param>
        /// <param name="announce">
        /// How a line gets spoken. Null in the bakeoff harnesses, which have no
        /// speech stack — and which is why every use of it is guarded rather
        /// than assumed.
        /// </param>
        public MessageDelivery(
            TriggerService triggers,
            GoogleVoiceTextSender sms = null,
            Func<string, Task> announce = null)
        {
            this.triggers = triggers ?? throw new ArgumentNullException(nameof(triggers));
            this.sms = sms;
            this.announce = announce;
        }

        /// <summary>Whether a number is configured to text.</summary>
        public static bool TextingConfigured => GoogleVoiceTextSender.NotifyNumber != null;

        /// <summary>
        /// A call just ended. Text the message now if there is one, and arrange to
        /// say it out loud if the text does not land.
        /// </summary>
        /// <remarks>
        /// Returns a Task the caller may await, but the caller does not have to —
        /// the teardown that invokes this is holding a phone line open, and a
        /// browser round trip is not something to make it wait on.
        /// </remarks>
        public async Task OnCallEndedAsync(CallRecord record)
        {
            if (record == null) return;

            if (!record.HasUndeliveredMessage)
            {
                // A call with no message is not a failure to deliver anything.
                // Silence here is the correct behaviour, and saying so in the log
                // saves the next person wondering whether delivery ran.
                Console.WriteLine("[call/deliver] no message was taken — nothing to pass on.");
                return;
            }

            // THE SPOKEN FALLBACK IS ARMED FIRST, before the text is attempted.
            //
            // Deliberately this way round. If it were armed only after a failed
            // send, then a send that HANGS — a wedged browser, a page that never
            // settles — would leave the message with no channel at all, and the
            // one thing worse than telling him twice is telling him never. The
            // fallback checks the Delivered flag when it fires, so arming it
            // early costs nothing when the text does go.
            ArmSpokenFallback(record);

            await TextAsync(record).ConfigureAwait(false);
        }

        private async Task TextAsync(CallRecord record)
        {
            string to = GoogleVoiceTextSender.NotifyNumber;

            if (sms == null || to == null)
            {
                Console.WriteLine(
                    "[call/deliver] not texting (" +
                    (sms == null ? "no Google Voice session" : "CallNotifyNumber is not set") +
                    ") — it will be spoken when he is back.");
                return;
            }

            try
            {
                // Someone may have got there first — a restart's catch-up, or the
                // spoken channel if the head start above was configured away.
                // Cheap to ask, and the alternative is a text about a message he
                // has already heard.
                CallRecord current = Find(record.Key);
                if (current != null && current.Delivered)
                {
                    Console.WriteLine(
                        "[call/deliver] already delivered by " + current.DeliveredHow +
                        " — not texting.");
                    return;
                }

                TextSendResult result = await sms
                    .SendAsync(to, record.TextMessage()).ConfigureAwait(false);

                if (!result.Sent)
                {
                    Console.WriteLine("[call/deliver] the text did not go: " + result.Detail);
                    return;
                }

                // MARKED ONLY AFTER THE SEND IS CONFIRMED, and the mark is what
                // silences the spoken channel. Marking first would mean a failed
                // send silently cancels the fallback too, and the message is lost
                // in the one case the fallback exists for.
                if (CallLogStore.MarkDelivered(record.Key, "text"))
                    Console.WriteLine("[call/deliver] texted to " + Mask(to) + ".");
                else
                    Console.WriteLine(
                        "[call/deliver] texted, but the log entry was already marked delivered.");
            }
            catch (Exception ex)
            {
                // Belt and braces: SendAsync already swallows its own failures.
                Console.WriteLine("[call/deliver] the text failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Arranges for a message to be spoken the next time somebody is actually
        /// at the machine.
        /// </summary>
        /// <remarks>
        /// A one-shot due NOW with <c>requiresPresence</c> is the whole mechanism:
        /// the trigger engine holds anything due while the user is away and
        /// retries it every tick until its grace runs out. So "when he gets back"
        /// needs no new machinery, and it inherits quiet hours and the busy check
        /// — an announcement over the top of a live conversation is the failure
        /// PresenceGate exists to prevent.
        ///
        /// The grace is the staleness rule: past it, the message stops being worth
        /// interrupting anyone for and stays in the call log instead.
        /// </remarks>
        private void ArmSpokenFallback(CallRecord record)
        {
            triggers.AddOneShot(
                // Keyed by the call, so two messages taken in one evening are two
                // separate announcements rather than one overwriting the other.
                "call:deliver:" + record.Key,
                DateTime.Now + SpokenHeadStart,
                () => SpeakAsync(record.Key),
                grace: SpeakWithin,
                // A message from a stranger at 2am is still not worth waking
                // anybody for; it will keep until morning, and the grace is long
                // enough that it will still be there.
                respectQuietHours: true,
                requiresPresence: true);
        }

        /// <summary>
        /// Says one message out loud, unless the text already got there.
        /// </summary>
        private async Task SpeakAsync(string key)
        {
            // RE-READ FROM DISK RATHER THAN TRUSTING THE RECORD WE CLOSED OVER.
            // Between arming this and it firing, the text may have gone, the app
            // may have restarted, or the same message may have been spoken by the
            // resume path. The flag on disk is the only current answer, and
            // MarkDelivered below settles the race properly by refusing the
            // second caller.
            CallRecord now = Find(key);
            if (now == null || !now.HasUndeliveredMessage) return;

            // Claim it BEFORE speaking. If speaking throws halfway, having said
            // most of it and marked it delivered is better than a loop that says
            // it again on the next tick.
            if (!CallLogStore.MarkDelivered(key, "spoken")) return;

            string line = "While you were out, " + now.SpokenMessage();
            Console.WriteLine("[call/deliver] speaking: " + line);

            if (announce == null) return;
            await announce(line).ConfigureAwait(false);
        }

        private static CallRecord Find(string key)
        {
            foreach (CallRecord r in CallLogStore.Load())
            {
                if (r.Key == key) return r;
            }
            return null;
        }

        /// <summary>
        /// Picks up messages that were never delivered before the last shutdown.
        /// </summary>
        /// <remarks>
        /// THEY GO THROUGH THE SAME TWO CHANNELS A FRESH CALL USES, rather than
        /// being folded into the startup catch-up sentence.
        ///
        /// The first version did fold them in, and it was wrong in a way worth
        /// recording. ResumeSummary is spoken by Program with no callback, so
        /// each message had to be marked delivered as it was ADDED — before
        /// anything had been said. Two consequences, both bad: a message
        /// recovered after a crash was never TEXTED, only queued for speech,
        /// which is precisely backwards for the case that matters (he is out, the
        /// app restarted, and the spoken line is the one channel that cannot
        /// reach him); and if the catch-up's grace expired while he was away, the
        /// message was marked delivered having been neither spoken nor sent.
        ///
        /// Handing them to OnCallEndedAsync instead means a restored message is
        /// texted like any other, marked only on a confirmed send, and spoken
        /// only if the text did not land. The cost is that it arrives as its own
        /// spoken line rather than inside the one catch-up sentence — which for
        /// something a stranger asked to be passed on is arguably where it
        /// belongs anyway.
        /// </remarks>
        public ResumeSummary Restore()
        {
            var summary = new ResumeSummary();

            List<CallRecord> waiting = CallLogStore.Undelivered(SpeakWithin);
            if (waiting.Count == 0) return summary;

            foreach (CallRecord record in waiting)
            {
                // Resumed, not Missed: logged rather than spoken, because
                // delivery is about to announce it properly on its own.
                summary.Resumed.Add(
                    $"an undelivered message from {record.Caller ?? "an unknown caller"}");
            }

            _ = Task.Run(async () =>
            {
                // ONE AT A TIME. Each text drives a scratch tab in the shared
                // browser, and three at once would be three tabs fighting over
                // one composer in one Google Voice session.
                foreach (CallRecord record in waiting)
                {
                    try { await OnCallEndedAsync(record).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[call/deliver] restoring a message failed: {ex.Message}");
                    }
                }
            });

            return summary;
        }

        // Last four only. This goes in a log file that gets pasted into chats.
        private static string Mask(string number) =>
            number != null && number.Length >= 4
                ? "..." + number.Substring(number.Length - 4)
                : "the configured number";
    }
}
