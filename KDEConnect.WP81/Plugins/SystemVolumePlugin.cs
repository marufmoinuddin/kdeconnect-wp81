using System;
using System.Collections.Generic;
using KDEConnect.WP81.Core;
using KDEConnect.WP81.Services;
using KDEConnect.WP81.Transport;

namespace KDEConnect.WP81.Plugins
{
    /// <summary>
    /// Remote system volume: lists the desktop's sinks and sets their level or
    /// mute state (remotesystemvolumeplugin.cpp:24-57).
    /// </summary>
    public sealed class SystemVolumePlugin : IPlugin
    {
        private IPluginHost _host;

        /// <summary>Raised when the peer reports its sinks.</summary>
        public event Action<string, List<VolumeSink>> SinksChanged;

        public string Name { get { return "systemvolume"; } }

        public void Initialize(IPluginHost host)
        {
            _host = host;
        }

        public IEnumerable<string> HandledPacketTypes
        {
            get { return new[] { PluginPackets.SystemVolumeState }; }
        }

        public IEnumerable<string> OutgoingPacketTypes
        {
            get { return new[] { PluginPackets.SystemVolumeRequest }; }
        }

        public bool OnPacketReceived(string deviceId, NetworkPacket packet, Action<NetworkPacket> sendReply)
        {
            if (packet == null || packet.Type != PluginPackets.SystemVolumeState)
            {
                return false;
            }
            List<VolumeSink> sinks = VolumeSink.ListFromPacket(packet);
            if (sinks.Count == 0)
            {
                return false;
            }
            Action<string, List<VolumeSink>> handler = SinksChanged;
            if (handler != null)
            {
                handler(deviceId, sinks);
            }
            return true;
        }

        public void OnLinkEstablished(string deviceId, ITlsSession session, BufferedLineReader reader)
        {
            RequestSinks(deviceId);
        }

        public void OnLinkClosed(string deviceId)
        {
        }

        public bool RequestSinks(string deviceId)
        {
            if (_host == null)
            {
                return false;
            }
            return _host.SendPacket(deviceId, PluginPackets.BuildSystemVolumeRequest());
        }

        public bool SetVolume(string deviceId, string sinkName, int volume)
        {
            if (_host == null)
            {
                return false;
            }
            return _host.SendPacket(deviceId, PluginPackets.BuildSystemVolumeSet(sinkName, volume));
        }

        public bool SetMuted(string deviceId, string sinkName, bool muted)
        {
            if (_host == null)
            {
                return false;
            }
            return _host.SendPacket(deviceId, PluginPackets.BuildSystemVolumeMute(sinkName, muted));
        }
    }
}
