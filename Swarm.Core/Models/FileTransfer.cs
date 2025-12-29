using Swarm.Core.Helpers;

namespace Swarm.Core.Models;

/// <summary>
/// Represents a file transfer (incoming or outgoing).
/// </summary>
public class FileTransfer
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public long BytesTransferred { get; set; }
    public double Progress => FileSize > 0 ? (double)BytesTransferred / FileSize * 100 : 0;
    public TransferDirection Direction { get; set; }
    public TransferStatus Status { get; set; } = TransferStatus.Pending;
    public Peer? RemotePeer { get; set; }
    public string? LocalPath { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    
    /// <summary>
    /// CancellationTokenSource for cancelling this transfer.
    /// </summary>
    public CancellationTokenSource? CancellationTokenSource { get; set; }
    
    /// <summary>
    /// Error message if transfer failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
    
    /// <summary>
    /// Number of retry attempts made.
    /// </summary>
    public int RetryCount { get; set; }
    
    /// <summary>
    /// Maximum retry attempts allowed.
    /// </summary>
    public const int MaxRetries = 3;
    
    /// <summary>
    /// Whether this transfer can be cancelled.
    /// </summary>
    public bool CanCancel => Status == TransferStatus.Pending || Status == TransferStatus.InProgress;
    
    /// <summary>
    /// Whether this transfer can be retried.
    /// </summary>
    public bool CanRetry => Status == TransferStatus.Failed && RetryCount < MaxRetries;
    
    public string SpeedDisplay
    {
        get
        {
            if (Status != TransferStatus.InProgress || BytesTransferred == 0) return "--";
            var elapsed = (DateTime.Now - StartTime).TotalSeconds;
            if (elapsed < 0.1) return "--";
            var bytesPerSecond = BytesTransferred / elapsed;
            return FileHelpers.FormatBytes(bytesPerSecond) + "/s";
        }
    }
    
    /// <summary>
    /// Cancel this transfer if possible.
    /// </summary>
    public void Cancel()
    {
        if (CanCancel)
        {
            CancellationTokenSource?.Cancel();
            Status = TransferStatus.Cancelled;
            EndTime = DateTime.Now;
        }
    }
}

public enum TransferDirection
{
    Incoming,
    Outgoing
}

public enum TransferStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Cancelled
}

