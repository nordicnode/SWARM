using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Swarm.Models;

namespace Swarm.Core;

/// <summary>
/// Service for monitoring and synchronizing a local folder with peers.
/// </summary>
public class SyncService : IDisposable
{
    private readonly Settings _settings;
    private readonly DiscoveryService _discoveryService;
    private readonly TransferService _transferService;
    
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cts;
    
    // Track file states for change detection
    private readonly ConcurrentDictionary<string, SyncedFile> _fileStates = new();
    
    // Debounce rapid changes
    private readonly ConcurrentDictionary<string, DateTime> _pendingChanges = new();
    private const int DEBOUNCE_MS = 500;
    
    // Ignore list for files currently being written by sync
    private readonly ConcurrentDictionary<string, DateTime> _ignoreList = new();
    private const int IGNORE_DURATION_MS = 5000;

    public string SyncFolderPath => _settings.SyncFolderPath;
    public bool IsEnabled => _settings.IsSyncEnabled;
    public bool IsRunning => _watcher != null;

    public event Action<SyncedFile>? FileChanged;
    public event Action<string>? SyncStatusChanged;
    public event Action<SyncedFile>? IncomingSyncFile;

    public SyncService(Settings settings, DiscoveryService discoveryService, TransferService transferService)
    {
        _settings = settings;
        _discoveryService = discoveryService;
        _transferService = transferService;
    }

    /// <summary>
    /// Starts monitoring the sync folder for changes.
    /// </summary>
    public void Start()
    {
        if (!_settings.IsSyncEnabled) return;

        _cts = new CancellationTokenSource();
        _settings.EnsureSyncFolderExists();

        // Build initial file state
        BuildInitialFileStates();

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
        Task.Run(() => ProcessPendingChanges(_cts.Token));

        SyncStatusChanged?.Invoke("Sync enabled - Watching for changes");
        System.Diagnostics.Debug.WriteLine($"SyncService started, watching: {_settings.SyncFolderPath}");
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
        BuildInitialFileStates();
        
        // Send manifest to all peers with sync enabled
        foreach (var peer in _discoveryService.Peers.Where(p => p.IsSyncEnabled))
        {
            await SendManifestToPeer(peer);
        }

        SyncStatusChanged?.Invoke("Sync complete");
    }

    /// <summary>
    /// Handles an incoming sync file from a peer.
    /// </summary>
    public async Task HandleIncomingSyncFile(SyncedFile syncFile, Stream dataStream)
    {
        var localPath = Path.Combine(_settings.SyncFolderPath, syncFile.RelativePath);
        
        // Add to ignore list to prevent echo
        _ignoreList[localPath] = DateTime.Now;

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
    /// Compares incoming manifest with local state and resolves differences.
    /// </summary>
    public async Task ProcessIncomingManifest(IEnumerable<SyncedFile> remoteManifest, Peer sourcePeer)
    {
        foreach (var remoteFile in remoteManifest)
        {
            if (_fileStates.TryGetValue(remoteFile.RelativePath, out var localFile))
            {
                // File exists locally - check for conflict
                if (remoteFile.ContentHash != localFile.ContentHash)
                {
                    // Conflict! Use Last Write Wins
                    if (remoteFile.LastModified > localFile.LastModified)
                    {
                        // Remote is newer - request the file
                        await RequestFileFromPeer(sourcePeer, remoteFile.RelativePath);
                    }
                    // Else: local is newer, we'll push our version during next sync
                }
            }
            else
            {
                // File doesn't exist locally - request it
                if (remoteFile.Action != SyncAction.Delete)
                {
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

    private void BuildInitialFileStates()
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
                    ContentHash = ComputeFileHash(filePath),
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
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        QueueChange(e.FullPath, SyncAction.Update);
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        QueueChange(e.FullPath, SyncAction.Delete);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        
        // Handle as delete old + create new
        QueueChange(e.OldFullPath, SyncAction.Delete);
        QueueChange(e.FullPath, SyncAction.Create);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"FileSystemWatcher error: {e.GetException().Message}");
        
        // Attempt to restart
        Stop();
        Start();
    }

    private bool ShouldIgnore(string path)
    {
        // Check if this file is being written by sync
        if (_ignoreList.TryGetValue(path, out var ignoreTime))
        {
            if ((DateTime.Now - ignoreTime).TotalMilliseconds < IGNORE_DURATION_MS)
            {
                return true;
            }
            _ignoreList.TryRemove(path, out _);
        }

        // Ignore system/hidden files
        var fileName = Path.GetFileName(path);
        if (fileName.StartsWith('.') || fileName.StartsWith("~"))
            return true;

        return false;
    }

    private void QueueChange(string fullPath, SyncAction action)
    {
        _pendingChanges[fullPath] = DateTime.Now;
        System.Diagnostics.Debug.WriteLine($"Queued change: {action} - {fullPath}");
    }

    private async Task ProcessPendingChanges(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(100, ct);
                
                var now = DateTime.Now;
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
            var hash = ComputeFileHash(fullPath);
            
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
        else if (!syncFile.IsDirectory)
        {
            var fullPath = Path.Combine(_settings.SyncFolderPath, syncFile.RelativePath);
            if (File.Exists(fullPath))
            {
                await _transferService.SendSyncFile(peer, fullPath, syncFile);
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
            await using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await dataStream.CopyToAsync(fileStream);
        }

        // Preserve modification time
        if (!syncFile.IsDirectory)
        {
            File.SetLastWriteTimeUtc(localPath, syncFile.LastModified);
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

    private static string ComputeFileHash(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return string.Empty;
        }
    }

    #endregion

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
