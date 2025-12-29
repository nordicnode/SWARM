using Microsoft.Extensions.Logging.Abstractions;
using Swarm.Core.Services;
using Xunit;

namespace Swarm.Core.Tests;

/// <summary>
/// Unit tests for the CryptoService (encryption and key management).
/// </summary>
public class CryptoServiceTests
{
    [Fact]
    public void GetPublicKey_ReturnsNonEmptyBytes()
    {
        using var cryptoService = new CryptoService(NullLogger<CryptoService>.Instance);

        var publicKey = cryptoService.GetPublicKey();
        Assert.NotNull(publicKey);
        Assert.NotEmpty(publicKey);
        Assert.True(publicKey.Length >= 32);
    }

    [Fact]
    public void GetPublicKeyFingerprint_ReturnsValidHex()
    {
        using var crypto = new CryptoService(NullLogger<CryptoService>.Instance);

        var fingerprint = crypto.GetPublicKeyFingerprint();

        Assert.NotNull(fingerprint);
        Assert.NotEmpty(fingerprint);
        // SHA-256 fingerprint in hex is 64 characters
        Assert.Equal(64, fingerprint.Length);
        Assert.Matches("^[A-F0-9]+$", fingerprint);
    }

    [Fact]
    public void GetShortFingerprint_ReturnsColonSeparatedFormat()
    {
        using var crypto = new CryptoService(NullLogger<CryptoService>.Instance);

        var shortFingerprint = crypto.GetShortFingerprint();

        Assert.NotNull(shortFingerprint);
        Assert.Contains(":", shortFingerprint);
    }

    [Fact]
    public void Sign_AndVerify_RoundTrip()
    {
        using var crypto = new CryptoService(NullLogger<CryptoService>.Instance);

        var message = "Hello, this is a test message!";
        var signature = crypto.Sign(message);

        var isValid = crypto.Verify(message, signature, crypto.GetPublicKey());

        Assert.True(isValid);
    }

    [Fact]
    public void Verify_WithTamperedMessage_ReturnsFalse()
    {
        using var crypto = new CryptoService(NullLogger<CryptoService>.Instance);

        var message = "Original message";
        var signature = crypto.Sign(message);

        var isValid = crypto.Verify("Tampered message", signature, crypto.GetPublicKey());

        Assert.False(isValid);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip_ReturnsOriginalData()
    {
        // Generate ephemeral key for session
        var (publicKey1, privateKey1) = CryptoService.GenerateEphemeralKeyPair();
        var (publicKey2, privateKey2) = CryptoService.GenerateEphemeralKeyPair();
        
        // Both sides derive same session key
        var sessionKey1 = CryptoService.DeriveSessionKey(privateKey1, publicKey2);
        var sessionKey2 = CryptoService.DeriveSessionKey(privateKey2, publicKey1);
        
        Assert.Equal(sessionKey1, sessionKey2);

        // Original data
        var originalData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

        // Encrypt with one key
        var encrypted = CryptoService.Encrypt(originalData, sessionKey1);
        Assert.NotEqual(originalData, encrypted);

        // Decrypt with same key
        var decrypted = CryptoService.Decrypt(encrypted, sessionKey2);

        Assert.Equal(originalData, decrypted);
        
        privateKey1.Dispose();
        privateKey2.Dispose();
    }

    [Fact]
    public void Encrypt_ProducesDifferentOutputEachTime()
    {
        var (publicKey, privateKey) = CryptoService.GenerateEphemeralKeyPair();
        var sessionKey = CryptoService.DeriveSessionKey(privateKey, publicKey);

        var data = new byte[] { 1, 2, 3, 4, 5 };

        // Encrypt twice - should produce different ciphertext due to random nonce
        var encrypted1 = CryptoService.Encrypt(data, sessionKey);
        var encrypted2 = CryptoService.Encrypt(data, sessionKey);

        Assert.NotEqual(encrypted1, encrypted2);
        
        privateKey.Dispose();
    }

    [Fact]
    public void ComputeFingerprint_ReturnsSameForSameKey()
    {
        using var crypto = new CryptoService(NullLogger<CryptoService>.Instance);
        var publicKey = crypto.GetPublicKey();

        var fp1 = CryptoService.ComputeFingerprint(publicKey);
        var fp2 = CryptoService.ComputeFingerprint(publicKey);

        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void ComputeShortFingerprint_ReturnsColonSeparatedHex()
    {
        using var crypto = new CryptoService(NullLogger<CryptoService>.Instance);
        var publicKey = crypto.GetPublicKey();

        var shortFp = CryptoService.ComputeShortFingerprint(publicKey);

        Assert.NotNull(shortFp);
        Assert.Contains(":", shortFp);
        Assert.Matches("^[A-F0-9:]+$", shortFp);
    }
}
