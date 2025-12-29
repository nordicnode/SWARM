using Swarm.Core.Abstractions;
using Swarm.Core.Models;
using Swarm.Core.Services;
using Xunit;

namespace Swarm.Core.Tests;

/// <summary>
/// Unit tests for FileStateRepository.
/// </summary>
public class FileStateRepositoryTests : IDisposable
{
    private readonly Settings _settings;
    private readonly string _testSyncFolder;
    private readonly IFileStateRepository _repository;

    public FileStateRepositoryTests()
    {
        _testSyncFolder = Path.Combine(Path.GetTempPath(), $"SwarmRepoTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testSyncFolder);
        _settings = new Settings { SyncFolderPath = _testSyncFolder };
        _repository = new FileStateRepository(_settings);
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

        // Act
        var result = _repository.Get("document.pdf");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("document.pdf", result.RelativePath);
        Assert.Equal("HASH123", result.ContentHash);
    }

    [Fact]
    public void Get_IsCaseInsensitive()
    {
        // Arrange
        var file = CreateTestFile("Document.PDF");
        _repository.AddOrUpdate(file);

        // Act
        var result = _repository.Get("document.pdf");

        // Assert
        Assert.NotNull(result);
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

        // Act
        var result = _repository.Remove("toremove.txt");

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

        var updated = CreateTestFile("update.txt", "HASH2");

        // Act
        _repository.AddOrUpdate(updated);

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

        // Act
        var dict = _repository.AsReadOnlyDictionary();

        // Assert
        Assert.Equal(2, dict.Count);
        Assert.True(dict.ContainsKey("x.txt"));
        Assert.True(dict.ContainsKey("y.txt"));
    }

    [Fact]
    public void SaveChanges_PersistsToFile()
    {
        // Arrange
        _repository.AddOrUpdate(CreateTestFile("persist.txt"));

        // Act
        _repository.SaveChanges();

        // Assert
        var cachePath = Path.Combine(_testSyncFolder, ".swarm-cache");
        Assert.True(File.Exists(cachePath));
    }

    [Fact]
    public void Load_RestoresPersistedState()
    {
        // Arrange
        _repository.AddOrUpdate(CreateTestFile("restored.txt", "RESTOREHASH"));
        _repository.SaveChanges();

        // Create new repository instance
        var newRepository = new FileStateRepository(_settings);

        // Act
        newRepository.Load();

        // Assert
        Assert.Equal(1, newRepository.Count);
        var restored = newRepository.Get("restored.txt");
        Assert.NotNull(restored);
        Assert.Equal("RESTOREHASH", restored.ContentHash);
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
        try
        {
            if (Directory.Exists(_testSyncFolder))
            {
                Directory.Delete(_testSyncFolder, recursive: true);
            }
        }
        catch { /* Ignore cleanup errors */ }
    }
}
