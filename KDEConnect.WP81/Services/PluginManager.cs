using System;
using System.Collections.Generic;
using KDEConnect.WP81.Core;

namespace KDEConnect.WP81.Services
{
    /// <summary>
    /// Plugin interface. Each plugin declares the packet types it handles
    /// (incoming) and the packet types it can send (outgoing). Capabilities
    /// advertised to peers must match what is actually implemented.
    /// </summary>
    public interface IPlugin
    {
        /// <summary>Unique plugin name (matches KDE Connect plugin IDs).</summary>
        string Name { get; }

        /// <summary>Packet types this plugin handles when received from a peer.</summary>
        IEnumerable<string> HandledPacketTypes { get; }

        /// <summary>Packet types this plugin can send to a peer (outgoing capabilities).</summary>
        IEnumerable<string> OutgoingPacketTypes { get; }

        /// <summary>Called when a packet of a handled type is received.</summary>
        /// <returns>True if handled, false to let other plugins try.</returns>
        bool OnPacketReceived(string deviceId, NetworkPacket packet, Action<NetworkPacket> sendReply);

        /// <summary>Called when a link is established with a peer.</summary>
        void OnLinkEstablished(string deviceId, ITlsSession session, BufferedLineReader reader);

        /// <summary>Called when a link is closed.</summary>
        void OnLinkClosed(string deviceId);
    }

    /// <summary>
    /// Manages plugin registration and packet dispatch. Ensures only
    /// implemented capabilities are advertised.
    /// </summary>
    public sealed class PluginManager
    {
        private readonly Dictionary<string, IPlugin> _plugins = new Dictionary<string, IPlugin>(StringComparer.Ordinal);
        private readonly LinkManager _linkManager;

        public PluginManager(LinkManager linkManager)
        {
            _linkManager = linkManager ?? throw new ArgumentNullException("linkManager");
            _linkManager.PacketReceived += OnPacketReceived;
        }

        public void RegisterPlugin(IPlugin plugin)
        {
            if (plugin == null) throw new ArgumentNullException("plugin");
            _plugins[plugin.Name] = plugin;
        }

        public IPlugin GetPlugin(string name)
        {
            IPlugin p;
            return _plugins.TryGetValue(name, out p) ? p : null;
        }

        /// <summary>
        /// Returns the union of all incoming capabilities across registered plugins.
        /// This is what DeviceInfo advertises to peers.
        /// </summary>
        public List<string> GetIncomingCapabilities()
        {
            var caps = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in _plugins.Values)
            {
                foreach (var t in p.HandledPacketTypes)
                    caps.Add(t);
            }
            return new List<string>(caps);
        }

        /// <summary>
        /// Returns the union of all outgoing capabilities across registered plugins.
        /// </summary>
        public List<string> GetOutgoingCapabilities()
        {
            var caps = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in _plugins.Values)
            {
                foreach (var t in p.OutgoingPacketTypes)
                    caps.Add(t);
            }
            return new List<string>(caps);
        }

        public void OnLinkEstablished(string deviceId, ITlsSession session, BufferedLineReader reader)
        {
            foreach (var p in _plugins.Values)
            {
                try { p.OnLinkEstablished(deviceId, session, reader); } catch (Exception) { }
            }
        }

        public void OnLinkClosed(string deviceId)
        {
            foreach (var p in _plugins.Values)
            {
                try { p.OnLinkClosed(deviceId); } catch (Exception) { }
            }
        }

        private void OnPacketReceived(object sender, PacketEventArgs e)
        {
            if (e?.Packet == null) return;

            // First try plugins that explicitly handle this packet type
            foreach (var p in _plugins.Values)
            {
                foreach (var handled in p.HandledPacketTypes)
                {
                    if (string.Equals(handled, e.Packet.Type, StringComparison.Ordinal))
                    {
                        bool handledFlag = p.OnPacketReceived(e.DeviceId, e.Packet, reply =>
                        {
                            _linkManager.SendPacket(e.DeviceId, reply);
                        });
                        if (handledFlag) return;
                    }
                }
            }

            // No plugin handled it - could log or send an error response
        }

        /// <summary>Sends a packet on behalf of a plugin.</summary>
        public bool SendPacket(string deviceId, NetworkPacket packet)
        {
            return _linkManager.SendPacket(deviceId, packet);
        }
    }
}