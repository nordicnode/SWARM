using Swarm.Core.Abstractions;
using Swarm.Core.Models;
using Swarm.Core.Services;
using Xunit;

namespace Swarm.Core.Tests;

/// <summary>
/// Unit tests for SqliteFileStateRepository.
/// These tests verify the SQLite-backed repository works correctly.
/// </summary>
public class SqliteFileStateRepositoryTests : IDisposable
{
    private readonly Settings _settings;
    private readonly string _testSyncFolder;
    private SqliteFileStateRepository _repository;

    public SqliteFileStateRepositoryTests()
    {
        _testSyncFolder = Path.Combine(Path.GetTempPath(), $"SwarmSqliteTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testSyncFolder);
        _settings = new Settings { SyncFolderPath = _testSyncFolder };
        _repository = new SqliteFileStateRepository(_settings);
    }

    [Fact]
    public void Count_ReturnsZero_WhenEmpty()
    {
        Assert.Equal(0, _repository.Count);
    }

    [Fact]
    public void AddOrUpdate_IncreasesCount()
    {
        // Arrange
        var file = CreateTestFile("test.txt");

        // Act
        _repository.AddOrUpdate(file);
        _repository.SaveChanges();

        // Assert
        Assert.Equal(1, _repository.Count);
    }

    [Fact]
    public void Get_ReturnsNull_WhenNotFound()
    {
        // Act
        var result = _repository.Get("nonexistent.txt");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Get_ReturnsFile_WhenExists()
    {
        // Arrange
        var file = CreateTestFile("document.pdf");
        _repository.AddOrUpdate(file);
        _repository.SaveChanges();

        // Act
        var result = _repository.Get("document.pdf");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("document.pdf", result.RelativePath);
        Assert.Equal("HASH123", result.ContentHash);
    }

    [Fact]
    public void Exists_ReturnsFalse_WhenNotFound()
    {
        Assert.False(_repository.Exists("missing.txt"));
    }

    [Fact]
    public void Exists_ReturnsTrue_WhenFound()
    {
        // Arrange
        _repository.AddOrUpdate(CreateTestFile("exists.txt"));
        _repository.SaveChanges();

        // Act & Assert
        Assert.True(_repository.Exists("exists.txt"));
    }

    [Fact]
    public void Remove_ReturnsFalse_WhenNotFound()
    {
        Assert.False(_repository.Remove("missing.txt"));
    }

    [Fact]
    public void Remove_ReturnsTrue_AndRemovesFile()
    {
        // Arrange
        _repository.AddOrUpdate(CreateTestFile("toremove.txt"));
        _repository.SaveChanges();

        // Act
        var result = _repository.Remove("toremove.txt");
        _repository.SaveChanges();

        // Assert
        Assert.True(result);
        Assert.Equal(0, _repository.Count);
        Assert.False(_repository.Exists("toremove.txt"));
    }

    [Fact]
    public void GetAll_ReturnsAllFiles()
    {
        // Arrange
        _repository.AddOrUpdate(CreateTestFile("file1.txt"));
        _repository.AddOrUpdate(CreateTestFile("file2.txt"));
        _repository.AddOrUpdate(CreateTestFile("subdir/file3.txt"));
        _repository.SaveChanges();

        // Act
        var all = _repository.GetAll();

        // Assert
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void Clear_RemovesAllFiles()
    {
        // Arrange
        _repository.AddOrUpdate(CreateTestFile("a.txt"));
        _repository.AddOrUpdate(CreateTestFile("b.txt"));
        _repository.SaveChanges();

        // Act
        _repository.Clear();

        // Assert
        Assert.Equal(0, _repository.Count);
    }

    [Fact]
    public void AddOrUpdate_UpdatesExistingFile()
    {
        // Arrange
        var original = CreateTestFile("update.txt", "HASH1");
        _repository.AddOrUpdate(original);
        _repository.SaveChanges();

        var updated = CreateTestFile("update.txt", "HASH2");

        // Act
        _repository.AddOrUpdate(updated);
        _repository.SaveChanges();

        // Assert
        Assert.Equal(1, _repository.Count);
        var result = _repository.Get("update.txt");
        Assert.Equal("HASH2", result?.ContentHash);
    }

    [Fact]
    public void AsReadOnlyDictionary_ReturnsAllEntries()
    {
        // Arrange
        _repository.AddOrUpdate(CreateTestFile("x.txt"));
        _repository.AddOrUpdate(CreateTestFile("y.txt"));
        _repository.SaveChanges();

        // Act
        var dict = _repository.AsReadOnlyDictionary();

        // Assert
        Assert.Equal(2, dict.Count);
        Assert.True(dict.ContainsKey("x.txt"));
        Assert.True(dict.ContainsKey("y.txt"));
    }

    [Fact]
    public void DatabaseFile_IsCreated()
    {
        // Assert
        var dbPath = Path.Combine(_testSyncFolder, ".swarm-state.db");
        Assert.True(File.Exists(dbPath));
    }

    [Fact]
    public void Data_PersistsAcrossInstances()
    {
        // Arrange
        _repository.AddOrUpdate(CreateTestFile("persist.txt", "PERSISTHASH"));
        _repository.SaveChanges();
        _repository.Dispose();

        // Act - Create new instance
        var newRepository = new SqliteFileStateRepository(_settings);

        // Assert
        Assert.Equal(1, newRepository.Count);
        var restored = newRepository.Get("persist.txt");
        Assert.NotNull(restored);
        Assert.Equal("PERSISTHASH", restored.ContentHash);

        newRepository.Dispose();
    }

    [Fact]
    public void BulkInsert_HandlesMany()
    {
        // Arrange - Insert 1000 files
        for (int i = 0; i < 1000; i++)
        {
            _repository.AddOrUpdate(CreateTestFile($"file{i}.txt", $"HASH{i}"));
        }

        // Act
        _repository.SaveChanges();

        // Assert
        Assert.Equal(1000, _repository.Count);
    }

    private static SyncedFile CreateTestFile(string relativePath, string hash = "HASH123")
    {
        return new SyncedFile
        {
            RelativePath = relativePath,
            ContentHash = hash,
            FileSize = 1024,
            LastModified = DateTime.UtcNow,
            Action = SyncAction.Create,
            SourcePeerId = "test-peer",
            IsDirectory = false
        };
    }

    public void Dispose()
    {
        _repository?.Dispose();
        try
        {
            if (Directory.Exists(_testSyncFolder))
            {
                // SQLite may hold file handles briefly
                Thread.Sleep(100);
                Directory.Delete(_testSyncFolder, recursive: true);
            }
        }
        catch { /* Ignore cleanup errors */ }
    }
}
