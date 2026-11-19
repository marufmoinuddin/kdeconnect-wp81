using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace KDEConnect.WP81.Transport
{
    /// <summary>
    /// Adapts a Windows Runtime input/output stream pair to System.IO.Stream so
    /// that an in-process TLS stack can run over a WinRT socket.
    ///
    /// Written by hand rather than relying on WindowsRuntimeStreamExtensions so
    /// that the phone project has no dependency on which .NET profile happens to
    /// expose those extension methods.
    /// </summary>
    public sealed class WinRtStreamAdapter : Stream
    {
        private readonly IInputStream _input;
        private readonly IOutputStream _output;
        private readonly bool _ownsStreams;
        private bool _closed;

        public WinRtStreamAdapter(IInputStream input, IOutputStream output, bool ownsStreams)
        {
            if (input == null && output == null)
            {
                throw new ArgumentNullException("input");
            }
            _input = input;
            _output = output;
            _ownsStreams = ownsStreams;
        }

        public override bool CanRead { get { return _input != null && !_closed; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return _output != null && !_closed; } }

        public override long Length
        {
            get { throw new NotSupportedException("Sequential stream"); }
        }

        public override long Position
        {
            get { throw new NotSupportedException("Sequential stream"); }
            set { throw new NotSupportedException("Sequential stream"); }
        }

        public override void Flush()
        {
            // Nothing buffered at this layer: writes go straight to the socket.
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<bool>(true);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // Synchronous Read over an async WinRT stream. The TLS stack runs on
            // a worker thread (never the UI thread), so blocking here is safe.
            return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_closed || _input == null)
            {
                return 0;
            }
            if (buffer == null)
            {
                throw new ArgumentNullException("buffer");
            }
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException("count");
            }
            if (count == 0)
            {
                return 0;
            }

            IBuffer readBuffer = buffer.AsBuffer(offset, count);
            cancellationToken.ThrowIfCancellationRequested();
            IBuffer result = await _input.ReadAsync(readBuffer, 4096, InputStreamOptions.Partial).AsTask(cancellationToken);

            uint bytesRead = result.Length;
            return (int)bytesRead;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_closed || _output == null)
            {
                throw new ObjectDisposedException("WinRtStreamAdapter");
            }
            if (buffer == null)
            {
                throw new ArgumentNullException("buffer");
            }
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException("count");
            }
            if (count == 0)
            {
                return;
            }

            // Copy the requested window into the buffer we hand to WinRT.
            byte[] window = new byte[count];
            Buffer.BlockCopy(buffer, offset, window, 0, count);
            IBuffer toWrite = window.AsBuffer();

            cancellationToken.ThrowIfCancellationRequested();
            await _output.WriteAsync(toWrite).AsTask(cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException("Sequential stream");
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException("Sequential stream");
        }

        protected override void Dispose(bool disposing)
        {
            if (!_closed)
            {
                _closed = true;
                if (_ownsStreams)
                {
                    try
                    {
                        if (_input != null)
                        {
                            _input.Dispose();
                        }
                    }
                    catch (Exception)
                    {
                    }
                    try
                    {
                        if (_output != null)
                        {
                            _output.Dispose();
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            base.Dispose(disposing);
        }
    }
}
