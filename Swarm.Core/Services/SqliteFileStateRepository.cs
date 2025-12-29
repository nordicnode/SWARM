using Microsoft.EntityFrameworkCore;
using Serilog;
using Swarm.Core.Abstractions;
using Swarm.Core.Data;
using Swarm.Core.Models;
using System.Text.Json;

namespace Swarm.Core.Services;

/// <summary>
/// SQLite-backed file state repository using EF Core.
/// Uses WAL mode for crash resilience and handles large file counts efficiently.
/// Auto-migrates from legacy JSON cache on first run.
/// </summary>
public class SqliteFileStateRepository : IFileStateRepository, IDisposable
{
    private const string DbFileName = ".swarm-state.db";
    private const string LegacyJsonFileName = ".swarm-cache";
    private readonly FileStateDbContext _context;
    private readonly string _syncFolderPath;
    private readonly object _lock = new();
    private bool _disposed;

    public SqliteFileStateRepository(Settings settings)
    {
        _syncFolderPath = settings.SyncFolderPath;
        var dbPath = Path.Combine(settings.SyncFolderPath, DbFileName);
        _context = new FileStateDbContext(dbPath);
        
        // Enable WAL mode for crash resilience
        _context.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        _context.Database.EnsureCreated();
        
        // Auto-migrate from legacy JSON cache if exists
        MigrateFromJsonIfNeeded();
        
        Log.Information("Initialized SQLite file state repository: {Path}", dbPath);
    }

    /// <summary>
    /// Migrates data from legacy .swarm-cache (JSON) to SQLite.
    /// Only runs once when upgrading from older versions.
    /// </summary>
    private void MigrateFromJsonIfNeeded()
    {
        var jsonCachePath = Path.Combine(_syncFolderPath, LegacyJsonFileName);
        
        if (!File.Exists(jsonCachePath))
            return;
            
        // Only migrate if SQLite is empty (first run after upgrade)
        if (_context.FileStates.Any())
        {
            // SQLite already has data - just delete the old JSON file
            TryDeleteLegacyCache(jsonCachePath);
            return;
        }
        
        try
        {
            Log.Information("Migrating from legacy JSON cache to SQLite...");
            
            var json = File.ReadAllText(jsonCachePath);
            var legacyCache = JsonSerializer.Deserialize<Dictionary<string, SyncedFile>>(json);
            
            if (legacyCache != null && legacyCache.Count > 0)
            {
                foreach (var kvp in legacyCache)
                {
                    _context.FileStates.Add(FileStateEntity.FromSyncedFile(kvp.Value));
                }
                
                _context.SaveChanges();
                Log.Information("Migrated {Count} file states from JSON to SQLite", legacyCache.Count);
            }
            
            // Backup and delete the old JSON file
            var backupPath = jsonCachePath + ".migrated";
            File.Move(jsonCachePath, backupPath, overwrite: true);
            Log.Information("Legacy JSON cache backed up to {Path}", backupPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to migrate from legacy JSON cache - starting fresh");
        }
    }
    
    private static void TryDeleteLegacyCache(string path)
    {
        try
        {
            File.Delete(path);
            Log.Debug("Deleted legacy JSON cache");
        }
        catch
        {
            // Ignore - not critical
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _context.FileStates.Count();
            }
        }
    }

    public SyncedFile? Get(string relativePath)
    {
        lock (_lock)
        {
            var entity = _context.FileStates.Find(relativePath);
            return entity?.ToSyncedFile();
        }
    }

    public IReadOnlyList<SyncedFile> GetAll()
    {
        lock (_lock)
        {
            return _context.FileStates
                .AsNoTracking()
                .Select(e => e.ToSyncedFile())
                .ToList();
        }
    }

    public void AddOrUpdate(SyncedFile file)
    {
        lock (_lock)
        {
            var existing = _context.FileStates.Find(file.RelativePath);
            if (existing != null)
            {
                existing.ContentHash = file.ContentHash;
                existing.FileSize = file.FileSize;
                existing.LastModified = file.LastModified;
                existing.Action = (int)file.Action;
                existing.SourcePeerId = file.SourcePeerId;
                existing.IsDirectory = file.IsDirectory;
            }
            else
            {
                _context.FileStates.Add(FileStateEntity.FromSyncedFile(file));
            }
        }
    }

    public bool Remove(string relativePath)
    {
        lock (_lock)
        {
            var entity = _context.FileStates.Find(relativePath);
            if (entity != null)
            {
                _context.FileStates.Remove(entity);
                return true;
            }
            return false;
        }
    }

    public bool Exists(string relativePath)
    {
        lock (_lock)
        {
            return _context.FileStates.Any(e => e.RelativePath == relativePath);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _context.FileStates.ExecuteDelete();
        }
    }

    public IReadOnlyDictionary<string, SyncedFile> AsReadOnlyDictionary()
    {
        lock (_lock)
        {
            return _context.FileStates
                .AsNoTracking()
                .ToDictionary(
                    e => e.RelativePath,
                    e => e.ToSyncedFile(),
                    StringComparer.OrdinalIgnoreCase);
        }
    }

    public void SaveChanges()
    {
        lock (_lock)
        {
            try
            {
                _context.SaveChanges();
                Log.Debug("Saved {Count} file states to SQLite", _context.FileStates.Count());
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save file states to SQLite");
            }
        }
    }

    public void Load()
    {
        // SQLite is always loaded - no-op
        // Data is persisted automatically
        Log.Debug("SQLite repository ready with {Count} entries", Count);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            lock (_lock)
            {
                SaveChanges();
                _context.Dispose();
            }
            _disposed = true;
        }
    }
}
