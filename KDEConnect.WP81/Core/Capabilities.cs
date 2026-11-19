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
            "kdeconnect.findmyphone.request"
            // Deliberately NOT advertised as incoming:
            //  - kdeconnect.clipboard / .clipboard.connect : Windows Phone 8.1 WinRT
            //    has no Clipboard class at all (docs/known-limitations.md L1), so a
            //    pushed clipboard would arrive with nowhere to go.
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
            "kdeconnect.clipboard"              // text the operator types into our own UI
            // Outgoing kdeconnect.clipboard is supported WITHOUT a clipboard API:
            // the operator types the text into our own UI and we send it, which is
            // what the desktop's clipboard plugin writes locally. We never claim to
            // read the phone clipboard (we cannot), so .clipboard.connect is not
            // advertised either.
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
