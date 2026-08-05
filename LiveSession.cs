using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Personal_Assistant.Configuration;
using Personal_Assistant.Diagnostics;
using Personal_Assistant.Dispatch;
using Personal_Assistant.GeminiClient;
using Personal_Assistant.LiveAudio;
using Personal_Assistant.SpeechManager;

namespace Personal_Assistant.Live
{
    // Bounds on a single Live conversation. These are the watchdog, and the
    // watchdog is the only thing standing between this build and the one failure
    // mode that can actually exhaust the free tier: not steady-state usage, but a
    // socket that gets stuck open streaming silence at ~32 input tokens/second,
    // which is ~115k tokens an hour with nobody in the room.
    public sealed class LiveSessionLimits
    {
        // Wall clock from ConnectAsync to close, no matter what is happening.
        // Comfortably under the API's own 15-minute audio-only session cap so we
        // are never the party that finds out what the server does at the limit.
        public TimeSpan HardCap { get; set; } =
            LaithConfig.Seconds("LiveHardCapSeconds", 600, 30, 870);

        // How long the user may go quiet — while actually able to be heard, see
        // IdleElapsed — before the conversation closes. local-laith's measured
        // follow-up window, so follow-ups don't need re-waking.
        public TimeSpan IdleWindow { get; set; } =
            LaithConfig.Seconds("LiveIdleSeconds", 12, 3, 120);

        // A handler that never returns must not become a stuck session by proxy.
        public TimeSpan ToolTimeout { get; set; } =
            LaithConfig.Seconds("LiveToolTimeoutSeconds", 30, 5, 120);

        // How often the watchdog re-checks. Fine enough that the idle accumulator
        // tracks reality, coarse enough to be free.
        public TimeSpan TickInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    }

    // Why a session ended. Distinguishes the ordinary exits from the ones that
    // should trip Phase 5's fallback, and names the one that must be loud.
    public enum LiveSessionOutcome
    {
        /// <summary>User went quiet for IdleWindow — the normal end of a conversation.</summary>
        Idle,
        /// <summary>Server closed the socket, or sent goAway and then closed.</summary>
        ServerClosed,
        /// <summary>The app is shutting down / the caller cancelled.</summary>
        Cancelled,
        /// <summary>Wall-clock cap tripped. Should never happen in normal use.</summary>
        HardCap,
        /// <summary>Handshake failed, or the session died mid-turn.</summary>
        Faulted,
    }

    // Owns ONE conversation, from the wake word to close.
    //
    // This is the wiring between LiveClient (protocol) and LiveAudio (devices),
    // and it is the third implementation of the provider seam IntentDispatcher
    // already defines — except that it owns audio end to end, so it bypasses
    // DispatchAsync and calls RunToolByNameAsync directly. No tool execution or
    // argument validation is reimplemented here; TryValidate still runs, inside
    // RunToolByNameAsync, exactly as it does on the turn-based path.
    //
    // Lifetime rules, in order of importance:
    //
    //   1. The socket closes on EVERY exit path. RunConversationAsync's finally
    //      disposes the client, Dispose() disposes it again idempotently, and the
    //      hard cap lives on the CancellationTokenSource itself rather than in a
    //      branch somebody could forget to write. There is no arrangement of
    //      events that leaves this object holding an open socket.
    //   2. The idle clock is never advanced by the assistant. A talkative reply
    //      that reset it would keep its own session alive forever, which is the
    //      leak, not a feature.
    //   3. Every open and close is logged with duration and audio totals, so a
    //      session that misbehaves is visible rather than silent.
    public sealed class LiveSession : IDisposable
    {
        private readonly LiveSessionOptions options;
        private readonly LiveSessionLimits limits;
        private readonly IntentDispatcher dispatcher;
        private readonly CommandContext context;
        private readonly SpeechService speech;

        // Optional so the smoke harnesses can construct a session without one.
        // Null means the close line still prints; only the cumulative totals go
        // missing, which is the right way round — the accounting must never be
        // the reason a conversation can't run.
        private readonly LatencyTracker latency;

        private LiveClient client;
        private LiveAudioPipeline audio;

        // Audio frames and activity markers share ONE queue drained by ONE task.
        // Both ordering constraints are load-bearing: frames must reach the model
        // in capture order, and activityStart must precede the pre-roll flush that
        // follows it (LiveAudioCapture raises UploadGateOpened before flushing,
        // which only helps if the two don't then race through separate sends).
        private readonly BlockingCollection<UplinkItem> uplink =
            new BlockingCollection<UplinkItem>(new ConcurrentQueue<UplinkItem>(), UplinkCapacity);

        // ~4 s of 20 ms frames. A socket that stalls longer than that has failed;
        // buffering more would only delay noticing while growing the heap.
        private const int UplinkCapacity = 200;

        private readonly TaskCompletionSource<LiveSessionOutcome> finished =
            new TaskCompletionSource<LiveSessionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        private CancellationTokenSource sessionCts;
        private CancellationToken callerToken;
        private CancellationTokenRegistration capReg;
        private Task uplinkPump;
        private Task watchdog;

        // Idle accounting. Milliseconds accumulated while the user could have
        // spoken and didn't — see IdleElapsed for why it is not simply a clock.
        private long idleMs;
        private DateTime lastTickUtc;

        // True between the first audio chunk of a model turn and its playback
        // draining. Read by the watchdog and the barge-in path.
        private volatile bool assistantTurnActive;
        private volatile bool toolRunning;

        // The user's turn — ONE signal, one meaning, whichever endpointer is in
        // charge.
        //
        // The two modes used to be branched on at five call sites, each
        // compensating for the other's compensation: the gate counted turns under
        // client endpointing, transcripts counted them under server VAD, and the
        // watchdog had to know which was which to decide whether the user held the
        // floor. But the modes differ only in WHO NOTICES the user speaking — the
        // energy gate, or the transcripts the server's own endpointer produces.
        // What a turn MEANS is identical either way, so it lives in
        // UserTurnStarted/UserTurnEnded and nothing downstream asks about the mode.
        private volatile bool userTurnActive;

        // "Is the user audibly speaking right now?" — the one question the two
        // modes genuinely answer differently. Bound once in HookEvents and asked
        // only by the watchdog, so this and the wiring beside it are the last two
        // places the endpointing mode is consulted at all.
        private Func<bool> userIsSpeaking = () => false;

        // When the last input transcript arrived, for the server-VAD answer to
        // that question. Ticks rather than DateTime because a struct can't be
        // written atomically across threads.
        private long lastUserSpeechTicks;

        // Latched when the model's own transcript says "49" / "Laith": from that
        // point in the reply, anything the keyword spotter hears could be the
        // assistant coming back through the speakers, so barge-in stands down.
        // Without it the assistant cuts itself off whenever it says its own name.
        private volatile bool replyNamesAssistant;

        // Set the moment a barge-in cuts a reply, cleared when the user's next
        // turn opens. Audio the model is still streaming for the abandoned turn
        // must not restart playback we just flushed.
        private volatile bool suppressAssistantAudio;

        // Latched by LiveClient's SetupComplete. It is what tells a conversation
        // that ENDED from a handshake that never happened — see the Closed
        // handler, where the difference decides whether the fallback fires.
        private volatile bool setupCompleted;

        // Tool calls the server withdrew (toolCallCancellation). Results for
        // these are dropped rather than sent.
        private readonly HashSet<string> cancelledToolCalls = new HashSet<string>(StringComparer.Ordinal);
        private readonly object cancelledLock = new object();

        private readonly Stopwatch sessionClock = new Stopwatch();

        // Wall-clock open time, for the accounting line. The Stopwatch measures
        // the duration; this is what says WHEN, which is what you need to match
        // a session against a quota dashboard or against "it stopped answering
        // around three".
        private DateTime openedLocal;

        private long assistantAudioBytes;
        private int toolCallsRun;
        private int turns;
        private int disposed;
        private int outcomeSet;

        // Snapshotted in ShutdownAsync, because the close log is written after the
        // capture device it would otherwise be read from has been disposed.
        private long uploadedBytes;

        // The most recent thing the model transcribed the user as saying. Handlers
        // put this on the speech bubble as the "you said" label; on this path
        // there is no Azure STT result to take it from.
        private volatile string lastInputTranscript = string.Empty;

        // When the last chunk of model audio arrived, for the stalled-turn rescue
        // in the watchdog. Ticks rather than DateTime because a struct can't be
        // written atomically across threads.
        private long lastAssistantAudioTicks;

        // A model turn with no audio for this long, and no turnComplete, is not
        // coming back. Long enough to survive ordinary network jitter between
        // chunks, short enough that the user isn't left staring at a dead mic.
        private static readonly TimeSpan AssistantAudioStallTimeout = TimeSpan.FromSeconds(5);

        // ---- speech bubble --------------------------------------------------
        //
        // On the turn-based path the bubble is posted by SpeechService.Say, which
        // this path never calls — the Live model does its own TTS. So the bubble
        // is driven from output transcripts instead: post on the first fragment of
        // a reply, grow it as more arrive, retract when the speakers go quiet.
        //
        // There is no Azure synth here to flip the state dict that normally
        // retracts it, which is exactly what HideBubble exists for.
        //
        // Every one of those Python calls takes the GIL, and they used to run on
        // LiveClient's receive loop — the same single thread that decodes the
        // model's audio and hands it to playback. A reply produces roughly forty
        // transcript fragments, so that thread was acquiring the GIL up to ~8
        // times a second while it was the only thing keeping the voice fed. The
        // bubble is decoration; it must never be able to stutter the audio. So it
        // gets its own thread, and the receive loop only ever sets fields.
        //
        // Latest-wins rather than a queue, because the bubble has no history —
        // only a current appearance. Intermediate states are worth nothing, and a
        // queue would just accumulate lag behind a slow render.
        private readonly object bubbleSync = new object();
        private readonly StringBuilder replyText = new StringBuilder();

        // The user's current utterance, accumulated across streamed fragments.
        private readonly StringBuilder inputText = new StringBuilder();
        private bool inputTurnClosed = true;

        // The appearance the bubble SHOULD have. The pump reconciles the real one
        // to this and tracks what is actually on screen itself, which is what lets
        // an arbitrary run of post/grow/hide collapse to the single right call.
        // The text is read out of replyText under this lock at render time, so a
        // growing reply costs one string per RENDER rather than one per fragment.
        private bool bubbleWanted;
        private string bubbleLabel = string.Empty;
        private long bubbleVersion;

        private Thread bubblePump;
        private readonly ManualResetEventSlim bubbleSignal = new ManualResetEventSlim(false);
        private volatile bool bubblePumpStopping;

        // Fragments arrive per-word, and re-rendering the window forty times for
        // one reply is wasted work even off the audio thread. The pump paces
        // itself to this — and because it always renders the LATEST state rather
        // than the one it was woken for, pacing can never leave the tail of a
        // reply off the bubble the way a plain throttle could.
        private static readonly TimeSpan BubbleUpdateInterval = TimeSpan.FromMilliseconds(120);

        public LiveSession(
            IntentDispatcher dispatcher,
            CommandContext context,
            IReadOnlyList<ToolDefinition> tools,
            SpeechService speech = null,
            LiveSessionLimits limits = null,
            LiveSessionOptions options = null,
            LatencyTracker latency = null)
        {
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.speech = speech;
            this.latency = latency;
            this.limits = limits ?? new LiveSessionLimits();
            this.options = options ?? new LiveSessionOptions();
            if (this.options.Tools == null) this.options.Tools = tools;
            this.options.SystemInstruction += BuildNameHints(this.context);

            // Without grounding the model will still answer questions about the
            // world — from training data, confidently, with no sign anything is
            // missing. That is how "no announced release date for Re:Zero season
            // 4 part 2" got said out loud as fact. If search is off, say so.
            if (!this.options.EnableGoogleSearch)
            {
                this.options.SystemInstruction += GeminiService.NoSearchCaveat +
                    " Offer to open a browser search with open_web_search instead.";
            }
        }

        // The same model does transcription and reasoning, so the proper nouns it
        // is likely to mishear are worth naming up front. Observed failures:
        // "Layth" -> "to life", "L.A.I.T.H. 49" -> "Vade forty-nine",
        // "Layth" -> "Leith"/"Lathe". There is no phrase-list/speech-context
        // parameter on the Live API the way Azure has boost_phrases, so the
        // system instruction is the only place to bias this.
        private static string BuildNameHints(CommandContext context)
        {
            var sb = new StringBuilder();

            // Native-audio models pick the language themselves and, per Google's
            // docs, "don't support explicitly setting the language code" — the
            // system instruction is the only documented way to restrict it. Left
            // free, this model has transcribed Layth's English as Italian
            // ("un po'.", "l'ita.") and then answered the wrong question.
            sb.Append(" Language: Layth speaks American English, only ever English. ");
            sb.Append("Always interpret the audio as English and always reply in English, even when a ");
            sb.Append("word is unclear or sounds foreign — an English word you are unsure of is far ");
            sb.Append("more likely than a switch to another language. Never transcribe or answer in ");
            sb.Append("any other language.");

            sb.Append(" Speech recognition note: the user is Layth (rhymes with \"faith\"), and you are ");
            sb.Append("L.A.I.T.H.49, spoken \"Laith forty-nine\". Audio that sounds like \"Leith\", \"Lathe\", ");
            sb.Append("\"to life\", \"Vade\" or \"Faith\" is almost certainly one of those two names — resolve it ");
            sb.Append("that way rather than transcribing the homophone literally.");

            if (context?.Contacts != null && context.Contacts.Count > 0)
            {
                sb.Append(" Known contact names, which are the only valid values for a contact argument: ");
                sb.Append(string.Join(", ", context.Contacts.Keys));
                sb.Append(". Map a name you hear to the closest one of these.");
            }

            return sb.ToString();
        }

        /// <summary>Why the last conversation ended. Meaningful once RunConversationAsync returns.</summary>
        public LiveSessionOutcome Outcome { get; private set; } = LiveSessionOutcome.Faulted;

        /// <summary>True once the user has cut a reply with the wakeword at least
        /// once in this conversation. Diagnostic only — a barge-in continues the
        /// session rather than ending it, which is why the turn-based loop's
        /// `listenImmediately` flag has no equivalent here.</summary>
        public bool BargedIn { get; private set; }

        // Runs one conversation to completion. Returns true when it ended the way
        // a conversation is supposed to end; false is what trips Phase 5's
        // fallback to the turn-based path.
        public async Task<bool> RunConversationAsync(CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(LiveSession));

            // The hard cap is the CancellationTokenSource's own timer rather than
            // a check inside a loop. Nothing has to remember to enforce it, and it
            // stays enforced through every await in this file including ones added
            // later.
            callerToken = cancellationToken;
            sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sessionCts.CancelAfter(limits.HardCap);
            CancellationToken ct = sessionCts.Token;

            // Ending the conversation hangs off the token, not off the watchdog
            // task. A watchdog that never got scheduled, threw on its first tick,
            // or was starved cannot leave RunConversationAsync awaiting forever:
            // the timer inside the CancellationTokenSource fires this regardless.
            capReg = ct.Register(() =>
            {
                if (Volatile.Read(ref outcomeSet) != 0) return;   // ordinary close already won

                if (callerToken.IsCancellationRequested)
                {
                    Complete(LiveSessionOutcome.Cancelled);
                    return;
                }

                // Nothing else cancelled it, so this is the wall-clock cap. This
                // is THE failure the class exists to prevent; it does not get a
                // quiet one-line log.
                Console.WriteLine(new string('!', 72));
                Console.WriteLine(
                    $"[live-session] HARD CAP: session hit {limits.HardCap.TotalSeconds:0}s and was " +
                    "force-closed. A conversation should never reach this — it means the session " +
                    "never ended on its own. Check the idle path.");
                Console.WriteLine(new string('!', 72));
                Complete(LiveSessionOutcome.HardCap);
            });

            sessionClock.Restart();
            openedLocal = DateTime.Now;
            lastTickUtc = DateTime.UtcNow;
            Interlocked.Exchange(ref idleMs, 0);

            Console.WriteLine($"[session] opening at {openedLocal:HH:mm:ss}");
            Console.WriteLine(
                $"[live-session] opening — model '{options.Model}', " +
                $"voice {(string.IsNullOrEmpty(options.Voice) ? "(server default — LAITH_LIVE_VOICE unset)" : options.Voice)}, " +
                $"endpointing {(options.ManualActivityDetection ? "client (energy gate)" : "server VAD")}, " +
                $"grounding {(options.EnableGoogleSearch ? "on" : "OFF")}, " +
                $"hard cap {limits.HardCap.TotalSeconds:0}s, idle window {limits.IdleWindow.TotalSeconds:0}s");

            try
            {
                // Before HookEvents, so the first transcript fragment of the
                // session already has somewhere to go.
                StartBubblePump();

                client = new LiveClient(options);
                audio = new LiveAudioPipeline();

                // The energy gate must not endpoint when the server is doing it —
                // otherwise the server's VAD just sees the silence the gate made,
                // and the truncation this was meant to fix survives the change.
                audio.Capture.UploadContinuously = !options.ManualActivityDetection;

                HookEvents();

                await client.ConnectAsync(ct).ConfigureAwait(false);

                // Only start the microphone once the server has acknowledged
                // setup. Capturing earlier would bank pre-roll against a session
                // that might never open, and uploading before setupComplete is a
                // protocol error the server drops the connection over.
                uplinkPump = Task.Run(() => UplinkPumpAsync(ct));
                watchdog = Task.Run(() => WatchdogAsync(ct));
                await audio.StartAsync().ConfigureAwait(false);

                LiveSessionOutcome outcome = await finished.Task.ConfigureAwait(false);
                return IsClean(outcome);
            }
            // Both catches go through Complete rather than assigning Outcome
            // directly. Assigning it looks equivalent and is not: closing the
            // socket below raises Closed, whose handler calls Complete, and with
            // no outcome recorded yet that would relabel a session that died
            // during the handshake as an ordinary ServerClosed — i.e. as clean.
            // Claiming the outcome here is what makes the first cause the
            // reported one.
            catch (OperationCanceledException)
            {
                Complete(cancellationToken.IsCancellationRequested
                    ? LiveSessionOutcome.Cancelled
                    : LiveSessionOutcome.HardCap);
                return IsClean(Outcome);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[live-session] failed: {ex.Message}");
                Complete(LiveSessionOutcome.Faulted);
                return IsClean(Outcome);
            }
            finally
            {
                // Every path — clean end, cancellation, mid-turn exception —
                // lands here, and this is what guarantees the socket does not
                // outlive the conversation.
                await ShutdownAsync().ConfigureAwait(false);
                LogClose();
            }
        }

        // Injects a complete user turn as text instead of audio.
        //
        // Not part of the spoken path — it exists so a session can be driven
        // without a microphone, which is the only way to exercise a tool round
        // trip in a harness or to tell "the socket is broken" apart from "the
        // microphone is broken" when one of them is. Counts as a real user turn,
        // because it is one.
        public Task SendTextAsync(string text)
        {
            if (client == null || !client.IsOpen || string.IsNullOrWhiteSpace(text))
                return Task.CompletedTask;

            lastInputTranscript = text;

            // A complete turn, start to finish, so it counts as one exactly as a
            // spoken turn does. Safe in either mode precisely because the activity
            // markers live in the wiring rather than in these two: a text turn
            // carries its own turnComplete and must not be bracketed by them.
            UserTurnStarted();
            UserTurnEnded();

            return client.SendTextAsync(text, sessionCts.Token);
        }

        // ---- events --------------------------------------------------------

        private void HookEvents()
        {
            LiveAudioCapture capture = audio.Capture;

            capture.FrameReady += frame => Enqueue(UplinkItem.Audio(frame));

            client.AudioReceived += OnAudioReceived;
            client.OutputTranscript += OnOutputTranscript;
            client.InputTranscript += text =>
            {
                if (!string.IsNullOrWhiteSpace(text)) AppendInputTranscript(text);
                Console.WriteLine($"[live-session] heard: {text}");
            };

            // ---- who observes the user's turn -------------------------------
            //
            // The ONE place the endpointing mode decides anything. Both branches
            // bind the same two signals to different observers; everything
            // downstream — the idle budget, the turn count, the floor test — reads
            // those signals and never this flag.
            if (options.ManualActivityDetection)
            {
                // Client endpointing: the energy gate decides where a turn ends,
                // and the server has to be told, explicitly and in both
                // directions. With automaticActivityDetection disabled and no
                // markers arriving, the model waits forever and the session looks
                // hung — the single most likely "it just sits there" bug.
                //
                // The markers are enqueued outside UserTurnStarted/Ended and
                // unconditionally, not folded into them: those are idempotent, and
                // a marker that got skipped because the turn had already been
                // closed by something else is exactly the half-sent pair that
                // wedges the model.
                //
                // Ordering is load-bearing. LiveAudioCapture raises Opened BEFORE
                // flushing its pre-roll, and the marker goes through the same
                // single uplink queue as the frames, so activityStart cannot be
                // overtaken by the onset audio it is supposed to bound.
                capture.UploadGateOpened += () =>
                {
                    UserTurnStarted();
                    Enqueue(UplinkItem.Activity(ActivityMarker.Start));
                };
                capture.UploadGateClosed += () =>
                {
                    UserTurnEnded();
                    Enqueue(UplinkItem.Activity(ActivityMarker.End));
                };

                // The gate is open for exactly as long as the user is audible.
                userIsSpeaking = () => audio.Capture.IsUploading;
            }
            else
            {
                // Server VAD: the gate uploads continuously and so says nothing
                // about turns — it opens and closes on assistant-audio boundaries.
                // The server's endpointer decides instead, and the first thing we
                // ever see of that decision is a transcript fragment. No activity
                // markers here; sending them is a protocol error.
                //
                // Same emptiness test the "heard:" handler uses, so a stray blank
                // transcript can't open a turn and inflate the turn count.
                client.InputTranscript += text =>
                {
                    if (!string.IsNullOrWhiteSpace(text)) UserTurnStarted();
                };

                // Nothing announces that the server's endpointer closed a turn, so
                // silence has to stand in for it — the same span the client gate
                // uses for the same purpose, from the same setting.
                userIsSpeaking = () =>
                    DateTime.UtcNow - new DateTime(Volatile.Read(ref lastUserSpeechTicks), DateTimeKind.Utc)
                        < TimeSpan.FromMilliseconds(LiveAudioCapture.HangoverMs);
            }

            client.Interrupted += () =>
            {
                Console.WriteLine("[live-session] model turn interrupted — dropping buffered audio");
                audio.Interrupt();

                // The buffered audio is gone, so PlaybackFinished may never fire
                // for this turn. Retract here or the cut-off reply stays on screen
                // until something else supersedes it.
                ClearBubble();
            };

            client.TurnComplete += () =>
            {
                // Releases any audio still held back for the playback lead, and
                // lets the playback monitor finish the turn.
                //
                // The turn is NOT over here and assistantTurnActive stays set:
                // turnComplete means the model stopped generating, while the
                // speakers are still working through everything already buffered.
                // Clearing it here would hand the floor back mid-sentence and end
                // the barge-in watch while the reply is still audible.
                audio.EndAssistantAudio();

                // No bubble flush here any more. The pump always renders the
                // LATEST state rather than the one it was woken for, so the final
                // fragment lands on its own — a flush would only be re-asserting
                // text the pump already has.
            };

            audio.Playback.PlaybackStarted += OnPlaybackStarted;
            audio.Playback.PlaybackFinished += OnPlaybackFinished;

            client.ToolCallReceived += calls => _ = HandleToolCallAsync(calls);
            client.ToolCallCancelled += ids =>
            {
                lock (cancelledLock)
                {
                    foreach (string id in ids) cancelledToolCalls.Add(id);
                }
                Console.WriteLine($"[live-session] server cancelled {ids.Count} tool call(s)");
            };

            client.GoingAway += left =>
                Console.WriteLine($"[live-session] server going away in {left?.ToString() ?? "unknown"}");

            client.SetupComplete += () => setupCompleted = true;

            client.Closed += (status, reason) =>
            {
                Console.WriteLine($"[live-session] socket closed ({status?.ToString() ?? "no status"}): {reason}");

                // A close BEFORE setupComplete is a handshake that failed, not a
                // conversation that ended, and the difference is the whole of
                // Phase 5's fallback: ServerClosed is clean, Faulted is not.
                //
                // This is not theoretical and it is not covered by claiming the
                // outcome in the catch below. An invalid API key makes the SERVER
                // close the socket, and that close arrives and raises this handler
                // BEFORE ConnectAsync's await observes the failure and throws — so
                // Complete lands here first, first-outcome-wins keeps it, and a bad
                // key reported itself as a clean conversation that simply had
                // nothing in it. The assistant would have greeted the user and then
                // gone silent, every single time, with no fallback and no notice.
                Complete(setupCompleted
                    ? LiveSessionOutcome.ServerClosed
                    : LiveSessionOutcome.Faulted);
            };
        }

        // ---- the user's turn -----------------------------------------------

        // The user has started speaking. Idempotent, and deliberately free of any
        // mode test: whichever observer calls it, a turn starting means the same
        // four things.
        private void UserTurnStarted()
        {
            Volatile.Write(ref lastUserSpeechTicks, DateTime.UtcNow.Ticks);
            if (userTurnActive) return;
            userTurnActive = true;

            // A real user turn, and the ONLY thing that resets the idle budget.
            // Assistant output must never reset it, or a talkative reply keeps its
            // own session alive forever — see WatchdogAsync.
            Interlocked.Exchange(ref idleMs, 0);
            Interlocked.Increment(ref turns);

            // The user is talking again, so whatever the model was still streaming
            // for the turn they cut is no longer wanted.
            suppressAssistantAudio = false;
        }

        // The user's turn is over. Idempotent for the same reason.
        private void UserTurnEnded()
        {
            userTurnActive = false;
        }

        private void OnAudioReceived(byte[] pcm)
        {
            if (pcm == null || pcm.Length == 0) return;

            // A barge-in already flushed this turn; re-enqueuing what the model is
            // still sending would start it playing again over the user.
            if (suppressAssistantAudio) return;

            Interlocked.Add(ref assistantAudioBytes, pcm.Length);
            Volatile.Write(ref lastAssistantAudioTicks, DateTime.UtcNow.Ticks);

            if (!assistantTurnActive)
            {
                assistantTurnActive = true;
                replyNamesAssistant = false;
                StartWakewordWatch();
            }

            // Shuts the mic gate before the first sound reaches the room, which is
            // what makes speaker echo structurally impossible instead of a
            // threshold's problem. When Phase 4b lands BeginSpeaking/EndSpeaking
            // on SpeechService, they route to this call site and OnPlaybackFinished.
            audio.EnqueueAssistantAudio(pcm);
        }

        private void OnOutputTranscript(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Console.WriteLine($"[live-session] said: {text}");

            // Latches for the rest of the reply: once the assistant has said its
            // own name, anything the spotter hears afterwards could be that.
            if (NamesAssistant(text)) replyNamesAssistant = true;

            ShowOrGrowBubble(text);
        }

        // Input transcripts stream in per-syllable — "in" / "tro" / "duce yourself
        // to Layth" is one utterance, not four. This used to overwrite, so the
        // bubble's "you said" label showed only the final fragment.
        //
        // Purely about the text now. Turn accounting used to be tangled in here
        // for server VAD only, which is what made `inputTurnClosed` mean two
        // unrelated things at once; UserTurnStarted owns that in both modes.
        private void AppendInputTranscript(string fragment)
        {
            lock (bubbleSync)
            {
                if (inputTurnClosed)
                {
                    inputText.Clear();
                    inputTurnClosed = false;
                }
                inputText.Append(fragment);
                lastInputTranscript = inputText.ToString().Trim();
            }
        }

        // All three of these only ever set fields and wake the pump. Nothing on
        // LiveClient's receive loop touches Python any more, which is the whole
        // point — see the bubble field block above.
        private void ShowOrGrowBubble(string fragment)
        {
            if (speech == null) return;

            lock (bubbleSync)
            {
                // A fragment is never empty, so an empty buffer means this is the
                // first of a reply — no separate "is the bubble up" flag to keep
                // in step with it.
                if (replyText.Length == 0)
                {
                    // The assistant is replying, so the user's turn is over: the
                    // next input fragment starts a fresh utterance rather than
                    // extending the one now shown on the bubble.
                    inputTurnClosed = true;
                }
                replyText.Append(fragment);

                bubbleWanted = true;
                bubbleLabel = lastInputTranscript;
                bubbleVersion++;
                bubbleSignal.Set();
            }
        }

        private void ClearBubble()
        {
            if (speech == null) return;

            lock (bubbleSync)
            {
                replyText.Clear();
                bubbleWanted = false;
                bubbleVersion++;
                bubbleSignal.Set();
            }
        }

        // ---- bubble pump ----------------------------------------------------

        private void StartBubblePump()
        {
            if (speech == null || bubblePump != null) return;

            bubblePumpStopping = false;
            bubblePump = new Thread(BubblePumpLoop)
            {
                IsBackground = true,
                Name = "laith-live-bubble",
            };
            bubblePump.Start();
        }

        // Reconciles the real bubble to the wanted one, forever, on its own
        // thread. It owns `shown` rather than reading it from shared state,
        // because it is the only thing that can change what is on screen — which
        // is what lets any run of post/grow/hide collapse to the one call that
        // ends up being right.
        private void BubblePumpLoop()
        {
            bool shown = false;
            long rendered = -1;
            DateTime lastRenderUtc = DateTime.MinValue;

            try
            {
                while (true)
                {
                    bubbleSignal.Wait();
                    if (bubblePumpStopping) break;

                    bool wanted;
                    string label, text;
                    long version;
                    lock (bubbleSync)
                    {
                        version = bubbleVersion;

                        // Checked and reset under the same lock the setters take,
                        // so a change made between the read and the reset cannot
                        // be lost — the signal it set survives.
                        if (version == rendered)
                        {
                            bubbleSignal.Reset();
                            continue;
                        }

                        wanted = bubbleWanted;
                        label = bubbleLabel;
                        text = wanted ? replyText.ToString() : string.Empty;
                    }

                    // Pace only the case that actually repeats — text growing on a
                    // bubble already up. Posting and retracting happen once each
                    // and a delay on either would read as lag.
                    //
                    // The signal is deliberately NOT reset before sleeping: the
                    // loop comes straight back, re-reads, and renders whatever the
                    // latest state is by then. That is why pacing here can't drop
                    // the tail of a reply the way the old throttle could, and why
                    // turnComplete no longer needs to flush anything.
                    if (shown && wanted)
                    {
                        TimeSpan since = DateTime.UtcNow - lastRenderUtc;
                        if (since < BubbleUpdateInterval)
                        {
                            Thread.Sleep(BubbleUpdateInterval - since);
                            continue;
                        }
                    }

                    try
                    {
                        if (!wanted)
                        {
                            if (shown) { speech.HideBubble(); shown = false; }
                        }
                        else if (!shown)
                        {
                            speech.SpeechBubble(label, text);
                            shown = true;
                        }
                        else
                        {
                            speech.UpdateBubble(label, text);
                        }
                    }
                    catch (Exception ex)
                    {
                        // The bubble is decoration. A Python-side failure must
                        // never take down a conversation that is otherwise working
                        // — and must not kill the pump either, or every later
                        // bubble in the session goes missing too.
                        Console.WriteLine($"[bubble] live-path update failed: {ex.Message}");
                    }

                    rendered = version;
                    lastRenderUtc = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[bubble] pump stopped: {ex.Message}");
            }
            finally
            {
                // A bubble must never outlive the conversation that posted it, and
                // on this path nothing else would ever retract it.
                if (shown)
                {
                    try { speech.HideBubble(); } catch { }
                }
            }
        }

        // Stops the pump where it stands rather than waiting for it to drain — the
        // retraction ShutdownAsync just asked for is guaranteed by the loop's
        // finally, not by processing the queue, so there is nothing to drain FOR.
        private void StopBubblePump()
        {
            Thread pump = bubblePump;
            if (pump == null) return;
            bubblePump = null;

            bubblePumpStopping = true;
            bubbleSignal.Set();

            // Bounded, because a wedged Python side must not stop the session
            // closing — the finally above is what guarantees the retraction, and a
            // pump that can't run is a pump whose bubble is already gone with it.
            try { pump.Join(TimeSpan.FromSeconds(2)); } catch { }
        }

        private void OnPlaybackStarted()
        {
            Console.WriteLine("[live-session] assistant audio audible");
        }

        private void OnPlaybackFinished()
        {
            // LiveAudioPipeline has already wired this to Capture.EndAssistantAudio,
            // which starts the speaker-tail countdown that reopens the mic.
            assistantTurnActive = false;
            StopWakewordWatch();

            // Retract only once the speakers are actually quiet. Transcripts run
            // ahead of audio, so hiding on turnComplete would pull the bubble off
            // screen while the reply was still being spoken.
            ClearBubble();
        }

        // ---- uplink --------------------------------------------------------

        private enum ActivityMarker { Start, End }

        private struct UplinkItem
        {
            public byte[] Frame;
            public ActivityMarker Marker;
            public bool IsMarker;

            public static UplinkItem Audio(byte[] frame) =>
                new UplinkItem { Frame = frame };

            public static UplinkItem Activity(ActivityMarker marker) =>
                new UplinkItem { Marker = marker, IsMarker = true };
        }

        private void Enqueue(UplinkItem item)
        {
            if (uplink.IsAddingCompleted) return;
            try
            {
                if (uplink.TryAdd(item)) return;
            }
            catch (InvalidOperationException)
            {
                return; // completed between the check and the add
            }

            // Full means the socket is not draining. Dropping the newest frame
            // keeps the utterance's onset, which is the part that matters, and
            // says so rather than silently losing audio.
            Console.WriteLine("[live-session] uplink queue full — dropping a frame (socket stalled?)");
        }

        // One task, one send at a time, in queue order. LiveClient serialises
        // sends internally too, but a semaphore does not preserve arrival order —
        // only a single consumer does.
        private async Task UplinkPumpAsync(CancellationToken ct)
        {
            try
            {
                foreach (UplinkItem item in uplink.GetConsumingEnumerable(ct))
                {
                    if (!client.IsOpen) break;

                    if (item.IsMarker)
                    {
                        if (item.Marker == ActivityMarker.Start)
                            await client.SendActivityStartAsync(ct).ConfigureAwait(false);
                        else
                            await client.SendActivityEndAsync(ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await client.SendAudioAsync(item.Frame, item.Frame.Length, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[live-session] uplink stopped: {ex.Message}");
                Complete(LiveSessionOutcome.Faulted);
            }
        }

        // ---- tools ---------------------------------------------------------

        // Tool handlers on this branch still speak their own confirmations through
        // Azure TTS (Phase 4a reshapes that). Until then they are assistant audio
        // like any other, so the mic gate is held shut across the whole batch —
        // otherwise the model hears the confirmation and answers it.
        private async Task HandleToolCallAsync(IReadOnlyList<LiveFunctionCall> calls)
        {
            if (calls == null || calls.Count == 0) return;

            toolRunning = true;
            audio.Capture.BeginAssistantAudio();

            var results = new List<LiveFunctionResult>(calls.Count);
            try
            {
                foreach (LiveFunctionCall call in calls)
                {
                    if (IsCancelled(call.Id))
                    {
                        Console.WriteLine($"[live-session] skipping cancelled tool call {call}");
                        continue;
                    }

                    Console.WriteLine($"[live-session] tool call {call}");
                    context.RecognizedText = lastInputTranscript;

                    try
                    {
                        // RunToolByNameAsync validates through TryValidate and logs
                        // unknown names / bad args itself. Nothing to duplicate here.
                        //
                        // speak:false because the MODEL speaks on this path. The
                        // handler's answer comes back as data instead, and goes
                        // into the tool response below — without it the model has
                        // nothing to answer FROM and makes something up, which is
                        // how "what time is it" once returned 7:20 AM UTC for a
                        // handler that had correctly computed 2:20 AM local.
                        Task<ToolResult> run =
                            dispatcher.RunToolByNameAsync(call.Name, call.Args, speak: false);
                        Task done = await Task.WhenAny(run, Task.Delay(limits.ToolTimeout))
                                              .ConfigureAwait(false);
                        if (done != run)
                        {
                            Console.WriteLine(
                                $"[live-session] tool '{call.Name}' exceeded " +
                                $"{limits.ToolTimeout.TotalSeconds:0}s — replying without waiting");
                            results.Add(LiveFunctionResult.Error(call, "timed out"));
                            continue;
                        }

                        ToolResult toolResult = await run.ConfigureAwait(false) ?? ToolResult.None;
                        Interlocked.Increment(ref toolCallsRun);
                        results.Add(new LiveFunctionResult(
                            call.Id, call.Name, toolResult.ToResponse()));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[live-session] tool '{call.Name}' threw: {ex.Message}");
                        results.Add(LiveFunctionResult.Error(call, ex.Message));
                    }
                }

                // Drop anything withdrawn while it was running, then send ONE
                // toolResponse carrying the batch, id-matched.
                results.RemoveAll(r => IsCancelled(r.Id));
                if (results.Count > 0 && client.IsOpen)
                {
                    await client.SendToolResponseAsync(results, sessionCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[live-session] tool batch failed: {ex.Message}");
            }
            finally
            {
                audio.Capture.EndAssistantAudio();
                toolRunning = false;
            }
        }

        private bool IsCancelled(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            lock (cancelledLock) { return cancelledToolCalls.Contains(id); }
        }

        // ---- barge-in ------------------------------------------------------

        private CancellationTokenSource wakewordCts;
        private readonly object wakewordLock = new object();

        // Say "49" to cut a reply. This is the barge-in that survives loud
        // speakers, and it exists because the level-based one cannot: once the
        // speakers are loud enough, bleed and speech overlap in level and no
        // threshold separates them. The keyword spotter matches one acoustic
        // pattern instead of measuring loudness, so bleed doesn't fool it.
        private void StartWakewordWatch()
        {
            if (speech == null) return;

            CancellationTokenSource cts;
            lock (wakewordLock)
            {
                if (wakewordCts != null) return;
                cts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
                wakewordCts = cts;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    bool heard = await speech.WatchForWakewordAsync(cts.Token).ConfigureAwait(false);
                    if (!heard || cts.IsCancellationRequested) return;

                    if (replyNamesAssistant)
                    {
                        Console.WriteLine("[live-session] wakeword ignored — the reply says it itself");
                        return;
                    }

                    OnBargeIn();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Console.WriteLine($"[live-session] wakeword watch error: {ex.Message}");
                }
            });
        }

        private void StopWakewordWatch()
        {
            CancellationTokenSource cts;
            lock (wakewordLock)
            {
                cts = wakewordCts;
                wakewordCts = null;
            }
            if (cts == null) return;
            try { cts.Cancel(); } catch { }
            try { cts.Dispose(); } catch { }
            speech?.StopWakewordWatch();
        }

        private void OnBargeIn()
        {
            Console.WriteLine("[live-session] wakeword over the reply — cutting it off");
            BargedIn = true;

            // Drop what hasn't played. The model may keep streaming audio for the
            // turn it doesn't know was abandoned, so latch it out until the user's
            // next turn opens.
            suppressAssistantAudio = true;
            assistantTurnActive = false;
            audio.Interrupt();

            // local-laith restarts the capture here to throw away a buffer full of
            // the reply's own echo. Half duplex means there is no such buffer to
            // throw away — but there IS a 400 ms speaker-tail guard that would eat
            // the command riding in behind the wakeword, and the spotter reports
            // the keyword a few hundred ms late, so the user is already saying it.
            // Clearing the tail is the same intent pointed at the same failure:
            // "the barge-in was never picked up".
            audio.Capture.RestartCapture();

            // The user's next frames open the gate, which sends activityStart and
            // is what tells the server its turn was interrupted. No extra message.
            Interlocked.Exchange(ref idleMs, 0);
        }

        private static bool NamesAssistant(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return text.IndexOf("49", StringComparison.Ordinal) >= 0
                || text.IndexOf("laith", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("l.a.i.t.h", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ---- watchdog ------------------------------------------------------

        // Idle time is ACCUMULATED, not read off a clock, and only while the user
        // could actually have been heard.
        //
        // The rule the brief sets is that assistant output must never reset the
        // timer — a talkative reply that did would keep its own session alive
        // indefinitely, which is the leak. But the naive reading, "close if the
        // last user turn was more than IdleWindow ago", cuts the user off mid-reply
        // and then refuses them a chance to answer, because half duplex means the
        // microphone is shut for the whole reply. Those seconds are not the user
        // being quiet; they are seconds the user was structurally unable to speak.
        //
        // So: assistant activity neither resets the budget nor spends it. Only a
        // real user turn resets it, and only time when the floor was theirs counts
        // against it. A model that talks forever therefore never spends idle
        // budget at all — which is precisely why the hard cap exists and is
        // enforced by the CancellationTokenSource rather than by this loop.
        private async Task WatchdogAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(limits.TickInterval, ct).ConfigureAwait(false);

                    DateTime now = DateTime.UtcNow;
                    TimeSpan delta = now - lastTickUtc;
                    lastTickUtc = now;

                    // A model turn that stops mid-flight without ever sending
                    // turnComplete would otherwise wedge the session shut: the
                    // playback buffer never drains (draining needs EndAudioInput),
                    // so PlaybackFinished never fires, so the mic gate never
                    // reopens, so no user turn can arrive and the idle clock never
                    // runs. Nothing short of the hard cap would end it. Releasing
                    // the turn here turns that hang into a recovered conversation.
                    if (assistantTurnActive)
                    {
                        var since = now - new DateTime(Volatile.Read(ref lastAssistantAudioTicks), DateTimeKind.Utc);
                        if (since > AssistantAudioStallTimeout)
                        {
                            Console.WriteLine(
                                $"[live-session] model audio stalled {since.TotalSeconds:F1}s with no " +
                                "turnComplete — releasing the turn and reopening the mic");
                            audio.EndAssistantAudio();
                            assistantTurnActive = false;

                            // Draining normally retracts the bubble via
                            // PlaybackFinished, but a stall with nothing buffered
                            // means that already fired. Idempotent, so the ordinary
                            // path is unaffected.
                            ClearBubble();
                        }
                    }

                    // Nothing announces the end of a user turn under server VAD,
                    // and under client endpointing a gate that failed to close
                    // would hold the floor forever. Both are the same failure —
                    // a turn nobody closed — so both get the same close, off the
                    // one "is the user audibly speaking" observer.
                    //
                    // Without this the idle accumulator could never advance and
                    // every session would run to the hard cap, which is exactly
                    // the bug the mode-specific version of this test had.
                    if (userTurnActive && !userIsSpeaking()) UserTurnEnded();

                    bool userHasTheFloor =
                        !assistantTurnActive &&
                        !toolRunning &&
                        !audio.Capture.AssistantAudioPlaying &&
                        !audio.Playback.IsPlaying &&
                        // The floor is the user's when they are neither being
                        // talked over nor mid-turn themselves. One test, both
                        // endpointing modes — the observer above absorbed the
                        // difference.
                        !userTurnActive;

                    long elapsed = userHasTheFloor
                        ? Interlocked.Add(ref idleMs, (long)delta.TotalMilliseconds)
                        : Interlocked.Read(ref idleMs);

                    if (elapsed >= (long)limits.IdleWindow.TotalMilliseconds)
                    {
                        Console.WriteLine(
                            $"[live-session] no follow-up for {limits.IdleWindow.TotalSeconds:0}s — closing");
                        Complete(LiveSessionOutcome.Idle);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // The cap registration above still ends the session, so a dead
                // watchdog degrades the idle window to the hard cap rather than
                // leaking a socket.
                Console.WriteLine($"[live-session] watchdog failed — idle close is now the hard cap: {ex.Message}");
            }
        }

        // The ways a conversation is allowed to end. Anything else trips Phase 5's
        // fallback.
        private static bool IsClean(LiveSessionOutcome outcome) =>
            outcome == LiveSessionOutcome.Idle ||
            outcome == LiveSessionOutcome.Cancelled ||
            outcome == LiveSessionOutcome.ServerClosed;

        // First outcome wins. Everything downstream — the return value, the
        // fallback, the close log — reads exactly one of these.
        private void Complete(LiveSessionOutcome outcome)
        {
            if (Interlocked.Exchange(ref outcomeSet, 1) != 0) return;
            Outcome = outcome;
            finished.TrySetResult(outcome);
            try { sessionCts?.Cancel(); } catch { }
        }

        // ---- teardown ------------------------------------------------------

        private async Task ShutdownAsync()
        {
            try { uplink.CompleteAdding(); } catch { }
            StopWakewordWatch();

            // A bubble must never outlive the conversation that posted it — on a
            // mid-reply close nothing else would ever retract it. Ask first, then
            // stop the pump: StopBubblePump retracts whatever is still on screen
            // as it exits, so the retraction survives either way round.
            ClearBubble();
            StopBubblePump();

            // Read the totals while the devices are still alive — the close log
            // is written after they have been disposed.
            if (audio != null) uploadedBytes = audio.Capture.BytesUploaded;

            // Stop the microphone before the socket: a frame that arrives after
            // the close would only log an error.
            try { audio?.Capture?.Stop(); } catch { }

            if (uplinkPump != null)
            {
                try { await Task.WhenAny(uplinkPump, Task.Delay(2000)).ConfigureAwait(false); } catch { }
            }

            if (client != null)
            {
                try { await client.CloseAsync("conversation ended: " + Outcome).ConfigureAwait(false); }
                catch (Exception ex) { Console.WriteLine($"[live-session] close failed: {ex.Message}"); }
            }

            if (watchdog != null)
            {
                try { await Task.WhenAny(watchdog, Task.Delay(1000)).ConfigureAwait(false); } catch { }
            }

            // Disposal, not just closure. CloseAsync can itself fail; Dispose
            // aborts, and both are idempotent.
            try { client?.Dispose(); } catch { }
            try { audio?.Dispose(); } catch { }
            client = null;
            audio = null;

            try { capReg.Dispose(); } catch { }
            try { sessionCts?.Dispose(); } catch { }
            sessionCts = null;
        }

        private void LogClose()
        {
            sessionClock.Stop();
            double seconds = sessionClock.Elapsed.TotalSeconds;
            long down = Interlocked.Read(ref assistantAudioBytes);

            // Duration and audio totals on every close, so a session that never
            // closes is conspicuous by the absence of this line — and so the
            // uploaded-seconds figure can be checked against the wall clock when
            // a bill or a quota warning needs explaining.
            Console.WriteLine(
                $"[live-session] closed — outcome={Outcome} duration={seconds:F1}s " +
                $"turns={Volatile.Read(ref turns)} tools={Volatile.Read(ref toolCallsRun)} " +
                $"up={FormatAudio(uploadedBytes, LiveClient.InputSampleRate)} " +
                $"down={FormatAudio(down, LiveClient.DefaultOutputSampleRate)}");

            LogAccounting(seconds, down);
        }

        // The quota-measurement half of the close log, under its own [session]
        // prefix so a whole run's worth can be grepped out and totalled without
        // the protocol chatter around it.
        //
        // Deliberately separate from the line above rather than folded into it:
        // that one is Phase 3's, it reports what the SESSION did, and it was
        // verified against the live endpoint in that shape. This one reports what
        // the ACCOUNT has spent, in the units the quota question is asked in.
        private void LogAccounting(double durationSeconds, long downBytes)
        {
            double up = AudioSeconds(uploadedBytes, LiveClient.InputSampleRate);
            double downSecs = AudioSeconds(downBytes, LiveClient.DefaultOutputSampleRate);
            DateTime closedLocal = DateTime.Now;
            int toolCalls = Volatile.Read(ref toolCallsRun);
            var duration = TimeSpan.FromSeconds(durationSeconds);

            // Recorded before the summary is printed, so #n counts this session
            // rather than the ones before it.
            latency?.RecordSession(openedLocal, closedLocal, duration, up, downSecs, toolCalls);

            Console.WriteLine(LatencyTracker.SessionSummary(
                latency?.SessionCount ?? 1,
                openedLocal,
                closedLocal,
                duration,
                Volatile.Read(ref turns),
                toolCalls,
                up,
                downSecs,
                Outcome.ToString()));

            if (latency != null) Console.WriteLine(latency.SessionTotals());
        }

        private static double AudioSeconds(long bytes, int sampleRate) =>
            bytes / (double)(sampleRate * 2);

        private static string FormatAudio(long bytes, int sampleRate) =>
            $"{bytes / 1024.0:F0}KiB/{AudioSeconds(bytes, sampleRate):F1}s";

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;

            // Belt and braces against the one failure mode that costs real money:
            // if RunConversationAsync never got to its finally (unhandled crash,
            // process teardown), this still closes the socket.
            Complete(LiveSessionOutcome.Cancelled);
            try { uplink.CompleteAdding(); } catch { }
            try { uplink.Dispose(); } catch { }
            StopWakewordWatch();

            // bubbleSignal is deliberately NOT disposed. Bubble setters run from
            // LiveClient's receive loop, which can still deliver a late event
            // after this returns; a disposed event would turn that harmless
            // no-op into an ObjectDisposedException on the socket's own thread.
            // One ManualResetEventSlim per conversation is not worth that.
            StopBubblePump();
            try { audio?.Dispose(); } catch { }
            try { client?.Dispose(); } catch { }
            try { capReg.Dispose(); } catch { }
            try { sessionCts?.Dispose(); } catch { }
        }
    }
}
