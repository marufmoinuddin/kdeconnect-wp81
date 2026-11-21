using System;
using System.Collections.Generic;
using KDEConnect.WP81.Core;
using KDEConnect.WP81.Services;
using KDEConnect.WP81.Transport;

namespace KDEConnect.WP81.Plugins
{
    /// <summary>
    /// Touchpad and keyboard: turns gestures on the phone into relative pointer
    /// moves and key presses on the desktop (x11remoteinput.cpp).
    /// </summary>
    public sealed class MousepadPlugin : IPlugin
    {
        private IPluginHost _host;

        /// <summary>Raised when the desktop reports whether its keyboard is enabled.</summary>
        public event Action<string, bool> KeyboardStateChanged;

        public string Name { get { return "mousepad"; } }

        public void Initialize(IPluginHost host)
        {
            _host = host;
        }

        public IEnumerable<string> HandledPacketTypes
        {
            get { return new[] { PluginPackets.MousepadKeyboardState }; }
        }

        public IEnumerable<string> OutgoingPacketTypes
        {
            get { return new[] { PluginPackets.MousepadRequest }; }
        }

        public bool OnPacketReceived(string deviceId, NetworkPacket packet, Action<NetworkPacket> sendReply)
        {
            if (packet == null || packet.Type != PluginPackets.MousepadKeyboardState)
            {
                return false;
            }
            bool state = packet.GetBool("state", false);
            Action<string, bool> handler = KeyboardStateChanged;
            if (handler != null)
            {
                handler(deviceId, state);
            }
            return true;
        }

        public void OnLinkEstablished(string deviceId, ITlsSession session, BufferedLineReader reader)
        {
        }

        public void OnLinkClosed(string deviceId)
        {
        }

        public bool MoveBy(string deviceId, double dx, double dy)
        {
            if (_host == null)
            {
                return false;
            }
            return _host.SendPacket(deviceId, PluginPackets.BuildMouseMove(dx, dy));
        }

        public bool Scroll(string deviceId, double dx, double dy)
        {
            if (_host == null)
            {
                return false;
            }
            return _host.SendPacket(deviceId, PluginPackets.BuildMouseScroll(dx, dy));
        }

        public bool Click(string deviceId, string button)
        {
            if (_host == null || string.IsNullOrEmpty(button))
            {
                return false;
            }
            return _host.SendPacket(deviceId, PluginPackets.BuildMouseClick(button));
        }

        public bool SendKey(string deviceId, string key, string specialKey,
            bool alt, bool ctrl, bool shift, bool super)
        {
            if (_host == null)
            {
                return false;
            }
            if (string.IsNullOrEmpty(key) && string.IsNullOrEmpty(specialKey))
            {
                return false;
            }
            return _host.SendPacket(deviceId,
                PluginPackets.BuildKey(key, specialKey, alt, ctrl, shift, super));
        }
    }
}
