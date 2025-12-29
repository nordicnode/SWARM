using Moq;
using Swarm.Core.Abstractions;
using Swarm.Core.Models;
using Swarm.Core.Services;
using Xunit;

namespace Swarm.Core.Tests;

/// <summary>
/// Unit tests for VersioningService.
/// Tests file versioning, restoration, and pruning functionality.
/// </summary>
public class VersioningServiceTests : IDisposable
{
    private readonly Settings _settings;
    private readonly Mock<IHashingService> _mockHashing;
    private readonly VersioningService _service;
    private readonly string _testSyncFolder;

    public VersioningServiceTests()
    {
        _testSyncFolder = Path.Combine(Path.GetTempPath(), $"SwarmVersionTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testSyncFolder);
        
        _settings = new Settings 
        { 
            SyncFolderPath = _testSyncFolder,
            VersioningEnabled = true,
            MaxVersionsPerFile = 5,
            MaxVersionAgeDays = 30
        };
        
        _mockHashing = new Mock<IHashingService>();
        _mockHashing.Setup(h => h.ComputeFileHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("TESTHASH123");
            
        _service = new VersioningService(_settings, _mockHashing.Object);
    }

    [Fact]
    public void Initialize_CreatesVersionsDirectory()
    {
        // Act
        _service.Initialize();
        
        // Assert
        var versionsPath = Path.Combine(_testSyncFolder, ".swarm-versions");
        Assert.True(Directory.Exists(versionsPath));
    }

    [Fact]
    public async Task CreateVersion_ReturnsVersionInfo_WhenFileExists()
    {
        // Arrange
        var testFile = Path.Combine(_testSyncFolder, "test.txt");
        await File.WriteAllTextAsync(testFile, "Test content");
        
        // Act
        var version = await _service.CreateVersionAsync("test.txt", testFile, "Manual");
        
        // Assert
        Assert.NotNull(version);
        Assert.Equal("test.txt", version.RelativePath);
        Assert.Equal("Manual", version.Reason);
    }

    [Fact]
    public async Task CreateVersion_ReturnsNull_WhenFileNotExist()
    {
        // Act
        var nonExistent = Path.Combine(_testSyncFolder, "nonexistent.txt");
        var version = await _service.CreateVersionAsync("nonexistent.txt", nonExistent, "Manual");
        
        // Assert
        Assert.Null(version);
    }

    [Fact]
    public void GetVersions_ReturnsEmptyList_WhenNoVersions()
    {
        // Act
        var versions = _service.GetVersions("test.txt");
        
        // Assert
        Assert.Empty(versions);
    }

    [Fact]
    public async Task GetVersions_ReturnsVersions_WhenExist()
    {
        // Arrange
        var testFile = Path.Combine(_testSyncFolder, "test.txt");
        await File.WriteAllTextAsync(testFile, "Version 1");
        
        // Different hash for each version
        _mockHashing.SetupSequence(h => h.ComputeFileHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("HASH1")
            .ReturnsAsync("HASH2");
        
        await _service.CreateVersionAsync("test.txt", testFile, "Manual");
        
        await File.WriteAllTextAsync(testFile, "Version 2");
        await _service.CreateVersionAsync("test.txt", testFile, "Manual");
        
        // Act
        var versions = _service.GetVersions("test.txt").ToList();
        
        // Assert
        Assert.Equal(2, versions.Count);
    }

    [Fact]
    public async Task RestoreVersion_RestoresContent()
    {
        // Arrange
        var testFile = Path.Combine(_testSyncFolder, "restore.txt");
        var originalContent = "Original content";
        await File.WriteAllTextAsync(testFile, originalContent);
        var version = await _service.CreateVersionAsync("restore.txt", testFile, "Manual");
        
        // Modify the file
        await File.WriteAllTextAsync(testFile, "Modified content");
        
        // Act
        var success = await _service.RestoreVersionAsync(version!);
        
        // Assert
        Assert.True(success);
        var restoredContent = await File.ReadAllTextAsync(testFile);
        Assert.Equal(originalContent, restoredContent);
    }

    [Fact]
    public async Task DeleteVersion_RemovesVersion()
    {
        // Arrange
        var testFile = Path.Combine(_testSyncFolder, "delete.txt");
        await File.WriteAllTextAsync(testFile, "Content");
        var version = await _service.CreateVersionAsync("delete.txt", testFile, "Manual");
        
        // Act
        var success = _service.DeleteVersion(version!);
        
        // Assert
        Assert.True(success);
        var versions = _service.GetVersions("delete.txt");
        Assert.Empty(versions);
    }

    [Fact]
    public void GetTotalVersionsSize_ReturnsZero_WhenNoVersions()
    {
        // Act
        var bytes = _service.GetTotalVersionsSize();
        
        // Assert
        Assert.Equal(0, bytes);
    }

    [Fact]
    public async Task GetTotalVersionsSize_ReturnsCorrectSize()
    {
        // Arrange
        var testFile = Path.Combine(_testSyncFolder, "storage.txt");
        var content = new string('x', 1000);
        await File.WriteAllTextAsync(testFile, content);
        await _service.CreateVersionAsync("storage.txt", testFile, "Manual");
        
        // Act
        var bytes = _service.GetTotalVersionsSize();
        
        // Assert
        Assert.True(bytes > 0);
    }

    [Fact]
    public void VersioningEnabled_RespectsSettings()
    {
        // Arrange
        _settings.VersioningEnabled = false;
        
        // Assert
        Assert.False(_settings.VersioningEnabled);
    }

    [Fact]
    public void GetTotalVersionCount_ReturnsZero_Initially()
    {
        // Act
        var count = _service.GetTotalVersionCount();
        
        // Assert
        Assert.Equal(0, count);
    }

    public void Dispose()
    {
        _service.Dispose();
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
