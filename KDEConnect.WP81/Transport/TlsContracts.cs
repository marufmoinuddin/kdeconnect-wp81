using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace KDEConnect.WP81.Transport
{
    /// <summary>
    /// A raw (not yet TLS-upgraded) TCP connection, in either direction.
    ///
    /// IMPORTANT - ROLE INVERSION:
    /// In KDE Connect the side that DIALS the TCP connection acts as the TLS
    /// *server*, and the side that ACCEPTS acts as the TLS *client*
    /// (docs/protocol.md §4, lanlinkprovider.cpp:379 and :563,
    /// LanLinkProvider.kt:330-331). So:
    ///
    ///   we dialled  -> HandshakeAsServerAsync
    ///   we accepted -> HandshakeAsClientAsync
    ///
    /// The names below follow the TLS role, not the TCP role, on purpose.
    /// </summary>
    public sealed class RawChannel : IDisposable
    {
        private bool _disposed;

        public RawChannel(Stream stream, object platformSocket, string remoteAddress, int remotePort)
        {
            if (stream == null)
            {
                throw new ArgumentNullException("stream");
            }
            Stream = stream;
            PlatformSocket = platformSocket;
            RemoteAddress = remoteAddress;
            RemotePort = remotePort;
        }

        /// <summary>Raw TCP byte stream. Usable by any in-process TLS stack.</summary>
        public Stream Stream { get; private set; }

        /// <summary>
        /// The platform object behind the stream (a StreamSocket on Windows
        /// Phone). Backends that need the socket itself - for example the WinRT
        /// backend, which upgrades the socket rather than wrapping the stream -
        /// read this; everyone else ignores it.
        /// </summary>
        public object PlatformSocket { get; private set; }

        public string RemoteAddress { get; private set; }
        public int RemotePort { get; private set; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try
            {
                Stream.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException)
            {
            }

            IDisposable disposableSocket = PlatformSocket as IDisposable;
            if (disposableSocket != null)
            {
                try
                {
                    disposableSocket.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }
    }

    /// <summary>
    /// Our own TLS identity: the self-signed certificate with CN = deviceId and
    /// its private key, both DER. See docs/protocol.md §5.
    /// </summary>
    public sealed class TlsIdentity
    {
        public TlsIdentity()
        {
        }

        public TlsIdentity(string deviceId, byte[] certificateDer, byte[] privateKeyDer)
        {
            DeviceId = deviceId;
            CertificateDer = certificateDer;
            PrivateKeyDer = privateKeyDer;
        }

        public byte[] CertificateDer { get; set; }
        public byte[] PrivateKeyDer { get; set; }
        public string DeviceId { get; set; }

        /// <summary>
        /// Extracts the public key DER from the certificate. Used for pairing
        /// verification code computation (PairingHandler.ComputeVerificationCode).
        /// </summary>
        public byte[] PublicKeyDer
        {
            get
            {
                if (CertificateDer == null || CertificateDer.Length == 0)
                    return null;
                try
                {
                    var parser = new Org.BouncyCastle.X509.X509CertificateParser();
                    var cert = parser.ReadCertificate(CertificateDer);
                    var pubKey = cert.GetPublicKey();
                    var spki = Org.BouncyCastle.X509.SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(pubKey);
                    return spki.GetDerEncoded();
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }
    }

    /// <summary>
    /// What we require from the peer during the handshake.
    /// </summary>
    public sealed class TlsPeerPolicy
    {
        public TlsPeerPolicy()
        {
            // Defaults describe the strictest useful state: no pin, no trust.
            RequirePeerCertificate = true;
        }

        /// <summary>The device id we expect, taken from the plaintext handshake.</summary>
        public string ExpectedDeviceId { get; set; }

        /// <summary>
        /// DER of the certificate we pinned when pairing, or null when the peer
        /// is not paired yet (trust on first use, pending operator approval).
        /// </summary>
        public byte[] PinnedCertificateDer { get; set; }

        /// <summary>
        /// Optional resolver used when <see cref="PinnedCertificateDer"/> is
        /// null: given the peer device id, return the stored pin or null. This
        /// exists because an inbound connection only reveals the peer id after
        /// the plaintext handshake, so the pin cannot be looked up in advance.
        /// Resolving it here means inbound links are pinned exactly like
        /// outbound ones instead of silently falling back to trust-on-first-use.
        /// </summary>
        public Func<string, byte[]> PinLookup { get; set; }

        /// <summary>
        /// True when a peer certificate must be presented at all. The reference
        /// requires one in both directions (lanlinkprovider.cpp:691-699), so
        /// this stays true; it is only ever relaxed for diagnostics.
        /// </summary>
        public bool RequirePeerCertificate { get; set; }

        /// <summary>
        /// Server role only: ask the client for a certificate. Mirrors
        /// needClientAuth / wantClientAuth (SslHelper.kt:209-221).
        /// </summary>
        public bool RequestClientCertificate { get; set; }
    }

    /// <summary>Outcome of a completed TLS handshake.</summary>
    public sealed class TlsHandshakeResult
    {
        public byte[] PeerCertificateDer { get; set; }
        public string PeerCertificateCommonName { get; set; }
        public bool PeerCertificateValidInTime { get; set; }
        public string CipherSuite { get; set; }
        public string ProtocolVersion { get; set; }
    }

    /// <summary>
    /// A completed TLS handshake: the encrypted application-data stream plus
    /// what we learned about the peer.
    /// </summary>
    public interface ITlsSession : IDisposable
    {
        /// <summary>Encrypted application data. Framed packets flow over this.</summary>
        Stream ApplicationData { get; }

        TlsHandshakeResult Result { get; }

        /// <summary>Orderly TLS close_notify if possible, then release the channel.</summary>
        void Close();
    }

    /// <summary>
    /// A TLS implementation. At least one backend must be able to perform BOTH
    /// roles, otherwise direct interoperability with stock KDE Connect is
    /// impossible (docs/protocol.md §4.2).
    /// </summary>
    public interface ITlsBackend
    {
        /// <summary>Short name for diagnostics, e.g. "winrt-streamsocket".</summary>
        string Name { get; }

        /// <summary>True when this backend can act as TLS server on an accepted socket.</summary>
        bool SupportsServerRole { get; }

        /// <summary>True when this backend can present our own certificate to the peer.</summary>
        bool SupportsClientCertificates { get; }

        /// <summary>
        /// TLS handshake on a connection WE dialled. Despite the TCP direction,
        /// this side is the TLS *server* in the KDE Connect protocol.
        /// </summary>
        Task<ITlsSession> HandshakeAsServerAsync(
            RawChannel channel,
            TlsIdentity local,
            TlsPeerPolicy peer,
            TimeSpan timeout,
            CancellationToken cancellationToken);

        /// <summary>
        /// TLS handshake on a connection the PEER dialled to us. Despite the TCP
        /// direction, this side is the TLS *client* in the KDE Connect protocol.
        /// </summary>
        Task<ITlsSession> HandshakeAsClientAsync(
            RawChannel channel,
            TlsIdentity local,
            TlsPeerPolicy peer,
            TimeSpan timeout,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Raised when a backend cannot perform the requested role. Carries the
    /// exact capability gap so the UI and the validation matrix can report it
    /// instead of failing with an opaque error.
    /// </summary>
    public sealed class TlsCapabilityException : Exception
    {
        public TlsCapabilityException(string backend, string role, string detail)
            : base("TLS backend '" + backend + "' cannot perform the '" + role + "' role: " + detail)
        {
            Backend = backend;
            Role = role;
        }

        public string Backend { get; private set; }
        public string Role { get; private set; }
    }
}
