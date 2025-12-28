using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;

namespace Swarm.Core.Services;

/// <summary>
/// Rescan mode for determining how thoroughly to check files.
/// </summary>
public enum RescanMode
{
    /// <summary>
    /// Quick scan using only file timestamps and sizes. Fast but may miss content-only changes.
    /// </summary>
    QuickTimestampOnly,
    
    /// <summary>
    /// Deep scan that recomputes file hashes. Slower but catches all changes including bit rot.
    /// </summary>
    DeepWithHash
}

/// <summary>
/// Progress information for rescan operations.
/// </summary>
public struct RescanProgress
{
    public int TotalFiles { get; set; }
    public int ScannedFiles { get; set; }
    public int ChangesDetected { get; set; }
    public string CurrentFile { get; set; }
    public bool IsRunning { get; set; }
    public double PercentComplete => TotalFiles > 0 ? (double)ScannedFiles / TotalFiles * 100 : 0;
}

/// <summary>
/// Represents a change detected during a rescan operation.
/// </summary>
public class RescanChange
{
    public string RelativePath { get; set; } = string.Empty;
    public RescanChangeType ChangeType { get; set; }
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
    public string? ExpectedHash { get; set; }
    public string? ActualHash { get; set; }
    public long? ExpectedSize { get; set; }
    public long? ActualSize { get; set; }
}

public enum RescanChangeType
{
    NewFile,
    ModifiedFile,
    DeletedFile,
    HashMismatch
}

/// <summary>
/// Service for periodic rescanning of the sync folder to catch changes
/// that FileSystemWatcher may have missed.
/// </summary>
public class RescanService : IDisposable
{
    private readonly Settings _settings;
    private readonly SyncService _syncService;
    private readonly Helpers.SwarmIgnoreService _swarmIgnoreService;
    
    private System.Threading.Timer? _rescanTimer;
    private CancellationTokenSource? _rescanCts;
    private bool _isRunning;
    private readonly object _lock = new();
    
    /// <summary>
    /// Last time a rescan completed successfully.
    /// </summary>
    public DateTime? LastRescanTime { get; private set; }
    
    /// <summary>
    /// Last rescan duration in seconds.
    /// </summary>
    public double LastRescanDurationSeconds { get; private set; }
    
    /// <summary>
    /// Number of changes found in the last rescan.
    /// </summary>
    public int LastRescanChangesFound { get; private set; }
    
    /// <summary>
    /// Whether a rescan is currently in progress.
    /// </summary>
    public bool IsRunning => _isRunning;
    
    /// <summary>
    /// Raised during rescan to report progress.
    /// </summary>
    public event Action<RescanProgress>? ProgressChanged;
    
    /// <summary>
    /// Raised when rescan completes.
    /// </summary>
    public event Action<int>? RescanCompleted;
    
    /// <summary>
    /// Raised when a change is detected that needs syncing.
    /// </summary>
    public event Action<RescanChange>? ChangeDetected;

    public RescanService(Settings settings, SyncService syncService)
    {
        _settings = settings;
        _syncService = syncService;
        _swarmIgnoreService = new Helpers.SwarmIgnoreService(settings);
    }

    /// <summary>
    /// Starts the periodic rescan timer based on settings.
    /// </summary>
    public void Start()
    {
        if (_settings.RescanIntervalMinutes <= 0)
        {
            System.Diagnostics.Debug.WriteLine("[RescanService] Periodic rescan disabled (interval = 0)");
            return;
        }

        var intervalMs = _settings.RescanIntervalMinutes * 60 * 1000;
        
        _rescanTimer = new System.Threading.Timer(
            _ => _ = RescanAsync(),
            null,
            intervalMs, // Initial delay (wait before first rescan)
            intervalMs  // Period
        );
        
        System.Diagnostics.Debug.WriteLine($"[RescanService] Started with interval: {_settings.RescanIntervalMinutes} minutes");
    }

    /// <summary>
    /// Stops the periodic rescan timer.
    /// </summary>
    public void Stop()
    {
        _rescanTimer?.Dispose();
        _rescanTimer = null;
        CancelCurrentRescan();
        System.Diagnostics.Debug.WriteLine("[RescanService] Stopped");
    }

    /// <summary>
    /// Updates the rescan interval. Restarts the timer if running.
    /// </summary>
    public void UpdateInterval(int intervalMinutes)
    {
        Stop();
        if (intervalMinutes > 0)
        {
            Start();
        }
    }

    /// <summary>
    /// Cancels any currently running rescan.
    /// </summary>
    public void CancelCurrentRescan()
    {
        _rescanCts?.Cancel();
        _rescanCts?.Dispose();
        _rescanCts = null;
    }

    /// <summary>
    /// Performs a rescan of the sync folder.
    /// </summary>
    /// <param name="mode">Rescan mode (quick or deep). If null, uses setting.</param>
    /// <returns>Number of changes detected.</returns>
    public async Task<int> RescanAsync(RescanMode? mode = null)
    {
        lock (_lock)
        {
            if (_isRunning)
            {
                System.Diagnostics.Debug.WriteLine("[RescanService] Rescan already in progress, skipping");
                return 0;
            }
            _isRunning = true;
        }

        var actualMode = mode ?? _settings.RescanMode;
        var startTime = DateTime.UtcNow;
        var changesDetected = 0;
        
        _rescanCts = new CancellationTokenSource();
        var ct = _rescanCts.Token;

        try
        {
            System.Diagnostics.Debug.WriteLine($"[RescanService] Starting {actualMode} rescan of {_settings.SyncFolderPath}");
            
            var syncFolder = _settings.SyncFolderPath;
            if (!Directory.Exists(syncFolder))
            {
                System.Diagnostics.Debug.WriteLine("[RescanService] Sync folder does not exist");
                return 0;
            }

            // Get current known file states from SyncService
            var knownFiles = _syncService.GetFileStates().ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value,
                StringComparer.OrdinalIgnoreCase
            );

            // Get all files on disk
            var filesOnDisk = Directory.EnumerateFiles(syncFolder, "*", SearchOption.AllDirectories)
                .Where(f => !ShouldIgnore(f))
                .Select(f => Path.GetRelativePath(syncFolder, f))
                .ToList();

            var totalFiles = filesOnDisk.Count + knownFiles.Count;
            var scannedFiles = 0;

            // Report initial progress
            ProgressChanged?.Invoke(new RescanProgress
            {
                TotalFiles = totalFiles,
                ScannedFiles = 0,
                ChangesDetected = 0,
                CurrentFile = "Starting...",
                IsRunning = true
            });

            // Check files on disk against known state
            foreach (var relativePath in filesOnDisk)
            {
                ct.ThrowIfCancellationRequested();
                
                var fullPath = Path.Combine(syncFolder, relativePath);
                
                try
                {
                    var fileInfo = new FileInfo(fullPath);
                    
                    if (knownFiles.TryGetValue(relativePath, out var knownFile))
                    {
                        // File exists in both - check for changes
                        bool hasChanged = false;
                        RescanChange? change = null;

                        if (actualMode == RescanMode.QuickTimestampOnly)
                        {
                            // Quick mode: compare timestamp and size
                            if (fileInfo.LastWriteTimeUtc != knownFile.LastModified ||
                                fileInfo.Length != knownFile.FileSize)
                            {
                                hasChanged = true;
                                change = new RescanChange
                                {
                                    RelativePath = relativePath,
                                    ChangeType = RescanChangeType.ModifiedFile,
                                    ExpectedSize = knownFile.FileSize,
                                    ActualSize = fileInfo.Length
                                };
                            }
                        }
                        else
                        {
                            // Deep mode: compare hash
                            var actualHash = await ComputeFileHashAsync(fullPath, ct);
                            if (!string.Equals(actualHash, knownFile.ContentHash, StringComparison.OrdinalIgnoreCase))
                            {
                                hasChanged = true;
                                change = new RescanChange
                                {
                                    RelativePath = relativePath,
                                    ChangeType = RescanChangeType.HashMismatch,
                                    ExpectedHash = knownFile.ContentHash,
                                    ActualHash = actualHash
                                };
                            }
                        }

                        if (hasChanged && change != null)
                        {
                            changesDetected++;
                            ChangeDetected?.Invoke(change);
                            System.Diagnostics.Debug.WriteLine($"[RescanService] Change detected: {change.ChangeType} - {relativePath}");
                        }

                        // Mark as seen
                        knownFiles.Remove(relativePath);
                    }
                    else
                    {
                        // New file not in known state
                        changesDetected++;
                        var change = new RescanChange
                        {
                            RelativePath = relativePath,
                            ChangeType = RescanChangeType.NewFile,
                            ActualSize = fileInfo.Length
                        };
                        ChangeDetected?.Invoke(change);
                        System.Diagnostics.Debug.WriteLine($"[RescanService] New file detected: {relativePath}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[RescanService] Error checking file {relativePath}: {ex.Message}");
                }

                scannedFiles++;
                
                // Report progress periodically (every 50 files)
                if (scannedFiles % 50 == 0)
                {
                    ProgressChanged?.Invoke(new RescanProgress
                    {
                        TotalFiles = totalFiles,
                        ScannedFiles = scannedFiles,
                        ChangesDetected = changesDetected,
                        CurrentFile = relativePath,
                        IsRunning = true
                    });
                }
            }

            // Remaining files in knownFiles are deleted
            foreach (var deletedFile in knownFiles.Keys)
            {
                ct.ThrowIfCancellationRequested();
                
                changesDetected++;
                var change = new RescanChange
                {
                    RelativePath = deletedFile,
                    ChangeType = RescanChangeType.DeletedFile
                };
                ChangeDetected?.Invoke(change);
                System.Diagnostics.Debug.WriteLine($"[RescanService] Deleted file detected: {deletedFile}");
                scannedFiles++;
            }

            // Final progress
            ProgressChanged?.Invoke(new RescanProgress
            {
                TotalFiles = totalFiles,
                ScannedFiles = scannedFiles,
                ChangesDetected = changesDetected,
                CurrentFile = "Complete",
                IsRunning = false
            });

            LastRescanTime = DateTime.UtcNow;
            LastRescanDurationSeconds = (DateTime.UtcNow - startTime).TotalSeconds;
            LastRescanChangesFound = changesDetected;
            
            System.Diagnostics.Debug.WriteLine(
                $"[RescanService] Rescan complete. Duration: {LastRescanDurationSeconds:F1}s, Changes: {changesDetected}");
            
            RescanCompleted?.Invoke(changesDetected);

            // If changes detected, trigger a force sync to reconcile
            if (changesDetected > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[RescanService] Triggering sync to reconcile {changesDetected} changes");
                await _syncService.ForceSyncAsync();
            }

            return changesDetected;
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("[RescanService] Rescan cancelled");
            ProgressChanged?.Invoke(new RescanProgress
            {
                IsRunning = false,
                CurrentFile = "Cancelled"
            });
            return changesDetected;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RescanService] Rescan failed: {ex.Message}");
            ProgressChanged?.Invoke(new RescanProgress
            {
                IsRunning = false,
                CurrentFile = $"Error: {ex.Message}"
            });
            return changesDetected;
        }
        finally
        {
            _isRunning = false;
            _rescanCts?.Dispose();
            _rescanCts = null;
        }
    }

    private bool ShouldIgnore(string path)
    {
        var fileName = Path.GetFileName(path);
        
        // Ignore system/hidden files
        if (fileName.StartsWith('.') || fileName.StartsWith('~'))
            return true;

        // Check .swarmignore patterns
        try
        {
            var relativePath = Path.GetRelativePath(_settings.SyncFolderPath, path);
            return _swarmIgnoreService.IsIgnored(relativePath);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            ProtocolConstants.FILE_STREAM_BUFFER_SIZE,
            useAsync: true);

        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash);
    }

    public void Dispose()
    {
        Stop();
    }
}

