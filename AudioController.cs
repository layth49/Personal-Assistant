using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Personal_Assistant.AudioControl
{
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
            var policyConfig = (IPolicyConfig)new CPolicyConfigClient();
            try
            {
                // Set for every role so audio, comms, and system sounds all move.
                policyConfig.SetDefaultEndpoint(deviceId, ERole.eConsole);
                policyConfig.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
                policyConfig.SetDefaultEndpoint(deviceId, ERole.eCommunications);
            }
            finally
            {
                Marshal.ReleaseComObject(policyConfig);
            }
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
