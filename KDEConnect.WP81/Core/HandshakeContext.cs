using System;
using System.Collections.Generic;
using System.Text;

namespace KDEConnect.WP81.Core
{
    public enum HandshakeDecision
    {
        Accept,
        Reject
    }

    public sealed class HandshakeContext
    {
        public HandshakeContext()
        {
            Failures = new List<string>();
        }

        /// <summary>Identity packet received in plaintext, before TLS.</summary>
        public NetworkPacket PlaintextIdentity { get; set; }

        /// <summary>Identity packet received inside TLS.</summary>
        public NetworkPacket SecureIdentity { get; set; }

        /// <summary>CN extracted from the peer certificate presented in the handshake.</summary>
        public string PeerCertificateCommonName { get; set; }

        /// <summary>True when the peer certificate is inside its validity window.</summary>
        public bool PeerCertificateValidInTime { get; set; }

        /// <summary>DER of the pinned certificate for this device, or null when unpaired.</summary>
        public byte[] PinnedCertificateDer { get; set; }

        /// <summary>DER of the certificate actually presented by the peer.</summary>
        public byte[] PresentedCertificateDer { get; set; }

        /// <summary>True when the peer already trusts us / we already trust the peer.</summary>
        public bool IsTrustedPeer { get; set; }

        public List<string> Failures { get; private set; }

        private void Fail(string reason)
        {
            Failures.Add(reason);
        }

        /// <summary>
        /// Applies every check the reference implementations perform between the
        /// plaintext connection and the establishment of a link, plus this
        /// client's stricter trust policy (docs/protocol.md §4.1, §5).
        /// </summary>
        public HandshakeDecision Evaluate()
        {
            // The acceptor receives a plaintext connection packet from the
            // dialer; the dialer has no such packet when it reached us by manual
            // IP rather than by a discovery broadcast. Plaintext consistency is
            // therefore checked only when we actually have one - the certificate
            // CN, validity and pinning checks below always run either way.
            string plaintextDeviceId = null;
            int plaintextProtocolVersion = 0;
            if (PlaintextIdentity != null)
            {
                if (!DeviceInfo.IsValidConnectionPacket(PlaintextIdentity) &&
                    !DeviceInfo.IsValidIdentityPacket(PlaintextIdentity))
                {
                    Fail("invalid plaintext identity packet");
                    return HandshakeDecision.Reject;
                }

                plaintextDeviceId = PlaintextIdentity.GetString("deviceId", string.Empty);
                plaintextProtocolVersion = PlaintextIdentity.GetInt("protocolVersion", 0);
                if (plaintextProtocolVersion < ProtocolConstants.ProtocolVersion)
                {
                    Fail("protocol version " + plaintextProtocolVersion + " is below " + ProtocolConstants.ProtocolVersion);
                    return HandshakeDecision.Reject;
                }
            }

            if (SecureIdentity == null)
            {
                Fail("no secure identity");
                return HandshakeDecision.Reject;
            }
            if (!DeviceInfo.IsValidIdentityPacket(SecureIdentity))
            {
                Fail("invalid secure identity packet");
                return HandshakeDecision.Reject;
            }

            // lanlinkprovider.cpp:425-436 - only meaningful when a plaintext
            // counterpart exists to compare against.
            string secureDeviceId = SecureIdentity.GetString("deviceId", string.Empty);
            if (plaintextDeviceId != null)
            {
                int secureProtocolVersion = SecureIdentity.GetInt("protocolVersion", -1);
                if (secureProtocolVersion != plaintextProtocolVersion)
                {
                    Fail("protocol version changed during handshake (" +
                         plaintextProtocolVersion + " -> " + secureProtocolVersion + ")");
                    return HandshakeDecision.Reject;
                }
                if (!string.Equals(secureDeviceId, plaintextDeviceId, StringComparison.Ordinal))
                {
                    Fail("device id changed during handshake");
                    return HandshakeDecision.Reject;
                }
            }

            // lanlinkprovider.cpp:691-699 - the certificate must be bound to the id.
            if (string.IsNullOrEmpty(PeerCertificateCommonName))
            {
                Fail("peer did not present a certificate");
                return HandshakeDecision.Reject;
            }
            if (!string.Equals(PeerCertificateCommonName, secureDeviceId, StringComparison.Ordinal))
            {
                Fail("certificate CN does not match device id");
                return HandshakeDecision.Reject;
            }
            if (!PeerCertificateValidInTime)
            {
                Fail("peer certificate is outside its validity window");
                return HandshakeDecision.Reject;
            }

            // Pinning: an already-paired peer must present the exact certificate
            // we stored; anything else is a changed certificate and is refused
            // (lanlinkprovider.cpp:709-714, LanLinkProvider.kt:415-419).
            if (PinnedCertificateDer != null)
            {
                if (PresentedCertificateDer == null)
                {
                    Fail("paired peer presented no certificate");
                    return HandshakeDecision.Reject;
                }
                if (!BytesEqual(PinnedCertificateDer, PresentedCertificateDer))
                {
                    Fail("peer certificate changed since pairing");
                    return HandshakeDecision.Reject;
                }
            }

            if (IsTrustedPeer && PinnedCertificateDer == null)
            {
                Fail("peer marked trusted but no certificate is pinned");
                return HandshakeDecision.Reject;
            }

            return HandshakeDecision.Accept;
        }

        public string DescribeFailures()
        {
            if (Failures.Count == 0)
            {
                return string.Empty;
            }
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Failures.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append("; ");
                }
                sb.Append(Failures[i]);
            }
            return sb.ToString();
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }
            return true;
        }
    }
}
