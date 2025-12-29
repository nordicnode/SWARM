using Moq;
using Swarm.Core.Abstractions;
using Swarm.Core.Models;
using Swarm.Core.Services;
using Xunit;

namespace Swarm.Core.Tests;

/// <summary>
/// Unit tests for SyncService core functionality.
/// These tests focus on testable units without requiring real file I/O.
/// </summary>
public class SyncServiceTests : IDisposable
{
    private readonly Mock<IDiscoveryService> _mockDiscovery;
    private readonly Mock<ITransferService> _mockTransfer;
    private readonly Mock<IHashingService> _mockHashing;
    private readonly Settings _settings;
    private readonly string _testSyncFolder;

    public SyncServiceTests()
    {
        _mockDiscovery = new Mock<IDiscoveryService>();
        _mockTransfer = new Mock<ITransferService>();
        _mockHashing = new Mock<IHashingService>();
        
        // Create a temporary folder for testing
        _testSyncFolder = Path.Combine(Path.GetTempPath(), $"SwarmTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testSyncFolder);
        
        _settings = new Settings { SyncFolderPath = _testSyncFolder };
        
        // Setup default mocks
        _mockDiscovery.Setup(d => d.LocalId).Returns("test-local-id");
        _mockHashing.Setup(h => h.ComputeFileHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("TESTHASH123");
    }

    private SyncService CreateSyncService()
    {
        var versioningService = new VersioningService(_settings, _mockHashing.Object);
        var cacheService = new FileStateCacheService(_settings);
        
        return new SyncService(
            _settings,
            _mockDiscovery.Object,
            _mockTransfer.Object,
            versioningService,
            _mockHashing.Object,
            cacheService);
    }

    [Fact]
    public void GetTrackedFileCount_ReturnsZero_WhenNoFiles()
    {
        // Arrange
        using var syncService = CreateSyncService();
        
        // Act
        var count = syncService.GetTrackedFileCount();
        
        // Assert
        Assert.Equal(0, count);
    }

    [Fact]
    public void SyncFolderPath_ReturnsConfiguredPath()
    {
        // Arrange
        using var syncService = CreateSyncService();
        
        // Act & Assert
        Assert.Equal(_testSyncFolder, syncService.SyncFolderPath);
    }

    [Fact]
    public void IsEnabled_ReturnsFalse_ByDefault()
    {
        // Arrange
        _settings.IsSyncEnabled = false;
        using var syncService = CreateSyncService();
        
        // Act & Assert
        Assert.False(syncService.IsEnabled);
    }

    [Fact]
    public void IsEnabled_ReturnsTrue_WhenEnabled()
    {
        // Arrange
        _settings.IsSyncEnabled = true;
        using var syncService = CreateSyncService();
        
        // Act & Assert
        Assert.True(syncService.IsEnabled);
    }

    [Fact]
    public void GetManifest_ReturnsEmptyList_WhenNoFiles()
    {
        // Arrange
        using var syncService = CreateSyncService();
        
        // Act
        var manifest = syncService.GetManifest();
        
        // Assert
        Assert.Empty(manifest);
    }

    [Fact]
    public void GetSessionBytesTransferred_ReturnsZero_Initially()
    {
        // Arrange
        using var syncService = CreateSyncService();
        
        // Act
        var bytes = syncService.GetSessionBytesTransferred();
        
        // Assert
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void AddBytesTransferred_IncrementsCounter()
    {
        // Arrange
        using var syncService = CreateSyncService();
        
        // Act
        syncService.AddBytesTransferred(1024);
        syncService.AddBytesTransferred(2048);
        
        // Assert
        Assert.Equal(3072, syncService.GetSessionBytesTransferred());
    }

    [Fact]
    public void LastSyncTime_IsNull_Initially()
    {
        // Arrange
        using var syncService = CreateSyncService();
        
        // Act & Assert
        Assert.Null(syncService.LastSyncTime);
    }

    [Fact]
    public void ProcessIncomingManifest_RequestsMissingFiles()
    {
        // Arrange
        using var syncService = CreateSyncService();

        var remotePeer = new Peer { Id = "remote-peer", Name = "Remote" };
        var remoteManifest = new List<SyncedFile>
        {
            new SyncedFile
            {
                RelativePath = "remote_file.txt",
                ContentHash = "REMOTEHASH",
                FileSize = 100,
                LastModified = DateTime.UtcNow
            }
        };

        // Act
        syncService.ProcessIncomingManifest(remoteManifest, remotePeer);
        
        // Assert - Verify file was requested
        _mockTransfer.Verify(t => t.RequestSyncFile(
            It.IsAny<Peer>(),
            It.Is<string>(s => s == "remote_file.txt"),
            It.IsAny<CancellationToken>()),
            Times.Once);
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
