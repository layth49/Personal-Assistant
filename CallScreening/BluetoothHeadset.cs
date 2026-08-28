using NAudio.CoreAudioApi;
using Personal_Assistant.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace Personal_Assistant.CallScreening
{
    /// <summary>
    /// Drops a Bluetooth headset's audio services from the PC so Phone Link will
    /// put call audio on the laptop, and puts them back afterwards.
    /// </summary>
    //
    // WHY THIS EXISTS
    //
    // Phone Link refuses to carry call audio on the PC while a Bluetooth headset
    // is connected to the PC. It does not disable the button — it SWAPS it, from
    // `Accept on PC` to `Use mobile device`, which answers on the handset instead.
    // With AirPods on, autonomous screening therefore answers into a path the
    // assistant cannot hear or speak on.
    //
    // Route B is the way out, and its one unverified step was confirmed by hand on
    // 2026-08-17: answer with `Use mobile device` (which auto-accepts on the
    // phone), disconnect the headset from the PC, then click `Transfer call to PC`
    // and the title becomes `Call on PC`. Answering first is what makes it safe —
    // it stops the twenty-second ring clock, so the disconnect and the transfer
    // happen against a connected call with no deadline.
    //
    // WHY SERVICE STATE AND NOT THE DEVICE NODE
    //
    // The two candidate mechanisms were `BluetoothSetServiceState` against the
    // audio service GUIDs, and disabling the PnP device node. The device node
    // needs elevation and takes the whole device away; the service state is a
    // per-device, per-service switch that the Bluetooth control panel itself uses,
    // and it leaves the pairing intact. So: disable A2DP and Hands-Free, leave
    // everything else alone, and re-enable on the way out.
    //
    // THE FAILURE MODE THAT MATTERS
    //
    // A disabled audio service is PERSISTENT. If this process dies between the
    // disconnect and the restore, the headset stays silent on this PC until
    // something re-enables it — and nothing in Windows' UI explains why. That is
    // the same hazard as the audio-role switch and it gets the same three
    // answers: restore in a finally, restore in the teardown hooks, and restore
    // from disk on the next start. `headset.json` is written BEFORE the first
    // service is disabled.
    public sealed class BluetoothHeadset
    {
        // The audio services worth dropping, and only those.
        //
        // A PC pairs with AirPods as an A2DP *source* and a Hands-Free *audio
        // gateway*. Disabling those two is what makes Windows stop treating the
        // headset as an audio device. AVRCP (remote control) and anything else the
        // device advertises are left alone: they are not what Phone Link objects
        // to, and every service touched is one more to put back.
        private static readonly Guid AdvancedAudioDistribution =
            new Guid("0000110D-0000-1000-8000-00805F9B34FB");
        private static readonly Guid AudioSink =
            new Guid("0000110B-0000-1000-8000-00805F9B34FB");
        private static readonly Guid Handsfree =
            new Guid("0000111E-0000-1000-8000-00805F9B34FB");

        private static readonly Guid[] AudioServices =
            { AdvancedAudioDistribution, AudioSink, Handsfree };

        private const uint ServiceDisable = 0x00;
        private const uint ServiceEnable = 0x01;
        private const uint Success = 0;

        // Bluetooth "major device class" for Audio/Video, which is how a headset
        // is recognised without depending on what the user named it. Matching on
        // the name alone would miss a renamed headset and would match a phone
        // called "Layth's AirPods".
        private const uint MajorClassAudioVideo = 0x04;

        private readonly object gate = new object();
        private readonly string only;
        private HeadsetState disabled;

        public BluetoothHeadset()
        {
            // Empty means "any connected audio device", which is the right default:
            // the thing that breaks screening is a headset being connected, not
            // which headset it is.
            only = LaithConfig.Text("CallHeadsetName", string.Empty);
        }

        /// <summary>
        /// Every paired Bluetooth device that is currently connected and looks like
        /// an audio device.
        /// </summary>
        public List<string> Connected()
        {
            var names = new List<string>();
            foreach (Device device in Enumerate())
                if (device.IsAudio && Matches(device.Name)) names.Add(device.Name);
            return names;
        }

        /// <summary>
        /// True when a headset is connected to the PC, which is the condition that
        /// makes Phone Link answer on the handset instead.
        /// </summary>
        public bool AnyConnected() => Connected().Count > 0;

        /// <summary>
        /// True while this router has a headset disabled and not yet restored — so
        /// callers can tell "no headset here" from "I took the headset away".
        /// </summary>
        public bool WasDisconnected { get { lock (gate) return disabled != null; } }

        /// <summary>
        /// Disables the audio services of every connected headset, and waits for
        /// Windows to actually drop the connection.
        /// </summary>
        /// <returns>
        /// True only if a headset was found AND is no longer connected afterwards.
        /// A false return means the caller should not expect the transfer to work —
        /// route B's own code treats it that way and hangs up cleanly.
        /// </returns>
        public bool Disconnect()
        {
            List<Device> headsets = new List<Device>();
            foreach (Device device in Enumerate())
                if (device.IsAudio && Matches(device.Name)) headsets.Add(device);

            if (headsets.Count == 0)
            {
                // ALREADY DONE COUNTS AS DONE. Route B reads a false return as
                // "do not expect the transfer to work" and hangs up cleanly, so
                // once the ring-time disconnect exists this has to distinguish
                // "there was never a headset / I could not drop it" from "I
                // already dropped it a moment ago".
                if (WasDisconnected)
                {
                    Console.WriteLine("[headset] already disconnected earlier in this call");
                    return true;
                }

                Console.WriteLine("[headset] nothing connected to disconnect");
                return false;
            }

            var record = new HeadsetState { At = DateTime.Now };

            // Persisted BEFORE the first service is touched, so a crash in the
            // middle leaves a record that is merely redundant rather than absent.
            foreach (Device device in headsets)
                record.Devices.Add(new SavedHeadset { Address = device.Address, Name = device.Name });
            HeadsetStore.Save(record);

            lock (gate) disabled = record;

            bool anyChanged = false;
            IntPtr radio = IntPtr.Zero;
            IntPtr find = IntPtr.Zero;
            try
            {
                find = OpenRadio(out radio);
                foreach (Device device in headsets)
                {
                    List<Guid> installed = find == IntPtr.Zero
                        ? new List<Guid>(AudioServices)
                        : InstalledServices(radio, device);

                    // Fall back to the guessed list only if the query came back
                    // empty, which would otherwise mean disabling nothing at all.
                    if (installed.Count == 0) installed = new List<Guid>(AudioServices);

                    // Named, because each one costs ~2s of a caller's dead air and
                    // the list is the only way to tell whether any of them could be
                    // skipped.
                    Console.WriteLine(
                        $"[headset] dropping {installed.Count} service(s) on {device.Name}: " +
                        string.Join(", ", installed.ConvertAll(Short)));

                    SavedHeadset record2 = record.Devices.Find(d => d.Address == device.Address);
                    foreach (Guid service in installed)
                    {
                        if (!SetService(radio, device, service, ServiceDisable)) continue;
                        anyChanged = true;

                        // Recorded as it happens, so the restore re-enables exactly
                        // what was disabled — including on the next start, if this
                        // process does not survive to do it itself.
                        record2?.Services.Add(service);
                    }
                }
            }
            finally
            {
                if (radio != IntPtr.Zero) CloseHandle(radio);
                if (find != IntPtr.Zero) BluetoothFindRadioClose(find);
            }

            if (anyChanged) HeadsetStore.Save(record);

            if (!anyChanged)
            {
                // Nothing was accepted, so there is nothing to put back and the
                // record would be a lie that costs the next start a pointless
                // repair.
                lock (gate) disabled = null;
                HeadsetStore.Clear();
                return false;
            }

            // WAIT FOR IT, rather than assuming. The service call returns as soon
            // as the request is accepted; Windows tears the audio link down
            // afterwards, and clicking `Transfer call to PC` while it is still up
            // is the same failed transfer this was built to avoid.
            bool gone = WaitUntilGone(headsets, TimeSpan.FromSeconds(4));
            Console.WriteLine(
                gone
                    ? "[headset] gone from the audio endpoint list — Phone Link will offer Accept on PC"
                    : "[headset] the headset is STILL an audio endpoint after 4s — the transfer will likely fail");
            return gone;
        }

        /// <summary>Re-enables whatever <see cref="Disconnect"/> disabled.</summary>
        public void Reconnect(string why)
        {
            HeadsetState record;
            lock (gate)
            {
                record = disabled;
                disabled = null;
            }

            if (record == null) return;

            Console.WriteLine($"[headset] {why} — restoring the headset's audio services");
            RestoreFrom(record);
            HeadsetStore.Clear();
        }

        /// <summary>
        /// Re-enables services left disabled by a process that died mid-call.
        /// Returns a line to say out loud, or null when there was nothing to do.
        /// </summary>
        public static string RepairFromDisk()
        {
            HeadsetState record = HeadsetStore.Load();
            if (record == null || record.Devices.Count == 0) return null;

            RestoreFrom(record);
            HeadsetStore.Clear();

            string names = string.Join(", ", record.Devices.ConvertAll(d => d.Name ?? "a headset"));
            return $"a call was in flight at {record.At:ddd HH:mm} — re-enabled Bluetooth audio for {names}";
        }

        private static void RestoreFrom(HeadsetState record)
        {
            foreach (SavedHeadset saved in record.Devices)
            {
                // Re-enabling works off the address alone, which matters: the
                // headset may well be out of range or powered off by now, and it
                // must still come back enabled for the next time it appears.
                var device = new Device { Address = saved.Address, Name = saved.Name };

                // The recorded list, falling back to the guessed one only for a
                // record written before the list was kept.
                List<Guid> services = saved.Services != null && saved.Services.Count > 0
                    ? saved.Services
                    : new List<Guid>(AudioServices);

                foreach (Guid service in services)
                    SetService(device, service, ServiceEnable);
            }
        }

        // A LIVE read, not the enumeration's cached one.
        //
        // Measured 2026-08-17: the AirPods really did drop — Layth watched it
        // happen — while this reported "STILL connected after 4s" the whole time.
        // `fConnected` as returned by BluetoothFindFirstDevice is a snapshot from
        // the enumeration and does not track the link promptly, so the check was
        // wrong about the one thing it existed to decide. BluetoothGetDeviceInfo
        // re-reads a single known device from the radio, which is both current and
        // far cheaper — the old check re-enumerated every paired device on this PC
        // four times a second and took ~2s per pass, which is most of where the
        // 10.8s disconnect went.
        private static bool StillConnected(IntPtr radio, Device device)
        {
            BLUETOOTH_DEVICE_INFO info = device.ToInfo();
            return BluetoothGetDeviceInfo(radio, ref info) == Success && info.fConnected;
        }

        /// <summary>
        /// True while Windows still exposes an audio endpoint for this headset.
        /// </summary>
        //
        // THE ENDPOINT IS THE QUESTION, NOT THE BLUETOOTH LINK.
        //
        // Two rounds of measurement on real AirPods (2026-08-17) reported "STILL
        // connected" while Layth watched the headset actually drop. Both the
        // enumeration's cached `fConnected` and a live BluetoothGetDeviceInfo say
        // the same thing, and they are not wrong — they report the baseband link,
        // which AirPods hold or immediately re-establish even with every audio
        // profile disabled. So "is the device connected" was never the question.
        //
        // What Phone Link objects to is a Bluetooth headset being available as an
        // AUDIO DEVICE, and the plan already recorded the tell: the AirPods appear
        // in the endpoint list as `Headphones (Layth's AirPods Pro)`. Presence
        // there is what swaps the toast button, so absence from there is what
        // route B has to wait for. It is also far cheaper to ask.
        private static bool AudioEndpointPresent(IEnumerable<Device> headsets)
        {
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    foreach (MMDevice endpoint in
                             enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
                    {
                        using (endpoint)
                        {
                            string name = endpoint.FriendlyName ?? string.Empty;
                            foreach (Device device in headsets)
                            {
                                if (string.IsNullOrWhiteSpace(device.Name)) continue;

                                // The endpoint name WRAPS the device name —
                                // "Headphones (Layth's AirPods Pro)" — so the
                                // containment runs this way round.
                                if (name.IndexOf(device.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                                    return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Better to report the headset as still present than to claim it is
                // gone on the strength of a failed query: the false positive costs
                // a refused transfer, the false negative costs a caller.
                Console.WriteLine($"[headset] could not read the endpoint list: {ex.Message}");
                return true;
            }

            return false;
        }

        private bool WaitUntilGone(List<Device> headsets, TimeSpan limit)
        {
            DateTime deadline = DateTime.Now.Add(limit);
            while (DateTime.Now < deadline)
            {
                if (!AudioEndpointPresent(headsets)) return true;
                Thread.Sleep(150);
            }
            return !AudioEndpointPresent(headsets);
        }

        private bool Matches(string name) =>
            string.IsNullOrWhiteSpace(only) ||
            (name != null && name.IndexOf(only, StringComparison.OrdinalIgnoreCase) >= 0);

        // --- the Win32 side ------------------------------------------------------

        /// <summary>
        /// The service GUIDs this device actually has installed.
        /// </summary>
        //
        // ASK, DO NOT GUESS. The first cut disabled a hardcoded list of three
        // audio GUIDs and one of them — AdvancedAudioDistribution (110D) — came
        // back "the specified service does not exist as an installed service" on
        // real AirPods (measured 2026-08-17). A guessed list fails loudly on the
        // services that are missing and, worse, silently misses any the device has
        // that are not on the list; leaving even one audio service installed is
        // enough for Windows to keep treating the headset as an audio device.
        private static List<Guid> InstalledServices(IntPtr radio, Device device)
        {
            var services = new List<Guid>();
            BLUETOOTH_DEVICE_INFO info = device.ToInfo();

            uint count = 0;
            uint probe = BluetoothEnumerateInstalledServices(radio, ref info, ref count, null);

            // 122 is ERROR_INSUFFICIENT_BUFFER, which is the SUCCESS path of the
            // sizing call: it means there are services and here is how many.
            if (count == 0) return services;
            if (probe != Success && probe != 122) return services;

            var buffer = new Guid[count];
            if (BluetoothEnumerateInstalledServices(radio, ref info, ref count, buffer) != Success)
                return services;

            for (int i = 0; i < count && i < buffer.Length; i++) services.Add(buffer[i]);
            return services;
        }

        // Opens its own radio. Only for the paths that have no radio in hand —
        // inside a loop this is the expensive way round, and route B pays for it
        // in dead air the caller is listening to.
        private static bool SetService(Device device, Guid service, uint flag)
        {
            IntPtr radio = IntPtr.Zero;
            IntPtr find = IntPtr.Zero;
            try
            {
                find = OpenRadio(out radio);
                if (find == IntPtr.Zero)
                {
                    Console.WriteLine("[headset] no Bluetooth radio on this PC");
                    return false;
                }
                return SetService(radio, device, service, flag);
            }
            finally
            {
                if (radio != IntPtr.Zero) CloseHandle(radio);
                if (find != IntPtr.Zero) BluetoothFindRadioClose(find);
            }
        }

        private static bool SetService(IntPtr radio, Device device, Guid service, uint flag)
        {
            try
            {
                BLUETOOTH_DEVICE_INFO info = device.ToInfo();
                uint result = BluetoothSetServiceState(radio, ref info, ref service, flag);
                if (result == Success) return true;

                // 1060 is ERROR_SERVICE_DOES_NOT_EXIST — the device simply does not
                // advertise this profile. Not a failure worth shouting about now
                // that the list is enumerated rather than guessed.
                if (result == 1060) return false;

                // 5 is ERROR_ACCESS_DENIED, which is the answer to the open
                // question of whether this needs elevation — say so plainly rather
                // than reporting a generic failure, because it decides whether
                // route B is viable at all.
                Console.WriteLine(
                    $"[headset] {(flag == ServiceDisable ? "disabling" : "enabling")} " +
                    $"{Short(service)} on {device.Name} failed: " +
                    (result == 5
                        ? "access denied — this needs elevation"
                        : new System.ComponentModel.Win32Exception((int)result).Message));
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[headset] service call threw: {ex.Message}");
                return false;
            }
        }

        private static string Short(Guid service) =>
            service == Handsfree ? "hands-free"
            : service == AudioSink ? "A2DP sink"
            : service == AdvancedAudioDistribution ? "A2DP"
            : service.ToString();

        // Opens the first radio. Returns the find handle (which the caller must
        // close alongside the radio) or IntPtr.Zero when this PC has no Bluetooth.
        private static IntPtr OpenRadio(out IntPtr radio)
        {
            var search = new BLUETOOTH_FIND_RADIO_PARAMS
            {
                dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>(),
            };
            return BluetoothFindFirstRadio(ref search, out radio);
        }

        private static IEnumerable<Device> Enumerate()
        {
            IntPtr radio = IntPtr.Zero;
            IntPtr radioFind = IntPtr.Zero;
            var found = new List<Device>();

            try
            {
                var radioParams = new BLUETOOTH_FIND_RADIO_PARAMS
                {
                    dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>(),
                };
                radioFind = BluetoothFindFirstRadio(ref radioParams, out radio);
                if (radioFind == IntPtr.Zero) return found;

                var search = new BLUETOOTH_DEVICE_SEARCH_PARAMS
                {
                    dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
                    fReturnAuthenticated = true,
                    fReturnRemembered = true,
                    fReturnConnected = true,
                    fReturnUnknown = false,

                    // No inquiry. An active scan takes seconds and this runs while
                    // a caller is already connected and waiting.
                    fIssueInquiry = false,
                    cTimeoutMultiplier = 0,
                    hRadio = radio,
                };

                var info = new BLUETOOTH_DEVICE_INFO
                {
                    dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>(),
                };

                IntPtr deviceFind = BluetoothFindFirstDevice(ref search, ref info);
                if (deviceFind == IntPtr.Zero) return found;

                try
                {
                    do
                    {
                        if (info.fConnected)
                        {
                            found.Add(new Device
                            {
                                Address = info.Address,
                                Name = info.szName,
                                ClassOfDevice = info.ulClassofDevice,
                            });
                        }

                        info = new BLUETOOTH_DEVICE_INFO
                        {
                            dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>(),
                        };
                    }
                    while (BluetoothFindNextDevice(deviceFind, ref info));
                }
                finally { BluetoothFindDeviceClose(deviceFind); }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[headset] could not enumerate Bluetooth devices: {ex.Message}");
            }
            finally
            {
                if (radio != IntPtr.Zero) CloseHandle(radio);
                if (radioFind != IntPtr.Zero) BluetoothFindRadioClose(radioFind);
            }

            return found;
        }

        private sealed class Device
        {
            public ulong Address;
            public string Name;
            public uint ClassOfDevice;

            public bool IsAudio => ((ClassOfDevice >> 8) & 0x1F) == MajorClassAudioVideo;

            public BLUETOOTH_DEVICE_INFO ToInfo() => new BLUETOOTH_DEVICE_INFO
            {
                dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>(),
                Address = Address,
                szName = Name ?? string.Empty,
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_FIND_RADIO_PARAMS
        {
            public uint dwSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEMTIME
        {
            public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
        }

        // The 4 bytes of padding between dwSize and Address are implicit: the C
        // struct has a ULONGLONG union there, and default sequential packing
        // aligns it the same way. Getting this wrong shifts szName and the name
        // comes back as garbage, which is the tell.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct BLUETOOTH_DEVICE_INFO
        {
            public uint dwSize;
            public ulong Address;
            public uint ulClassofDevice;
            [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
            [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
            [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
            public SYSTEMTIME stLastSeen;
            public SYSTEMTIME stLastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            public uint dwSize;
            [MarshalAs(UnmanagedType.Bool)] public bool fReturnAuthenticated;
            [MarshalAs(UnmanagedType.Bool)] public bool fReturnRemembered;
            [MarshalAs(UnmanagedType.Bool)] public bool fReturnUnknown;
            [MarshalAs(UnmanagedType.Bool)] public bool fReturnConnected;
            [MarshalAs(UnmanagedType.Bool)] public bool fIssueInquiry;
            public byte cTimeoutMultiplier;
            public IntPtr hRadio;
        }

        // bthprops.cpl is the documented host for these, and is present on every
        // Windows with a Bluetooth stack.
        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern IntPtr BluetoothFindFirstRadio(
            ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, out IntPtr phRadio);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern bool BluetoothFindRadioClose(IntPtr hFind);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern IntPtr BluetoothFindFirstDevice(
            ref BLUETOOTH_DEVICE_SEARCH_PARAMS pbtsp, ref BLUETOOTH_DEVICE_INFO pbtdi);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern bool BluetoothFindNextDevice(
            IntPtr hFind, ref BLUETOOTH_DEVICE_INFO pbtdi);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern bool BluetoothFindDeviceClose(IntPtr hFind);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern uint BluetoothGetDeviceInfo(
            IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi);

        // pGuidServices is null on the sizing call, hence the array being nullable
        // rather than a ref — passing a zero-length array would ask for zero
        // services rather than asking how many there are.
        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern uint BluetoothEnumerateInstalledServices(
            IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi, ref uint pcServiceInout,
            [In, Out] Guid[] pGuidServices);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern uint BluetoothSetServiceState(
            IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi, ref Guid pGuidService, uint dwServiceFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }

    internal sealed class SavedHeadset
    {
        public ulong Address { get; set; }
        public string Name { get; set; }

        // Exactly the services that were disabled, recorded as each one succeeds.
        // Re-enabling a guessed list would both miss services this device has and
        // enable ones it never had — and the restore is the half that runs when
        // nobody is watching.
        public List<Guid> Services { get; set; } = new List<Guid>();
    }

    internal sealed class HeadsetState
    {
        public DateTime At { get; set; }
        public List<SavedHeadset> Devices { get; set; } = new List<SavedHeadset>();
    }

    // Copied from CallAudioStore, which was copied from EventWatchStore — same
    // discipline: write to a temp file and move it into place, treat a corrupt
    // file as absent, and never let a store failure take the call down.
    internal static class HeadsetStore
    {
        private static readonly object gate = new object();

        private static string Path => System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "callscreening", "headset.json");

        public static void Save(HeadsetState state)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                    string temp = Path + ".tmp";

                    using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
                    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("at", state.At.ToString("o", CultureInfo.InvariantCulture));
                        writer.WriteStartArray("devices");
                        foreach (SavedHeadset device in state.Devices)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("address", device.Address.ToString(CultureInfo.InvariantCulture));
                            if (device.Name != null) writer.WriteString("name", device.Name);
                            writer.WriteStartArray("services");
                            foreach (Guid service in device.Services ?? new List<Guid>())
                                writer.WriteStringValue(service.ToString());
                            writer.WriteEndArray();
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }

                    if (File.Exists(Path)) File.Delete(Path);
                    File.Move(temp, Path);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[headset] could not persist the record: {ex.Message}");
            }
        }

        public static HeadsetState Load()
        {
            try
            {
                lock (gate)
                {
                    if (!File.Exists(Path)) return null;

                    using (var document = JsonDocument.Parse(File.ReadAllText(Path)))
                    {
                        JsonElement root = document.RootElement;
                        var state = new HeadsetState();

                        if (root.TryGetProperty("at", out JsonElement at) &&
                            at.ValueKind == JsonValueKind.String &&
                            DateTime.TryParse(at.GetString(), CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out DateTime parsed))
                        {
                            state.At = parsed;
                        }

                        if (root.TryGetProperty("devices", out JsonElement devices) &&
                            devices.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement entry in devices.EnumerateArray())
                            {
                                if (entry.ValueKind != JsonValueKind.Object) continue;
                                if (!entry.TryGetProperty("address", out JsonElement address)) continue;
                                if (!ulong.TryParse(address.GetString(), NumberStyles.Integer,
                                        CultureInfo.InvariantCulture, out ulong value)) continue;

                                var saved = new SavedHeadset
                                {
                                    Address = value,
                                    Name = entry.TryGetProperty("name", out JsonElement name)
                                        ? name.GetString() : null,
                                };

                                if (entry.TryGetProperty("services", out JsonElement services) &&
                                    services.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (JsonElement service in services.EnumerateArray())
                                        if (Guid.TryParse(service.GetString(), out Guid parsedService))
                                            saved.Services.Add(parsedService);
                                }

                                state.Devices.Add(saved);
                            }
                        }

                        return state;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[headset] the record is unreadable, ignoring it: {ex.Message}");
                return null;
            }
        }

        public static void Clear()
        {
            try { lock (gate) if (File.Exists(Path)) File.Delete(Path); }
            catch (Exception ex) { Console.WriteLine($"[headset] could not clear the record: {ex.Message}"); }
        }
    }
}
