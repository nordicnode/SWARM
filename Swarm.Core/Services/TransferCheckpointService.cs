using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Swarm.Core.Data;

namespace Swarm.Core.Services;

/// <summary>
/// Service for managing transfer checkpoints to enable resumable file transfers.
/// Persists transfer progress to SQLite and allows resuming interrupted transfers.
/// </summary>
public class TransferCheckpointService : IDisposable
{
    private readonly FileStateDbContext _context;
    private readonly ILogger<TransferCheckpointService> _logger;
    private readonly object _lock = new();
    private bool _disposed;

    // Update checkpoint every N bytes to avoid excessive database writes
    private const long CheckpointInterval = 1024 * 1024; // 1 MB

    public TransferCheckpointService(FileStateDbContext context, ILogger<TransferCheckpointService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new checkpoint for a transfer that is starting.
    /// </summary>
    public Guid CreateCheckpoint(string relativePath, string peerId, bool isIncoming, long totalBytes, string contentHash, string tempFilePath)
    {
        lock (_lock)
        {
            // Remove any existing checkpoint for this file/peer combination
            var existing = _context.TransferCheckpoints
                .FirstOrDefault(c => c.RelativePath == relativePath && c.PeerId == peerId && !c.IsCompleted);
            
            if (existing != null)
            {
                _context.TransferCheckpoints.Remove(existing);
            }

            var checkpoint = new TransferCheckpointEntity
            {
                RelativePath = relativePath,
                PeerId = peerId,
                IsIncoming = isIncoming,
                TotalBytes = totalBytes,
                BytesTransferred = 0,
                ContentHash = contentHash,
                TempFilePath = tempFilePath,
                StartedAt = DateTime.UtcNow,
                LastUpdatedAt = DateTime.UtcNow,
                IsCompleted = false
            };

            _context.TransferCheckpoints.Add(checkpoint);
            _context.SaveChanges();

            _logger.LogDebug("Created checkpoint for {Path} with peer {PeerId}", relativePath, peerId);
            return checkpoint.Id;
        }
    }

    /// <summary>
    /// Updates the progress of a transfer checkpoint.
    /// </summary>
    public void UpdateProgress(Guid checkpointId, long bytesTransferred)
    {
        lock (_lock)
        {
            var checkpoint = _context.TransferCheckpoints.Find(checkpointId);
            if (checkpoint == null) return;

            // Only update if progress is significant (avoid excessive writes)
            var bytesChange = bytesTransferred - checkpoint.BytesTransferred;
            if (bytesChange < CheckpointInterval && bytesTransferred != checkpoint.TotalBytes) return;

            checkpoint.BytesTransferred = bytesTransferred;
            checkpoint.LastUpdatedAt = DateTime.UtcNow;
            _context.SaveChanges();

            _logger.LogDebug("Updated checkpoint {Id}: {Bytes}/{Total}", 
                checkpointId, bytesTransferred, checkpoint.TotalBytes);
        }
    }

    /// <summary>
    /// Marks a transfer as completed and removes the checkpoint.
    /// </summary>
    public void CompleteCheckpoint(Guid checkpointId)
    {
        lock (_lock)
        {
            var checkpoint = _context.TransferCheckpoints.Find(checkpointId);
            if (checkpoint != null)
            {
                checkpoint.IsCompleted = true;
                checkpoint.LastUpdatedAt = DateTime.UtcNow;
                _context.SaveChanges();

                _logger.LogDebug("Completed checkpoint {Id} for {Path}", checkpointId, checkpoint.RelativePath);
            }
        }
    }

    /// <summary>
    /// Removes a checkpoint (transfer was cancelled or failed permanently).
    /// </summary>
    public void RemoveCheckpoint(Guid checkpointId)
    {
        lock (_lock)
        {
            var checkpoint = _context.TransferCheckpoints.Find(checkpointId);
            if (checkpoint != null)
            {
                _context.TransferCheckpoints.Remove(checkpoint);
                _context.SaveChanges();

                _logger.LogDebug("Removed checkpoint {Id}", checkpointId);
            }
        }
    }

    /// <summary>
    /// Gets a resumable checkpoint for a specific file and peer.
    /// Returns null if no resumable checkpoint exists.
    /// </summary>
    public TransferCheckpointEntity? GetResumableCheckpoint(string relativePath, string peerId, string contentHash)
    {
        lock (_lock)
        {
            var checkpoint = _context.TransferCheckpoints
                .AsNoTracking()
                .FirstOrDefault(c => 
                    c.RelativePath == relativePath && 
                    c.PeerId == peerId && 
                    c.ContentHash == contentHash &&
                    !c.IsCompleted);

            if (checkpoint != null)
            {
                // Verify the temp file exists for incoming transfers
                if (checkpoint.IsIncoming && !string.IsNullOrEmpty(checkpoint.TempFilePath))
                {
                    if (!File.Exists(checkpoint.TempFilePath))
                    {
                        _logger.LogDebug("Temp file missing for checkpoint {Id}, cannot resume", checkpoint.Id);
                        RemoveCheckpoint(checkpoint.Id);
                        return null;
                    }

                    // Verify temp file size matches checkpoint
                    var fileInfo = new FileInfo(checkpoint.TempFilePath);
                    if (fileInfo.Length != checkpoint.BytesTransferred)
                    {
                        _logger.LogDebug("Temp file size mismatch for checkpoint {Id}, cannot resume", checkpoint.Id);
                        RemoveCheckpoint(checkpoint.Id);
                        return null;
                    }
                }

                _logger.LogDebug("Found resumable checkpoint for {Path}: {Bytes}/{Total}", 
                    relativePath, checkpoint.BytesTransferred, checkpoint.TotalBytes);
            }

            return checkpoint;
        }
    }

    /// <summary>
    /// Gets all incomplete checkpoints (for cleanup on startup).
    /// </summary>
    public List<TransferCheckpointEntity> GetIncompleteCheckpoints()
    {
        lock (_lock)
        {
            return _context.TransferCheckpoints
                .AsNoTracking()
                .Where(c => !c.IsCompleted)
                .ToList();
        }
    }

    /// <summary>
    /// Cleans up stale checkpoints older than the specified duration.
    /// </summary>
    public int CleanupStaleCheckpoints(TimeSpan maxAge)
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow - maxAge;
            var stale = _context.TransferCheckpoints
                .Where(c => c.LastUpdatedAt < cutoff && !c.IsCompleted)
                .ToList();

            foreach (var checkpoint in stale)
            {
                // Clean up temp files
                if (!string.IsNullOrEmpty(checkpoint.TempFilePath) && File.Exists(checkpoint.TempFilePath))
                {
                    try
                    {
                        File.Delete(checkpoint.TempFilePath);
                    }
                    catch { /* Ignore cleanup errors */ }
                }
                _context.TransferCheckpoints.Remove(checkpoint);
            }

            if (stale.Count > 0)
            {
                _context.SaveChanges();
                _logger.LogInformation("Cleaned up {Count} stale checkpoints", stale.Count);
            }

            return stale.Count;
        }
    }

    /// <summary>
    /// Cleans up completed checkpoints.
    /// </summary>
    public int CleanupCompletedCheckpoints()
    {
        lock (_lock)
        {
            var count = _context.TransferCheckpoints
                .Where(c => c.IsCompleted)
                .ExecuteDelete();

            if (count > 0)
            {
                _logger.LogDebug("Cleaned up {Count} completed checkpoints", count);
            }

            return count;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            lock (_lock)
            {
                _context.SaveChanges();
            }
            _disposed = true;
        }
    }
}
