using Personal_Assistant.Configuration;
using Personal_Assistant.Dispatch;
using Personal_Assistant.Live;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Personal_Assistant.CallScreening
{
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
        /// <summary>The Live socket went away mid-call.</summary>
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

    /// <summary>
    /// One screened call's conversation: a Gemini Live session with the caller on
    /// the other end of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Drives <see cref="LiveClient"/> DIRECTLY rather than going through
    /// <c>LiveSession</c>. LiveSession is 77 KB welded to <c>LiveAudioPipeline</c> —
    /// the real microphone and the real speakers — and a call uses neither: its
    /// ear is a WASAPI loopback on the endpoint Phone Link plays the caller into,
    /// and its mouth is a virtual cable. Threading a second audio pair through
    /// LiveSession would put the everyday voice path at risk for no gain. The
    /// precedent for a bare LiveClient is <c>VoiceClipRenderer.RenderOneAsync</c>
    /// (VoiceClips.cs:175), which opens one with no session at all.
    /// </para>
    /// <para>
    /// PHASE 3a — the caller hears Gemini's own voice, not a clone of Layth's.
    /// The model's audio is routed to the cable exactly as it arrives. Phase 3b
    /// stops routing it and drives an ElevenLabs clone off
    /// <see cref="LiveClient.OutputTranscript"/> instead, which is already enabled
    /// here: measured 2026-08-15, the transcript LEADS the model's own audio by
    /// 338–355 ms, so 2.5's synthesis is wasted compute rather than added latency
    /// and the swap costs nothing but the ElevenLabs key.
    /// </para>
    /// <para>
    /// THE CALLER IS UNTRUSTED. This session is never given
    /// <c>registry.ToolDefinitions</c>; it gets <see cref="CallTools"/>, which is
    /// two borrowed read-only tools plus two that only exist for the duration of
    /// the call. A stranger saying "send a text to Mum" must do nothing at all.
    /// </para>
    /// </remarks>
    public sealed class CallSession : IDisposable
    {
        // Gemini Live hands back 24 kHz mono PCM16 (LiveClient.DefaultOutputSampleRate)
        // and the cable wants 16 kHz. The rate is read off each chunk's mimeType
        // rather than assumed, so the resampler is built on the first chunk.
        private readonly object gate = new object();
        private MonoResampler outbound;
        private int outboundRate;

        private readonly CallAudioBridge bridge;
        private readonly CallTools tools;
        private readonly string systemInstruction;
        private readonly string model;
        private readonly string voice;
        private readonly TimeSpan maxCall;
        private readonly TimeSpan silenceLimit;
        private readonly int thinkingBudget;
        private int bargeIns;

        // How long a goodbye may take to reach the caller before the call is ended
        // anyway. Long enough for a farewell sentence, short enough that a model
        // which called hang_up and then kept talking cannot hold the line open.
        private static readonly TimeSpan GoodbyeGrace = TimeSpan.FromSeconds(6);

        // The caller's frames, handed from the WASAPI capture thread to the send
        // pump. Bounded and non-blocking on the producer side: a stalled callback
        // is DROPPED audio rather than late audio, so the one thing this must
        // never do is make the capture thread wait.
        private readonly BlockingCollection<byte[]> outgoing =
            new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(), 150);
        private int dropped;

        private readonly StringBuilder callerSaid = new StringBuilder();
        private readonly StringBuilder assistantSaid = new StringBuilder();
        private readonly List<CallTranscriptLine> transcript = new List<CallTranscriptLine>();

        private readonly Stopwatch clock = new Stopwatch();
        private readonly TaskCompletionSource<CallEnding> finished =
            new TaskCompletionSource<CallEnding>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Last moment anything was heard from either end. The dead-line detector.
        private DateTime lastSound = DateTime.Now;

        private string message;
        private LiveClient client;
        private CancellationTokenSource life;

        public CallSession(
            CallAudioBridge bridge,
            CallTools tools,
            string systemInstruction,
            TimeSpan maxCall)
        {
            this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            this.tools = tools ?? CallTools.None;
            this.systemInstruction = systemInstruction;
            this.maxCall = maxCall;

            // CallModel defaults to whatever the everyday assistant runs, which is
            // deliberate: there is no second model to keep working here. The
            // half-cascade TEXT-mode model the plan originally named does not
            // exist — measured 2026-08-15, no Live model on this key serves TEXT.
            model = LaithConfig.Text("CallModel", LiveSessionOptions.DefaultModel);
            // Through CallGreeting, which is the one accessor for this setting —
            // the greeting clips are cached under it, and a second copy of the
            // expression here could drift and greet the caller in a voice the
            // conversation then does not use.
            voice = CallGreeting.Voice;
            silenceLimit = TimeSpan.FromSeconds(
                LaithConfig.Int("CallSilenceSeconds", 15, 5, 120));
            thinkingBudget = LaithConfig.Int("CallThinkingBudget", 0, -1, 8192);
        }

        /// <summary>What take_message recorded, if anything.</summary>
        public string Message { get { lock (gate) return message; } }

        /// <summary>
        /// Holds the conversation until somebody ends it.
        /// </summary>
        /// <param name="call">Who is on the phone, as the toast described them.</param>
        /// <param name="stillConnected">
        /// Asked every couple of seconds. False means the caller hung up — the one
        /// ending nothing else can detect, because a caller who leaves says
        /// nothing, which is indistinguishable from a caller who is thinking.
        /// </param>
        public async Task<CallOutcome> RunAsync(
            IncomingCall call, Func<bool> stillConnected, CancellationToken cancel = default)
        {
            var outcome = new CallOutcome
            {
                Caller = call?.Caller ?? "an unknown number",
                StartedAt = DateTime.Now,
            };

            clock.Start();
            life = CancellationTokenSource.CreateLinkedTokenSource(cancel);

            try
            {
                var options = new LiveSessionOptions
                {
                    Model = model,
                    Voice = voice,
                    SystemInstruction = systemInstruction,

                    // THE WHOLE OF PART C, IN ONE LINE. Never
                    // registry.ToolDefinitions — see the class remarks.
                    Tools = tools.Definitions,

                    // Off, and not because of quota. Grounding would let a caller
                    // use Layth's key as a search engine, and a screening call has
                    // no question that needs the web — it needs to know who is
                    // calling and what to tell him.
                    EnableGoogleSearch = false,

                    // Measured 2026-08-15: 0 is accepted, and takes ~330 ms off the
                    // front of every reply. A call cannot afford deliberation
                    // tokens — but no deliberation is also the state in which a
                    // model is most likely to answer from reflex, and the first
                    // real conversation did exactly that (see CallPersona). The
                    // prompt was the fault and was fixed there; this is the lever
                    // to reach for second, without a rebuild. Negative leaves the
                    // model's own default.
                    ThinkingBudget = thinkingBudget < 0 ? (int?)null : thinkingBudget,

                    // Both on. Input for the record of what the caller actually
                    // said; output because it is what Phase 3b's clone will speak
                    // from, and having it running now means that swap changes one
                    // event handler rather than the session setup.
                    InputAudioTranscription = true,
                    OutputAudioTranscription = true,

                    // Server VAD. The half-duplex gate LiveAudio needs on speakers
                    // is not needed here: the model's voice goes to the cable and
                    // the caller's arrives on the speakers, so there is no path by
                    // which the model can hear itself. What CAN reach this leg is
                    // the machine's own audio — which is why the call holds
                    // PresenceGate.MuteFor and pauses media for its duration.
                    ManualActivityDetection = false,
                };

                client = new LiveClient(options);
                Wire(client);

                Console.WriteLine(
                    $"[call] opening a Live session ({model}, voice {voice ?? "default"}, " +
                    $"{tools.Definitions.Count} tools: {tools.Describe()})");

                using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(life.Token))
                {
                    connecting.CancelAfter(TimeSpan.FromSeconds(20));
                    await client.ConnectAsync(connecting.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // A caller listening to a socket that never opened is the worst
                // outcome available, so this is loud and the service hangs up on
                // it rather than hoping.
                outcome.Ending = CallEnding.NeverStarted;
                outcome.Failure = $"{ex.GetType().Name}: {ex.Message}";
                outcome.Duration = clock.Elapsed;
                Console.WriteLine($"[call] the Live session would not open: {outcome.Failure}");
                return outcome;
            }

            // Only now is the caller's audio worth sending anywhere.
            bridge.FrameCaptured += OnCallerFrame;
            Task pump = Task.Run(() => PumpAsync(life.Token));

            try
            {
                CallEnding ending = await WaitForEndAsync(stillConnected, life.Token).ConfigureAwait(false);
                outcome.Ending = ending;
            }
            finally
            {
                // TRACED STEP BY STEP because a real call hung somewhere in here
                // (2026-08-22) and the log simply stopped after the session closed
                // — every one of these is an await that can, in principle, never
                // come back, and nothing distinguished them from outside.
                Console.WriteLine("[call/teardown] unhooking the capture");
                bridge.FrameCaptured -= OnCallerFrame;
                outgoing.CompleteAdding();
                life.Cancel();

                Console.WriteLine("[call/teardown] waiting for the send pump");
                try { await Task.WhenAny(pump, Task.Delay(1000)).ConfigureAwait(false); } catch { }

                Console.WriteLine("[call/teardown] closing the Live socket");

                // BOUNDED, because an unbounded close here deadlocks the call.
                //
                // Measured 2026-08-22 on a real screened call: the model said
                // goodbye, and this await never returned. The line stayed open, the
                // widget counted upwards forever, the audio route was left pointing
                // at the virtual cable, and the trace stopped dead on this exact
                // line three calls running.
                //
                // The cause is the ordering just above. CloseAsync completes the
                // WebSocket close HANDSHAKE — it sends a close frame and waits for
                // the peer's reply — but that reply can only be observed by the
                // receive loop, and `life.Cancel()` a few lines earlier has already
                // torn that loop down. So it waits for a frame nobody will ever
                // read. LiveClient's own 5s CancellationToken does not rescue it:
                // ClientWebSocket.CloseAsync on .NET Framework does not reliably
                // honour cancellation once it is waiting on the peer.
                //
                // The socket is disposed by client.Dispose() moments later
                // regardless, so a close that has not completed costs nothing. A
                // teardown that never completes costs the whole feature.
                try
                {
                    Task close = client.CloseAsync("call ended");
                    if (await Task.WhenAny(close, Task.Delay(2000)).ConfigureAwait(false) != close)
                        Console.WriteLine("[call/teardown] the Live socket would not close; moving on");
                }
                catch { }

                Console.WriteLine("[call/teardown] Live socket closed");
            }

            Flush(CallSpeaker.Caller, callerSaid);
            Flush(CallSpeaker.Assistant, assistantSaid);

            outcome.Duration = clock.Elapsed;
            lock (gate)
            {
                outcome.Message = message;
                outcome.Transcript = transcript.ToList();
            }
            Console.WriteLine("[call/teardown] outcome assembled");

            if (Volatile.Read(ref dropped) > 0)
            {
                // Named because it is the tell for "the caller sounded chopped up":
                // the send pump could not keep up with the capture callback.
                Console.WriteLine($"[call] {dropped} caller frame(s) were dropped before sending.");
            }

            return outcome;
        }

        // --- the two audio directions -------------------------------------------

        // Below this a frame is the line idling, not somebody speaking. Same floor
        // the preflight uses for "something rather than nothing", about -46 dBFS.
        private const double SpeechFloor = 0.005;

        // The bridge's timer thread. Hands off and returns; see CallAudioBridge.
        private void OnCallerFrame(byte[] frame)
        {
            // LEVEL, not arrival. The bridge emits a frame every 20 ms whether the
            // caller is talking or not — it has to, or server VAD never sees the
            // silence that ends a turn — so "a frame arrived" now means nothing at
            // all about whether anyone is there. Treating arrival as sound would
            // make the dead-line detector below unable to ever fire.
            if (CallAudioFormat.Rms(frame) >= SpeechFloor) lastSound = DateTime.Now;

            if (!outgoing.IsAddingCompleted && outgoing.TryAdd(frame)) return;
            Interlocked.Increment(ref dropped);
        }

        private async Task PumpAsync(CancellationToken cancel)
        {
            try
            {
                foreach (byte[] frame in outgoing.GetConsumingEnumerable(cancel))
                {
                    if (client == null || !client.IsOpen) return;
                    await client.SendAudioAsync(frame, frame.Length, cancel).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // The socket died under us. Ending the call is WaitForEndAsync's
                // job via Closed; this only has to stop and say why.
                Console.WriteLine($"[call] could not send the caller's audio: {ex.Message}");
            }
        }

        private void Wire(LiveClient c)
        {
            // The model's own voice, straight down the cable. Phase 3b deletes
            // this handler and speaks the transcript through the clone instead.
            c.AudioReceived += pcm =>
            {
                lastSound = DateTime.Now;
                try { bridge.Send(ToCallRate(pcm, c.OutputSampleRate)); }
                catch (Exception ex) { Console.WriteLine($"[call] outbound audio failed: {ex.Message}"); }
            };

            // Barge-in. Without the flush the model keeps talking over the caller
            // for as long as it had run ahead of playback — see ClearOutbound.
            // COUNTED, because a barge-in is indistinguishable from a bug from the
            // outside. Each one discards whatever the model had already generated
            // and not yet played, so a server VAD firing on line noise chops the
            // assistant off mid-sentence — which the caller hears as choppy audio
            // and which leaves a transcript line with no speech behind it. If the
            // count climbs while the caller is silent, the flush is the fault and
            // not the network.
            c.Interrupted += () =>
            {
                Interlocked.Increment(ref bargeIns);
                bridge.ClearOutbound();
                Flush(CallSpeaker.Assistant, assistantSaid);
            };

            c.InputTranscript += text =>
            {
                lastSound = DateTime.Now;
                lock (gate) callerSaid.Append(text);
            };
            c.OutputTranscript += text => { lock (gate) assistantSaid.Append(text); };

            c.TurnComplete += () =>
            {
                Flush(CallSpeaker.Caller, callerSaid);
                Flush(CallSpeaker.Assistant, assistantSaid);
            };

            c.ToolCallReceived += calls => Task.Run(() => RunToolsAsync(calls));

            c.Closed += (status, reason) =>
            {
                Console.WriteLine($"[call] the Live session closed: {reason}");
                finished.TrySetResult(CallEnding.SessionLost);
            };
        }

        private byte[] ToCallRate(byte[] pcm16, int sourceRate)
        {
            if (pcm16 == null || pcm16.Length == 0) return pcm16;

            MonoResampler resampler;
            lock (gate)
            {
                // Built on the first chunk, because the rate is parsed off the
                // chunk's mimeType and is not known before one arrives. Rebuilt if
                // it ever changes, which would otherwise silently pitch-shift the
                // rest of the call.
                if (outbound == null || outboundRate != sourceRate)
                {
                    if (outbound != null)
                        Console.WriteLine($"[call] model output rate changed {outboundRate} -> {sourceRate}Hz");
                    outboundRate = sourceRate <= 0 ? LiveClient.DefaultOutputSampleRate : sourceRate;
                    outbound = new MonoResampler(outboundRate, CallAudioFormat.GeminiRate);
                }
                resampler = outbound;
            }

            short[] mono = CallAudioFormat.Downmix(pcm16, pcm16.Length, 1);
            short[] resampled = resampler.Process(mono, mono.Length);
            return CallAudioFormat.ToBytes(resampled, resampled.Length);
        }

        // --- tools ----------------------------------------------------------------

        private async Task RunToolsAsync(IReadOnlyList<LiveFunctionCall> calls)
        {
            var results = new List<LiveFunctionResult>();

            foreach (LiveFunctionCall call in calls)
            {
                try
                {
                    if (CallTools.IsHangUp(call.Name))
                    {
                        Console.WriteLine("[call] the assistant is wrapping the call up.");
                        results.Add(LiveFunctionResult.Done(call));

                        // LET THE GOODBYE LAND FIRST.
                        //
                        // Measured 2026-08-17: the model called hang_up and said
                        // "Goodbye Amin" in the same turn, the session was torn down
                        // the instant the tool came back, and the caller never heard
                        // it — the transcript line was even flushed AFTER the "Live
                        // session closed" line, which is the tell. The model
                        // generates faster than the cable plays, so at the moment
                        // hang_up arrives its farewell is still sitting in the
                        // outbound buffer.
                        //
                        // Detached, so the tool response still goes back
                        // immediately: blocking here would hold the response the
                        // model is waiting on before it will finish speaking.
                        _ = Task.Run(async () =>
                        {
                            await bridge.DrainOutboundAsync(GoodbyeGrace).ConfigureAwait(false);
                            finished.TrySetResult(CallEnding.Wrapped);
                        });
                        continue;
                    }

                    if (CallTools.IsTakeMessage(call.Name))
                    {
                        results.Add(new LiveFunctionResult(call.Id, call.Name, TakeMessage(call.Args)));
                        continue;
                    }

                    ToolResult result = await tools.RunAsync(call.Name, call.Args).ConfigureAwait(false);
                    results.Add(new LiveFunctionResult(call.Id, call.Name, result.ToResponse()));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[call] tool '{call.Name}' failed: {ex.Message}");
                    results.Add(LiveFunctionResult.Error(call, ex.Message));
                }
            }

            try
            {
                if (client != null && client.IsOpen)
                    await client.SendToolResponseAsync(results).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[call] could not return tool results: {ex.Message}");
            }
        }

        private IReadOnlyDictionary<string, string> TakeMessage(IReadOnlyDictionary<string, string> args)
        {
            args.TryGetValue("message", out string text);
            if (string.IsNullOrWhiteSpace(text))
            {
                return new Dictionary<string, string>
                {
                    ["error"] = "no message text was given — pass what the caller actually said."
                };
            }

            lock (gate)
            {
                // Appended rather than replaced: a caller who adds "oh, and tell
                // him..." should not overwrite what they said first.
                message = string.IsNullOrWhiteSpace(message) ? text.Trim() : message + " " + text.Trim();
            }

            Console.WriteLine($"[call] message taken: {text.Trim()}");
            return new Dictionary<string, string> { ["result"] = "written down" };
        }

        // --- endings --------------------------------------------------------------

        // Five ways a call ends, and the two that need watching for.
        private async Task<CallEnding> WaitForEndAsync(Func<bool> stillConnected, CancellationToken cancel)
        {
            DateTime deadline = DateTime.Now.Add(maxCall);
            bool nudged = false;
            DateTime nudgedAt = DateTime.MinValue;

            // A heartbeat of what the inbound leg is actually carrying.
            //
            // Added after the first real call, which produced a perfect log and no
            // conversation: the route was engaged, the call was answered on the PC,
            // and not one transcript line arrived. Nothing in the log distinguished
            // "the caller said nothing" from "no audio reached us at all", so the
            // next question had to be asked of a human instead of read off the
            // screen. `silent` counts frames the bridge PADDED — if it tracks the
            // total, no audio is arriving and the inbound leg is dead, whatever the
            // caller was doing.
            int tick = 0;
            long silentAtStart = bridge.SilenceFrames;

            using (cancel.Register(() => finished.TrySetResult(CallEnding.Cancelled)))
            {
                while (true)
                {
                    Task done = await Task.WhenAny(
                        finished.Task, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None))
                        .ConfigureAwait(false);

                    if (done == finished.Task) return finished.Task.Result;

                    if (++tick % 5 == 0)
                    {
                        long silent = bridge.SilenceFrames - silentAtStart;
                        long total = bridge.FramesEmitted;
                        Console.WriteLine(
                            $"[call] inbound level {bridge.InboundLevel:F4} gain {bridge.InboundGain:F1}x, " +
                            $"{total} frames ({silent} of them padded silence), " +
                            $"sent {total - Volatile.Read(ref dropped)}, " +
                            $"barge-ins {Volatile.Read(ref bargeIns)}");
                    }

                    if (DateTime.Now >= deadline)
                    {
                        Console.WriteLine($"[call] the {maxCall.TotalSeconds:F0}s cap ran out.");
                        return CallEnding.TimeCap;
                    }

                    // The caller putting the phone down. Nothing on the audio path
                    // says so — a line that has gone dead sounds exactly like a
                    // caller who has stopped to think — so the Phone Link window is
                    // the only honest source. ~350 ms per look, hence two seconds.
                    if (stillConnected != null)
                    {
                        bool connected;
                        try { connected = stillConnected(); }
                        catch (Exception ex)
                        {
                            // Unknown is not "hung up": ending a live call because
                            // a UIA read threw would cut off a real conversation.
                            Console.WriteLine($"[call] could not read the call state: {ex.Message}");
                            connected = true;
                        }
                        if (!connected) return CallEnding.CallerLeft;
                    }

                    TimeSpan quiet = DateTime.Now - lastSound;
                    if (quiet < silenceLimit) { nudged = false; continue; }

                    if (!nudged)
                    {
                        // One prompt, then give up. A caller who dialled and then
                        // walked away would otherwise hold the line open to the cap.
                        //
                        // `lastSound` is deliberately NOT bumped here. It was, and
                        // that made the very next tick see a fresh line, which
                        // cleared the flag above and re-armed the nudge — so the
                        // assistant asked "are you still there?" on a loop and
                        // never once concluded the line was dead. Caught
                        // 2026-08-17 by bakeoff/callsession, which logged the
                        // identical nudge twice in one 40s run.
                        nudged = true;
                        nudgedAt = DateTime.Now;
                        Console.WriteLine($"[call] {quiet.TotalSeconds:F0}s of silence — prompting once.");
                        try
                        {
                            await client.SendTextAsync(
                                "(The caller has said nothing for a while. Ask once, briefly, " +
                                "whether they are still there.)", cancel).ConfigureAwait(false);
                        }
                        catch (Exception ex) { Console.WriteLine($"[call] nudge failed: {ex.Message}"); }
                        continue;
                    }

                    // Anything the caller says moves lastSound, which trips the
                    // `quiet < silenceLimit` branch above and clears the flag — so
                    // reaching here means a whole further window of nothing since
                    // the question was asked.
                    if (DateTime.Now - nudgedAt < silenceLimit) continue;

                    Console.WriteLine("[call] still nothing — treating the line as dead.");
                    return CallEnding.Silence;
                }
            }
        }

        /// <summary>Ends the call from outside — end_call, or process teardown.</summary>
        public void Stop(CallEnding why = CallEnding.Cancelled) => finished.TrySetResult(why);

        /// <summary>
        /// Raised as each transcript line is finalised, for anything that wants to
        /// watch the call as it happens rather than read it afterwards — the call
        /// widget, in practice, which is the only visible sign a call is running
        /// at all once the Google Voice browser is headless.
        /// </summary>
        /// <remarks>
        /// Fires on whatever thread completed the turn, and it fires while a
        /// stranger is on the line: a handler that blocks stalls the call. The
        /// widget's handler only marshals onto its own UI thread, which is the
        /// right shape for anything else that subscribes.
        /// </remarks>
        public event Action<CallTranscriptLine> LineRecorded;

        private void Flush(CallSpeaker speaker, StringBuilder buffer)
        {
            CallTranscriptLine line = null;

            lock (gate)
            {
                if (buffer.Length == 0) return;
                string text = buffer.ToString().Trim();
                buffer.Clear();
                if (text.Length == 0) return;

                line = new CallTranscriptLine(speaker, text, clock.Elapsed);
                transcript.Add(line);
                Console.WriteLine($"[call] {line}");
            }

            // Raised OUTSIDE the lock. Handlers are arbitrary code, and holding
            // `gate` across one would let a subscriber deadlock the audio path.
            try { LineRecorded?.Invoke(line); }
            catch (Exception ex) { Console.WriteLine($"[call] transcript listener threw: {ex.Message}"); }
        }

        public void Dispose()
        {
            Stop();
            try { life?.Cancel(); } catch { }
            try { client?.Dispose(); } catch { }
            try { outgoing.Dispose(); } catch { }
            try { life?.Dispose(); } catch { }
        }
    }
}
