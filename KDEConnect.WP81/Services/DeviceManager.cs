using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KDEConnect.WP81.Core;
using KDEConnect.WP81.Transport;

namespace KDEConnect.WP81.Services
{
    /// <summary>
    /// Central coordinator: UDP discovery, the device list, pairing state and
    /// link lifecycle. This is the single source of truth for the UI layer.
    ///
    /// Capabilities advertised to peers are derived from registered plugins,
    /// never from the static reference list, so a peer is never told about a
    /// packet type this build cannot actually handle.
    /// </summary>
    public sealed class DeviceManager
    {
        public event EventHandler DeviceListChanged;
        public event EventHandler OwnIdentityChanged;
        public event EventHandler<PairingEventArgs> PairingRequestReceived;
        public event EventHandler<PairingEventArgs> PairingStateChanged;

        private readonly DeviceStore _store;
        private readonly LinkManager _linkManager;
        private readonly PluginManager _pluginManager;
        private readonly WinRtDatagramDiscovery _discovery;
        private readonly object _gate = new object();
        private readonly Dictionary<string, DeviceRecord> _devices =
            new Dictionary<string, DeviceRecord>(StringComparer.Ordinal);
        private readonly Dictionary<string, Pairing> _pairings =
            new Dictionary<string, Pairing>(StringComparer.Ordinal);
        private readonly HashSet<string> _connecting = new HashSet<string>(StringComparer.Ordinal);

        private DeviceStore.OwnIdentity _ownIdentity;
        private CancellationTokenSource _discoveryCts;

        public DeviceManager(DeviceStore store, LinkManager linkManager, PluginManager pluginManager)
        {
            if (store == null)
            {
                throw new ArgumentNullException("store");
            }
            if (linkManager == null)
            {
                throw new ArgumentNullException("linkManager");
            }
            if (pluginManager == null)
            {
                throw new ArgumentNullException("pluginManager");
            }
            _store = store;
            _linkManager = linkManager;
            _pluginManager = pluginManager;
            _discovery = new WinRtDatagramDiscovery(OnDiscoveryDatagram);

            _linkManager.LinkEstablished += OnLinkEstablished;
            _linkManager.LinkFailed += OnLinkFailed;
            _linkManager.LinkClosed += OnLinkClosed;
            _linkManager.PacketReceived += OnPacketReceived;

            // Pinning is resolved by device id so inbound and outbound links
            // are both verified against the stored trust record.
            _linkManager.SetPinLookup(GetPinnedCertificate);

            LoadStoredDevices();
        }

        public LinkManager LinkManager { get { return _linkManager; } }

        public PluginManager PluginManager { get { return _pluginManager; } }

        public DeviceStore.OwnIdentity OwnIdentity { get { lock (_gate) return _ownIdentity; } }

        /// <summary>Declared to peers; comes from the plugins that exist.</summary>
        public List<string> AdvertisedIncoming { get { return _pluginManager.GetIncomingCapabilities(); } }

        public List<string> AdvertisedOutgoing { get { return _pluginManager.GetOutgoingCapabilities(); } }

        /// <summary>Tells the coordinator who we are. Called once at startup.</summary>
        public void SetOwnIdentity(DeviceStore.OwnIdentity own)
        {
            if (own == null)
            {
                throw new ArgumentNullException("own");
            }
            lock (_gate)
            {
                _ownIdentity = own;
            }

            var handler = OwnIdentityChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        public List<DeviceRecord> GetAllDevices()
        {
            lock (_gate)
            {
                List<DeviceRecord> list = new List<DeviceRecord>();
                foreach (DeviceRecord r in _devices.Values)
                {
                    list.Add(r);
                }
                list.Sort(delegate(DeviceRecord a, DeviceRecord b)
                {
                    string an = a.DeviceName != null && a.DeviceName.Length > 0 ? a.DeviceName : a.DeviceId;
                    string bn = b.DeviceName != null && b.DeviceName.Length > 0 ? b.DeviceName : b.DeviceId;
                    return string.Compare(an, bn, StringComparison.CurrentCultureIgnoreCase);
                });
                return list;
            }
        }

        public DeviceRecord GetDevice(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                return null;
            }
            lock (_gate)
            {
                DeviceRecord r;
                return _devices.TryGetValue(deviceId, out r) ? r : null;
            }
        }

        public bool IsConnected(string deviceId)
        {
            return !string.IsNullOrEmpty(deviceId) && _linkManager.IsConnected(deviceId);
        }

        public bool IsConnecting(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                return false;
            }
            lock (_gate)
            {
                return _connecting.Contains(deviceId);
            }
        }

        public bool IsPaired(string deviceId)
        {
            DeviceRecord r = GetDevice(deviceId);
            return r != null && r.IsPaired;
        }

        /// <summary>True when a pairing attempt is in flight for this device.</summary>
        public bool IsPairing(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                return false;
            }
            lock (_gate)
            {
                Pairing p;
                return _pairings.TryGetValue(deviceId, out p) && p.Handler.IsBusy;
            }
        }

        /// <summary>The code both sides must compare, or null when not pairing.</summary>
        public string GetVerificationCode(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                return null;
            }
            lock (_gate)
            {
                Pairing p;
                if (!_pairings.TryGetValue(deviceId, out p) || p.PeerCertificateDer == null)
                {
                    return null;
                }
                byte[] ownKey = _linkManager.LocalIdentity.PublicKeyDer;
                if (ownKey == null)
                {
                    return null;
                }
                return PairingHandler.ComputeVerificationCode(
                    ownKey, PublicKeyOf(p.PeerCertificateDer), p.Handler.PairingTimestamp);
            }
        }

        // ---- discovery -------------------------------------------------------

        public async Task StartDiscoveryAsync()
        {
            CancellationToken token;
            lock (_gate)
            {
                if (_discoveryCts != null)
                {
                    return;
                }
                _discoveryCts = new CancellationTokenSource();
                token = _discoveryCts.Token;
            }

            await _discovery.StartReceivingAsync(token);
            await SendDiscoveryAnnouncementAsync(token);
        }

        public void StopDiscovery()
        {
            CancellationTokenSource cts;
            lock (_gate)
            {
                cts = _discoveryCts;
                _discoveryCts = null;
            }
            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                }
                catch (Exception)
                {
                }
                cts.Dispose();
            }
            _discovery.StopReceiving();
        }

        /// <summary>Re-announces us; used by the Refresh button.</summary>
        public async Task RefreshAsync()
        {
            CancellationToken token;
            lock (_gate)
            {
                if (_discoveryCts == null)
                {
                    return;
                }
                token = _discoveryCts.Token;
            }
            await SendDiscoveryAnnouncementAsync(token);
        }

        /// <summary>Our own identity datagram, or null before the identity exists.</summary>
        private byte[] BuildIdentityDatagram()
        {
            DeviceStore.OwnIdentity own = OwnIdentity;
            if (own == null)
            {
                return null;
            }

            DeviceInfo info = new DeviceInfo(
                own.DeviceId,
                string.IsNullOrEmpty(own.DeviceName) ? "WP81" : own.DeviceName,
                DeviceKind.Phone,
                ProtocolConstants.ProtocolVersion);
            info.IncomingCapabilities.AddRange(AdvertisedIncoming);
            info.OutgoingCapabilities.AddRange(AdvertisedOutgoing);
            return info.ToUdpDiscoveryPacket(ProtocolConstants.MinTcpPort).Serialize();
        }

        private async Task SendDiscoveryAnnouncementAsync(CancellationToken token)
        {
            byte[] payload = BuildIdentityDatagram();
            if (payload == null)
            {
                return;
            }
            List<string> targets = WinRtDatagramDiscovery.BuildTargets(null);
            await WinRtDatagramDiscovery.SendDiscoveryAsync(
                payload, ProtocolConstants.UdpPort, targets, token);
        }

        /// <summary>
        /// Manual "add device by IP": unicasts our identity to one host instead
        /// of broadcasting.
        ///
        /// This is the guaranteed fallback when broadcast is blocked, and it is
        /// deliberately one-way. We cannot dial the peer first because the
        /// outbound connection packet needs their device id, which we do not
        /// know - but announcing ourselves to them is enough: the peer will open
        /// the TCP link to our listener, where we play the acceptor role.
        /// </summary>
        public async Task<bool> ProbeAddressAsync(string ipv4)
        {
            if (!Sanitize.IsIPv4(ipv4))
            {
                return false;
            }

            // The listener must be bound first, or the peer has nowhere to reply.
            await StartDiscoveryAsync();

            CancellationToken token;
            lock (_gate)
            {
                if (_discoveryCts == null)
                {
                    return false;
                }
                token = _discoveryCts.Token;
            }

            byte[] payload = BuildIdentityDatagram();
            if (payload == null)
            {
                return false;
            }

            List<string> targets = new List<string>();
            targets.Add(ipv4);
            List<string> sent = await WinRtDatagramDiscovery.SendToExactTargetsAsync(
                payload, ProtocolConstants.UdpPort, targets, token);
            return sent.Count > 0;
        }

        private void OnDiscoveryDatagram(string json, string fromAddress)
        {
            try
            {
                NetworkPacket np = NetworkPacket.Unserialize(json);
                if (!DeviceInfo.IsValidUdpDiscoveryPacket(np))
                {
                    return;
                }

                string peerId = np.GetString("deviceId", string.Empty);
                DeviceStore.OwnIdentity own = OwnIdentity;
                if (own != null && string.Equals(peerId, own.DeviceId, StringComparison.Ordinal))
                {
                    return; // our own broadcast
                }

                DeviceInfo info = DeviceInfo.FromIdentityPacketAndCert(np, null);
                int tcpPort = np.GetInt("tcpPort", ProtocolConstants.MinTcpPort);

                lock (_gate)
                {
                    DeviceRecord record;
                    if (!_devices.TryGetValue(info.Id, out record))
                    {
                        record = new DeviceRecord();
                        record.DeviceId = info.Id;
                        _devices[info.Id] = record;
                    }

                    record.DeviceName = info.Name;
                    record.DeviceType = DeviceInfo.KindToString(info.Kind);
                    record.ProtocolVersion = info.ProtocolVersion;
                    record.LastAddress = fromAddress;
                    record.LastTcpPort = tcpPort;
                    record.LastSeenUnixSeconds = NowUnixSeconds();
                }

                NotifyDeviceListChanged();
            }
            catch (Exception)
            {
                // One malformed datagram must never stop the listener.
            }
        }

        // ---- connection ------------------------------------------------------

        /// <summary>Connects to a device using its last seen address/port.</summary>
        public async Task<bool> ConnectAsync(string deviceId)
        {
            DeviceRecord record = GetDevice(deviceId);
            if (record == null || string.IsNullOrEmpty(record.LastAddress))
            {
                return false;
            }
            return await ConnectAsync(deviceId, record.LastAddress, record.LastTcpPort);
        }

        /// <summary>Connects to an explicit address (manual-IP fallback).</summary>
        public async Task<bool> ConnectAsync(string deviceId, string address, int port)
        {
            if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(address))
            {
                return false;
            }

            lock (_gate)
            {
                _connecting.Add(deviceId);
            }
            NotifyDeviceListChanged();

            try
            {
                await _linkManager.ConnectAsync(deviceId, address, port);
                return _linkManager.IsConnected(deviceId);
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                lock (_gate)
                {
                    _connecting.Remove(deviceId);
                }
                NotifyDeviceListChanged();
            }
        }

        /// <summary>Adds a device reached by typing an IP, with no discovery.</summary>
        public DeviceRecord AddManualDevice(string deviceId, string address, int port, string name)
        {
            if (!Sanitize.IsValidDeviceId(deviceId))
            {
                return null;
            }
            DeviceRecord record = GetDevice(deviceId);
            lock (_gate)
            {
                if (record == null)
                {
                    record = new DeviceRecord();
                    record.DeviceId = deviceId;
                    _devices[deviceId] = record;
                }
                record.LastAddress = address;
                record.LastTcpPort = port;
                record.LastSeenUnixSeconds = NowUnixSeconds();
                if (!string.IsNullOrEmpty(name))
                {
                    record.DeviceName = name;
                }
            }
            NotifyDeviceListChanged();
            return record;
        }

        /// <summary>Unpairs: drops the link, the pin and the trust record.</summary>
        public bool Unpair(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                return false;
            }

            _linkManager.Disconnect(deviceId);
            _store.RemovePeer(deviceId);

            lock (_gate)
            {
                _pairings.Remove(deviceId);
                DeviceRecord record;
                if (_devices.TryGetValue(deviceId, out record))
                {
                    record.IsPaired = false;
                    record.PinnedCertificateDer = null;
                    record.PinnedCertificateSha256 = null;
                }
            }

            NotifyDeviceListChanged();
            return true;
        }

        // ---- pairing ---------------------------------------------------------

        /// <summary>We initiate pairing. Requires an established link.</summary>
        public async Task<bool> RequestPairingAsync(string deviceId)
        {
            if (!IsConnected(deviceId))
            {
                bool connected = await ConnectAsync(deviceId);
                if (!connected)
                {
                    return false;
                }
            }

            Pairing pairing;
            lock (_gate)
            {
                if (!_pairings.TryGetValue(deviceId, out pairing))
                {
                    pairing = new Pairing();
                    _pairings[deviceId] = pairing;
                }
            }

            PairingEvent outcome;
            NetworkPacket packet = pairing.Handler.RequestPairing(NowUnixSeconds(), out outcome);
            if (packet != null)
            {
                _linkManager.SendPacket(deviceId, packet);
            }

            NotifyPairingStateChanged(deviceId, outcome);
            return packet != null;
        }

        /// <summary>We accept a pending incoming request and pin the peer cert.</summary>
        public bool AcceptPairing(string deviceId)
        {
            Pairing pairing;
            lock (_gate)
            {
                if (!_pairings.TryGetValue(deviceId, out pairing))
                {
                    return false;
                }
            }

            PairingEvent outcome;
            NetworkPacket packet = pairing.Handler.AcceptPairing(out outcome);
            if (packet == null)
            {
                return false;
            }

            _linkManager.SendPacket(deviceId, packet);
            PersistPairing(deviceId, pairing);
            NotifyPairingStateChanged(deviceId, outcome);
            return true;
        }

        public bool RejectPairing(string deviceId)
        {
            Pairing pairing;
            lock (_gate)
            {
                if (!_pairings.TryGetValue(deviceId, out pairing))
                {
                    return false;
                }
            }

            PairingEvent outcome;
            NetworkPacket packet = pairing.Handler.CancelPairing(out outcome);
            if (packet != null)
            {
                _linkManager.SendPacket(deviceId, packet);
            }
            NotifyPairingStateChanged(deviceId, outcome);
            return true;
        }

        private void OnPacketReceived(object sender, PacketEventArgs e)
        {
            if (e == null || e.Packet == null)
            {
                return;
            }
            if (!string.Equals(e.Packet.Type, ProtocolConstants.PacketTypePair, StringComparison.Ordinal))
            {
                return;
            }

            Pairing pairing;
            bool created = false;
            lock (_gate)
            {
                if (!_pairings.TryGetValue(e.DeviceId, out pairing))
                {
                    pairing = new Pairing();
                    _pairings[e.DeviceId] = pairing;
                    created = true;
                }
            }

            // The peer certificate only exists after the handshake, so capture
            // it from the link session the first time we see a pair packet.
            if (created || pairing.PeerCertificateDer == null)
            {
                pairing.PeerCertificateDer = GetPeerCertificate(e.DeviceId);
            }

            PairingEvent outcome;
            NetworkPacket reply = pairing.Handler.OnPacketReceived(e.Packet, NowUnixSeconds(), out outcome);

            if (reply != null)
            {
                _linkManager.SendPacket(e.DeviceId, reply);
            }

            if (outcome == PairingEvent.IncomingRequest)
            {
                var handler = PairingRequestReceived;
                if (handler != null)
                {
                    PairingEventArgs args = new PairingEventArgs();
                    args.DeviceId = e.DeviceId;
                    args.VerificationCode = GetVerificationCode(e.DeviceId);
                    handler(this, args);
                }
            }
            else if (outcome == PairingEvent.Accepted)
            {
                // Their accept (or our own round-trip completing) means trust.
                PersistPairing(e.DeviceId, pairing);
            }
            else if (outcome == PairingEvent.Unpaired || outcome == PairingEvent.CancelledByPeer)
            {
                lock (_gate)
                {
                    _pairings.Remove(e.DeviceId);
                }
                Unpair(e.DeviceId);
            }

            NotifyPairingStateChanged(e.DeviceId, outcome);
        }

        private void PersistPairing(string deviceId, Pairing pairing)
        {
            DeviceRecord record = GetDevice(deviceId);
            if (record == null)
            {
                return;
            }

            byte[] cert = pairing.PeerCertificateDer;
            if (cert == null)
            {
                // Refuse to record trust without something to pin: a pairing
                // that stores no certificate cannot be verified later.
                return;
            }

            lock (_gate)
            {
                record.IsPaired = true;
                record.PinnedCertificateDer = cert;
                record.PinnedCertificateSha256 = Redactor.CertificateFingerprint(cert, 8);
                record.PairedAtUnixSeconds = NowUnixSeconds();
            }

            _store.UpsertPeer(record);
            NotifyDeviceListChanged();
        }

        private byte[] GetPinnedCertificate(string deviceId)
        {
            DeviceRecord record = GetDevice(deviceId);
            if (record == null || !record.IsPaired)
            {
                return null;
            }
            return record.PinnedCertificateDer;
        }

        private byte[] GetPeerCertificate(string deviceId)
        {
            // The link session keeps the presented certificate; exposing it
            // through LinkManager would widen that type's surface, so the
            // record is updated from the link event instead (below).
            DeviceRecord record = GetDevice(deviceId);
            if (record != null && record.PinnedCertificateDer != null)
            {
                return record.PinnedCertificateDer;
            }
            lock (_gate)
            {
                byte[] transient;
                if (_peerCertificates.TryGetValue(deviceId, out transient))
                {
                    return transient;
                }
            }
            return null;
        }

        private readonly Dictionary<string, byte[]> _peerCertificates =
            new Dictionary<string, byte[]>(StringComparer.Ordinal);

        // ---- link events -----------------------------------------------------

        private void OnLinkEstablished(object sender, LinkEventArgs e)
        {
            lock (_gate)
            {
                _connecting.Remove(e.DeviceId);

                DeviceRecord record;
                if (!_devices.TryGetValue(e.DeviceId, out record))
                {
                    record = new DeviceRecord();
                    record.DeviceId = e.DeviceId;
                    _devices[e.DeviceId] = record;
                }

                if (e.PeerInfo != null)
                {
                    record.DeviceName = e.PeerInfo.Name;
                    record.DeviceType = DeviceInfo.KindToString(e.PeerInfo.Kind);
                    record.ProtocolVersion = e.PeerInfo.ProtocolVersion;
                    record.IncomingCapabilities = new List<string>(e.PeerInfo.IncomingCapabilities);
                    record.OutgoingCapabilities = new List<string>(e.PeerInfo.OutgoingCapabilities);
                }

                if (!string.IsNullOrEmpty(e.RemoteAddress))
                {
                    record.LastAddress = e.RemoteAddress;
                }
                if (e.RemotePort > 0)
                {
                    record.LastTcpPort = e.RemotePort;
                }
                record.LastSeenUnixSeconds = NowUnixSeconds();

                // Keep the peer certificate available for the verification code.
                if (e.PeerCertificateDer != null)
                {
                    _peerCertificates[e.DeviceId] = e.PeerCertificateDer;
                }
            }

            NotifyDeviceListChanged();
            _pluginManager.OnLinkEstablished(e.DeviceId, e.Session, e.Reader);
        }

        private void OnLinkFailed(object sender, LinkEventArgs e)
        {
            lock (_gate)
            {
                _connecting.Remove(e.DeviceId);
            }
            NotifyDeviceListChanged();
        }

        private void OnLinkClosed(object sender, LinkEventArgs e)
        {
            lock (_gate)
            {
                _pairings.Remove(e.DeviceId);
                _peerCertificates.Remove(e.DeviceId);
            }
            NotifyDeviceListChanged();
            _pluginManager.OnLinkClosed(e.DeviceId);
        }

        private void LoadStoredDevices()
        {
            List<DeviceRecord> stored = _store.LoadPeers();
            lock (_gate)
            {
                for (int i = 0; i < stored.Count; i++)
                {
                    _devices[stored[i].DeviceId] = stored[i];
                }
            }
        }

        public void NotifyDeviceListChanged()
        {
            var handler = DeviceListChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void NotifyPairingStateChanged(string deviceId, PairingEvent outcome)
        {
            var handler = PairingStateChanged;
            if (handler != null)
            {
                PairingEventArgs args = new PairingEventArgs();
                args.DeviceId = deviceId;
                args.Outcome = outcome;
                args.VerificationCode = GetVerificationCode(deviceId);
                handler(this, args);
            }
        }

        // ---- helpers ---------------------------------------------------------

        private static long NowUnixSeconds()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        private static byte[] PublicKeyOf(byte[] certificateDer)
        {
            if (certificateDer == null || certificateDer.Length == 0)
            {
                return null;
            }
            TlsIdentity temporary = new TlsIdentity(null, certificateDer, null);
            return temporary.PublicKeyDer;
        }

        private sealed class Pairing
        {
            public Pairing()
            {
                Handler = new PairingHandler();
            }

            public PairingHandler Handler { get; private set; }

            /// <summary>Peer certificate DER captured at link time.</summary>
            public byte[] PeerCertificateDer { get; set; }
        }
    }

    public sealed class PairingEventArgs : EventArgs
    {
        public string DeviceId { get; set; }
        public string VerificationCode { get; set; }
        public PairingEvent Outcome { get; set; }
    }
}
