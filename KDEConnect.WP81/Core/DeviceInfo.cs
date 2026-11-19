using System;
using System.Collections.Generic;

namespace KDEConnect.WP81.Core
{
    public enum DeviceKind
    {
        Unknown,
        Desktop,
        Laptop,
        Phone,
        Tablet,
        Tv
    }

    /// <summary>
    /// Local and remote device identity, plus the three identity-flavoured
    /// packets. Port of kdeconnect-kde core/deviceinfo.h.
    /// </summary>
    public sealed class DeviceInfo
    {
        public DeviceInfo(string id, string name, DeviceKind kind, int protocolVersion)
        {
            Id = id;
            Name = name;
            Kind = kind;
            ProtocolVersion = protocolVersion;
            IncomingCapabilities = new List<string>();
            OutgoingCapabilities = new List<string>();
        }

        public string Id { get; private set; }
        public string Name { get; private set; }
        public DeviceKind Kind { get; private set; }
        public int ProtocolVersion { get; private set; }
        public List<string> IncomingCapabilities { get; private set; }
        public List<string> OutgoingCapabilities { get; private set; }

        // ---- device type ----------------------------------------------------

        public static DeviceKind KindFromString(string value)
        {
            if (value == "desktop") return DeviceKind.Desktop;
            if (value == "laptop") return DeviceKind.Laptop;
            if (value == "phone" || value == "smartphone") return DeviceKind.Phone;
            if (value == "tablet") return DeviceKind.Tablet;
            if (value == "tv") return DeviceKind.Tv;
            return DeviceKind.Unknown;
        }

        public static string KindToString(DeviceKind kind)
        {
            switch (kind)
            {
                case DeviceKind.Desktop: return "desktop";
                case DeviceKind.Laptop: return "laptop";
                case DeviceKind.Phone: return "phone";
                case DeviceKind.Tablet: return "tablet";
                case DeviceKind.Tv: return "tv";
                default: return "unknown";
            }
        }

        // ---- packet builders ------------------------------------------------

        /// <summary>UDP broadcast: deviceId, deviceName, protocolVersion, tcpPort.</summary>
        public NetworkPacket ToUdpDiscoveryPacket(int tcpPort)
        {
            NetworkPacket np = new NetworkPacket(ProtocolConstants.PacketTypeIdentity);
            np.SetString("deviceId", Id);
            np.SetString("deviceName", Name);
            np.SetInt("protocolVersion", ProtocolVersion);
            np.SetInt("tcpPort", tcpPort);
            return np;
        }

        /// <summary>First plaintext line on a TCP connection this client dials.</summary>
        public NetworkPacket ToConnectionPacket(string targetDeviceId, int targetProtocolVersion)
        {
            NetworkPacket np = new NetworkPacket(ProtocolConstants.PacketTypeIdentity);
            np.SetString("deviceId", Id);
            np.SetString("deviceName", Name);
            np.SetInt("protocolVersion", ProtocolVersion);
            np.SetString("targetDeviceId", targetDeviceId);
            np.SetInt("targetProtocolVersion", targetProtocolVersion);
            return np;
        }

        /// <summary>First line sent inside TLS.</summary>
        public NetworkPacket ToIdentityPacket()
        {
            NetworkPacket np = new NetworkPacket(ProtocolConstants.PacketTypeIdentity);
            np.SetString("deviceId", Id);
            np.SetString("deviceName", Name);
            np.SetString("deviceType", KindToString(Kind));
            np.SetInt("protocolVersion", ProtocolVersion);
            np.SetStringList("incomingCapabilities", IncomingCapabilities);
            np.SetStringList("outgoingCapabilities", OutgoingCapabilities);
            return np;
        }

        // ---- parsing --------------------------------------------------------

        public static DeviceInfo FromIdentityPacketAndCert(NetworkPacket np, string certificateDeviceId)
        {
            DeviceInfo info = new DeviceInfo(
                np.GetString("deviceId", string.Empty),
                FilterName(np.GetString("deviceName", string.Empty)),
                KindFromString(np.GetString("deviceType", string.Empty)),
                np.GetInt("protocolVersion", -1));
            info.IncomingCapabilities.AddRange(np.GetStringList("incomingCapabilities"));
            info.OutgoingCapabilities.AddRange(np.GetStringList("outgoingCapabilities"));
            return info;
        }

        public static string FilterName(string input)
        {
            return Sanitize.DeviceName(input);
        }

        // ---- validation -----------------------------------------------------

        public static bool IsValidDeviceId(string deviceId)
        {
            return Sanitize.IsValidDeviceId(deviceId);
        }

        public static bool IsValidIdentityPacket(NetworkPacket np)
        {
            if (np == null || np.Type != ProtocolConstants.PacketTypeIdentity)
            {
                return false;
            }
            string name = FilterName(np.GetString("deviceName", string.Empty));
            return name.Length > 0 && IsValidDeviceId(np.GetString("deviceId", string.Empty));
        }

        public static bool IsValidUdpDiscoveryPacket(NetworkPacket np)
        {
            return np != null &&
                   np.Type == ProtocolConstants.PacketTypeIdentity &&
                   IsValidDeviceId(np.GetString("deviceId", string.Empty));
        }

        public static bool IsValidConnectionPacket(NetworkPacket np)
        {
            return np != null &&
                   np.Type == ProtocolConstants.PacketTypeIdentity &&
                   IsValidDeviceId(np.GetString("deviceId", string.Empty)) &&
                   IsValidDeviceId(np.GetString("targetDeviceId", string.Empty));
        }
    }
}
