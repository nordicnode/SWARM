using System.Collections.Concurrent;
using Swarm.Core.Helpers;
using Swarm.Core.Models;
using Serilog;

namespace Swarm.Core.Services;

/// <summary>
/// Encapsulates FileSystemWatcher logic for monitoring a folder.
/// Provides debounced events for create, change, delete, and rename operations.
/// </summary>
public class FileWatcherService : IDisposable
{
    private readonly Settings _settings;
    private readonly SwarmIgnoreService _swarmIgnoreService;
    
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cts;
    
    // Debounce rapid changes
    private readonly ConcurrentDictionary<string, DateTime> _pendingChanges = new();
    private readonly ConcurrentDictionary<string, (string OldPath, DateTime Time)> _pendingRenames = new();
    private const int DEBOUNCE_MS = ProtocolConstants.SYNC_DEBOUNCE_MS;
    
    // Ignore list for files currently being written by sync
    private readonly ConcurrentDictionary<string, DateTime> _ignoreList = new();
    private const int IGNORE_DURATION_MS = ProtocolConstants.SYNC_IGNORE_DURATION_MS;
    
    // Directory rename coalescence tracking
    private readonly ConcurrentDictionary<string, (string NewParentPath, HashSet<string> AffectedFiles, DateTime FirstSeen)> 
        _directoryRenameCandidates = new();
    private readonly ConcurrentDictionary<string, DateTime> _recentDirectoryRenames = new();
    
    private const int DIRECTORY_RENAME_DEBOUNCE_MS = 500;
    private const int DIRECTORY_RENAME_THRESHOLD = 5;
    private const int DIRECTORY_RENAME_MEMORY_MS = 2000;
    private const int RENAME_BATCH_DELAY_MS = 1000;
    
    private DateTime _lastRenameEventTime = DateTime.MinValue;

    public bool IsRunning => _watcher != null;
    public string WatchPath => _settings.SyncFolderPath;

    // Events
    public event Action<string, SyncAction>? FileChangeDetected;
    public event Action<string, string>? FileRenameDetected; // oldPath, newPath
    public event Action<string, string>? DirectoryRenameDetected; // oldPath, newPath
    public event Action<Exception, bool>? WatcherError; // exception, isBufferOverflow

    public FileWatcherService(Settings settings)
    {
        _settings = settings;
        _swarmIgnoreService = new SwarmIgnoreService(settings);
    }

    /// <summary>
    /// Starts monitoring the folder for changes.
    /// </summary>
    public void Start()
    {
        if (_watcher != null) return;
        
        _cts = new CancellationTokenSource();
        
        if (!Directory.Exists(_settings.SyncFolderPath))
        {
            Directory.CreateDirectory(_settings.SyncFolderPath);
        }

        _watcher = new FileSystemWatcher(_settings.SyncFolderPath)
        {
            NotifyFilter = NotifyFilters.FileName
                         | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite
                         | NotifyFilters.Size
                         | NotifyFilters.CreationTime,
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            InternalBufferSize = 65536 // 64KB buffer
        };

        _watcher.Created += OnFileCreated;
        _watcher.Changed += OnFileChanged;
        _watcher.Deleted += OnFileDeleted;
        _watcher.Renamed += OnFileRenamed;
        _watcher.Error += OnWatcherError;

        // Start debounce processor
        _ = Task.Run(() => ProcessPendingChanges(_cts.Token));
        
        Log.Information($"FileWatcherService started, watching: {_settings.SyncFolderPath}");
    }

    /// <summary>
    /// Stops monitoring the folder.
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
        
        Log.Information("FileWatcherService stopped");
    }

    /// <summary>
    /// Adds a path to the ignore list temporarily.
    /// Used to prevent echo when writing files from sync.
    /// </summary>
    public void IgnoreTemporarily(string fullPath)
    {
        var normalizedPath = FileHelpers.NormalizePath(fullPath);
        _ignoreList[normalizedPath] = DateTime.Now;
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
        
        _lastRenameEventTime = DateTime.Now;
        QueueRename(e.OldFullPath, e.FullPath);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var exception = e.GetException();
        Log.Error(exception, $"FileSystemWatcher error: {exception.Message}");
        
        bool isBufferOverflow = false;
        if (exception is System.ComponentModel.Win32Exception win32Ex)
        {
            if (win32Ex.NativeErrorCode == 122)
            {
                Log.Fatal("[CRITICAL] FileSystemWatcher internal buffer overflow detected!");
                isBufferOverflow = true;
            }
        }
        
        if (exception.Message.Contains("buffer") || exception.Message.Contains("overflow"))
        {
            isBufferOverflow = true;
        }
        
        WatcherError?.Invoke(exception, isBufferOverflow);
        
        // Attempt recovery
        Stop();
        Start();
    }

    private bool ShouldIgnore(string path)
    {
        var normalizedPath = FileHelpers.NormalizePath(path);

        // Check ignore list
        if (_ignoreList.TryGetValue(normalizedPath, out var ignoreTime))
        {
            if ((DateTime.Now - ignoreTime).TotalMilliseconds < IGNORE_DURATION_MS)
            {
                return true;
            }
            _ignoreList.TryRemove(normalizedPath, out _);
        }

        // Ignore hidden files
        var fileName = Path.GetFileName(path);
        if (fileName.StartsWith('.') || fileName.StartsWith("~"))
            return true;

        // Check .swarmignore
        try
        {
            var relativePath = Path.GetRelativePath(_settings.SyncFolderPath, path);
            if (_swarmIgnoreService.IsIgnored(relativePath))
            {
                Log.Debug($"[SwarmIgnore] Ignoring: {relativePath}");
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, $"[SwarmIgnore] Error checking path: {ex.Message}");
        }

        return false;
    }

    private void QueueChange(string fullPath, SyncAction action)
    {
        _pendingChanges[fullPath] = DateTime.Now;
        Log.Debug($"Queued change: {action} - {fullPath}");
    }

    private void QueueRename(string oldFullPath, string newFullPath)
    {
        _pendingRenames[newFullPath] = (oldFullPath, DateTime.Now);
        
        var oldDir = Path.GetDirectoryName(oldFullPath) ?? "";
        var newDir = Path.GetDirectoryName(newFullPath) ?? "";
        
        if (oldDir != newDir && Path.GetFileName(oldFullPath) == Path.GetFileName(newFullPath))
        {
            TrackDirectoryRenameCandidate(oldDir, newDir, newFullPath);
        }
        
        Log.Debug($"Queued rename: {oldFullPath} -> {newFullPath}");
    }

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

    private void MarkDirectoryAsRenamed(string oldPath)
    {
        _recentDirectoryRenames[oldPath.ToLowerInvariant()] = DateTime.Now;
    }

    private bool IsSubpathOfRenamedDirectory(string path)
    {
        var normalizedPath = path.ToLowerInvariant();
        var now = DateTime.Now;
        
        foreach (var (dirPath, timestamp) in _recentDirectoryRenames.ToList())
        {
            if ((now - timestamp).TotalMilliseconds > DIRECTORY_RENAME_MEMORY_MS)
            {
                _recentDirectoryRenames.TryRemove(dirPath, out _);
                continue;
            }
            
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
                var timeSinceLastRename = (now - _lastRenameEventTime).TotalMilliseconds;
                var shouldProcessRenames = timeSinceLastRename >= RENAME_BATCH_DELAY_MS || _lastRenameEventTime == DateTime.MinValue;

                if (shouldProcessRenames && (_pendingRenames.Count > 0 || _directoryRenameCandidates.Count > 0))
                {
                    // Process directory renames first
                    foreach (var (oldParent, (newParent, affectedFiles, firstSeen)) in _directoryRenameCandidates.ToList())
                    {
                        var age = (now - firstSeen).TotalMilliseconds;
                        
                        if (age >= DIRECTORY_RENAME_DEBOUNCE_MS)
                        {
                            _directoryRenameCandidates.TryRemove(oldParent, out _);
                            
                            if (affectedFiles.Count >= DIRECTORY_RENAME_THRESHOLD)
                            {
                                Log.Information($"[DIRECTORY RENAME] Detected: {oldParent} -> {newParent} ({affectedFiles.Count} files)");
                                MarkDirectoryAsRenamed(oldParent);
                                DirectoryRenameDetected?.Invoke(oldParent, newParent);
                                
                                // Remove individual file renames that are part of this directory rename
                                foreach (var file in affectedFiles)
                                {
                                    _pendingRenames.TryRemove(file, out _);
                                }
                            }
                        }
                    }

                    // Process individual file renames
                    foreach (var (newPath, (oldPath, time)) in _pendingRenames.ToList())
                    {
                        if ((now - time).TotalMilliseconds >= DEBOUNCE_MS)
                        {
                            _pendingRenames.TryRemove(newPath, out _);
                            
                            // Skip if part of a recently detected directory rename
                            if (IsSubpathOfRenamedDirectory(oldPath))
                            {
                                Log.Debug($"Suppressing file rename (part of dir rename): {oldPath}");
                                continue;
                            }
                            
                            FileRenameDetected?.Invoke(oldPath, newPath);
                        }
                    }
                }

                // Process regular file changes
                foreach (var (path, time) in _pendingChanges.ToList())
                {
                    if ((now - time).TotalMilliseconds >= DEBOUNCE_MS)
                    {
                        _pendingChanges.TryRemove(path, out _);
                        
                        // Determine action from most recent state
                        var action = File.Exists(path) ? SyncAction.Update : SyncAction.Delete;
                        if (!_pendingChanges.ContainsKey(path))
                        {
                            FileChangeDetected?.Invoke(path, action);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Error processing pending changes: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
