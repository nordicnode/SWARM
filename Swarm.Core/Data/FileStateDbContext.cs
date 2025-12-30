using Microsoft.EntityFrameworkCore;
using Swarm.Core.Models;

namespace Swarm.Core.Data;

/// <summary>
/// EF Core DbContext for file state persistence.
/// Uses SQLite with WAL mode for crash resilience.
/// </summary>
public class FileStateDbContext : DbContext
{
    private readonly string _dbPath;

    public DbSet<FileStateEntity> FileStates { get; set; } = null!;
    public DbSet<TransferCheckpointEntity> TransferCheckpoints { get; set; } = null!;

    public FileStateDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Use Cache=Shared for better connection pooling
        optionsBuilder.UseSqlite($"Data Source={_dbPath};Mode=ReadWriteCreate;Cache=Shared");
    }

    /// <summary>
    /// Ensures the database is created and configured with WAL mode for better concurrency.
    /// Call this after creating the context to enable Write-Ahead Logging.
    /// </summary>
    public void EnableWalMode()
    {
        // WAL mode provides:
        // - Readers don't block writers
        // - Writers don't block readers
        // - Better crash resilience
        // - ~10x faster writes in many cases
        Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;"); // Good balance of safety and speed
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FileStateEntity>(entity =>
        {
            entity.HasKey(e => e.RelativePath);
            entity.Property(e => e.RelativePath).HasMaxLength(1024);
            entity.Property(e => e.ContentHash).HasMaxLength(128);
            entity.Property(e => e.SourcePeerId).HasMaxLength(64);
            entity.HasIndex(e => e.ContentHash);
        });

        modelBuilder.Entity<TransferCheckpointEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RelativePath).HasMaxLength(1024).IsRequired();
            entity.Property(e => e.PeerId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ContentHash).HasMaxLength(128);
            entity.Property(e => e.TempFilePath).HasMaxLength(2048);
            entity.HasIndex(e => new { e.RelativePath, e.PeerId }).IsUnique();
        });
    }
}

/// <summary>
/// Entity for storing file state in SQLite.
/// </summary>
public class FileStateEntity
{
    public string RelativePath { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime LastModified { get; set; }
    public int Action { get; set; }
    public string SourcePeerId { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }

    public SyncedFile ToSyncedFile()
    {
        return new SyncedFile
        {
            RelativePath = RelativePath,
            ContentHash = ContentHash,
            FileSize = FileSize,
            LastModified = LastModified,
            Action = (SyncAction)Action,
            SourcePeerId = SourcePeerId,
            IsDirectory = IsDirectory
        };
    }

    public static FileStateEntity FromSyncedFile(SyncedFile file)
    {
        return new FileStateEntity
        {
            RelativePath = file.RelativePath,
            ContentHash = file.ContentHash,
            FileSize = file.FileSize,
            LastModified = file.LastModified,
            Action = (int)file.Action,
            SourcePeerId = file.SourcePeerId,
            IsDirectory = file.IsDirectory
        };
    }
}

/// <summary>
/// Entity for storing transfer checkpoint data to enable resumable transfers.
/// </summary>
public class TransferCheckpointEntity
{
    /// <summary>
    /// Unique identifier for this checkpoint.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Relative path of the file being transferred.
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Peer ID the transfer is with.
    /// </summary>
    public string PeerId { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is an incoming (download) or outgoing (upload) transfer.
    /// </summary>
    public bool IsIncoming { get; set; }

    /// <summary>
    /// Total size of the file in bytes.
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Number of bytes already transferred.
    /// </summary>
    public long BytesTransferred { get; set; }

    /// <summary>
    /// Content hash of the file for integrity verification.
    /// </summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// Path to the temporary file for partial downloads.
    /// </summary>
    public string TempFilePath { get; set; } = string.Empty;

    /// <summary>
    /// When the transfer was started.
    /// </summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last time the checkpoint was updated.
    /// </summary>
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether the transfer was completed successfully.
    /// </summary>
    public bool IsCompleted { get; set; }
}

