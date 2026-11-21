using System;
using System.Collections.Generic;
using System.Text;

namespace KDEConnect.WP81.Core
{
    /// <summary>
    /// Byte-oriented persistence. Kept as an interface so the store can be
    /// tested on the host and backed by ApplicationData.LocalFolder on the
    /// phone, without Core depending on WinRT.
    /// </summary>
    public interface IBlobStore
    {
        byte[] Read(string name);
        void Write(string name, byte[] data);
        void Delete(string name);
        bool Exists(string name);
    }

    /// <summary>
    /// In-memory store used by tests (and as a null object).
    /// </summary>
    public sealed class MemoryBlobStore : IBlobStore
    {
        private readonly Dictionary<string, byte[]> _items =
            new Dictionary<string, byte[]>(StringComparer.Ordinal);

        public byte[] Read(string name)
        {
            byte[] data;
            return _items.TryGetValue(name, out data) ? data : null;
        }

        public void Write(string name, byte[] data)
        {
            _items[name] = data;
        }

        public void Delete(string name)
        {
            _items.Remove(name);
        }

        public bool Exists(string name)
        {
            return _items.ContainsKey(name);
        }
    }

    /// <summary>
    /// Own identity + paired/known peers, persisted as JSON.
    ///
    /// Two rules matter more than anything else here:
    ///  1. The private key never leaves this class and is never logged
    ///     (mission brief: no key in Git, logs, fixtures or release archives).
    ///  2. Unpairing must remove the pin so the old trust record can no longer
    ///     grant access (docs/protocol.md §5).
    /// </summary>
    public sealed class DeviceStore
    {
        public const string IdentityFileName = "own-identity.json";
        public const string PeersFileName = "peers.json";

        private readonly IBlobStore _store;
        private readonly object _gate = new object();
        private List<DeviceRecord> _peers;
        private OwnIdentity _own;

        public DeviceStore(IBlobStore store)
        {
            if (store == null)
            {
                throw new ArgumentNullException("store");
            }
            _store = store;
        }

        // ---- own identity --------------------------------------------------

        public sealed class OwnIdentity
        {
            public OwnIdentity()
            {
            }

            public string DeviceId { get; set; }
            public string DeviceName { get; set; }
            public byte[] CertificateDer { get; set; }
            public byte[] PrivateKeyDer { get; set; }
        }

        /// <summary>
        /// Loads the stored identity without being told the device id - the id
        /// comes from the file. This is the correct entry point at startup:
        /// the id must be stable across launches or every restart would mint a
        /// new certificate and silently drop every pairing.
        /// </summary>
        public OwnIdentity LoadIdentity()
        {
            lock (_gate)
            {
                if (_own != null)
                {
                    return _own;
                }

                byte[] raw = _store.Read(IdentityFileName);
                if (raw == null || raw.Length == 0)
                {
                    return null;
                }

                try
                {
                    JObject json = Json.Parse(Utf8(raw)) as JObject;
                    if (json == null)
                    {
                        return null;
                    }
                    string storedId = json.GetString("deviceId", string.Empty);
                    byte[] cert = Base64(json.GetString("certificateDer", null));
                    byte[] key = Base64(json.GetString("privateKeyDer", null));
                    if (!Sanitize.IsValidDeviceId(storedId) ||
                        cert == null || cert.Length == 0 ||
                        key == null || key.Length == 0)
                    {
                        return null;
                    }

                    _own = new OwnIdentity();
                    _own.DeviceId = storedId;
                    _own.DeviceName = json.GetString("deviceName", string.Empty);
                    _own.CertificateDer = cert;
                    _own.PrivateKeyDer = key;
                    return _own;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        public OwnIdentity LoadOrCreateIdentity(string deviceId, string deviceName)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                throw new ArgumentNullException("deviceId");
            }

            lock (_gate)
            {
                // The cache must obey the same rule as the file: an identity
                // minted for a different device id is not ours to reuse, or the
                // peer would reject the handshake on a CN mismatch.
                if (_own != null)
                {
                    if (string.Equals(_own.DeviceId, deviceId, StringComparison.Ordinal))
                    {
                        return _own;
                    }
                    _own = null;
                }

                byte[] raw = _store.Read(IdentityFileName);
                if (raw != null && raw.Length > 0)
                {
                    try
                    {
                        JObject json = Json.Parse(Utf8(raw)) as JObject;
                        if (json != null)
                        {
                            string storedId = json.GetString("deviceId", string.Empty);
                            byte[] cert = Base64(json.GetString("certificateDer", null));
                            byte[] key = Base64(json.GetString("privateKeyDer", null));

                            // An identity whose certificate does not match the
                            // current device id would be rejected by the peer
                            // (CN must equal deviceId), so it is regenerated
                            // rather than silently reused - the same rule the
                            // reference applies (SslHelper.kt:76-99).
                            if (cert != null && key != null && cert.Length > 0 && key.Length > 0 &&
                                string.Equals(storedId, deviceId, StringComparison.Ordinal))
                            {
                                _own = new OwnIdentity();
                                _own.DeviceId = storedId;
                                _own.DeviceName = deviceName;
                                _own.CertificateDer = cert;
                                _own.PrivateKeyDer = key;
                                return _own;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Corrupt identity: fall through and regenerate.
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// Persists a freshly generated identity. The caller has already
        /// verified that the certificate's CN matches <c>deviceId</c>.
        /// </summary>
        public void SaveIdentity(OwnIdentity identity)
        {
            if (identity == null)
            {
                throw new ArgumentNullException("identity");
            }
            if (identity.CertificateDer == null || identity.CertificateDer.Length == 0)
            {
                throw new ArgumentException("certificate is empty", "identity");
            }
            if (identity.PrivateKeyDer == null || identity.PrivateKeyDer.Length == 0)
            {
                throw new ArgumentException("private key is empty", "identity");
            }

            JObject json = new JObject();
            json.Set("deviceId", new JString(identity.DeviceId ?? string.Empty));
            json.Set("deviceName", new JString(identity.DeviceName ?? string.Empty));
            json.Set("certificateDer", new JString(ToBase64(identity.CertificateDer)));
            json.Set("privateKeyDer", new JString(ToBase64(identity.PrivateKeyDer)));

            lock (_gate)
            {
                _store.Write(IdentityFileName, Utf8(Json.Write(json)));
                _own = identity;
            }
        }

        // ---- peers ----------------------------------------------------------

        public List<DeviceRecord> LoadPeers()
        {
            lock (_gate)
            {
                if (_peers != null)
                {
                    return _peers;
                }

                _peers = new List<DeviceRecord>();
                byte[] raw = _store.Read(PeersFileName);
                if (raw == null || raw.Length == 0)
                {
                    return _peers;
                }

                try
                {
                    JObject json = Json.Parse(Utf8(raw)) as JObject;
                    if (json == null)
                    {
                        return _peers;
                    }
                    JArray array = json.GetArray("peers");
                    if (array == null)
                    {
                        return _peers;
                    }
                    for (int i = 0; i < array.Count; i++)
                    {
                        DeviceRecord record = DeviceRecord.FromJson(array[i] as JObject);
                        if (record != null)
                        {
                            _peers.Add(record);
                        }
                    }
                }
                catch (Exception)
                {
                    // A corrupt peer file must not stop the app from starting;
                    // it degrades to "no known devices" and can be re-paired.
                    _peers.Clear();
                }

                return _peers;
            }
        }

        public DeviceRecord FindPeer(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                return null;
            }
            List<DeviceRecord> peers = LoadPeers();
            lock (_gate)
            {
                for (int i = 0; i < peers.Count; i++)
                {
                    if (string.Equals(peers[i].DeviceId, deviceId, StringComparison.Ordinal))
                    {
                        return peers[i];
                    }
                }
            }
            return null;
        }

        /// <summary>Inserts or replaces a record, then persists immediately.</summary>
        public void UpsertPeer(DeviceRecord record)
        {
            if (record == null)
            {
                throw new ArgumentNullException("record");
            }
            if (!Sanitize.IsValidDeviceId(record.DeviceId))
            {
                throw new ArgumentException("invalid device id", "record");
            }

            lock (_gate)
            {
                List<DeviceRecord> peers = LoadPeers();
                for (int i = 0; i < peers.Count; i++)
                {
                    if (string.Equals(peers[i].DeviceId, record.DeviceId, StringComparison.Ordinal))
                    {
                        peers[i] = record;
                        PersistPeers(peers);
                        return;
                    }
                }
                peers.Add(record);
                PersistPeers(peers);
            }
        }

        /// <summary>
        /// Removes every trace of a peer: record, pin and capabilities. After
        /// this the old trust record can no longer grant access.
        /// </summary>
        public bool RemovePeer(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                return false;
            }

            lock (_gate)
            {
                List<DeviceRecord> peers = LoadPeers();
                bool removed = false;
                for (int i = peers.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(peers[i].DeviceId, deviceId, StringComparison.Ordinal))
                    {
                        peers.RemoveAt(i);
                        removed = true;
                    }
                }
                if (removed)
                {
                    PersistPeers(peers);
                }
                return removed;
            }
        }

        /// <summary>Deletes everything this app remembers.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _store.Delete(IdentityFileName);
                _store.Delete(PeersFileName);
                _peers = new List<DeviceRecord>();
                _own = null;
            }
        }

        private void PersistPeers(List<DeviceRecord> peers)
        {
            JArray array = new JArray();
            for (int i = 0; i < peers.Count; i++)
            {
                array.Add(peers[i].ToJson());
            }
            JObject json = new JObject();
            json.Set("peers", array);
            _store.Write(PeersFileName, Utf8(Json.Write(json)));
        }

        // ---- helpers --------------------------------------------------------

        private static byte[] Utf8(string text)
        {
            return Encoding.UTF8.GetBytes(text ?? string.Empty);
        }

        private static string Utf8(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return string.Empty;
            }
            try
            {
                return new UTF8Encoding(false, true).GetString(data, 0, data.Length);
            }
            catch (DecoderFallbackException ex)
            {
                throw new FormatException("stored data is not valid UTF-8", ex);
            }
        }

        private static string ToBase64(byte[] data)
        {
            return data == null ? string.Empty : Convert.ToBase64String(data);
        }

        private static byte[] Base64(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }
            try
            {
                return Convert.FromBase64String(value);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
