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
            // Interop mode: stand up the phone's TLS server role for a real
            // client to connect to, then exit without running the suite.
            if (ListenMode.TryRun(args))
            {
                return 0;
            }

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
            AddressNormalisationTests();
            StorageListingTests();
            RecordingStreamTests();
            CapabilityTests();
            RateLimitTests();
            RedactorTests();
            GoldenFixtureTests();
            TlsTests();
            LineReaderTests();
            LinkEstablisherTests();
            PinLookupTests();
            DeviceStoreTests();
            PluginPacketTests();
            AddressValidationTests();
            IdentityLifecycleTests();
            RedactionTests();
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

        private static void CheckTrue(string name, bool condition)
        {
            Check(name, condition, "expected true");
        }

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


        // ------------------------------------------------------------------ //
        //  Persistence: own identity and paired peers must survive a restart.
        // ------------------------------------------------------------------ //

        private static void DeviceStoreTests()
        {
            Section("DeviceStore (persistence)");

            MemoryBlobStore blobs = new MemoryBlobStore();
            DeviceStore store = new DeviceStore(blobs);

            // Nothing stored yet -> caller must generate one.
            Check("identity absent before first run",
                store.LoadOrCreateIdentity(PhoneDeviceId, "WP81") == null, "returned one");

            DeviceStore.OwnIdentity own = new DeviceStore.OwnIdentity();
            own.DeviceId = PhoneDeviceId;
            own.DeviceName = "WP81";
            own.CertificateDer = CertificateProvider.CreateIdentity(PhoneDeviceId).CertificateDer;
            own.PrivateKeyDer = CertificateProvider.CreateIdentity(PhoneDeviceId).PrivateKeyDer;
            store.SaveIdentity(own);

            DeviceStore.OwnIdentity reloaded = store.LoadOrCreateIdentity(PhoneDeviceId, "WP81");
            Check("identity survives a reload", reloaded != null, "null");
            CheckEqual("reloaded device id", PhoneDeviceId, reloaded.DeviceId);
            Check("reloaded certificate identical",
                BytesEqual(own.CertificateDer, reloaded.CertificateDer), "different");
            Check("reloaded private key identical",
                BytesEqual(own.PrivateKeyDer, reloaded.PrivateKeyDer), "different");

            // A stored identity for a DIFFERENT device id must not be reused:
            // its CN would not match and the peer would reject the handshake.
            DeviceStore otherStore = new DeviceStore(new MemoryBlobStore());
            DeviceStore.OwnIdentity foreign = new DeviceStore.OwnIdentity();
            foreign.DeviceId = DesktopDeviceId;
            foreign.CertificateDer = own.CertificateDer;
            foreign.PrivateKeyDer = own.PrivateKeyDer;
            otherStore.SaveIdentity(foreign);
            Check("identity for another device id is not reused",
                otherStore.LoadOrCreateIdentity(PhoneDeviceId, "WP81") == null, "reused");

            // Corrupt identity must degrade to "regenerate", never throw.
            MemoryBlobStore corruptBlobs = new MemoryBlobStore();
            corruptBlobs.Write(DeviceStore.IdentityFileName,
                System.Text.Encoding.UTF8.GetBytes("{ not json at all"));
            DeviceStore corrupt = new DeviceStore(corruptBlobs);
            Check("corrupt identity degrades gracefully",
                corrupt.LoadOrCreateIdentity(PhoneDeviceId, "WP81") == null, "did not degrade");

            // Peers: insert, find, replace without duplicating.
            DeviceRecord desktop = new DeviceRecord();
            desktop.DeviceId = DesktopDeviceId;
            desktop.DeviceName = "G14";
            desktop.IsPaired = true;
            desktop.PinnedCertificateDer = new byte[] { 1, 2, 3 };
            desktop.IncomingCapabilities.Add("kdeconnect.ping");
            store.UpsertPeer(desktop);

            Check("peer found after insert", store.FindPeer(DesktopDeviceId) != null, "missing");
            Check("unknown peer not found", store.FindPeer(PhoneDeviceId) == null, "found");

            desktop.DeviceName = "G14 (renamed)";
            store.UpsertPeer(desktop);
            CheckEqual("upsert replaces instead of duplicating", 1, store.LoadPeers().Count);
            CheckEqual("upsert keeps the new name", "G14 (renamed)",
                store.FindPeer(DesktopDeviceId).DeviceName);

            // A fresh store instance over the same blobs sees the same peers:
            // this is what makes pairing survive an app restart.
            DeviceStore reopened = new DeviceStore(blobs);
            DeviceRecord persisted = reopened.FindPeer(DesktopDeviceId);
            Check("peers survive a reopen", persisted != null, "missing");
            Check("pin survives a reopen", persisted != null &&
                persisted.PinnedCertificateDer != null &&
                persisted.PinnedCertificateDer.Length == 3, "pin lost");
            Check("capabilities survive a reopen", persisted != null &&
                persisted.Supports("kdeconnect.ping"), "capabilities lost");

            // Unpairing must destroy the trust record.
            Check("remove returns true", reopened.RemovePeer(DesktopDeviceId), "false");
            Check("peer gone after remove", reopened.FindPeer(DesktopDeviceId) == null, "still there");
            Check("pin gone after remove", reopened.FindPeer(DesktopDeviceId) == null, "pin retained");
            Check("remove of unknown peer returns false", !reopened.RemovePeer("x"), "true");

            // Corrupt peer file degrades to an empty list instead of throwing.
            MemoryBlobStore badPeers = new MemoryBlobStore();
            badPeers.Write(DeviceStore.PeersFileName,
                System.Text.Encoding.UTF8.GetBytes("[[[["));
            DeviceStore degraded = new DeviceStore(badPeers);
            CheckEqual("corrupt peer file degrades to empty", 0, degraded.LoadPeers().Count);

            // Invalid device ids are refused on write.
            CheckThrows<ArgumentException>("invalid device id refused on upsert", delegate
            {
                DeviceRecord bad = new DeviceRecord();
                bad.DeviceId = "nope";
                store.UpsertPeer(bad);
            });

            // Clear wipes both files.
            reopened.Clear();
            Check("clear removes identity", reopened.LoadOrCreateIdentity(PhoneDeviceId, "WP81") == null,
                "identity survived");
            CheckEqual("clear removes peers", 0, reopened.LoadPeers().Count);
        }



        // ------------------------------------------------------------------ //
        //  Startup identity lifecycle and log-safety helpers.
        // ------------------------------------------------------------------ //

        private static void IdentityLifecycleTests()
        {
            Section("Identity lifecycle (startup correctness)");

            MemoryBlobStore blobs = new MemoryBlobStore();
            DeviceStore store = new DeviceStore(blobs);

            // Nothing stored: startup must create, not resurrect.
            Check("fresh install has no identity", store.LoadIdentity() == null, "found one");

            string id = Sanitize.GenerateDeviceId();
            Check("generated device id is valid", Sanitize.IsValidDeviceId(id), id);
            CheckEqual("generated device id length", 32, id.Length);

            TlsIdentity minted = CertificateProvider.CreateIdentity(id);
            DeviceStore.OwnIdentity own = new DeviceStore.OwnIdentity();
            own.DeviceId = id;
            own.DeviceName = "WP81";
            own.CertificateDer = minted.CertificateDer;
            own.PrivateKeyDer = minted.PrivateKeyDer;
            store.SaveIdentity(own);

            // THE regression this guards: a restart must reuse the same id.
            DeviceStore restarted = new DeviceStore(blobs);
            DeviceStore.OwnIdentity reloaded = restarted.LoadIdentity();
            Check("identity loads on restart", reloaded != null, "null");
            CheckEqual("device id stable across restart", id, reloaded.DeviceId);
            Check("certificate stable across restart",
                BytesEqual(minted.CertificateDer, reloaded.CertificateDer), "different");
            CheckEqual("name restored", "WP81", reloaded.DeviceName);

            // A second call must be stable too (cache path).
            CheckEqual("id stable on second load", id, restarted.LoadIdentity().DeviceId);

            // Corrupt / partial files must not resurrect a broken identity.
            MemoryBlobStore bad = new MemoryBlobStore();
            bad.Write(DeviceStore.IdentityFileName,
                System.Text.Encoding.UTF8.GetBytes("{\"deviceId\":\"short\",\"certificateDer\":\"AAAA\"}"));
            Check("invalid stored identity rejected", new DeviceStore(bad).LoadIdentity() == null,
                "accepted a broken identity");

            // Generated ids must all satisfy the wire rule (32-38 chars).
            bool allValid = true;
            for (int i = 0; i < 200; i++)
            {
                if (!Sanitize.IsValidDeviceId(Sanitize.GenerateDeviceId()))
                {
                    allValid = false;
                }
            }
            Check("200 generated ids are all wire-valid", allValid, "some were invalid");
        }

        private static void RedactionTests()
        {
            Section("Redactor (log safety)");

            CheckEqual("null device id redacts cleanly", "(none)", Redactor.RedactDeviceId(null));
            CheckEqual("empty device id redacts cleanly", "(none)", Redactor.RedactDeviceId(""));
            CheckEqual("short id passes through", "abc", Redactor.RedactDeviceId("abc"));
            CheckEqual("long id is truncated", "abcdef...", Redactor.RedactDeviceId("abcdef0123456789"));

            byte[] cert = CertificateProvider.CreateIdentity(PhoneDeviceId).CertificateDer;
            string fp = Redactor.CertificateFingerprint(cert, 8);
            Check("certificate fingerprint is non-empty", !string.IsNullOrEmpty(fp), "empty");
            Check("certificate fingerprint differs per cert",
                fp != Redactor.CertificateFingerprint(desktopCertForTests(), 8), "identical");

            // Packet descriptions must never echo a private-looking body verbatim.
            NetworkPacket np = new NetworkPacket("kdeconnect.clipboard");
            np.SetString("password", "hunter2");
            string described = Redactor.DescribePacket(np);
            Check("sensitive packet body is not echoed",
                described.IndexOf("hunter2", StringComparison.Ordinal) < 0, described);
        }

        private static byte[] desktopCertForTests()
        {
            return CertificateProvider.CreateIdentity(DesktopDeviceId).CertificateDer;
        }



        // ------------------------------------------------------------------ //
        //  Pin lookup: inbound links must be verified against the stored pin.
        // ------------------------------------------------------------------ //

        private static void PinLookupTests()
        {
            Section("Pinning via lookup (inbound)");

            DeviceInfo phoneInfo = BuildInfo(PhoneDeviceId, "WP81");
            DeviceInfo desktopInfo = BuildInfo(DesktopDeviceId, "G14");
            TlsIdentity phoneIdentity = CertificateProvider.CreateIdentity(PhoneDeviceId);
            TlsIdentity desktopIdentity = CertificateProvider.CreateIdentity(DesktopDeviceId);
            NetworkPacket discovery = desktopInfo.ToUdpDiscoveryPacket(1716);

            // The acceptor learns the peer id only after the plaintext packet,
            // so a real pairing pin has to be resolved by callback.
            TlsPeerPolicy dialerPolicy = new TlsPeerPolicy();
            dialerPolicy.ExpectedDeviceId = DesktopDeviceId;

            TlsPeerPolicy pairedAcceptor = new TlsPeerPolicy();
            pairedAcceptor.PinLookup = delegate(string peerId)
            {
                return string.Equals(peerId, PhoneDeviceId, StringComparison.Ordinal)
                    ? phoneIdentity.CertificateDer
                    : null;
            };

            LinkOutcome good = RunLink(phoneInfo, desktopInfo, phoneIdentity, desktopIdentity,
                discovery, dialerPolicy, pairedAcceptor);

            Check("pinned inbound link establishes",
                good.Acceptor != null && good.Acceptor.Success,
                good.Acceptor == null ? "null" : good.Acceptor.Failure);
            Check("inbound peer is marked trusted when the pin matches",
                good.Acceptor != null && good.Acceptor.Success &&
                good.Acceptor.Context != null && good.Acceptor.Context.IsTrustedPeer,
                "not trusted");
            if (good.Acceptor != null && good.Acceptor.Session != null) good.Acceptor.Session.Close();
            if (good.Dialer != null && good.Dialer.Session != null) good.Dialer.Session.Close();

            // A pin that resolves to the WRONG certificate must be refused:
            // this is the impostor case the whole pinning scheme exists for.
            TlsPeerPolicy wrongPinAcceptor = new TlsPeerPolicy();
            wrongPinAcceptor.PinLookup = delegate(string peerId)
            {
                return desktopIdentity.CertificateDer; // wrong key on purpose
            };

            LinkOutcome bad = RunLink(phoneInfo, desktopInfo, phoneIdentity, desktopIdentity,
                discovery, dialerPolicy, wrongPinAcceptor);

            Check("inbound link refused when the resolved pin differs",
                bad.Acceptor != null && !bad.Acceptor.Success &&
                bad.Acceptor.Failure != null &&
                bad.Acceptor.Failure.Contains("certificate changed"),
                bad.Acceptor == null ? "null" :
                    (bad.Acceptor.Success ? "accepted an impostor" : bad.Acceptor.Failure));

            // An unpaired peer resolves to null and must still be admitted
            // (trust on first use, pending the operator's verification code).
            TlsPeerPolicy unpairedAcceptor = new TlsPeerPolicy();
            unpairedAcceptor.PinLookup = delegate(string peerId) { return null; };

            LinkOutcome tofu = RunLink(phoneInfo, desktopInfo, phoneIdentity, desktopIdentity,
                discovery, dialerPolicy, unpairedAcceptor);

            Check("unpaired inbound peer admitted as untrusted",
                tofu.Acceptor != null && tofu.Acceptor.Success &&
                tofu.Acceptor.Context != null && !tofu.Acceptor.Context.IsTrustedPeer,
                tofu.Acceptor == null ? "null" : tofu.Acceptor.Failure);
            if (tofu.Acceptor != null && tofu.Acceptor.Session != null) tofu.Acceptor.Session.Close();
            if (tofu.Dialer != null && tofu.Dialer.Session != null) tofu.Dialer.Session.Close();
        }



        // ------------------------------------------------------------------ //
        //  Plugin wire format. The plugin classes themselves only wrap these.
        // ------------------------------------------------------------------ //

        private static NetworkPacket RoundTrip(NetworkPacket packet)
        {
            string frame = System.Text.Encoding.UTF8.GetString(packet.Serialize(), 0,
                packet.Serialize().Length);
            return NetworkPacket.Unserialize(frame);
        }

        private static void PluginPacketTests()
        {
            Section("Plugin packets - battery");

            NetworkPacket bat = PluginPackets.BuildBattery(57, true);
            CheckEqual("battery type", PluginPackets.Battery, bat.Type);
            CheckEqual("battery charge", 57, bat.GetInt("currentCharge", -1));
            CheckTrue("battery charging flag", bat.GetBool("isCharging", false));

            // Unknown charging state must not be sent as false.
            NetworkPacket batUnknown = PluginPackets.BuildBattery(42, (bool?)null);
            Check("battery omits isCharging when unknown", !batUnknown.Has("isCharging"), "sent a guess");

            CheckEqual("battery clamps high", 100,
                PluginPackets.BuildBattery(140, (bool?)null).GetInt("currentCharge", -1));
            CheckEqual("battery clamps low", 0,
                PluginPackets.BuildBattery(-9, (bool?)null).GetInt("currentCharge", -1));

            BatteryState parsed = PluginPackets.ParseBattery(RoundTrip(bat));
            Check("battery round-trips through serialisation", parsed != null, "null");
            CheckEqual("battery parsed charge", 57, parsed.CurrentCharge);
            CheckTrue("battery parsed charging", parsed.IsCharging);
            CheckTrue("battery marks charge present", parsed.HasCharge);
            CheckEqual("battery display", "57% (charging)", parsed.DisplayText);

            Check("battery rejects other packet types",
                PluginPackets.ParseBattery(new NetworkPacket(ProtocolConstants.PacketTypePing)) == null, "accepted");

            Section("Plugin packets - clipboard and ping");

            NetworkPacket clip = PluginPackets.BuildClipboard("hello world", 1700000000000L);
            CheckEqual("clipboard type", PluginPackets.Clipboard, clip.Type);
            NetworkPacket clipBack = RoundTrip(clip);
            CheckEqual("clipboard content survives", "hello world", clipBack.GetString("content", null));
            CheckEqual("clipboard timestamp survives", 1700000000000L, clipBack.GetLong("timestamp", 0));

            Section("Plugin packets - media control");

            NetworkPacket play = PluginPackets.BuildMprisAction("player1", PluginPackets.MprisAction.Play);
            CheckEqual("mpris request type", PluginPackets.MprisRequest, play.Type);
            CheckEqual("mpris action", "Play", play.GetString("action", null));
            CheckEqual("mpris player", "player1", play.GetString("player", null));
            CheckEqual("mpris seek volume clamps", 100,
                PluginPackets.BuildMprisSetVolume(null, 250).GetInt("setVolume", -1));

            // State parse, as the desktop sends it.
            NetworkPacket state = new NetworkPacket(PluginPackets.MprisState);
            state.SetString("player", "spotify");
            state.SetString("title", "Song");
            state.SetString("artist", "Artist");
            state.SetString("album", "Album");
            state.SetBool("isPlaying", true);
            state.SetLong("length", 215000L);
            state.SetLong("pos", 65000L);
            state.SetInt("volume", 60);
            state.SetBool("canPlay", true);
            state.SetBool("canGoNext", true);

            MprisState ms = MprisState.FromPacket(RoundTrip(state));
            Check("mpris state parses", ms != null, "null");
            CheckEqual("mpris title", "Song", ms.Title);
            CheckEqual("mpris artist", "Artist", ms.Artist);
            CheckTrue("mpris playing", ms.IsPlaying);
            CheckEqual("mpris length", 215000L, ms.Length);
            CheckTrue("mpris can go next", ms.CanGoNext);
            CheckEqual("mpris display line", "Song — Artist", ms.DisplayLine);
            CheckEqual("mpris position line", "1:05 / 3:35", ms.PositionLine);

            Check("mpris rejects wrong type",
                MprisState.FromPacket(new NetworkPacket(PluginPackets.Battery)) == null, "accepted");
            Check("mpris rejects an empty state packet",
                MprisState.FromPacket(new NetworkPacket(PluginPackets.MprisState)) == null, "accepted");

            MprisState noMedia = new MprisState();
            CheckEqual("mpris empty display", "(no media)", noMedia.DisplayLine);
            CheckEqual("mpris formats zero", "0:00", MprisState.FormatMilliseconds(0));
            CheckEqual("mpris formats negative", "0:00", MprisState.FormatMilliseconds(-5));

            Section("Plugin packets - touchpad and presenter");

            NetworkPacket mv = PluginPackets.BuildMouseMove(12.5, -3.25);
            CheckEqual("mousepad type", PluginPackets.MousepadRequest, mv.Type);
            NetworkPacket mvBack = RoundTrip(mv);
            Check("mouse dx survives", Math.Abs(mvBack.GetDouble("dx", 0) - 12.5) < 0.0001, "lost");
            Check("mouse dy survives", Math.Abs(mvBack.GetDouble("dy", 0) + 3.25) < 0.0001, "lost");
            Check("move is not flagged as scroll", !mv.Has("scroll"), "flagged");

            NetworkPacket sc = PluginPackets.BuildMouseScroll(0, 1);
            CheckTrue("scroll flagged", sc.GetBool("scroll", false));

            NetworkPacket clk = PluginPackets.BuildMouseClick(PluginPackets.MouseButton.Right);
            CheckTrue("right click flag", clk.GetBool("rightclick", false));

            NetworkPacket key = PluginPackets.BuildKey("a", null, false, true, false, false);
            CheckEqual("key char", "a", key.GetString("key", null));
            CheckTrue("key ctrl modifier", key.GetBool("ctrl", false));
            Check("key omits unset modifiers", !key.Has("alt") && !key.Has("shift"), "sent junk");

            NetworkPacket special = PluginPackets.BuildKey(null, PluginPackets.SpecialKey.Enter, false, false, false, false);
            CheckEqual("special key", "Enter", special.GetString("specialKey", null));
            Check("special key omits key field", !special.Has("key"), "sent both");

            NetworkPacket pres = PluginPackets.BuildPresenterMove(0.1f, -0.2f);
            CheckEqual("presenter type", PluginPackets.Presenter, pres.Type);
            CheckTrue("presenter stop flag",
                PluginPackets.BuildPresenterStop().GetBool("stop", false));

            Section("Plugin packets - system volume");

            NetworkPacket req = PluginPackets.BuildSystemVolumeRequest();
            CheckEqual("volume request type", PluginPackets.SystemVolumeRequest, req.Type);

            NetworkPacket setv = PluginPackets.BuildSystemVolumeSet("PulseAudio/Sink/x", 42);
            CheckEqual("volume set name", "PulseAudio/Sink/x", setv.GetString("name", null));
            CheckEqual("volume set clamps", 100,
                PluginPackets.BuildSystemVolumeSet("s", 500).GetInt("volume", -1));

            NetworkPacket mute = PluginPackets.BuildSystemVolumeMute("s", true);
            CheckTrue("mute flag", mute.GetBool("muted", false));
            Check("mute omits volume", !mute.Has("volume"), "sent volume too");

            // sinkList array shape
            NetworkPacket sinks = new NetworkPacket(PluginPackets.SystemVolumeState);
            JArray list = new JArray();
            JObject s1 = new JObject();
            s1.Set("name", new JString("alsa_output.pci-0000_00_1f.3"));
            s1.Set("volume", JNumber.From(35));
            s1.Set("muted", new JBool(false));
            JObject s2 = new JObject();
            s2.Set("name", new JString("PulseAudio/Sink/hdmi"));
            s2.Set("muted", new JBool(true));
            list.Add(s1);
            list.Add(s2);
            sinks.Body.Set("sinkList", list);

            System.Collections.Generic.List<VolumeSink> parsedSinks =
                VolumeSink.ListFromPacket(RoundTrip(sinks));
            CheckEqual("sink list parsed", 2, parsedSinks.Count);
            CheckEqual("sink 1 volume", 35, parsedSinks[0].Volume);
            CheckTrue("sink 2 muted", parsedSinks[1].Muted);
            CheckEqual("sink display name trims prefix", "hdmi", parsedSinks[1].DisplayName);

            // single-sink update shape
            NetworkPacket one = new NetworkPacket(PluginPackets.SystemVolumeState);
            one.SetString("name", "PulseAudio/Sink/a");
            one.SetInt("volume", 70);
            System.Collections.Generic.List<VolumeSink> oneSink = VolumeSink.ListFromPacket(RoundTrip(one));
            CheckEqual("single sink update parsed", 1, oneSink.Count);
            CheckEqual("single sink volume", 70, oneSink[0].Volume);

            CheckEqual("volume rejects wrong type", 0,
                VolumeSink.ListFromPacket(new NetworkPacket(PluginPackets.Battery)).Count);

            Section("Plugin packets - share and find my phone");

            NetworkPacket text = PluginPackets.BuildShareText("https://example.org");
            CheckEqual("share type", PluginPackets.ShareRequest, text.Type);
            CheckEqual("share text survives", "https://example.org",
                RoundTrip(text).GetString("text", null));

            NetworkPacket url = PluginPackets.BuildShareUrl("https://example.org/x");
            CheckEqual("share url field", "https://example.org/x", url.GetString("url", null));

            CheckEqual("find my phone type", PluginPackets.FindMyPhoneRequest,
                PluginPackets.BuildFindMyPhone().Type);
        }



        // ------------------------------------------------------------------ //
        //  Manual "add device by IP" relies on address validation.
        // ------------------------------------------------------------------ //

        private static void AddressValidationTests()
        {
            Section("IPv4 validation (manual entry)");

            CheckTrue("accepts 192.168.1.42", Sanitize.IsIPv4("192.168.1.42"));
            CheckTrue("accepts 0.0.0.0", Sanitize.IsIPv4("0.0.0.0"));
            CheckTrue("accepts 255.255.255.255", Sanitize.IsIPv4("255.255.255.255"));
            CheckTrue("accepts 10.0.0.1", Sanitize.IsIPv4("10.0.0.1"));

            Check("rejects null", !Sanitize.IsIPv4(null), "accepted");
            Check("rejects empty", !Sanitize.IsIPv4(""), "accepted");
            Check("rejects three octets", !Sanitize.IsIPv4("192.168.1"), "accepted");
            Check("rejects five octets", !Sanitize.IsIPv4("1.2.3.4.5"), "accepted");
            Check("rejects octet over 255", !Sanitize.IsIPv4("192.168.1.256"), "accepted");
            Check("rejects negative octet", !Sanitize.IsIPv4("192.168.1.-1"), "accepted");
            Check("rejects letters", !Sanitize.IsIPv4("192.168.1.a"), "accepted");
            Check("rejects hostname", !Sanitize.IsIPv4("g14.local"), "accepted");
            Check("rejects empty octet", !Sanitize.IsIPv4("192.168..1"), "accepted");
            Check("rejects untrimmed input", !Sanitize.IsIPv4(" 192.168.1.1 "), "accepted spaces");
        }

        // ------------------------------------------------------------------
        // Address normalisation: WinRT reports a dual-mode socket's remote
        // address in whatever form the stack chose, and an address that cannot
        // be dialled back produces the worst kind of bug - discovery works, the
        // device appears in the list, and every connection attempt fails.
        // ------------------------------------------------------------------
        private static void RecordingStreamTests()
        {
            Section("handshake recording");

            byte[] payload = new byte[100];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)i;
            }

            System.IO.MemoryStream inner = new System.IO.MemoryStream(payload);
            RecordingReadStream recorder = new RecordingReadStream(inner, 32);

            byte[] buffer = new byte[64];
            int first = recorder.Read(buffer, 0, 64);
            CheckEqual("reads through to the inner stream", 64L, first);
            CheckEqual("total bytes counted", 64L, recorder.TotalBytesRead);
            CheckTrue("capture stops at the limit", recorder.Captured.Length <= 32);
            CheckEqual("capture keeps the first byte", 0L, recorder.Captured[0]);
            CheckEqual("capture keeps the second byte", 1L, recorder.Captured[1]);

            string described = recorder.Describe();
            CheckTrue("description reports the byte count", described.Contains("64 byte"));
            CheckTrue("description shows hex", described.Contains("00 01 02"));

            System.IO.MemoryStream empty = new System.IO.MemoryStream(new byte[0]);
            RecordingReadStream silent = new RecordingReadStream(empty, 32);
            CheckEqual("empty stream reads zero", 0L, silent.Read(buffer, 0, 64));
            CheckTrue("zero bytes is called out as no ClientHello",
                silent.Describe().Contains("0 bytes"));
        }

        private static void StorageListingTests()
        {
            Section("storage listing");

            System.Collections.Generic.List<StorageEntry> input =
                new System.Collections.Generic.List<StorageEntry>();
            input.Add(new StorageEntry { Name = "b.txt", IsFolder = false, SizeBytes = 2048 });
            input.Add(new StorageEntry { Name = "Zebra", IsFolder = true });
            input.Add(new StorageEntry { Name = "a.txt", IsFolder = false, SizeBytes = 10 });
            input.Add(new StorageEntry { Name = "apple", IsFolder = true });
            input.Add(null);

            System.Collections.Generic.List<StorageEntry> ordered = StorageListing.Order(input);
            CheckEqual("nulls dropped from the listing", 4L, ordered.Count);
            CheckEqual("folders sort before files, case-insensitively", "apple", ordered[0].Name);
            CheckEqual("second folder", "Zebra", ordered[1].Name);
            CheckEqual("first file", "a.txt", ordered[2].Name);
            CheckEqual("last file", "b.txt", ordered[3].Name);

            CheckEqual("bytes", "512 B", StorageListing.FormatSize(512));
            CheckEqual("exactly one kilobyte", "1.0 KB", StorageListing.FormatSize(1024));
            CheckTrue("megabytes are labelled", StorageListing.FormatSize(5 * 1024 * 1024).EndsWith(" MB"));
            CheckTrue("gigabytes are labelled",
                StorageListing.FormatSize(3L * 1024L * 1024L * 1024L).EndsWith(" GB"));
            CheckEqual("negative size is unknown", "?", StorageListing.FormatSize(-1));

            CheckEqual("folder subtitle", "folder",
                StorageListing.Subtitle(new StorageEntry { IsFolder = true }));
            CheckEqual("problem outranks other detail", "denied",
                StorageListing.Subtitle(new StorageEntry { Problem = "denied" }));
            CheckEqual("file subtitle is just its size when undated", "1.0 KB",
                StorageListing.Subtitle(new StorageEntry { SizeBytes = 1024 }));

        }

        private static void AddressNormalisationTests()
        {
            Section("address normalisation");

            CheckEqual("ipv4-mapped becomes ipv4",
                "192.168.0.5", Sanitize.NormalizeHostAddress("::ffff:192.168.0.5"));
            CheckEqual("uppercase mapped prefix still unwrapped",
                "10.1.2.3", Sanitize.NormalizeHostAddress("::FFFF:10.1.2.3"));
            CheckEqual("brackets stripped",
                "10.0.0.2", Sanitize.NormalizeHostAddress("[::ffff:10.0.0.2]"));
            CheckEqual("plain ipv4 untouched",
                "192.168.1.42", Sanitize.NormalizeHostAddress("192.168.1.42"));
            CheckEqual("surrounding spaces trimmed",
                "192.168.1.9", Sanitize.NormalizeHostAddress("  192.168.1.9 "));
            CheckEqual("link-local with zone kept",
                "fe80::1%3", Sanitize.NormalizeHostAddress("fe80::1%3"));
            CheckEqual("empty stays empty",
                "", Sanitize.NormalizeHostAddress(""));
            CheckEqual("null becomes empty",
                "", Sanitize.NormalizeHostAddress(null));

            Check("ipv4 is not ipv6", !Sanitize.IsIPv6("192.168.0.5"), "reported as ipv6");
            Check("link-local is ipv6", Sanitize.IsIPv6("fe80::1"), "not reported as ipv6");
            Check("mapped is still ipv6 before normalisation",
                Sanitize.IsIPv6("::ffff:192.168.0.5"), "mapped not detected");
            Check("normalised mapped is not ipv6",
                !Sanitize.IsIPv6(Sanitize.NormalizeHostAddress("::ffff:192.168.0.5")),
                "unwrapped address still looks ipv6");
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
