using System.IO;

namespace Swarm.Core;

/// <summary>
/// How to handle conflicts when files differ between local and remote.
/// </summary>
public enum ConflictResolutionMode
{
    /// <summary>
    /// Automatically use the newest file (Last Write Wins). This is the default.
    /// Archived version is still saved to version history.
    /// </summary>
    AutoNewest,
    
    /// <summary>
    /// Keep both files by renaming the conflict. Format: filename (conflict from PeerName).ext
    /// </summary>
    KeepBoth,
    
    /// <summary>
    /// Always ask the user to resolve conflicts via dialog.
    /// </summary>
    AskUser,
    
    /// <summary>
    /// Always keep local version, ignore remote changes.
    /// </summary>
    AlwaysKeepLocal,
    
    /// <summary>
    /// Always accept remote version, overwrite local.
    /// </summary>
    AlwaysKeepRemote
}

/// <summary>
/// Information about a detected file conflict.
/// </summary>
public class FileConflict
{
    public string RelativePath { get; set; } = string.Empty;
    public string LocalHash { get; set; } = string.Empty;
    public string RemoteHash { get; set; } = string.Empty;
    public DateTime LocalModified { get; set; }
    public DateTime RemoteModified { get; set; }
    public long LocalSize { get; set; }
    public long RemoteSize { get; set; }
    public string SourcePeerName { get; set; } = string.Empty;
    public string SourcePeerId { get; set; } = string.Empty;
    
    /// <summary>
    /// Which version would win under AutoNewest mode.
    /// </summary>
    public ConflictWinner AutoWinner => LocalModified >= RemoteModified 
        ? ConflictWinner.Local 
        : ConflictWinner.Remote;
}

public enum ConflictWinner
{
    Local,
    Remote
}

/// <summary>
/// Result of conflict resolution (user's choice).
/// </summary>
public enum ConflictChoice
{
    KeepLocal,
    KeepRemote,
    KeepBoth,
    Skip
}

/// <summary>
/// Service for detecting and resolving file conflicts during sync.
/// </summary>
public class ConflictResolutionService
{
    private readonly Settings _settings;
    private readonly VersioningService? _versioningService;
    private readonly ActivityLogService? _activityLogService;
    
    /// <summary>
    /// Raised when a conflict needs user resolution (only when mode is AskUser).
    /// The Func should return the user's choice. If null, skip resolution.
    /// </summary>
    public event Func<FileConflict, Task<ConflictChoice?>>? ConflictNeedsResolution;
    
    /// <summary>
    /// Raised when a conflict is auto-resolved.
    /// </summary>
    public event Action<FileConflict, ConflictChoice>? ConflictAutoResolved;

    public ConflictResolutionService(Settings settings, VersioningService? versioningService = null, ActivityLogService? activityLogService = null)
    {
        _settings = settings;
        _versioningService = versioningService;
        _activityLogService = activityLogService;
    }

    /// <summary>
    /// Determines how to resolve a conflict based on current settings.
    /// </summary>
    /// <param name="conflict">The conflict details.</param>
    /// <returns>The resolution choice, or null if skipped.</returns>
    public async Task<ConflictChoice?> ResolveConflictAsync(FileConflict conflict)
    {
        var mode = _settings.ConflictResolution;
        ConflictChoice choice;
        
        switch (mode)
        {
            case ConflictResolutionMode.AutoNewest:
                choice = conflict.AutoWinner == ConflictWinner.Local 
                    ? ConflictChoice.KeepLocal 
                    : ConflictChoice.KeepRemote;
                LogResolution(conflict, choice, "Auto (newest wins)");
                ConflictAutoResolved?.Invoke(conflict, choice);
                return choice;
                
            case ConflictResolutionMode.KeepBoth:
                LogResolution(conflict, ConflictChoice.KeepBoth, "Auto (keep both)");
                ConflictAutoResolved?.Invoke(conflict, ConflictChoice.KeepBoth);
                return ConflictChoice.KeepBoth;
                
            case ConflictResolutionMode.AlwaysKeepLocal:
                LogResolution(conflict, ConflictChoice.KeepLocal, "Auto (always local)");
                ConflictAutoResolved?.Invoke(conflict, ConflictChoice.KeepLocal);
                return ConflictChoice.KeepLocal;
                
            case ConflictResolutionMode.AlwaysKeepRemote:
                LogResolution(conflict, ConflictChoice.KeepRemote, "Auto (always remote)");
                ConflictAutoResolved?.Invoke(conflict, ConflictChoice.KeepRemote);
                return ConflictChoice.KeepRemote;
                
            case ConflictResolutionMode.AskUser:
                if (ConflictNeedsResolution != null)
                {
                    var userChoice = await ConflictNeedsResolution.Invoke(conflict);
                    if (userChoice.HasValue)
                    {
                        LogResolution(conflict, userChoice.Value, "User choice");
                    }
                    return userChoice;
                }
                // Fall back to auto-newest if no handler
                choice = conflict.AutoWinner == ConflictWinner.Local 
                    ? ConflictChoice.KeepLocal 
                    : ConflictChoice.KeepRemote;
                LogResolution(conflict, choice, "Auto (no handler)");
                return choice;
                
            default:
                return ConflictChoice.KeepLocal;
        }
    }

    /// <summary>
    /// Archives a file to version history before overwriting.
    /// Call this before applying a conflict resolution that overwrites local.
    /// </summary>
    public async Task ArchiveLocalBeforeOverwriteAsync(string fullPath, string relativePath)
    {
        if (_versioningService != null && _settings.VersioningEnabled)
        {
            try
            {
                await _versioningService.CreateVersionAsync(relativePath, fullPath, "Conflict");
                System.Diagnostics.Debug.WriteLine($"[ConflictResolution] Archived local version: {relativePath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConflictResolution] Failed to archive: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Generates a conflict filename for KeepBoth resolution.
    /// Example: "document.txt" -> "document (conflict from Laptop).txt"
    /// </summary>
    public static string GenerateConflictFilename(string originalPath, string peerName)
    {
        var dir = Path.GetDirectoryName(originalPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(originalPath);
        var ext = Path.GetExtension(originalPath);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        
        // Sanitize peer name for filename
        var safePeerName = string.Join("_", peerName.Split(Path.GetInvalidFileNameChars()));
        
        var conflictName = $"{name} (conflict {timestamp} from {safePeerName}){ext}";
        return Path.Combine(dir, conflictName);
    }

    private void LogResolution(FileConflict conflict, ConflictChoice choice, string method)
    {
        var message = $"Conflict on '{conflict.RelativePath}' from {conflict.SourcePeerName}: {choice} ({method})";
        System.Diagnostics.Debug.WriteLine($"[ConflictResolution] {message}");
        
        _activityLogService?.LogConflict(conflict.RelativePath, conflict.SourcePeerName, choice.ToString());
    }
}
