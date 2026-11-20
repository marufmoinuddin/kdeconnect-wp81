using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KDEConnect.WP81.Core;

namespace KDEConnect.WP81.Transport
{
    /// <summary>
    /// Outcome of a link establishment attempt.
    /// </summary>
    public sealed class LinkEstablishResult
    {
        public LinkEstablishResult()
        {
            Success = false;
        }

        public bool Success { get; set; }

        /// <summary>Why it failed; null on success.</summary>
        public string Failure { get; set; }

        /// <summary>The established session, kept open for the packet loop.</summary>
        public ITlsSession Session { get; set; }

        /// <summary>Reader positioned on the session's application stream.</summary>
        public BufferedLineReader Reader { get; set; }

        /// <summary>Peer identity from the secure identity packet.</summary>
        public DeviceInfo Peer { get; set; }

        /// <summary>Certificate evidence from the TLS handshake.</summary>
        public TlsHandshakeResult Tls { get; set; }

        /// <summary>The full decision record, for diagnostics.</summary>
        public HandshakeContext Context { get; set; }

        internal static LinkEstablishResult Failed(string reason)
        {
            LinkEstablishResult result = new LinkEstablishResult();
            result.Failure = reason;
            return result;
        }
    }

    /// <summary>
    /// Drives one connection from a raw channel to a validated link.
    ///
    /// The sequence is fixed by the reference (docs/protocol.md §4.1):
    ///
    ///   dialer  : send connection packet (plaintext) -> TLS *server* role
    ///   acceptor: read  connection packet (plaintext, 1 s) -> TLS *client* role
    ///   both    : send identity inside TLS -> read peer identity (1 s)
    ///   both    : validate CN, validity, pinning, plaintext/secure consistency
    ///
    /// This class is deliberately free of WinRT types so the whole sequence can
    /// be exercised on the host against a real TLS backend.
    /// </summary>
    public static class LinkEstablisher
    {
        /// <summary>We dialled the TCP connection, so we must play TLS server.</summary>
        public static async Task<LinkEstablishResult> EstablishOutboundAsync(
            RawChannel channel,
            ITlsBackend backend,
            TlsIdentity localIdentity,
            DeviceInfo localInfo,
            NetworkPacket discoveryPacket,
            TlsPeerPolicy policy,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (channel == null)
            {
                return LinkEstablishResult.Failed("no channel");
            }
            if (policy == null || string.IsNullOrEmpty(policy.ExpectedDeviceId))
            {
                return LinkEstablishResult.Failed("no expected device id for the outbound connection");
            }

            try
            {
                NetworkPacket connection = localInfo.ToConnectionPacket(
                    policy.ExpectedDeviceId, ProtocolConstants.ProtocolVersion);
                await WriteAsync(channel.Stream, connection.Serialize(), cancellationToken)
                    .ConfigureAwait(false);

                ITlsSession session = await backend.HandshakeAsServerAsync(
                    channel, localIdentity, policy, timeout, cancellationToken).ConfigureAwait(false);

                return await CompleteAsync(
                    session, localInfo, discoveryPacket, policy, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return LinkEstablishResult.Failed("cancelled");
            }
            catch (Exception ex)
            {
                return LinkEstablishResult.Failed("outbound link failed: " + ex.Message);
            }
        }

        /// <summary>The peer dialled us, so we must play TLS client.</summary>
        public static async Task<LinkEstablishResult> EstablishInboundAsync(
            RawChannel channel,
            ITlsBackend backend,
            TlsIdentity localIdentity,
            DeviceInfo localInfo,
            TlsPeerPolicy policy,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (channel == null)
            {
                return LinkEstablishResult.Failed("no channel");
            }

            try
            {
                BufferedLineReader plaintextReader =
                    new BufferedLineReader(channel.Stream, ProtocolConstants.MaxIdentityPacketSize);

                string line = await plaintextReader.ReadLineAsync(
                    TimeSpan.FromMilliseconds(ProtocolConstants.PlaintextHandshakeTimeoutMs),
                    cancellationToken).ConfigureAwait(false);

                NetworkPacket connection = NetworkPacket.Unserialize(line);
                if (!DeviceInfo.IsValidConnectionPacket(connection))
                {
                    return LinkEstablishResult.Failed("invalid connection packet");
                }

                string targetDeviceId = connection.GetString("targetDeviceId", string.Empty);
                if (!string.Equals(targetDeviceId, localInfo.Id, StringComparison.Ordinal))
                {
                    return LinkEstablishResult.Failed("connection is addressed to another device");
                }

                int targetVersion = connection.GetInt("targetProtocolVersion", -1);
                if (targetVersion != -1 && targetVersion != ProtocolConstants.ProtocolVersion)
                {
                    return LinkEstablishResult.Failed(
                        "peer requires protocol version " + targetVersion +
                        ", we speak " + ProtocolConstants.ProtocolVersion);
                }

                // If the caller did not know who was calling (manual listen),
                // adopt the id the peer claimed in plaintext; pinning still
                // applies if we already trust that id.
                if (policy.ExpectedDeviceId == null || policy.ExpectedDeviceId.Length == 0)
                {
                    policy.ExpectedDeviceId = connection.GetString("deviceId", string.Empty);
                }

                ITlsSession session = await backend.HandshakeAsClientAsync(
                    channel, localIdentity, policy, timeout, cancellationToken).ConfigureAwait(false);

                return await CompleteAsync(
                    session, localInfo, connection, policy, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return LinkEstablishResult.Failed("cancelled");
            }
            catch (Exception ex)
            {
                return LinkEstablishResult.Failed("inbound link failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Identity exchange inside TLS plus the validation policy. Shared by
        /// both directions because after the handshake they are identical.
        /// </summary>
        private static async Task<LinkEstablishResult> CompleteAsync(
            ITlsSession session,
            DeviceInfo localInfo,
            NetworkPacket plaintext,
            TlsPeerPolicy policy,
            CancellationToken cancellationToken)
        {
            BufferedLineReader reader =
                new BufferedLineReader(session.ApplicationData, ProtocolConstants.MaxPacketSize);

            await WriteAsync(session.ApplicationData, localInfo.ToIdentityPacket().Serialize(),
                cancellationToken).ConfigureAwait(false);

            string secureLine = await reader.ReadLineAsync(
                TimeSpan.FromMilliseconds(ProtocolConstants.SecureIdentityTimeoutMs),
                cancellationToken).ConfigureAwait(false);

            NetworkPacket secure = NetworkPacket.Unserialize(secureLine);
            if (!DeviceInfo.IsValidIdentityPacket(secure))
            {
                session.Close();
                return LinkEstablishResult.Failed("invalid secure identity packet");
            }

            HandshakeContext context = new HandshakeContext();
            context.PlaintextIdentity = plaintext;
            context.SecureIdentity = secure;
            context.PeerCertificateCommonName = session.Result.PeerCertificateCommonName;
            context.PeerCertificateValidInTime = session.Result.PeerCertificateValidInTime;
            context.PresentedCertificateDer = session.Result.PeerCertificateDer;
            context.PinnedCertificateDer = policy.PinnedCertificateDer;
            context.IsTrustedPeer = policy.PinnedCertificateDer != null;

            if (context.Evaluate() != HandshakeDecision.Accept)
            {
                session.Close();
                return LinkEstablishResult.Failed("rejected: " + context.DescribeFailures());
            }

            LinkEstablishResult result = new LinkEstablishResult();
            result.Success = true;
            result.Session = session;
            result.Reader = reader;
            result.Tls = session.Result;
            result.Context = context;
            result.Peer = DeviceInfo.FromIdentityPacketAndCert(
                secure, session.Result.PeerCertificateCommonName);
            return result;
        }

        private static async Task WriteAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
        {
            await stream.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
