using System;
using System.Collections.Generic;
using KDEConnect.WP81.Core;
using KDEConnect.WP81.Services;
using KDEConnect.WP81.Transport;

namespace KDEConnect.WP81.Plugins
{
    /// <summary>
    /// Presenter: normalised pointer movement during a slideshow
    /// (presenterplugin.cpp:66-107). Values are fractions of the screen, unlike
    /// the touchpad's raw pixel deltas.
    /// </summary>
    public sealed class PresenterPlugin : IPlugin
    {
        private IPluginHost _host;

        public string Name { get { return "presenter"; } }

        public void Initialize(IPluginHost host)
        {
            _host = host;
        }

        public IEnumerable<string> HandledPacketTypes
        {
            get { return new string[0]; }
        }

        public IEnumerable<string> OutgoingPacketTypes
        {
            get { return new[] { PluginPackets.Presenter }; }
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

        public bool Move(string deviceId, float dx, float dy)
        {
            if (_host == null)
            {
                return false;
            }
            return _host.SendPacket(deviceId, PluginPackets.BuildPresenterMove(dx, dy));
        }

        public bool Stop(string deviceId)
        {
            if (_host == null)
            {
                return false;
            }
            return _host.SendPacket(deviceId, PluginPackets.BuildPresenterStop());
        }
    }
}
