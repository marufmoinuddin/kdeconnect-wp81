using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using KDEConnect.WP81.Transport;
using KDEConnect.WP81.Security;
using Org.BouncyCastle.X509;
using System.Text;
using KDEConnect.WP81.Core;

namespace KdeConnect.HostTests
{
    /// <summary>
    /// Self-contained test runner for the portable protocol core.
    /// No external test framework, so it builds offline with the .NET SDK and
    /// has zero dependency on the phone project.
    /// Exit code 0 = all tests passed, 1 = at least one failure.
    /// </summary>
    public static class Program
    {
        private static int _passed;
        private static int _failed;
        private static readonly StringBuilder _failures = new StringBuilder();

        public static int Main(string[] args)
        {
            RunAll();

            Console.WriteLine();
            Console.WriteLine("passed: " + _passed + "  failed: " + _failed);
            if (_failed > 0)
            {
                Console.WriteLine();
                Console.WriteLine("FAILURES:");
                Console.Write(_failures.ToString());
                return 1;
            }
            return 0;
        }

        private static void RunAll()
        {
            JsonTests();
            FrameReaderTests();
            PacketTests();
            DeviceInfoTests();
            PairingTests();
            VerificationCodeTests();
            HandshakeTests();
            SanitizeTests();
            CapabilityTests();
            RateLimitTests();
            RedactorTests();
            GoldenFixtureTests();
            TlsTests();
            LineReaderTests();
            LinkEstablisherTests();
        }

        // ------------------------------------------------------------------ //
        //  TLS: real handshakes over real sockets, using the same Bouncy Castle
        //  1.8.2 build the phone project vendors.
        // ------------------------------------------------------------------ //

        private const string PhoneDeviceId = "0123456789abcdef0123456789abcdef";
        private const string DesktopDeviceId = "abcdef0123456789abcdef0123456789";

        private sealed class HandshakeOutcome
        {
            public ITlsSession Dialer;
            public ITlsSession Acceptor;
            public Exception Error;
        }

        /// <summary>
        /// Runs one full KDE-Connect-shaped handshake over loopback TCP.
        /// Remember the role inversion: the side that DIALS is the TLS server.
        /// </summary>
        private static HandshakeOutcome PerformHandshake(
            TlsIdentity dialerIdentity,
            TlsIdentity acceptorIdentity,
            TlsPeerPolicy dialerPolicy,
            TlsPeerPolicy acceptorPolicy)
        {
            HandshakeOutcome outcome = new HandshakeOutcome();
            BouncyCastleTlsBackend backend = new BouncyCastleTlsBackend();

            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Task dialerTask = Task.Run(delegate
            {
                TcpClient client = null;
                try
                {
                    // Deliberately NOT in a using block: the socket must outlive
                    // the handshake so the session's application stream works.
                    // Ownership passes to RawChannel, released by session.Close().
                    client = new TcpClient();
                    client.Connect(IPAddress.Loopback, port);
                    RawChannel channel = new RawChannel(client.GetStream(), client, "127.0.0.1", port);
                    outcome.Dialer = backend.HandshakeAsServerAsync(
                        channel, dialerIdentity, dialerPolicy,
                        TimeSpan.FromSeconds(15), CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    if (client != null)
                    {
                        client.Close();
                    }
                    if (outcome.Error == null)
                    {
                        outcome.Error = ex;
                    }
                }
            });

            Task acceptorTask = Task.Run(delegate
            {
                TcpClient server = null;
                try
                {
                    server = listener.AcceptTcpClient();
                    RawChannel channel = new RawChannel(server.GetStream(), server, "127.0.0.1", port);
                    outcome.Acceptor = backend.HandshakeAsClientAsync(
                        channel, acceptorIdentity, acceptorPolicy,
                        TimeSpan.FromSeconds(15), CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    if (server != null)
                    {
                        server.Close();
                    }
                    if (outcome.Error == null)
                    {
                        outcome.Error = ex;
                    }
                }
            });

            Task.WaitAll(new Task[] { dialerTask, acceptorTask }, TimeSpan.FromSeconds(30));
            listener.Stop();
            return outcome;
        }


        private static void TlsTests()
        {
            Section("TLS (Bouncy Castle 1.8.2, host)");

            // ---- certificate shape ---------------------------------------- //
            TlsIdentity phone = CertificateProvider.CreateIdentity(PhoneDeviceId);
            TlsIdentity desktop = CertificateProvider.CreateIdentity(DesktopDeviceId);

            X509Certificate phoneCertificate = CertificateProvider.LoadCertificate(phone.CertificateDer);
            CheckEqual("cert CN is the device id", PhoneDeviceId, CertificateProvider.GetCommonName(phoneCertificate));
            Check("cert valid now", CertificateProvider.IsValidAt(phoneCertificate, DateTime.UtcNow), "not valid");
            Check("cert notBefore in the past", phoneCertificate.NotBefore <= DateTime.UtcNow, "future");
            Check("cert notAfter years away", phoneCertificate.NotAfter >= DateTime.UtcNow.AddYears(5), "too soon");
            Check("cert organisation", phoneCertificate.SubjectDN.ToString().Contains("KDE"), phoneCertificate.SubjectDN.ToString());
            Check("private key reloads", CertificateProvider.LoadPrivateKey(phone.PrivateKeyDer) != null, "null");
            Check("cert reloads", CertificateProvider.LoadCertificate(phone.CertificateDer) != null, "null");
            Check("distinguished names differ per device",
                CertificateProvider.GetCommonName(CertificateProvider.LoadCertificate(desktop.CertificateDer)) == DesktopDeviceId,
                "mismatch");

            // ---- mutual handshake, we dialled (we are the TLS server) ------ //
            HandshakeOutcome out1 = PerformHandshake(phone, desktop,
                new TlsPeerPolicy(), new TlsPeerPolicy());

            if (out1.Error != null)
            {
                Check("handshake (dialer=TLS server) completes", false, out1.Error.ToString());
            }
            else
            {
                Check("handshake (dialer=TLS server) completes", true, string.Empty);
                Check("dialer obtained an application stream", out1.Dialer.ApplicationData != null, "null");
                Check("acceptor obtained an application stream", out1.Acceptor.ApplicationData != null, "null");
                CheckEqual("acceptor sees dialer certificate CN", PhoneDeviceId,
                    out1.Acceptor.Result.PeerCertificateCommonName);
                CheckEqual("dialer sees acceptor certificate CN", DesktopDeviceId,
                    out1.Dialer.Result.PeerCertificateCommonName);
                Check("acceptor got a real certificate", out1.Acceptor.Result.PeerCertificateDer != null &&
                    out1.Acceptor.Result.PeerCertificateDer.Length > 0, "empty");
                Check("acceptor found certificate valid in time",
                    out1.Acceptor.Result.PeerCertificateValidInTime, "invalid");
                Check("dialer found certificate valid in time",
                    out1.Dialer.Result.PeerCertificateValidInTime, "invalid");
                CheckEqual("peer certificate bytes match what was generated", 1,
                    BytesEqual(desktop.CertificateDer, out1.Dialer.Result.PeerCertificateDer) ? 1 : 0);

                // application data must actually flow both ways
                byte[] payload = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"kdeconnect.ping\"}\n");
                out1.Dialer.ApplicationData.Write(payload, 0, payload.Length);
                out1.Dialer.ApplicationData.Flush();
                byte[] received = new byte[payload.Length];
                int read = ReadFully(out1.Acceptor.ApplicationData, received, payload.Length);
                CheckEqual("application data crosses the tunnel", payload.Length, read);
                Check("application data intact",
                    System.Text.Encoding.UTF8.GetString(received) == "{\"type\":\"kdeconnect.ping\"}\n",
                    System.Text.Encoding.UTF8.GetString(received));

                out1.Dialer.Close();
                out1.Acceptor.Close();
            }

            // ---- reverse roles: we accepted (we are the TLS client) ------- //
            HandshakeOutcome out2 = PerformHandshake(desktop, phone,
                new TlsPeerPolicy(), new TlsPeerPolicy());

            if (out2.Error != null)
            {
                Check("handshake (acceptor=TLS client) completes", false, out2.Error.ToString());
            }
            else
            {
                Check("handshake (acceptor=TLS client) completes", true, string.Empty);
                // Role check: here the DIALER holds the desktop identity and the
                // ACCEPTOR holds the phone identity, so each side must see the
                // other's common name.
                CheckEqual("dialer sees acceptor certificate CN", PhoneDeviceId,
                    out2.Dialer.Result.PeerCertificateCommonName);
                CheckEqual("acceptor sees dialer certificate CN", DesktopDeviceId,
                    out2.Acceptor.Result.PeerCertificateCommonName);
                out2.Dialer.Close();
                out2.Acceptor.Close();
            }

            // ---- pinning policy ------------------------------------------- //
            byte[] pinned = desktop.CertificateDer;
            HandshakeContext pinnedOk = new HandshakeContext();
            pinnedOk.PlaintextIdentity = BuildConnection(DesktopDeviceId, 8);
            pinnedOk.SecureIdentity = BuildIdentity(DesktopDeviceId, 8);
            pinnedOk.PeerCertificateCommonName = DesktopDeviceId;
            pinnedOk.PeerCertificateValidInTime = true;
            pinnedOk.PinnedCertificateDer = pinned;
            pinnedOk.PresentedCertificateDer = desktop.CertificateDer;
            pinnedOk.IsTrustedPeer = true;
            Check("pinning accepts the original certificate", pinnedOk.Evaluate() == HandshakeDecision.Accept,
                pinnedOk.DescribeFailures());

            HandshakeContext pinnedChanged = Clone(pinnedOk);
            pinnedChanged.PresentedCertificateDer = phone.CertificateDer;
            Check("pinning rejects a replaced certificate", pinnedChanged.Evaluate() == HandshakeDecision.Reject,
                "accepted");

            // ---- timeout --------------------------------------------------- //
            TcpListener silent = new TcpListener(IPAddress.Loopback, 0);
            silent.Start();
            int silentPort = ((IPEndPoint)silent.LocalEndpoint).Port;
            Task<string> stalled = Task.Run(delegate
            {
                using (TcpClient c = new TcpClient())
                {
                    c.Connect(IPAddress.Loopback, silentPort);
                    RawChannel ch = new RawChannel(c.GetStream(), c, "127.0.0.1", silentPort);
                    BouncyCastleTlsBackend b = new BouncyCastleTlsBackend();
                    try
                    {
                        b.HandshakeAsServerAsync(ch, phone, new TlsPeerPolicy(),
                            TimeSpan.FromMilliseconds(700), CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (TimeoutException)
                    {
                        return "timeout";
                    }
                }
                return "no-timeout";
            });
            // The listener never accepts, so the handshake cannot finish.
            string timeoutOutcome = stalled.Wait(TimeSpan.FromSeconds(10)) ? stalled.Result : "hung";
            silent.Stop();
            Check("handshake times out instead of hanging forever", timeoutOutcome != "hung", timeoutOutcome);
        }

        private static int ReadFully(Stream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = stream.Read(buffer, total, count - total);
                if (n <= 0)
                {
                    break;
                }
                total += n;
            }
            return total;
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

        // ------------------------------------------------------------------ //

        private static void Check(string name, bool condition, string detail)
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine("  ok   " + name);
            }
            else
            {
                _failed++;
                Console.WriteLine("  FAIL " + name + (string.IsNullOrEmpty(detail) ? string.Empty : " -- " + detail));
                _failures.Append("  ").Append(name).Append(": ").Append(detail).AppendLine();
            }
        }

        private static void CheckEqual(string name, string expected, string actual)
        {
            Check(name, string.Equals(expected, actual, StringComparison.Ordinal),
                "expected <" + expected + "> got <" + actual + ">");
        }

        private static void CheckEqual(string name, long expected, long actual)
        {
            Check(name, expected == actual, "expected " + expected + " got " + actual);
        }

        private static void CheckThrows<T>(string name, Action action) where T : Exception
        {
            try
            {
                action();
                Check(name, false, "expected " + typeof(T).Name + " but nothing was thrown");
            }
            catch (T)
            {
                Check(name, true, string.Empty);
            }
            catch (Exception ex)
            {
                Check(name, false, "expected " + typeof(T).Name + " got " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("== " + title);
        }

        // ------------------------------------------------------------------ //

        private static void JsonTests()
        {
            Section("JSON");

            JObject o = new JObject();
            o.Set("a", new JString("x"));
            o.Set("b", JNumber.From(42));
            o.Set("c", new JBool(true));
            o.Set("d", JNull.Instance);
            o.Set("e", JNumber.From(1.5));
            string written = Json.Write(o);
            CheckEqual("json write compact", "{\"a\":\"x\",\"b\":42,\"c\":true,\"d\":null,\"e\":1.5}", written);

            JValue parsed = Json.Parse(written);
            JObject po = parsed as JObject;
            Check("json round trip", po != null, "not an object");
            CheckEqual("json rt a", "x", po.GetString("a", null));
            CheckEqual("json rt b", 42L, po.GetLong("b", -1));
            Check("json rt c", po.GetBool("c", false), "c not true");
            Check("json rt d null", po.Get("d") is JNull, "d is not null");
            Check("json rt e", Math.Abs(po.GetDouble("e", 0) - 1.5) < 1e-12, "e wrong");

            // Escapes, including a non-ASCII character emitted raw.
            JString s = new JString("quote\" back\\ nl\n tab\t é");
            string escaped = Json.Write(s);
            CheckEqual("json escape", "\"quote\\\" back\\\\ nl\\n tab\\t é\"", escaped);
            JString back = Json.Parse(escaped) as JString;
            Check("json escape round trip", back != null && back.Value == "quote\" back\\ nl\n tab\t é", "mismatch");

            // \u escapes
            JString uni = Json.Parse("\"\\u0041\\u00e9\"") as JString;
            Check("json unicode escape", uni != null && uni.Value == "Aé", "got " + (uni == null ? "null" : uni.Value));

            CheckThrows<JsonParseException>("json reject truncated object", delegate { Json.Parse("{\"a\":1"); });
            CheckThrows<JsonParseException>("json reject trailing garbage", delegate { Json.Parse("{\"a\":1} x"); });
            CheckThrows<JsonParseException>("json reject raw control char", delegate { Json.Parse("{\"a\":\"b\tc\"}"); });
            CheckThrows<JsonParseException>("json reject leading zero", delegate { Json.Parse("{\"a\":01}"); });
            CheckThrows<JsonParseException>("json reject bad escape", delegate { Json.Parse("{\"a\":\"\\q\"}"); });
            CheckThrows<JsonParseException>("json reject bare true", delegate { Json.Parse("true1"); });
            CheckThrows<JsonParseException>("json reject empty", delegate { Json.Parse(""); });

            // Depth limit: 30 nested arrays must be refused, not stack overflow.
            StringBuilder deep = new StringBuilder();
            for (int i = 0; i < 30; i++) deep.Append('[');
            for (int i = 0; i < 30; i++) deep.Append(']');
            CheckThrows<JsonParseException>("json reject deep nesting", delegate { Json.Parse(deep.ToString()); });

            // Size limit
            StringBuilder big = new StringBuilder("[\"");
            big.Append('x', Json.MaxInputLength + 10);
            big.Append("\"]");
            CheckThrows<JsonParseException>("json reject oversized input", delegate { Json.Parse(big.ToString()); });

            // Numbers arriving as strings (kdeconnect-android does this).
            JObject sn = Json.Parse("{\"v\":\"8\",\"w\":\"3.7\"}") as JObject;
            CheckEqual("json string number int", 8L, sn.GetLong("v", -1));
            CheckEqual("json string number double", 4L, sn.GetLong("w", -1));

            // Non-finite doubles are refused at construction.
            CheckThrows<InvalidOperationException>("json reject NaN", delegate { JNumber.From(double.NaN); });
            CheckThrows<InvalidOperationException>("json reject Infinity", delegate { JNumber.From(double.PositiveInfinity); });

            // Whole doubles serialise as integers (reference writes 1 not 1.0).
            CheckEqual("json whole double as int", "1", Json.Write(JNumber.From(1.0)));
        }

        private static void FrameReaderTests()
        {
            Section("FrameReader");

            FrameReader fr = new FrameReader(ProtocolConstants.MaxIdentityPacketSize);
            byte[] part1 = Encoding.UTF8.GetBytes("{\"a\":");
            byte[] part2 = Encoding.UTF8.GetBytes("1}\n{\"b\":2}\n");

            FrameReader.FeedResult r1 = fr.Feed(part1, 0, part1.Length);
            Check("frame partial yields nothing", r1.Frames.Count == 0, "got " + r1.Frames.Count);
            Check("frame partial buffered", fr.BufferedBytes == part1.Length, "buffered " + fr.BufferedBytes);

            FrameReader.FeedResult r2 = fr.Feed(part2, 0, part2.Length);
            CheckEqual("frame two frames from second read", 2, r2.Frames.Count);
            CheckEqual("frame first content", "{\"a\":1}", r2.Frames[0]);
            CheckEqual("frame second content", "{\"b\":2}", r2.Frames[1]);

            // Everything in one read.
            FrameReader fr2 = new FrameReader(64);
            byte[] both = Encoding.UTF8.GetBytes("one\ntwo\nthree\n");
            FrameReader.FeedResult r3 = fr2.Feed(both, 0, both.Length);
            CheckEqual("frame three in one read", 3, r3.Frames.Count);

            // CRLF tolerated.
            FrameReader fr3 = new FrameReader(64);
            byte[] crlf = Encoding.UTF8.GetBytes("abc\r\n");
            FrameReader.FeedResult r4 = fr3.Feed(crlf, 0, crlf.Length);
            CheckEqual("frame crlf", 1, r4.Frames.Count);
            CheckEqual("frame crlf content", "abc", r4.Frames[0]);

            // Over limit: a peer that never sends a newline must be cut off.
            FrameReader fr4 = new FrameReader(16);
            byte[] flood = Encoding.UTF8.GetBytes(new string('x', 64));
            FrameReader.FeedResult r5 = fr4.Feed(flood, 0, flood.Length);
            Check("frame over limit flagged", r5.OverLimit, "not flagged");
            CheckEqual("frame over limit discards buffer", 0, fr4.BufferedBytes);

            // Invalid UTF-8 is rejected, not silently replaced.
            FrameReader fr5 = new FrameReader(64);
            byte[] bad = new byte[] { (byte)'"', 0xC3, 0x28, (byte)'"', (byte)'\n' };
            FrameReader.FeedResult r6 = fr5.Feed(bad, 0, bad.Length);
            Check("frame invalid utf8 flagged", r6.DecodeFailed, "not flagged");
            CheckEqual("frame invalid utf8 yields no frame", 0, r6.Frames.Count);

            // Reset clears state.
            fr4.Reset();
            CheckEqual("frame reset", 0, fr4.BufferedBytes);
        }

        private static void PacketTests()
        {
            Section("NetworkPacket");

            NetworkPacket np = new NetworkPacket(ProtocolConstants.PacketTypePing);
            np.Id = 1700000000000L;
            np.SetString("message", "hello");
            byte[] wire = np.Serialize();
            Check("packet ends with newline", wire[wire.Length - 1] == '\n', "no newline");

            string text = Encoding.UTF8.GetString(wire, 0, wire.Length - 1);
            NetworkPacket back = NetworkPacket.Unserialize(text);
            CheckEqual("packet type rt", ProtocolConstants.PacketTypePing, back.Type);
            CheckEqual("packet id rt", 1700000000000L, back.Id);
            CheckEqual("packet body rt", "hello", back.GetString("message", null));

            // With payload + transfer info.
            NetworkPacket fp = new NetworkPacket("kdeconnect.share.request");
            fp.Id = 5;
            fp.SetString("filename", "a.txt");
            fp.PayloadSize = 12345;
            fp.PayloadTransferInfo = new JObject();
            fp.PayloadTransferInfo.Set("port", JNumber.From(1740));
            NetworkPacket fpBack = NetworkPacket.Unserialize(Encoding.UTF8.GetString(fp.Serialize()));
            Check("packet payload size rt", fpBack.PayloadSize == 12345, "got " + fpBack.PayloadSize);
            Check("packet has payload", fpBack.HasPayload, "no payload");
            Check("packet transfer info rt", fpBack.HasPayloadTransferInfo && fpBack.PayloadTransferInfo.GetInt("port", -1) == 1740, "bad port");

            // A packet without payload must not carry payloadSize.
            NetworkPacket plain = NetworkPacket.Unserialize(text);
            Check("packet no payload flag", !plain.HasPayload, "unexpected payload");

            CheckThrows<FormatException>("packet reject missing type", delegate { NetworkPacket.Unserialize("{\"id\":1,\"body\":{}}"); });
            CheckThrows<FormatException>("packet reject not object", delegate { NetworkPacket.Unserialize("[1,2]"); });
            CheckThrows<FormatException>("packet reject empty", delegate { NetworkPacket.Unserialize(""); });
            CheckThrows<FormatException>("packet reject oversized", delegate
            {
                string huge = "{\"id\":1,\"type\":\"x\",\"body\":{\"m\":\"" + new string('y', ProtocolConstants.MaxPacketSize) + "\"}}";
                NetworkPacket.Unserialize(huge);
            });

            // deviceId is scrubbed on receive (networkpacket.cpp:85-90).
            NetworkPacket scrubbed = NetworkPacket.Unserialize(
                "{\"id\":1,\"type\":\"kdeconnect.identity\",\"body\":{\"deviceId\":\"abc def/ghi\",\"deviceName\":\"n\"}}");
            CheckEqual("packet deviceid scrubbed", "abcdefghi", scrubbed.GetString("deviceId", null));

            // Non-finite doubles are dropped when setting (NetworkPacket.kt:134-141).
            NetworkPacket nf = new NetworkPacket("x");
            nf.SetDouble("v", double.NaN);
            Check("packet drops NaN", !nf.Has("v"), "NaN was kept");
        }

        private static void DeviceInfoTests()
        {
            Section("DeviceInfo");

            string goodId = "0123456789abcdef0123456789abcdef"; // 32 chars
            string maxId = new string('a', 38);
            Check("deviceid 32 ok", DeviceInfo.IsValidDeviceId(goodId), "rejected 32");
            Check("deviceid 38 ok", DeviceInfo.IsValidDeviceId(maxId), "rejected 38");
            Check("deviceid 31 bad", !DeviceInfo.IsValidDeviceId(new string('a', 31)), "accepted 31");
            Check("deviceid 39 bad", !DeviceInfo.IsValidDeviceId(new string('a', 39)), "accepted 39");
            Check("deviceid bad char", !DeviceInfo.IsValidDeviceId(goodId.Substring(0, 31) + "x!"), "accepted bad char");
            Check("deviceid null", !DeviceInfo.IsValidDeviceId(null), "accepted null");
            Check("deviceid dots bad", !DeviceInfo.IsValidDeviceId("deadbeef.deadbeef.deadbeef.deadbee"), "accepted dots");

            CheckEqual("filtername strips", "abc", DeviceInfo.FilterName("a[b]c"));
            CheckEqual("filtername truncates", 32, DeviceInfo.FilterName(new string('x', 100)).Length);

            // Identity packet validation.
            NetworkPacket id = new NetworkPacket(ProtocolConstants.PacketTypeIdentity);
            id.SetString("deviceId", goodId);
            id.SetString("deviceName", "My Phone");
            id.SetInt("protocolVersion", 8);
            Check("identity valid", DeviceInfo.IsValidIdentityPacket(id), "rejected valid identity");

            NetworkPacket idNoName = new NetworkPacket(ProtocolConstants.PacketTypeIdentity);
            idNoName.SetString("deviceId", goodId);
            idNoName.SetString("deviceName", "[]<>");
            Check("identity needs name", !DeviceInfo.IsValidIdentityPacket(idNoName), "accepted empty filtered name");

            NetworkPacket idBadType = new NetworkPacket("kdeconnect.ping");
            Check("identity needs type", !DeviceInfo.IsValidIdentityPacket(idBadType), "accepted wrong type");

            // UDP discovery only needs a valid device id.
            NetworkPacket udp = new NetworkPacket(ProtocolConstants.PacketTypeIdentity);
            udp.SetString("deviceId", goodId);
            udp.SetInt("tcpPort", 1716);
            Check("udp discovery valid", DeviceInfo.IsValidUdpDiscoveryPacket(udp), "rejected");
            NetworkPacket udpBadId = new NetworkPacket(ProtocolConstants.PacketTypeIdentity);
            udpBadId.SetString("deviceId", "short");
            udpBadId.SetInt("tcpPort", 1716);
            Check("udp discovery rejects bad id", !DeviceInfo.IsValidUdpDiscoveryPacket(udpBadId), "accepted bad id");

            // Connection packet needs both ids valid.
            NetworkPacket conn = id.CloneForTest();
            conn.SetString("targetDeviceId", maxId);
            Check("connection valid", DeviceInfo.IsValidConnectionPacket(conn), "rejected");
            conn.SetString("targetDeviceId", "short");
            Check("connection needs valid target", !DeviceInfo.IsValidConnectionPacket(conn), "accepted short target");

            // Capability extraction.
            NetworkPacket cap = new NetworkPacket(ProtocolConstants.PacketTypeIdentity);
            JArray arr = new JArray();
            arr.Add(new JString("kdeconnect.ping"));
            arr.Add(new JString("kdeconnect.battery"));
            cap.Body.Set("incomingCapabilities", arr);
            CheckEqual("capability list parsed", 2, cap.GetStringList("incomingCapabilities").Count);

            // deviceType mapping.
            Check("devicetype phone", DeviceInfo.KindFromString("phone") == DeviceKind.Phone, "bad");
            Check("devicetype smartphone", DeviceInfo.KindFromString("smartphone") == DeviceKind.Phone, "bad");
            Check("devicetype unknown", DeviceInfo.KindFromString("toaster") == DeviceKind.Unknown, "bad");
        }

        private static void PairingTests()
        {
            Section("Pairing");

            // Happy path: we ask, they accept.
            PairingHandler h = new PairingHandler();
            PairingEvent ev;
            NetworkPacket req = h.RequestPairing(1000, out ev);
            Check("pair request built", req != null, "null packet");
            Check("pair request state", h.State == PairState.Requested, h.State.ToString());
            CheckEqual("pair request timestamp", 1000L, req.GetLong("timestamp", -1));
            Check("pair request flag", req.GetBool("pair", false), "pair flag not true");
            CheckEqual("pair request type", ProtocolConstants.PacketTypePair, req.Type);

            NetworkPacket accept = new NetworkPacket(ProtocolConstants.PacketTypePair);
            accept.SetBool("pair", true);
            NetworkPacket reply = h.OnPacketReceived(accept, 1010, out ev);
            Check("pair accepted", ev == PairingEvent.Accepted, ev.ToString());
            Check("pair state paired", h.State == PairState.Paired, h.State.ToString());
            Check("pair no reply needed", reply == null, "unexpected reply");

            // Peer asks, we accept.
            PairingHandler h2 = new PairingHandler();
            NetworkPacket peerReq = new NetworkPacket(ProtocolConstants.PacketTypePair);
            peerReq.SetBool("pair", true);
            peerReq.SetLong("timestamp", 5000);
            NetworkPacket r2 = h2.OnPacketReceived(peerReq, 5010, out ev);
            Check("peer request event", ev == PairingEvent.IncomingRequest, ev.ToString());
            Check("peer request state", h2.State == PairState.RequestedByPeer, h2.State.ToString());
            Check("peer request no immediate reply", r2 == null, "replied too early");

            NetworkPacket accept2 = h2.AcceptPairing(out ev);
            Check("we accept", accept2 != null && accept2.GetBool("pair", false), "no accept packet");
            Check("we accept has no timestamp", !accept2.Has("timestamp"), "timestamp present");
            Check("we accept paired", h2.State == PairState.Paired, h2.State.ToString());
            Check("we accept event", ev == PairingEvent.Accepted, ev.ToString());

            // Reject.
            PairingHandler h3 = new PairingHandler();
            h3.RequestPairing(10, out ev);
            NetworkPacket reject = new NetworkPacket(ProtocolConstants.PacketTypePair);
            reject.SetBool("pair", false);
            h3.OnPacketReceived(reject, 11, out ev);
            Check("reject event", ev == PairingEvent.CancelledByPeer, ev.ToString());
            Check("reject state", h3.State == PairState.NotPaired, h3.State.ToString());

            // Timeout.
            PairingHandler h4 = new PairingHandler();
            h4.RequestPairing(10, out ev);
            NetworkPacket timeoutPacket = h4.OnTimeout(out ev);
            Check("timeout event", ev == PairingEvent.TimedOut, ev.ToString());
            Check("timeout sends reject", timeoutPacket != null && !timeoutPacket.GetBool("pair", true), "no reject");
            Check("timeout state", h4.State == PairState.NotPaired, h4.State.ToString());

            // Clock skew beyond 1800s.
            PairingHandler h5 = new PairingHandler();
            NetworkPacket skewed = new NetworkPacket(ProtocolConstants.PacketTypePair);
            skewed.SetBool("pair", true);
            skewed.SetLong("timestamp", 0);
            h5.OnPacketReceived(skewed, 5000, out ev);
            Check("clock skew rejected", ev == PairingEvent.FailedClockOutOfSync, ev.ToString());
            Check("clock skew not paired", h5.State == PairState.NotPaired, h5.State.ToString());

            // Request without timestamp is ignored.
            PairingHandler h6 = new PairingHandler();
            NetworkPacket noTs = new NetworkPacket(ProtocolConstants.PacketTypePair);
            noTs.SetBool("pair", true);
            h6.OnPacketReceived(noTs, 100, out ev);
            Check("missing timestamp ignored", ev == PairingEvent.FailedIgnored, ev.ToString());

            // Second request before timeout is ignored.
            PairingHandler h7 = new PairingHandler();
            h7.OnPacketReceived(peerReq, 5000, out ev);
            h7.OnPacketReceived(peerReq, 5010, out ev);
            Check("second request ignored", ev == PairingEvent.FailedIgnored, ev.ToString());
            Check("second request keeps state", h7.State == PairState.RequestedByPeer, h7.State.ToString());

            // Accept from an already-paired device without timestamp is ignored.
            PairingHandler h8 = new PairingHandler();
            h8.RequestPairing(1, out ev);
            h8.OnPacketReceived(accept, 2, out ev);
            Check("h8 paired", h8.State == PairState.Paired, h8.State.ToString());
            h8.OnPacketReceived(accept, 3, out ev);
            Check("accept while paired ignored", ev == PairingEvent.FailedIgnored, ev.ToString());

            // Paired + request WITH timestamp = unpair then new request.
            PairingHandler h9 = new PairingHandler();
            h9.RequestPairing(1, out ev);
            h9.OnPacketReceived(accept, 2, out ev);
            NetworkPacket reReq = new NetworkPacket(ProtocolConstants.PacketTypePair);
            reReq.SetBool("pair", true);
            reReq.SetLong("timestamp", 9000);
            NetworkPacket reReply = h9.OnPacketReceived(reReq, 9010, out ev);
            // The reference emits unpaired() first and then falls through to the
            // NotPaired branch (pairinghandler.cpp:41-64); we surface the
            // actionable final event and the state reflects both steps.
            Check("repair ends requesting", h9.State == PairState.RequestedByPeer, h9.State.ToString());
            Check("repair reports incoming request", ev == PairingEvent.IncomingRequest, ev.ToString());
            Check("repair no auto reply", reReply == null, "unexpected reply");

            // Unpair while paired.
            PairingHandler h10 = new PairingHandler();
            h10.RequestPairing(1, out ev);
            h10.OnPacketReceived(accept, 2, out ev);
            NetworkPacket unpair = h10.Unpair(out ev);
            Check("unpair event", ev == PairingEvent.Unpaired, ev.ToString());
            Check("unpair state", h10.State == PairState.NotPaired, h10.State.ToString());
            Check("unpair sends pair=false", unpair != null && !unpair.GetBool("pair", true), "wrong packet");

            // Re-requesting while pending re-sends, as PairingHandler::requestPairing
            // does (it only refuses when already paired).
            PairingHandler h11 = new PairingHandler();
            h11.RequestPairing(1, out ev);
            NetworkPacket second = h11.RequestPairing(2, out ev);
            Check("second local request re-sends", second != null, "null packet");
            Check("second local request keeps state", h11.State == PairState.Requested, h11.State.ToString());

            // Already paired: local request refused.
            PairingHandler h12 = new PairingHandler();
            h12.RequestPairing(1, out ev);
            h12.OnPacketReceived(accept, 2, out ev);
            NetworkPacket third = h12.RequestPairing(3, out ev);
            Check("request while paired refused", third == null && ev == PairingEvent.FailedIgnored, ev.ToString());

            // Pairing while peer already asked: we accept instead.
            PairingHandler h13 = new PairingHandler();
            h13.OnPacketReceived(peerReq, 5000, out ev);
            NetworkPacket auto = h13.RequestPairing(5010, out ev);
            Check("auto accept their request", auto != null && auto.GetBool("pair", false), "no auto accept");
            Check("auto accept state", h13.State == PairState.Paired, h13.State.ToString());
        }

        private static void VerificationCodeTests()
        {
            Section("Verification code");

            byte[] a = Encoding.ASCII.GetBytes("public-key-AAAAAAAAAAAAAAAA");
            byte[] b = Encoding.ASCII.GetBytes("public-key-ZZZZZZZZZZZZZZZZZZ");

            string c1 = PairingHandler.ComputeVerificationCode(a, b, 12345);
            string c2 = PairingHandler.ComputeVerificationCode(b, a, 12345);
            CheckEqual("verification code order independent", c1, c2);
            CheckEqual("verification code length", 8, c1.Length);
            Check("verification code uppercase hex", IsUpperHex(c1), c1);

            string c3 = PairingHandler.ComputeVerificationCode(a, b, 12346);
            Check("verification code depends on timestamp", c1 != c3, "same code for different timestamps");

            CheckEqual("verification code empty on null", string.Empty,
                PairingHandler.ComputeVerificationCode(null, b, 1));
        }

        private static bool IsUpperHex(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return true;
        }

        private static void HandshakeTests()
        {
            Section("Handshake");

            string deviceId = "0123456789abcdef0123456789abcdef";
            byte[] certA = Encoding.ASCII.GetBytes("CERT-A");
            byte[] certB = Encoding.ASCII.GetBytes("CERT-B");

            NetworkPacket plain = BuildConnection(deviceId, 8);
            NetworkPacket secure = BuildIdentity(deviceId, 8);

            HandshakeContext ok = new HandshakeContext();
            ok.PlaintextIdentity = plain;
            ok.SecureIdentity = secure;
            ok.PeerCertificateCommonName = deviceId;
            ok.PeerCertificateValidInTime = true;
            ok.PresentedCertificateDer = certA;
            Check("handshake accept unpaired", ok.Evaluate() == HandshakeDecision.Accept, ok.DescribeFailures());

            HandshakeContext cnMismatch = Clone(ok);
            cnMismatch.PeerCertificateCommonName = "ffffffffffffffffffffffffffffffff";
            Check("handshake reject cn mismatch", cnMismatch.Evaluate() == HandshakeDecision.Reject, "accepted");

            HandshakeContext noCert = Clone(ok);
            noCert.PeerCertificateCommonName = string.Empty;
            noCert.PresentedCertificateDer = null;
            Check("handshake reject missing cert", noCert.Evaluate() == HandshakeDecision.Reject, "accepted");

            HandshakeContext versionChange = Clone(ok);
            NetworkPacket secureBad = BuildIdentity(deviceId, 7);
            versionChange.SecureIdentity = secureBad;
            Check("handshake reject version change", versionChange.Evaluate() == HandshakeDecision.Reject, "accepted");

            HandshakeContext idChange = Clone(ok);
            idChange.SecureIdentity = BuildIdentity("ffffffffffffffffffffffffffffffff", 8);
            Check("handshake reject id change", idChange.Evaluate() == HandshakeDecision.Reject, "accepted");

            HandshakeContext oldVersion = Clone(ok);
            NetworkPacket oldPlain = BuildConnection(deviceId, 7);
            oldPlain.SetString("targetDeviceId", deviceId);
            oldVersion.PlaintextIdentity = oldPlain;
            Check("handshake reject old protocol", oldVersion.Evaluate() == HandshakeDecision.Reject, "accepted");

            HandshakeContext expired = Clone(ok);
            expired.PeerCertificateValidInTime = false;
            Check("handshake reject expired cert", expired.Evaluate() == HandshakeDecision.Reject, "accepted");

            // Pinning: paired peer with matching cert is accepted.
            HandshakeContext pinned = Clone(ok);
            pinned.IsTrustedPeer = true;
            pinned.PinnedCertificateDer = certA;
            pinned.PresentedCertificateDer = certA;
            Check("handshake accept pinned match", pinned.Evaluate() == HandshakeDecision.Accept, pinned.DescribeFailures());

            // Pinning: changed certificate is refused.
            HandshakeContext changed = Clone(ok);
            changed.IsTrustedPeer = true;
            changed.PinnedCertificateDer = certA;
            changed.PresentedCertificateDer = certB;
            Check("handshake reject changed cert", changed.Evaluate() == HandshakeDecision.Reject, "accepted");

            // Trusted but nothing pinned is a broken state: refuse.
            HandshakeContext trustedNoPin = Clone(ok);
            trustedNoPin.IsTrustedPeer = true;
            trustedNoPin.PinnedCertificateDer = null;
            Check("handshake reject trusted without pin", trustedNoPin.Evaluate() == HandshakeDecision.Reject, "accepted");

            // No secure identity at all.
            HandshakeContext noSecure = Clone(ok);
            noSecure.SecureIdentity = null;
            Check("handshake reject no secure identity", noSecure.Evaluate() == HandshakeDecision.Reject, "accepted");
        }

        private static HandshakeContext Clone(HandshakeContext src)
        {
            HandshakeContext c = new HandshakeContext();
            c.PlaintextIdentity = src.PlaintextIdentity;
            c.SecureIdentity = src.SecureIdentity;
            c.PeerCertificateCommonName = src.PeerCertificateCommonName;
            c.PeerCertificateValidInTime = src.PeerCertificateValidInTime;
            c.PinnedCertificateDer = src.PinnedCertificateDer;
            c.PresentedCertificateDer = src.PresentedCertificateDer;
            c.IsTrustedPeer = src.IsTrustedPeer;
            return c;
        }

        private static NetworkPacket BuildConnection(string deviceId, int protocolVersion)
        {
            NetworkPacket np = new NetworkPacket(ProtocolConstants.PacketTypeIdentity);
            np.SetString("deviceId", deviceId);
            np.SetString("deviceName", "Peer");
            np.SetInt("protocolVersion", protocolVersion);
            np.SetString("targetDeviceId", deviceId);
            np.SetInt("targetProtocolVersion", ProtocolConstants.ProtocolVersion);
            return np;
        }

        private static NetworkPacket BuildIdentity(string deviceId, int protocolVersion)
        {
            NetworkPacket np = new NetworkPacket(ProtocolConstants.PacketTypeIdentity);
            np.SetString("deviceId", deviceId);
            np.SetString("deviceName", "Peer");
            np.SetString("deviceType", "desktop");
            np.SetInt("protocolVersion", protocolVersion);
            np.SetStringList("incomingCapabilities", new List<string>(new string[] { "kdeconnect.ping" }));
            np.SetStringList("outgoingCapabilities", new List<string>(new string[] { "kdeconnect.ping" }));
            return np;
        }

        private static void SanitizeTests()
        {
            Section("Sanitize");

            CheckEqual("file plain", "report.pdf", Sanitize.FileName("report.pdf"));
            CheckEqual("file strips traversal", "passwd", Sanitize.FileName("../../etc/passwd"));
            CheckEqual("file strips backslash traversal", "x.txt", Sanitize.FileName("..\\..\\x.txt"));
            CheckEqual("file strips drive", "c.txt", Sanitize.FileName("C:\\temp\\c.txt"));
            CheckEqual("file strips slashes", "a.txt", Sanitize.FileName("/a/b/a.txt"));
            CheckEqual("file rejects dot", "received-file", Sanitize.FileName(".."));
            CheckEqual("file rejects empty", "received-file", Sanitize.FileName(""));
            CheckEqual("file rejects null", "received-file", Sanitize.FileName(null));
            CheckEqual("file rejects spaces only", "received-file", Sanitize.FileName("   "));
            CheckEqual("file strips control chars", "ab", Sanitize.FileName("a\u0000\u0007b"));
            CheckEqual("file strips colon", "ab", Sanitize.FileName("a:b"));
            CheckEqual("file keeps unicode", "café.txt", Sanitize.FileName("café.txt"));
            CheckEqual("file prefixes reserved", "_CON.txt", Sanitize.FileName("CON.txt"));
            CheckEqual("file prefixes reserved stem", "_aux", Sanitize.FileName("aux"));
            Check("file bounded length", Sanitize.FileName(new string('z', 500)).Length <= 120,
                "len=" + Sanitize.FileName(new string('z', 500)).Length);
            Check("file keeps extension on truncate",
                Sanitize.FileName(new string('z', 500) + ".jpg").EndsWith(".jpg", StringComparison.Ordinal),
                Sanitize.FileName(new string('z', 500) + ".jpg"));

            CheckEqual("name bounded", 32, Sanitize.DeviceName(new string('q', 200)).Length);
            CheckEqual("name strips", "ab", Sanitize.DeviceName("a<b"));
        }

        private static void CapabilityTests()
        {
            Section("Capabilities");

            Check("cap can send protocol pair", Capabilities.CanSendToPeer(ProtocolConstants.PacketTypePair, null), "refused");
            Check("cap can send identity", Capabilities.CanSendToPeer(ProtocolConstants.PacketTypeIdentity, null), "refused");

            List<string> peerIn = new List<string>();
            peerIn.Add("kdeconnect.ping");
            Check("cap can send declared", Capabilities.CanSendToPeer("kdeconnect.ping", peerIn), "refused");
            Check("cap cannot send undeclared", !Capabilities.CanSendToPeer("kdeconnect.sms.messages", peerIn), "allowed");
            Check("cap cannot send with no peer info", !Capabilities.CanSendToPeer("kdeconnect.ping", null), "allowed");

            Check("cap can receive ping", Capabilities.CanReceive("kdeconnect.ping"), "refused");
            Check("cap can receive pair", Capabilities.CanReceive(ProtocolConstants.PacketTypePair), "refused");
            Check("cap cannot receive sms", !Capabilities.CanReceive("kdeconnect.sms.messages"), "allowed");
            Check("cap cannot receive runcommand", !Capabilities.CanReceive("kdeconnect.runcommand"), "allowed");
            // WP8.1 WinRT has no Clipboard class (docs/known-limitations.md L1), so a
            // pushed clipboard would have nowhere to go: never advertise it as incoming.
            Check("cap cannot receive clipboard", !Capabilities.CanReceive("kdeconnect.clipboard"), "allowed");
            Check("cap cannot receive clipboard.connect", !Capabilities.CanReceive("kdeconnect.clipboard.connect"), "allowed");
            // ...but sending operator-typed text to the desktop needs no clipboard API.
            List<string> outCaps = Capabilities.OutgoingList();
            Check("cap can send clipboard", outCaps.Contains("kdeconnect.clipboard"), "missing");

            Check("cap incoming non empty", Capabilities.Incoming.Length > 0, "empty");
            Check("cap outgoing non empty", Capabilities.Outgoing.Length > 0, "empty");
            Check("cap describes", Capabilities.Describe().Contains("kdeconnect.ping"), Capabilities.Describe());
        }

        private static void RateLimitTests()
        {
            Section("RateLimit");

            RateLimitGuard g = new RateLimitGuard(500);
            Check("ratelimit first event allowed", !g.WouldDrop("1.2.3.4", 1000), "dropped");
            g.Record("1.2.3.4", 1000);
            Check("ratelimit inside window dropped", g.WouldDrop("1.2.3.4", 1400), "allowed");
            Check("ratelimit other key allowed", !g.WouldDrop("5.6.7.8", 1400), "dropped");
            Check("ratelimit after window allowed", !g.WouldDrop("1.2.3.4", 1501), "dropped");
            Check("ratelimit boundary exclusive", !g.WouldDrop("1.2.3.4", 1500), "dropped at exact boundary");

            // The documented interop delay must exceed the desktop window.
            Check("interop delay exceeds desktop window",
                ProtocolConstants.UdpToTcpDelayMs > ProtocolConstants.PeerRateLimitWindowMs,
                "delay=" + ProtocolConstants.UdpToTcpDelayMs + " window=" + ProtocolConstants.PeerRateLimitWindowMs);
        }

        private static void RedactorTests()
        {
            Section("Redactor");

            Check("redact content key", Redactor.IsSensitiveKey("content"), "not sensitive");
            Check("redact password key", Redactor.IsSensitiveKey("password"), "not sensitive");
            Check("redact filename key", Redactor.IsSensitiveKey("filename"), "not sensitive");
            Check("keep type key", !Redactor.IsSensitiveKey("type"), "sensitive");

            NetworkPacket np = new NetworkPacket("kdeconnect.clipboard");
            np.SetString("content", "super secret clipboard text");
            np.SetString("timestamp", "123");
            string described = Redactor.DescribePacket(np);
            string secret = "super secret clipboard text";
            Check("describe hides value", !described.Contains(secret), described);
            Check("describe keeps length", described.Contains("redacted " + secret.Length + " chars"), described);
            Check("describe keeps type", described.Contains("kdeconnect.clipboard"), described);

            CheckEqual("safe line strips newline", "a b", Redactor.SafeLine("a\nb", 100));
            Check("safe line bounded", Redactor.SafeLine(new string('x', 500), 50).Length == 50, "not bounded");
            CheckEqual("fingerprint empty", string.Empty, Redactor.CertificateFingerprint(null, 8));
            Check("fingerprint groups", Redactor.CertificateFingerprint(Encoding.ASCII.GetBytes("abc"), 4).Split(':').Length == 4, "wrong groups");
        }

        private static void GoldenFixtureTests()
        {
            Section("Golden fixtures (derived from the pinned reference source)");

            // UDP discovery packet as produced by DeviceInfo::toUdpDiscoveryPacket.
            // id is a wall-clock value, so it is pinned here for reproducibility.
            string udpFixture =
                "{\"id\":1763500000000,\"type\":\"kdeconnect.identity\",\"body\":" +
                "{\"deviceId\":\"0123456789abcdef0123456789abcdef\",\"deviceName\":\"G14\"," +
                "\"protocolVersion\":8,\"tcpPort\":1716}}";
            NetworkPacket udp = NetworkPacket.Unserialize(udpFixture);
            Check("fixture udp type", udp.Type == ProtocolConstants.PacketTypeIdentity, udp.Type);
            CheckEqual("fixture udp port", 1716, udp.GetInt("tcpPort", -1));
            CheckEqual("fixture udp version", 8, udp.GetInt("protocolVersion", -1));
            Check("fixture udp valid", DeviceInfo.IsValidUdpDiscoveryPacket(udp), "invalid");

            // Connection packet as produced by toConnectionPacket.
            string connFixture =
                "{\"id\":1763500000001,\"type\":\"kdeconnect.identity\",\"body\":" +
                "{\"deviceId\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"deviceName\":\"WP81\"," +
                "\"protocolVersion\":8,\"targetDeviceId\":\"0123456789abcdef0123456789abcdef\"," +
                "\"targetProtocolVersion\":8}}";
            NetworkPacket conn = NetworkPacket.Unserialize(connFixture);
            Check("fixture connection valid", DeviceInfo.IsValidConnectionPacket(conn), "invalid");
            CheckEqual("fixture connection target", "0123456789abcdef0123456789abcdef", conn.GetString("targetDeviceId", null));

            // Pair request as produced by PairingHandler::requestPairing.
            NetworkPacket pairReq = PairingHandler.BuildRequestPacket(1763500000);
            pairReq.Id = 1763500000002L;
            string pairWire = Encoding.UTF8.GetString(pairReq.Serialize()).TrimEnd('\n');
            CheckEqual("fixture pair request wire",
                "{\"id\":1763500000002,\"type\":\"kdeconnect.pair\",\"body\":{\"pair\":true,\"timestamp\":1763500000}}",
                pairWire);

            // Pair accept: note there is NO timestamp, which is what distinguishes
            // an accept from a request (pairinghandler.cpp:42).
            NetworkPacket pairAccept = PairingHandler.BuildAcceptPacket();
            pairAccept.Id = 1763500000003L;
            CheckEqual("fixture pair accept wire",
                "{\"id\":1763500000003,\"type\":\"kdeconnect.pair\",\"body\":{\"pair\":true}}",
                Encoding.UTF8.GetString(pairAccept.Serialize()).TrimEnd('\n'));

            // Ping with a message, as produced by PingPlugin::sendPing(message).
            NetworkPacket ping = new NetworkPacket(ProtocolConstants.PacketTypePing);
            ping.Id = 1763500000004L;
            ping.SetString("message", "Ping!");
            CheckEqual("fixture ping wire",
                "{\"id\":1763500000004,\"type\":\"kdeconnect.ping\",\"body\":{\"message\":\"Ping!\"}}",
                Encoding.UTF8.GetString(ping.Serialize()).TrimEnd('\n'));

            // Battery report, as produced by the battery plugin.
            NetworkPacket battery = new NetworkPacket("kdeconnect.battery");
            battery.Id = 1763500000005L;
            battery.SetInt("currentCharge", 87);
            battery.SetBool("isCharging", false);
            CheckEqual("fixture battery wire",
                "{\"id\":1763500000005,\"type\":\"kdeconnect.battery\",\"body\":{\"currentCharge\":87,\"isCharging\":false}}",
                Encoding.UTF8.GetString(battery.Serialize()).TrimEnd('\n'));

            // Share announcement with payload, as produced by CompositeUploadJob::sendPacketFor.
            NetworkPacket share = new NetworkPacket("kdeconnect.share.request");
            share.Id = 1763500000006L;
            share.SetString("filename", "notes.txt");
            share.PayloadSize = 4096;
            share.PayloadTransferInfo = new JObject();
            share.PayloadTransferInfo.Set("port", JNumber.From(1739));
            share.SetInt("numberOfFiles", 1);
            share.SetLong("totalPayloadSize", 4096);
            string shareWire = Encoding.UTF8.GetString(share.Serialize()).TrimEnd('\n');
            NetworkPacket shareBack = NetworkPacket.Unserialize(shareWire);
            CheckEqual("fixture share filename", "notes.txt", shareBack.GetString("filename", null));
            CheckEqual("fixture share payload size", 4096L, shareBack.PayloadSize);
            CheckEqual("fixture share port", 1739, shareBack.PayloadTransferInfo.GetInt("port", -1));
            CheckEqual("fixture share total", 4096L, shareBack.GetLong("totalPayloadSize", -1));
            Check("fixture share payload flag", shareBack.HasPayload, "no payload");
        }

        // ------------------------------------------------------------------ //
        //  Buffered line reader and full link establishment.
        // ------------------------------------------------------------------ //

        private static DeviceInfo BuildInfo(string id, string name)
        {
            DeviceInfo info = new DeviceInfo(id, name, DeviceKind.Phone, ProtocolConstants.ProtocolVersion);
            info.IncomingCapabilities.AddRange(Capabilities.IncomingList());
            info.OutgoingCapabilities.AddRange(Capabilities.OutgoingList());
            return info;
        }

        private static void LineReaderTests()
        {
            Section("BufferedLineReader");

            // Two frames arriving in one write: the second must survive the first read.
            using (MemoryStream stream = new MemoryStream())
            {
                byte[] both = System.Text.Encoding.UTF8.GetBytes("{\"a\":1}\n{\"b\":2}\n");
                stream.Write(both, 0, both.Length);
                stream.Position = 0;

                BufferedLineReader reader = new BufferedLineReader(stream, 4096);
                string first = reader.ReadLineAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                    .GetAwaiter().GetResult();
                string second = reader.ReadLineAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                    .GetAwaiter().GetResult();
                CheckEqual("reader first frame", "{\"a\":1}", first);
                CheckEqual("reader second frame from carry", "{\"b\":2}", second);
                CheckEqual("reader carry emptied", 0, reader.BufferedByteCount);
            }

            // CRLF is tolerated and not treated as content.
            using (MemoryStream stream = new MemoryStream())
            {
                byte[] data = System.Text.Encoding.UTF8.GetBytes("hello\r\n");
                stream.Write(data, 0, data.Length);
                stream.Position = 0;
                BufferedLineReader reader = new BufferedLineReader(stream, 4096);
                CheckEqual("reader strips CR", "hello",
                    reader.ReadLineAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                        .GetAwaiter().GetResult());
            }

            // A frame longer than the limit with no newline must be cut off.
            using (MemoryStream stream = new MemoryStream())
            {
                byte[] flood = new byte[300];
                for (int i = 0; i < flood.Length; i++)
                {
                    flood[i] = (byte)'x';
                }
                stream.Write(flood, 0, flood.Length);
                stream.Position = 0;
                BufferedLineReader reader = new BufferedLineReader(stream, 64);
                CheckThrows<InvalidDataException>("reader enforces frame limit", delegate
                {
                    reader.ReadLineAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                        .GetAwaiter().GetResult();
                });
            }

            // Peer closing mid-frame is an error, not a silent short read.
            using (MemoryStream stream = new MemoryStream())
            {
                byte[] partial = System.Text.Encoding.UTF8.GetBytes("{\"incomplete\"");
                stream.Write(partial, 0, partial.Length);
                stream.Position = 0;
                BufferedLineReader reader = new BufferedLineReader(stream, 4096);
                CheckThrows<EndOfStreamException>("reader reports peer close", delegate
                {
                    reader.ReadLineAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                        .GetAwaiter().GetResult();
                });
            }

            // Malformed UTF-8 is rejected rather than repaired.
            using (MemoryStream stream = new MemoryStream())
            {
                byte[] bad = new byte[] { (byte)'"', 0xC3, 0x28, (byte)'"', (byte)'\n' };
                stream.Write(bad, 0, bad.Length);
                stream.Position = 0;
                BufferedLineReader reader = new BufferedLineReader(stream, 4096);
                CheckThrows<InvalidDataException>("reader rejects invalid utf8", delegate
                {
                    reader.ReadLineAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                        .GetAwaiter().GetResult();
                });
            }
        }

        private sealed class LinkOutcome
        {
            public LinkEstablishResult Dialer;
            public LinkEstablishResult Acceptor;
            public Exception Error;
        }

        /// <summary>
        /// Runs the complete KDE Connect link sequence over loopback: plaintext
        /// connection packet, TLS handshake in the inverted roles, identity
        /// exchange inside TLS, then the validation policy.
        /// </summary>
        private static LinkOutcome RunLink(
            DeviceInfo dialerInfo,
            DeviceInfo acceptorInfo,
            TlsIdentity dialerIdentity,
            TlsIdentity acceptorIdentity,
            NetworkPacket discoveryPacket,
            TlsPeerPolicy dialerPolicy,
            TlsPeerPolicy acceptorPolicy)
        {
            LinkOutcome outcome = new LinkOutcome();
            BouncyCastleTlsBackend backend = new BouncyCastleTlsBackend();

            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Task dialerTask = Task.Run(delegate
            {
                TcpClient client = null;
                try
                {
                    client = new TcpClient();
                    client.Connect(IPAddress.Loopback, port);
                    RawChannel channel = new RawChannel(client.GetStream(), client, "127.0.0.1", port);
                    outcome.Dialer = LinkEstablisher.EstablishOutboundAsync(
                        channel, backend, dialerIdentity, dialerInfo, discoveryPacket,
                        dialerPolicy, TimeSpan.FromSeconds(15), CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    if (client != null)
                    {
                        client.Close();
                    }
                    if (outcome.Error == null)
                    {
                        outcome.Error = ex;
                    }
                }
            });

            Task acceptorTask = Task.Run(delegate
            {
                TcpClient server = null;
                try
                {
                    server = listener.AcceptTcpClient();
                    RawChannel channel = new RawChannel(server.GetStream(), server, "127.0.0.1", port);
                    outcome.Acceptor = LinkEstablisher.EstablishInboundAsync(
                        channel, backend, acceptorIdentity, acceptorInfo,
                        acceptorPolicy, TimeSpan.FromSeconds(15), CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    if (server != null)
                    {
                        server.Close();
                    }
                    if (outcome.Error == null)
                    {
                        outcome.Error = ex;
                    }
                }
            });

            Task.WaitAll(new Task[] { dialerTask, acceptorTask }, TimeSpan.FromSeconds(40));
            listener.Stop();
            return outcome;
        }

        private static void LinkEstablisherTests()
        {
            Section("Link establishment (full sequence)");

            DeviceInfo phoneInfo = BuildInfo(PhoneDeviceId, "WP81");
            DeviceInfo desktopInfo = BuildInfo(DesktopDeviceId, "G14");
            TlsIdentity phoneIdentity = CertificateProvider.CreateIdentity(PhoneDeviceId);
            TlsIdentity desktopIdentity = CertificateProvider.CreateIdentity(DesktopDeviceId);

            // What the phone "heard" over UDP from the desktop.
            NetworkPacket discovery = desktopInfo.ToUdpDiscoveryPacket(1716);

            TlsPeerPolicy dialerPolicy = new TlsPeerPolicy();
            dialerPolicy.ExpectedDeviceId = DesktopDeviceId;
            TlsPeerPolicy acceptorPolicy = new TlsPeerPolicy();

            LinkOutcome link = RunLink(phoneInfo, desktopInfo, phoneIdentity, desktopIdentity,
                discovery, dialerPolicy, acceptorPolicy);

            if (link.Error != null)
            {
                Check("link establishes end to end", false, link.Error.ToString());
                return;
            }

            Check("dialer link established", link.Dialer != null && link.Dialer.Success,
                link.Dialer == null ? "null" : link.Dialer.Failure);
            Check("acceptor link established", link.Acceptor != null && link.Acceptor.Success,
                link.Acceptor == null ? "null" : link.Acceptor.Failure);

            if (link.Dialer == null || !link.Dialer.Success ||
                link.Acceptor == null || !link.Acceptor.Success)
            {
                return;
            }

            CheckEqual("dialer learned the peer id", DesktopDeviceId, link.Dialer.Peer.Id);
            CheckEqual("acceptor learned the peer id", PhoneDeviceId, link.Acceptor.Peer.Id);
            CheckEqual("dialer learned the peer name", "G14", link.Dialer.Peer.Name);
            CheckEqual("acceptor learned the peer name", "WP81", link.Acceptor.Peer.Name);
            Check("peer capabilities parsed (dialer side)",
                link.Dialer.Peer.Supports("kdeconnect.ping"), "no ping capability");
            // Supports() answers "may I send this type TO the peer", i.e. it
            // reads the peer's incoming list. The acceptor parsed the phone's
            // identity, so it must see what the phone accepts.
            Check("peer capabilities parsed (acceptor side)",
                link.Acceptor.Peer.Supports("kdeconnect.ping"), "phone does not accept ping");
            Check("acceptor does not claim a type the phone only sends",
                !link.Acceptor.Peer.Supports("kdeconnect.mpris.request"),
                "mpris.request wrongly listed as incoming");
            Check("certificate evidence carried through",
                link.Dialer.Tls != null && link.Dialer.Tls.PeerCertificateDer != null, "missing");

            // A plugin packet must be readable straight after the handshake.
            NetworkPacket ping = new NetworkPacket(ProtocolConstants.PacketTypePing);
            ping.Id = 4242;
            ping.SetString("message", "after-link");
            byte[] wire = ping.Serialize();
            link.Dialer.Session.ApplicationData.Write(wire, 0, wire.Length);
            link.Dialer.Session.ApplicationData.Flush();

            string received = link.Acceptor.Reader.ReadLineAsync(
                TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult();
            NetworkPacket readBack = NetworkPacket.Unserialize(received);
            CheckEqual("packet type after link", ProtocolConstants.PacketTypePing, readBack.Type);
            CheckEqual("packet id after link", 4242L, readBack.Id);
            CheckEqual("packet body after link", "after-link", readBack.GetString("message", null));

            link.Dialer.Session.Close();
            link.Acceptor.Session.Close();

            // ---- rejection paths ------------------------------------------ //

            // Inbound connection addressed to somebody else.
            LinkOutcome wrongTarget = RunLink(phoneInfo, desktopInfo, phoneIdentity, desktopIdentity,
                discovery, dialerPolicy, acceptorPolicy);
            Check("re-run is reproducible",
                wrongTarget.Dialer != null && wrongTarget.Dialer.Success, "flaky");

            DeviceInfo otherInfo = BuildInfo("ffffffffffffffffffffffffffffffff", "Other");
            TlsIdentity otherIdentity = CertificateProvider.CreateIdentity(otherInfo.Id);
            TlsPeerPolicy mismatchPolicy = new TlsPeerPolicy();

            // The dialer claims to be targeting the wrong device id.
            TlsPeerPolicy badTargetPolicy = new TlsPeerPolicy();
            badTargetPolicy.ExpectedDeviceId = "ffffffffffffffffffffffffffffffff";
            LinkOutcome badTarget = RunLink(phoneInfo, desktopInfo, phoneIdentity, desktopIdentity,
                discovery, badTargetPolicy, mismatchPolicy);
            Check("link refused when addressed to another device",
                badTarget.Acceptor != null && !badTarget.Acceptor.Success &&
                badTarget.Acceptor.Failure != null &&
                badTarget.Acceptor.Failure.Contains("another device"),
                badTarget.Acceptor == null ? "null" : badTarget.Acceptor.Failure);

            // A pinned certificate that does not match what the peer presents.
            TlsPeerPolicy pinPolicy = new TlsPeerPolicy();
            pinPolicy.ExpectedDeviceId = DesktopDeviceId;
            pinPolicy.PinnedCertificateDer = phoneIdentity.CertificateDer; // wrong one on purpose
            TlsPeerPolicy pinnedAcceptor = new TlsPeerPolicy();
            pinnedAcceptor.PinnedCertificateDer = phoneIdentity.CertificateDer;
            LinkOutcome changed = RunLink(phoneInfo, desktopInfo, phoneIdentity, desktopIdentity,
                discovery, pinPolicy, pinnedAcceptor);
            Check("link refused when the pinned certificate differs",
                changed.Dialer != null && !changed.Dialer.Success &&
                changed.Dialer.Failure != null &&
                changed.Dialer.Failure.Contains("certificate changed"),
                changed.Dialer == null ? "null" : changed.Dialer.Failure);
        }
    }



    internal static class TestExtensions
    {
        /// <summary>Shallow copy helper used by the handshake tests.</summary>
        public static NetworkPacket CloneForTest(this NetworkPacket np)
        {
            return NetworkPacket.Unserialize(Encoding.UTF8.GetString(np.Serialize()));
        }
    }
}
