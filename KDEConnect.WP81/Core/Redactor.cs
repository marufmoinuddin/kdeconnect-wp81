using System;
using System.Collections.Generic;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;

namespace KDEConnect.WP81.Core
{
    /// <summary>
    /// Log redaction. Clipboard contents, credentials, payload bytes and file
    /// names must never reach the diagnostics export (mission brief, Phase 3).
    /// </summary>
    public static class Redactor
    {
        private static readonly string[] SensitiveKeys = new string[]
        {
            "content",
            "password",
            "token",
            "secret",
            "key",
            "text",
            "url",
            "filename",
            "phoneThumbnail",
            "message"
        };

        /// <summary>True when a packet body key must not be logged verbatim.</summary>
        public static bool IsSensitiveKey(string key)
        {
            if (key == null)
            {
                return false;
            }
            for (int i = 0; i < SensitiveKeys.Length; i++)
            {
                if (string.Equals(SensitiveKeys[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Returns a loggable summary of a packet: type, id and body keys, with
        /// sensitive values replaced by their length.
        /// </summary>
        public static string DescribePacket(NetworkPacket packet)
        {
            if (packet == null)
            {
                return "<null packet>";
            }
            StringBuilder sb = new StringBuilder();
            sb.Append(packet.Type);
            sb.Append(" id=");
            sb.Append(packet.Id);
            sb.Append(" keys=[");
            bool first = true;
            foreach (string key in packet.Body.Keys)
            {
                if (!first)
                {
                    sb.Append(',');
                }
                first = false;
                sb.Append(key);
                if (IsSensitiveKey(key))
                {
                    JValue v = packet.Body.Get(key);
                    JString s = v as JString;
                    sb.Append("=<redacted ");
                    sb.Append(s != null ? s.Value.Length : 0);
                    sb.Append(" chars>");
                }
            }
            sb.Append(']');
            if (packet.HasPayload)
            {
                sb.Append(" payloadSize=");
                sb.Append(packet.PayloadSize);
            }
            return sb.ToString();
        }

        /// <summary>Trims a free-text value to a bounded, single-line form.</summary>
        public static string SafeLine(string value, int maxLength)
        {
            if (value == null)
            {
                return string.Empty;
            }
            StringBuilder sb = new StringBuilder(Math.Min(value.Length, maxLength));
            for (int i = 0; i < value.Length && sb.Length < maxLength; i++)
            {
                char c = value[i];
                if (c == '\r' || c == '\n' || c == '\t')
                {
                    sb.Append(' ');
                }
                else if (c < 0x20 || c == 0x7f)
                {
                    continue;
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Short, log-safe form of a device id. The id is not a secret, but
        /// diagnostics should not spray full identifiers around; the first six
        /// characters are enough to correlate two log lines.
        /// </summary>
        public static string RedactDeviceId(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                return "(none)";
            }
            if (deviceId.Length <= 6)
            {
                return deviceId;
            }
            return deviceId.Substring(0, 6) + "...";
        }

        /// <summary>Short fingerprint of a certificate for the UI/log (SHA-256, hex, colon-separated).</summary>
        public static string CertificateFingerprint(byte[] der, int groups)
        {
            if (der == null || der.Length == 0)
            {
                return string.Empty;
            }
            Sha256Digest digest = new Sha256Digest();
            digest.BlockUpdate(der, 0, der.Length);
            byte[] hash = new byte[digest.GetDigestSize()];
            digest.DoFinal(hash, 0);
            StringBuilder sb = new StringBuilder();
            int limit = Math.Min(groups, hash.Length);
            for (int i = 0; i < limit; i++)
            {
                if (i > 0)
                {
                    sb.Append(':');
                }
                sb.Append(hash[i].ToString("X2"));
            }
            return sb.ToString();
        }
    }
}
