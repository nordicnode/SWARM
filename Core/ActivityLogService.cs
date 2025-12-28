using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace Swarm.Core;

/// <summary>
/// Type of activity log entry.
/// </summary>
public enum ActivityType
{
    // Sync operations
    SyncStarted,
    SyncCompleted,
    FileCreated,
    FileModified,
    FileDeleted,
    FileRenamed,
    FileSynced,
    
    // Transfer operations
    TransferStarted,
    TransferCompleted,
    TransferFailed,
    
    // Peer operations
    PeerConnected,
    PeerDisconnected,
    PeerTrusted,
    PeerUntrusted,
    
    // Conflict operations
    ConflictDetected,
    ConflictResolved,
    
    // System operations
    RescanStarted,
    RescanCompleted,
    Error,
    Warning,
    Info
}

/// <summary>
/// Severity level for activity log entries.
/// </summary>
public enum ActivitySeverity
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// Represents a single activity log entry.
/// </summary>
public class ActivityLogEntry
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public ActivityType Type { get; set; }
    public ActivitySeverity Severity { get; set; } = ActivitySeverity.Info;
    public string Message { get; set; } = string.Empty;
    public string? FilePath { get; set; }
    public string? PeerName { get; set; }
    public string? PeerId { get; set; }
    public long? FileSize { get; set; }
    public string? Details { get; set; }
    
    public string TypeDisplay => Type switch
    {
        ActivityType.SyncStarted => "Sync Started",
        ActivityType.SyncCompleted => "Sync Completed",
        ActivityType.FileCreated => "File Created",
        ActivityType.FileModified => "File Modified",
        ActivityType.FileDeleted => "File Deleted",
        ActivityType.FileRenamed => "File Renamed",
        ActivityType.FileSynced => "File Synced",
        ActivityType.TransferStarted => "Transfer Started",
        ActivityType.TransferCompleted => "Transfer Completed",
        ActivityType.TransferFailed => "Transfer Failed",
        ActivityType.PeerConnected => "Peer Connected",
        ActivityType.PeerDisconnected => "Peer Disconnected",
        ActivityType.PeerTrusted => "Peer Trusted",
        ActivityType.PeerUntrusted => "Peer Removed",
        ActivityType.ConflictDetected => "Conflict Detected",
        ActivityType.ConflictResolved => "Conflict Resolved",
        ActivityType.RescanStarted => "Rescan Started",
        ActivityType.RescanCompleted => "Rescan Completed",
        ActivityType.Error => "Error",
        ActivityType.Warning => "Warning",
        ActivityType.Info => "Info",
        _ => Type.ToString()
    };
}

/// <summary>
/// Service for logging and persisting application activity.
/// Maintains an in-memory buffer and periodically persists to disk.
/// </summary>
public class ActivityLogService : IDisposable
{
    private const string LogFileName = "activity.log";
    private const int MaxEntriesInMemory = 1000;
    private const int MaxEntriesOnDisk = 10000;
    
    private readonly Settings _settings;
    private readonly ConcurrentQueue<ActivityLogEntry> _entries = new();
    private readonly object _fileLock = new();
    private readonly string _logFilePath;
    private long _nextId = 1;
    private System.Threading.Timer? _persistTimer;
    private bool _isDirty;
    
    /// <summary>
    /// Raised when a new entry is added.
    /// </summary>
    public event Action<ActivityLogEntry>? EntryAdded;
    
    /// <summary>
    /// Gets all entries in memory (most recent first).
    /// </summary>
    public IEnumerable<ActivityLogEntry> Entries => _entries.Reverse();
    
    /// <summary>
    /// Gets the count of entries in memory.
    /// </summary>
    public int Count => _entries.Count;
    
    /// <summary>
    /// Clears all activity entries.
    /// </summary>
    public void ClearAll()
    {
        while (_entries.TryDequeue(out _)) { }
        _isDirty = true;
    }

    public ActivityLogService(Settings settings)
    {
        _settings = settings;
        
        // Store log file in settings directory
        var settingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Swarm");
        
        // Use portable mode if applicable
        if (Settings.IsPortableMode)
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath))
            {
                settingsDir = Path.GetDirectoryName(processPath) ?? settingsDir;
            }
        }
        
        _logFilePath = Path.Combine(settingsDir, LogFileName);
        
        // Load existing entries
        LoadFromDisk();
        
        // Start periodic persistence (every 30 seconds)
        _persistTimer = new System.Threading.Timer(_ => PersistToDisk(), null, 30000, 30000);
    }

    /// <summary>
    /// Logs a new activity entry.
    /// </summary>
    public ActivityLogEntry Log(ActivityType type, string message, 
        string? filePath = null, 
        string? peerName = null, 
        string? peerId = null,
        long? fileSize = null,
        string? details = null,
        ActivitySeverity? severity = null)
    {
        var entry = new ActivityLogEntry
        {
            Id = Interlocked.Increment(ref _nextId),
            Timestamp = DateTime.UtcNow,
            Type = type,
            Severity = severity ?? GetDefaultSeverity(type),
            Message = message,
            FilePath = filePath,
            PeerName = peerName,
            PeerId = peerId,
            FileSize = fileSize,
            Details = details
        };
        
        _entries.Enqueue(entry);
        _isDirty = true;
        
        // Trim if over limit
        while (_entries.Count > MaxEntriesInMemory)
        {
            _entries.TryDequeue(out _);
        }
        
        EntryAdded?.Invoke(entry);
        System.Diagnostics.Debug.WriteLine($"[ActivityLog] {entry.TypeDisplay}: {message}");
        
        return entry;
    }

    /// <summary>
    /// Convenience method for logging info.
    /// </summary>
    public ActivityLogEntry LogInfo(string message, string? details = null)
        => Log(ActivityType.Info, message, details: details, severity: ActivitySeverity.Info);

    /// <summary>
    /// Convenience method for logging warnings.
    /// </summary>
    public ActivityLogEntry LogWarning(string message, string? details = null)
        => Log(ActivityType.Warning, message, details: details, severity: ActivitySeverity.Warning);

    /// <summary>
    /// Convenience method for logging errors.
    /// </summary>
    public ActivityLogEntry LogError(string message, string? details = null)
        => Log(ActivityType.Error, message, details: details, severity: ActivitySeverity.Error);

    /// <summary>
    /// Logs a file sync event.
    /// </summary>
    public void LogFileSync(string relativePath, string action, string? peerName = null, long? fileSize = null)
    {
        var type = action.ToLowerInvariant() switch
        {
            "create" or "created" => ActivityType.FileCreated,
            "update" or "modified" => ActivityType.FileModified,
            "delete" or "deleted" => ActivityType.FileDeleted,
            "rename" or "renamed" => ActivityType.FileRenamed,
            _ => ActivityType.FileSynced
        };
        
        Log(type, $"{action}: {relativePath}", 
            filePath: relativePath, 
            peerName: peerName, 
            fileSize: fileSize);
    }

    /// <summary>
    /// Logs a peer connection event.
    /// </summary>
    public void LogPeerEvent(string peerName, string peerId, bool connected)
    {
        Log(connected ? ActivityType.PeerConnected : ActivityType.PeerDisconnected,
            $"{peerName} {(connected ? "connected" : "disconnected")}",
            peerName: peerName,
            peerId: peerId);
    }

    /// <summary>
    /// Logs a transfer event.
    /// </summary>
    public void LogTransfer(string fileName, string peerName, bool completed, bool isUpload, long? fileSize = null, string? error = null)
    {
        var direction = isUpload ? "to" : "from";
        
        if (completed)
        {
            Log(ActivityType.TransferCompleted,
                $"Transferred {fileName} {direction} {peerName}",
                filePath: fileName,
                peerName: peerName,
                fileSize: fileSize,
                severity: ActivitySeverity.Success);
        }
        else if (error != null)
        {
            Log(ActivityType.TransferFailed,
                $"Failed to transfer {fileName} {direction} {peerName}",
                filePath: fileName,
                peerName: peerName,
                details: error,
                severity: ActivitySeverity.Error);
        }
        else
        {
            Log(ActivityType.TransferStarted,
                $"Transferring {fileName} {direction} {peerName}",
                filePath: fileName,
                peerName: peerName,
                fileSize: fileSize);
        }
    }

    /// <summary>
    /// Logs a conflict event.
    /// </summary>
    public void LogConflict(string relativePath, string peerName, string resolution)
    {
        Log(ActivityType.ConflictDetected,
            $"Conflict: {relativePath} (from {peerName}) - {resolution}",
            filePath: relativePath,
            peerName: peerName,
            details: resolution,
            severity: ActivitySeverity.Warning);
    }

    /// <summary>
    /// Gets entries filtered by type.
    /// </summary>
    public IEnumerable<ActivityLogEntry> GetEntriesByType(ActivityType type)
        => _entries.Where(e => e.Type == type).Reverse();

    /// <summary>
    /// Gets entries filtered by severity.
    /// </summary>
    public IEnumerable<ActivityLogEntry> GetEntriesBySeverity(ActivitySeverity severity)
        => _entries.Where(e => e.Severity == severity).Reverse();

    /// <summary>
    /// Gets entries within a date range.
    /// </summary>
    public IEnumerable<ActivityLogEntry> GetEntriesByDateRange(DateTime start, DateTime end)
        => _entries.Where(e => e.Timestamp >= start && e.Timestamp <= end).Reverse();

    /// <summary>
    /// Searches entries by message content.
    /// </summary>
    public IEnumerable<ActivityLogEntry> Search(string query)
    {
        var lowerQuery = query.ToLowerInvariant();
        return _entries.Where(e => 
            e.Message.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (e.FilePath?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (e.PeerName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (e.Details?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
        ).Reverse();
    }

    /// <summary>
    /// Gets the most recent N entries.
    /// </summary>
    public IEnumerable<ActivityLogEntry> GetRecentEntries(int count)
        => _entries.Reverse().Take(count);

    /// <summary>
    /// Clears all entries from memory (does not affect persisted log).
    /// </summary>
    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
        _isDirty = true;
    }

    /// <summary>
    /// Exports entries to a file.
    /// </summary>
    public async Task ExportAsync(string filePath, bool asJson = false)
    {
        var entries = _entries.Reverse().ToList();
        
        if (asJson)
        {
            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json);
        }
        else
        {
            // CSV format
            var lines = new List<string>
            {
                "Timestamp,Type,Severity,Message,FilePath,PeerName,FileSize,Details"
            };
            
            foreach (var entry in entries)
            {
                var line = $"\"{entry.Timestamp:yyyy-MM-dd HH:mm:ss}\"," +
                           $"\"{entry.TypeDisplay}\"," +
                           $"\"{entry.Severity}\"," +
                           $"\"{EscapeCsv(entry.Message)}\"," +
                           $"\"{EscapeCsv(entry.FilePath ?? "")}\"," +
                           $"\"{EscapeCsv(entry.PeerName ?? "")}\"," +
                           $"\"{entry.FileSize?.ToString() ?? ""}\"," +
                           $"\"{EscapeCsv(entry.Details ?? "")}\"";
                lines.Add(line);
            }
            
            await File.WriteAllLinesAsync(filePath, lines);
        }
    }

    private static string EscapeCsv(string value)
        => value.Replace("\"", "\"\"");

    private static ActivitySeverity GetDefaultSeverity(ActivityType type) => type switch
    {
        ActivityType.Error => ActivitySeverity.Error,
        ActivityType.TransferFailed => ActivitySeverity.Error,
        ActivityType.Warning => ActivitySeverity.Warning,
        ActivityType.ConflictDetected => ActivitySeverity.Warning,
        ActivityType.TransferCompleted => ActivitySeverity.Success,
        ActivityType.SyncCompleted => ActivitySeverity.Success,
        ActivityType.FileSynced => ActivitySeverity.Success,
        _ => ActivitySeverity.Info
    };

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_logFilePath)) return;
            
            var lines = File.ReadAllLines(_logFilePath);
            
            foreach (var line in lines.TakeLast(MaxEntriesInMemory))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<ActivityLogEntry>(line);
                    if (entry != null)
                    {
                        _entries.Enqueue(entry);
                        if (entry.Id >= _nextId)
                            _nextId = entry.Id + 1;
                    }
                }
                catch { /* Skip malformed lines */ }
            }
            
            System.Diagnostics.Debug.WriteLine($"[ActivityLog] Loaded {_entries.Count} entries from disk");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ActivityLog] Failed to load: {ex.Message}");
        }
    }

    private void PersistToDisk()
    {
        if (!_isDirty) return;
        
        lock (_fileLock)
        {
            try
            {
                var entries = _entries.ToArray();
                var dir = Path.GetDirectoryName(_logFilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                
                // Write as JSON Lines format
                using var writer = new StreamWriter(_logFilePath, append: false);
                foreach (var entry in entries.TakeLast(MaxEntriesOnDisk))
                {
                    writer.WriteLine(JsonSerializer.Serialize(entry));
                }
                
                _isDirty = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ActivityLog] Failed to persist: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _persistTimer?.Dispose();
        _persistTimer = null;
        PersistToDisk(); // Final save
    }
}
