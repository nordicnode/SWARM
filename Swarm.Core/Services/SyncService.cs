using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Swarm.Core.Helpers;
using Swarm.Core.Models;

namespace Swarm.Core.Services;

public struct SyncProgress
{
    public int TotalFiles { get; set; }
    public int CompletedFiles { get; set; }
    public string CurrentFileName { get; set; }
    public double CurrentFilePercent { get; set; } // 0-100
}

/// <summary>
/// Service for monitoring and synchronizing a local folder with peers.
/// </summary>
public class SyncService : IDisposable
{
    private readonly Settings _settings;
    private readonly DiscoveryService _discoveryService;
    private readonly TransferService _transferService;
    private readonly VersioningService _versioningService;
    private readonly SwarmIgnoreService _swarmIgnoreService;
    private readonly ActivityLogService? _activityLogService;
    
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cts;
    
    // Track file states for change detection
    private readonly ConcurrentDictionary<string, SyncedFile> _fileStates = new();
    
    // Debounce rapid changes
    private readonly ConcurrentDictionary<string, DateTime> _pendingChanges = new();
    private readonly ConcurrentDictionary<string, (string OldPath, DateTime Time)> _pendingRenames = new();
    private const int DEBOUNCE_MS = ProtocolConstants.SYNC_DEBOUNCE_MS;
    
    // Ignore list for files currently being written by sync
    private readonly ConcurrentDictionary<string, DateTime> _ignoreList = new();
    private const int IGNORE_DURATION_MS = ProtocolConstants.SYNC_IGNORE_DURATION_MS;
    
    // Activity log debounce to prevent duplicate entries
    private readonly ConcurrentDictionary<string, DateTime> _activityLogDebounce = new();
    private const int ACTIVITY_DEBOUNCE_MS = 2000; // 2 second debounce for activity logging

    // Directory rename coalescence: track potential directory renames detected from file rename patterns
    // Key: oldParentPath, Value: (newParentPath, set of affected file paths, first seen time)
    private readonly ConcurrentDictionary<string, (string NewParentPath, HashSet<string> AffectedFiles, DateTime FirstSeen)> 
        _directoryRenameCandidates = new();
    
    // Track recently renamed directories to suppress redundant child file rename broadcasts
    private readonly ConcurrentDictionary<string, DateTime> _recentDirectoryRenames = new();
    
    // Extended debounce for directory rename detection (longer than file debounce to collect all events)
    private const int DIRECTORY_RENAME_DEBOUNCE_MS = 500;
    
    // Threshold: if this many files share a parent path change, treat as directory rename
    private const int DIRECTORY_RENAME_THRESHOLD = 3;
    
    // How long to remember a directory rename for child suppression
    private const int DIRECTORY_RENAME_MEMORY_MS = 2000;

    public string SyncFolderPath => _settings.SyncFolderPath;
    public bool IsEnabled => _settings.IsSyncEnabled;
    public bool IsRunning => _watcher != null;
    
    // Dashboard tracking
    private DateTime? _lastSyncTime;
    private long _sessionBytesTransferred;
    
    /// <summary>
    /// Gets the last sync completion time.
    /// </summary>
    public DateTime? LastSyncTime => _lastSyncTime;
    
    /// <summary>
    /// Gets the number of files in the sync folder.
    /// </summary>
    public int GetTrackedFileCount()
    {
        try
        {
            if (!Directory.Exists(_settings.SyncFolderPath))
                return 0;
            return Directory.EnumerateFiles(_settings.SyncFolderPath, "*", SearchOption.AllDirectories).Count();
        }
        catch
        {
            return _fileStates.Count; // Fallback to tracked state
        }
    }
    
    /// <summary>
    /// Gets the total bytes transferred this session.
    /// </summary>
    public long GetSessionBytesTransferred() => _sessionBytesTransferred;
    
    /// <summary>
    /// Adds to the session bytes transferred count.
    /// </summary>
    public void AddBytesTransferred(long bytes)
    {
        Interlocked.Add(ref _sessionBytesTransferred, bytes);
    }

    public event Action<SyncedFile>? FileChanged;
    public event Action<string>? SyncStatusChanged;
    public event Action<SyncedFile>? IncomingSyncFile;
    public event Action<string, string?>? FileConflictDetected;
    public event Action<SyncProgress>? SyncProgressChanged;
    
    /// <summary>
    /// Raised when a deep rescan is recommended (e.g., after FSW buffer overflow).
    /// External services like RescanService can subscribe to this.
    /// </summary>
    public event Action? RescanRequested;

    // Progress tracking fields (totalFilesCount is set by bulk sync operations)
    private int _totalFilesCount = 0;
    private int _completedFilesCount = 0;
    private readonly object _progressLock = new();


    // Track pending delta sync operations (relativePath -> peer awaiting delta)
    private readonly ConcurrentDictionary<string, (Peer peer, SyncedFile syncFile)> _pendingDeltaSyncs = new();

    public SyncService(Settings settings, DiscoveryService discoveryService, TransferService transferService, VersioningService versioningService, ActivityLogService? activityLogService = null)
    {
        _settings = settings;
        _discoveryService = discoveryService;
        _transferService = transferService;
        _versioningService = versioningService;
        _activityLogService = activityLogService;
        _swarmIgnoreService = new SwarmIgnoreService(settings);

        // Subscribe to TransferService sync events
        _transferService.SyncFileReceived += OnSyncFileReceived;
        _transferService.SyncDeleteReceived += OnSyncDeleteReceived;
        _transferService.SyncManifestReceived += OnSyncManifestReceived;
        _transferService.SyncFileRequested += OnSyncFileRequested;
        _transferService.SyncRenameReceived += OnSyncRenameReceived;

        // Subscribe to delta sync events
        _transferService.SignaturesRequested += OnSignaturesRequested;
        _transferService.BlockSignaturesReceived += OnBlockSignaturesReceived;
        _transferService.DeltaDataReceived += OnDeltaDataReceived;
        
        // Subscribe to peer discovery to auto-sync with new trusted peers
        _discoveryService.PeerDiscovered += OnPeerDiscovered;
    }
    
    private async void OnPeerDiscovered(Peer peer)
    {
        // Only sync with trusted peers that have sync enabled
        if (!peer.IsTrusted || !peer.IsSyncEnabled || !IsRunning)
            return;
            
        System.Diagnostics.Debug.WriteLine($"[SYNC] Trusted peer {peer.Name} connected, sending manifest...");
        
        try
        {
            // Short delay to ensure peer is fully initialized
            await Task.Delay(500);
            await SendManifestToPeer(peer);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SYNC] Failed to sync with {peer.Name}: {ex.Message}");
        }
    }


    /// <summary>
    /// Starts monitoring the sync folder for changes.
    /// </summary>
    public void Start()
    {
        if (!_settings.IsSyncEnabled) return;

        _cts = new CancellationTokenSource();
        _settings.EnsureSyncFolderExists();

        // Run initialization in background to prevent UI freeze
        Task.Run(async () =>
        {
            try
            {
                // Check if cancelled before starting heavy work
                if (_cts.Token.IsCancellationRequested) return;

                // Build initial file state
                await BuildInitialFileStates();

                // Check again before enabling watcher
                if (_cts.Token.IsCancellationRequested) return;

                // Set up FileSystemWatcher
                _watcher = new FileSystemWatcher(_settings.SyncFolderPath)
                {
                    NotifyFilter = NotifyFilters.FileName
                                 | NotifyFilters.DirectoryName
                                 | NotifyFilters.LastWrite
                                 | NotifyFilters.Size
                                 | NotifyFilters.CreationTime,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true
                };

                _watcher.Created += OnFileCreated;
                _watcher.Changed += OnFileChanged;
                _watcher.Deleted += OnFileDeleted;
                _watcher.Renamed += OnFileRenamed;
                _watcher.Error += OnWatcherError;

                // Start debounce processor
                _ = Task.Run(() => ProcessPendingChanges(_cts.Token));

                SyncStatusChanged?.Invoke("Sync enabled - Watching for changes");
                System.Diagnostics.Debug.WriteLine($"SyncService started, watching: {_settings.SyncFolderPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to start SyncService: {ex.Message}");
                SyncStatusChanged?.Invoke("Sync failed to start");
            }
        });
    }

    /// <summary>
    /// Stops monitoring the sync folder.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        SyncStatusChanged?.Invoke("Sync disabled");
    }

    /// <summary>
    /// Changes the sync folder path.
    /// </summary>
    public void SetSyncFolderPath(string newPath)
    {
        var wasRunning = IsRunning;
        
        if (wasRunning) Stop();
        
        _settings.SyncFolderPath = newPath;
        _settings.Save();
        _fileStates.Clear();
        
        if (wasRunning) Start();
    }

    /// <summary>
    /// Enables or disables synchronization.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled == _settings.IsSyncEnabled) return;
        
        _settings.IsSyncEnabled = enabled;
        _settings.Save();

        if (enabled)
            Start();
        else
            Stop();
    }

    /// <summary>
    /// Forces a full sync with all connected peers.
    /// </summary>
    public async Task ForceSyncAsync()
    {
        SyncStatusChanged?.Invoke("Syncing...");
        
        // Rebuild local state
        await BuildInitialFileStates();
        
        // Send manifest to all peers with sync enabled
        foreach (var peer in _discoveryService.Peers.Where(p => p.IsSyncEnabled))
        {
            await SendManifestToPeer(peer);
        }

        _lastSyncTime = DateTime.Now;
        SyncStatusChanged?.Invoke("Sync complete");
    }

    /// <summary>
    /// Handles an incoming sync file from a peer.
    /// </summary>
    public async Task HandleIncomingSyncFile(SyncedFile syncFile, Stream dataStream)
    {
        var localPath = Path.Combine(_settings.SyncFolderPath, syncFile.RelativePath);
        var normalizedPath = FileHelpers.NormalizePath(localPath);
        
        // Add to ignore list to prevent echo
        _ignoreList[normalizedPath] = DateTime.Now;

        try
        {
            switch (syncFile.Action)
            {
                case SyncAction.Create:
                case SyncAction.Update:
                    await WriteIncomingFile(syncFile, localPath, dataStream);
                    break;

                case SyncAction.Delete:
                    DeleteLocalFile(syncFile, localPath);
                    break;

                case SyncAction.Rename:
                    // Rename handled separately
                    break;
            }

            // Update our state
            if (syncFile.Action != SyncAction.Delete)
            {
                _fileStates[syncFile.RelativePath] = syncFile;
            }
            else
            {
                _fileStates.TryRemove(syncFile.RelativePath, out _);
            }

            IncomingSyncFile?.Invoke(syncFile);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to handle sync file {syncFile.RelativePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the current manifest of all synced files.
    /// </summary>
    public IEnumerable<SyncedFile> GetManifest()
    {
        return _fileStates.Values.ToList();
    }

    /// <summary>
    /// Gets the current file states dictionary for integrity verification.
    /// </summary>
    public IReadOnlyDictionary<string, SyncedFile> GetFileStates()
    {
        return _fileStates;
    }


    /// <summary>
    /// Compares incoming manifest with local state and resolves differences.
    /// </summary>
    public async Task ProcessIncomingManifest(IEnumerable<SyncedFile> remoteManifest, Peer sourcePeer)
    {
        var filesToRequest = new List<string>();
        var filesToSend = new List<SyncedFile>();

        foreach (var remoteFile in remoteManifest)
        {
            if (_fileStates.TryGetValue(remoteFile.RelativePath, out var localFile))
            {
                // File exists locally - check for conflict
                if (remoteFile.ContentHash != localFile.ContentHash)
                {
                    // Check for future timestamp (Clock Drift Protection)
                    if (remoteFile.LastModified > DateTime.UtcNow.AddMinutes(10))
                    {
                        System.Diagnostics.Debug.WriteLine($"[Warning] Peer {sourcePeer.Name} has file {remoteFile.RelativePath} from the future ({remoteFile.LastModified}). Ignoring to prevent corruption.");
                        continue;
                    }

                    // Conflict resolution
                    bool shouldRequest = false;

                    if (remoteFile.LastModified > localFile.LastModified)
                    {
                        // Remote is newer - Last Write Wins
                        shouldRequest = true;
                    }
                    else if (remoteFile.LastModified == localFile.LastModified)
                    {
                        // Timestamps equal but content differs - Tie-break using ContentHash
                        // Deterministic convergence: lexicographically smaller hash wins
                        if (string.Compare(remoteFile.ContentHash, localFile.ContentHash, StringComparison.Ordinal) < 0)
                        {
                            shouldRequest = true;
                        }
                    }

                    if (shouldRequest)
                    {
                        await RequestFileFromPeer(sourcePeer, remoteFile.RelativePath);
                    }
                    // Else: local is newer (or wins tie-break), we'll push our version during next sync
                }
            }
            else
            {
                // File doesn't exist locally - request it
                if (remoteFile.Action != SyncAction.Delete)
                {
                    // Check for future timestamp on new files too
                    if (remoteFile.LastModified > DateTime.UtcNow.AddMinutes(10))
                    {
                        System.Diagnostics.Debug.WriteLine($"[Warning] Peer {sourcePeer.Name} offering new file {remoteFile.RelativePath} from the future. Ignoring.");
                        continue;
                    }
                    
                    await RequestFileFromPeer(sourcePeer, remoteFile.RelativePath);
                }
            }
        }

        // Check for files we have that remote doesn't
        var remoteSet = remoteManifest.Select(f => f.RelativePath).ToHashSet();
        foreach (var localFile in _fileStates.Values)
        {
            if (!remoteSet.Contains(localFile.RelativePath))
            {
                // We have a file that remote doesn't - send it
                await SendFileToPeer(sourcePeer, localFile);
            }
        }
    }

    #region Private Methods

    private async Task BuildInitialFileStates()
    {
        _fileStates.Clear();
        
        if (!Directory.Exists(_settings.SyncFolderPath)) return;

        foreach (var filePath in Directory.EnumerateFiles(_settings.SyncFolderPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                var relativePath = Path.GetRelativePath(_settings.SyncFolderPath, filePath);
                var fileInfo = new FileInfo(filePath);
                
                _fileStates[relativePath] = new SyncedFile
                {
                    RelativePath = relativePath,
                    ContentHash = await ComputeFileHash(filePath),
                    LastModified = fileInfo.LastWriteTimeUtc,
                    FileSize = fileInfo.Length,
                    Action = SyncAction.Create,
                    SourcePeerId = _discoveryService.LocalId,
                    IsDirectory = false
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to read file {filePath}: {ex.Message}");
            }
        }

        System.Diagnostics.Debug.WriteLine($"Built initial state with {_fileStates.Count} files");
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        QueueChange(e.FullPath, SyncAction.Create);
        
        var relativePath = Path.GetRelativePath(_settings.SyncFolderPath, e.FullPath);
        LogActivityDebounced(relativePath, "created");
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        QueueChange(e.FullPath, SyncAction.Update);
        
        // Don't log "modified" if we just logged "created" for this file
        var relativePath = Path.GetRelativePath(_settings.SyncFolderPath, e.FullPath);
        LogActivityDebounced(relativePath, "modified");
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        QueueChange(e.FullPath, SyncAction.Delete);
        
        var relativePath = Path.GetRelativePath(_settings.SyncFolderPath, e.FullPath);
        LogActivityDebounced(relativePath, "deleted");
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        
        // Queue as a proper rename operation with both old and new paths
        QueueRename(e.OldFullPath, e.FullPath);
        
        var oldRelativePath = Path.GetRelativePath(_settings.SyncFolderPath, e.OldFullPath);
        var newRelativePath = Path.GetRelativePath(_settings.SyncFolderPath, e.FullPath);
        _activityLogService?.LogFileSync(newRelativePath, $"renamed from {oldRelativePath}");
    }
    
    /// <summary>
    /// Logs file activity with debounce to prevent duplicate entries from multiple FSW events.
    /// </summary>
    private void LogActivityDebounced(string relativePath, string action)
    {
        // Key on file path only - ignore any subsequent events for the same file within debounce window
        var key = relativePath.ToLowerInvariant();
        var now = DateTime.Now;
        
        // Check if we recently logged ANY action for this file
        if (_activityLogDebounce.TryGetValue(key, out var lastLog))
        {
            if ((now - lastLog).TotalMilliseconds < ACTIVITY_DEBOUNCE_MS)
            {
                return; // Skip - already logged recently
            }
        }
        
        _activityLogDebounce[key] = now;
        _activityLogService?.LogFileSync(relativePath, action);
        
        // Clean old entries periodically
        if (_activityLogDebounce.Count > 100)
        {
            var cutoff = now.AddMilliseconds(-ACTIVITY_DEBOUNCE_MS * 2);
            foreach (var old in _activityLogDebounce.Where(kv => kv.Value < cutoff).ToList())
            {
                _activityLogDebounce.TryRemove(old.Key, out _);
            }
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var exception = e.GetException();
        System.Diagnostics.Debug.WriteLine($"FileSystemWatcher error: {exception.Message}");
        
        // Detect buffer overflow (common when many files change at once)
        // The internal buffer can overflow if there's heavy file system activity
        bool isPotentialBufferOverflow = false;
        if (exception is System.ComponentModel.Win32Exception win32Ex)
        {
            // Error code 122 (ERROR_INSUFFICIENT_BUFFER) or general internal buffer overflow
            if (win32Ex.NativeErrorCode == 122)
            {
                System.Diagnostics.Debug.WriteLine("[CRITICAL] FileSystemWatcher internal buffer overflow detected!");
                isPotentialBufferOverflow = true;
            }
        }
        
        // Also treat "too many changes" type errors as potential overflow
        if (exception.Message.Contains("buffer") || exception.Message.Contains("overflow"))
        {
            isPotentialBufferOverflow = true;
        }
        
        SyncStatusChanged?.Invoke("Sync error - recovering...");
        
        // Attempt to restart the watcher
        Stop();
        Start();
        
        // Always trigger a full sync after watcher error to catch any missed changes
        // This is especially important for buffer overflow scenarios where events were lost
        _ = Task.Run(async () =>
        {
            try
            {
                // Give the watcher time to stabilize after restart
                await Task.Delay(2000);
                
                if (isPotentialBufferOverflow)
                {
                    System.Diagnostics.Debug.WriteLine("Triggering full sync to recover from buffer overflow");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("Triggering full sync to recover from watcher error");
                }
                
                await ForceSyncAsync();
                
                // After a buffer overflow, also recommend a deep rescan to catch any missed changes
                if (isPotentialBufferOverflow)
                {
                    RescanRequested?.Invoke();
                }
            }
            catch (Exception syncEx)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to sync after watcher error: {syncEx.Message}");
            }
        });
    }

    private bool ShouldIgnore(string path)
    {
        var normalizedPath = FileHelpers.NormalizePath(path);

        // Check if this file is being written by sync
        if (_ignoreList.TryGetValue(normalizedPath, out var ignoreTime))
        {
            if ((DateTime.Now - ignoreTime).TotalMilliseconds < IGNORE_DURATION_MS)
            {
                return true;
            }
            _ignoreList.TryRemove(normalizedPath, out _);
        }

        // Ignore system/hidden files
        var fileName = Path.GetFileName(path);
        if (fileName.StartsWith('.') || fileName.StartsWith("~"))
            return true;

        // Check .swarmignore patterns
        try
        {
            var relativePath = Path.GetRelativePath(_settings.SyncFolderPath, path);
            if (_swarmIgnoreService.IsIgnored(relativePath))
            {
                System.Diagnostics.Debug.WriteLine($"[SwarmIgnore] Ignoring: {relativePath}");
                return true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SwarmIgnore] Error checking path: {ex.Message}");
        }

        return false;
    }

    private void QueueChange(string fullPath, SyncAction action)
    {
        _pendingChanges[fullPath] = DateTime.Now;
        System.Diagnostics.Debug.WriteLine($"Queued change: {action} - {fullPath}");
    }

    private void QueueRename(string oldFullPath, string newFullPath)
    {
        _pendingRenames[newFullPath] = (oldFullPath, DateTime.Now);
        
        // Check if this could be part of a directory rename:
        // Same filename but different parent directory = files moved with their parent
        var oldDir = Path.GetDirectoryName(oldFullPath) ?? "";
        var newDir = Path.GetDirectoryName(newFullPath) ?? "";
        
        if (oldDir != newDir && 
            Path.GetFileName(oldFullPath) == Path.GetFileName(newFullPath))
        {
            // Same filename, different parent = potential directory rename
            TrackDirectoryRenameCandidate(oldDir, newDir, newFullPath);
        }
        
        System.Diagnostics.Debug.WriteLine($"Queued rename: {oldFullPath} -> {newFullPath}");
    }

    /// <summary>
    /// Tracks a file rename as a potential directory rename candidate.
    /// Multiple files with the same parent path change indicate a directory was renamed.
    /// </summary>
    private void TrackDirectoryRenameCandidate(string oldParent, string newParent, string newFilePath)
    {
        _directoryRenameCandidates.AddOrUpdate(
            oldParent,
            _ => (newParent, new HashSet<string> { newFilePath }, DateTime.Now),
            (_, existing) =>
            {
                existing.AffectedFiles.Add(newFilePath);
                return existing;
            });
    }

    /// <summary>
    /// Marks a directory as recently renamed to suppress child file rename broadcasts.
    /// </summary>
    private void MarkDirectoryAsRenamed(string oldPath)
    {
        _recentDirectoryRenames[oldPath.ToLowerInvariant()] = DateTime.Now;
    }

    /// <summary>
    /// Checks if a path is a subpath of a recently renamed directory.
    /// Used to suppress individual file renames that are part of a directory rename.
    /// </summary>
    private bool IsSubpathOfRenamedDirectory(string path)
    {
        var normalizedPath = path.ToLowerInvariant();
        var now = DateTime.Now;
        
        foreach (var (dirPath, timestamp) in _recentDirectoryRenames.ToList())
        {
            // Clean up old entries
            if ((now - timestamp).TotalMilliseconds > DIRECTORY_RENAME_MEMORY_MS)
            {
                _recentDirectoryRenames.TryRemove(dirPath, out _);
                continue;
            }
            
            // Check if this path starts with the renamed directory path
            if (normalizedPath.StartsWith(dirPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.Equals(dirPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private async Task ProcessPendingChanges(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(100, ct);
                
                var now = DateTime.Now;

                // === STEP 1: Process confirmed directory renames first ===
                // These are explicit directory rename events from FileSystemWatcher
                var directoryRenames = _pendingRenames
                    .Where(kvp => (now - kvp.Value.Time).TotalMilliseconds >= DEBOUNCE_MS
                                  && Directory.Exists(kvp.Key))
                    .ToList();

                foreach (var kvp in directoryRenames)
                {
                    var newPath = kvp.Key;
                    var oldPath = kvp.Value.OldPath;
                    
                    if (_pendingRenames.TryRemove(newPath, out _))
                    {
                        // Mark this directory's old path so we can suppress child file renames
                        MarkDirectoryAsRenamed(oldPath);
                        await ProcessFileRename(oldPath, newPath);
                    }
                }

                // === STEP 2: Coalesce file renames into directory renames ===
                // If multiple files share the same parent path change, treat as directory rename
                var candidatesToProcess = _directoryRenameCandidates
                    .Where(kvp => (now - kvp.Value.FirstSeen).TotalMilliseconds >= DIRECTORY_RENAME_DEBOUNCE_MS)
                    .ToList();

                foreach (var candidate in candidatesToProcess)
                {
                    if (_directoryRenameCandidates.TryRemove(candidate.Key, out var info))
                    {
                        if (info.AffectedFiles.Count >= DIRECTORY_RENAME_THRESHOLD)
                        {
                            // Enough files share this parent change - treat as directory rename
                            System.Diagnostics.Debug.WriteLine(
                                $"[DirectoryRenameCoalescence] Coalescing {info.AffectedFiles.Count} file renames into directory rename: " +
                                $"{candidate.Key} -> {info.NewParentPath}");
                            
                            // Remove individual file renames from pending queue
                            foreach (var filePath in info.AffectedFiles)
                            {
                                _pendingRenames.TryRemove(filePath, out _);
                            }
                            
                            // Mark as renamed to suppress any stragglers
                            MarkDirectoryAsRenamed(candidate.Key);
                            
                            // Process as single directory rename (only if directory still exists)
                            if (Directory.Exists(info.NewParentPath))
                            {
                                await ProcessFileRename(candidate.Key, info.NewParentPath);
                            }
                        }
                        // Else: not enough files, individual renames will be processed below
                    }
                }

                // === STEP 3: Process remaining individual file renames ===
                // Skip files whose parent was already renamed (subsumed by directory rename)
                var renamesToProcess = _pendingRenames
                    .Where(kvp => (now - kvp.Value.Time).TotalMilliseconds >= DEBOUNCE_MS)
                    .Where(kvp => !IsSubpathOfRenamedDirectory(kvp.Value.OldPath))
                    .Select(kvp => (NewPath: kvp.Key, OldPath: kvp.Value.OldPath))
                    .ToList();

                foreach (var (newPath, oldPath) in renamesToProcess)
                {
                    if (_pendingRenames.TryRemove(newPath, out _))
                    {
                        await ProcessFileRename(oldPath, newPath);
                    }
                }

                // === STEP 4: Process other changes (create, update, delete) ===
                var toProcess = _pendingChanges
                    .Where(kvp => (now - kvp.Value).TotalMilliseconds >= DEBOUNCE_MS)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var fullPath in toProcess)
                {
                    if (_pendingChanges.TryRemove(fullPath, out _))
                    {
                        await ProcessFileChange(fullPath);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error processing changes: {ex.Message}");
            }
        }
    }

    private async Task ProcessFileChange(string fullPath)
    {
        var relativePath = Path.GetRelativePath(_settings.SyncFolderPath, fullPath);
        SyncedFile syncFile;

        if (File.Exists(fullPath))
        {
            // File exists - create or update
            var fileInfo = new FileInfo(fullPath);
            var hash = await ComputeFileHash(fullPath);
            
            var action = _fileStates.ContainsKey(relativePath) ? SyncAction.Update : SyncAction.Create;
            
            syncFile = new SyncedFile
            {
                RelativePath = relativePath,
                ContentHash = hash,
                LastModified = fileInfo.LastWriteTimeUtc,
                FileSize = fileInfo.Length,
                Action = action,
                SourcePeerId = _discoveryService.LocalId,
                IsDirectory = false
            };

            _fileStates[relativePath] = syncFile;
        }
        else if (Directory.Exists(fullPath))
        {
            // Directory created
            syncFile = new SyncedFile
            {
                RelativePath = relativePath,
                Action = SyncAction.Create,
                SourcePeerId = _discoveryService.LocalId,
                IsDirectory = true,
                LastModified = DateTime.UtcNow
            };
        }
        else
        {
            // File/folder deleted
            _fileStates.TryRemove(relativePath, out var existing);
            
            syncFile = new SyncedFile
            {
                RelativePath = relativePath,
                Action = SyncAction.Delete,
                SourcePeerId = _discoveryService.LocalId,
                IsDirectory = existing?.IsDirectory ?? false,
                LastModified = DateTime.UtcNow
            };
        }

        FileChanged?.Invoke(syncFile);

        // Broadcast to all peers
        await BroadcastChangeToAllPeers(syncFile, fullPath);
    }

    private async Task ProcessFileRename(string oldFullPath, string newFullPath)
    {
        var oldRelativePath = Path.GetRelativePath(_settings.SyncFolderPath, oldFullPath);
        var newRelativePath = Path.GetRelativePath(_settings.SyncFolderPath, newFullPath);
        
        // Check if the new path exists (it should after rename)
        var isDirectory = Directory.Exists(newFullPath);
        var isFile = File.Exists(newFullPath);
        
        if (!isDirectory && !isFile)
        {
            System.Diagnostics.Debug.WriteLine($"Rename target no longer exists: {newFullPath}");
            return;
        }

        // Update our local file state
        if (isDirectory)
        {
            // For directory renames, update all child file states
            var oldPrefix = oldRelativePath + Path.DirectorySeparatorChar;
            var filesToUpdate = _fileStates
                .Where(kvp => kvp.Key.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase) ||
                              kvp.Key.Equals(oldRelativePath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            
            foreach (var kvp in filesToUpdate)
            {
                if (_fileStates.TryRemove(kvp.Key, out var state))
                {
                    // Update the relative path to reflect the new directory name
                    var newPath = newRelativePath + kvp.Key.Substring(oldRelativePath.Length);
                    state.RelativePath = newPath;
                    _fileStates[newPath] = state;
                }
            }
            
            System.Diagnostics.Debug.WriteLine(
                $"[DirectoryRename] Updated {filesToUpdate.Count} child file states for {oldRelativePath} -> {newRelativePath}");
        }
        else
        {
            // Single file rename
            if (_fileStates.TryRemove(oldRelativePath, out var existingState))
            {
                existingState.RelativePath = newRelativePath;
                _fileStates[newRelativePath] = existingState;
            }
        }

        var syncFile = new SyncedFile
        {
            RelativePath = newRelativePath,
            OldRelativePath = oldRelativePath,
            Action = SyncAction.Rename,
            SourcePeerId = _discoveryService.LocalId,
            IsDirectory = isDirectory,
            LastModified = DateTime.UtcNow
        };

        FileChanged?.Invoke(syncFile);

        // Broadcast rename to all peers
        await BroadcastChangeToAllPeers(syncFile, newFullPath);
    }

    private async Task BroadcastChangeToAllPeers(SyncedFile syncFile, string fullPath)
    {
        foreach (var peer in _discoveryService.Peers.Where(p => p.IsSyncEnabled))
        {
            try
            {
                await SendFileToPeer(peer, syncFile);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to sync to {peer.Name}: {ex.Message}");
            }
        }
    }

    private async Task SendFileToPeer(Peer peer, SyncedFile syncFile)
    {
        if (syncFile.Action == SyncAction.Delete)
        {
            await _transferService.SendSyncDelete(peer, syncFile);
        }
        else if (syncFile.Action == SyncAction.Rename)
        {
            // Send rename notification - no file data needed!
            await _transferService.SendSyncRename(peer, syncFile);
        }
        else if (!syncFile.IsDirectory)
        {
            var fullPath = Path.Combine(_settings.SyncFolderPath, syncFile.RelativePath);
            if (File.Exists(fullPath))
            {
                var fileInfo = new FileInfo(fullPath);

                // Use delta sync for files larger than threshold
                if (fileInfo.Length >= ProtocolConstants.DELTA_THRESHOLD)
                {
                    System.Diagnostics.Debug.WriteLine($"Using delta sync for large file: {syncFile.RelativePath} ({fileInfo.Length} bytes)");
                    
                    // Store pending delta sync info and request signatures from peer
                    _pendingDeltaSyncs[syncFile.RelativePath] = (peer, syncFile);
                    await _transferService.RequestBlockSignatures(peer, syncFile.RelativePath);
                    // Delta will be sent when we receive their signatures via OnBlockSignaturesReceived
                }
                else
                {
                    // Small file - send full content
                    await _transferService.SendSyncFile(peer, fullPath, syncFile, (sent, total) => 
                    {
                        ReportProgress(syncFile.RelativePath, (double)sent / total * 100);
                    });
                    
                    lock(_progressLock)
                    {
                        _completedFilesCount++;
                        ReportProgress(null, 0);
                    }
                }
            }
        }
        else
        {
            await _transferService.SendSyncDirectory(peer, syncFile);
        }
    }


    private async Task SendManifestToPeer(Peer peer)
    {
        await _transferService.SendSyncManifest(peer, GetManifest().ToList());
    }

    private async Task RequestFileFromPeer(Peer peer, string relativePath)
    {
        await _transferService.RequestSyncFile(peer, relativePath);
    }

    private async Task WriteIncomingFile(SyncedFile syncFile, string localPath, Stream dataStream)
    {
        // Ensure directory exists
        var directory = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (syncFile.IsDirectory)
        {
            Directory.CreateDirectory(localPath);
        }
        else
        {
            // Version control: Create version before overwriting if content differs
            if (File.Exists(localPath))
            {
                try
                {
                    var currentHash = await ComputeFileHash(localPath);
                    if (currentHash != syncFile.ContentHash)
                    {
                        // Use versioning service if enabled, otherwise fallback to legacy backup
                        if (_settings.VersioningEnabled)
                        {
                            var version = await _versioningService.CreateVersionAsync(
                                syncFile.RelativePath,
                                localPath,
                                "Conflict",
                                syncFile.SourcePeerId);
                            
                            if (version != null)
                            {
                                FileConflictDetected?.Invoke(localPath, null); // null = stored in versions folder
                                System.Diagnostics.Debug.WriteLine($"Conflict detected! Version {version.VersionId} created for {syncFile.RelativePath}");
                            }
                        }
                        else
                        {
                            // Legacy backup behavior
                            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                            var backupPath = $"{localPath}.conflict-{timestamp}.bak";
                            
                            File.Copy(localPath, backupPath, overwrite: true);
                            FileConflictDetected?.Invoke(localPath, backupPath);
                            System.Diagnostics.Debug.WriteLine($"Conflict detected! Backed up to {backupPath}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to process conflict backup: {ex.Message}");
                }
            }

            await FileHelpers.ExecuteWithRetryAsync(async () =>
            {
                await using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, ProtocolConstants.FILE_STREAM_BUFFER_SIZE, useAsync: true);
                
                // Read exactly FileSize bytes
                var buffer = new byte[ProtocolConstants.DEFAULT_BUFFER_SIZE]; // Use 1MB buffer from constants
                long totalRead = 0;
                while (totalRead < syncFile.FileSize)
                {
                    var toRead = (int)Math.Min(buffer.Length, syncFile.FileSize - totalRead);
                    var bytesRead = await dataStream.ReadAsync(buffer.AsMemory(0, toRead));
                    if (bytesRead == 0) throw new EndOfStreamException("Unexpected end of stream while receiving synced file");
                    
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                    totalRead += bytesRead;

                    ReportProgress(syncFile.RelativePath, (double)totalRead / syncFile.FileSize * 100);
                }
            });

            lock(_progressLock)
            {
                _completedFilesCount++;
                ReportProgress(null, 0);
            }
        }

        // Preserve modification time
        if (!syncFile.IsDirectory)
        {
            try
            {
                File.SetLastWriteTimeUtc(localPath, syncFile.LastModified);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to set timestamp for {localPath}: {ex.Message}");
            }
        }
    }


    private void DeleteLocalFile(SyncedFile syncFile, string localPath)
    {
        try
        {
            if (syncFile.IsDirectory)
            {
                if (Directory.Exists(localPath))
                {
                    Directory.Delete(localPath, recursive: true);
                }
            }
            else
            {
                if (File.Exists(localPath))
                {
                    File.Delete(localPath);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to delete {localPath}: {ex.Message}");
        }
    }

    private static async Task<string> ComputeFileHash(string filePath)
    {
        // Use retry logic as file might be briefly locked
        return await FileHelpers.ExecuteWithRetryAsync(async () =>
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, ProtocolConstants.FILE_STREAM_BUFFER_SIZE, useAsync: true);
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(stream);
            return Convert.ToHexString(hash);
        });
    }


    private async void OnSyncFileReceived(SyncedFile syncFile, Stream stream, Peer peer)
    {
        await HandleIncomingSyncFile(syncFile, stream);
    }

    private async void OnSyncDeleteReceived(SyncedFile syncFile, Peer peer)
    {
        await HandleIncomingSyncFile(syncFile, Stream.Null);
    }

    private void OnSyncRenameReceived(SyncedFile syncFile, Peer peer)
    {
        // Handle incoming rename - move the local file
        var oldLocalPath = Path.Combine(_settings.SyncFolderPath, syncFile.OldRelativePath);
        var newLocalPath = Path.Combine(_settings.SyncFolderPath, syncFile.RelativePath);
        var normalizedNewPath = FileHelpers.NormalizePath(newLocalPath);

        // Add to ignore list to prevent echo
        _ignoreList[normalizedNewPath] = DateTime.Now;

        try
        {
            // Ensure target directory exists
            var directory = Path.GetDirectoryName(newLocalPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (syncFile.IsDirectory)
            {
                if (Directory.Exists(oldLocalPath))
                {
                    Directory.Move(oldLocalPath, newLocalPath);
                    System.Diagnostics.Debug.WriteLine($"Directory renamed: {syncFile.OldRelativePath} -> {syncFile.RelativePath}");
                }
            }
            else
            {
                if (File.Exists(oldLocalPath))
                {
                    File.Move(oldLocalPath, newLocalPath, overwrite: true);
                    System.Diagnostics.Debug.WriteLine($"File renamed: {syncFile.OldRelativePath} -> {syncFile.RelativePath}");
                }
            }

            // Update our state
            if (_fileStates.TryRemove(syncFile.OldRelativePath, out var existingState))
            {
                existingState.RelativePath = syncFile.RelativePath;
                _fileStates[syncFile.RelativePath] = existingState;
            }

            IncomingSyncFile?.Invoke(syncFile);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to process rename {syncFile.OldRelativePath} -> {syncFile.RelativePath}: {ex.Message}");
        }
    }

    private async void OnSyncManifestReceived(List<SyncedFile> manifest, Peer peer)
    {
        await ProcessIncomingManifest(manifest, peer);
    }

    private async void OnSyncFileRequested(string relativePath, Peer peer)
    {
        // Peer is requesting a file from us
        var manifest = GetManifest();
        var file = manifest.FirstOrDefault(f => f.RelativePath == relativePath);
        if (file != null)
        {
            var fullPath = Path.Combine(SyncFolderPath, relativePath);
            if (File.Exists(fullPath))
            {
                await _transferService.SendSyncFile(peer, fullPath, file);
            }
        }
    }

    #region Delta Sync Event Handlers

    private async void OnSignaturesRequested(string relativePath, Peer peer)
    {
        // Peer wants our block signatures for a file so they can compute a delta
        var fullPath = Path.Combine(SyncFolderPath, relativePath);
        if (File.Exists(fullPath))
        {
            try
            {
                var signatures = await DeltaSyncService.ComputeBlockSignaturesAsync(fullPath);
                await _transferService.SendBlockSignatures(peer, relativePath, signatures);
                System.Diagnostics.Debug.WriteLine($"Sent {signatures.Count} block signatures for {relativePath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to compute/send signatures for {relativePath}: {ex.Message}");
            }
        }
        else
        {
            // File doesn't exist locally - send empty signatures (peer will send full file)
            await _transferService.SendBlockSignatures(peer, relativePath, []);
        }
    }

    private async void OnBlockSignaturesReceived(string relativePath, List<BlockSignature> signatures, Peer peer)
    {
        // We received signatures from peer, now compute delta and send it
        if (!_pendingDeltaSyncs.TryRemove(relativePath, out var pendingInfo))
        {
            System.Diagnostics.Debug.WriteLine($"Received unexpected signatures for {relativePath}");
            return;
        }

        var fullPath = Path.Combine(SyncFolderPath, relativePath);
        if (!File.Exists(fullPath))
        {
            System.Diagnostics.Debug.WriteLine($"File no longer exists for delta sync: {relativePath}");
            return;
        }

        try
        {
            if (signatures.Count == 0)
            {
                // Peer doesn't have the file - send full content
                System.Diagnostics.Debug.WriteLine($"Peer has no signatures for {relativePath}, sending full file");
                await _transferService.SendSyncFile(peer, fullPath, pendingInfo.syncFile, (sent, total) =>
                {
                    ReportProgress(relativePath, (double)sent / total * 100);
                });
            }
            else
            {
                // Compute delta against peer's signatures
                var instructions = await DeltaSyncService.ComputeDeltaAsync(fullPath, signatures);
                
                // Update syncFile with current info
                var fileInfo = new FileInfo(fullPath);
                pendingInfo.syncFile.FileSize = fileInfo.Length;

                await _transferService.SendDeltaData(peer, pendingInfo.syncFile, instructions);

                var deltaSize = DeltaSyncService.EstimateDeltaSize(instructions);
                var savings = fileInfo.Length > 0 ? (1.0 - (double)deltaSize / fileInfo.Length) * 100 : 0;
                System.Diagnostics.Debug.WriteLine($"Delta sync for {relativePath}: saved {savings:F1}% bandwidth");
            }

            lock (_progressLock)
            {
                _completedFilesCount++;
                ReportProgress(null, 0);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to send delta for {relativePath}: {ex.Message}");
        }
    }

    private async void OnDeltaDataReceived(SyncedFile syncFile, List<DeltaInstruction> instructions, Peer peer)
    {
        // Apply delta to reconstruct the file
        var localPath = Path.Combine(SyncFolderPath, syncFile.RelativePath);
        var normalizedPath = FileHelpers.NormalizePath(localPath);

        // Add to ignore list to prevent echo
        _ignoreList[normalizedPath] = DateTime.Now;

        try
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(localPath))
            {
                // Apply delta to existing file
                var tempPath = localPath + ".delta-temp";

                await DeltaSyncService.ApplyDeltaAsync(localPath, tempPath, instructions);

                // Replace original with reconstructed file
                File.Delete(localPath);
                File.Move(tempPath, localPath);

                System.Diagnostics.Debug.WriteLine($"Applied delta for {syncFile.RelativePath}");
            }
            else
            {
                // No base file - this shouldn't happen with delta sync
                // The sender should have sent a full file instead
                System.Diagnostics.Debug.WriteLine($"Warning: Received delta for non-existent file {syncFile.RelativePath}");
            }

            // Preserve modification time
            try
            {
                File.SetLastWriteTimeUtc(localPath, syncFile.LastModified);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to set timestamp for {localPath}: {ex.Message}");
            }

            // Update our state
            _fileStates[syncFile.RelativePath] = syncFile;
            IncomingSyncFile?.Invoke(syncFile);

            lock (_progressLock)
            {
                _completedFilesCount++;
                ReportProgress(null, 0);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to apply delta for {syncFile.RelativePath}: {ex.Message}");
        }
    }

    #endregion

    private void ReportProgress(string? currentFile, double percent)
    {
        if (currentFile == null) currentFile = "Syncing...";
        
        SyncProgressChanged?.Invoke(new SyncProgress
        {
            TotalFiles = _totalFilesCount,
            CompletedFiles = _completedFilesCount,
            CurrentFileName = currentFile,
            CurrentFilePercent = percent
        });
    }

    #endregion

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();

        if (_transferService != null)
        {
            _transferService.SyncFileReceived -= OnSyncFileReceived;
            _transferService.SyncDeleteReceived -= OnSyncDeleteReceived;
            _transferService.SyncManifestReceived -= OnSyncManifestReceived;
            _transferService.SyncFileRequested -= OnSyncFileRequested;
            _transferService.SyncRenameReceived -= OnSyncRenameReceived;

            // Unsubscribe from delta sync events
            _transferService.SignaturesRequested -= OnSignaturesRequested;
            _transferService.BlockSignaturesReceived -= OnBlockSignaturesReceived;
            _transferService.DeltaDataReceived -= OnDeltaDataReceived;
        }
    }

}

