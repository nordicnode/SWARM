using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Swarm.Core.Abstractions;
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
    private readonly IDiscoveryService _discoveryService;
    private readonly ITransferService _transferService;
    private readonly VersioningService _versioningService;
    private readonly SwarmIgnoreService _swarmIgnoreService;
    private readonly IHashingService _hashingService;
    private readonly IFileStateRepository _fileStateRepository;
    private readonly ActivityLogService? _activityLogService;
    private readonly ConflictResolutionService? _conflictResolutionService;
    private readonly FolderEncryptionService _folderEncryptionService;
    private readonly FileWatcherService _fileWatcherService;
    private readonly ILogger<SyncService> _logger;
    private readonly IToastService? _toastService;
    
    private CancellationTokenSource? _cts;
    
    // Activity log debounce to prevent duplicate entries
    private readonly ConcurrentDictionary<string, DateTime> _activityLogDebounce = new();
    private const int ACTIVITY_DEBOUNCE_MS = ProtocolConstants.ACTIVITY_DEBOUNCE_MS;

    public string SyncFolderPath => _settings.SyncFolderPath;
    public bool IsEnabled => _settings.IsSyncEnabled;
    public bool IsRunning => _fileWatcherService.IsRunning;
    
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Failed to enumerate sync folder files: {ex.Message}");
            return _fileStateRepository.Count; // Fallback to tracked state
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
    public event Action<string, DateTime>? TimeTravelDetected; // fileName, futureTime
    public event Action<SyncProgress>? SyncProgressChanged;
    
    /// <summary>
    /// Notifies subscribers that sync status has changed (e.g., paused/resumed).
    /// </summary>
    public void NotifyStatusChanged(string? status = null)
    {
        var statusMessage = status ?? (_settings.IsSyncCurrentlyPaused ? "Sync paused" : "Sync enabled - Watching for changes");
        SyncStatusChanged?.Invoke(statusMessage);
    }
    
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

    public SyncService(Settings settings, IDiscoveryService discoveryService, ITransferService transferService, VersioningService versioningService, IHashingService hashingService, IFileStateRepository fileStateRepository, FolderEncryptionService folderEncryptionService, ILogger<SyncService> logger, IToastService? toastService = null, ActivityLogService? activityLogService = null, ConflictResolutionService? conflictResolutionService = null)
    {
        _settings = settings;
        _discoveryService = discoveryService;
        _transferService = transferService;
        _versioningService = versioningService;
        _hashingService = hashingService;
        _fileStateRepository = fileStateRepository;
        _folderEncryptionService = folderEncryptionService;
        _logger = logger;
        _toastService = toastService;
        _activityLogService = activityLogService;
        _conflictResolutionService = conflictResolutionService;
        _swarmIgnoreService = new SwarmIgnoreService(settings);

        
        // Create FileWatcherService and subscribe to its events
        _fileWatcherService = new FileWatcherService(settings);
        _fileWatcherService.FileChangeDetected += OnFileWatcherChange;
        _fileWatcherService.FileRenameDetected += OnFileWatcherRename;
        _fileWatcherService.DirectoryRenameDetected += OnFileWatcherDirectoryRename;
        _fileWatcherService.WatcherError += OnFileWatcherError;

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
            
        _logger.LogInformation($"[SYNC] Trusted peer {peer.Name} connected, sending manifest...");
        
        try
        {
            // Short delay to ensure peer is fully initialized
            await Task.Delay(500);
            await SendManifestToPeer(peer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[SYNC] Failed to sync with {peer.Name}: {ex.Message}");
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

                // Start the FileWatcherService
                _fileWatcherService.Start();

                SyncStatusChanged?.Invoke("Sync enabled - Watching for changes");
                _logger.LogInformation($"SyncService started, watching: {_settings.SyncFolderPath}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to start SyncService: {ex.Message}");
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
        _fileWatcherService.Stop();
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
        _fileStateRepository.Clear();
        
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
        // Don't sync if paused
        if (_settings.IsSyncCurrentlyPaused)
        {
            SyncStatusChanged?.Invoke("Sync paused");
            return;
        }

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
        
        // Add to ignore list to prevent echo
        IgnorePathTemporarily(localPath);

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
                _fileStateRepository.AddOrUpdate(syncFile);
            }
            else
            {
                _fileStateRepository.Remove(syncFile.RelativePath);
            }

            IncomingSyncFile?.Invoke(syncFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to handle sync file {syncFile.RelativePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the current manifest of all synced files.
    /// </summary>
    public IEnumerable<SyncedFile> GetManifest()
    {
        return _fileStateRepository.GetAll();
    }

    /// <summary>
    /// Gets the current file states dictionary for integrity verification.
    /// </summary>
    public IReadOnlyDictionary<string, SyncedFile> GetFileStates()
    {
        return _fileStateRepository.AsReadOnlyDictionary();
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
            var localFile = _fileStateRepository.Get(remoteFile.RelativePath);
            if (localFile != null)
            {
                // File exists locally - check for conflict
                if (remoteFile.ContentHash != localFile.ContentHash)
                {
                    // Check for future timestamp (Clock Drift Protection)
                    if (remoteFile.LastModified > DateTime.UtcNow.AddMinutes(10))
                    {
                        _logger.LogWarning($"[Warning] Peer {sourcePeer.Name} has file {remoteFile.RelativePath} from the future ({remoteFile.LastModified}). Ignoring to prevent corruption.");
                        TimeTravelDetected?.Invoke(remoteFile.RelativePath, remoteFile.LastModified);
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
                        _logger.LogWarning($"[Warning] Peer {sourcePeer.Name} offering new file {remoteFile.RelativePath} from the future. Ignoring.");
                        TimeTravelDetected?.Invoke(remoteFile.RelativePath, remoteFile.LastModified);
                        continue;
                    }
                    
                    await RequestFileFromPeer(sourcePeer, remoteFile.RelativePath);
                }
            }
        }

        // Check for files we have that remote doesn't
        var remoteSet = remoteManifest.Select(f => f.RelativePath).ToHashSet();
        foreach (var localFile in _fileStateRepository.GetAll())
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
        _fileStateRepository.Clear();
        
        if (!Directory.Exists(_settings.SyncFolderPath)) return;

        // Load from persistent storage first
        _fileStateRepository.Load();
        var existingStates = _fileStateRepository.AsReadOnlyDictionary();
        var cacheHits = 0;
        var cacheMisses = 0;

        foreach (var filePath in Directory.EnumerateFiles(_settings.SyncFolderPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                // Skip hidden files/directories (like .swarm-cache and .swarm-versions)
                // BUT whitelist .swarm-vault so encryption metadata syncs to peers
                bool isVaultMetadata = filePath.Contains(".swarm-vault");
                if (!isVaultMetadata && (filePath.Contains(Path.DirectorySeparatorChar + ".") || Path.GetFileName(filePath).StartsWith(".")))
                    continue;

                var relativePath = Path.GetRelativePath(_settings.SyncFolderPath, filePath);
                
                // Also check ignore service
                if (_swarmIgnoreService.IsIgnored(relativePath)) continue;

                var fileInfo = new FileInfo(filePath);
                string contentHash;

                // Check cache
                if (existingStates.TryGetValue(relativePath, out var cachedFile) &&
                    cachedFile.FileSize == fileInfo.Length &&
                    Math.Abs((cachedFile.LastModified - fileInfo.LastWriteTimeUtc).TotalSeconds) < 1.0) // 1s tolerance
                {
                    // Cache hit - trust the hash
                    contentHash = cachedFile.ContentHash;
                    cacheHits++;
                }
                else
                {
                    // Cache miss - compute hash
                    contentHash = await _hashingService.ComputeFileHashAsync(filePath);
                    cacheMisses++;
                }
                
                _fileStateRepository.AddOrUpdate(new SyncedFile
                {
                    RelativePath = relativePath,
                    ContentHash = contentHash,
                    LastModified = fileInfo.LastWriteTimeUtc,
                    FileSize = fileInfo.Length,
                    Action = SyncAction.Create,
                    SourcePeerId = _discoveryService.LocalId,
                    IsDirectory = false
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to read file {filePath}: {ex.Message}");
            }
        }

        _logger.LogInformation($"Built initial state. Cache hits: {cacheHits}, Misses/Updates: {cacheMisses}. Total tracked: {_fileStateRepository.Count}");
        
        // Save updated cache immediately
        _fileStateRepository.SaveChanges();
    }




    // ========== FileWatcherService Event Handlers ==========

    private async void OnFileWatcherChange(string fullPath, SyncAction action)
    {
        try
        {
            // Skip if sync is paused
            if (_settings.IsSyncCurrentlyPaused)
            {
                _logger.LogDebug("[SYNC] Skipping file change while sync is paused: {Path}", fullPath);
                return;
            }

            var relativePath = Path.GetRelativePath(_settings.SyncFolderPath, fullPath);
            LogActivityDebounced(relativePath, action.ToString().ToLower());
            
            await ProcessFileChange(fullPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[SYNC] Error processing file change for {fullPath}: {ex.Message}");
        }
    }

    private async void OnFileWatcherRename(string oldPath, string newPath)
    {
        try
        {
            // Skip if sync is paused
            if (_settings.IsSyncCurrentlyPaused)
            {
                _logger.LogDebug("[SYNC] Skipping rename while sync is paused: {OldPath} -> {NewPath}", oldPath, newPath);
                return;
            }

            var oldRelativePath = Path.GetRelativePath(_settings.SyncFolderPath, oldPath);
            var newRelativePath = Path.GetRelativePath(_settings.SyncFolderPath, newPath);
            _activityLogService?.LogFileSync(newRelativePath, $"renamed from {oldRelativePath}");
            
            await ProcessFileRename(oldPath, newPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[SYNC] Error processing rename {oldPath} -> {newPath}: {ex.Message}");
        }
    }

    private async void OnFileWatcherDirectoryRename(string oldPath, string newPath)
    {
        try
        {
            var oldRelativePath = Path.GetRelativePath(_settings.SyncFolderPath, oldPath);
            var newRelativePath = Path.GetRelativePath(_settings.SyncFolderPath, newPath);
            _logger.LogInformation($"[SYNC] Directory renamed: {oldRelativePath} -> {newRelativePath}");
            _activityLogService?.LogFileSync(newRelativePath, $"directory renamed from {oldRelativePath}");
            
            await ProcessFileRename(oldPath, newPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[SYNC] Error processing directory rename {oldPath} -> {newPath}: {ex.Message}");
        }
    }

    private void OnFileWatcherError(Exception exception, bool isBufferOverflow)
    {
        SyncStatusChanged?.Invoke("Sync error - recovering...");
        
        // Trigger full sync after error
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2000);
                
                if (isBufferOverflow)
                {
                    _logger.LogWarning("Triggering full sync to recover from buffer overflow");
                }
                else
                {
                    _logger.LogWarning("Triggering full sync to recover from watcher error");
                }
                
                await ForceSyncAsync();
                
                if (isBufferOverflow)
                {
                    RescanRequested?.Invoke();
                }
            }
            catch (Exception syncEx)
            {
                _logger.LogError(syncEx, $"Failed to sync after watcher error: {syncEx.Message}");
            }
        });
    }

    /// <summary>
    /// Logs file activity with debounce to prevent duplicate entries.
    /// </summary>
    private void LogActivityDebounced(string relativePath, string action)
    {
        var key = relativePath.ToLowerInvariant();
        var now = DateTime.Now;
        
        if (_activityLogDebounce.TryGetValue(key, out var lastLog))
        {
            if ((now - lastLog).TotalMilliseconds < ACTIVITY_DEBOUNCE_MS)
            {
                return;
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

    /// <summary>
    /// Tells the FileWatcherService to ignore a path temporarily (for incoming sync writes).
    /// </summary>
    private void IgnorePathTemporarily(string fullPath)
    {
        _fileWatcherService.IgnoreTemporarily(fullPath);
    }

    private async Task ProcessFileChange(string fullPath)
    {
        var relativePath = Path.GetRelativePath(_settings.SyncFolderPath, fullPath);

        // [SECURITY] Race Condition Check: 
        // If this file is in an encrypted folder but not encrypted (.senc), it's a plaintext leak.
        // We MUST ignore it until FolderEncryptionService encrypts it and deletes the original.
        if (_folderEncryptionService.GetEncryptedFolderFor(relativePath) != null && 
            !fullPath.EndsWith(".senc", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Security Block: Blocked plaintext file in encrypted folder: {Path}", relativePath);
            _toastService?.ShowToast("Security Alert", $"Blocked plaintext file '{Path.GetFileName(relativePath)}' in encrypted folder.", ToastType.Warning);
            return; 
        }
        SyncedFile syncFile;

        if (File.Exists(fullPath))
        {
            // File exists - create or update
            var fileInfo = new FileInfo(fullPath);
            var hash = await _hashingService.ComputeFileHashAsync(fullPath);
            
            var action = _fileStateRepository.Exists(relativePath) ? SyncAction.Update : SyncAction.Create;
            
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

            _fileStateRepository.AddOrUpdate(syncFile);
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
            var existing = _fileStateRepository.Get(relativePath);
            _fileStateRepository.Remove(relativePath);
            
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
            _logger.LogDebug("Rename target no longer exists: {Path}", newFullPath);
            return;
        }

        // Update our local file state
        if (isDirectory)
        {
            // For directory renames, update all child file states
            var oldPrefix = oldRelativePath + Path.DirectorySeparatorChar;
            var filesToUpdate = _fileStateRepository.GetAll()
                .Where(f => f.RelativePath.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase) ||
                            f.RelativePath.Equals(oldRelativePath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            
            foreach (var file in filesToUpdate)
            {
                _fileStateRepository.Remove(file.RelativePath);
                var newPath = newRelativePath + file.RelativePath.Substring(oldRelativePath.Length);
                file.RelativePath = newPath;
                _fileStateRepository.AddOrUpdate(file);
            }
            
            _logger.LogDebug(
                "[DirectoryRename] Updated {Count} child file states for {OldRelativePath} -> {NewRelativePath}",
                filesToUpdate.Count, oldRelativePath, newRelativePath);
        }
        else
        {
            // Single file rename
            var existingState = _fileStateRepository.Get(oldRelativePath);
            if (existingState != null)
            {
                _fileStateRepository.Remove(oldRelativePath);
                existingState.RelativePath = newRelativePath;
                _fileStateRepository.AddOrUpdate(existingState);
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
        // Don't broadcast if sync is paused
        if (_settings.IsSyncCurrentlyPaused)
        {
            _logger.LogDebug("[SYNC] Skipping broadcast while sync is paused: {Path}", syncFile.RelativePath);
            return;
        }

        foreach (var peer in _discoveryService.Peers.Where(p => p.IsSyncEnabled))
        {
            try
            {
                await SendFileToPeer(peer, syncFile);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to sync to {PeerName}: {Message}", peer.Name, ex.Message);
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
                // [OPTIMIZATION] Skip delta sync for encrypted files (.senc) as requested
                // Even with chunking, the overhead might be considered wasteful if entropy is high
                if (fileInfo.Length >= ProtocolConstants.DELTA_THRESHOLD && 
                    !syncFile.RelativePath.EndsWith(".senc", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Using delta sync for large file: {RelativePath} ({Length} bytes)", syncFile.RelativePath, fileInfo.Length);
                    
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
            // Conflict resolution: Check if content differs and resolve based on settings
            if (File.Exists(localPath))
            {
                try
                {
                    var currentHash = await _hashingService.ComputeFileHashAsync(localPath);
                    if (currentHash != syncFile.ContentHash)
                    {
                        // Build conflict info
                        var localInfo = new FileInfo(localPath);
                        var conflict = new FileConflict
                        {
                            RelativePath = syncFile.RelativePath,
                            LocalHash = currentHash,
                            RemoteHash = syncFile.ContentHash,
                            LocalModified = localInfo.LastWriteTimeUtc,
                            RemoteModified = syncFile.LastModified,
                            LocalSize = localInfo.Length,
                            RemoteSize = syncFile.FileSize,
                            SourcePeerName = syncFile.SourcePeerName ?? "Unknown",
                            SourcePeerId = syncFile.SourcePeerId ?? ""
                        };

                        // Resolve conflict using service (or fallback to auto-newest)
                        ConflictChoice? choice = null;
                        if (_conflictResolutionService != null)
                        {
                            choice = await _conflictResolutionService.ResolveConflictAsync(conflict);
                        }
                        else
                        {
                            // No service - use auto-newest fallback
                            choice = conflict.AutoWinner == ConflictWinner.Local 
                                ? ConflictChoice.KeepLocal 
                                : ConflictChoice.KeepRemote;
                        }

                        // Handle choice
                        switch (choice)
                        {
                            case ConflictChoice.KeepLocal:
                                _logger.LogInformation($"[Conflict] Keeping local version of {syncFile.RelativePath}");
                                return; // Don't overwrite
                            
                            case ConflictChoice.Skip:
                                _logger.LogInformation($"[Conflict] Skipped resolution for {syncFile.RelativePath}");
                                return; // Don't overwrite
                            
                            case ConflictChoice.KeepBoth:
                                // Save remote file with conflict name
                                var conflictPath = ConflictResolutionService.GenerateConflictFilename(
                                    localPath, syncFile.SourcePeerName ?? "Remote");
                                await WriteStreamToFile(syncFile, conflictPath, dataStream);
                                FileConflictDetected?.Invoke(localPath, conflictPath);
                                _logger.LogInformation($"[Conflict] Kept both: {syncFile.RelativePath} and conflict copy");
                                return; // We wrote the conflict copy, don't overwrite original
                            
                            case ConflictChoice.KeepRemote:
                            default:
                                // Archive local before overwriting
                                if (_conflictResolutionService != null)
                                {
                                    await _conflictResolutionService.ArchiveLocalBeforeOverwriteAsync(localPath, syncFile.RelativePath);
                                }
                                else if (_settings.VersioningEnabled)
                                {
                                    await _versioningService.CreateVersionAsync(
                                        syncFile.RelativePath, localPath, "Conflict", syncFile.SourcePeerId);
                                }
                                FileConflictDetected?.Invoke(localPath, null);
                                _logger.LogInformation($"[Conflict] Overwriting local with remote for {syncFile.RelativePath}");
                                break; // Continue to write remote file
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to process conflict for {syncFile.RelativePath}: {ex.Message}");
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
                _logger.LogDebug(ex, "Failed to set timestamp for {LocalPath}: {Message}", localPath, ex.Message);
            }
        }
    }

    /// <summary>
    /// Writes a stream to a file. Used for conflict copies.
    /// </summary>
    private async Task WriteStreamToFile(SyncedFile syncFile, string targetPath, Stream dataStream)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await FileHelpers.ExecuteWithRetryAsync(async () =>
        {
            await using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, ProtocolConstants.FILE_STREAM_BUFFER_SIZE, useAsync: true);
            
            var buffer = new byte[ProtocolConstants.DEFAULT_BUFFER_SIZE];
            long totalRead = 0;
            while (totalRead < syncFile.FileSize)
            {
                var toRead = (int)Math.Min(buffer.Length, syncFile.FileSize - totalRead);
                var bytesRead = await dataStream.ReadAsync(buffer.AsMemory(0, toRead));
                if (bytesRead == 0) break; // End of stream
                
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;
            }
        });

        try
        {
            File.SetLastWriteTimeUtc(targetPath, syncFile.LastModified);
        }
        catch { /* Ignore timestamp errors for conflict copies */ }
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
            _logger.LogDebug(ex, "Failed to delete {LocalPath}: {Message}", localPath, ex.Message);
        }
    }




    private async void OnSyncFileReceived(SyncedFile syncFile, Stream stream, Peer peer)
    {
        try
        {
            await HandleIncomingSyncFile(syncFile, stream);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[SYNC] Error receiving file {syncFile.RelativePath}: {ex.Message}");
        }
    }

    private async void OnSyncDeleteReceived(SyncedFile syncFile, Peer peer)
    {
        try
        {
            await HandleIncomingSyncFile(syncFile, Stream.Null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[SYNC] Error processing delete {syncFile.RelativePath}: {ex.Message}");
        }
    }

    private void OnSyncRenameReceived(SyncedFile syncFile, Peer peer)
    {
        // Handle incoming rename - move the local file
        var oldLocalPath = Path.Combine(_settings.SyncFolderPath, syncFile.OldRelativePath);
        var newLocalPath = Path.Combine(_settings.SyncFolderPath, syncFile.RelativePath);

        // Add to ignore list to prevent echo
        IgnorePathTemporarily(newLocalPath);

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
                    _logger.LogDebug("Directory renamed: {OldPath} -> {NewPath}", syncFile.OldRelativePath, syncFile.RelativePath);
                }
            }
            else
            {
                if (File.Exists(oldLocalPath))
                {
                    File.Move(oldLocalPath, newLocalPath, overwrite: true);
                    _logger.LogDebug("File renamed: {OldPath} -> {NewPath}", syncFile.OldRelativePath, syncFile.RelativePath);
                }
            }

            // Update our state
            var existingState = _fileStateRepository.Get(syncFile.OldRelativePath);
            if (existingState != null)
            {
                _fileStateRepository.Remove(syncFile.OldRelativePath);
                existingState.RelativePath = syncFile.RelativePath;
                _fileStateRepository.AddOrUpdate(existingState);
            }

            IncomingSyncFile?.Invoke(syncFile);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to process rename {OldPath} -> {NewPath}: {Message}", syncFile.OldRelativePath, syncFile.RelativePath, ex.Message);
        }
    }

    private async void OnSyncManifestReceived(List<SyncedFile> manifest, Peer peer)
    {
        try
        {
            await ProcessIncomingManifest(manifest, peer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[SYNC] Error processing manifest from {peer.Name}: {ex.Message}");
        }
    }

    private async void OnSyncFileRequested(string relativePath, Peer peer)
    {
        try
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
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[SYNC] Error sending requested file {relativePath} to {peer.Name}: {ex.Message}");
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
                _logger.LogDebug("Sent {Count} block signatures for {RelativePath}", signatures.Count, relativePath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to compute/send signatures for {RelativePath}: {Message}", relativePath, ex.Message);
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
            _logger.LogDebug("Received unexpected signatures for {RelativePath}", relativePath);
            return;
        }

        var fullPath = Path.Combine(SyncFolderPath, relativePath);
        if (!File.Exists(fullPath))
        {
            _logger.LogDebug("File no longer exists for delta sync: {RelativePath}", relativePath);
            return;
        }

        try
        {
            if (signatures.Count == 0)
            {
                // Peer doesn't have the file - send full content
                _logger.LogDebug("Peer has no signatures for {RelativePath}, sending full file", relativePath);
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
                _logger.LogDebug("Delta sync for {RelativePath}: saved {Savings:F1}% bandwidth", relativePath, savings);
            }

            lock (_progressLock)
            {
                _completedFilesCount++;
                ReportProgress(null, 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send delta for {RelativePath}: {Message}", relativePath, ex.Message);
        }
    }

    private async void OnDeltaDataReceived(SyncedFile syncFile, List<DeltaInstruction> instructions, Peer peer)
    {
        // Apply delta to reconstruct the file
        var localPath = Path.Combine(SyncFolderPath, syncFile.RelativePath);

        // Add to ignore list to prevent echo
        IgnorePathTemporarily(localPath);

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

                _logger.LogDebug("Applied delta for {RelativePath}", syncFile.RelativePath);
            }
            else
            {
                // No base file - this shouldn't happen with delta sync
                // The sender should have sent a full file instead
                _logger.LogDebug("Warning: Received delta for non-existent file {RelativePath}", syncFile.RelativePath);
            }

            // Preserve modification time
            try
            {
                File.SetLastWriteTimeUtc(localPath, syncFile.LastModified);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to set timestamp for {LocalPath}: {Message}", localPath, ex.Message);
            }

            // Update our state
            _fileStateRepository.AddOrUpdate(syncFile);
            IncomingSyncFile?.Invoke(syncFile);

            lock (_progressLock)
            {
                _completedFilesCount++;
                ReportProgress(null, 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to apply delta for {RelativePath}: {Message}", syncFile.RelativePath, ex.Message);
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

        // Save cache on exit
        _fileStateRepository?.SaveChanges();
    }

}

