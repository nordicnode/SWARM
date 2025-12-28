using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Swarm.Core.Services;

/// <summary>
/// A stream wrapper that throttles read/write operations to limit bandwidth usage.
/// Uses a simple delay-based rate limiting approach.
/// </summary>
public class ThrottledStream : Stream
{
    private readonly Stream _innerStream;
    private readonly long _maxReadBytesPerSecond;
    private readonly long _maxWriteBytesPerSecond;
    private readonly bool _leaveOpen;
    
    private readonly Stopwatch _readStopwatch = new();
    private readonly Stopwatch _writeStopwatch = new();
    private long _totalBytesRead;
    private long _totalBytesWritten;

    /// <summary>
    /// Creates a new throttled stream wrapper.
    /// </summary>
    /// <param name="innerStream">The stream to wrap.</param>
    /// <param name="maxReadBytesPerSecond">Maximum read speed in bytes/sec (0 = unlimited).</param>
    /// <param name="maxWriteBytesPerSecond">Maximum write speed in bytes/sec (0 = unlimited).</param>
    /// <param name="leaveOpen">If true, the inner stream is not disposed when this stream is disposed.</param>
    public ThrottledStream(Stream innerStream, long maxReadBytesPerSecond = 0, long maxWriteBytesPerSecond = 0, bool leaveOpen = false)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        _maxReadBytesPerSecond = maxReadBytesPerSecond;
        _maxWriteBytesPerSecond = maxWriteBytesPerSecond;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => _innerStream.CanSeek;
    public override bool CanWrite => _innerStream.CanWrite;
    public override long Length => _innerStream.Length;
    public override long Position 
    { 
        get => _innerStream.Position; 
        set => _innerStream.Position = value; 
    }

    public override void Flush() => _innerStream.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _innerStream.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);
    public override void SetLength(long value) => _innerStream.SetLength(value);

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = _innerStream.Read(buffer, offset, count);
        ThrottleRead(bytesRead);
        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var bytesRead = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken);
        await ThrottleReadAsync(bytesRead, cancellationToken);
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var bytesRead = await _innerStream.ReadAsync(buffer, cancellationToken);
        await ThrottleReadAsync(bytesRead, cancellationToken);
        return bytesRead;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _innerStream.Write(buffer, offset, count);
        ThrottleWrite(count);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await _innerStream.WriteAsync(buffer, offset, count, cancellationToken);
        await ThrottleWriteAsync(count, cancellationToken);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _innerStream.WriteAsync(buffer, cancellationToken);
        await ThrottleWriteAsync(buffer.Length, cancellationToken);
    }

    private void ThrottleRead(int bytesRead)
    {
        if (_maxReadBytesPerSecond <= 0 || bytesRead <= 0) return;
        
        // Fix burst behavior: if stopwatch has been idle too long, reset before calculating
        if (_readStopwatch.IsRunning && _readStopwatch.ElapsedMilliseconds > 10000)
        {
            _readStopwatch.Restart();
            _totalBytesRead = 0;
        }
        
        if (!_readStopwatch.IsRunning)
            _readStopwatch.Start();

        _totalBytesRead += bytesRead;
        
        var elapsedMs = _readStopwatch.ElapsedMilliseconds;
        var expectedMs = (_totalBytesRead * 1000) / _maxReadBytesPerSecond;
        
        if (expectedMs > elapsedMs)
        {
            Thread.Sleep((int)(expectedMs - elapsedMs));
        }
    }

    private async Task ThrottleReadAsync(int bytesRead, CancellationToken cancellationToken)
    {
        if (_maxReadBytesPerSecond <= 0 || bytesRead <= 0) return;
        
        // Fix burst behavior: if stopwatch has been idle too long, reset before calculating
        if (_readStopwatch.IsRunning && _readStopwatch.ElapsedMilliseconds > 10000)
        {
            _readStopwatch.Restart();
            _totalBytesRead = 0;
        }
        
        if (!_readStopwatch.IsRunning)
            _readStopwatch.Start();

        _totalBytesRead += bytesRead;
        
        var elapsedMs = _readStopwatch.ElapsedMilliseconds;
        var expectedMs = (_totalBytesRead * 1000) / _maxReadBytesPerSecond;
        
        if (expectedMs > elapsedMs)
        {
            await Task.Delay((int)(expectedMs - elapsedMs), cancellationToken);
        }
    }

    private void ThrottleWrite(int bytesWritten)
    {
        if (_maxWriteBytesPerSecond <= 0 || bytesWritten <= 0) return;
        
        // Fix burst behavior: if stopwatch has been idle too long, reset before calculating
        if (_writeStopwatch.IsRunning && _writeStopwatch.ElapsedMilliseconds > 10000)
        {
            _writeStopwatch.Restart();
            _totalBytesWritten = 0;
        }
        
        if (!_writeStopwatch.IsRunning)
            _writeStopwatch.Start();

        _totalBytesWritten += bytesWritten;
        
        var elapsedMs = _writeStopwatch.ElapsedMilliseconds;
        var expectedMs = (_totalBytesWritten * 1000) / _maxWriteBytesPerSecond;
        
        if (expectedMs > elapsedMs)
        {
            Thread.Sleep((int)(expectedMs - elapsedMs));
        }
    }

    private async Task ThrottleWriteAsync(int bytesWritten, CancellationToken cancellationToken)
    {
        if (_maxWriteBytesPerSecond <= 0 || bytesWritten <= 0) return;
        
        // Fix burst behavior: if stopwatch has been idle too long, reset before calculating
        if (_writeStopwatch.IsRunning && _writeStopwatch.ElapsedMilliseconds > 10000)
        {
            _writeStopwatch.Restart();
            _totalBytesWritten = 0;
        }
        
        if (!_writeStopwatch.IsRunning)
            _writeStopwatch.Start();

        _totalBytesWritten += bytesWritten;
        
        var elapsedMs = _writeStopwatch.ElapsedMilliseconds;
        var expectedMs = (_totalBytesWritten * 1000) / _maxWriteBytesPerSecond;
        
        if (expectedMs > elapsedMs)
        {
            await Task.Delay((int)(expectedMs - elapsedMs), cancellationToken);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _innerStream.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_leaveOpen)
        {
            await _innerStream.DisposeAsync();
        }
        await base.DisposeAsync();
    }
}

