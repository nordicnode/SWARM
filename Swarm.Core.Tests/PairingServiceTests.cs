using Swarm.Core.Models;
using Swarm.Core.Services;

namespace Swarm.Core.Tests;

/// <summary>
/// Unit tests for the PairingService.
/// </summary>
public class PairingServiceTests
{
    [Fact]
    public void GenerateMyPairingCode_ReturnsSixDigits()
    {
        // Arrange
        using var cryptoService = new CryptoService();
        var pairingService = new PairingService(cryptoService);

        // Act
        var code = pairingService.GenerateMyPairingCode();

        // Assert
        Assert.NotNull(code);
        Assert.Equal(6, code.Length);
        Assert.Matches("^[0-9]{6}$", code); // Six digits
    }

    [Fact]
    public void GenerateMyPairingCode_IsConsistent()
    {
        // Same crypto service should generate same code
        using var cryptoService = new CryptoService();
        var pairingService = new PairingService(cryptoService);

        var code1 = pairingService.GenerateMyPairingCode();
        var code2 = pairingService.GenerateMyPairingCode();

        Assert.Equal(code1, code2);
    }

    [Fact]
    public void GetMyFingerprint_ReturnsValidFormat()
    {
        using var cryptoService = new CryptoService();
        var pairingService = new PairingService(cryptoService);

        var fingerprint = pairingService.GetMyFingerprint();

        Assert.NotNull(fingerprint);
        Assert.NotEmpty(fingerprint);
        // Should contain colon-separated hex values (8 bytes = 7 colons)
        Assert.Contains(":", fingerprint);
        Assert.Matches("^[A-F0-9:]+$", fingerprint);
    }

    [Fact]
    public void GeneratePeerPairingCode_WithValidPeer_ReturnsCode()
    {
        using var cryptoService = new CryptoService();
        var pairingService = new PairingService(cryptoService);

        // Create a peer with our own public key (for testing)
        var peer = new Peer
        {
            Id = "test-peer",
            Name = "Test Peer",
            PublicKeyBase64 = Convert.ToBase64String(cryptoService.GetPublicKey())
        };

        var code = pairingService.GeneratePeerPairingCode(peer);

        Assert.NotNull(code);
        Assert.Equal(6, code.Length);
        Assert.Matches("^[0-9]{6}$", code);
    }

    [Fact]
    public void GeneratePeerPairingCode_MatchesMyCode_WhenSameKey()
    {
        using var cryptoService = new CryptoService();
        var pairingService = new PairingService(cryptoService);

        var myCode = pairingService.GenerateMyPairingCode();

        // Create peer with same public key
        var peer = new Peer
        {
            Id = "self",
            Name = "Self",
            PublicKeyBase64 = Convert.ToBase64String(cryptoService.GetPublicKey())
        };

        var peerCode = pairingService.GeneratePeerPairingCode(peer);

        Assert.Equal(myCode, peerCode);
    }

    [Fact]
    public void GeneratePeerPairingCode_WithNullPublicKey_ReturnsPlaceholder()
    {
        using var cryptoService = new CryptoService();
        var pairingService = new PairingService(cryptoService);

        var peer = new Peer
        {
            Id = "test-peer",
            Name = "Test",
            PublicKeyBase64 = null
        };

        var code = pairingService.GeneratePeerPairingCode(peer);

        Assert.Equal("------", code);
    }

    [Fact]
    public void VerifyPairingCode_WithMatchingCode_ReturnsTrue()
    {
        using var cryptoService = new CryptoService();
        var pairingService = new PairingService(cryptoService);

        // Create peer with known public key
        var peer = new Peer
        {
            Id = "peer1",
            Name = "Peer 1",
            PublicKeyBase64 = Convert.ToBase64String(cryptoService.GetPublicKey())
        };

        var expectedCode = pairingService.GeneratePeerPairingCode(peer);

        var result = pairingService.VerifyPairingCode(peer, expectedCode);

        Assert.True(result);
    }

    [Fact]
    public void VerifyPairingCode_WithWrongCode_ReturnsFalse()
    {
        using var cryptoService = new CryptoService();
        var pairingService = new PairingService(cryptoService);

        var peer = new Peer
        {
            Id = "peer1",
            Name = "Peer 1",
            PublicKeyBase64 = Convert.ToBase64String(cryptoService.GetPublicKey())
        };

        var result = pairingService.VerifyPairingCode(peer, "000000");

        Assert.False(result);
    }
}
