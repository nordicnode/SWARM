using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Swarm.Core.Security;

/// <summary>
/// macOS implementation of secure key storage using the Keychain.
/// Uses the 'security' command-line tool to interact with the login keychain.
/// </summary>
public class MacOSSecureKeyStorage : ISecureKeyStorage
{
    private const string ServiceName = "com.swarm.keystore";
    private readonly string _storageDirectory;
    private readonly ILogger<MacOSSecureKeyStorage> _logger;

    public MacOSSecureKeyStorage(string storageDirectory, ILogger<MacOSSecureKeyStorage> logger)
    {
        _storageDirectory = storageDirectory;
        _logger = logger;
        Directory.CreateDirectory(_storageDirectory);
    }

    public void StoreKey(string keyName, byte[] keyData)
    {
        try
        {
            // First try to delete any existing key
            DeleteFromKeychain(keyName);

            // Convert to base64 for keychain storage
            var base64Data = Convert.ToBase64String(keyData);

            // Add to keychain using security command
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "security",
                Arguments = $"add-generic-password -a \"{keyName}\" -s \"{ServiceName}\" -w \"{base64Data}\" -U",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            process?.WaitForExit(10000);

            if (process?.ExitCode == 0)
            {
                _logger.LogInformation("Stored key '{KeyName}' in macOS Keychain", keyName);
            }
            else
            {
                // Fall back to file-based storage with permissions
                StoreFallback(keyName, keyData);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Keychain storage failed, using fallback for '{KeyName}'", keyName);
            StoreFallback(keyName, keyData);
        }
    }

    public byte[]? RetrieveKey(string keyName)
    {
        try
        {
            // Try to retrieve from keychain
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "security",
                Arguments = $"find-generic-password -a \"{keyName}\" -s \"{ServiceName}\" -w",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            process?.WaitForExit(10000);

            if (process?.ExitCode == 0)
            {
                var base64Data = process.StandardOutput.ReadToEnd().Trim();
                if (!string.IsNullOrEmpty(base64Data))
                {
                    var keyData = Convert.FromBase64String(base64Data);
                    _logger.LogInformation("Retrieved key '{KeyName}' from macOS Keychain", keyName);
                    return keyData;
                }
            }

            // Fall back to file-based storage
            return RetrieveFallback(keyName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Keychain retrieval failed, trying fallback for '{KeyName}'", keyName);
            return RetrieveFallback(keyName);
        }
    }

    public bool KeyExists(string keyName)
    {
        // Check keychain first
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "security",
                Arguments = $"find-generic-password -a \"{keyName}\" -s \"{ServiceName}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            process?.WaitForExit(5000);
            if (process?.ExitCode == 0)
                return true;
        }
        catch { }

        // Check fallback location
        return File.Exists(GetKeyPath(keyName));
    }

    public void DeleteKey(string keyName)
    {
        DeleteFromKeychain(keyName);
        
        // Also delete fallback file if exists
        var filePath = GetKeyPath(keyName);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
        
        _logger.LogInformation("Deleted key '{KeyName}'", keyName);
    }

    private void DeleteFromKeychain(string keyName)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "security",
                Arguments = $"delete-generic-password -a \"{keyName}\" -s \"{ServiceName}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(5000);
        }
        catch { }
    }

    private void StoreFallback(string keyName, byte[] keyData)
    {
        var filePath = GetKeyPath(keyName);
        File.WriteAllBytes(filePath, keyData);
        SetUnixPermissions(filePath, "600");
        _logger.LogInformation("Stored key '{KeyName}' with file fallback (chmod 600)", keyName);
    }

    private byte[]? RetrieveFallback(string keyName)
    {
        var filePath = GetKeyPath(keyName);
        if (!File.Exists(filePath))
            return null;

        return File.ReadAllBytes(filePath);
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
        catch { }
    }
}
