using NAudio.CoreAudioApi;
using Personal_Assistant.AudioControl;
using Personal_Assistant.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Personal_Assistant.CallScreening
{
    // Why the call audio path cannot be used, in words the assistant can say.
    //
    // Deliberately its own type rather than CallScreeningService's ArmRefusal,
    // which it is otherwise identical to. This file has to compile against
    // nothing but AudioController and NAudio so the bakeoff/callaudio harness can
    // drive it standalone; ArmRefusal lives in the file that pulls in FlaUI, the
    // trigger engine and the whole Phone Link surface. Phase 2 converts one to
    // the other in a line.
    public sealed class CallAudioFault
    {
        public string Spoken { get; }   // what the user hears
        public string Reason { get; }   // for the log

        public CallAudioFault(string spoken, string reason)
        {
            Spoken = spoken;
            Reason = reason;
        }

        public override string ToString() => Reason;
    }

    /// <summary>The three endpoints a screened call uses, resolved to ids.</summary>
    public sealed class CallAudioRoute
    {
        /// <summary>Render endpoint Phone Link plays the caller into (the monitor).
        /// Becomes the Communications-role render default; loopback-captured.</summary>
        public string MonitorRenderId { get; set; }
        public string MonitorRenderName { get; set; }

        /// <summary>Render endpoint the assistant's voice is played into
        /// (<c>CABLE Input</c>). Never becomes a default — it is written directly.</summary>
        public string CableRenderId { get; set; }
        public string CableRenderName { get; set; }

        /// <summary>Capture endpoint Phone Link hears (<c>CABLE Output</c>).
        /// Becomes the Communications-role capture default.</summary>
        public string CableCaptureId { get; set; }
        public string CableCaptureName { get; set; }

        public override string ToString() =>
            $"in={MonitorRenderName} out={CableRenderName} mic={CableCaptureName}";
    }

    /// <summary>
    /// Moves the <b>Communications</b> role onto the call audio path and — the
    /// part that actually matters — puts it back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only eCommunications moves. eConsole and eMultimedia stay where they are,
    /// so music, system sounds and L.A.I.T.H.'s ordinary speech never leave the
    /// real speakers, and <c>switch_audio_output</c> keeps working unchanged.
    /// Measured on this machine 2026-08-15 (bakeoff/callaudio/EndpointCheck):
    /// Multimedia and Communications ALREADY point at different devices for both
    /// render and capture, so restore must put back exactly what it found, per
    /// role. Re-fusing the three roles on the way out — which is what
    /// <c>AudioController.SwitchOutputDevice</c> does, correctly, for a spoken
    /// "switch to my headphones" — would quietly destroy a split the user has.
    /// </para>
    /// <para>
    /// The restore path is the highest-stakes code in this feature. Leaving the
    /// Communications role on a virtual cable breaks the next real call or Teams
    /// meeting with nothing on screen to explain it. That is not hypothetical:
    /// VoiceMeeter did exactly this on 2026-08-15, left a dead device as the
    /// comms default, and the assistant went deaf until the roles were put back
    /// by hand. So restore runs from four places — a <c>finally</c>, the
    /// <c>ProcessExit</c> hook, <c>Console.CancelKeyPress</c>, and a disk-backed
    /// repair at startup for the case where the process did not get to run any of
    /// them.
    /// </para>
    /// </remarks>
    public sealed class CallAudioRouter : IDisposable
    {
        private readonly AudioController audio;

        private readonly string cableRenderName;    // AI -> caller
        private readonly string cableCaptureName;   // what Phone Link hears
        private readonly string monitorRenderName;  // caller -> AI, loopback-captured
        private readonly bool toneTest;

        private readonly object gate = new object();
        private readonly bool moveAllRoles;

        // Every router that has moved a role and not yet put it back. Static so
        // a teardown hook can restore without having been handed the instance —
        // Program.cs registers its hooks before any call object exists.
        private static readonly List<CallAudioRouter> engaged = new List<CallAudioRouter>();

        private CallAudioRoute route;
        private readonly List<SavedRole> saved = new List<SavedRole>();
        private bool isEngaged;

        // WHICH ROLES A CALL MOVES, and why it is no longer just Communications.
        //
        // The plan was emphatic that only eCommunications should move, so that
        // music, system sounds and L.A.I.T.H.'s own voice never leave the real
        // devices. That was right in principle and wrong in fact, and it took a
        // real call to find out: **Phone Link's call audio does not run in
        // PhoneExperienceHost at all.** Measured 2026-08-17 with
        // bakeoff/callaudio/AudioSessions during a live call — the audio sessions
        // belong to `svchost` (the Bluetooth audio-gateway service), and it opened
        //
        //     [play] Speakers          active, peaks tracking the caller
        //     [rec ] Microphone Array  active, peaks tracking the room
        //
        // while the Communications capture default was CABLE Output. It ignored
        // the role entirely and took the plain default. The caller heard silence;
        // the model heard the caller fine, because Speakers happens to be the
        // Console and Multimedia playback default too — which is also why the
        // inbound leg working never proved the role was being honoured.
        //
        // So a call moves all three roles. The cost is real and is paid back
        // immediately: the machine's default microphone is the cable for the
        // duration, which makes the wake word deaf until the call ends. That is
        // survivable — the assistant is busy and muted anyway (see PresenceGate
        // and the hush in CallScreeningService) — but it is exactly the state that
        // must never be left behind, which is why the restore path is what it is.
        //
        // CallMoveAllRoles=false reverts to comms-only, for a machine where the
        // telephony stack does honour the role.
        private static readonly AudioRole[] AllRoles =
        {
            AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications
        };

        private static readonly AudioRole[] CommunicationsOnly = { AudioRole.Communications };

        private AudioRole[] RolesToMove => moveAllRoles ? AllRoles : CommunicationsOnly;

        public CallAudioRouter(AudioController audio = null)
        {
            this.audio = audio ?? new AudioController();

            // Through LaithConfig, so each gets a LAITH_* override and a line in
            // the startup [config] dump for free.
            cableRenderName = LaithConfig.Text("CallCableOut", "CABLE Input");
            cableCaptureName = LaithConfig.Text("CallCableMic", "CABLE Output");

            // The built-in speakers, NOT the monitor endpoint the field name still
            // remembers. The inbound leg moved on 2026-08-16 when every NVIDIA
            // endpoint on this machine read NotPresent hours after being Active;
            // App.config was updated and this fallback was not, so the bakeoff
            // harness — which deliberately ships no appSettings of its own, to run
            // against exactly these defaults — resolved a device that no longer
            // exists and refused every preflight.
            monitorRenderName = LaithConfig.Text("CallCableIn", "Speakers (2- Realtek(R) Audio)");
            toneTest = LaithConfig.Bool("CallPreflightTone", true);
            moveAllRoles = LaithConfig.Bool("CallMoveAllRoles", true);
        }

        public bool IsEngaged { get { lock (gate) return isEngaged; } }

        public CallAudioRoute Route { get { lock (gate) return route; } }

        // --- Resolving ---------------------------------------------------------------

        /// <summary>
        /// Turns the three configured device names into endpoint ids. Returns null
        /// on success, or the reason it could not.
        /// </summary>
        public CallAudioFault Resolve(out CallAudioRoute resolved)
        {
            resolved = null;

            // FindEndpointId refuses ambiguous matches and prints the candidates,
            // which is what makes a config key safe on a machine that also has a
            // Voicemod device and two Steam ones.
            string cableRenderId = audio.FindEndpointId(cableRenderName, capture: false);
            if (cableRenderId == null) return MissingDevice(cableRenderName, capture: false, leg: "outbound");

            string cableCaptureId = audio.FindEndpointId(cableCaptureName, capture: true);
            if (cableCaptureId == null) return MissingDevice(cableCaptureName, capture: true, leg: "outbound");

            string monitorRenderId = audio.FindEndpointId(monitorRenderName, capture: false);
            if (monitorRenderId == null) return MissingDevice(monitorRenderName, capture: false, leg: "inbound");

            resolved = new CallAudioRoute
            {
                CableRenderId = cableRenderId,
                CableRenderName = NameOf(cableRenderId, capture: false) ?? cableRenderName,
                CableCaptureId = cableCaptureId,
                CableCaptureName = NameOf(cableCaptureId, capture: true) ?? cableCaptureName,
                MonitorRenderId = monitorRenderId,
                MonitorRenderName = NameOf(monitorRenderId, capture: false) ?? monitorRenderName,
            };

            // Logged at arm time on purpose: when a leg turns out to be silent,
            // the first question is always "which device did it actually pick?".
            Console.WriteLine($"[call-audio] route resolved: {resolved}");
            return null;
        }

        // Distinguishes "not installed" from "there but not active", because they
        // need different sentences from the user. The monitor endpoint going
        // inactive when the monitor is switched off is the most likely everyday
        // breakage this feature has, and "no such device" would send Layth looking
        // for a driver problem that isn't there.
        private CallAudioFault MissingDevice(string name, bool capture, string leg)
        {
            string state = StateOf(name, capture);
            string kind = capture ? "recording" : "playback";

            if (state != null)
            {
                return new CallAudioFault(
                    $"I can't screen calls — the {leg} audio device, {name}, is {Humanise(state)}.",
                    $"{leg} leg: {kind} device '{name}' is present but {state}");
            }

            return new CallAudioFault(
                $"I can't screen calls — there's no {kind} device called {name} on this PC.",
                $"{leg} leg: no active {kind} device matches '{name}' " +
                "(VB-CABLE not installed, or the name in the config is wrong)");
        }

        private static string Humanise(string state) =>
            state == "Unplugged" ? "unplugged — the monitor is probably switched off"
            : state == "Disabled" ? "disabled in Windows sound settings"
            : state == "NotPresent" ? "not present"
            : state.ToLowerInvariant();

        // Looks past DeviceState.Active, which is all AudioController enumerates.
        private static string StateOf(string name, bool capture)
        {
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(
                        capture ? DataFlow.Capture : DataFlow.Render, DeviceState.All))
                    {
                        using (device)
                        {
                            if (device.State == DeviceState.Active) continue;
                            if (device.FriendlyName != null &&
                                device.FriendlyName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                                return device.State.ToString();
                        }
                    }
                }
            }
            catch { /* diagnosis only; the refusal stands either way */ }
            return null;
        }

        private static string NameOf(string id, bool capture)
        {
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                using (MMDevice device = enumerator.GetDevice(id))
                {
                    return device?.FriendlyName;
                }
            }
            catch { return null; }
        }

        // --- Preflight ---------------------------------------------------------------

        /// <summary>
        /// Proves both legs actually carry audio. Returns null when they do, or
        /// the reason — naming the failing leg — when they do not.
        /// </summary>
        /// <remarks>
        /// A signal test rather than a presence test. A driver being installed and
        /// a device being the right one are not the same question as sound getting
        /// from one end of a cable to the other, and every way this feature fails
        /// sounds identical from the outside: the caller hears nothing. So both
        /// legs get a tone played into them and are asked whether it arrived.
        /// </remarks>
        public async Task<CallAudioFault> PreflightAsync(CancellationToken cancel = default)
        {
            CallAudioFault fault = Resolve(out CallAudioRoute r);
            if (fault != null) return fault;

            if (!toneTest)
            {
                Console.WriteLine("[call-audio] preflight tone disabled (CallPreflightTone=false) — presence only.");
                return null;
            }

            // Outbound first: it is the leg with two virtual endpoints in it and
            // therefore the one most likely to be misconfigured.
            fault = await TestOutboundAsync(r, cancel).ConfigureAwait(false);
            if (fault != null) return fault;

            return await TestInboundAsync(r, cancel).ConfigureAwait(false);
        }

        /// <summary>
        /// AI -&gt; caller: a tone into <c>CABLE Input</c>, listened for on
        /// <c>CABLE Output</c>.
        /// </summary>
        public Task<CallAudioFault> TestOutboundAsync(
            CallAudioRoute r, CancellationToken cancel = default) =>
            TestLegAsync(
                "outbound", r.CableRenderId, r.CableCaptureId, loopback: false,
                describe: $"{r.CableRenderName} -> {r.CableCaptureName}",
                spokenLeg: "my voice can't reach the caller",
                cancel: cancel);

        /// <summary>
        /// caller -&gt; AI: a tone into the monitor endpoint, listened for on the
        /// loopback of that same endpoint.
        /// </summary>
        /// <remarks>
        /// Separately callable because it is the leg that can break on its own,
        /// every day, by someone switching a monitor off — and because it is the
        /// only leg that exists on a machine with no cable installed, so it is
        /// testable before the prerequisite is met.
        /// </remarks>
        public Task<CallAudioFault> TestInboundAsync(
            CallAudioRoute r, CancellationToken cancel = default) =>
            TestLegAsync(
                "inbound", r.MonitorRenderId, r.MonitorRenderId, loopback: true,
                describe: $"{r.MonitorRenderName} (loopback)",
                spokenLeg: "I can't hear the caller",
                cancel: cancel);

        /// <summary>
        /// Resolves just the inbound endpoint by name, for testing that leg
        /// without the cable being installed.
        /// </summary>
        public CallAudioFault ResolveInbound(out CallAudioRoute resolved)
        {
            resolved = null;

            string id = audio.FindEndpointId(monitorRenderName, capture: false);
            if (id == null) return MissingDevice(monitorRenderName, capture: false, leg: "inbound");

            resolved = new CallAudioRoute
            {
                MonitorRenderId = id,
                MonitorRenderName = NameOf(id, capture: false) ?? monitorRenderName,
            };
            return null;
        }

        // How long the recorder runs, and where inside that window the tone sits.
        // The lead-in is recorded too and used as the noise floor, so the verdict
        // is "louder than this endpoint's own silence", not "louder than a number
        // someone guessed".
        //
        // The lead-in is RENDERED as digital silence rather than waited out — see
        // the remarks on CallAudioProbe.PlayToneAsync. On an idle endpoint the
        // loopback does not deliver a packet until something plays, so a silent
        // wait produced a capture that began at the tone and a "floor" that was
        // the tone's own first 250 ms.
        private static readonly TimeSpan PreflightLeadIn = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan PreflightTone = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan PreflightWindow = TimeSpan.FromMilliseconds(1400);

        // Absolute floor, about -46 dBFS. Below this the "signal" is the endpoint
        // idling and nothing was carried.
        private const double SilenceFloor = 0.005;

        private async Task<CallAudioFault> TestLegAsync(
            string leg, string renderId, string listenId, bool loopback,
            string describe, string spokenLeg, CancellationToken cancel)
        {
            try
            {
                Task<byte[]> recording = CallAudioProbe.RecordAsync(
                    listenId, loopback, PreflightWindow, cancel);

                await CallAudioProbe.PlayToneAsync(
                    renderId, PreflightTone, leadIn: PreflightLeadIn, cancel: cancel)
                    .ConfigureAwait(false);

                byte[] captured = await recording.ConfigureAwait(false);

                int bytesPerMs = CallAudioFormat.GeminiRate / 1000 * 2;
                int floorEnd = Math.Min(captured.Length, (int)(PreflightLeadIn.TotalMilliseconds * bytesPerMs));
                int toneStart = Math.Min(captured.Length, floorEnd + 50 * bytesPerMs);

                double floor = CallAudioFormat.Rms(captured, 0, floorEnd);
                double signal = CallAudioFormat.Rms(captured, toneStart, captured.Length - toneStart);

                Console.WriteLine(
                    $"[call-audio] {leg} leg {describe}: floor={floor:F4} tone={signal:F4} " +
                    $"({captured.Length / bytesPerMs}ms captured)");

                // Both tests, not either: a leg that was already noisy passes the
                // absolute floor on its own noise, and a quiet endpoint with a
                // dead cable passes nothing.
                if (signal >= SilenceFloor && signal > floor * 3) return null;

                return new CallAudioFault(
                    $"I can't screen calls — {spokenLeg}. The audio isn't getting through.",
                    captured.Length == 0
                        ? $"{leg} leg {describe}: nothing was captured at all"
                        : $"{leg} leg {describe}: tone RMS {signal:F4} against a floor of {floor:F4} " +
                          "— the test tone did not arrive");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new CallAudioFault(
                    $"I can't screen calls — {spokenLeg}.",
                    $"{leg} leg {describe}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // --- Engaging and restoring --------------------------------------------------

        /// <summary>
        /// Points the Communications role at the call path, after writing down
        /// what it pointed at before. Returns null on success.
        /// </summary>
        public CallAudioFault Engage(out CallAudioRoute engagedRoute)
        {
            engagedRoute = null;

            CallAudioFault fault = Resolve(out CallAudioRoute r);
            if (fault != null) return fault;

            lock (gate)
            {
                if (isEngaged) { engagedRoute = route; return null; }

                // A record left over from a previous run means that run died with
                // the role still moved. Put it back BEFORE reading the current
                // defaults, or the cable gets written down as the original and
                // becomes permanent — the one way this feature could break the
                // machine for good rather than for one call.
                string repaired = RepairAfterCrash();
                if (repaired != null) Console.WriteLine($"[call-audio] {repaired}");

                AudioRole[] moving = RolesToMove;

                string currentRender = audio.GetDefaultEndpointId(capture: false, role: AudioRole.Communications);
                string currentCapture = audio.GetDefaultEndpointId(capture: true, role: AudioRole.Communications);

                // Belt and braces on the same failure: if the defaults are already
                // sitting on the call path and there was no record to repair from,
                // there is nothing safe to write down. Refuse and say so rather
                // than guess an original.
                //
                // Only the CAPTURE side is diagnostic, and the distinction is not
                // pedantic — testing render too made this refuse forever.
                //
                // The inbound leg was originally the monitor's endpoint, which is
                // never a legitimate default, so matching on it meant "a previous
                // run left this behind". Since 2026-08-16 the inbound leg is the
                // built-in speakers (the monitor endpoint turned out to vanish
                // whenever the display was off), and those are the normal comms
                // playback default — so `currentRender == MonitorRenderId` is now
                // simply the healthy resting state, and screening could never arm.
                //
                // `CABLE Output` as the communications microphone has no such
                // innocent reading: nothing sets it but this router or the VB-CABLE
                // installer, and in both cases the saved originals are the only
                // trustworthy source of what to put back.
                if (currentCapture == r.CableCaptureId)
                {
                    return new CallAudioFault(
                        "I can't screen calls — the communications audio devices are already " +
                        "set to the call path, so I don't know what to put back afterwards.",
                        $"comms defaults already on the call route (render={currentRender}, " +
                        $"capture={currentCapture}) with no saved originals");
                }

                // Exactly the roles that are about to move, so the restore path can
                // never put back a role this call never touched — a `switch_audio_output`
                // during the call would otherwise be undone on hang-up.
                saved.Clear();
                foreach (AudioRole role in moving)
                {
                    saved.Add(new SavedRole
                    {
                        Role = role,
                        RenderId = audio.GetDefaultEndpointId(capture: false, role: role),
                        CaptureId = audio.GetDefaultEndpointId(capture: true, role: role),
                    });
                }
                foreach (SavedRole s in saved)
                {
                    s.RenderName = NameOf(s.RenderId, capture: false);
                    s.CaptureName = NameOf(s.CaptureId, capture: true);
                }

                route = r;

                // Written BEFORE the switch, so a crash between the two lines
                // leaves a record that is merely redundant rather than absent.
                CallAudioStore.Save(new CallAudioState
                {
                    Engaged = true,
                    At = DateTime.Now,
                    Roles = saved,
                });

                foreach (AudioRole role in moving)
                {
                    AudioController.SetDefaultEndpoint(r.MonitorRenderId, role);
                    AudioController.SetDefaultEndpoint(r.CableCaptureId, role);
                }

                isEngaged = true;
                lock (engaged) engaged.Add(this);
            }

            Console.WriteLine(
                $"[call-audio] {(moveAllRoles ? "ALL THREE roles" : "communications role")} -> " +
                $"{r.MonitorRenderName} / {r.CableCaptureName}" +
                (moveAllRoles ? " (the wake word is deaf until this call ends)" : " (console and multimedia untouched)"));

            // READ BACK, rather than trust the setter.
            //
            // Engage used to assume its writes landed, and only Restore verified.
            // That gap cost a real phone call: the caller heard nothing, and
            // "did the default actually move?" could not be answered from the log,
            // so it had to be re-run by hand under the probe. IPolicyConfig returns
            // an HRESULT the interop deliberately ignores, so this is the only
            // evidence there is.
            foreach (AudioRole role in RolesToMove)
            {
                Confirm(role, capture: false, expected: r.MonitorRenderId);
                Confirm(role, capture: true, expected: r.CableCaptureId);
            }

            engagedRoute = r;
            return null;
        }

        private void Confirm(AudioRole role, bool capture, string expected)
        {
            string now = audio.GetDefaultEndpointId(capture, role);
            if (now == expected) return;

            Console.WriteLine(
                $"[call-audio] WARNING: the {role} {(capture ? "recording" : "playback")} default " +
                $"did NOT move — it is still {NameOf(now, capture) ?? now ?? "(none)"}. " +
                "The caller will hear silence on that leg.");
        }

        /// <summary>
        /// Puts the Communications role back exactly where it was. Safe to call
        /// any number of times, from any thread, including from a teardown hook.
        /// </summary>
        public void Restore(string why)
        {
            List<SavedRole> putBack;

            lock (gate)
            {
                if (!isEngaged) return;
                isEngaged = false;
                putBack = new List<SavedRole>(saved);
                saved.Clear();
                route = null;
            }

            lock (engaged) engaged.Remove(this);

            Console.WriteLine($"[call-audio] {why} — restoring {Describe(putBack)}");
            RestoreRoles(putBack, audio);
            CallAudioStore.Clear();
        }

        private static string Describe(List<SavedRole> roles) =>
            roles.Count == 1
                ? "the communications role"
                : $"{roles.Count} audio roles";

        // The one place roles are actually put back, shared by the instance path
        // and the disk-backed repair.
        private static void RestoreRoles(List<SavedRole> roles, AudioController audio)
        {
            if (roles == null) return;

            foreach (SavedRole role in roles)
            {
                // A null id means Windows had no default for that role when we
                // started, which is a real answer and not an error — there is
                // nothing to put back and setting something would be inventing
                // state.
                if (role.RenderId != null) Set(role.RenderId, role.Role, capture: false);
                if (role.CaptureId != null) Set(role.CaptureId, role.Role, capture: true);
            }

            // Read back rather than trust the setter. IPolicyConfig returns an
            // HRESULT the interop deliberately ignores, and the id may name a
            // device that has since been unplugged — in which case the role is
            // still wrong and somebody needs to know now, not on the next call.
            foreach (SavedRole role in roles)
            {
                Verify(audio, role.RenderId, role.Role, capture: false);
                Verify(audio, role.CaptureId, role.Role, capture: true);
            }
        }

        private static void Set(string id, AudioRole role, bool capture)
        {
            try { AudioController.SetDefaultEndpoint(id, role); }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[call-audio] COULD NOT RESTORE the {role} " +
                    $"{(capture ? "recording" : "playback")} default: {ex.Message}");
            }
        }

        private static void Verify(AudioController audio, string expected, AudioRole role, bool capture)
        {
            if (expected == null) return;

            string now = audio.GetDefaultEndpointId(capture, role);
            if (now == expected) return;

            string kind = capture ? "recording" : "playback";

            // NEVER WALK AWAY LEAVING A VIRTUAL CABLE AS THE DEFAULT.
            //
            // Measured 2026-08-17, and it cost a real call. Route B answered while
            // the AirPods were connected, so the saved Communications devices were
            // the AirPods' own — and route B then deleted those endpoints to get
            // the audio onto the PC. The restore could not reach them, warned, and
            // left the machine with CABLE Output as its Communications microphone.
            // Windows hid it for as long as the AirPods were back; the moment they
            // were switched off it fell back onto the cable again, and the NEXT
            // call refused to answer at all because the defaults already looked
            // like the call route.
            //
            // A warning was not enough. If the saved device is gone, put the role
            // on real hardware instead — a wrong-but-working microphone is
            // recoverable, a virtual cable is silence that looks like a setting.
            if (IsVirtual(NameOf(now, capture)))
            {
                string fallback = FirstRealEndpoint(audio, capture);
                if (fallback != null)
                {
                    Console.WriteLine(
                        $"[call-audio] the saved {role} {kind} device is gone, and the default fell " +
                        $"back to a virtual cable — putting it on {NameOf(fallback, capture)} instead");
                    Set(fallback, role, capture);
                    return;
                }
            }

            Console.WriteLine(
                $"[call-audio] WARNING: the {role} {kind} default is still {NameOf(now, capture) ?? now ?? "(none)"}, " +
                $"not {NameOf(expected, capture) ?? expected}. " +
                "Fix it with: AudioState.exe set \"<playback>\" \"<recording>\"");
        }

        // The virtual devices this machine actually has. Named rather than
        // detected because Windows exposes no "is this real hardware" flag, and
        // the same list already earns its keep in AudioState.
        private static bool IsVirtual(string name) =>
            name != null &&
            (name.IndexOf("cable", StringComparison.OrdinalIgnoreCase) >= 0 ||
             name.IndexOf("vb-audio", StringComparison.OrdinalIgnoreCase) >= 0 ||
             name.IndexOf("voicemeeter", StringComparison.OrdinalIgnoreCase) >= 0 ||
             name.IndexOf("voicemod", StringComparison.OrdinalIgnoreCase) >= 0 ||
             name.IndexOf("steam streaming", StringComparison.OrdinalIgnoreCase) >= 0);

        private static string FirstRealEndpoint(AudioController audio, bool capture)
        {
            try
            {
                foreach (string name in capture ? audio.ListInputDevices() : audio.ListOutputDevices())
                {
                    if (IsVirtual(name)) continue;
                    string id = audio.FindEndpointId(name, capture);
                    if (id != null) return id;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[call-audio] could not pick a fallback device: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Restores every engaged router, then sweeps the persisted record. For
        /// <c>ProcessExit</c> and <c>Console.CancelKeyPress</c>, where there is no
        /// instance to hand in and about two seconds to work with.
        /// </summary>
        public static void RestoreAll(string why)
        {
            CallAudioRouter[] live;
            lock (engaged) live = engaged.ToArray();

            foreach (CallAudioRouter router in live)
            {
                try { router.Restore(why); } catch { /* teardown; nobody left to tell */ }
            }

            // Also the disk pass, for the case where a router engaged and the
            // static list lost it (a second AppDomain, a partially initialised
            // process). Cheap, and this is the code that must not be clever.
            try
            {
                string repaired = RepairAfterCrash();
                if (repaired != null) Console.WriteLine($"[call-audio] {repaired}");
            }
            catch { }
        }

        /// <summary>
        /// Puts the Communications role back if a previous run died with it moved.
        /// Returns a one-line description of what it repaired, or null when there
        /// was nothing to do. Call it at startup, before anything opens audio.
        /// </summary>
        public static string RepairAfterCrash()
        {
            CallAudioState state = CallAudioStore.Load();
            if (state == null || !state.Engaged) return null;
            if (state.Roles == null || state.Roles.Count == 0) return null;

            var audio = new AudioController();
            RestoreRoles(state.Roles, audio);
            CallAudioStore.Clear();

            SavedRole first = state.Roles[0];
            return
                $"a call was in flight at {state.At:ddd HH:mm} — {Describe(state.Roles)} put back to " +
                $"{first.RenderName ?? first.RenderId ?? "(none)"} / " +
                $"{first.CaptureName ?? first.CaptureId ?? "(none)"}";
        }

        public void Dispose() => Restore("router disposed");
    }

    /// <summary>Where one audio role pointed before a call started.</summary>
    internal sealed class SavedRole
    {
        public AudioRole Role { get; set; }
        public string RenderId { get; set; }
        public string RenderName { get; set; }
        public string CaptureId { get; set; }
        public string CaptureName { get; set; }
    }

    // What the audio roles pointed at before a call started.
    //
    // A LIST since 2026-08-17, because a call now moves all three roles rather
    // than Communications alone — Windows' telephony stack turned out to ignore
    // the Communications role for capture. The roles genuinely can differ from
    // each other (measured 2026-08-15), so restoring one value across three roles
    // would flatten a split the user has.
    internal sealed class CallAudioState
    {
        public bool Engaged { get; set; }
        public DateTime At { get; set; }
        public List<SavedRole> Roles { get; set; } = new List<SavedRole>();
    }

    // Where the pre-call audio defaults live between runs. Same discipline as
    // EventWatchStore (EventWatch.cs:497) and CallScreeningStore: AppData, a
    // per-write temp plus File.Replace, one save lock, every failure non-fatal.
    //
    // It carries the friendly names alongside the ids purely so a failed restore
    // can be read by a human — an endpoint id is a GUID, and the message that
    // matters most in this feature is the one printed when putting the role back
    // did not work.
    internal static class CallAudioStore
    {
        public static string Path
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("LAITH_CALLAUDIO_PATH");
                if (!string.IsNullOrWhiteSpace(over)) return over.Trim();

                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LAITH",
                    "callaudio.json");
            }
        }

        private static readonly object saveGate = new object();

        public static CallAudioState Load()
        {
            try
            {
                if (!File.Exists(Path)) return null;
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path)))
                {
                    JsonElement root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return null;

                    var state = new CallAudioState
                    {
                        Engaged = root.TryGetProperty("engaged", out JsonElement e) &&
                                  e.ValueKind == JsonValueKind.True,
                        At = ReadTime(root, "at"),
                    };

                    if (root.TryGetProperty("roles", out JsonElement roles) &&
                        roles.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement entry in roles.EnumerateArray())
                        {
                            if (entry.ValueKind != JsonValueKind.Object) continue;
                            state.Roles.Add(new SavedRole
                            {
                                Role = ReadRole(entry),
                                RenderId = ReadString(entry, "render"),
                                RenderName = ReadString(entry, "render_name"),
                                CaptureId = ReadString(entry, "capture"),
                                CaptureName = ReadString(entry, "capture_name"),
                            });
                        }
                        return state;
                    }

                    // A file written before the roles list existed. It only ever
                    // described Communications — read it as that rather than
                    // discarding it, because the one moment this file matters is
                    // the first start after a crash, and a crash under the old
                    // build is exactly when it would be the old shape.
                    string legacyRender = ReadString(root, "render");
                    string legacyCapture = ReadString(root, "capture");
                    if (legacyRender != null || legacyCapture != null)
                    {
                        state.Roles.Add(new SavedRole
                        {
                            Role = AudioRole.Communications,
                            RenderId = legacyRender,
                            RenderName = ReadString(root, "render_name"),
                            CaptureId = legacyCapture,
                            CaptureName = ReadString(root, "capture_name"),
                        });
                    }

                    return state;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[call-audio] could not read {Path}: {ex.Message}");
                return null;
            }
        }

        private static AudioRole ReadRole(JsonElement entry) =>
            Enum.TryParse(ReadString(entry, "role") ?? string.Empty, ignoreCase: true, out AudioRole parsed)
                ? parsed
                // Communications is the safe default for an unreadable role: it is
                // the one every version of this file has always moved.
                : AudioRole.Communications;

        private static string ReadString(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        private static DateTime ReadTime(JsonElement root, string name)
        {
            string raw = ReadString(root, name);
            return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTime parsed)
                ? parsed
                : DateTime.MinValue;
        }

        public static void Save(CallAudioState state)
        {
            lock (saveGate)
            {
                string temp = null;
                try
                {
                    string path = Path;
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));

                    temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                    {
                        writer.WriteStartObject();
                        writer.WriteBoolean("engaged", state.Engaged);
                        writer.WriteString("at", state.At.ToString("o", CultureInfo.InvariantCulture));

                        writer.WriteStartArray("roles");
                        foreach (SavedRole role in state.Roles ?? new List<SavedRole>())
                        {
                            writer.WriteStartObject();
                            writer.WriteString("role", role.Role.ToString());
                            if (role.RenderId != null) writer.WriteString("render", role.RenderId);
                            if (role.RenderName != null) writer.WriteString("render_name", role.RenderName);
                            if (role.CaptureId != null) writer.WriteString("capture", role.CaptureId);
                            if (role.CaptureName != null) writer.WriteString("capture_name", role.CaptureName);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();

                        writer.WriteEndObject();
                    }

                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                    temp = null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[call-audio] could not save to {Path}: {ex.Message}");
                }
                finally
                {
                    if (temp != null)
                    {
                        try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    }
                }
            }
        }

        /// <summary>
        /// Marks the record not-engaged, keeping the ids for diagnosis.
        /// </summary>
        /// <remarks>
        /// Rewritten rather than deleted: when a restore does not take, the file
        /// is the only place the original device ids still exist, and deleting it
        /// would throw away the answer at the exact moment somebody needs it.
        /// </remarks>
        public static void Clear()
        {
            CallAudioState state = Load();
            if (state == null) return;
            if (!state.Engaged) return;

            state.Engaged = false;
            Save(state);
        }
    }
}
