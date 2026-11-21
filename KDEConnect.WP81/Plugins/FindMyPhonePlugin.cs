using System;
using System.Collections.Generic;
using KDEConnect.WP81.Core;
using KDEConnect.WP81.Services;
using KDEConnect.WP81.Transport;

namespace KDEConnect.WP81.Plugins
{
    /// <summary>
    /// Find my phone: the desktop asks this device to ring. Inbound only - a
    /// phone has no business asking a desktop to make noise.
    /// </summary>
    public sealed class FindMyPhonePlugin : IPlugin
    {
        /// <summary>Raised when the peer asks us to ring.</summary>
        public event Action<string> RingRequested;

        public string Name { get { return "findmyphone"; } }

        public void Initialize(IPluginHost host)
        {
            // Inbound only; nothing to send.
        }

        public IEnumerable<string> HandledPacketTypes
        {
            get { return new[] { PluginPackets.FindMyPhoneRequest }; }
        }

        public IEnumerable<string> OutgoingPacketTypes
        {
            get { return new string[0]; }
        }

        public bool OnPacketReceived(string deviceId, NetworkPacket packet, Action<NetworkPacket> sendReply)
        {
            if (packet == null || packet.Type != PluginPackets.FindMyPhoneRequest)
            {
                return false;
            }
            Action<string> handler = RingRequested;
            if (handler != null)
            {
                handler(deviceId);
            }
            return true;
        }

        public void OnLinkEstablished(string deviceId, ITlsSession session, BufferedLineReader reader)
        {
        }

        public void OnLinkClosed(string deviceId)
        {
        }
    }
}
