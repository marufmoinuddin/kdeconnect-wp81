using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto.Tls;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using KDEConnect.WP81.Core;
using KDEConnect.WP81.Transport;

namespace KDEConnect.WP81.Security
{
    /// <summary>
    /// In-process TLS built on Bouncy Castle 1.8.2.
    ///
    /// This backend exists because Windows Phone 8.1 WinRT cannot: act as a TLS
    /// server, present a client certificate, or pin a self-signed peer
    /// certificate (docs/tls-backend.md §2). Without it, direct interoperability
    /// with stock KDE Connect is impossible in either TCP direction.
    ///
    /// Role reminder (docs/protocol.md §4): the side that DIALS the TCP
    /// connection is the TLS *server*, the side that ACCEPTS is the TLS *client*.
    /// So HandshakeAsServerAsync is used on outbound connections and
    /// HandshakeAsClientAsync on inbound ones.
    /// </summary>
    public sealed class BouncyCastleTlsBackend : ITlsBackend
    {
        private static readonly int[] CipherSuites = new int[]
        {
            // IANA values (RFC 5246 / RFC 5288). Kept explicit rather than via
            // named constants so the offered set is visible in review.
            0x009C, // TLS_RSA_WITH_AES_128_GCM_SHA256
            0x009D, // TLS_RSA_WITH_AES_256_GCM_SHA384
            0x002F, // TLS_RSA_WITH_AES_128_CBC_SHA
            0x0035, // TLS_RSA_WITH_AES_256_CBC_SHA
            0x003C, // TLS_RSA_WITH_AES_128_CBC_SHA256
            0x003D, // TLS_RSA_WITH_AES_256_CBC_SHA256
            0x000A, // TLS_RSA_WITH_3DES_EDE_CBC_SHA
            0x0005  // TLS_RSA_WITH_RC4_128_SHA
        };

        public string Name { get { return "bouncycastle-1.8.2"; } }

        public bool SupportsServerRole { get { return true; } }

        public bool SupportsClientCertificates { get { return true; } }

        public Task<ITlsSession> HandshakeAsServerAsync(
            RawChannel channel,
            TlsIdentity local,
            TlsPeerPolicy peer,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            return RunAsync(channel, timeout, cancellationToken, delegate
            {
                ServerHandler handler = new ServerHandler(local, peer);
                TlsServerProtocol protocol = new TlsServerProtocol(channel.Stream, new SecureRandom());
                try
                {
                    protocol.Accept(handler);
                }
                catch (Exception ex)
                {
                    throw new IOException("TLS server handshake failed at stage '" + handler.Stage + "': " + ex.Message, ex);
                }
                return new Session(protocol, handler.Collect(), channel);
            });
        }

        public Task<ITlsSession> HandshakeAsClientAsync(
            RawChannel channel,
            TlsIdentity local,
            TlsPeerPolicy peer,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            return RunAsync(channel, timeout, cancellationToken, delegate
            {
                ClientHandler handler = new ClientHandler(local, peer);
                TlsClientProtocol protocol = new TlsClientProtocol(channel.Stream, new SecureRandom());
                try
                {
                    protocol.Connect(handler);
                }
                catch (Exception ex)
                {
                    throw new IOException("TLS client handshake failed at stage '" + handler.Stage + "': " + ex.Message, ex);
                }
                return new Session(protocol, handler.Collect(), channel);
            });
        }

        /// <summary>
        /// Bouncy Castle's handshake is blocking, so it runs on the thread pool;
        /// a timeout or cancellation tears the channel down, which is what
        /// unblocks a handshake stuck mid-flight.
        /// </summary>
        private static async Task<ITlsSession> RunAsync(
            RawChannel channel,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            Func<ITlsSession> handshake)
        {
            if (channel == null)
            {
                throw new ArgumentNullException("channel");
            }

            Task<ITlsSession> work = Task.Run(handshake, cancellationToken);
            Task delay = Task.Delay(timeout, cancellationToken);
            Task first = await Task.WhenAny(work, delay).ConfigureAwait(false);

            if (first != work)
            {
                try
                {
                    channel.Dispose();
                }
                catch (Exception)
                {
                }
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("TLS handshake did not complete within " +
                                            timeout.TotalMilliseconds.ToString("F0") + " ms");
            }

            return await work.ConfigureAwait(false);
        }

        // ---------------------------------------------------------------- //
        //  Collects what the handshake learned about the peer.
        // ---------------------------------------------------------------- //

        private sealed class PeerEvidence
        {
            public byte[] CertificateDer;
            public string CommonName;
            public bool ValidInTime;
            public string CertificateError;
        }

        private static void Capture(PeerEvidence evidence, Org.BouncyCastle.Crypto.Tls.Certificate certificate)
        {
            if (certificate == null || certificate.Length == 0)
            {
                evidence.CertificateError = "peer presented no certificate";
                return;
            }

            try
            {
                X509CertificateStructure structure = certificate.GetCertificateAt(0);
                byte[] der = structure.GetEncoded();
                X509Certificate parsed = new X509CertificateParser().ReadCertificate(der);

                evidence.CertificateDer = der;
                evidence.CommonName = CertificateProvider.GetCommonName(parsed);
                evidence.ValidInTime = CertificateProvider.IsValidAt(parsed, DateTime.UtcNow);
                if (!evidence.ValidInTime)
                {
                    evidence.CertificateError = "peer certificate is outside its validity window";
                }
            }
            catch (Exception ex)
            {
                evidence.CertificateError = "could not parse peer certificate: " + ex.Message;
            }
        }

        private static TlsHandshakeResult ToResult(PeerEvidence evidence)
        {
            TlsHandshakeResult result = new TlsHandshakeResult();
            result.PeerCertificateDer = evidence.CertificateDer;
            result.PeerCertificateCommonName = evidence.CommonName == null ? string.Empty : evidence.CommonName;
            result.PeerCertificateValidInTime = evidence.ValidInTime;
            result.CipherSuite = string.Empty;
            result.ProtocolVersion = string.Empty;
            return result;
        }

        // ---------------------------------------------------------------- //
        //  TLS server role - used on connections WE dialled.
        // ---------------------------------------------------------------- //

        private sealed class ServerHandler : AbstractTlsServer
        {
            private readonly TlsIdentity _local;
            private readonly TlsPeerPolicy _peer;
            private readonly PeerEvidence _evidence = new PeerEvidence();
            private TlsServerContext _context;

            /// <summary>Which BC callback we last entered; surfaced on failure.</summary>
            public string Stage = "created";

            public ServerHandler(TlsIdentity local, TlsPeerPolicy peer)
            {
                _local = local;
                _peer = peer;
            }

            public override void Init(TlsServerContext context)
            {
                base.Init(context);
                _context = context;
            }

            /// <summary>
            /// Bouncy Castle's AbstractTlsServer caps at TLS 1.1 by default.
            /// KDE Connect/Android pins TLS 1.2 (SslHelper.kt:165) and Qt offers
            /// it, so the ceiling is raised here. Without this the negotiated
            /// version is &lt; 1.2 and BC rejects a CertificateRequest that
            /// carries signature algorithms
            /// (TlsServerProtocol.cs:202, IsTlsV12 != sigAlgs != null).
            /// </summary>
            protected override ProtocolVersion MaximumVersion
            {
                get { return ProtocolVersion.TLSv12; }
            }

            public override ProtocolVersion GetServerVersion()
            {
                Stage = "GetServerVersion";
                return base.GetServerVersion();
            }

            public override int GetSelectedCipherSuite()
            {
                Stage = "GetSelectedCipherSuite";
                return base.GetSelectedCipherSuite();
            }

            protected override int[] GetCipherSuites()
            {
                return CipherSuites;
            }

            public override TlsCredentials GetCredentials()
            {
                Stage = "GetCredentials";
                if (_context == null)
                {
                    throw new InvalidOperationException("TLS server context not initialised");
                }
                Org.BouncyCastle.Crypto.Tls.Certificate chain = new Org.BouncyCastle.Crypto.Tls.Certificate(
                    new X509CertificateStructure[] { CertificateStructureOf(_local.CertificateDer) });
                // RSA key transport (TlsRsaKeyExchange.ProcessServerCredentials)
                // requires TlsEncryptionCredentials; handing it a signer instead
                // makes BC raise internal_error(80) - verified against
                // pcl-v1.8.2 TlsRsaKeyExchange.cs:37-45.
                return new DefaultTlsEncryptionCredentials(
                    _context, chain,
                    CertificateProvider.LoadPrivateKey(_local.PrivateKeyDer));
            }

            public override TlsKeyExchange GetKeyExchange()
            {
                Stage = "GetKeyExchange";
                return new TlsRsaKeyExchange(SignatureAlgorithms());
            }

            public override CertificateRequest GetCertificateRequest()
            {
                Stage = "GetCertificateRequest";
                // KDE Connect requires the peer's certificate: the desktop reads
                // peerCertificate() and compares CN with deviceId
                // (lanlinkprovider.cpp:691-699). Mirrors needClientAuth /
                // wantClientAuth (SslHelper.kt:209-221).
                //
                // BC requires SupportedSignatureAlgorithms to be present exactly
                // when TLS 1.2 is negotiated (TlsServerProtocol.cs:202), so the
                // list is supplied conditionally rather than unconditionally.
                bool isTls12 = mContext != null && TlsUtilities.IsTlsV12(mContext);
                return new CertificateRequest(
                    new byte[] { ClientCertificateType.rsa_sign },
                    isTls12 ? SignatureAlgorithms() : null,
                    null);
            }

            public override void NotifyClientCertificate(Org.BouncyCastle.Crypto.Tls.Certificate certificate)
            {
                Capture(_evidence, certificate);
            }

            public TlsHandshakeResult Collect()
            {
                return ToResult(_evidence);
            }
        }

        // ---------------------------------------------------------------- //
        //  TLS client role - used on connections the PEER dialled to us.
        // ---------------------------------------------------------------- //

        private sealed class ClientHandler : AbstractTlsClient
        {
            private readonly TlsIdentity _local;
            private readonly TlsPeerPolicy _peer;
            private readonly PeerEvidence _evidence = new PeerEvidence();
            private TlsClientContext _context;

            /// <summary>Which BC callback we last entered; surfaced on failure.</summary>
            public string Stage = "created";

            public ClientHandler(TlsIdentity local, TlsPeerPolicy peer)
            {
                _local = local;
                _peer = peer;
            }

            public override void Init(TlsClientContext context)
            {
                base.Init(context);
                _context = context;
            }

            public override int[] GetCipherSuites()
            {
                Stage = "GetCipherSuites";
                return CipherSuites;
            }

            public override TlsKeyExchange GetKeyExchange()
            {
                Stage = "GetKeyExchange";
                return new TlsRsaKeyExchange(SignatureAlgorithms());
            }

            public override TlsAuthentication GetAuthentication()
            {
                Stage = "GetAuthentication";
                // Called after Init(), so the client context is available here.
                return new ClientAuthentication(_local, _peer, _evidence, _context);
            }

            public TlsHandshakeResult Collect()
            {
                return ToResult(_evidence);
            }
        }

        /// <summary>
        /// Supplies OUR certificate to the peer (the piece WinRT cannot do) and
        /// captures the peer's server certificate for validation and pinning.
        /// </summary>
        private sealed class ClientAuthentication : TlsAuthentication
        {
            private readonly TlsIdentity _local;
            private readonly TlsPeerPolicy _peer;
            private readonly PeerEvidence _evidence;
            private readonly TlsClientContext _context;

            public ClientAuthentication(TlsIdentity local, TlsPeerPolicy peer, PeerEvidence evidence, TlsClientContext context)
            {
                _local = local;
                _peer = peer;
                _evidence = evidence;
                _context = context;
            }

            public void NotifyServerCertificate(Org.BouncyCastle.Crypto.Tls.Certificate certificate)
            {
                Capture(_evidence, certificate);
            }

            public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest)
            {
                if (certificateRequest == null)
                {
                    return null;
                }
                Org.BouncyCastle.Crypto.Tls.Certificate chain = new Org.BouncyCastle.Crypto.Tls.Certificate(
                    new X509CertificateStructure[] { CertificateStructureOf(_local.CertificateDer) });

                if (_context == null)
                {
                    return null;
                }
                // The client's own credentials feed CertificateVerify, which is a
                // signature, so the signer flavour is correct here - and TLS 1.2+
                // requires the algorithm to be stated (DefaultTlsSignerCredentials.cs:36).
                return new DefaultTlsSignerCredentials(
                    _context, chain,
                    CertificateProvider.LoadPrivateKey(_local.PrivateKeyDer),
                    ClientSignatureAlgorithm());
            }
        }

        // ---------------------------------------------------------------- //
        //  Shared helpers
        // ---------------------------------------------------------------- //

        /// <summary>
        /// The algorithm used for our own handshake signatures. sha256+rsa is
        /// offered by every peer we target (Qt/OpenSSL and BC both list it).
        /// </summary>
        private static SignatureAndHashAlgorithm ClientSignatureAlgorithm()
        {
            return new SignatureAndHashAlgorithm(HashAlgorithm.sha256, SignatureAlgorithm.rsa);
        }

        private static IList SignatureAlgorithms()
        {
            ArrayList list = new ArrayList();
            list.Add(new SignatureAndHashAlgorithm(HashAlgorithm.sha512, SignatureAlgorithm.rsa));
            list.Add(new SignatureAndHashAlgorithm(HashAlgorithm.sha384, SignatureAlgorithm.rsa));
            list.Add(new SignatureAndHashAlgorithm(HashAlgorithm.sha256, SignatureAlgorithm.rsa));
            list.Add(new SignatureAndHashAlgorithm(HashAlgorithm.sha1, SignatureAlgorithm.rsa));
            return list;
        }

        private static X509CertificateStructure CertificateStructureOf(byte[] certificateDer)
        {
            return new X509CertificateParser().ReadCertificate(certificateDer).CertificateStructure;
        }

        private sealed class Session : ITlsSession
        {
            private readonly TlsProtocol _protocol;
            private readonly RawChannel _channel;
            private bool _closed;

            public Session(TlsProtocol protocol, TlsHandshakeResult result, RawChannel channel)
            {
                _protocol = protocol;
                Result = result;
                _channel = channel;
                ApplicationData = protocol.Stream;
            }

            public Stream ApplicationData { get; private set; }

            public TlsHandshakeResult Result { get; private set; }

            public void Close()
            {
                if (_closed)
                {
                    return;
                }
                _closed = true;
                try
                {
                    _protocol.Close();
                }
                catch (Exception)
                {
                }
                Dispose();
            }

            public void Dispose()
            {
                if (_closed)
                {
                    return;
                }
                _closed = true;
                try
                {
                    _protocol.Close();
                }
                catch (Exception)
                {
                }
                if (_channel != null)
                {
                    _channel.Dispose();
                }
            }
        }
    }
}
