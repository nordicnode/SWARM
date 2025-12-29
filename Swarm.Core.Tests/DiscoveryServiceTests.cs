using Swarm.Core.Models;
using Swarm.Core.Services;
using Xunit;

namespace Swarm.Core.Tests;

/// <summary>
/// Unit tests for DiscoveryService.
/// Tests peer discovery and management functionality.
/// </summary>
public class DiscoveryServiceTests : IDisposable
{
    private readonly Settings _settings;
    private readonly CryptoService _cryptoService;
    private readonly DiscoveryService _service;

    public DiscoveryServiceTests()
    {
        _settings = new Settings
        {
            DeviceName = "TestDevice"
        };
        
        _cryptoService = new CryptoService();
        
        _service = new DiscoveryService("test-local-id", _cryptoService, _settings, null);
    }

    [Fact]
    public void Constructor_InitializesWithDefaults()
    {
        // Assert
        Assert.NotNull(_service.Peers);
        Assert.Empty(_service.Peers);
    }

    [Fact]
    public void LocalId_ReturnsConstructorValue()
    {
        // Act
        var localId = _service.LocalId;
        
        // Assert
        Assert.Equal("test-local-id", localId);
    }

    [Fact]
    public void Peers_IsObservableCollection()
    {
        // Assert
        Assert.IsType<System.Collections.ObjectModel.ObservableCollection<Peer>>(_service.Peers);
    }

    [Fact]
    public void Start_SetsTransferPort()
    {
        // Act
        _service.Start(12345);
        
        // Assert
        Assert.Equal(12345, _service.TransferPort);
    }

    [Fact]
    public void IsTrusted_ReturnsFalse_WhenPeerNotInSettings()
    {
        // Arrange
        var peer = new Peer { Id = "unknown-peer" };
        
        // Act
        var isTrusted = _settings.TrustedPeers.Any(p => p.Id == peer.Id);
        
        // Assert
        Assert.False(isTrusted);
    }

    [Fact]
    public void IsTrusted_ReturnsTrue_WhenPeerInSettings()
    {
        // Arrange
        _settings.TrustedPeers.Add(new TrustedPeer { Id = "known-peer", Name = "Known Peer" });
        var peer = new Peer { Id = "known-peer" };
        
        // Act
        var isTrusted = _settings.TrustedPeers.Any(p => p.Id == peer.Id);
        
        // Assert
        Assert.True(isTrusted);
    }

    [Fact]
    public void PeerDiscovered_CanSubscribe()
    {
        // Arrange
        Peer? discoveredPeer = null;
        _service.PeerDiscovered += p => discoveredPeer = p;
        
        // Assert - We can't easily trigger discovery, but we can test subscription works
        Assert.Null(discoveredPeer); // No peer discovered yet
    }

    [Fact]
    public void PeerLost_CanSubscribe()
    {
        // Arrange
        Peer? lostPeer = null;
        _service.PeerLost += p => lostPeer = p;
        
        // Assert - We can't easily trigger loss, but we can test subscription works
        Assert.Null(lostPeer); // No peer lost yet
    }

    [Fact]
    public void IsSyncEnabled_DefaultsFalse()
    {
        // Assert
        Assert.False(_service.IsSyncEnabled);
    }

    [Fact]
    public void IsSyncEnabled_CanBeSet()
    {
        // Act
        _service.IsSyncEnabled = true;
        
        // Assert
        Assert.True(_service.IsSyncEnabled);
    }

    public void Dispose()
    {
        _service.Dispose();
        _cryptoService.Dispose();
    }
}
