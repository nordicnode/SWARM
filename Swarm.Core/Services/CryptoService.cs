using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Swarm.Core.Services;

/// <summary>
/// Cryptographic service for secure peer communication.
/// Handles ECDSA key management, digital signatures, and AES-256-GCM encryption.
/// </summary>
public class CryptoService : IDisposable
{
    private static readonly string KeyDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Swarm", "keys");

    private static readonly string IdentityKeyPath = Path.Combine(KeyDirectory, "identity.key");

    private ECDsa? _identityKey;
    private readonly object _keyLock = new();

    /// <summary>
    /// Gets or creates the persistent ECDSA identity key for this device.
    /// </summary>
    public ECDsa GetOrCreateIdentityKey()
    {
        lock (_keyLock)
        {
            if (_identityKey != null) return _identityKey;

            Directory.CreateDirectory(KeyDirectory);

            if (File.Exists(IdentityKeyPath))
            {
                try
                {
                    var keyData = File.ReadAllBytes(IdentityKeyPath);
                    _identityKey = ECDsa.Create();
                    _identityKey.ImportECPrivateKey(keyData, out _);
                    System.Diagnostics.Debug.WriteLine("Loaded existing identity key");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load identity key: {ex.Message}");
                    _identityKey = null;
                }
            }

            if (_identityKey == null)
            {
                _identityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var keyData = _identityKey.ExportECPrivateKey();
                File.WriteAllBytes(IdentityKeyPath, keyData);
                System.Diagnostics.Debug.WriteLine("Generated new identity key");
            }

            return _identityKey;
        }
    }

    /// <summary>
    /// Gets the public key bytes for the identity key.
    /// </summary>
    public byte[] GetPublicKey()
    {
        var key = GetOrCreateIdentityKey();
        return key.ExportSubjectPublicKeyInfo();
    }

    /// <summary>
    /// Gets a human-readable fingerprint of the public key (SHA-256 hash, hex encoded).
    /// </summary>
    public string GetPublicKeyFingerprint()
    {
        var publicKey = GetPublicKey();
        var hash = SHA256.HashData(publicKey);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Gets a shortened fingerprint for display (first 16 chars with colons).
    /// </summary>
    public string GetShortFingerprint()
    {
        var full = GetPublicKeyFingerprint();
        // Format as XX:XX:XX:XX:XX:XX:XX:XX
        var sb = new StringBuilder();
        for (int i = 0; i < Math.Min(16, full.Length); i += 2)
        {
            if (sb.Length > 0) sb.Append(':');
            sb.Append(full.AsSpan(i, 2));
        }
        return sb.ToString();
    }

    #region Digital Signatures

    /// <summary>
    /// Signs data using the identity key.
    /// </summary>
    public byte[] Sign(byte[] data)
    {
        var key = GetOrCreateIdentityKey();
        return key.SignData(data, HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// Signs a string message using the identity key.
    /// </summary>
    public byte[] Sign(string message)
    {
        return Sign(Encoding.UTF8.GetBytes(message));
    }

    /// <summary>
    /// Verifies a signature against the provided public key.
    /// </summary>
    public static bool Verify(byte[] data, byte[] signature, byte[] publicKey)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Signature verification failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Verifies a signature for a string message.
    /// </summary>
    public static bool Verify(string message, byte[] signature, byte[] publicKey)
    {
        return Verify(Encoding.UTF8.GetBytes(message), signature, publicKey);
    }

    #endregion

    #region AES-256-GCM Encryption

    /// <summary>
    /// Encrypts data using AES-256-GCM.
    /// Returns (nonce || ciphertext || tag).
    /// </summary>
    public static byte[] Encrypt(byte[] plaintext, byte[] sessionKey)
    {
        if (sessionKey.Length != 32)
            throw new ArgumentException("Session key must be 32 bytes for AES-256", nameof(sessionKey));

        var nonce = new byte[ProtocolConstants.NONCE_SIZE];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[ProtocolConstants.TAG_SIZE];

        using var aes = new AesGcm(sessionKey, ProtocolConstants.TAG_SIZE);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Format: nonce (12) || ciphertext (N) || tag (16)
        var result = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, result, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length + ciphertext.Length, tag.Length);

        return result;
    }

    /// <summary>
    /// Decrypts data using AES-256-GCM.
    /// Input format: (nonce || ciphertext || tag).
    /// </summary>
    public static byte[] Decrypt(byte[] encryptedData, byte[] sessionKey)
    {
        if (sessionKey.Length != 32)
            throw new ArgumentException("Session key must be 32 bytes for AES-256", nameof(sessionKey));

        if (encryptedData.Length < ProtocolConstants.NONCE_SIZE + ProtocolConstants.TAG_SIZE)
            throw new ArgumentException("Encrypted data too short", nameof(encryptedData));

        var nonce = new byte[ProtocolConstants.NONCE_SIZE];
        var ciphertextLength = encryptedData.Length - ProtocolConstants.NONCE_SIZE - ProtocolConstants.TAG_SIZE;
        var ciphertext = new byte[ciphertextLength];
        var tag = new byte[ProtocolConstants.TAG_SIZE];

        Buffer.BlockCopy(encryptedData, 0, nonce, 0, ProtocolConstants.NONCE_SIZE);
        Buffer.BlockCopy(encryptedData, ProtocolConstants.NONCE_SIZE, ciphertext, 0, ciphertextLength);
        Buffer.BlockCopy(encryptedData, ProtocolConstants.NONCE_SIZE + ciphertextLength, tag, 0, ProtocolConstants.TAG_SIZE);

        var plaintext = new byte[ciphertextLength];

        using var aes = new AesGcm(sessionKey, ProtocolConstants.TAG_SIZE);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }

    #endregion

    #region ECDH Key Exchange

    /// <summary>
    /// Generates an ephemeral ECDH key pair for session key derivation.
    /// Returns (publicKey, privateKey).
    /// </summary>
    public static (byte[] PublicKey, ECDiffieHellman PrivateKey) GenerateEphemeralKeyPair()
    {
        var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        return (publicKey, ecdh);
    }

    /// <summary>
    /// Derives a 32-byte session key from ECDH key agreement.
    /// </summary>
    public static byte[] DeriveSessionKey(ECDiffieHellman localPrivateKey, byte[] remotePublicKey)
    {
        using var remoteKey = ECDiffieHellman.Create();
        remoteKey.ImportSubjectPublicKeyInfo(remotePublicKey, out _);

        // Use HKDF to derive a 32-byte key from the shared secret
        var sharedSecret = localPrivateKey.DeriveKeyMaterial(remoteKey.PublicKey);

        // HKDF-Expand with "SWARM-SESSION" as info
        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            sharedSecret,
            ProtocolConstants.SESSION_KEY_SIZE,
            info: Encoding.UTF8.GetBytes("SWARM-SESSION"));
    }

    /// <summary>
    /// Computes a fingerprint for a public key.
    /// </summary>
    public static string ComputeFingerprint(byte[] publicKey)
    {
        var hash = SHA256.HashData(publicKey);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Gets a shortened fingerprint for display.
    /// </summary>
    public static string ComputeShortFingerprint(byte[] publicKey)
    {
        var full = ComputeFingerprint(publicKey);
        var sb = new StringBuilder();
        for (int i = 0; i < Math.Min(16, full.Length); i += 2)
        {
            if (sb.Length > 0) sb.Append(':');
            sb.Append(full.AsSpan(i, 2));
        }
        return sb.ToString();
    }

    #endregion

    public void Dispose()
    {
        lock (_keyLock)
        {
            _identityKey?.Dispose();
            _identityKey = null;
        }
    }
}

