using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Swarm.Core.Security;

/// <summary>
/// Windows implementation of secure key storage using DPAPI (Data Protection API).
/// Keys are encrypted with CurrentUser scope - only the same Windows user can decrypt.
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsSecureKeyStorage : ISecureKeyStorage
{
    private readonly string _storageDirectory;
    private readonly ILogger<WindowsSecureKeyStorage> _logger;

    // Magic header to identify DPAPI-protected files
    private static readonly byte[] ProtectedFileHeader = "SWARM_DPAPI_V1"u8.ToArray();

    public WindowsSecureKeyStorage(string storageDirectory, ILogger<WindowsSecureKeyStorage> logger)
    {
        _storageDirectory = storageDirectory;
        _logger = logger;
        Directory.CreateDirectory(_storageDirectory);
    }

    public void StoreKey(string keyName, byte[] keyData)
    {
        try
        {
            // Encrypt the key data using DPAPI with CurrentUser scope
            var protectedData = ProtectedData.Protect(
                keyData, 
                optionalEntropy: null, 
                scope: DataProtectionScope.CurrentUser);

            // Write with header to identify protected files
            var filePath = GetKeyPath(keyName);
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            fs.Write(ProtectedFileHeader);
            fs.Write(protectedData);

            _logger.LogInformation("Stored key '{KeyName}' with DPAPI protection", keyName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store key '{KeyName}' with DPAPI", keyName);
            throw;
        }
    }

    public byte[]? RetrieveKey(string keyName)
    {
        var filePath = GetKeyPath(keyName);
        if (!File.Exists(filePath))
            return null;

        try
        {
            var fileData = File.ReadAllBytes(filePath);

            // Check if this is a DPAPI-protected file
            if (fileData.Length > ProtectedFileHeader.Length &&
                fileData.AsSpan(0, ProtectedFileHeader.Length).SequenceEqual(ProtectedFileHeader))
            {
                // Extract protected data (after header)
                var protectedData = fileData.AsSpan(ProtectedFileHeader.Length).ToArray();

                // Decrypt using DPAPI
                var keyData = ProtectedData.Unprotect(
                    protectedData,
                    optionalEntropy: null,
                    scope: DataProtectionScope.CurrentUser);

                _logger.LogInformation("Retrieved DPAPI-protected key '{KeyName}'", keyName);
                return keyData;
            }
            else
            {
                // Legacy unprotected key - return as-is for migration
                _logger.LogWarning("Key '{KeyName}' is not DPAPI-protected (legacy format)", keyName);
                return fileData;
            }
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "Failed to decrypt key '{KeyName}' - may be protected by different user", keyName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve key '{KeyName}'", keyName);
            return null;
        }
    }

    public bool KeyExists(string keyName)
    {
        return File.Exists(GetKeyPath(keyName));
    }

    public void DeleteKey(string keyName)
    {
        var filePath = GetKeyPath(keyName);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            _logger.LogInformation("Deleted key '{KeyName}'", keyName);
        }
    }

    /// <summary>
    /// Checks if a key is stored in legacy (unprotected) format.
    /// </summary>
    public bool IsLegacyFormat(string keyName)
    {
        var filePath = GetKeyPath(keyName);
        if (!File.Exists(filePath))
            return false;

        var fileData = File.ReadAllBytes(filePath);
        return fileData.Length <= ProtectedFileHeader.Length ||
               !fileData.AsSpan(0, ProtectedFileHeader.Length).SequenceEqual(ProtectedFileHeader);
    }

    private string GetKeyPath(string keyName)
    {
        // Sanitize key name for filesystem
        var safeName = string.Join("_", keyName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_storageDirectory, $"{safeName}.key");
    }
}
