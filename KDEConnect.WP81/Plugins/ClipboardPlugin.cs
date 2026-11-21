using System;
using System.Collections.Generic;
using KDEConnect.WP81.Core;
using KDEConnect.WP81.Services;
using KDEConnect.WP81.Transport;

namespace KDEConnect.WP81.Plugins
{
    /// <summary>
    /// Clipboard: OUTGOING TEXT ONLY.
    ///
    /// Windows Phone 8.1 WinRT has no clipboard class at all
    /// (docs/known-limitations.md L1), so this plugin cannot read the system
    /// clipboard and must not advertise that it can. The operator types text
    /// into our own UI and we send it to the desktop.
    /// </summary>
    public sealed class ClipboardPlugin : IPlugin
    {
        private IPluginHost _host;

        public string Name { get { return "clipboard"; } }

        public void Initialize(IPluginHost host)
        {
            _host = host;
        }

        /// <summary>Nothing inbound: we never claim to accept clipboard data.</summary>
        public IEnumerable<string> HandledPacketTypes
        {
            get { return new string[0]; }
        }

        public IEnumerable<string> OutgoingPacketTypes
        {
            get { return new[] { PluginPackets.Clipboard }; }
        }

        public bool OnPacketReceived(string deviceId, NetworkPacket packet, Action<NetworkPacket> sendReply)
        {
            return false;
        }

        public void OnLinkEstablished(string deviceId, ITlsSession session, BufferedLineReader reader)
        {
        }

        public void OnLinkClosed(string deviceId)
        {
        }

        /// <summary>Sends text to the peer's clipboard.</summary>
        public bool SendText(string deviceId, string text)
        {
            if (_host == null || string.IsNullOrEmpty(text))
            {
                return false;
            }
            long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            return _host.SendPacket(deviceId, PluginPackets.BuildClipboard(text, now));
        }
    }
}
