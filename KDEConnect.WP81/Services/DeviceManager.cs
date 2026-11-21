using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;
using KDEConnect.WP81.Core;
using KDEConnect.WP81.Transport;

namespace KDEConnect.WP81.Services
{
    /// <summary>
    /// Central coordinator for device discovery, the device list, pairing state,
    /// and link lifecycle. This is the single source of truth for the UI layer.
    /// </summary>
    public sealed class DeviceManager
    {
        public event EventHandler DeviceListChanged;
        public event EventHandler OwnIdentityChanged;

        private readonly DeviceStore _store;
        private readonly WinRtDatagramDiscovery _discovery;
        private readonly LinkManager _linkManager;
        private readonly PluginManager _pluginManager;
        private readonly object _gate = new object();
        private readonly Dictionary<string, DeviceRecord> _devices = new Dictionary<string, DeviceRecord>(StringComparer.Ordinal);
        private readonly HashSet<string> _connecting = new HashSet<string>(StringComparer.Ordinal);
        private DeviceStore.OwnIdentity _ownIdentity;
        private CancellationTokenSource _discoveryCts;

        public DeviceManager(DeviceStore store, LinkManager linkManager, PluginManager pluginManager)
        {
            _store = store ?? throw new ArgumentNullException("store");
            _linkManager = linkManager ?? throw new ArgumentNullException("linkManager");
            _pluginManager = pluginManager ?? throw new ArgumentNullException("pluginManager");

            _discovery = new WinRtDatagramDiscovery(OnDiscoveryDatagram);
            _linkManager.LinkEstablished += OnLinkEstablished;
            _linkManager.LinkFailed += OnLinkFailed;
            _linkManager.LinkClosed += OnLinkClosed;
        }

        public DeviceStore.OwnIdentity OwnIdentity { get { lock (_gate) return _ownIdentity; } }

        public List<DeviceRecord> GetAllDevices()
        {
            lock (_gate)
            {
                return _devices.Values.OrderBy(r => r.DeviceName ?? r.DeviceId).ToList();
            }
        }

        public DeviceRecord GetDevice(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return null;
            lock (_gate)
            {
                DeviceRecord r;
                return _devices.TryGetValue(deviceId, out r) ? r : null;
            }
        }

        public bool IsConnected(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return false;
            return _linkManager.IsConnected(deviceId);
        }

        public bool IsConnecting(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return false;
            lock (_gate) return _connecting.Contains(deviceId);
        }

        /// <summary>
        /// Loads or creates the persistent own identity. Must be called once
        /// at startup before any network activity.
        /// </summary>
        public async Task InitializeOwnIdentityAsync(string deviceName)
        {
            string deviceId = Sanitize.GenerateDeviceId();
            _ownIdentity = _store.LoadOrCreateIdentity(deviceId, deviceName);
            if (_ownIdentity == null)
            {
                TlsIdentity identity = CertificateProvider.CreateIdentity(deviceId);
                _ownIdentity = new DeviceStore.OwnIdentity
                {
                    DeviceId = deviceId,
                    DeviceName = deviceName ?? "WP81",
                    CertificateDer = identity.CertificateDer,
                    PrivateKeyDer = identity.PrivateKeyDer
                };
                _store.SaveIdentity(_ownIdentity);
            }
            else if (!string.IsNullOrEmpty(deviceName) && !string.Equals(_ownIdentity.DeviceName, deviceName, StringComparison.Ordinal))
            {
                _ownIdentity.DeviceName = deviceName;
                _store.SaveIdentity(_ownIdentity);
            }

            var handler = OwnIdentityChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        /// <summary>Starts UDP discovery listener and sends initial announcement.</summary>
        public async Task StartDiscoveryAsync()
        {
            lock (_gate)
            {
                if (_discoveryCts != null) return;
                _discoveryCts = new CancellationTokenSource();
            }

            await _discovery.StartReceivingAsync(_discoveryCts.Token);
            await SendDiscoveryAnnouncementAsync(_discoveryCts.Token);
        }

        /// <summary>Stops discovery listener.</summary>
        public void StopDiscovery()
        {
            lock (_gate)
            {
                if (_discoveryCts != null)
                {
                    _discoveryCts.Cancel();
                    _discoveryCts = null;
                }
            }
        }

        /// <summary>Connects to a specific device by IP (manual entry fallback).</summary>
        public async Task<bool> ConnectAsync(string deviceId, string address, int port)
        {
            if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(address))
                return false;

            lock (_gate) _connecting.Add(deviceId);
            NotifyDeviceListChanged();

            try
            {
                await _linkManager.ConnectAsync(deviceId, address, port);
                return true;
            }
            finally
            {
                lock (_gate) _connecting.Remove(deviceId);
                NotifyDeviceListChanged();
            }
        }

        /// <summary>Initiates pairing with a device.</summary>
        public async Task<bool> PairAsync(string deviceId)
        {
            DeviceRecord record = GetDevice(deviceId);
            if (record == null || record.IsPaired) return false;

            // Pairing happens inside the link; we just ensure a link exists.
            if (!IsConnected(deviceId))
            {
                if (string.IsNullOrEmpty(record.LastSeenAddress) || record.LastSeenTcpPort == 0)
                    return false;

                await ConnectAsync(deviceId, record.LastSeenAddress, record.LastSeenTcpPort);
            }

            // The actual pairing message is sent by the plugin manager
            // once the link is established.
            return _linkManager.IsConnected(deviceId);
        }

        /// <summary>Unpairs a device and removes the pin.</summary>
        public bool Unpair(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return false;

            _linkManager.Disconnect(deviceId);
            _store.RemovePeer(deviceId);

            lock (_gate) _devices.Remove(deviceId);
            NotifyDeviceListChanged();
            return true;
        }

        private void OnDiscoveryDatagram(string json, string fromAddress)
        {
            try
            {
                NetworkPacket np = NetworkPacket.Unserialize(json);
                if (!DeviceInfo.IsValidUdpDiscoveryPacket(np)) return;

                DeviceInfo info = DeviceInfo.FromUdpDiscoveryPacket(np);
                if (info == null || string.IsNullOrEmpty(info.Id)) return;
                if (string.Equals(info.Id, _ownIdentity?.DeviceId, StringComparison.Ordinal)) return; // ourselves

                lock (_gate)
                {
                    DeviceRecord record;
                    if (!_devices.TryGetValue(info.Id, out record))
                    {
                        record = new DeviceRecord
                        {
                            DeviceId = info.Id,
                            DeviceName = info.Name,
                            DeviceType = info.Type,
                            ProtocolVersion = info.ProtocolVersion,
                            IncomingCapabilities = new List<string>(info.IncomingCapabilities),
                            OutgoingCapabilities = new List<string>(info.OutgoingCapabilities)
                        };
                        _devices[info.Id] = record;
                    }
                    else
                    {
                        record.DeviceName = info.Name;
                        record.DeviceType = info.Type;
                        record.ProtocolVersion = info.ProtocolVersion;
                        record.IncomingCapabilities = new List<string>(info.IncomingCapabilities);
                        record.OutgoingCapabilities = new List<string>(info.OutgoingCapabilities);
                    }
                    record.LastSeenAddress = fromAddress;
                    record.LastSeenTcpPort = info.TcpPort;
                    record.LastSeenUtc = DateTime.UtcNow;
                }

                NotifyDeviceListChanged();
            }
            catch (Exception)
            {
                // Malformed datagram — ignore.
            }
        }

        private async Task SendDiscoveryAnnouncementAsync(CancellationToken ct)
        {
            if (_ownIdentity == null) return;

            DeviceInfo info = new DeviceInfo(_ownIdentity.DeviceId, _ownIdentity.DeviceName, DeviceKind.Phone, ProtocolConstants.ProtocolVersion);
            info.IncomingCapabilities.AddRange(Capabilities.IncomingList());
            info.OutgoingCapabilities.AddRange(Capabilities.OutgoingList());

            byte[] payload = info.ToUdpDiscoveryPacket(ProtocolConstants.UdpPort).Serialize();
            var targets = WinRtDatagramDiscovery.BuildTargets(null);
            await WinRtDatagramDiscovery.SendDiscoveryAsync(payload, ProtocolConstants.UdpPort, targets, ct);
        }

        private void OnLinkEstablished(object sender, LinkEventArgs e)
        {
            lock (_gate)
            {
                DeviceRecord record;
                if (!_devices.TryGetValue(e.DeviceId, out record))
                {
                    record = new DeviceRecord { DeviceId = e.DeviceId };
                    _devices[e.DeviceId] = record;
                }
                record.IsConnected = true;
                record.LastSeenAddress = e.RemoteAddress;
                record.LastSeenTcpPort = e.RemotePort;
                record.IncomingCapabilities = new List<string>(e.PeerInfo.IncomingCapabilities);
                record.OutgoingCapabilities = new List<string>(e.PeerInfo.OutgoingCapabilities);
            }
            NotifyDeviceListChanged();
            _pluginManager.OnLinkEstablished(e.DeviceId, e.Session, e.Reader);
        }

        private void OnLinkFailed(object sender, LinkEventArgs e)
        {
            lock (_gate)
            {
                DeviceRecord record;
                if (_devices.TryGetValue(e.DeviceId, out record))
                {
                    record.IsConnected = false;
                }
                _connecting.Remove(e.DeviceId);
            }
            NotifyDeviceListChanged();
        }

        private void OnLinkClosed(object sender, LinkEventArgs e)
        {
            lock (_gate)
            {
                DeviceRecord record;
                if (_devices.TryGetValue(e.DeviceId, out record))
                {
                    record.IsConnected = false;
                }
            }
            NotifyDeviceListChanged();
            _pluginManager.OnLinkClosed(e.DeviceId);
        }

        public void NotifyDeviceListChanged()
        {
            var handler = DeviceListChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }

    public sealed class LinkEventArgs : EventArgs
    {
        public string DeviceId { get; set; }
        public string RemoteAddress { get; set; }
        public int RemotePort { get; set; }
        public DeviceInfo PeerInfo { get; set; }
        public ITlsSession Session { get; set; }
        public BufferedLineReader Reader { get; set; }
    }
}