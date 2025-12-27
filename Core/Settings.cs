using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Swarm.Core;

/// <summary>
/// Application settings with persistence support.
/// </summary>
public class Settings
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Swarm");

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>
    /// Path to the sync folder.
    /// </summary>
    public string SyncFolderPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "SWARM",
        "Synced");

    /// <summary>
    /// Whether folder synchronization is enabled.
    /// </summary>
    public bool IsSyncEnabled { get; set; } = true;

    /// <summary>
    /// List of trusted peers that can sync without confirmation.
    /// </summary>
    public List<Swarm.Models.TrustedPeer> TrustedPeers { get; set; } = [];

    /// <summary>
    /// Maps trusted peer IDs to their public keys (Base64 encoded).
    /// Used for signature verification.
    /// </summary>
    public Dictionary<string, string> TrustedPeerPublicKeys { get; set; } = [];

    /// <summary>
    /// Legacy list of trusted peer IDs (for migration purposes).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? TrustedPeerIds { get; set; }

    /// <summary>
    /// Default download path for manual file transfers.
    /// </summary>
    public string DownloadPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "Swarm");

    /// <summary>
    /// Custom display name for this device.
    /// </summary>
    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>
    /// Whether to auto-accept file transfers from trusted peers.
    /// </summary>
    public bool AutoAcceptFromTrusted { get; set; } = false;

    /// <summary>
    /// Whether to show transfer notifications.
    /// </summary>
    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>
    /// Whether to start the application minimized.
    /// </summary>
    public bool StartMinimized { get; set; } = false;

    /// <summary>
    /// Whether to show a message when a transfer completes.
    /// </summary>
    public bool ShowTransferComplete { get; set; } = true;

    /// <summary>
    /// Unique identifier for this device.
    /// </summary>
    public string LocalId { get; set; } = string.Empty;

    /// <summary>
    /// Loads settings from disk, or returns default settings if file doesn't exist.
    /// </summary>
    public static Settings Load()
    {
        Settings settings;
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                settings = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
            }
            else
            {
                settings = new Settings();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
            settings = new Settings();
        }

        // Ensure LocalId exists
        if (string.IsNullOrEmpty(settings.LocalId))
        {
            settings.LocalId = Guid.NewGuid().ToString()[..8];
            settings.Save();
        }

        // Migration: Convert old TrustedPeerIds to TrustedPeers
        if (settings.TrustedPeerIds != null && settings.TrustedPeerIds.Count > 0)
        {
            foreach (var id in settings.TrustedPeerIds)
            {
                if (!settings.TrustedPeers.Any(p => p.Id == id))
                {
                    settings.TrustedPeers.Add(new Swarm.Models.TrustedPeer { Id = id, Name = "Unknown Peer" });
                }
            }
            settings.TrustedPeerIds = null; // Clear after migration
            settings.Save(); // Save migrated settings
        }

        return settings;
    }

    /// <summary>
    /// Saves settings to disk.
    /// </summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
        }
    }

    /// <summary>
    /// Ensures the sync folder exists.
    /// </summary>
    public void EnsureSyncFolderExists()
    {
        Directory.CreateDirectory(SyncFolderPath);
    }

    /// <summary>
    /// Creates a deep copy of the settings.
    /// </summary>
    public Settings Clone()
    {
        var clone = new Settings
        {
            LocalId = LocalId,
            SyncFolderPath = SyncFolderPath,
            IsSyncEnabled = IsSyncEnabled,
            DownloadPath = DownloadPath,
            DeviceName = DeviceName,
            AutoAcceptFromTrusted = AutoAcceptFromTrusted,
            NotificationsEnabled = NotificationsEnabled,
            StartMinimized = StartMinimized,
            ShowTransferComplete = ShowTransferComplete
        };

        foreach (var peer in TrustedPeers)
        {
            clone.TrustedPeers.Add(new Swarm.Models.TrustedPeer { Id = peer.Id, Name = peer.Name });
        }

        foreach (var kvp in TrustedPeerPublicKeys)
        {
            clone.TrustedPeerPublicKeys[kvp.Key] = kvp.Value;
        }

        return clone;
    }

    /// <summary>
    /// Updates this instance with values from another settings instance.
    /// </summary>
    public void UpdateFrom(Settings source)
    {
        LocalId = source.LocalId;
        SyncFolderPath = source.SyncFolderPath;
        IsSyncEnabled = source.IsSyncEnabled;
        DownloadPath = source.DownloadPath;
        DeviceName = source.DeviceName;
        AutoAcceptFromTrusted = source.AutoAcceptFromTrusted;
        NotificationsEnabled = source.NotificationsEnabled;
        StartMinimized = source.StartMinimized;
        ShowTransferComplete = source.ShowTransferComplete;
        
        TrustedPeers.Clear();
        foreach (var peer in source.TrustedPeers)
        {
            TrustedPeers.Add(new Swarm.Models.TrustedPeer { Id = peer.Id, Name = peer.Name });
        }

        TrustedPeerPublicKeys.Clear();
        foreach (var kvp in source.TrustedPeerPublicKeys)
        {
            TrustedPeerPublicKeys[kvp.Key] = kvp.Value;
        }
    }
}
