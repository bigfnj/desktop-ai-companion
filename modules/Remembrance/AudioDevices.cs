using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;

namespace DesktopAICompanion.RemembranceModule
{
    /// <summary>One selectable audio endpoint for the options dropdowns: a stable id + a friendly name.
    /// An empty id means "the system default", resolved live at record time.</summary>
    internal sealed class AudioDevice
    {
        public string Id;
        public string Name;
    }

    /// <summary>Enumerates WASAPI render (for system/loopback capture) and capture (microphone) endpoints, and
    /// resolves a saved id back to a device. All best-effort: enumeration never throws, and a missing id falls
    /// back to the system default.</summary>
    internal static class AudioDevices
    {
        public static List<AudioDevice> RenderDevices() { return Enumerate(DataFlow.Render, "System default output"); }
        public static List<AudioDevice> CaptureDevices() { return Enumerate(DataFlow.Capture, "System default microphone"); }

        private static List<AudioDevice> Enumerate(DataFlow flow, string defaultLabel)
        {
            var list = new List<AudioDevice> { new AudioDevice { Id = "", Name = defaultLabel } };
            try
            {
                using (var en = new MMDeviceEnumerator())
                {
                    foreach (MMDevice d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
                    {
                        try { list.Add(new AudioDevice { Id = d.ID, Name = d.FriendlyName }); }
                        catch { }
                        finally { try { d.Dispose(); } catch { } }
                    }
                }
            }
            catch { }
            return list;
        }

        /// <summary>Resolve a saved friendly name to an MMDevice, or the system default when it is blank / a
        /// default-label / a device no longer present (the options dropdown stores the display name). The caller
        /// owns the returned device and must dispose it.</summary>
        public static MMDevice Resolve(DataFlow flow, string friendlyName)
        {
            var en = new MMDeviceEnumerator();
            try
            {
                if (!string.IsNullOrWhiteSpace(friendlyName))
                {
                    foreach (MMDevice d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
                    {
                        if (string.Equals(d.FriendlyName, friendlyName, StringComparison.Ordinal)) return d;
                        try { d.Dispose(); } catch { }
                    }
                }
                Role role = flow == DataFlow.Render ? Role.Multimedia : Role.Communications;
                return en.GetDefaultAudioEndpoint(flow, role);
            }
            finally { try { en.Dispose(); } catch { } }
        }
    }
}
