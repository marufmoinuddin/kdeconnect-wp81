using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KDEConnect.WP81.Core;
using KDEConnect.WP81.Transport;

namespace KDEConnect.WP81.Services
{
    /// <summary>
    /// Manages outbound/inbound TCP links, TLS handshakes, and the per-session
    /// packet loops. Wires WinRtTcpDialer/WinRtTcpListener + LinkEstablisher
    /// + BouncyCastleTlsBackend into a single component the DeviceManager uses.
    /// </summary>
    public sealed class LinkManager : IDisposable
    {
        public event EventHandler<LinkEventArgs> LinkEstablished;
        public event EventHandler<LinkEventArgs> LinkFailed;
        public event EventHandler<LinkEventArgs> LinkClosed;

        private readonly BouncyCastleTlsBackend _tlsBackend;
        private readonly TlsIdentity _localIdentity;
        private readonly DeviceInfo _localInfo;
        private readonly WinRtTcpListener _listener;
        private readonly object _gate = new object();
        private readonly Dictionary<string, LinkSession> _sessions = new Dictionary<string, LinkSession>(StringComparer.Ordinal);
        private readonly CancellationTokenSource _shutdownCts = new CancellationTokenSource();

        public LinkManager(TlsIdentity localIdentity, DeviceInfo localInfo)
        {
            _tlsBackend = new BouncyCastleTlsBackend();
            _localIdentity = localIdentity ?? throw new ArgumentNullException("localIdentity");
            _localInfo = localInfo ?? throw new ArgumentNullException("localInfo");

            _listener = new WinRtTcpListener(ProtocolConstants.MinTcpPort, OnInboundConnection);
        }

        public bool IsConnected(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return false;
            lock (_gate)
            {
                LinkSession s;
                return _sessions.TryGetValue(deviceId, out s) && s.IsActive;
            }
        }

        public async Task StartListeningAsync()
        {
            await _listener.StartAsync(_shutdownCts.Token);
        }

        public void StopListening()
        {
            _listener.Dispose();
        }

        /// <summary>
        /// Initiates an outbound connection to a known peer.
        /// </summary>
        public async Task ConnectAsync(string deviceId, string address, int port)
        {
            if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(address))
                throw new ArgumentException("deviceId and address required");

            LinkSession existing;
            lock (_gate)
            {
                if (_sessions.TryGetValue(deviceId, out existing) && existing.IsActive)
                    return; // already connected
            }

            RawChannel channel = await WinRtTcpDialer.ConnectAsync(
                address, port, TimeSpan.FromSeconds(10), _shutdownCts.Token);

            var policy = new TlsPeerPolicy
            {
                ExpectedDeviceId = deviceId
            };

            // Check if we have a pinned certificate for this peer
            // (would be loaded from DeviceRecord in DeviceManager.ConnectAsync)
            // For now we trust on first use (TOFU).

            var result = await LinkEstablisher.EstablishOutboundAsync(
                channel, _tlsBackend, _localIdentity, _localInfo, null, policy,
                TimeSpan.FromSeconds(15), _shutdownCts.Token);

            if (!result.Success)
            {
                var handler = LinkFailed;
                if (handler != null) handler(this, new LinkEventArgs { DeviceId = deviceId, RemoteAddress = address, RemotePort = port });
                throw new InvalidOperationException("Link failed: " + result.Failure);
            }

            var session = new LinkSession
            {
                DeviceId = deviceId,
                TlsSession = result.Session,
                Reader = result.Reader,
                PeerInfo = result.Peer,
                RemoteAddress = address,
                RemotePort = port,
                IsActive = true
            };

            lock (_gate) _sessions[deviceId] = session;

            var established = LinkEstablished;
            if (established != null)
                established(this, new LinkEventArgs
                {
                    DeviceId = deviceId,
                    RemoteAddress = address,
                    RemotePort = port,
                    PeerInfo = result.Peer,
                    Session = result.Session,
                    Reader = result.Reader
                });

            // Start the packet read loop
            _ = ReadLoopAsync(session);
        }

        public void Disconnect(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return;

            LinkSession session;
            lock (_gate)
            {
                if (!_sessions.TryGetValue(deviceId, out session) || !session.IsActive)
                    return;
                session.IsActive = false;
            }

            try { session.TlsSession.Close(); } catch (Exception) { }
            _sessions.Remove(deviceId);

            var handler = LinkClosed;
            if (handler != null) handler(this, new LinkEventArgs { DeviceId = deviceId });
        }

        /// <summary>Sends a packet to a connected peer.</summary>
        public bool SendPacket(string deviceId, NetworkPacket packet)
        {
            if (string.IsNullOrEmpty(deviceId) || packet == null) return false;

            LinkSession session;
            lock (_gate)
            {
                if (!_sessions.TryGetValue(deviceId, out session) || !session.IsActive)
                    return false;
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

        /// <summary>Gets the local TlsIdentity for verification code computation.</summary>
        public TlsIdentity LocalIdentity { get { return _localIdentity; } }

        private async void OnInboundConnection(RawChannel channel)
        {
            if (_shutdownCts.Token.IsCancellationRequested) return;

            var policy = new TlsPeerPolicy(); // TOFU - no expected device ID, no pin

            var result = await LinkEstablisher.EstablishInboundAsync(
                channel, _tlsBackend, _localIdentity, _localInfo, policy,
                TimeSpan.FromSeconds(15), _shutdownCts.Token);

            if (!result.Success)
            {
                var handler = LinkFailed;
                if (handler != null) handler(this, new LinkEventArgs { DeviceId = result.Peer?.Id ?? "unknown" });
                return;
            }

            string deviceId = result.Peer.Id;
            LinkSession existing;
            lock (_gate)
            {
                if (_sessions.TryGetValue(deviceId, out existing) && existing.IsActive)
                {
                    try { result.Session.Close(); } catch (Exception) { }
                    return;
                }
            }

            var session = new LinkSession
            {
                DeviceId = deviceId,
                TlsSession = result.Session,
                Reader = result.Reader,
                PeerInfo = result.Peer,
                RemoteAddress = channel.RemoteAddress,
                RemotePort = channel.RemotePort,
                IsActive = true
            };

            lock (_gate) _sessions[deviceId] = session;

            var established = LinkEstablished;
            if (established != null)
                established(this, new LinkEventArgs
                {
                    DeviceId = deviceId,
                    RemoteAddress = channel.RemoteAddress,
                    RemotePort = channel.RemotePort,
                    PeerInfo = result.Peer,
                    Session = result.Session,
                    Reader = result.Reader
                });

            _ = ReadLoopAsync(session);
        }

        private async Task ReadLoopAsync(LinkSession session)
        {
            while (session.IsActive && !_shutdownCts.Token.IsCancellationRequested)
            {
                try
                {
                    string line = await session.Reader.ReadLineAsync(
                        TimeSpan.FromSeconds(30), _shutdownCts.Token);

                    NetworkPacket packet = NetworkPacket.Unserialize(line);

                    // Forward to plugin manager via event (subscribed by DeviceManager)
                    var handler = PacketReceived;
                    if (handler != null)
                        handler(this, new PacketEventArgs { DeviceId = session.DeviceId, Packet = packet });
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (EndOfStreamException)
                {
                    break;
                }
                catch (Exception)
                {
                    // Malformed packet or read error - continue trying
                }
            }

            // Link died
            if (session.IsActive)
            {
                session.IsActive = false;
                var handler = LinkClosed;
                if (handler != null) handler(this, new LinkEventArgs { DeviceId = session.DeviceId });
            }
        }

        public event EventHandler<PacketEventArgs> PacketReceived;

        public void Dispose()
        {
            _shutdownCts.Cancel();
            try { _listener.Dispose(); } catch (Exception) { }

            lock (_gate)
            {
                foreach (var s in _sessions.Values)
                {
                    try { s.TlsSession.Close(); } catch (Exception) { }
                }
                _sessions.Clear();
            }
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

    public sealed class PacketEventArgs : EventArgs
    {
        public string DeviceId { get; set; }
        public NetworkPacket Packet { get; set; }
    }
}