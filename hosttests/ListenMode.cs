using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using KDEConnect.WP81.Security;
using KDEConnect.WP81.Transport;

/// <summary>
/// Runs the phone's TLS *server* role on a local port so a real desktop client
/// can be pointed at it, e.g.
///
///     dotnet run --no-build -- --listen 17199
///     openssl s_client -connect 127.0.0.1:17199
///
/// This exists because the phone reported
/// "TLS server handshake failed at stage 'created': internal_error(80)" against a
/// real desktop, and "stage created" means it died inside ClientHello processing
/// before any of our callbacks ran. Reproducing that here means iterating in
/// seconds against the same OpenSSL that Qt links, instead of once per build on
/// a phone.
/// </summary>
internal static class ListenMode
{
    public static bool TryRun(string[] args)
    {
        if (args == null || args.Length < 2)
        {
            return false;
        }
        if (!string.Equals(args[0], "--listen", StringComparison.Ordinal))
        {
            return false;
        }

        int port;
        if (!int.TryParse(args[1], out port))
        {
            Console.WriteLine("usage: --listen <port>");
            return true;
        }

        Run(port);
        return true;
    }

    private static void Run(int port)
    {
        // Must satisfy the device id rule: 32-38 chars of [a-zA-Z0-9_-].
        string deviceId = Guid.NewGuid().ToString("N");
        TlsIdentity identity = CertificateProvider.CreateIdentity(deviceId);
        BouncyCastleTlsBackend backend = new BouncyCastleTlsBackend();

        TcpListener listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine("listening on 127.0.0.1:" + port.ToString() + " (deviceId " + deviceId + ")");

        TcpClient client = listener.AcceptTcpClient();
        Console.WriteLine("connection accepted");

        RawChannel channel = new RawChannel(client.GetStream(), client, "127.0.0.1", port);
        try
        {
            ITlsSession session = backend.HandshakeAsServerAsync(
                channel, identity, new TlsPeerPolicy(),
                TimeSpan.FromSeconds(20), CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("HANDSHAKE OK");
            try
            {
                session.ApplicationData.WriteByte(0x41);
                session.ApplicationData.Flush();
            }
            catch (Exception writeError)
            {
                Console.WriteLine("write after handshake failed: " + writeError.Message);
            }
            session.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine("HANDSHAKE FAILED: " + ex.GetType().Name + ": " + ex.Message);
            Exception inner = ex.InnerException;
            while (inner != null)
            {
                Console.WriteLine("  caused by " + inner.GetType().Name + ": " + inner.Message);
                inner = inner.InnerException;
            }
        }
        finally
        {
            try
            {
                listener.Stop();
            }
            catch (Exception)
            {
            }
        }
    }
}
