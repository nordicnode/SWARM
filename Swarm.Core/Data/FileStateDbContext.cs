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

    public FileStateDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite($"Data Source={_dbPath};Mode=ReadWriteCreate;Cache=Shared");
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
