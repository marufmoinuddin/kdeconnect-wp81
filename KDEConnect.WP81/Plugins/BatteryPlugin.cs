using System;
using System.Collections.Generic;
using KDEConnect.WP81.Core;
using KDEConnect.WP81.Services;
using KDEConnect.WP81.Transport;

namespace KDEConnect.WP81.Plugins
{
    /// <summary>Where our own battery level comes from.</summary>
    public interface IBatterySource
    {
        /// <summary>False when the platform cannot report a level.</summary>
        bool TryRead(out int percent);
    }

    /// <summary>
    /// Battery: report our level to the peer and show theirs.
    ///
    /// WP8.1 exposes the charge percentage but no charging state, so
    /// isCharging is omitted rather than sent as false - the desktop then
    /// shows a level without claiming to know whether it is charging
    /// (docs/known-limitations.md).
    /// </summary>
    public sealed class BatteryPlugin : IPlugin
    {
        private IPluginHost _host;
        private readonly IBatterySource _source;

        public BatteryPlugin(IBatterySource source)
        {
            _source = source;
        }

        /// <summary>Raised when a peer reports its battery.</summary>
        public event Action<string, BatteryState> BatteryChanged;

        public string Name { get { return "battery"; } }

        public void Initialize(IPluginHost host)
        {
            _host = host;
        }

        public IEnumerable<string> HandledPacketTypes
        {
            get { return new[] { PluginPackets.Battery, PluginPackets.BatteryRequest }; }
        }

        public IEnumerable<string> OutgoingPacketTypes
        {
            get { return new[] { PluginPackets.Battery, PluginPackets.BatteryRequest }; }
        }

        public bool OnPacketReceived(string deviceId, NetworkPacket packet, Action<NetworkPacket> sendReply)
        {
            if (packet == null)
            {
                return false;
            }

            if (packet.Type == PluginPackets.BatteryRequest)
            {
                // The desktop asked for our level.
                if (sendReply != null)
                {
                    sendReply(BuildOurBattery());
                }
                return true;
            }

            if (packet.Type == PluginPackets.Battery)
            {
                BatteryState state = PluginPackets.ParseBattery(packet);
                if (state == null)
                {
                    return false;
                }
                Action<string, BatteryState> handler = BatteryChanged;
                if (handler != null)
                {
                    handler(deviceId, state);
                }
                return true;
            }

            return false;
        }

        public void OnLinkEstablished(string deviceId, ITlsSession session, BufferedLineReader reader)
        {
            // Announce our level as soon as a link exists, and ask for theirs.
            SendOurBattery(deviceId);
            RequestPeerBattery(deviceId);
        }

        public void OnLinkClosed(string deviceId)
        {
        }

        public bool SendOurBattery(string deviceId)
        {
            if (_host == null)
            {
                return false;
            }
            return _host.SendPacket(deviceId, BuildOurBattery());
        }

        public bool RequestPeerBattery(string deviceId)
        {
            if (_host == null)
            {
                return false;
            }
            return _host.SendPacket(deviceId, PluginPackets.BuildBatteryRequest());
        }

        private NetworkPacket BuildOurBattery()
        {
            int percent = -1;
            if (_source != null && _source.TryRead(out percent) && percent >= 0)
            {
                // charging is null: not knowable on WP8.1
                return PluginPackets.BuildBattery(percent, (bool?)null);
            }
            return PluginPackets.BuildBattery(0, (bool?)null);
        }
    }
}
