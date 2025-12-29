using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Swarm.Core.Security;

/// <summary>
/// Linux implementation of secure key storage using file permissions.
/// Uses chmod 600 to restrict access to the current user only.
/// </summary>
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
        SetUnixPermissions(_storageDirectory, "700");
    }

    public void StoreKey(string keyName, byte[] keyData)
    {
        try
        {
            var filePath = GetKeyPath(keyName);
            File.WriteAllBytes(filePath, keyData);
            
            // Set file permissions to owner-only (600)
            SetUnixPermissions(filePath, "600");
            
            _logger.LogInformation("Stored key '{KeyName}' with chmod 600 protection", keyName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store key '{KeyName}'", keyName);
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

    private string GetKeyPath(string keyName)
    {
        var safeName = string.Join("_", keyName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_storageDirectory, $"{safeName}.key");
    }

    private void SetUnixPermissions(string path, string mode)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"{mode} \"{path}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set permissions on {Path}", path);
        }
    }
}
