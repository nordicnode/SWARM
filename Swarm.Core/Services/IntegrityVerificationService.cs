using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Swarm.Core.Abstractions;
using Swarm.Core.Helpers;

namespace Swarm.Core.Services;

/// <summary>
/// Background service that periodically verifies file integrity by comparing
/// actual filesystem state against stored hashes. Catches any changes missed
/// by FileSystemWatcher (which can be unreliable on some OS configurations).
/// </summary>
public class IntegrityVerificationService : IDisposable
{
    private readonly Settings _settings;
    private readonly SyncService _syncService;
    private readonly IHashingService _hashingService;
    private readonly ILogger<IntegrityVerificationService> _logger;
    
    private CancellationTokenSource? _cts;
    private Task? _verificationTask;
    
    /// <summary>
    /// Interval between full tree verifications (default: 4 hours).
    /// </summary>
    public TimeSpan VerificationInterval { get; set; } = TimeSpan.FromHours(4);
    
    /// <summary>
    /// Minimum interval between verifications (prevents excessive CPU usage).
    /// </summary>
    public TimeSpan MinVerificationInterval { get; } = TimeSpan.FromMinutes(15);
    
    /// <summary>
    /// Number of files verified in the last run.
    /// </summary>
    public int LastVerificationFileCount { get; private set; }
    
    /// <summary>
    /// Number of discrepancies found in the last run.
    /// </summary>
    public int LastVerificationDiscrepancies { get; private set; }
    
    /// <summary>
    /// Timestamp of the last completed verification.
    /// </summary>
    public DateTime? LastVerificationTime { get; private set; }
    
    /// <summary>
    /// Whether a verification is currently in progress.
    /// </summary>
    public bool IsVerifying { get; private set; }
    
    /// <summary>
    /// Event fired when verification completes.
    /// </summary>
    public event Action<int, int>? VerificationCompleted; // fileCount, discrepancyCount
    
    public IntegrityVerificationService(
        Settings settings,
        SyncService syncService,
        IHashingService hashingService,
        ILogger<IntegrityVerificationService>? logger = null)
    {
        _settings = settings;
        _syncService = syncService;
        _hashingService = hashingService;
        _logger = logger ?? NullLogger<IntegrityVerificationService>.Instance;
    }
    
    /// <summary>
    /// Starts the background verification service.
    /// </summary>
    public void Start()
    {
        if (_cts != null) return;
        
        _cts = new CancellationTokenSource();
        _verificationTask = Task.Run(() => RunVerificationLoop(_cts.Token));
        
        _logger.LogInformation($"IntegrityVerificationService started. Interval: {VerificationInterval}");
    }
    
    /// <summary>
    /// Stops the background verification service.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        _verificationTask?.Wait(TimeSpan.FromSeconds(5));
        _cts?.Dispose();
        _cts = null;
        
        _logger.LogInformation("IntegrityVerificationService stopped");
    }
    
    /// <summary>
    /// Triggers an immediate verification (for manual refresh).
    /// </summary>
    public async Task VerifyNowAsync()
    {
        if (IsVerifying)
        {
            _logger.LogWarning("Verification already in progress, skipping");
            return;
        }
        
        await PerformVerificationAsync(CancellationToken.None);
    }
    
    private async Task RunVerificationLoop(CancellationToken ct)
    {
        // Initial delay before first verification (let app settle)
        await Task.Delay(TimeSpan.FromMinutes(2), ct);
        
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PerformVerificationAsync(ct);
                await Task.Delay(VerificationInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in verification loop");
                await Task.Delay(TimeSpan.FromMinutes(5), ct); // Wait before retry
            }
        }
    }
    
    private async Task PerformVerificationAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_settings.SyncFolderPath))
        {
            _logger.LogWarning("Sync folder does not exist, skipping verification");
            return;
        }
        
        IsVerifying = true;
        var startTime = DateTime.Now;
        var fileCount = 0;
        var discrepancies = 0;
        var changesDetected = new ConcurrentBag<string>();
        
        try
        {
            _logger.LogInformation($"Starting full tree verification of {_settings.SyncFolderPath}");
            
            // Get all files in sync folder
            var files = Directory.EnumerateFiles(_settings.SyncFolderPath, "*", SearchOption.AllDirectories)
                .Where(f => !ShouldSkipFile(f))
                .ToList();
            
            fileCount = files.Count;
            
            // Process files in parallel with limited concurrency
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = ct
            };
            
            await Parallel.ForEachAsync(files, options, async (filePath, token) =>
            {
                try
                {
                    var relativePath = Path.GetRelativePath(_settings.SyncFolderPath, filePath);
                    
                    // Compute current hash
                    var currentHash = await _hashingService.ComputeFileHashAsync(filePath, token);
                    
                    // Get stored state from manifest
                    var manifest = _syncService.GetManifest();
                    var storedFile = manifest
                        .FirstOrDefault(f => f.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
                    
                    if (storedFile == null)
                    {
                        // File exists but not in manifest - new file missed by FSW
                        changesDetected.Add(filePath);
                        Interlocked.Increment(ref discrepancies);
                        _logger.LogWarning($"[Verification] New file not in manifest: {relativePath}");
                    }
                    else if (storedFile.ContentHash != currentHash)
                    {
                        // Hash mismatch - file changed but FSW missed it
                        changesDetected.Add(filePath);
                        Interlocked.Increment(ref discrepancies);
                        _logger.LogWarning($"[Verification] Hash mismatch: {relativePath}");
                    }
                }
                catch (IOException)
                {
                    // File locked or in use, skip
                }
                catch (UnauthorizedAccessException)
                {
                    // Access denied, skip
                }
            });
            
            // Also check for deleted files (in manifest but not on disk)
            var manifestFiles = _syncService.GetManifest().ToList();
            foreach (var manifestFile in manifestFiles)
            {
                if (ct.IsCancellationRequested) break;
                
                var fullPath = Path.Combine(_settings.SyncFolderPath, manifestFile.RelativePath);
                if (!File.Exists(fullPath) && !ShouldSkipFile(fullPath))
                {
                    discrepancies++;
                    _logger.LogWarning($"[Verification] File in manifest but deleted: {manifestFile.RelativePath}");
                    // Trigger delete sync
                    changesDetected.Add(fullPath);
                }
            }
            
            var elapsed = DateTime.Now - startTime;
            _logger.LogInformation($"Verification complete: {fileCount} files checked, {discrepancies} discrepancies in {elapsed.TotalSeconds:F1}s");
            
            // Trigger sync for any detected changes
            if (changesDetected.Count > 0 && !ct.IsCancellationRequested)
            {
                _logger.LogInformation($"Triggering resync for {changesDetected.Count} changed files");
                
                // Force a full sync to reconcile discrepancies
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _syncService.ForceSyncAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during resync after verification");
                    }
                });
            }
            
            LastVerificationFileCount = fileCount;
            LastVerificationDiscrepancies = discrepancies;
            LastVerificationTime = DateTime.Now;
            
            VerificationCompleted?.Invoke(fileCount, discrepancies);
        }
        finally
        {
            IsVerifying = false;
        }
    }
    
    private bool ShouldSkipFile(string path)
    {
        var fileName = Path.GetFileName(path);
        
        // Skip hidden/system files
        if (fileName.StartsWith('.') || fileName.StartsWith('~'))
            return true;
        
        // Skip internal Swarm folders
        if (path.Contains(".swarm-cache") || path.Contains(".swarm-versions"))
            return true;
        
        return false;
    }
    
    public void Dispose()
    {
        Stop();
    }
}
