using System.Buffers;
using System.IO;
using System.Net.Sockets;

namespace Swarm.Core;

/// <summary>
/// A stream wrapper that encrypts all writes and decrypts all reads using AES-256-GCM.
/// Each chunk is prefixed with its length for proper framing.
/// Uses ArrayPool for zero-allocation encryption/decryption buffers.
/// </summary>
public class SecureStream : Stream
{
    private readonly NetworkStream _inner;
    private readonly byte[] _sessionKey;
    private readonly BinaryReader _reader;
    private readonly BinaryWriter _writer;
    private bool _disposed;

    // Buffer for decrypted read data
    private byte[]? _readBuffer;
    private int _readBufferOffset;
    private int _readBufferLength;

    public SecureStream(NetworkStream inner, byte[] sessionKey)
    {
        if (sessionKey.Length != ProtocolConstants.SESSION_KEY_SIZE)
            throw new ArgumentException($"Session key must be {ProtocolConstants.SESSION_KEY_SIZE} bytes", nameof(sessionKey));

        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _sessionKey = sessionKey;
        _reader = new BinaryReader(_inner, System.Text.Encoding.UTF8, leaveOpen: true);
        _writer = new BinaryWriter(_inner, System.Text.Encoding.UTF8, leaveOpen: true);
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Reads decrypted data from the stream.
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // If we have buffered data, return from that first
        if (_readBuffer != null && _readBufferOffset < _readBufferLength)
        {
            var available = _readBufferLength - _readBufferOffset;
            var toCopy = Math.Min(available, count);
            Buffer.BlockCopy(_readBuffer, _readBufferOffset, buffer, offset, toCopy);
            _readBufferOffset += toCopy;
            return toCopy;
        }

        // Read next encrypted chunk
        int encryptedLength;
        try
        {
            encryptedLength = _reader.ReadInt32();
        }
        catch (EndOfStreamException)
        {
            return 0;
        }

        if (encryptedLength <= 0 || encryptedLength > ProtocolConstants.MAX_ENCRYPTED_CHUNK_SIZE)
        {
            throw new InvalidDataException($"Invalid encrypted chunk size: {encryptedLength}");
        }

        // Use ArrayPool for encrypted data buffer
        var encryptedData = ArrayPool<byte>.Shared.Rent(encryptedLength);
        try
        {
            var totalRead = 0;
            while (totalRead < encryptedLength)
            {
                var bytesRead = _inner.Read(encryptedData, totalRead, encryptedLength - totalRead);
                if (bytesRead == 0)
                    throw new EndOfStreamException("Unexpected end of encrypted stream");
                totalRead += bytesRead;
            }

            // Decrypt - extract exact bytes to pass to CryptoService
            var exactEncrypted = new byte[encryptedLength];
            Buffer.BlockCopy(encryptedData, 0, exactEncrypted, 0, encryptedLength);
            var decrypted = CryptoService.Decrypt(exactEncrypted, _sessionKey);

            // Buffer the decrypted data
            _readBuffer = decrypted;
            _readBufferOffset = 0;
            _readBufferLength = decrypted.Length;

            // Return as much as requested
            var toReturn = Math.Min(_readBufferLength, count);
            Buffer.BlockCopy(_readBuffer, 0, buffer, offset, toReturn);
            _readBufferOffset = toReturn;
            return toReturn;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(encryptedData);
        }
    }

    /// <summary>
    /// Writes encrypted data to the stream.
    /// </summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (count == 0) return;

        // Extract the chunk to encrypt (need exact size array for CryptoService)
        var plaintext = new byte[count];
        Buffer.BlockCopy(buffer, offset, plaintext, 0, count);

        // Encrypt
        var encrypted = CryptoService.Encrypt(plaintext, _sessionKey);

        // Write length-prefixed encrypted data
        _writer.Write(encrypted.Length);
        _inner.Write(encrypted, 0, encrypted.Length);
    }

    /// <summary>
    /// Writes encrypted data asynchronously.
    /// </summary>
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (count == 0) return;

        var plaintext = new byte[count];
        Buffer.BlockCopy(buffer, offset, plaintext, 0, count);

        var encrypted = CryptoService.Encrypt(plaintext, _sessionKey);

        // Write length prefix
        var lengthBytes = BitConverter.GetBytes(encrypted.Length);
        await _inner.WriteAsync(lengthBytes, cancellationToken);
        await _inner.WriteAsync(encrypted, cancellationToken);
    }

    /// <summary>
    /// Reads decrypted data asynchronously.
    /// </summary>
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // If we have buffered data, return from that first
        if (_readBuffer != null && _readBufferOffset < _readBufferLength)
        {
            var available = _readBufferLength - _readBufferOffset;
            var toCopy = Math.Min(available, count);
            Buffer.BlockCopy(_readBuffer, _readBufferOffset, buffer, offset, toCopy);
            _readBufferOffset += toCopy;
            return toCopy;
        }

        // Read length prefix
        var lengthBytes = new byte[4];
        var lengthRead = 0;
        while (lengthRead < 4)
        {
            var read = await _inner.ReadAsync(lengthBytes.AsMemory(lengthRead, 4 - lengthRead), cancellationToken);
            if (read == 0) return 0;
            lengthRead += read;
        }

        var encryptedLength = BitConverter.ToInt32(lengthBytes, 0);
        if (encryptedLength <= 0 || encryptedLength > ProtocolConstants.MAX_ENCRYPTED_CHUNK_SIZE)
        {
            throw new InvalidDataException($"Invalid encrypted chunk size: {encryptedLength}");
        }

        // Use ArrayPool for encrypted data
        var encryptedData = ArrayPool<byte>.Shared.Rent(encryptedLength);
        try
        {
            var totalRead = 0;
            while (totalRead < encryptedLength)
            {
                var bytesRead = await _inner.ReadAsync(encryptedData.AsMemory(totalRead, encryptedLength - totalRead), cancellationToken);
                if (bytesRead == 0)
                    throw new EndOfStreamException("Unexpected end of encrypted stream");
                totalRead += bytesRead;
            }

            // Extract exact bytes for CryptoService
            var exactEncrypted = new byte[encryptedLength];
            Buffer.BlockCopy(encryptedData, 0, exactEncrypted, 0, encryptedLength);
            var decrypted = CryptoService.Decrypt(exactEncrypted, _sessionKey);

            _readBuffer = decrypted;
            _readBufferOffset = 0;
            _readBufferLength = decrypted.Length;

            var toReturn = Math.Min(_readBufferLength, count);
            Buffer.BlockCopy(_readBuffer, 0, buffer, offset, toReturn);
            _readBufferOffset = toReturn;
            return toReturn;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(encryptedData);
        }
    }

    public override void Flush()
    {
        _inner.Flush();
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _inner.FlushAsync(cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _reader.Dispose();
                _writer.Dispose();
                // Don't dispose _inner - caller owns it
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}
