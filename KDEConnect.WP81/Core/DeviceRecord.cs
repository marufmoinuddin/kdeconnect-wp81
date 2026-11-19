using System;
using System.Collections.Generic;

namespace KDEConnect.WP81.Core
{
    public enum LinkState
    {
        Offline = 0,
        Connecting = 1,
        ConnectedUnpaired = 2,
        Pairing = 3,
        Paired = 4,
        Error = 5
    }

    /// <summary>
    /// Everything this client remembers about one peer. Persisted as JSON in
    /// the app's local folder; never contains our own private key.
    /// </summary>
    public sealed class DeviceRecord
    {
        public DeviceRecord()
        {
            IncomingCapabilities = new List<string>();
            OutgoingCapabilities = new List<string>();
        }

        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public string DeviceType { get; set; }
        public int ProtocolVersion { get; set; }

        /// <summary>DER of the peer certificate, pinned at pairing time.</summary>
        public byte[] PinnedCertificateDer { get; set; }

        /// <summary>Hex form of <see cref="PinnedCertificateDer"/>, for display.</summary>
        public string PinnedCertificateSha256 { get; set; }

        public bool IsPaired { get; set; }
        public long PairedAtUnixSeconds { get; set; }
        public long LastSeenUnixSeconds { get; set; }
        public int LastProtocolVersionSeen { get; set; }

        public string LastAddress { get; set; }
        public int LastTcpPort { get; set; }

        public List<string> IncomingCapabilities { get; set; }
        public List<string> OutgoingCapabilities { get; set; }

        /// <summary>Optional operator note shown in the UI.</summary>
        public string Note { get; set; }

        public bool Supports(string packetType)
        {
            if (packetType == null)
            {
                return false;
            }
            for (int i = 0; i < IncomingCapabilities.Count; i++)
            {
                if (string.Equals(IncomingCapabilities[i], packetType, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // ---- serialisation --------------------------------------------------

        public JObject ToJson()
        {
            JObject o = new JObject();
            o.Set("deviceId", new JString(DeviceId ?? string.Empty));
            o.Set("deviceName", new JString(DeviceName ?? string.Empty));
            o.Set("deviceType", new JString(DeviceType ?? "unknown"));
            o.Set("protocolVersion", JNumber.From(ProtocolVersion));
            if (PinnedCertificateDer != null)
            {
                o.Set("pinnedCertificateDer", new JString(Convert.ToBase64String(PinnedCertificateDer)));
            }
            o.Set("pinnedCertificateSha256", new JString(PinnedCertificateSha256 ?? string.Empty));
            o.Set("isPaired", new JBool(IsPaired));
            o.Set("pairedAtUnixSeconds", JNumber.From(PairedAtUnixSeconds));
            o.Set("lastSeenUnixSeconds", JNumber.From(LastSeenUnixSeconds));
            o.Set("lastProtocolVersionSeen", JNumber.From(LastProtocolVersionSeen));
            o.Set("lastAddress", new JString(LastAddress ?? string.Empty));
            o.Set("lastTcpPort", JNumber.From(LastTcpPort));
            o.Set("note", new JString(Note ?? string.Empty));

            JArray inc = new JArray();
            for (int i = 0; i < IncomingCapabilities.Count; i++)
            {
                inc.Add(new JString(IncomingCapabilities[i]));
            }
            o.Set("incomingCapabilities", inc);

            JArray outg = new JArray();
            for (int i = 0; i < OutgoingCapabilities.Count; i++)
            {
                outg.Add(new JString(OutgoingCapabilities[i]));
            }
            o.Set("outgoingCapabilities", outg);

            return o;
        }

        public static DeviceRecord FromJson(JObject o)
        {
            if (o == null)
            {
                return null;
            }
            DeviceRecord r = new DeviceRecord();
            r.DeviceId = o.GetString("deviceId", string.Empty);
            if (!Sanitize.IsValidDeviceId(r.DeviceId))
            {
                return null;
            }
            r.DeviceName = o.GetString("deviceName", string.Empty);
            r.DeviceType = o.GetString("deviceType", "unknown");
            r.ProtocolVersion = o.GetInt("protocolVersion", ProtocolConstants.ProtocolVersion);

            string cert = o.GetString("pinnedCertificateDer", null);
            if (!string.IsNullOrEmpty(cert))
            {
                try
                {
                    r.PinnedCertificateDer = Convert.FromBase64String(cert);
                }
                catch (FormatException)
                {
                    r.PinnedCertificateDer = null;
                }
            }
            r.PinnedCertificateSha256 = o.GetString("pinnedCertificateSha256", string.Empty);
            r.IsPaired = o.GetBool("isPaired", false);
            r.PairedAtUnixSeconds = o.GetLong("pairedAtUnixSeconds", 0);
            r.LastSeenUnixSeconds = o.GetLong("lastSeenUnixSeconds", 0);
            r.LastProtocolVersionSeen = o.GetInt("lastProtocolVersionSeen", 0);
            r.LastAddress = o.GetString("lastAddress", string.Empty);
            r.LastTcpPort = o.GetInt("lastTcpPort", 0);
            r.Note = o.GetString("note", string.Empty);
            r.IncomingCapabilities = o.GetStringList("incomingCapabilities");
            r.OutgoingCapabilities = o.GetStringList("outgoingCapabilities");

            if (!r.IsPaired)
            {
                // A trust record without a pin must never look like trust.
                r.PinnedCertificateDer = null;
                r.PinnedCertificateSha256 = string.Empty;
            }
            return r;
        }
    }

    /// <summary>Mutable view of a peer used by the UI.</summary>
    public sealed class DeviceViewModel
    {
        public DeviceViewModel()
        {
            State = LinkState.Offline;
        }

        public string DeviceId { get; set; }
        public string DisplayName { get; set; }
        public string DeviceType { get; set; }
        public LinkState State { get; set; }
        public string StatusText { get; set; }
        public bool IsPaired { get; set; }
        public string LastError { get; set; }
        public int BatteryPercent { get; set; }
        public bool BatteryCharging { get; set; }
        public string MediaTitle { get; set; }
        public string MediaArtist { get; set; }
        public bool MediaPlaying { get; set; }
        public long LastSeenUnixSeconds { get; set; }
        public int ProtocolVersion { get; set; }
        public bool SupportsTouchpad { get; set; }
        public bool SupportsMediaControl { get; set; }
        public bool SupportsVolume { get; set; }
        public bool SupportsShare { get; set; }
        public bool SupportsClipboard { get; set; }
        public bool SupportsPresenter { get; set; }
    }
}
