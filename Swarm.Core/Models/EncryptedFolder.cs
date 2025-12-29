using System.Text.Json.Serialization;

namespace Swarm.Core.Models;

/// <summary>
/// Represents an encrypted folder configuration.
/// Files within are stored encrypted with filename obfuscation.
/// </summary>
public class EncryptedFolder
{
    /// <summary>
    /// Relative path from sync root to the encrypted folder.
    /// </summary>
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>
    /// PBKDF2 salt for key derivation (Base64 encoded).
    /// </summary>
    public string Salt { get; set; } = string.Empty;

    /// <summary>
    /// Encrypted known value for password verification (Base64 encoded).
    /// </summary>
    public string Verifier { get; set; } = string.Empty;

    /// <summary>
    /// Last time this folder was accessed (for auto-lock).
    /// </summary>
    public DateTime LastAccessed { get; set; } = DateTime.MinValue;

    /// <summary>
    /// Runtime state: whether the folder is currently locked.
    /// Not persisted to settings.
    /// </summary>
    [JsonIgnore]
    public bool IsLocked { get; set; } = true;

    /// <summary>
    /// Runtime state: cached derived key (cleared on lock).
    /// Not persisted to settings.
    /// </summary>
    [JsonIgnore]
    public byte[]? CachedKey { get; set; }
}

/// <summary>
/// Manifest mapping obfuscated filenames to real filenames.
/// Stored encrypted in .swarm-vault/manifest.senc
/// </summary>
public class EncryptedFileManifest
{
    /// <summary>
    /// Maps obfuscated filename (e.g., "abc123.senc") to real filename (e.g., "Budget_2025.xlsx").
    /// </summary>
    public Dictionary<string, string> FileMap { get; set; } = new();

    /// <summary>
    /// Version of the manifest format.
    /// </summary>
    public int Version { get; set; } = 1;
}

/// <summary>
/// Vault configuration stored in .swarm-vault/config.json
/// </summary>
public class VaultConfig
{
    /// <summary>
    /// Format version.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// PBKDF2 salt for key derivation (Base64).
    /// </summary>
    public string Salt { get; set; } = string.Empty;

    /// <summary>
    /// Encrypted known value for password verification (Base64).
    /// </summary>
    public string Verifier { get; set; } = string.Empty;
}
