using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using KDEConnect.WP81.Core;

namespace KDEConnect.WP81.Transport
{
    /// <summary>
    /// UDP discovery: receive on 1716, send identity datagrams.
    ///
    /// Sending is class 4 (partially supported), not class 2: Microsoft never
    /// documents broadcast-send behaviour for Windows 8.1/Phone 8.1, and the
    /// community reports that limited broadcast 255.255.255.255 is not received
    /// on WP8.1 unless the WLAN adapter is bound (docs/known-limitations.md L7).
    /// Three target strategies are therefore tried in order and manual IP entry
    /// remains available as the guaranteed path.
    /// </summary>
    public sealed class WinRtDatagramDiscovery : IDisposable
    {
        /// <summary>Mirrors ProtocolConstants.UdpPort.</summary>
        public const int DiscoveryPort = ProtocolConstants.UdpPort;

        private readonly Action<string, string> _onDatagram;
        private DatagramSocket _receiver;
        private bool _disposed;

        /// <summary>json = raw datagram text, senderAddress = remote IP.</summary>
        public WinRtDatagramDiscovery(Action<string, string> onDatagram)
        {
            if (onDatagram == null)
            {
                throw new ArgumentNullException("onDatagram");
            }
            _onDatagram = onDatagram;
        }

        public bool IsListening { get { return _receiver != null; } }

        /// <summary>
        /// Binds UDP 1716 so other devices' broadcasts reach us. Failure to
        /// bind is reported to the caller rather than swallowed: discovery would
        /// silently half-work otherwise, and manual IP entry still functions.
        /// </summary>
        public async Task StartReceivingAsync(CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException("WinRtDatagramDiscovery");
            }
            if (_receiver != null)
            {
                return;
            }

            DatagramSocket socket = new DatagramSocket();
            socket.MessageReceived += OnMessageReceived;

            try
            {
                await socket.BindServiceNameAsync(
                    DiscoveryPort.ToString(CultureInfo.InvariantCulture)).AsTask(cancellationToken);
            }
            catch (Exception)
            {
                socket.MessageReceived -= OnMessageReceived;
                try
                {
                    socket.Dispose();
                }
                catch (Exception)
                {
                }
                throw;
            }

            _receiver = socket;
        }

        /// <summary>
        /// Releases the bound socket without poisoning the instance, so
        /// discovery can be restarted after the app resumes from suspend.
        /// </summary>
        public void StopReceiving()
        {
            DatagramSocket socket = _receiver;
            _receiver = null;
            if (socket == null)
            {
                return;
            }
            try
            {
                socket.MessageReceived -= OnMessageReceived;
            }
            catch (Exception)
            {
            }
            try
            {
                socket.Dispose();
            }
            catch (Exception)
            {
            }
        }

        private void OnMessageReceived(DatagramSocket sender, DatagramSocketMessageReceivedEventArgs args)
        {
            try
            {
                DataReader reader = args.GetDataReader();
                uint length = reader.UnconsumedBufferLength;
                if (length == 0)
                {
                    return;
                }
                // Hard ceiling so a hostile datagram cannot make us allocate
                // without bound (docs/protocol.md §2).
                if (length > ProtocolConstants.MaxUdpDatagramSize)
                {
                    return;
                }

                byte[] bytes = new byte[length];
                reader.ReadBytes(bytes);

                string json = Encoding.UTF8.GetString(bytes, 0, bytes.Length);
                string from = args.RemoteAddress != null ? args.RemoteAddress.DisplayName : string.Empty;

                _onDatagram(json, from);
            }
            catch (Exception)
            {
                // One malformed datagram must not stop the listener.
            }
        }

        /// <summary>
        /// Sends one discovery datagram to every candidate target, trying each
        /// strategy so a single platform quirk cannot disable discovery.
        /// Returns the addresses that accepted the send (not a delivery proof).
        /// </summary>
        public static async Task<List<string>> SendDiscoveryAsync(
            byte[] payload,
            int targetPort,
            IEnumerable<string> explicitTargets,
            CancellationToken cancellationToken)
        {
            if (payload == null || payload.Length == 0)
            {
                throw new ArgumentNullException("payload");
            }

            List<string> sent = new List<string>();
            List<string> targets = BuildTargets(explicitTargets);

            foreach (string target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await SendOnceAsync(target, payload, targetPort, cancellationToken);
                    if (!sent.Contains(target))
                    {
                        sent.Add(target);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Individual targets may be blocked by the platform; the
                    // next strategy is still worth trying.
                }
            }

            return sent;
        }

        /// <summary>
        /// 1. limited broadcast, 2. subnet-directed broadcast for each local
        /// IPv4 address, 3. any operator-supplied addresses (manual IP).
        /// </summary>
        public static List<string> BuildTargets(IEnumerable<string> explicitTargets)
        {
            List<string> targets = new List<string>();
            AddTarget(targets, "255.255.255.255");

            foreach (string directed in GetSubnetBroadcastAddresses())
            {
                AddTarget(targets, directed);
            }

            if (explicitTargets != null)
            {
                foreach (string custom in explicitTargets)
                {
                    if (!string.IsNullOrEmpty(custom))
                    {
                        AddTarget(targets, custom.Trim());
                    }
                }
            }

            return targets;
        }

        private static void AddTarget(List<string> targets, string value)
        {
            if (!string.IsNullOrEmpty(value) && !targets.Contains(value))
            {
                targets.Add(value);
            }
        }

        /// <summary>
        /// Derives x.x.x.255 from each local IPv4 address. This is the fallback
        /// for the case where limited broadcast is not delivered (L7).
        /// </summary>
        public static IEnumerable<string> GetSubnetBroadcastAddresses()
        {
            List<string> results = new List<string>();
            try
            {
                IReadOnlyList<HostName> names = NetworkInformation.GetHostNames();
                for (int i = 0; i < names.Count; i++)
                {
                    HostName name = names[i];
                    if (name == null || name.Type != HostNameType.Ipv4)
                    {
                        continue;
                    }
                    string display = name.DisplayName;
                    if (string.IsNullOrEmpty(display))
                    {
                        continue;
                    }

                    string directed = ToDirectedBroadcast(display);
                    if (directed != null)
                    {
                        AddTarget(results, directed);
                    }
                }
            }
            catch (Exception)
            {
                // No local IPv4 (Wi-Fi off) - limited broadcast alone is still sent.
            }
            return results;
        }

        /// <summary>
        /// Turns "192.168.1.23" into "192.168.1.255". Returns null when the
        /// address is not a plausible private IPv4 literal, so we never invent
        /// a target from an unexpected host-name form.
        /// </summary>
        public static string ToDirectedBroadcast(string ipv4)
        {
            if (string.IsNullOrEmpty(ipv4))
            {
                return null;
            }

            string[] parts = ipv4.Split('.');
            if (parts.Length != 4)
            {
                return null;
            }
            for (int i = 0; i < 4; i++)
            {
                int octet;
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out octet) ||
                    octet < 0 || octet > 255)
                {
                    return null;
                }
            }

            // /24 is the home-network assumption; loopback is not a target.
            if (parts[0] == "127")
            {
                return null;
            }
            return parts[0] + "." + parts[1] + "." + parts[2] + ".255";
        }

        private static async Task SendOnceAsync(
            string target,
            byte[] payload,
            int port,
            CancellationToken cancellationToken)
        {
            using (DatagramSocket socket = new DatagramSocket())
            {
                IBuffer buffer = payload.AsBuffer();
                HostName host = new HostName(target);
                string service = port.ToString(CultureInfo.InvariantCulture);

                // Broadcast and multicast are sent through an IOutputStream
                // obtained from GetOutputStreamAsync (the documented path);
                // ConnectAsync would restrict the socket to one remote endpoint.
                using (IOutputStream output =
                    await socket.GetOutputStreamAsync(host, service).AsTask(cancellationToken))
                {
                    await output.WriteAsync(buffer).AsTask(cancellationToken);
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

            DatagramSocket socket = _receiver;
            _receiver = null;
            if (socket == null)
            {
                return;
            }

            try
            {
                socket.MessageReceived -= OnMessageReceived;
            }
            catch (Exception)
            {
            }
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
