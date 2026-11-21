using System;
using System.Collections.Generic;
using KDEConnect.WP81.Core;
using KDEConnect.WP81.Services;
using KDEConnect.WP81.Transport;

namespace KDEConnect.WP81.Plugins
{
    /// <summary>
    /// Media control: a remote control for the desktop's player.
    ///
    /// The phone never uses its own media stack for this - it only sends
    /// requests and renders the state the desktop reports
    /// (docs/compatibility-matrix.md, mpris rows).
    /// </summary>
    public sealed class MprisPlugin : IPlugin
    {
        private IPluginHost _host;

        /// <summary>Raised when the peer publishes player state.</summary>
        public event Action<string, MprisState> StateChanged;

        public string Name { get { return "mpris"; } }

        public void Initialize(IPluginHost host)
        {
            _host = host;
        }

        public IEnumerable<string> HandledPacketTypes
        {
            get { return new[] { PluginPackets.MprisState }; }
        }

        public IEnumerable<string> OutgoingPacketTypes
        {
            get { return new[] { PluginPackets.MprisRequest }; }
        }

        public bool OnPacketReceived(string deviceId, NetworkPacket packet, Action<NetworkPacket> sendReply)
        {
            MprisState state = MprisState.FromPacket(packet);
            if (state == null)
            {
                return false;
            }
            Action<string, MprisState> handler = StateChanged;
            if (handler != null)
            {
                handler(deviceId, state);
            }
            return true;
        }

        public void OnLinkEstablished(string deviceId, ITlsSession session, BufferedLineReader reader)
        {
            // Ask for the current track so the page is not empty on entry.
            RequestNowPlaying(deviceId, null);
        }

        public void OnLinkClosed(string deviceId)
        {
        }

        public bool SendAction(string deviceId, string player, string action)
        {
            if (_host == null || string.IsNullOrEmpty(action))
            {
                return false;
            }
            return _host.SendPacket(deviceId, PluginPackets.BuildMprisAction(player, action));
        }

        public bool RequestNowPlaying(string deviceId, string player)
        {
            if (_host == null)
            {
                return false;
            }
            return _host.SendPacket(deviceId, PluginPackets.BuildMprisRequestNowPlaying(player));
        }

        public bool RequestVolume(string deviceId, string player)
        {
            if (_host == null)
            {
                return false;
            }
            return _host.SendPacket(deviceId, PluginPackets.BuildMprisRequestVolume(player));
        }

        public bool SetVolume(string deviceId, string player, int volume)
        {
            if (_host == null)
            {
                return false;
            }
            return _host.SendPacket(deviceId, PluginPackets.BuildMprisSetVolume(player, volume));
        }
    }
}
