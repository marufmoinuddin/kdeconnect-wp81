using System;
using System.Collections.Generic;
using System.Text;

namespace KDEConnect.WP81.Core
{
    /// <summary>
    /// Incremental, bounded line framing for the KDE Connect wire format.
    ///
    /// Feed it raw bytes as they arrive; it yields complete frames (without the
    /// trailing newline) and enforces a hard per-frame size limit so a peer that
    /// never sends a newline cannot make the client buffer without bound.
    /// </summary>
    public sealed class FrameReader
    {
        private readonly int _maxFrameSize;
        private readonly List<byte> _buffer = new List<byte>();

        public FrameReader(int maxFrameSize)
        {
            if (maxFrameSize <= 0)
            {
                throw new ArgumentOutOfRangeException("maxFrameSize");
            }
            _maxFrameSize = maxFrameSize;
        }

        public int BufferedBytes { get { return _buffer.Count; } }

        /// <summary>
        /// Result of feeding data in.
        /// </summary>
        public sealed class FeedResult
        {
            public FeedResult()
            {
                Frames = new List<string>();
            }

            public List<string> Frames { get; private set; }

            /// <summary>Set when the peer must be disconnected.</summary>
            public bool OverLimit { get; set; }

            /// <summary>Set when a frame could not be decoded as UTF-8 or JSON-unsafe.</summary>
            public bool DecodeFailed { get; set; }
        }

        public FeedResult Feed(byte[] data, int offset, int count)
        {
            FeedResult result = new FeedResult();
            if (data == null || count <= 0)
            {
                return result;
            }

            int end = offset + count;
            for (int i = offset; i < end; i++)
            {
                byte b = data[i];
                if (b == (byte)'\n')
                {
                    byte[] frameBytes = _buffer.ToArray();
                    _buffer.Clear();
                    string frame = Decode(frameBytes, result);
                    if (frame != null)
                    {
                        result.Frames.Add(frame);
                    }
                    continue;
                }

                // Tolerate CR before LF (some peers add it).
                if (b == (byte)'\r' && i + 1 < end && data[i + 1] == (byte)'\n')
                {
                    continue;
                }

                _buffer.Add(b);
                if (_buffer.Count > _maxFrameSize)
                {
                    _buffer.Clear();
                    result.OverLimit = true;
                    return result;
                }
            }

            return result;
        }

        private static string Decode(byte[] frameBytes, FeedResult result)
        {
            try
            {
                // Strict UTF-8: reject malformed sequences instead of silently
                // substituting replacement characters.
                UTF8Encoding strict = new UTF8Encoding(false, true);
                return strict.GetString(frameBytes);
            }
            catch (DecoderFallbackException)
            {
                result.DecodeFailed = true;
                return null;
            }
        }

        /// <summary>Discard everything buffered (used after an error).</summary>
        public void Reset()
        {
            _buffer.Clear();
        }
    }
}
