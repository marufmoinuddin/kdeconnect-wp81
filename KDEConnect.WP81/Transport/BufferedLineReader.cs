using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KDEConnect.WP81.Transport
{
    /// <summary>
    /// Line-oriented reader over a KDE Connect stream that keeps leftover bytes.
    ///
    /// The protocol is "compact JSON + '\n'" with no length prefix, and the
    /// handshake line and the first plugin packet can arrive in the same TCP
    /// segment - so anything read past the newline must be retained for the
    /// packet loop rather than discarded (docs/protocol.md §2).
    /// </summary>
    public sealed class BufferedLineReader
    {
        private readonly Stream _stream;
        private readonly int _maxFrameBytes;
        private readonly byte[] _chunk = new byte[512];
        private readonly List<byte> _carry = new List<byte>();

        public BufferedLineReader(Stream stream, int maxFrameBytes)
        {
            if (stream == null)
            {
                throw new ArgumentNullException("stream");
            }
            if (maxFrameBytes <= 0)
            {
                throw new ArgumentOutOfRangeException("maxFrameBytes");
            }
            _stream = stream;
            _maxFrameBytes = maxFrameBytes;
        }

        /// <summary>True when a previous read left bytes that may hold a full frame.</summary>
        public bool HasBufferedData { get { return _carry.Count > 0; } }

        public int BufferedByteCount { get { return _carry.Count; } }

        /// <summary>
        /// Reads one '\n'-terminated frame (CR tolerated), bounded by
        /// <c>maxFrameBytes</c> and by <c>timeout</c>.
        /// </summary>
        public async Task<string> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using (CancellationTokenSource source =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                source.CancelAfter(timeout);

                while (true)
                {
                    int newline = IndexOfNewline();
                    if (newline >= 0)
                    {
                        return ExtractLine(newline);
                    }

                    if (_carry.Count > _maxFrameBytes)
                    {
                        throw new InvalidDataException(
                            "frame exceeds the " + _maxFrameBytes + " byte limit without a newline");
                    }

                    int read = await _stream.ReadAsync(_chunk, 0, _chunk.Length, source.Token)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("peer closed the connection mid-frame");
                    }
                    for (int i = 0; i < read; i++)
                    {
                        _carry.Add(_chunk[i]);
                    }
                }
            }
        }

        private int IndexOfNewline()
        {
            for (int i = 0; i < _carry.Count; i++)
            {
                if (_carry[i] == (byte)'\n')
                {
                    return i;
                }
            }
            return -1;
        }

        private string ExtractLine(int newlineIndex)
        {
            int length = newlineIndex;
            // Tolerate CRLF without treating CR as content.
            if (length > 0 && _carry[length - 1] == (byte)'\r')
            {
                length--;
            }

            byte[] frame = new byte[length];
            for (int i = 0; i < length; i++)
            {
                frame[i] = _carry[i];
            }
            _carry.RemoveRange(0, newlineIndex + 1);

            try
            {
                // Strict UTF-8: malformed sequences drop the frame rather than
                // silently substituting replacement characters.
                return new UTF8Encoding(false, true).GetString(frame);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("frame is not valid UTF-8", ex);
            }
        }

        /// <summary>Discards everything buffered (after an error).</summary>
        public void Reset()
        {
            _carry.Clear();
        }
    }
}
