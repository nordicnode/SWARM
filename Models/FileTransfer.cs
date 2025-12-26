namespace Swarm.Models;

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
    
    public string SpeedDisplay
    {
        get
        {
            if (Status != TransferStatus.InProgress || BytesTransferred == 0) return "--";
            var elapsed = (DateTime.Now - StartTime).TotalSeconds;
            if (elapsed < 0.1) return "--";
            var bytesPerSecond = BytesTransferred / elapsed;
            return FormatBytes(bytesPerSecond) + "/s";
        }
    }

    private static string FormatBytes(double bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        int order = 0;
        while (bytes >= 1024 && order < sizes.Length - 1)
        {
            order++;
            bytes /= 1024;
        }
        return $"{bytes:0.##} {sizes[order]}";
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
