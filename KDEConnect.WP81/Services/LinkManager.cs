using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KDEConnect.WP81.Core;
using KDEConnect.WP81.Security;
using KDEConnect.WP81.Transport;

namespace KDEConnect.WP81.Services
{
    /// <summary>
    /// Manages outbound/inbound TCP links, TLS handshakes and the per-session
    /// packet loops. Wires WinRtTcpDialer/WinRtTcpListener + LinkEstablisher +
    /// BouncyCastleTlsBackend into the single component DeviceManager uses.
    ///
    /// <see cref="Stop"/> and <see cref="StartAsync"/> are a pair, not a
    /// one-way shutdown: Windows Phone kills sockets on suspend, so the
    /// listener has to be rebuildable when the app resumes
    /// (docs/known-limitations.md L2).
    /// </summary>
    public sealed class LinkManager : IDisposable
    {
        public event EventHandler<LinkEventArgs> LinkEstablished;
        public event EventHandler<LinkEventArgs> LinkFailed;
        public event EventHandler<LinkEventArgs> LinkClosed;
        public event EventHandler<PacketEventArgs> PacketReceived;

        private readonly BouncyCastleTlsBackend _tlsBackend = new BouncyCastleTlsBackend();
        private readonly TlsIdentity _localIdentity;
        private readonly DeviceInfo _localInfo;
        private readonly object _gate = new object();
        private readonly Dictionary<string, LinkSession> _sessions =
            new Dictionary<string, LinkSession>(StringComparer.Ordinal);

        private WinRtTcpListener _listener;
        private CancellationTokenSource _cts;
        private Func<string, byte[]> _pinLookup;

        public LinkManager(TlsIdentity localIdentity, DeviceInfo localInfo)
        {
            if (localIdentity == null)
            {
                throw new ArgumentNullException("localIdentity");
            }
            if (localInfo == null)
            {
                throw new ArgumentNullException("localInfo");
            }
            _localIdentity = localIdentity;
            _localInfo = localInfo;
        }

        public TlsIdentity LocalIdentity { get { return _localIdentity; } }

        public DeviceInfo LocalInfo { get { return _localInfo; } }

        /// <summary>
        /// Supplies the pinned peer certificate (DER) for a device id, or null
        /// when the peer is not paired. Without this, both directions would be
        /// blind trust-on-first-use and a re-keyed impostor would be accepted.
        /// </summary>
        public void SetPinLookup(Func<string, byte[]> lookup)
        {
            _pinLookup = lookup;
        }

        public bool IsListening
        {
            get
            {
                lock (_gate)
                {
                    return _listener != null;
                }
            }
        }

        public bool IsConnected(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                return false;
            }
            lock (_gate)
            {
                LinkSession session;
                return _sessions.TryGetValue(deviceId, out session) && session.IsActive;
            }
        }

        public int ConnectedCount
        {
            get
            {
                lock (_gate)
                {
                    int n = 0;
                    foreach (var s in _sessions.Values)
                    {
                        if (s.IsActive) n++;
                    }
                    return n;
                }
            }
        }

        /// <summary>Starts (or restarts) the inbound listener. Idempotent.</summary>
        public async Task StartAsync()
        {
            CancellationTokenSource cts;
            lock (_gate)
            {
                if (_cts != null || _listener != null)
                {
                    return;
                }
                cts = new CancellationTokenSource();
                _cts = cts;
            }

            WinRtTcpListener listener =
                new WinRtTcpListener(ProtocolConstants.MinTcpPort, OnInboundConnection);
            try
            {
                await listener.StartAsync(cts.Token);
            }
            catch (Exception)
            {
                cts.Dispose();
                lock (_gate)
                {
                    _cts = null;
                }
                throw;
            }

            lock (_gate)
            {
                _listener = listener;
            }
        }

        /// <summary>Tears down the listener and every session. Safe to call twice.</summary>
        public void Stop()
        {
            CancellationTokenSource cts;
            WinRtTcpListener listener;
            lock (_gate)
            {
                cts = _cts;
                _cts = null;
                listener = _listener;
                _listener = null;
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

            if (listener != null)
            {
                listener.Dispose();
            }

            List<LinkSession> closing = new List<LinkSession>();
            lock (_gate)
            {
                foreach (var s in _sessions.Values)
                {
                    closing.Add(s);
                }
                _sessions.Clear();
            }

            foreach (var session in closing)
            {
                session.IsActive = false;
                try
                {
                    session.TlsSession.Close();
                }
                catch (Exception)
                {
                }
                NotifyClosed(session.DeviceId);
            }
        }

        /// <summary>Initiates an outbound connection to a known peer.</summary>
        public async Task ConnectAsync(string deviceId, string address, int port)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                throw new ArgumentException("deviceId is required", "deviceId");
            }
            if (string.IsNullOrEmpty(address))
            {
                throw new ArgumentException("address is required", "address");
            }
            if (port < ProtocolConstants.MinTcpPort || port > ProtocolConstants.MaxTcpPort)
            {
                throw new ArgumentOutOfRangeException("port",
                    "KDE Connect uses TCP " + ProtocolConstants.MinTcpPort +
                    "-" + ProtocolConstants.MaxTcpPort);
            }

            lock (_gate)
            {
                LinkSession existing;
                if (_sessions.TryGetValue(deviceId, out existing) && existing.IsActive)
                {
                    return; // already connected
                }
            }

            CancellationToken token = CurrentToken();

            RawChannel channel = await WinRtTcpDialer.ConnectAsync(
                address, port, TimeSpan.FromSeconds(10), token);

            TlsPeerPolicy policy = new TlsPeerPolicy();
            policy.ExpectedDeviceId = deviceId;
            policy.PinLookup = _pinLookup;

            LinkEstablishResult result = await LinkEstablisher.EstablishOutboundAsync(
                channel, _tlsBackend, _localIdentity, _localInfo, null, policy,
                TimeSpan.FromSeconds(15), token);

            if (!result.Success)
            {
                NotifyFailed(deviceId, address, port);
                throw new InvalidOperationException("Link failed: " + result.Failure);
            }

            CompleteSession(deviceId, result, address, port);
        }

        public void Disconnect(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                return;
            }

            LinkSession session;
            lock (_gate)
            {
                if (!_sessions.TryGetValue(deviceId, out session))
                {
                    return;
                }
                _sessions.Remove(deviceId);
                session.IsActive = false;
            }

            try
            {
                session.TlsSession.Close();
            }
            catch (Exception)
            {
            }
            NotifyClosed(deviceId);
        }

        /// <summary>Sends one packet to a connected peer, framed with a newline.</summary>
        public bool SendPacket(string deviceId, NetworkPacket packet)
        {
            if (string.IsNullOrEmpty(deviceId) || packet == null)
            {
                return false;
            }

            LinkSession session;
            lock (_gate)
            {
                if (!_sessions.TryGetValue(deviceId, out session) || !session.IsActive)
                {
                    return false;
                }
            }

            try
            {
                byte[] wire = packet.Serialize();
                session.TlsSession.ApplicationData.Write(wire, 0, wire.Length);
                session.TlsSession.ApplicationData.Flush();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private CancellationToken CurrentToken()
        {
            lock (_gate)
            {
                return _cts != null ? _cts.Token : CancellationToken.None;
            }
        }

        private async void OnInboundConnection(RawChannel channel)
        {
            CancellationToken token = CurrentToken();
            if (token.IsCancellationRequested)
            {
                channel.Dispose();
                return;
            }

            try
            {
                TlsPeerPolicy policy = new TlsPeerPolicy();
                policy.PinLookup = _pinLookup;

                LinkEstablishResult result = await LinkEstablisher.EstablishInboundAsync(
                    channel, _tlsBackend, _localIdentity, _localInfo, policy,
                    TimeSpan.FromSeconds(15), token);

                if (!result.Success)
                {
                    NotifyFailed(
                        result.Peer != null ? result.Peer.Id : "unknown",
                        channel.RemoteAddress, channel.RemotePort);
                    return;
                }

                string deviceId = result.Peer.Id;
                lock (_gate)
                {
                    LinkSession existing;
                    if (_sessions.TryGetValue(deviceId, out existing) && existing.IsActive)
                    {
                        // Duplicate link for a peer we already serve: keep the
                        // first, drop this one rather than fighting over it.
                        try
                        {
                            result.Session.Close();
                        }
                        catch (Exception)
                        {
                        }
                        return;
                    }
                }

                CompleteSession(deviceId, result, channel.RemoteAddress, channel.RemotePort);
            }
            catch (Exception ex)
            {
                NotifyFailed("unknown", channel.RemoteAddress, channel.RemotePort);
                System.Diagnostics.Debug.WriteLine("inbound link failed: " + ex.Message);
            }
        }

        private void CompleteSession(
            string deviceId, LinkEstablishResult result, string remoteAddress, int remotePort)
        {
            LinkSession session = new LinkSession();
            session.DeviceId = deviceId;
            session.TlsSession = result.Session;
            session.Reader = result.Reader;
            session.PeerInfo = result.Peer;
            session.RemoteAddress = remoteAddress;
            session.RemotePort = remotePort;
            session.IsActive = true;

            lock (_gate)
            {
                _sessions[deviceId] = session;
            }

            var established = LinkEstablished;
            if (established != null)
            {
                LinkEventArgs args = new LinkEventArgs();
                args.DeviceId = deviceId;
                args.RemoteAddress = remoteAddress;
                args.RemotePort = remotePort;
                args.PeerInfo = result.Peer;
                args.Session = result.Session;
                args.Reader = result.Reader;
                args.WasPinned = result.Context != null && result.Context.IsTrustedPeer;
                args.PeerCertificateDer = result.Tls != null ? result.Tls.PeerCertificateDer : null;
                established(this, args);
            }

            Task ignored = ReadLoopAsync(session);
        }

        private async Task ReadLoopAsync(LinkSession session)
        {
            while (session.IsActive)
            {
                CancellationToken token = CurrentToken();
                if (token.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    string line = await session.Reader.ReadLineAsync(
                        TimeSpan.FromSeconds(30), token);

                    NetworkPacket packet = NetworkPacket.Unserialize(line);

                    var handler = PacketReceived;
                    if (handler != null)
                    {
                        PacketEventArgs args = new PacketEventArgs();
                        args.DeviceId = session.DeviceId;
                        args.Packet = packet;
                        handler(this, args);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (EndOfStreamException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception)
                {
                    // A malformed or oversized frame must not kill the link:
                    // keep reading until the peer actually closes.
                }
            }

            if (session.IsActive)
            {
                session.IsActive = false;
                lock (_gate)
                {
                    LinkSession current;
                    if (_sessions.TryGetValue(session.DeviceId, out current) && current == session)
                    {
                        _sessions.Remove(session.DeviceId);
                    }
                }
                try
                {
                    session.TlsSession.Close();
                }
                catch (Exception)
                {
                }
                NotifyClosed(session.DeviceId);
            }
        }

        private void NotifyFailed(string deviceId, string address, int port)
        {
            var handler = LinkFailed;
            if (handler != null)
            {
                LinkEventArgs args = new LinkEventArgs();
                args.DeviceId = deviceId;
                args.RemoteAddress = address;
                args.RemotePort = port;
                handler(this, args);
            }
        }

        private void NotifyClosed(string deviceId)
        {
            var handler = LinkClosed;
            if (handler != null)
            {
                LinkEventArgs args = new LinkEventArgs();
                args.DeviceId = deviceId;
                handler(this, args);
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private sealed class LinkSession
        {
            public string DeviceId { get; set; }
            public ITlsSession TlsSession { get; set; }
            public BufferedLineReader Reader { get; set; }
            public DeviceInfo PeerInfo { get; set; }
            public string RemoteAddress { get; set; }
            public int RemotePort { get; set; }
            public bool IsActive { get; set; }
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

        /// <summary>True when the peer certificate matched a stored pin.</summary>
        public bool WasPinned { get; set; }

        /// <summary>DER of the certificate the peer presented, for pinning.</summary>
        public byte[] PeerCertificateDer { get; set; }
    }

    public sealed class PacketEventArgs : EventArgs
    {
        public string DeviceId { get; set; }
        public NetworkPacket Packet { get; set; }
    }
}
