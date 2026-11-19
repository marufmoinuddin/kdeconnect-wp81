using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KDEConnect.WP81.Core
{
    public enum PairState
    {
        NotPaired = 0,
        Requested = 1,        // we asked
        RequestedByPeer = 2,  // they asked, waiting for our answer
        Paired = 3
    }

    public enum PairingEvent
    {
        None,
        IncomingRequest,
        OutgoingRequestSent,
        Accepted,
        Rejected,
        TimedOut,
        CancelledByUs,
        CancelledByPeer,
        Unpaired,
        FailedClockOutOfSync,
        FailedIgnored
    }

    /// <summary>
    /// Pure pairing state machine, a direct port of
    /// kdeconnect-kde core/backends/pairinghandler.cpp so that the logic can be
    /// unit-tested on the host without any socket or UI.
    ///
    /// No timers live here: the owner starts/stops the timeout and calls
    /// <see cref="OnTimeout"/>.
    /// </summary>
    public sealed class PairingHandler
    {
        public const int TimeoutMs = ProtocolConstants.PairingTimeoutMs;
        public const int AllowedTimestampSkewSeconds = ProtocolConstants.PairingTimestampSkewSeconds;

        private long _pairingTimestamp;

        public PairingHandler()
        {
            State = PairState.NotPaired;
        }

        public PairState State { get; private set; }

        public long PairingTimestamp { get { return _pairingTimestamp; } }

        public bool IsPaired { get { return State == PairState.Paired; } }

        /// <summary>True while a pairing attempt is in flight in either direction.</summary>
        public bool IsBusy
        {
            get { return State == PairState.Requested || State == PairState.RequestedByPeer; }
        }

        /// <summary>
        /// Builds the request packet (with timestamp) or the accept/reject
        /// packet, mirroring PairingHandler::requestPairing/acceptPairing/
        /// cancelPairing/unpair.
        /// </summary>
        public static NetworkPacket BuildRequestPacket(long timestampSeconds)
        {
            NetworkPacket np = new NetworkPacket(ProtocolConstants.PacketTypePair);
            np.SetBool("pair", true);
            np.SetLong("timestamp", timestampSeconds);
            return np;
        }

        public static NetworkPacket BuildAcceptPacket()
        {
            NetworkPacket np = new NetworkPacket(ProtocolConstants.PacketTypePair);
            np.SetBool("pair", true);
            return np;
        }

        public static NetworkPacket BuildRejectPacket()
        {
            NetworkPacket np = new NetworkPacket(ProtocolConstants.PacketTypePair);
            np.SetBool("pair", false);
            return np;
        }

        // ---- outgoing -------------------------------------------------------

        /// <summary>
        /// We initiate pairing. Returns the packet to send, or null when the
        /// attempt is not allowed (already paired, or the other side already
        /// asked, in which case the caller should send an accept instead).
        /// </summary>
        public NetworkPacket RequestPairing(long nowSeconds, out PairingEvent outcome)
        {
            if (State == PairState.Paired)
            {
                outcome = PairingEvent.FailedIgnored;
                return null;
            }
            if (State == PairState.RequestedByPeer)
            {
                // Reference behaviour: accept their request instead (pairinghandler.cpp:94-97).
                return AcceptPairing(out outcome);
            }

            State = PairState.Requested;
            _pairingTimestamp = nowSeconds;
            outcome = PairingEvent.OutgoingRequestSent;
            return BuildRequestPacket(nowSeconds);
        }

        public NetworkPacket AcceptPairing(out PairingEvent outcome)
        {
            if (State == PairState.Paired)
            {
                outcome = PairingEvent.FailedIgnored;
                return null;
            }
            if (State != PairState.RequestedByPeer)
            {
                outcome = PairingEvent.FailedIgnored;
                return null;
            }
            State = PairState.Paired;
            outcome = PairingEvent.Accepted;
            return BuildAcceptPacket();
        }

        public NetworkPacket CancelPairing(out PairingEvent outcome)
        {
            if (State != PairState.Requested && State != PairState.RequestedByPeer)
            {
                outcome = PairingEvent.FailedIgnored;
                return null;
            }
            State = PairState.NotPaired;
            outcome = PairingEvent.CancelledByUs;
            return BuildRejectPacket();
        }

        public NetworkPacket Unpair(out PairingEvent outcome)
        {
            bool wasPaired = State == PairState.Paired;
            State = PairState.NotPaired;
            outcome = wasPaired ? PairingEvent.Unpaired : PairingEvent.None;
            return BuildRejectPacket();
        }

        // ---- incoming -------------------------------------------------------

        /// <summary>
        /// Applies an incoming kdeconnect.pair packet. Returns the packet to
        /// send back (accept/reject) or null, and reports what happened.
        /// </summary>
        public NetworkPacket OnPacketReceived(NetworkPacket np, long nowSeconds, out PairingEvent outcome)
        {
            if (np == null || np.Type != ProtocolConstants.PacketTypePair)
            {
                outcome = PairingEvent.FailedIgnored;
                return null;
            }

            bool wantsPair = np.GetBool("pair", false);
            if (wantsPair)
            {
                switch (State)
                {
                    case PairState.Requested:
                        // Their accept answers our request.
                        State = PairState.Paired;
                        outcome = PairingEvent.Accepted;
                        return null;

                    case PairState.RequestedByPeer:
                        // A second request before the first one timed out is ignored.
                        outcome = PairingEvent.FailedIgnored;
                        return null;

                    case PairState.Paired:
                        // Pairing requests carry a timestamp, accepts do not.
                        if (!np.Has("timestamp"))
                        {
                            outcome = PairingEvent.FailedIgnored;
                            return null;
                        }
                        State = PairState.NotPaired;
                        outcome = PairingEvent.Unpaired;
                        goto case PairState.NotPaired;

                    case PairState.NotPaired:
                    default:
                        long timestamp = np.GetLong("timestamp", -1L);
                        if (timestamp == -1L)
                        {
                            outcome = PairingEvent.FailedIgnored;
                            return null;
                        }
                        long skew = Math.Abs(nowSeconds - timestamp);
                        if (skew > AllowedTimestampSkewSeconds)
                        {
                            outcome = PairingEvent.FailedClockOutOfSync;
                            return BuildRejectPacket();
                        }
                        _pairingTimestamp = timestamp;
                        State = PairState.RequestedByPeer;
                        outcome = PairingEvent.IncomingRequest;
                        return null;
                }
            }

            // pair == false
            switch (State)
            {
                case PairState.NotPaired:
                    outcome = PairingEvent.FailedIgnored;
                    return null;

                case PairState.Requested:
                case PairState.RequestedByPeer:
                    State = PairState.NotPaired;
                    outcome = PairingEvent.CancelledByPeer;
                    return null;

                default: // Paired
                    State = PairState.NotPaired;
                    outcome = PairingEvent.Unpaired;
                    return null;
            }
        }

        /// <summary>Called when the pairing timer expires (pairinghandler.cpp:173-179).</summary>
        public NetworkPacket OnTimeout(out PairingEvent outcome)
        {
            if (State != PairState.Requested && State != PairState.RequestedByPeer)
            {
                outcome = PairingEvent.FailedIgnored;
                return null;
            }
            State = PairState.NotPaired;
            outcome = PairingEvent.TimedOut;
            return BuildRejectPacket();
        }

        /// <summary>Forgets all local pairing state without sending anything.</summary>
        public void Reset()
        {
            State = PairState.NotPaired;
            _pairingTimestamp = 0;
        }

        // ---- verification code ---------------------------------------------

        /// <summary>
        /// The8-hex-digit code both ends display for manual comparison.
        /// Port of PairingHandler::verificationKey (pairinghandler.cpp:188-209):
        ///   a = own public key DER, b = peer public key DER,
        ///   order them so a >= b, then SHA256(a || b || str(timestamp)).
        /// </summary>
        public static string ComputeVerificationCode(
            byte[] ownPublicKeyDer,
            byte[] peerPublicKeyDer,
            long timestampSeconds)
        {
            if (ownPublicKeyDer == null || peerPublicKeyDer == null)
            {
                return string.Empty;
            }

            byte[] a = ownPublicKeyDer;
            byte[] b = peerPublicKeyDer;
            if (CompareUnsigned(a, b) < 0)
            {
                byte[] tmp = a;
                a = b;
                b = tmp;
            }

            byte[] timestampBytes = Encoding.ASCII.GetBytes(
                timestampSeconds.ToString(CultureInfo.InvariantCulture));

            byte[] buffer = new byte[a.Length + b.Length + timestampBytes.Length];
            Buffer.BlockCopy(a, 0, buffer, 0, a.Length);
            Buffer.BlockCopy(b, 0, buffer, a.Length, b.Length);
            Buffer.BlockCopy(timestampBytes, 0, buffer, a.Length + b.Length, timestampBytes.Length);

            byte[] hash;
            using (SHA256 sha = SHA256.Create())
            {
                hash = sha.ComputeHash(buffer);
            }

            StringBuilder sb = new StringBuilder(8);
            for (int i = 0; i < 4; i++)
            {
                sb.Append(hash[i].ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>Unsigned lexicographic comparison of two byte arrays.</summary>
        private static int CompareUnsigned(byte[] x, byte[] y)
        {
            int len = Math.Min(x.Length, y.Length);
            for (int i = 0; i < len; i++)
            {
                if (x[i] != y[i])
                {
                    return x[i] < y[i] ? -1 : 1;
                }
            }
            return x.Length.CompareTo(y.Length);
        }
    }
}
