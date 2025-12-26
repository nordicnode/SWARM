using System.IO;
using System.Text.Json;

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
    /// List of trusted peer IDs that can sync without confirmation.
    /// </summary>
    public List<string> TrustedPeerIds { get; set; } = [];

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
    /// Loads settings from disk, or returns default settings if file doesn't exist.
    /// </summary>
    public static Settings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
        }

        return new Settings();
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
}
