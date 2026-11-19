using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KDEConnect.WP81.Core
{
    public enum JType
    {
        Object,
        Array,
        String,
        Number,
        Boolean,
        Null
    }

    /// <summary>
    /// Base class of the minimal JSON value model used for KDE Connect packets.
    /// Platform-neutral on purpose: this file compiles both into the phone app
    /// and into the host-side test project.
    /// </summary>
    public abstract class JValue
    {
        public abstract JType Type { get; }
    }

    public sealed class JObject : JValue
    {
        private readonly Dictionary<string, JValue> _items =
            new Dictionary<string, JValue>(StringComparer.Ordinal);

        // Insertion order is tracked explicitly so that serialisation is
        // byte-for-byte reproducible (Dictionary enumeration order is an
        // implementation detail, and the wire format is compared in tests).
        private readonly List<string> _order = new List<string>();

        public override JType Type { get { return JType.Object; } }

        public int Count { get { return _items.Count; } }

        public IEnumerable<string> Keys { get { return _order; } }

        public bool Has(string key)
        {
            return key != null && _items.ContainsKey(key);
        }

        public JValue Get(string key)
        {
            JValue v;
            return key != null && _items.TryGetValue(key, out v) ? v : null;
        }

        public void Set(string key, JValue value)
        {
            if (key == null)
            {
                throw new ArgumentNullException("key");
            }
            if (!_items.ContainsKey(key))
            {
                _order.Add(key);
            }
            _items[key] = value ?? JNull.Instance;
        }

        public void Remove(string key)
        {
            if (key != null && _items.Remove(key))
            {
                _order.Remove(key);
            }
        }

        public string GetString(string key, string defaultValue)
        {
            JValue v = Get(key);
            JString s = v as JString;
            return s != null ? s.Value : defaultValue;
        }

        public string GetString(string key)
        {
            return GetString(key, null);
        }

        /// <summary>
        /// Mirrors the reference behaviour: an integer field may arrive as a
        /// JSON number with or without a fractional part (kdeconnect-android
        /// NetworkPacket.getIntOrNull falls back to toDoubleOrNull().toInt()).
        /// </summary>
        public long GetLong(string key, long defaultValue)
        {
            JValue v = Get(key);
            JNumber n = v as JNumber;
            if (n == null)
            {
                // Tolerate numbers arriving as JSON strings: kdeconnect-android
                // accepts them (NetworkPacket.kt:71-78) and it does send
                // targetProtocolVersion as a string (LanLinkProvider.kt:261).
                JString s = v as JString;
                if (s != null)
                {
                    long parsed;
                    if (long.TryParse(s.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    {
                        return parsed;
                    }
                    double parsedDouble;
                    if (double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedDouble) &&
                        !double.IsNaN(parsedDouble) && !double.IsInfinity(parsedDouble))
                    {
                        return (long)Math.Round(parsedDouble, MidpointRounding.AwayFromZero);
                    }
                }
                return defaultValue;
            }
            if (n.IsInteger)
            {
                return n.IntegerValue;
            }
            double d = n.DoubleValue;
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                return defaultValue;
            }
            return (long)Math.Round(d, MidpointRounding.AwayFromZero);
        }

        public int GetInt(string key, int defaultValue)
        {
            long v = GetLong(key, defaultValue);
            if (v > int.MaxValue)
            {
                return int.MaxValue;
            }
            if (v < int.MinValue)
            {
                return int.MinValue;
            }
            return (int)v;
        }

        public double GetDouble(string key, double defaultValue)
        {
            JValue v = Get(key);
            JNumber n = v as JNumber;
            if (n == null)
            {
                return defaultValue;
            }
            return n.IsInteger ? (double)n.IntegerValue : n.DoubleValue;
        }

        public bool GetBool(string key, bool defaultValue)
        {
            JValue v = Get(key);
            JBool b = v as JBool;
            if (b != null)
            {
                return b.Value;
            }
            // Tolerate "true"/"false" strings, as the reference does.
            JString s = v as JString;
            if (s != null)
            {
                if (string.Equals(s.Value, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (string.Equals(s.Value, "false", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return defaultValue;
        }

        public JArray GetArray(string key)
        {
            return Get(key) as JArray;
        }

        public JObject GetObject(string key)
        {
            return Get(key) as JObject;
        }

        /// <summary>Capability-style list of strings; returns an empty list when absent.</summary>
        public List<string> GetStringList(string key)
        {
            List<string> result = new List<string>();
            JArray a = GetArray(key);
            if (a != null)
            {
                for (int i = 0; i < a.Count; i++)
                {
                    JString s = a[i] as JString;
                    if (s != null)
                    {
                        result.Add(s.Value);
                    }
                }
            }
            return result;
        }
    }

    public sealed class JArray : JValue
    {
        private readonly List<JValue> _items = new List<JValue>();

        public override JType Type { get { return JType.Array; } }

        public int Count { get { return _items.Count; } }

        public JValue this[int index]
        {
            get { return _items[index]; }
        }

        public void Add(JValue value)
        {
            _items.Add(value ?? JNull.Instance);
        }
    }

    public sealed class JString : JValue
    {
        private readonly string _value;

        public JString(string value)
        {
            _value = value ?? string.Empty;
        }

        public override JType Type { get { return JType.String; } }

        public string Value { get { return _value; } }
    }

    public sealed class JNumber : JValue
    {
        private readonly bool _isInteger;
        private readonly long _integer;
        private readonly double _double;

        private JNumber(bool isInteger, long integer, double d)
        {
            _isInteger = isInteger;
            _integer = integer;
            _double = d;
        }

        public static JNumber From(long value)
        {
            return new JNumber(true, value, 0.0);
        }

        public static JNumber From(int value)
        {
            return new JNumber(true, value, 0.0);
        }

        /// <summary>
        /// Non-finite doubles cannot be represented in JSON. The reference
        /// implementation silently drops them when setting a field, so we
        /// refuse to build such a value here and callers skip the field.
        /// </summary>
        public static JNumber From(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new InvalidOperationException("Non-finite double cannot be represented in JSON");
            }
            if (value == Math.Floor(value) && Math.Abs(value) < 9007199254740992.0)
            {
                // Whole numbers are kept integral so they serialise like the
                // reference (e.g. 1 not 1.0) when the caller meant an integer.
                return new JNumber(true, (long)value, value);
            }
            return new JNumber(false, 0, value);
        }

        public override JType Type { get { return JType.Number; } }

        public bool IsInteger { get { return _isInteger; } }

        public long IntegerValue { get { return _integer; } }

        public double DoubleValue { get { return _isInteger ? (double)_integer : _double; } }
    }

    public sealed class JBool : JValue
    {
        private readonly bool _value;

        public JBool(bool value)
        {
            _value = value;
        }

        public override JType Type { get { return JType.Boolean; } }

        public bool Value { get { return _value; } }
    }

    public sealed class JNull : JValue
    {
        public static readonly JNull Instance = new JNull();

        private JNull()
        {
        }

        public override JType Type { get { return JType.Null; } }
    }

    public sealed class JsonParseException : Exception
    {
        public JsonParseException(string message, int position)
            : base(message + " (at offset " + position.ToString(CultureInfo.InvariantCulture) + ")")
        {
            Position = position;
        }

        public int Position { get; private set; }
    }

    /// <summary>
    /// Bounded recursive-descent JSON reader and compact writer.
    /// Limits are enforced while parsing so that hostile input cannot exhaust
    /// the stack or the heap before the caller gets a chance to reject it.
    /// </summary>
    public static class Json
    {
        public const int MaxDepth = 16;
        public const int MaxInputLength = 1024 * 1024; // 1 MiB, matches the frame cap

        public static JValue Parse(string text)
        {
            if (text == null)
            {
                throw new JsonParseException("null input", 0);
            }
            if (text.Length > MaxInputLength)
            {
                throw new JsonParseException("input too large", 0);
            }
            Parser p = new Parser(text);
            JValue value = p.ParseValue(0);
            p.SkipWhitespace();
            if (!p.AtEnd)
            {
                throw new JsonParseException("trailing characters", p.Position);
            }
            return value;
        }

        public static string Write(JValue value)
        {
            StringBuilder sb = new StringBuilder(256);
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, JValue value, int depth)
        {
            if (value == null || value is JNull)
            {
                sb.Append("null");
                return;
            }
            if (depth > MaxDepth)
            {
                throw new InvalidOperationException("JSON nesting too deep");
            }

            JObject obj = value as JObject;
            if (obj != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (string key in obj.Keys)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    WriteString(sb, key);
                    sb.Append(':');
                    WriteValue(sb, obj.Get(key), depth + 1);
                }
                sb.Append('}');
                return;
            }

            JArray arr = value as JArray;
            if (arr != null)
            {
                sb.Append('[');
                for (int i = 0; i < arr.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }
                    WriteValue(sb, arr[i], depth + 1);
                }
                sb.Append(']');
                return;
            }

            JString s = value as JString;
            if (s != null)
            {
                WriteString(sb, s.Value);
                return;
            }

            JBool b = value as JBool;
            if (b != null)
            {
                sb.Append(b.Value ? "true" : "false");
                return;
            }

            JNumber n = value as JNumber;
            if (n != null)
            {
                if (n.IsInteger)
                {
                    sb.Append(n.IntegerValue.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    double d = n.DoubleValue;
                    if (double.IsNaN(d) || double.IsInfinity(d))
                    {
                        throw new InvalidOperationException("Non-finite double cannot be represented in JSON");
                    }
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                }
                return;
            }

            throw new InvalidOperationException("Unknown JSON value type");
        }

        private static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            // Non-ASCII is emitted raw, matching QJsonDocument::toJson.
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        private sealed class Parser
        {
            private readonly string _text;
            private int _pos;

            public Parser(string text)
            {
                _text = text;
                _pos = 0;
            }

            public int Position { get { return _pos; } }

            public bool AtEnd { get { return _pos >= _text.Length; } }

            public void SkipWhitespace()
            {
                while (_pos < _text.Length)
                {
                    char c = _text[_pos];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                    {
                        _pos++;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            private char Peek()
            {
                if (_pos >= _text.Length)
                {
                    throw new JsonParseException("unexpected end of input", _pos);
                }
                return _text[_pos];
            }

            private void Expect(char expected)
            {
                if (_pos >= _text.Length || _text[_pos] != expected)
                {
                    throw new JsonParseException("expected '" + expected + "'", _pos);
                }
                _pos++;
            }

            private void ExpectLiteral(string literal)
            {
                if (_pos + literal.Length > _text.Length ||
                    string.CompareOrdinal(_text, _pos, literal, 0, literal.Length) != 0)
                {
                    throw new JsonParseException("expected '" + literal + "'", _pos);
                }
                _pos += literal.Length;
            }

            public JValue ParseValue(int depth)
            {
                if (depth > MaxDepth)
                {
                    throw new JsonParseException("nesting too deep", _pos);
                }
                SkipWhitespace();
                char c = Peek();
                switch (c)
                {
                    case '{':
                        return ParseObject(depth);
                    case '[':
                        return ParseArray(depth);
                    case '"':
                        return new JString(ParseString());
                    case 't':
                        ExpectLiteral("true");
                        return new JBool(true);
                    case 'f':
                        ExpectLiteral("false");
                        return new JBool(false);
                    case 'n':
                        ExpectLiteral("null");
                        return JNull.Instance;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9'))
                        {
                            return ParseNumber();
                        }
                        throw new JsonParseException("unexpected character '" + c + "'", _pos);
                }
            }

            private JObject ParseObject(int depth)
            {
                Expect('{');
                JObject obj = new JObject();
                SkipWhitespace();
                if (Peek() == '}')
                {
                    _pos++;
                    return obj;
                }
                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"')
                    {
                        throw new JsonParseException("expected object key", _pos);
                    }
                    string key = ParseString();
                    SkipWhitespace();
                    Expect(':');
                    JValue value = ParseValue(depth + 1);
                    obj.Set(key, value);
                    SkipWhitespace();
                    char c = Peek();
                    if (c == ',')
                    {
                        _pos++;
                        continue;
                    }
                    if (c == '}')
                    {
                        _pos++;
                        return obj;
                    }
                    throw new JsonParseException("expected ',' or '}'", _pos);
                }
            }

            private JArray ParseArray(int depth)
            {
                Expect('[');
                JArray arr = new JArray();
                SkipWhitespace();
                if (Peek() == ']')
                {
                    _pos++;
                    return arr;
                }
                while (true)
                {
                    JValue value = ParseValue(depth + 1);
                    arr.Add(value);
                    SkipWhitespace();
                    char c = Peek();
                    if (c == ',')
                    {
                        _pos++;
                        continue;
                    }
                    if (c == ']')
                    {
                        _pos++;
                        return arr;
                    }
                    throw new JsonParseException("expected ',' or ']'", _pos);
                }
            }

            private string ParseString()
            {
                Expect('"');
                StringBuilder sb = new StringBuilder();
                while (true)
                {
                    if (_pos >= _text.Length)
                    {
                        throw new JsonParseException("unterminated string", _pos);
                    }
                    char c = _text[_pos++];
                    if (c == '"')
                    {
                        return sb.ToString();
                    }
                    if (c < 0x20)
                    {
                        throw new JsonParseException("raw control character in string", _pos - 1);
                    }
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (_pos >= _text.Length)
                    {
                        throw new JsonParseException("unterminated escape", _pos);
                    }
                    char e = _text[_pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_pos + 4 > _text.Length)
                            {
                                throw new JsonParseException("truncated \\u escape", _pos);
                            }
                            string hex = _text.Substring(_pos, 4);
                            int code;
                            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                            {
                                throw new JsonParseException("invalid \\u escape", _pos);
                            }
                            _pos += 4;
                            sb.Append((char)code);
                            break;
                        default:
                            throw new JsonParseException("invalid escape '\\" + e + "'", _pos - 1);
                    }
                }
            }

            private JValue ParseNumber()
            {
                int start = _pos;
                bool isInteger = true;

                if (Peek() == '-')
                {
                    _pos++;
                }
                // Integer part: JSON forbids a leading zero followed by digits.
                if (_pos >= _text.Length || _text[_pos] < '0' || _text[_pos] > '9')
                {
                    throw new JsonParseException("invalid number", _pos);
                }
                if (_text[_pos] == '0')
                {
                    _pos++;
                    if (_pos < _text.Length && _text[_pos] >= '0' && _text[_pos] <= '9')
                    {
                        throw new JsonParseException("leading zero in number", _pos);
                    }
                }
                else
                {
                    while (_pos < _text.Length && _text[_pos] >= '0' && _text[_pos] <= '9')
                    {
                        _pos++;
                    }
                }

                if (_pos < _text.Length && _text[_pos] == '.')
                {
                    isInteger = false;
                    _pos++;
                    if (_pos >= _text.Length || _text[_pos] < '0' || _text[_pos] > '9')
                    {
                        throw new JsonParseException("invalid fraction", _pos);
                    }
                    while (_pos < _text.Length && _text[_pos] >= '0' && _text[_pos] <= '9')
                    {
                        _pos++;
                    }
                }

                if (_pos < _text.Length && (_text[_pos] == 'e' || _text[_pos] == 'E'))
                {
                    isInteger = false;
                    _pos++;
                    if (_pos < _text.Length && (_text[_pos] == '+' || _text[_pos] == '-'))
                    {
                        _pos++;
                    }
                    if (_pos >= _text.Length || _text[_pos] < '0' || _text[_pos] > '9')
                    {
                        throw new JsonParseException("invalid exponent", _pos);
                    }
                    while (_pos < _text.Length && _text[_pos] >= '0' && _text[_pos] <= '9')
                    {
                        _pos++;
                    }
                }

                string token = _text.Substring(start, _pos - start);
                if (isInteger)
                {
                    long l;
                    if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
                    {
                        return JNumber.From(l);
                    }
                    // Out of long range: fall through to double, like the reference.
                }
                double d;
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ||
                    double.IsNaN(d) || double.IsInfinity(d))
                {
                    throw new JsonParseException("invalid number '" + token + "'", start);
                }
                return JNumber.From(d);
            }
        }
    }
}
