// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

using System.Collections.Concurrent;
using System.IO.Pipes;

namespace XenoAtom.MsBuildPipeLogger;

internal class PipeBuffer : Stream
{
    private const int BufferSize = 8192;

    private readonly ConcurrentBag<Buffer> _pool = new();

    private readonly BlockingCollection<Buffer> _queue = new(new ConcurrentQueue<Buffer>());

    private Buffer? _current;

    private int _connectionBoundaryReached;

    /// <summary>
    /// Queues a marker that separates the bytes written by two different clients. Reads stop at the
    /// marker so that a consumer never decodes bytes from two connections as a single stream.
    /// </summary>
    public void MarkConnectionBoundary()
    {
        try
        {
            _queue.Add(Buffer.ConnectionBoundary);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Returns and clears the flag set when a read reached a connection boundary.
    /// </summary>
    public bool TryConsumeConnectionBoundary() => Interlocked.Exchange(ref _connectionBoundaryReached, 0) != 0;

    public void CompleteAdding()
    {
        try
        {
            if (!_queue.IsAddingCompleted)
            {
                _queue.CompleteAdding();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public bool IsCompleted => _queue.IsCompleted;

    public bool FillFromStream(Stream stream, CancellationToken cancellationToken)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (!_pool.TryTake(out var buffer))
        {
            buffer = new Buffer();
        }

        if (buffer.FillFromStream(stream, cancellationToken) == 0)
        {
            // Didn't write anything, return it to the pool
            _pool.Add(buffer);
            return false;
        }

        try
        {
            _queue.Add(buffer, cancellationToken);
            return true;
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (OperationCanceledException)
        {
        }

        if (buffer.FromPool)
        {
            _pool.Add(buffer);
        }

        return false;
    }

    public bool TryWriteEndOfFile()
    {
        try
        {
            Write(new byte[1], 0, 1);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateReadWriteArguments(buffer, offset, count);
        try
        {
            _queue.Add(new Buffer(buffer, offset, count));
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateReadWriteArguments(buffer, offset, count);

        var read = 0;
        while (read < count)
        {
            // Ensure a buffer is available
            var current = TakeBuffer();
            if (current is null)
            {
                break;
            }

            if (current.IsConnectionBoundary)
            {
                if (read > 0)
                {
                    // Hand back what the previous client wrote first. The boundary stays current so
                    // the next call reports it.
                    break;
                }

                // Report the end of the connection as end of stream so the caller can reset the
                // decoder it layered on top of this buffer before the next client's bytes arrive.
                _current = null;
                Interlocked.Exchange(ref _connectionBoundaryReached, 1);
                break;
            }

            // Get as much as we can from the current buffer
            read += current.Read(buffer, offset + read, count - read);
            if (current.Count == 0)
            {
                // Used up this buffer, return to the pool if it's a pool buffer
                if (current.FromPool)
                {
                    _pool.Add(current);
                }

                _current = null;
            }
        }

        return read;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CompleteAdding();
            _queue.Dispose();
        }

        base.Dispose(disposing);
    }

    private static void ValidateReadWriteArguments(byte[] buffer, int offset, int count)
    {
        if (buffer is null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (buffer.Length - offset < count)
        {
            throw new ArgumentException("The offset and count exceed the buffer bounds.", nameof(count));
        }
    }

    private Buffer? TakeBuffer()
    {
        if (_current is not null)
        {
            return _current;
        }

        // Take() can throw when marked as complete from another thread
        // https://docs.microsoft.com/en-us/dotnet/api/system.collections.concurrent.blockingcollection-1.take?view=netcore-3.1
        try
        {
            _current = _queue.Take();
            return _current;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private class Buffer
    {
        private readonly byte[] _buffer;

        private int _offset;

        /// <summary>
        /// A shared, immutable marker queued between two client connections. It carries no data, so a
        /// single instance can be reused by every <see cref="PipeBuffer"/>.
        /// </summary>
        public static Buffer ConnectionBoundary { get; } = new(isConnectionBoundary: true);

        public int Count { get; private set; }

        public bool FromPool { get; }

        public bool IsConnectionBoundary { get; }

        public Buffer()
        {
            _buffer = new byte[BufferSize];
            FromPool = true;
        }

        private Buffer(bool isConnectionBoundary)
        {
            _buffer = Array.Empty<byte>();
            IsConnectionBoundary = isConnectionBoundary;
        }

        public Buffer(byte[] buffer, int offset, int count)
        {
            _buffer = new byte[count];
            System.Buffer.BlockCopy(buffer, offset, _buffer, 0, count);
            Count = count;
        }

        public int FillFromStream(Stream stream, CancellationToken cancellationToken)
        {
            _offset = 0;
            if (stream is AnonymousPipeServerStream || stream is AnonymousPipeClientStream)
            {
                // We can't use ReadAsync with Anonymous PipeStream
                // https://github.com/dotnet/runtime/issues/23638
                // https://docs.microsoft.com/en-us/windows/win32/ipc/anonymous-pipe-operations
                // Asynchronous (overlapped) read and write operations are not supported by anonymous pipes
                Count = cancellationToken.IsCancellationRequested ? 0 : stream.Read(_buffer, _offset, BufferSize);
            }
            else
            {
                try
                {
                    var readTask = stream.ReadAsync(_buffer, _offset, BufferSize, cancellationToken);
                    Count = readTask.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    Count = 0;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    Count = 0;
                }
            }

            return Count;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            var available = count > Count ? Count : count;
            Array.Copy(_buffer, _offset, buffer, offset, available);
            _offset += available;
            Count -= available;
            return available;
        }
    }

    // Not implemented

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }
}