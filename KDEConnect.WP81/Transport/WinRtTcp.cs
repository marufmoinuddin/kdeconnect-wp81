using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using KDEConnect.WP81.Core;

namespace KDEConnect.WP81.Transport
{
    /// <summary>
    /// Outbound TCP connections.
    ///
    /// Note the KDE Connect role inversion: the side that dials becomes the TLS
    /// *server* (docs/protocol.md §4), so the caller of <see cref="ConnectAsync"/>
    /// must run HandshakeAsServerAsync on the returned channel.
    /// </summary>
    public static class WinRtTcpDialer
    {
        public static async Task<RawChannel> ConnectAsync(
            string host,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(host))
            {
                throw new ArgumentNullException("host");
            }

            StreamSocket socket = new StreamSocket();
            bool handedOver = false;
            try
            {
                HostName name = new HostName(host);
                string service = port.ToString(CultureInfo.InvariantCulture);

                Task connecting = socket.ConnectAsync(name, service).AsTask(cancellationToken);
                Task timing = Task.Delay(timeout, cancellationToken);
                Task first = await Task.WhenAny(connecting, timing).ConfigureAwait(false);

                if (first != connecting)
                {
                    throw new TimeoutException("TCP connect to " + host + ":" + service +
                                                " did not complete within " +
                                                timeout.TotalMilliseconds.ToString("F0") + " ms");
                }

                // Propagate the real connect failure (unreachable, refused, ...).
                await connecting.ConfigureAwait(false);

                string remote = socket.Information != null && socket.Information.RemoteAddress != null
                    ? socket.Information.RemoteAddress.DisplayName
                    : host;

                RawChannel channel = new RawChannel(
                    new WinRtStreamAdapter(socket.InputStream, socket.OutputStream, false),
                    socket,
                    remote,
                    port);
                handedOver = true;
                return channel;
            }
            finally
            {
                if (!handedOver)
                {
                    try
                    {
                        socket.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
    }

    /// <summary>
    /// Inbound TCP listener. The accepted side plays the TLS *client* role in
    /// KDE Connect, so callers run HandshakeAsClientAsync on these channels.
    /// </summary>
    public sealed class WinRtTcpListener : IDisposable
    {
        private readonly int _port;
        private readonly Action<RawChannel> _onConnection;
        private StreamSocketListener _listener;
        private bool _disposed;

        public WinRtTcpListener(int port, Action<RawChannel> onConnection)
        {
            if (onConnection == null)
            {
                throw new ArgumentNullException("onConnection");
            }
            if (port < ProtocolConstants.MinTcpPort || port > ProtocolConstants.MaxTcpPort)
            {
                throw new ArgumentOutOfRangeException("port",
                    "KDE Connect only uses TCP ports " + ProtocolConstants.MinTcpPort +
                    "-" + ProtocolConstants.MaxTcpPort);
            }
            _port = port;
            _onConnection = onConnection;
        }

        /// <summary>Bound port after StartAsync (useful when port 0 semantics are added later).</summary>
        public int Port { get { return _port; } }

        public bool IsListening { get { return _listener != null; } }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException("WinRtTcpListener");
            }
            if (_listener != null)
            {
                return;
            }

            StreamSocketListener listener = new StreamSocketListener();
            listener.ConnectionReceived += OnConnectionReceived;

            try
            {
                await listener.BindServiceNameAsync(
                    _port.ToString(CultureInfo.InvariantCulture)).AsTask(cancellationToken);
            }
            catch (Exception)
            {
                listener.ConnectionReceived -= OnConnectionReceived;
                try
                {
                    listener.Dispose();
                }
                catch (Exception)
                {
                }
                throw;
            }

            _listener = listener;
        }

        private void OnConnectionReceived(
            StreamSocketListener sender,
            StreamSocketListenerConnectionReceivedEventArgs args)
        {
            StreamSocket socket = args.Socket;
            if (socket == null)
            {
                return;
            }

            try
            {
                string remote = socket.Information != null && socket.Information.RemoteAddress != null
                    ? socket.Information.RemoteAddress.DisplayName
                    : string.Empty;

                RawChannel channel = new RawChannel(
                    new WinRtStreamAdapter(socket.InputStream, socket.OutputStream, false),
                    socket,
                    remote,
                    _port);

                _onConnection(channel);
            }
            catch (Exception)
            {
                // A failed channel must never take the listener down with it.
                try
                {
                    socket.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            StreamSocketListener listener = _listener;
            _listener = null;
            if (listener == null)
            {
                return;
            }

            try
            {
                listener.ConnectionReceived -= OnConnectionReceived;
            }
            catch (Exception)
            {
            }
            try
            {
                listener.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }
}
