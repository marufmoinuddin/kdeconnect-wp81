using System;
using System.Collections.Generic;
using System.Text;

namespace KDEConnect.WP81.Core
{
    /// <summary>
    /// The packet types this client is willing to send and receive.
    ///
    /// A capability is a packet type string (see docs/protocol.md §2.1). Only
    /// types listed here are ever placed in our identity packet, so the peer
    /// will never be told about a path that is a stub.
    /// </summary>
    public static class Capabilities
    {
        /// <summary>Types we accept from the peer (our incomingCapabilities).</summary>
        public static readonly string[] Incoming = new string[]
        {
            ProtocolConstants.PacketTypePing,
            "kdeconnect.battery",
            "kdeconnect.mpris",                 // media state reported by the desktop
            "kdeconnect.mousepad.keyboardstate",
            "kdeconnect.systemvolume",          // sink list / volume state
            "kdeconnect.share.request",         // incoming files
            "kdeconnect.share.request.update",
            "kdeconnect.clipboard",
            "kdeconnect.clipboard.connect",
            "kdeconnect.findmyphone.request"
        };

        /// <summary>Types we send to the peer (our outgoingCapabilities).</summary>
        public static readonly string[] Outgoing = new string[]
        {
            ProtocolConstants.PacketTypePing,
            "kdeconnect.battery",
            "kdeconnect.mpris.request",         // control the desktop's players
            "kdeconnect.mousepad.request",      // touchpad / keyboard / presenter input
            "kdeconnect.presenter",
            "kdeconnect.systemvolume.request",
            "kdeconnect.share.request",         // send files
            "kdeconnect.clipboard",
            "kdeconnect.clipboard.connect"
        };

        /// <summary>
        /// True when the peer declared it can receive <paramref name="type"/>.
        /// Mirrors Device::sendPacket (device.cpp:390-393): protocol packets are
        /// always allowed, everything else must be in the peer's incoming list.
        /// </summary>
        public static bool CanSendToPeer(string type, ICollection<string> peerIncomingCapabilities)
        {
            if (type == ProtocolConstants.PacketTypeIdentity ||
                type == ProtocolConstants.PacketTypePair)
            {
                return true;
            }
            if (peerIncomingCapabilities == null)
            {
                return false;
            }
            return peerIncomingCapabilities.Contains(type);
        }

        /// <summary>
        /// True when we declared we accept <paramref name="type"/>. Mirrors
        /// Device::privateReceivedPacket (device.cpp:404-425).
        /// </summary>
        public static bool CanReceive(string type)
        {
            if (type == ProtocolConstants.PacketTypeIdentity ||
                type == ProtocolConstants.PacketTypePair)
            {
                return true;
            }
            for (int i = 0; i < Incoming.Length; i++)
            {
                if (string.Equals(Incoming[i], type, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        public static List<string> IncomingList()
        {
            return new List<string>(Incoming);
        }

        public static List<string> OutgoingList()
        {
            return new List<string>(Outgoing);
        }

        /// <summary>Human-readable summary for the diagnostics export.</summary>
        public static string Describe()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("in=[");
            sb.Append(string.Join(",", Incoming));
            sb.Append("] out=[");
            sb.Append(string.Join(",", Outgoing));
            sb.Append("]");
            return sb.ToString();
        }
    }
}
