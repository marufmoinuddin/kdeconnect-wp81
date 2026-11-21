using System.Collections.Generic;

namespace KDEConnect.WP81.Core
{
    /// <summary>
    /// One audio sink reported by a desktop in <c>kdeconnect.systemvolume</c>
    /// (remotesystemvolumeplugin.cpp:24-41).
    /// </summary>
    public sealed class VolumeSink
    {
        public VolumeSink()
        {
            Volume = -1;
        }

        public string Name { get; set; }
        public int Volume { get; set; }
        public bool Muted { get; set; }
        public bool Enabled { get; set; }

        /// <summary>Name with the PulseAudio prefix trimmed, for display.</summary>
        public string DisplayName
        {
            get
            {
                if (string.IsNullOrEmpty(Name))
                {
                    return "(unnamed sink)";
                }
                // Desktops usually send "PulseAudio/Sink/alsa_output.pci-0000_00_1f.3"
                int slash = Name.LastIndexOf('/');
                string tail = slash >= 0 && slash < Name.Length - 1
                    ? Name.Substring(slash + 1)
                    : Name;
                return tail.Length == 0 ? Name : tail;
            }
        }

        /// <summary>
        /// Parses a sink list packet. Handles both shapes the reference emits:
        /// a full <c>sinkList</c> array, and a single-sink update carrying only
        /// <c>name</c> plus <c>volume</c>/<c>muted</c>.
        /// Returns an empty list for packets that carry neither.
        /// </summary>
        public static List<VolumeSink> ListFromPacket(NetworkPacket packet)
        {
            List<VolumeSink> sinks = new List<VolumeSink>();
            if (packet == null || packet.Type != PluginPackets.SystemVolumeState)
            {
                return sinks;
            }

            JArray array = packet.Body.GetArray("sinkList");
            if (array != null)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    JObject item = array[i] as JObject;
                    if (item == null)
                    {
                        continue;
                    }
                    VolumeSink sink = new VolumeSink();
                    sink.Name = item.GetString("name", string.Empty);
                    sink.Volume = item.GetInt("volume", -1);
                    sink.Muted = item.GetBool("muted", false);
                    sink.Enabled = item.GetBool("enabled", true);
                    if (!string.IsNullOrEmpty(sink.Name))
                    {
                        sinks.Add(sink);
                    }
                }
                return sinks;
            }

            // Single-sink update.
            string name = packet.GetString("name", null);
            if (!string.IsNullOrEmpty(name))
            {
                VolumeSink sink = new VolumeSink();
                sink.Name = name;
                sink.Volume = packet.GetInt("volume", -1);
                sink.Muted = packet.GetBool("muted", false);
                sink.Enabled = packet.GetBool("enabled", true);
                sinks.Add(sink);
            }
            return sinks;
        }
    }
}
