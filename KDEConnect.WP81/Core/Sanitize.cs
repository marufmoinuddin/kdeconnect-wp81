using System;
using System.Text;

namespace KDEConnect.WP81.Core
{
    /// <summary>
    /// Input sanitisation for values that arrive from the network.
    /// Every method is total: it never throws and always returns something safe.
    /// </summary>
    public static class Sanitize
    {
        /// <summary>
        /// Device ids must match ^[a-zA-Z0-9_-]{32,38}$ (deviceinfo.h:191).
        /// Returns null when the value is not acceptable as a device id.
        /// </summary>
        public static string DeviceIdOrNull(string value)
        {
            if (value == null)
            {
                return null;
            }
            if (value.Length < ProtocolConstants.MinDeviceIdLength ||
                value.Length > ProtocolConstants.MaxDeviceIdLength)
            {
                return null;
            }
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool ok = (c >= 'a' && c <= 'z') ||
                          (c >= 'A' && c <= 'Z') ||
                          (c >= '0' && c <= '9') ||
                          c == '_' || c == '-';
                if (!ok)
                {
                    return null;
                }
            }
            return value;
        }

        /// <summary>
        /// A fresh protocol device id: 32 lowercase hex characters, which
        /// satisfies the 32-38 char [a-zA-Z0-9_-] rule the reference enforces
        /// (deviceinfo.h:170-186) and is what kdeconnect-android generates
        /// (LanLinkProvider.kt:60-64).
        /// </summary>
        public static string GenerateDeviceId()
        {
            return Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// True for a dotted-quad IPv4 literal with each octet in range. Used to
        /// validate operator-typed addresses before they reach a socket.
        /// </summary>
        public static bool IsIPv4(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }
            string[] parts = value.Split('.');
            if (parts.Length != 4)
            {
                return false;
            }
            for (int i = 0; i < 4; i++)
            {
                if (parts[i].Length == 0 || parts[i].Length > 3)
                {
                    return false;
                }
                int octet = 0;
                for (int c = 0; c < parts[i].Length; c++)
                {
                    char ch = parts[i][c];
                    if (ch < '0' || ch > '9')
                    {
                        return false;
                    }
                    octet = (octet * 10) + (ch - '0');
                }
                if (octet > 255)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Turns the address a datagram arrived from into something that can be
        /// dialled back.
        /// </summary>
        /// <remarks>
        /// WinRT reports the remote address of a dual-mode socket in whatever
        /// form the stack chose. On this platform that is frequently
        /// IPv4-mapped IPv6 ("::ffff:192.168.0.5"), which a plain TCP connect
        /// will not resolve even though the peer is perfectly reachable on
        /// 192.168.0.5. Discovery would then succeed and every dial would fail,
        /// which is exactly the kind of split-brain this project keeps paying
        /// for: the device is listed, and pairing insists it is unreachable.
        /// </remarks>
        public static string NormalizeHostAddress(string address)
        {
            if (string.IsNullOrEmpty(address))
            {
                return string.Empty;
            }

            string value = address.Trim();

            // "[fe80::1]" style brackets are not part of the address.
            if (value.Length > 2 && value[0] == '[' && value[value.Length - 1] == ']')
            {
                value = value.Substring(1, value.Length - 2);
            }

            // IPv4-mapped IPv6 -> plain IPv4.
            const string mappedPrefix = "::ffff:";
            if (value.Length > mappedPrefix.Length &&
                value.StartsWith(mappedPrefix, StringComparison.OrdinalIgnoreCase) &&
                value.IndexOf('.') > 0)
            {
                value = value.Substring(mappedPrefix.Length);
            }

            return value;
        }

        /// <summary>True when the address is IPv6 rather than dotted-quad IPv4.</summary>
        public static bool IsIPv6(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }
            return value.IndexOf(':') >= 0 && !IsIPv4(value);
        }

        /// <summary>True when the value is a syntactically valid device id.</summary>
        public static bool IsValidDeviceId(string value)
        {
            return DeviceIdOrNull(value) != null;
        }

        /// <summary>
        /// Keeps only characters legal in a device id. Used when a packet body
        /// carries a deviceId (networkpacket.cpp:85-90 scrubs it before use).
        /// </summary>
        public static string DeviceId(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }
            StringBuilder sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if ((c >= 'a' && c <= 'z') ||
                    (c >= 'A' && c <= 'Z') ||
                    (c >= '0' && c <= '9') ||
                    c == '_' || c == '-')
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// DeviceInfo::filterName: strip ["',;:.!?()[]&lt;&gt;] and truncate to 32 chars.
        /// </summary>
        public static string DeviceName(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            StringBuilder sb = new StringBuilder(Math.Min(value.Length, ProtocolConstants.MaxDeviceNameLength));
            for (int i = 0; i < value.Length && sb.Length < ProtocolConstants.MaxDeviceNameLength; i++)
            {
                char c = value[i];
                if (ProtocolConstants.DeviceNameInvalidCharacters.IndexOf(c) >= 0)
                {
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Turns an attacker-controlled filename into a single path segment that
        /// is safe to create inside the app's own download folder.
        ///
        /// Removes directory separators, traversal, control characters, leading
        /// dots and Windows-reserved device names; guarantees a non-empty result
        /// and a bounded length.
        /// </summary>
        public static string FileName(string value)
        {
            string name = value == null ? string.Empty : value;

            // Only ever keep the last path segment, whatever separator is used.
            int slash = name.LastIndexOfAny(new char[] { '/', '\\' });
            if (slash >= 0)
            {
                name = name.Substring(slash + 1);
            }

            StringBuilder sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c < 0x20 || c == 0x7f)
                {
                    continue;
                }
                if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' ||
                    c == '"' || c == '<' || c == '>' || c == '|')
                {
                    continue;
                }
                sb.Append(c);
            }

            name = sb.ToString().Trim();

            // No hidden/system files created by accident, no "." / "..".
            while (name.StartsWith(".", StringComparison.Ordinal))
            {
                name = name.Substring(1);
            }
            name = name.Trim();

            if (name.Length == 0)
            {
                return "received-file";
            }

            // Windows reserved device names, with or without an extension.
            string stem = name;
            int dot = name.IndexOf('.');
            if (dot > 0)
            {
                stem = name.Substring(0, dot);
            }
            string upper = stem.ToUpperInvariant();
            if (upper == "CON" || upper == "PRN" || upper == "AUX" || upper == "NUL" ||
                upper == "COM1" || upper == "COM2" || upper == "COM3" || upper == "COM4" ||
                upper == "COM5" || upper == "COM6" || upper == "COM7" || upper == "COM8" ||
                upper == "COM9" || upper == "LPT1" || upper == "LPT2" || upper == "LPT3" ||
                upper == "LPT4" || upper == "LPT5" || upper == "LPT6" || upper == "LPT7" ||
                upper == "LPT8" || upper == "LPT9")
            {
                name = "_" + name;
            }

            // Bound the length while keeping the extension recognisable.
            const int MaxNameLength = 120;
            if (name.Length > MaxNameLength)
            {
                int extensionLength = 0;
                int lastDot = name.LastIndexOf('.');
                if (lastDot > 0 && name.Length - lastDot <= 16)
                {
                    extensionLength = name.Length - lastDot;
                }
                string extension = extensionLength > 0 ? name.Substring(name.Length - extensionLength) : string.Empty;
                string stemPart = name.Substring(0, name.Length - extensionLength);
                if (stemPart.Length > MaxNameLength - extensionLength)
                {
                    stemPart = stemPart.Substring(0, MaxNameLength - extensionLength);
                }
                name = stemPart + extension;
            }

            if (name.Length == 0)
            {
                return "received-file";
            }
            return name;
        }
    }
}
