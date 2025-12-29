using System.IO;
using System.Security.Cryptography;
using Swarm.Core.Abstractions;
using Swarm.Core.Models;

namespace Swarm.Core.Services;

/// <summary>
/// Progress information for integrity verification.
/// </summary>
public struct IntegrityProgress
{
    public int TotalFiles { get; set; }
    public int CheckedFiles { get; set; }
    public string CurrentFile { get; set; }
    public double PercentComplete => TotalFiles > 0 ? (double)CheckedFiles / TotalFiles * 100 : 0;
}

/// <summary>
/// Service for verifying file integrity by comparing file hashes against stored state.
/// </summary>
public class IntegrityService
{
    private readonly Settings _settings;
    private readonly SyncService _syncService;
    private readonly IHashingService _hashingService;

    /// <summary>
    /// Raised during integrity verification to report progress.
    /// </summary>
    public event Action<IntegrityProgress>? ProgressChanged;

    public IntegrityService(Settings settings, SyncService syncService, IHashingService hashingService)
    {
        _settings = settings;
        _syncService = syncService;
        _hashingService = hashingService;
    }

    /// <summary>
    /// Verifies the integrity of all local files by comparing their current hashes
    /// against the stored state from the last sync.
    /// </summary>
    public async Task<IntegrityResult> VerifyLocalIntegrityAsync(CancellationToken ct = default)
    {
        var result = new IntegrityResult
        {
            StartTime = DateTime.UtcNow
        };

        try
        {
            var fileStates = _syncService.GetFileStates();
            var syncFolder = _settings.SyncFolderPath;

            if (!Directory.Exists(syncFolder))
            {
                result.CompletedSuccessfully = false;
                result.ErrorMessage = "Sync folder does not exist.";
                result.Duration = DateTime.UtcNow - result.StartTime;
                return result;
            }

            // Get all actual files on disk
            var filesOnDisk = Directory.EnumerateFiles(syncFolder, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(syncFolder, f))
                .Where(f => !ShouldIgnore(f))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Get all files in stored state
            var storedPaths = fileStates.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

            result.TotalFiles = storedPaths.Count;
            var checkedCount = 0;

            // Check stored files against disk
            foreach (var relativePath in storedPaths)
            {
                ct.ThrowIfCancellationRequested();

                var fullPath = Path.Combine(syncFolder, relativePath);
                var storedFile = fileStates[relativePath];

                // Report progress
                checkedCount++;
                ProgressChanged?.Invoke(new IntegrityProgress
                {
                    TotalFiles = result.TotalFiles,
                    CheckedFiles = checkedCount,
                    CurrentFile = relativePath
                });

                if (!File.Exists(fullPath))
                {
                    // File in state but missing from disk
                    result.MissingFiles.Add(relativePath);
                    continue;
                }

                // Compute current hash
                try
                {
                    var currentHash = await _hashingService.ComputeFileHashAsync(fullPath, ct);

                    if (string.Equals(currentHash, storedFile.ContentHash, StringComparison.OrdinalIgnoreCase))
                    {
                        result.HealthyFiles++;
                    }
                    else
                    {
                        result.CorruptedFiles.Add(new IntegrityIssue
                        {
                            RelativePath = relativePath,
                            ExpectedHash = storedFile.ContentHash,
                            ActualHash = currentHash
                        });
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to hash file {relativePath}: {ex.Message}");
                    result.CorruptedFiles.Add(new IntegrityIssue
                    {
                        RelativePath = relativePath,
                        ExpectedHash = storedFile.ContentHash,
                        ActualHash = $"ERROR: {ex.Message}"
                    });
                }
            }

            // Find files on disk that aren't in stored state
            foreach (var diskFile in filesOnDisk)
            {
                if (!storedPaths.Contains(diskFile))
                {
                    result.UnknownFiles.Add(diskFile);
                }
            }

            result.CompletedSuccessfully = true;
        }
        catch (OperationCanceledException)
        {
            result.CompletedSuccessfully = false;
            result.ErrorMessage = "Integrity check was cancelled.";
        }
        catch (Exception ex)
        {
            result.CompletedSuccessfully = false;
            result.ErrorMessage = $"Integrity check failed: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"Integrity check error: {ex}");
        }

        result.Duration = DateTime.UtcNow - result.StartTime;
        return result;
    }



    /// <summary>
    /// Determines if a file should be ignored during integrity check.
    /// </summary>
    private static bool ShouldIgnore(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        return fileName.StartsWith('.') || fileName.StartsWith('~');
    }
}

