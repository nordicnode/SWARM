namespace Swarm.Core.Models;

/// <summary>
/// Represents a file being tracked for synchronization.
/// </summary>
public class SyncedFile
{
    /// <summary>
    /// Path relative to the sync folder root.
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// SHA256 hash of the file content for change detection.
    /// </summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// Last modification time of the file.
    /// </summary>
    public DateTime LastModified { get; set; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// The action that triggered this sync event.
    /// </summary>
    public SyncAction Action { get; set; }

    /// <summary>
    /// ID of the peer that originated this change.
    /// </summary>
    public string SourcePeerId { get; set; } = string.Empty;

    /// <summary>
    /// Name of the peer that originated this change (for UI display).
    /// </summary>
    public string SourcePeerName { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is a directory (folder) rather than a file.
    /// </summary>
    public bool IsDirectory { get; set; }

    /// <summary>
    /// The old relative path before a rename operation (only used for SyncAction.Rename).
    /// </summary>
    public string OldRelativePath { get; set; } = string.Empty;
}

/// <summary>
/// Types of synchronization actions.
/// </summary>
public enum SyncAction
{
    Create,
    Update,
    Delete,
    Rename
}

