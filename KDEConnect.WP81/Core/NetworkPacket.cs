using System;
using System.Collections.Generic;
using System.Text;

namespace KDEConnect.WP81.Core
{
    /// <summary>
    /// A KDE Connect packet: the JSON envelope {id, type, body[, payloadSize,
    /// payloadTransferInfo]} terminated by a single newline on the wire.
    /// See docs/protocol.md §2.
    /// </summary>
    public sealed class NetworkPacket
    {
        public const string PayloadSizeKey = "payloadSize";
        public const string PayloadTransferInfoKey = "payloadTransferInfo";

        private readonly JObject _body;

        public NetworkPacket(string type)
            : this(type, new JObject())
        {
        }

        public NetworkPacket(string type, JObject body)
        {
            if (string.IsNullOrEmpty(type))
            {
                throw new ArgumentException("Packet type is required", "type");
            }
            Type = type;
            _body = body ?? new JObject();
            Id = DateTimeOffset.UtcNow.ToUnixTimeMillisecondsSafe();
            PayloadSize = 0;
            PayloadTransferInfo = null;
        }

        public string Type { get; private set; }

        public long Id { get; set; }

        public JObject Body { get { return _body; } }

        /// <summary>0 means "no payload"; -1 means an endless stream.</summary>
        public long PayloadSize { get; set; }

        public JObject PayloadTransferInfo { get; set; }

        public bool HasPayload { get { return PayloadSize != 0; } }

        public bool HasPayloadTransferInfo
        {
            get { return PayloadTransferInfo != null && PayloadTransferInfo.Count > 0; }
        }

        // ---- body accessors -------------------------------------------------

        public void SetString(string key, string value)
        {
            _body.Set(key, new JString(value ?? string.Empty));
        }

        public void SetInt(string key, int value)
        {
            _body.Set(key, JNumber.From(value));
        }

        public void SetLong(string key, long value)
        {
            _body.Set(key, JNumber.From(value));
        }

        public void SetBool(string key, bool value)
        {
            _body.Set(key, new JBool(value));
        }

        public void SetDouble(string key, double value)
        {
            // The reference drops non-finite doubles (NetworkPacket.kt:134-141).
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return;
            }
            _body.Set(key, JNumber.From(value));
        }

        public void SetStringList(string key, IList<string> values)
        {
            JArray arr = new JArray();
            if (values != null)
            {
                for (int i = 0; i < values.Count; i++)
                {
                    arr.Add(new JString(values[i] ?? string.Empty));
                }
            }
            _body.Set(key, arr);
        }

        public string GetString(string key, string defaultValue) { return _body.GetString(key, defaultValue); }
        public int GetInt(string key, int defaultValue) { return _body.GetInt(key, defaultValue); }
        public long GetLong(string key, long defaultValue) { return _body.GetLong(key, defaultValue); }
        public double GetDouble(string key, double defaultValue) { return _body.GetDouble(key, defaultValue); }
        public bool GetBool(string key, bool defaultValue) { return _body.GetBool(key, defaultValue); }
        public bool Has(string key) { return _body.Has(key); }

        public List<string> GetStringList(string key) { return _body.GetStringList(key); }

        // ---- wire format ----------------------------------------------------

        /// <summary>Serialises to the on-wire form: compact JSON + '\n'.</summary>
        public byte[] Serialize()
        {
            JObject envelope = new JObject();
            envelope.Set("id", JNumber.From(Id));
            envelope.Set("type", new JString(Type));
            envelope.Set("body", _body);
            if (HasPayload)
            {
                envelope.Set(PayloadSizeKey, JNumber.From(PayloadSize));
                if (PayloadTransferInfo != null)
                {
                    envelope.Set(PayloadTransferInfoKey, PayloadTransferInfo);
                }
            }
            string json = Json.Write(envelope);
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            byte[] result = new byte[jsonBytes.Length + 1];
            Buffer.BlockCopy(jsonBytes, 0, result, 0, jsonBytes.Length);
            result[jsonBytes.Length] = (byte)'\n';
            return result;
        }

        /// <summary>
        /// Parses one frame (with or without the trailing newline).
        /// Throws <see cref="JsonParseException"/> or <see cref="FormatException"/>
        /// on malformed input; callers decide whether to drop or abort.
        /// </summary>
        public static NetworkPacket Unserialize(string frame)
        {
            if (frame == null)
            {
                throw new FormatException("null frame");
            }
            string trimmed = frame.TrimEnd('\r', '\n');
            if (trimmed.Length == 0)
            {
                throw new FormatException("empty frame");
            }
            if (trimmed.Length > ProtocolConstants.MaxPacketSize)
            {
                throw new FormatException("frame too large");
            }

            JObject envelope = Json.Parse(trimmed) as JObject;
            if (envelope == null)
            {
                throw new FormatException("packet is not a JSON object");
            }

            string type = envelope.GetString("type", null);
            if (string.IsNullOrEmpty(type))
            {
                throw new FormatException("packet has no type");
            }

            JObject body = envelope.GetObject("body");
            if (body == null)
            {
                body = new JObject();
            }

            NetworkPacket packet = new NetworkPacket(type, body);
            packet.Id = envelope.GetLong("id", packet.Id);
            packet.PayloadSize = envelope.GetLong(PayloadSizeKey, 0);
            packet.PayloadTransferInfo = envelope.GetObject(PayloadTransferInfoKey);

            // The reference scrubs deviceId of characters that are not legal in
            // a D-Bus path before using it (networkpacket.cpp:85-90).
            if (body.Has("deviceId"))
            {
                string deviceId = body.GetString("deviceId", null);
                if (deviceId != null)
                {
                    body.Set("deviceId", new JString(Sanitize.DeviceId(deviceId)));
                }
            }

            return packet;
        }
    }

    internal static class DateTimeOffsetExtensions
    {
        /// <summary>
        /// Epoch milliseconds. Written by hand because the WinRT/.NET profile
        /// this project targets does not expose DateTimeOffset.ToUnixTimeMilliseconds.
        /// </summary>
        public static long ToUnixTimeMillisecondsSafe(this DateTimeOffset value)
        {
            return (long)(value.ToUniversalTime() - new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalMilliseconds;
        }
    }
}
