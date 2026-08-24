using Personal_Assistant.Triggers;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Personal_Assistant.CallScreening
{
    /// <summary>
    /// The proven transport: a call arriving through Phone Link over Bluetooth.
    /// </summary>
    /// <remarks>
    /// A WRAPPER, deliberately, and not a rewrite. PhoneLinkCallWatcher and
    /// PhoneLinkCallController have both been through seven real calls and carry a
    /// measured UI map that cost one call per line to learn; the value of an
    /// ICallTransport seam is not worth disturbing a byte of that. So this holds
    /// the two of them and forwards.
    ///
    /// It is still the better route whenever the phone IS at home — no carrier
    /// forwarding, no browser, no Google in the path. Google Voice exists because
    /// Bluetooth range meant screening only worked when Layth was close enough to
    /// his own phone to answer it himself, not because this stopped working.
    ///
    /// The BluetoothHeadset is passed IN rather than constructed here. That looks
    /// like a nicety and is not: CallScreeningService reaches for the same headset
    /// on its own (Reconnect after a call, RepairFromDisk at startup), and two
    /// instances of a thing that disables and re-enables a hardware device would
    /// disagree about WasDisconnected. The app has had exactly this bug once
    /// already, with a second SpeechService quietly killing the echo gate.
    /// </remarks>
    public sealed class PhoneLinkCallTransport : ICallTransport
    {
        private readonly PhoneLinkCallController controller = new PhoneLinkCallController();
        private readonly PhoneLinkCallWatcher watcher;

        public PhoneLinkCallTransport(
            TriggerService triggers,
            BluetoothHeadset headset,
            Func<bool> isArmed,
            Func<IncomingCall, Task> onIncomingCall,
            TimeSpan? pollInterval = null)
        {
            if (headset == null) throw new ArgumentNullException(nameof(headset));

            // ROUTE B'S MISSING PIECE. A seam rather than a direct call, because
            // BluetoothHeadset returns false when the headset is genuinely still
            // connected afterwards, and route B reads that as "do not expect the
            // transfer to work" and hangs up cleanly rather than leaving a caller
            // connected to nothing.
            controller.DisconnectHeadset = headset.Disconnect;

            watcher = new PhoneLinkCallWatcher(triggers, isArmed, onIncomingCall, pollInterval);
        }

        public string Name => "phone link";

        // The reason BluetoothHeadset exists at all — see ICallTransport.
        public bool RequiresHeadsetDisconnect => true;

        // It answers a phone that is ringing in his hand, so it must never sit
        // armed by default. See ICallTransport.AnswersOnlyMissedCalls.
        public bool AnswersOnlyMissedCalls => false;

        /// <summary>Phone Link cannot be driven if its app is not running.</summary>
        public ArmRefusal NotReady()
        {
            try
            {
                Process[] processes = Process.GetProcessesByName("PhoneExperienceHost");
                foreach (Process p in processes) p.Dispose();
                if (processes.Length > 0) return null;
            }
            catch
            {
                // Unable to look is not the same as knowing it is absent; let the
                // arm proceed and fail loudly later if it really is missing.
                return null;
            }

            return new ArmRefusal(
                "I can't screen calls while Phone Link isn't running.",
                "PhoneExperienceHost is not running");
        }

        public void Start() => watcher.Start();
        public AnswerResult Answer() => controller.Answer();
        public bool Decline() => controller.Decline();
        public bool HangUp(int attempts = 2) => controller.HangUp(attempts);
        public CallLocation CurrentLocation() => controller.CurrentLocation();
        public string Describe(CallLocation location) => PhoneLinkCallController.Describe(location);

        public void Dispose() => watcher?.Dispose();
    }
}
