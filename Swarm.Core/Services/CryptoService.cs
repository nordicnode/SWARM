using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Swarm.Core.Security;

namespace Swarm.Core.Services;

/// <summary>
/// Cryptographic service for secure peer communication.
/// Handles ECDSA key management, digital signatures, and AES-256-GCM encryption.
/// </summary>
public class CryptoService : IDisposable
{
    private const string PortableMarkerFile = "portable.marker";
    private const string IdentityKeyName = "identity";
    
    private readonly ILogger<CryptoService> _logger;
    private readonly ISecureKeyStorage? _secureStorage;
    
    public CryptoService(ILogger<CryptoService> logger, ISecureKeyStorage? secureStorage = null)
    {
        _logger = logger;
        _secureStorage = secureStorage;
    }

    /// <summary>
    /// Gets the keys directory, respecting portable mode.
    /// In portable mode, keys are stored next to the executable.
    /// Otherwise, they're in AppData/Roaming/Swarm/keys.
    /// </summary>
    public static string GetKeysDirectory()
    {
        var exeDir = GetExecutableDirectory();
        var isPortable = File.Exists(Path.Combine(exeDir, PortableMarkerFile));
        
        if (isPortable)
        {
            return Path.Combine(exeDir, "keys");
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Swarm", "keys");
    }
    
    private static string GetExecutableDirectory()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            return Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory;
        }
        return AppContext.BaseDirectory;
    }
    
    private static readonly string KeyDirectory = GetKeysDirectory();
    private static readonly string LegacyIdentityKeyPath = Path.Combine(KeyDirectory, "identity.key");

    private ECDsa? _identityKey;
    private readonly object _keyLock = new();

    /// <summary>
    /// Gets or creates the persistent ECDSA identity key for this device.
    /// Uses platform-specific secure storage (DPAPI on Windows, Keychain on macOS).
    /// </summary>
    public ECDsa GetOrCreateIdentityKey()
    {
        lock (_keyLock)
        {
            if (_identityKey != null) return _identityKey;

            Directory.CreateDirectory(KeyDirectory);

            // Try to load from secure storage first
            if (_secureStorage != null)
            {
                var keyData = _secureStorage.RetrieveKey(IdentityKeyName);
                if (keyData != null)
                {
                    try
                    {
                        _identityKey = ECDsa.Create();
                        _identityKey.ImportECPrivateKey(keyData, out _);
                        _logger.LogInformation("Loaded identity key from secure storage");
                        
                        // Check for and migrate legacy unprotected key
                        MigrateLegacyKeyIfNeeded();
                        
                        return _identityKey;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to load identity key from secure storage");
                        _identityKey = null;
                    }
                }
                
                // Check for legacy unprotected key to migrate
                if (File.Exists(LegacyIdentityKeyPath))
                {
                    try
                    {
                        var legacyKeyData = File.ReadAllBytes(LegacyIdentityKeyPath);
                        _identityKey = ECDsa.Create();
                        _identityKey.ImportECPrivateKey(legacyKeyData, out _);
                        
                        // Migrate to secure storage
                        _secureStorage.StoreKey(IdentityKeyName, legacyKeyData);
                        
                        // Remove legacy file after successful migration
                        File.Delete(LegacyIdentityKeyPath);
                        _logger.LogInformation("Migrated legacy identity key to secure storage");
                        
                        return _identityKey;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to migrate legacy identity key");
                        _identityKey = null;
                    }
                }
            }
            else
            {
                // Fallback: no secure storage available, use legacy file-based storage
                if (File.Exists(LegacyIdentityKeyPath))
                {
                    try
                    {
                        var keyData = File.ReadAllBytes(LegacyIdentityKeyPath);
                        _identityKey = ECDsa.Create();
                        _identityKey.ImportECPrivateKey(keyData, out _);
                        _logger.LogWarning("Loaded identity key from unprotected file (no secure storage available)");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to load identity key: {Message}", ex.Message);
                        _identityKey = null;
                    }
                }
            }

            // Generate new key if none exists
            if (_identityKey == null)
            {
                _identityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var keyData = _identityKey.ExportECPrivateKey();
                
                if (_secureStorage != null)
                {
                    _secureStorage.StoreKey(IdentityKeyName, keyData);
                    _logger.LogInformation("Generated new identity key (stored in secure storage)");
                }
                else
                {
                    File.WriteAllBytes(LegacyIdentityKeyPath, keyData);
                    _logger.LogWarning("Generated new identity key (stored unprotected - no secure storage)");
                }
            }

            return _identityKey;
        }
    }
    
    /// <summary>
    /// Removes legacy unprotected key file if secure storage is in use.
    /// </summary>
    private void MigrateLegacyKeyIfNeeded()
    {
        if (File.Exists(LegacyIdentityKeyPath) && _secureStorage != null)
        {
            try
            {
                File.Delete(LegacyIdentityKeyPath);
                _logger.LogInformation("Removed legacy unprotected key file after migration");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remove legacy key file");
            }
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
    public bool Verify(byte[] data, byte[] signature, byte[] publicKey)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Signature verification failed: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Verifies a signature for a string message.
    /// </summary>
    public bool Verify(string message, byte[] signature, byte[] publicKey)
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

