using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Personal_Assistant.AudioControl
{
    // Which of the three Windows endpoint roles a caller means.
    //
    // Public mirror of the private ERole inside AudioController, so callers can
    // name a role without the undocumented interop enum leaking out of this file.
    // The values must keep matching ERole — they are cast across directly.
    public enum AudioRole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2
    }

    // System audio control for L.A.I.T.H.: master-volume up/down/mute/set on the
    // default playback device, plus switching which output device is the default
    // (speakers <-> headphones).
    //
    // Volume + enumeration go through NAudio's CoreAudio wrappers. Setting the
    // default endpoint has no public Windows API, so it uses the long-standing
    // undocumented IPolicyConfig COM interface (the same one EarTrumpet /
    // AudioSwitcher use).
    public class AudioController
    {
        // How much a single "volume up"/"volume down" moves the master level.
        private const float StepFraction = 0.10f; // 10 percentage points

        // Enumerator is cheap to keep alive and safe to reuse; the *default*
        // device is fetched fresh every call because it can change (that's what
        // the switch-device feature does).
        private readonly MMDeviceEnumerator enumerator = new MMDeviceEnumerator();

        private MMDevice GetDefaultRenderDevice() =>
            enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        // Raises the master volume one step. Returns the resulting 0-100 percent.
        public int VolumeUp() => Adjust(StepFraction);

        // Lowers the master volume one step. Returns the resulting 0-100 percent.
        public int VolumeDown() => Adjust(-StepFraction);

        private int Adjust(float delta)
        {
            using (var device = GetDefaultRenderDevice())
            {
                var vol = device.AudioEndpointVolume;
                float current = vol.MasterVolumeLevelScalar;
                float target = Clamp01(current + delta);
                vol.MasterVolumeLevelScalar = target;
                // Nudging volume up from a muted state should unmute — matches
                // what pressing the volume keys does.
                if (vol.Mute && target > 0f) vol.Mute = false;
                return ToPercent(target);
            }
        }

        // Sets the master volume to an absolute 0-100 percent. Returns the
        // clamped value actually applied.
        public int SetVolume(int percent)
        {
            int clamped = Math.Max(0, Math.Min(100, percent));
            using (var device = GetDefaultRenderDevice())
            {
                var vol = device.AudioEndpointVolume;
                vol.MasterVolumeLevelScalar = clamped / 100f;
                if (vol.Mute && clamped > 0) vol.Mute = false;
                return clamped;
            }
        }

        public void Mute() => SetMute(true);

        public void Unmute() => SetMute(false);

        private void SetMute(bool muted)
        {
            using (var device = GetDefaultRenderDevice())
            {
                device.AudioEndpointVolume.Mute = muted;
            }
        }

        // Current default-device state, for spoken feedback.
        public int CurrentVolumePercent()
        {
            using (var device = GetDefaultRenderDevice())
            {
                return ToPercent(device.AudioEndpointVolume.MasterVolumeLevelScalar);
            }
        }

        // --- Output-device switching -------------------------------------------------

        // Friendly names of the active playback devices (e.g. "Speakers (Realtek
        // Audio)", "Headphones (Arctis 7)"). Used to tell the user what's
        // available when a switch request doesn't match.
        public IReadOnlyList<string> ListOutputDevices()
        {
            var names = new List<string>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device) names.Add(device.FriendlyName);
            }
            return names;
        }

        // Makes the active render device whose friendly name (or adapter name)
        // contains `spokenName` the default for all roles. Returns the matched
        // device's friendly name, or null if nothing matched.
        public string SwitchOutputDevice(string spokenName)
        {
            if (string.IsNullOrWhiteSpace(spokenName)) return null;
            string needle = spokenName.Trim();

            string matchedId = null;
            string matchedName = null;

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    if (Contains(device.FriendlyName, needle) ||
                        Contains(SafeAdapterName(device), needle))
                    {
                        matchedId = device.ID;
                        matchedName = device.FriendlyName;
                        break;
                    }
                }
            }

            if (matchedId == null) return null;

            SetDefaultEndpoint(matchedId);
            return matchedName;
        }

        // Adapter/device-description name; guarded because some drivers throw
        // when the property store is read.
        private static string SafeAdapterName(MMDevice device)
        {
            try { return device.DeviceFriendlyName; }
            catch { return string.Empty; }
        }

        private static bool Contains(string haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) &&
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private static void SetDefaultEndpoint(string deviceId)
        {
            // Set for every role so audio, comms, and system sounds all move.
            // This is what "switch to my headphones" means — the user is not
            // thinking about roles, they want everything to follow.
            SetDefaultEndpoint(deviceId, AudioRole.Console);
            SetDefaultEndpoint(deviceId, AudioRole.Multimedia);
            SetDefaultEndpoint(deviceId, AudioRole.Communications);
        }

        // --- Role-scoped routing (call screening) ------------------------------------

        // Points a single role at a device, leaving the other two alone.
        //
        // Call screening needs exactly this: Phone Link follows the
        // *Communications* role, so moving only that role sends call audio down a
        // virtual cable while music, system sounds and L.A.I.T.H.'s own voice stay
        // on the real speakers. Moving all three (what SwitchOutputDevice does)
        // would drag the whole desktop onto a cable nobody can hear.
        public static void SetDefaultEndpoint(string deviceId, AudioRole role)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                throw new ArgumentException("device id is required", nameof(deviceId));

            var policyConfig = (IPolicyConfig)new CPolicyConfigClient();
            try
            {
                policyConfig.SetDefaultEndpoint(deviceId, (ERole)role);
            }
            finally
            {
                Marshal.ReleaseComObject(policyConfig);
            }
        }

        // The device currently serving `role`, or null if Windows has none (no
        // audio hardware, or every endpoint disabled).
        //
        // This is what gets written down before a call so the originals can be put
        // back afterwards. Leaving the Communications role on a virtual cable is
        // the worst failure this feature has: the next real call or meeting is
        // silently broken with nothing on screen to explain why.
        public string GetDefaultEndpointId(bool capture, AudioRole role)
        {
            try
            {
                using (var device = enumerator.GetDefaultAudioEndpoint(
                    capture ? DataFlow.Capture : DataFlow.Render, (Role)role))
                {
                    return device?.ID;
                }
            }
            catch (Exception ex)
            {
                // Genuinely happens on a machine with no default for the role.
                // Null means "there was nothing to restore", which is a real
                // answer, not an error.
                Console.WriteLine($"[audio] no default {(capture ? "capture" : "render")} device for {role}: {ex.Message}");
                return null;
            }
        }

        // Friendly names of the active recording devices, mirroring
        // ListOutputDevices. Needed because the inbound leg of a call is a
        // *capture* endpoint, and nothing in this class could see one before.
        public IReadOnlyList<string> ListInputDevices()
        {
            var names = new List<string>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device) names.Add(device.FriendlyName);
            }
            return names;
        }

        // Resolves a configured device name to its endpoint id, or null.
        //
        // Deliberately stricter than SwitchOutputDevice's matching. That one is
        // driven by speech ("switch to my headphones") where a loose substring is
        // the point. This one is driven by a config key naming a specific virtual
        // cable, on a machine that also has a Voicemod virtual device and two
        // Steam ones — so an exact friendly-name match is tried first, and a
        // substring match is only accepted when it is *unambiguous*. Routing a
        // call into the wrong virtual device is silent, and silence is exactly
        // what it looks like when it works.
        public string FindEndpointId(string name, bool capture)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string needle = name.Trim();

            var exact = new List<KeyValuePair<string, string>>();   // id -> friendly name
            var partial = new List<KeyValuePair<string, string>>();

            foreach (var device in enumerator.EnumerateAudioEndPoints(
                capture ? DataFlow.Capture : DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    if (string.Equals(device.FriendlyName, needle, StringComparison.OrdinalIgnoreCase))
                        exact.Add(new KeyValuePair<string, string>(device.ID, device.FriendlyName));
                    else if (Contains(device.FriendlyName, needle))
                        partial.Add(new KeyValuePair<string, string>(device.ID, device.FriendlyName));
                }
            }

            string kind = capture ? "recording" : "playback";

            if (exact.Count == 1) return exact[0].Key;
            if (partial.Count == 1) return partial[0].Key;

            if (partial.Count > 1)
            {
                Console.WriteLine(
                    $"[audio] '{needle}' matches {partial.Count} active {kind} devices; " +
                    "refusing to guess. Use the full device name:");
                foreach (var entry in partial)
                    Console.WriteLine($"[audio]   {entry.Value}");
            }
            else
            {
                Console.WriteLine($"[audio] no active {kind} device matches '{needle}'.");
            }

            return null;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        private static int ToPercent(float scalar) => (int)Math.Round(scalar * 100f);

        // --- Undocumented IPolicyConfig interop --------------------------------------

        private enum ERole
        {
            eConsole = 0,
            eMultimedia = 1,
            eCommunications = 2
        }

        // The method order matters: it must match the COM vtable exactly, so the
        // earlier (unused) methods are declared as opaque PreserveSig stubs and
        // only SetDefaultEndpoint is given real marshalling.
        [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat(string deviceName, IntPtr format);
            [PreserveSig] int GetDeviceFormat(string deviceName, bool bDefault, IntPtr format);
            [PreserveSig] int ResetDeviceFormat(string deviceName);
            [PreserveSig] int SetDeviceFormat(string deviceName, IntPtr endpointFormat, IntPtr mixFormat);
            [PreserveSig] int GetProcessingPeriod(string deviceName, bool bDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
            [PreserveSig] int SetProcessingPeriod(string deviceName, IntPtr period);
            [PreserveSig] int GetShareMode(string deviceName, IntPtr mode);
            [PreserveSig] int SetShareMode(string deviceName, IntPtr mode);
            [PreserveSig] int GetPropertyValue(string deviceName, bool bFxStore, IntPtr key, IntPtr value);
            [PreserveSig] int SetPropertyValue(string deviceName, bool bFxStore, IntPtr key, IntPtr value);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
            [PreserveSig] int SetEndpointVisibility(string deviceName, bool visible);
        }

        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
        private class CPolicyConfigClient
        {
        }
    }
}
