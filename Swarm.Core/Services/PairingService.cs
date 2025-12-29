using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Swarm.Core.Models;

namespace Swarm.Core.Services;

/// <summary>
/// Service for generating and verifying pairing codes for secure peer trust establishment.
/// </summary>
public class PairingService
{
    private readonly CryptoService _cryptoService;
    private readonly ILogger<PairingService> _logger;

    public PairingService(CryptoService cryptoService, ILogger<PairingService> logger)
    {
        _cryptoService = cryptoService;
        _logger = logger;
    }

    /// <summary>
    /// Generates a 6-digit pairing code from this device's public key.
    /// The code is derived from a SHA256 hash of the public key, taking the last 6 numeric digits.
    /// </summary>
    public string GenerateMyPairingCode()
    {
        var publicKey = _cryptoService.GetPublicKey();
        return GenerateCodeFromPublicKey(publicKey);
    }

    /// <summary>
    /// Generates a 6-digit pairing code from a peer's public key.
    /// </summary>
    public string GeneratePeerPairingCode(Peer peer)
    {
        if (string.IsNullOrEmpty(peer.PublicKeyBase64))
            return "------";

        try
        {
            var publicKey = Convert.FromBase64String(peer.PublicKeyBase64);
            return GenerateCodeFromPublicKey(publicKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate pairing code for peer {PeerId}", peer.Id);
            return "------";
        }
    }

    /// <summary>
    /// Verifies if the entered code matches the peer's expected pairing code.
    /// </summary>
    public bool VerifyPairingCode(Peer peer, string enteredCode)
    {
        var expectedCode = GeneratePeerPairingCode(peer);
        return expectedCode == enteredCode;
    }

    /// <summary>
    /// Gets the full fingerprint of this device's public key (for advanced verification).
    /// </summary>
    public string GetMyFingerprint()
    {
        var publicKey = _cryptoService.GetPublicKey();
        return GenerateFingerprint(publicKey);
    }

    /// <summary>
    /// Gets the full fingerprint of a peer's public key.
    /// </summary>
    public string GetPeerFingerprint(Peer peer)
    {
        if (string.IsNullOrEmpty(peer.PublicKeyBase64))
            return "Unknown";

        try
        {
            var publicKey = Convert.FromBase64String(peer.PublicKeyBase64);
            return GenerateFingerprint(publicKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get fingerprint for peer {PeerId}", peer.Id);
            return "Invalid";
        }
    }

    private static string GenerateCodeFromPublicKey(byte[] publicKey)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(publicKey);
        
        // Convert hash to a number and take last 6 digits
        // Use first 8 bytes of hash to get a large number
        var hashNumber = BitConverter.ToUInt64(hash, 0);
        var code = (hashNumber % 1000000).ToString("D6");
        
        return code;
    }

    private static string GenerateFingerprint(byte[] publicKey)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(publicKey);
        
        // Return first 16 bytes as hex with colons (like SSH fingerprints)
        return string.Join(":", hash.Take(8).Select(b => b.ToString("X2")));
    }
}
