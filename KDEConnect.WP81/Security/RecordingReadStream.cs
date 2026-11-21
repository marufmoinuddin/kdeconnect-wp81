using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KDEConnect.WP81.Security
{
    /// <summary>
    /// Records what actually arrives at the TLS layer, so a handshake failure can
    /// be described instead of guessed at.
    /// </summary>
    /// <remarks>
    /// Bouncy Castle reports almost every internal problem as
    /// "internal_error(80)" - a malformed record, an unexpected exception and a
    /// peer that closed the connection all look identical from outside. That cost
    /// a full build-and-deploy cycle to discover. The distinction that matters is
    /// cheap to capture:
    ///
    ///   zero bytes read  -> the peer never spoke. Our plaintext identity packet
    ///                       was probably rejected, or the peer closed early.
    ///   bytes read       -> the peer did send a ClientHello and we rejected it,
    ///                       so the first bytes identify the protocol version,
    ///                       session id length and cipher suite list.
    /// </remarks>
    public sealed class RecordingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _limit;
        private readonly MemoryStream _captured;
        private long _totalRead;

        public RecordingReadStream(Stream inner, int limit)
        {
            if (inner == null)
            {
                throw new ArgumentNullException("inner");
            }
            _inner = inner;
            _limit = limit > 0 ? limit : 512;
            _captured = new MemoryStream();
        }

        /// <summary>Total bytes handed to the TLS layer.</summary>
        public long TotalBytesRead { get { return _totalRead; } }

        /// <summary>The first bytes received, for diagnosis.</summary>
        public byte[] Captured
        {
            get { return _captured.ToArray(); }
        }

        /// <summary>A one-line summary suitable for a dialog or a log.</summary>
        public string Describe()
        {
            byte[] bytes = Captured;
            if (_totalRead == 0)
            {
                return "peer sent 0 bytes before failing (no ClientHello: the " +
                       "connection packet was probably rejected, or the peer closed)";
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("peer sent ");
            sb.Append(_totalRead.ToString());
            sb.Append(" byte(s); first ");
            int show = bytes.Length < 32 ? bytes.Length : 32;
            sb.Append(show.ToString());
            sb.Append(": ");
            for (int i = 0; i < show; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(bytes[i].ToString("X2"));
            }
            return sb.ToString();
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return _inner.CanWrite; } }

        public override long Length
        {
            get { throw new NotSupportedException("Recording stream"); }
        }

        public override long Position
        {
            get { throw new NotSupportedException("Recording stream"); }
            set { throw new NotSupportedException("Recording stream"); }
        }

        public override void Flush()
        {
            _inner.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return _inner.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            if (read > 0)
            {
                Record(buffer, offset, read);
            }
            return read;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int read = await _inner.ReadAsync(buffer, offset, count, cancellationToken)
                .ConfigureAwait(false);
            if (read > 0)
            {
                Record(buffer, offset, read);
            }
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
        }

        public override Task WriteAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return _inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException("Recording stream");
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException("Recording stream");
        }

        private void Record(byte[] buffer, int offset, int count)
        {
            _totalRead += count;

            long room = _limit - _captured.Length;
            if (room <= 0)
            {
                return;
            }
            int take = count;
            if (take > room)
            {
                take = (int)room;
            }
            _captured.Write(buffer, offset, take);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _captured.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
