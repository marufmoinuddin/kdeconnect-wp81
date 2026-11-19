namespace KDEConnect.WP81.Core
{
    /// <summary>
    /// Protocol constants, all taken from the pinned upstream checkouts.
    /// See docs/protocol.md for the citation of every value.
    /// </summary>
    public static class ProtocolConstants
    {
        /// <summary>NetworkPacket::s_protocolVersion (kdeconnect-kde core/networkpacket.cpp:33).</summary>
        public const int ProtocolVersion = 8;

        public const string PacketTypeIdentity = "kdeconnect.identity";
        public const string PacketTypePair = "kdeconnect.pair";
        public const string PacketTypePing = "kdeconnect.ping";

        // Ports (kdeconnect-kde core/backends/lan/lanlinkprovider.h:75-77,
        // compositeuploadjob.h:72-73).
        public const int UdpPort = 1716;
        public const int MinTcpPort = 1716;
        public const int MaxTcpPort = 1764;
        public const int MinPayloadPort = 1739;
        public const int MaxPayloadPort = 1764;

        // Frame and queue bounds.
        /// <summary>lanlinkprovider.cpp:45 MAX_IDENTITY_PACKET_SIZE.</summary>
        public const int MaxIdentityPacketSize = 8192;

        /// <summary>
        /// Ceiling for any application frame. The desktop allows 32 MiB
        /// (landevicelink.cpp:22); this client deliberately uses a much tighter
        /// bound so a hostile peer cannot make it allocate freely.
        /// </summary>
        public const int MaxPacketSize = 1024 * 1024;

        public const int MaxUdpDatagramSize = 512 * 1024;
        public const int MaxUnpairedConnections = 42;

        // Timing (milliseconds).
        public const int PlaintextHandshakeTimeoutMs = 1000;   // lanlinkprovider.cpp:491-499
        public const int SecureIdentityTimeoutMs = 1000;       // lanlinkprovider.cpp:396-403
        public const int PairingTimeoutMs = 30 * 1000;         // pairinghandler.h:20
        public const int PayloadWaitTimeoutMs = 30 * 1000;     // compositeuploadjob.cpp:36
        public const int PairingTimestampSkewSeconds = 1800;   // pairinghandler.cpp:16
        public const int PayloadChunkSize = 4096;              // uploadjob.cpp:68

        /// <summary>
        /// Delay this client must observe between sending its own UDP discovery
        /// datagram and opening the matching TCP connection. The desktop
        /// rate-limits by IP on both events within 500 ms
        /// (lanlinkprovider.cpp:49, :278, :478); 700 ms leaves margin.
        /// </summary>
        public const int UdpToTcpDelayMs = 700;

        /// <summary>Desktop rate-limit window (lanlinkprovider.cpp:49).</summary>
        public const int PeerRateLimitWindowMs = 500;

        // Device id: deviceinfo.h:191 ^[a-zA-Z0-9_-]{32,38}$
        public const int MinDeviceIdLength = 32;
        public const int MaxDeviceIdLength = 38;

        public const int MaxDeviceNameLength = 32;             // deviceinfo.h:168
        public const string DeviceTypePhone = "phone";

        /// <summary>
        /// Characters stripped from device names by DeviceInfo::filterName
        /// (deviceinfo.h:167): ["',;:.!?()[]&lt;&gt;]
        /// </summary>
        public const string DeviceNameInvalidCharacters = "\"',;:.!?()[]<>";

        /// <summary>
        /// Upper bound on a single incoming payload. There is no protocol-level
        /// limit, so the operator sets this; 2 GiB by default.
        /// </summary>
        public const long MaxIncomingPayloadBytes = 2L * 1024 * 1024 * 1024;
    }
}
