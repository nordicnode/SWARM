using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Swarm.Core.Security;

/// <summary>
/// macOS implementation of secure key storage using the Keychain.
/// Uses the 'security' command-line tool to interact with the login keychain.
/// Uses ArgumentList for safe parameter passing (no command injection).
/// </summary>
[SupportedOSPlatform("macos")]
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
        ValidateKeyName(keyName);
        
        try
        {
            // First try to delete any existing key
            DeleteFromKeychain(keyName);

            // Convert to base64 for keychain storage
            var base64Data = Convert.ToBase64String(keyData);

            // Add to keychain using security command with ArgumentList for safety
            var psi = new ProcessStartInfo
            {
                FileName = "security",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // Use ArgumentList for injection-safe argument passing
            psi.ArgumentList.Add("add-generic-password");
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(keyName);
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(ServiceName);
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add(base64Data);
            psi.ArgumentList.Add("-U");

            var process = Process.Start(psi);
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
        ValidateKeyName(keyName);
        
        try
        {
            // Try to retrieve from keychain with ArgumentList for safety
            var psi = new ProcessStartInfo
            {
                FileName = "security",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("find-generic-password");
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(keyName);
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(ServiceName);
            psi.ArgumentList.Add("-w");

            var process = Process.Start(psi);
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
        ValidateKeyName(keyName);
        
        // Check keychain first
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "security",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("find-generic-password");
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(keyName);
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(ServiceName);

            var process = Process.Start(psi);
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
        ValidateKeyName(keyName);
        
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
            var psi = new ProcessStartInfo
            {
                FileName = "security",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("delete-generic-password");
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(keyName);
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(ServiceName);

            var process = Process.Start(psi);
            process?.WaitForExit(5000);
        }
        catch { }
    }

    private void StoreFallback(string keyName, byte[] keyData)
    {
        var filePath = GetKeyPath(keyName);
        File.WriteAllBytes(filePath, keyData);
        
        // Use native .NET API for permissions
        File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        
        _logger.LogInformation("Stored key '{KeyName}' with file fallback (owner-only)", keyName);
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

