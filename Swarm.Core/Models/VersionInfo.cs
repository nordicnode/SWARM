namespace Swarm.Core.Models;

/// <summary>
/// Represents metadata for a file version stored in the versioning system.
/// </summary>
public class VersionInfo
{
    /// <summary>
    /// Original file's relative path within the sync folder.
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Unique version identifier (timestamp-based: yyyyMMdd-HHmmss-fff).
    /// </summary>
    public string VersionId { get; set; } = string.Empty;

    /// <summary>
    /// When this version was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Size of the versioned file in bytes.
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// SHA256 hash of the file content for deduplication.
    /// </summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// Reason for creating this version: "Conflict", "BeforeSync", "Manual", "BeforeDelete".
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// ID of the peer that triggered this version creation (if applicable).
    /// </summary>
    public string? SourcePeerId { get; set; }

    /// <summary>
    /// Gets the display-friendly file size.
    /// </summary>
    public string FileSizeDisplay => FormatFileSize(FileSize);

    /// <summary>
    /// Gets the display-friendly timestamp.
    /// </summary>
    public string CreatedAtDisplay => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}

