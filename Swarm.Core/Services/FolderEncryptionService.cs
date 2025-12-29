using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Swarm.Core.Models;

namespace Swarm.Core.Services;

/// <summary>
/// Service for managing encrypted folders with zero-knowledge sync support.
/// Uses 32KB chunked AES-256-GCM encryption for delta sync compatibility.
/// </summary>
public class FolderEncryptionService : IDisposable
{
    private const string VaultDirName = ".swarm-vault";
    private const string ConfigFileName = "config.json";
    private const string ManifestFileName = "manifest.senc";
    private const string EncryptedExtension = ".senc";
    private const int ChunkSize = 32 * 1024; // 32KB chunks for delta sync
    private const int Pbkdf2Iterations = 100_000;
    private const int KeySize = 32; // AES-256
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string VerifierPlaintext = "SWARM-VAULT-VERIFY-2024";

    private readonly Settings _settings;
    private readonly ILogger<FolderEncryptionService> _logger;
    private readonly string _syncRoot;
    private readonly System.Timers.Timer _autoLockTimer;
    private readonly object _lockObj = new();

    /// <summary>
    /// Event raised when a folder is auto-locked due to inactivity.
    /// </summary>
    public event Action<string>? FolderAutoLocked;

    public FolderEncryptionService(Settings settings, ILogger<FolderEncryptionService> logger)
    {
        _settings = settings;
        _logger = logger;
        _syncRoot = settings.SyncFolderPath;

        // Auto-lock timer: check every 60 seconds
        _autoLockTimer = new System.Timers.Timer(60_000);
        _autoLockTimer.Elapsed += CheckAutoLock;
        _autoLockTimer.AutoReset = true;
        _autoLockTimer.Start();
    }

    #region Folder Management

    /// <summary>
    /// Creates a new encrypted folder with the given password.
    /// </summary>
    public bool CreateEncryptedFolder(string relativePath, string password)
    {
        try
        {
            var fullPath = Path.Combine(_syncRoot, relativePath);
            var vaultDir = Path.Combine(fullPath, VaultDirName);

            // Create directories
            Directory.CreateDirectory(fullPath);
            Directory.CreateDirectory(vaultDir);

            // Generate salt
            var salt = new byte[SaltSize];
            RandomNumberGenerator.Fill(salt);

            // Derive key
            var key = DeriveKey(password, salt);

            // Create verifier (encrypted known value)
            var verifierBytes = Encoding.UTF8.GetBytes(VerifierPlaintext);
            var encryptedVerifier = EncryptChunk(verifierBytes, key);

            // Save config
            var config = new VaultConfig
            {
                Version = 1,
                Salt = Convert.ToBase64String(salt),
                Verifier = Convert.ToBase64String(encryptedVerifier)
            };

            var configPath = Path.Combine(vaultDir, ConfigFileName);
            File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

            // Create empty manifest
            var manifest = new EncryptedFileManifest();
            SaveManifest(vaultDir, manifest, key);

            // Add to settings
            var encryptedFolder = new EncryptedFolder
            {
                FolderPath = relativePath,
                Salt = config.Salt,
                Verifier = config.Verifier,
                IsLocked = false,
                CachedKey = key,
                LastAccessed = DateTime.Now
            };

            lock (_lockObj)
            {
                _settings.EncryptedFolders.Add(encryptedFolder);
            }
            _settings.Save();

            _logger.LogInformation("Created encrypted folder: {Path}", relativePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create encrypted folder: {Path}", relativePath);
            return false;
        }
    }

    /// <summary>
    /// Unlocks an encrypted folder with the given password.
    /// </summary>
    public bool UnlockFolder(string relativePath, string password)
    {
        try
        {
            EncryptedFolder? folder;
            lock (_lockObj)
            {
                folder = _settings.EncryptedFolders.FirstOrDefault(f => f.FolderPath == relativePath);
            }

            if (folder == null)
                return false;

            var salt = Convert.FromBase64String(folder.Salt);
            var key = DeriveKey(password, salt);

            // Verify password
            var encryptedVerifier = Convert.FromBase64String(folder.Verifier);
            try
            {
                var decrypted = DecryptChunk(encryptedVerifier, key);
                var verifierText = Encoding.UTF8.GetString(decrypted);
                if (verifierText != VerifierPlaintext)
                    return false;
            }
            catch
            {
                return false; // Decryption failed = wrong password
            }

            // Unlock
            folder.IsLocked = false;
            folder.CachedKey = key;
            folder.LastAccessed = DateTime.Now;

            _logger.LogInformation("Unlocked encrypted folder: {Path}", relativePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unlock folder: {Path}", relativePath);
            return false;
        }
    }

    /// <summary>
    /// Locks an encrypted folder, clearing the cached key.
    /// </summary>
    public void LockFolder(string relativePath)
    {
        lock (_lockObj)
        {
            var folder = _settings.EncryptedFolders.FirstOrDefault(f => f.FolderPath == relativePath);
            if (folder != null)
            {
                if (folder.CachedKey != null)
                {
                    Array.Clear(folder.CachedKey, 0, folder.CachedKey.Length);
                    folder.CachedKey = null;
                }
                folder.IsLocked = true;
                _logger.LogInformation("Locked encrypted folder: {Path}", relativePath);
            }
        }
    }

    /// <summary>
    /// Locks all encrypted folders (call on app close).
    /// </summary>
    public void LockAllFolders()
    {
        lock (_lockObj)
        {
            foreach (var folder in _settings.EncryptedFolders)
            {
                if (folder.CachedKey != null)
                {
                    Array.Clear(folder.CachedKey, 0, folder.CachedKey.Length);
                    folder.CachedKey = null;
                }
                folder.IsLocked = true;
            }
        }
        _logger.LogInformation("Locked all encrypted folders");
    }

    /// <summary>
    /// Checks if a path is within an encrypted folder.
    /// </summary>
    public EncryptedFolder? GetEncryptedFolderFor(string relativePath)
    {
        lock (_lockObj)
        {
            return _settings.EncryptedFolders
                .FirstOrDefault(f => relativePath.StartsWith(f.FolderPath, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Checks if a directory is an encrypted folder.
    /// </summary>
    public bool IsEncryptedFolder(string relativePath)
    {
        var fullPath = Path.Combine(_syncRoot, relativePath);
        var configPath = Path.Combine(fullPath, VaultDirName, ConfigFileName);
        return File.Exists(configPath);
    }

    /// <summary>
    /// Gets all unlocked folders.
    /// </summary>
    public IReadOnlyList<EncryptedFolder> GetUnlockedFolders()
    {
        lock (_lockObj)
        {
            return _settings.EncryptedFolders.Where(f => !f.IsLocked).ToList();
        }
    }

    #endregion

    #region File Encryption

    /// <summary>
    /// Encrypts a file and stores it with an obfuscated name.
    /// Returns the obfuscated filename.
    /// </summary>
    public string? EncryptFile(string relativePath, EncryptedFolder folder)
    {
        if (folder.IsLocked || folder.CachedKey == null)
            return null;

        try
        {
            var fullPath = Path.Combine(_syncRoot, relativePath);
            var folderFullPath = Path.Combine(_syncRoot, folder.FolderPath);
            var vaultDir = Path.Combine(folderFullPath, VaultDirName);

            // Generate obfuscated name
            var obfuscatedName = Guid.NewGuid().ToString("N")[..12] + EncryptedExtension;
            var encryptedPath = Path.Combine(folderFullPath, obfuscatedName);

            // Read and encrypt file in chunks
            using var inputStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read);
            using var outputStream = new FileStream(encryptedPath, FileMode.Create, FileAccess.Write);

            // Write header: magic + version + chunk size
            outputStream.Write(Encoding.ASCII.GetBytes("SENC"));
            outputStream.Write(BitConverter.GetBytes((ushort)1)); // version
            outputStream.Write(BitConverter.GetBytes((ushort)(ChunkSize / 1024))); // chunk size in KB

            var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
            try
            {
                int bytesRead;
                while ((bytesRead = inputStream.Read(buffer, 0, ChunkSize)) > 0)
                {
                    var chunk = buffer.AsSpan(0, bytesRead).ToArray();
                    var encrypted = EncryptChunk(chunk, folder.CachedKey);
                    
                    // Write chunk length + encrypted data
                    outputStream.Write(BitConverter.GetBytes(encrypted.Length));
                    outputStream.Write(encrypted);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            // Update manifest
            var manifest = LoadManifest(vaultDir, folder.CachedKey);
            var originalName = Path.GetFileName(relativePath);
            manifest.FileMap[obfuscatedName] = originalName;
            SaveManifest(vaultDir, manifest, folder.CachedKey);

            // Delete original file
            File.Delete(fullPath);

            folder.LastAccessed = DateTime.Now;
            _logger.LogDebug("Encrypted file: {Original} -> {Obfuscated}", originalName, obfuscatedName);
            return obfuscatedName;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encrypt file: {Path}", relativePath);
            return null;
        }
    }

    /// <summary>
    /// Decrypts a file to a stream for reading.
    /// </summary>
    public Stream? DecryptFileToStream(string obfuscatedPath, EncryptedFolder folder)
    {
        if (folder.IsLocked || folder.CachedKey == null)
            return null;

        try
        {
            var fullPath = Path.Combine(_syncRoot, folder.FolderPath, obfuscatedPath);
            
            using var inputStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read);
            var outputStream = new MemoryStream();

            // Read and verify header
            var magic = new byte[4];
            inputStream.ReadExactly(magic);
            if (Encoding.ASCII.GetString(magic) != "SENC")
                throw new InvalidDataException("Invalid encrypted file format");

            var versionBytes = new byte[2];
            inputStream.ReadExactly(versionBytes);
            // var version = BitConverter.ToUInt16(versionBytes);

            var chunkSizeBytes = new byte[2];
            inputStream.ReadExactly(chunkSizeBytes);
            // var chunkSizeKB = BitConverter.ToUInt16(chunkSizeBytes);

            // Read and decrypt chunks
            var lengthBuffer = new byte[4];
            int lengthRead;
            while ((lengthRead = inputStream.Read(lengthBuffer, 0, 4)) == 4)
            {
                var encryptedLength = BitConverter.ToInt32(lengthBuffer);
                if (encryptedLength <= 0 || encryptedLength > ChunkSize + NonceSize + TagSize + 1024)
                    break;

                var encryptedChunk = new byte[encryptedLength];
                var totalChunkRead = 0;
                while (totalChunkRead < encryptedLength)
                {
                    var read = inputStream.Read(encryptedChunk, totalChunkRead, encryptedLength - totalChunkRead);
                    if (read == 0) break;
                    totalChunkRead += read;
                }
                if (totalChunkRead != encryptedLength)
                    break;

                var decrypted = DecryptChunk(encryptedChunk, folder.CachedKey);
                outputStream.Write(decrypted);
            }

            outputStream.Position = 0;
            folder.LastAccessed = DateTime.Now;
            return outputStream;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt file: {Path}", obfuscatedPath);
            return null;
        }
    }

    /// <summary>
    /// Gets the real filename for an obfuscated file.
    /// </summary>
    public string? GetRealFileName(string obfuscatedName, EncryptedFolder folder)
    {
        if (folder.IsLocked || folder.CachedKey == null)
            return null;

        try
        {
            var vaultDir = Path.Combine(_syncRoot, folder.FolderPath, VaultDirName);
            var manifest = LoadManifest(vaultDir, folder.CachedKey);
            return manifest.FileMap.TryGetValue(obfuscatedName, out var realName) ? realName : null;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Manifest Management

    private EncryptedFileManifest LoadManifest(string vaultDir, byte[] key)
    {
        var manifestPath = Path.Combine(vaultDir, ManifestFileName);
        if (!File.Exists(manifestPath))
            return new EncryptedFileManifest();

        var encrypted = File.ReadAllBytes(manifestPath);
        var decrypted = DecryptChunk(encrypted, key);
        var json = Encoding.UTF8.GetString(decrypted);
        return JsonSerializer.Deserialize<EncryptedFileManifest>(json) ?? new EncryptedFileManifest();
    }

    private void SaveManifest(string vaultDir, EncryptedFileManifest manifest, byte[] key)
    {
        var json = JsonSerializer.Serialize(manifest);
        var plaintext = Encoding.UTF8.GetBytes(json);
        var encrypted = EncryptChunk(plaintext, key);
        
        var manifestPath = Path.Combine(vaultDir, ManifestFileName);
        File.WriteAllBytes(manifestPath, encrypted);
    }

    #endregion

    #region Cryptographic Operations

    private static byte[] DeriveKey(string password, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(
            password, 
            salt, 
            Pbkdf2Iterations, 
            HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(KeySize);
    }

    private static byte[] EncryptChunk(byte[] plaintext, byte[] key)
    {
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Format: nonce || ciphertext || tag
        var result = new byte[NonceSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, NonceSize + ciphertext.Length, TagSize);

        return result;
    }

    private static byte[] DecryptChunk(byte[] encrypted, byte[] key)
    {
        if (encrypted.Length < NonceSize + TagSize)
            throw new ArgumentException("Encrypted data too short");

        var nonce = encrypted.AsSpan(0, NonceSize).ToArray();
        var ciphertextLength = encrypted.Length - NonceSize - TagSize;
        var ciphertext = encrypted.AsSpan(NonceSize, ciphertextLength).ToArray();
        var tag = encrypted.AsSpan(NonceSize + ciphertextLength, TagSize).ToArray();

        var plaintext = new byte[ciphertextLength];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }

    #endregion

    #region Auto-Lock

    private void CheckAutoLock(object? sender, System.Timers.ElapsedEventArgs e)
    {
        var autoLockMinutes = _settings.EncryptionAutoLockMinutes;
        if (autoLockMinutes <= 0) return;

        var threshold = DateTime.Now.AddMinutes(-autoLockMinutes);

        lock (_lockObj)
        {
            foreach (var folder in _settings.EncryptedFolders.Where(f => !f.IsLocked))
            {
                if (folder.LastAccessed < threshold)
                {
                    if (folder.CachedKey != null)
                    {
                        Array.Clear(folder.CachedKey, 0, folder.CachedKey.Length);
                        folder.CachedKey = null;
                    }
                    folder.IsLocked = true;
                    
                    _logger.LogInformation("Auto-locked folder due to inactivity: {Path}", folder.FolderPath);
                    FolderAutoLocked?.Invoke(folder.FolderPath);
                }
            }
        }
    }

    #endregion

    public void Dispose()
    {
        _autoLockTimer.Stop();
        _autoLockTimer.Dispose();
        LockAllFolders();
    }
}
