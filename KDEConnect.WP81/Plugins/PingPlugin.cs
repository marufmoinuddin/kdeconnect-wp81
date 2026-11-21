using System;
using KDEConnect.WP81.Core;
using KDEConnect.WP81.Services;

namespace KDEConnect.WP81.Plugins
{
    /// <summary>
    /// Ping plugin: responds to kdeconnect.ping with kdeconnect.ping.
    /// This is the first end-to-end test of the link.
    /// </summary>
    public sealed class PingPlugin : IPlugin
    {
        public string Name { get { return "ping"; } }

        public IEnumerable<string> HandledPacketTypes
        {
            get { return new[] { ProtocolConstants.PacketTypePing }; }
        }

        public IEnumerable<string> OutgoingPacketTypes
        {
            get { return new[] { ProtocolConstants.PacketTypePing }; }
        }

        public bool OnPacketReceived(string deviceId, NetworkPacket packet, Action<NetworkPacket> sendReply)
        {
            if (packet == null || packet.Type != ProtocolConstants.PacketTypePing) return false;

            // Echo back a ping response with the same ID
            NetworkPacket reply = new NetworkPacket(ProtocolConstants.PacketTypePing);
            reply.Id = packet.Id;
            reply.SetString("message", packet.GetString("message", string.Empty));
            sendReply(reply);
            return true;
        }

        public void OnLinkEstablished(string deviceId, ITlsSession session, BufferedLineReader reader)
        {
            // No initialization needed
        }

        public void OnLinkClosed(string deviceId)
        {
        }
    }
}