using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;

namespace Swarm.Core.Security;

/// <summary>
/// Linux implementation of secure key storage using file permissions.
/// Uses native .NET APIs to set owner-only permissions (600).
/// </summary>
[SupportedOSPlatform("linux")]
public class LinuxSecureKeyStorage : ISecureKeyStorage
{
    private readonly string _storageDirectory;
    private readonly ILogger<LinuxSecureKeyStorage> _logger;

    public LinuxSecureKeyStorage(string storageDirectory, ILogger<LinuxSecureKeyStorage> logger)
    {
        _storageDirectory = storageDirectory;
        _logger = logger;
        
        // Create directory with restricted permissions
        Directory.CreateDirectory(_storageDirectory);
        SetDirectoryPermissions(_storageDirectory);
    }

    public void StoreKey(string keyName, byte[] keyData)
    {
        ValidateKeyName(keyName);
        
        try
        {
            var filePath = GetKeyPath(keyName);
            File.WriteAllBytes(filePath, keyData);
            
            // Set file permissions to owner-only (600) using native API
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            
            _logger.LogInformation("Stored key '{KeyName}' with owner-only permissions", keyName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store key '{KeyName}'", keyName);
            throw;
        }
    }

    public byte[]? RetrieveKey(string keyName)
    {
        ValidateKeyName(keyName);
        
        var filePath = GetKeyPath(keyName);
        if (!File.Exists(filePath))
            return null;

        try
        {
            var keyData = File.ReadAllBytes(filePath);
            _logger.LogInformation("Retrieved key '{KeyName}'", keyName);
            return keyData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve key '{KeyName}'", keyName);
            return null;
        }
    }

    public bool KeyExists(string keyName)
    {
        ValidateKeyName(keyName);
        return File.Exists(GetKeyPath(keyName));
    }

    public void DeleteKey(string keyName)
    {
        ValidateKeyName(keyName);
        
        var filePath = GetKeyPath(keyName);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            _logger.LogInformation("Deleted key '{KeyName}'", keyName);
        }
    }

    private string GetKeyPath(string keyName)
    {
        var safeName = string.Join("_", keyName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_storageDirectory, $"{safeName}.key");
    }

    private void SetDirectoryPermissions(string path)
    {
        try
        {
            // Set directory to owner-only (700)
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set permissions on directory {Path}", path);
        }
    }
    
    /// <summary>
    /// Validates key name to prevent path traversal and injection attacks.
    /// </summary>
    private static void ValidateKeyName(string keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName))
            throw new ArgumentException("Key name cannot be empty", nameof(keyName));
            
        if (keyName.Contains("..") || keyName.Contains('/') || keyName.Contains('\\'))
            throw new ArgumentException("Key name contains invalid characters", nameof(keyName));
    }
}

