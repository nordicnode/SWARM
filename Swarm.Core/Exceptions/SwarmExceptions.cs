namespace Swarm.Core.Exceptions;

/// <summary>
/// Exception thrown when secure handshake with a peer fails.
/// </summary>
public class HandshakeFailedException : Exception
{
    public string? PeerId { get; }
    public string? PeerName { get; }
    public string? Reason { get; }

    public HandshakeFailedException(string message) : base(message)
    {
    }

    public HandshakeFailedException(string message, string? peerId, string? peerName, string? reason = null)
        : base(message)
    {
        PeerId = peerId;
        PeerName = peerName;
        Reason = reason;
    }

    public HandshakeFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Exception thrown when a file transfer is interrupted.
/// </summary>
public class TransferInterruptedException : Exception
{
    public string? FileName { get; }
    public long BytesTransferred { get; }
    public long TotalBytes { get; }
    public bool IsResumable { get; }

    public TransferInterruptedException(string message) : base(message)
    {
    }

    public TransferInterruptedException(string message, string? fileName, long bytesTransferred, long totalBytes, bool isResumable = false)
        : base(message)
    {
        FileName = fileName;
        BytesTransferred = bytesTransferred;
        TotalBytes = totalBytes;
        IsResumable = isResumable;
    }

    public TransferInterruptedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Exception thrown when connection to a peer fails or is lost.
/// </summary>
public class PeerConnectionException : Exception
{
    public string? PeerId { get; }
    public string? IpAddress { get; }
    public int Port { get; }
    public bool IsTransient { get; }

    public PeerConnectionException(string message) : base(message)
    {
    }

    public PeerConnectionException(string message, string? peerId, string? ipAddress, int port, bool isTransient = true)
        : base(message)
    {
        PeerId = peerId;
        IpAddress = ipAddress;
        Port = port;
        IsTransient = isTransient;
    }

    public PeerConnectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Exception thrown when file encryption or decryption fails.
/// </summary>
public class EncryptionException : Exception
{
    public string? FilePath { get; }
    public bool IsEncryption { get; }

    public EncryptionException(string message) : base(message)
    {
    }

    public EncryptionException(string message, string? filePath, bool isEncryption)
        : base(message)
    {
        FilePath = filePath;
        IsEncryption = isEncryption;
    }

    public EncryptionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Exception thrown when sync operations encounter an unrecoverable error.
/// </summary>
public class SyncException : Exception
{
    public string? RelativePath { get; }
    public string? SourcePeerId { get; }

    public SyncException(string message) : base(message)
    {
    }

    public SyncException(string message, string? relativePath, string? sourcePeerId = null)
        : base(message)
    {
        RelativePath = relativePath;
        SourcePeerId = sourcePeerId;
    }

    public SyncException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
