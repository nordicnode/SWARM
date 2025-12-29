using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace Swarm.Core.Services;

/// <summary>
/// Application settings with persistence support.
/// </summary>
public class Settings
{
    private const string PortableMarkerFile = "portable.marker";
    private const string SettingsFileName = "settings.json";

    /// <summary>
    /// Gets whether the application is running in portable mode.
    /// Portable mode is active when a 'portable.marker' file exists next to the executable.
    /// </summary>
    public static bool IsPortableMode { get; } = CheckPortableMode();

    private static bool CheckPortableMode()
    {
        var exeDir = GetExecutableDirectory();
        return File.Exists(Path.Combine(exeDir, PortableMarkerFile));
    }

    private static string GetExecutableDirectory()
    {
        // For single-file apps, use the process path
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            return Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory;
        }
        return AppContext.BaseDirectory;
    }

    private static string GetSettingsDirectory()
    {
        if (IsPortableMode)
        {
            return GetExecutableDirectory();
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Swarm");
    }

    private static readonly string SettingsDirectory = GetSettingsDirectory();
    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, SettingsFileName);

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
    public List<Swarm.Core.Models.TrustedPeer> TrustedPeers { get; set; } = [];

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
    /// Whether file versioning is enabled.
    /// </summary>
    public bool VersioningEnabled { get; set; } = true;

    /// <summary>
    /// Maximum number of versions to keep per file.
    /// </summary>
    public int MaxVersionsPerFile { get; set; } = 10;

    /// <summary>
    /// Maximum age of versions in days before auto-pruning.
    /// </summary>
    public int MaxVersionAgeDays { get; set; } = 30;

    /// <summary>
    /// Maximum upload speed in KB/s (0 = unlimited).
    /// </summary>
    public long MaxUploadSpeedKBps { get; set; } = 0;

    /// <summary>
    /// Maximum download speed in KB/s (0 = unlimited).
    /// </summary>
    public long MaxDownloadSpeedKBps { get; set; } = 0;

    /// <summary>
    /// List of folder paths (relative to sync folder) that are excluded from syncing.
    /// Used for Selective Sync feature.
    /// </summary>
    public List<string> ExcludedFolders { get; set; } = [];

    /// <summary>
    /// Interval in minutes between automatic rescans (0 = disabled).
    /// Rescan catches changes that FileSystemWatcher may have missed.
    /// </summary>
    public int RescanIntervalMinutes { get; set; } = 30;

    /// <summary>
    /// Rescan mode: QuickTimestampOnly (fast) or DeepWithHash (thorough).
    /// </summary>
    public RescanMode RescanMode { get; set; } = RescanMode.QuickTimestampOnly;

    /// <summary>
    /// Conflict resolution mode when files differ between local and remote.
    /// </summary>
    public ConflictResolutionMode ConflictResolution { get; set; } = ConflictResolutionMode.AutoNewest;

    /// <summary>
    /// Sync is paused until this time (null = not paused).
    /// Used for temporary "Pause for X minutes" feature.
    /// </summary>
    public DateTime? SyncPausedUntil { get; set; }

    /// <summary>
    /// Whether to automatically pause sync when on battery power.
    /// </summary>
    public bool PauseOnBattery { get; set; } = false;

    /// <summary>
    /// Whether to automatically pause sync on metered network connections.
    /// </summary>
    public bool PauseOnMeteredNetwork { get; set; } = false;

    /// <summary>
    /// Whether to minimize to system tray instead of exiting when closing the window.
    /// </summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>
    /// List of encrypted folders with their configuration.
    /// </summary>
    public List<Models.EncryptedFolder> EncryptedFolders { get; set; } = new();

    /// <summary>
    /// Auto-lock timeout in minutes for encrypted folders (0 = disabled).
    /// </summary>
    public int EncryptionAutoLockMinutes { get; set; } = 15;

    /// <summary>
    /// Sync schedule configuration for time-based sync windows.
    /// </summary>
    public Models.SyncSchedule SyncSchedule { get; set; } = new();

    /// <summary>
    /// Returns true if sync is currently in a paused state (either manual or auto).
    /// </summary>
    [JsonIgnore]
    public bool IsSyncCurrentlyPaused => 
        (SyncPausedUntil.HasValue && SyncPausedUntil.Value > DateTime.Now) ||
        (PauseOnBattery && IsOnBattery()) ||
        (PauseOnMeteredNetwork && IsOnMeteredConnection()) ||
        (SyncSchedule.IsEnabled && !SyncSchedule.IsSyncAllowedNow);

    /// <summary>
    /// Gets the remaining pause time as a human-readable string.
    /// </summary>
    [JsonIgnore]
    public string PauseRemainingDisplay
    {
        get
        {
            if (!SyncPausedUntil.HasValue || SyncPausedUntil.Value <= DateTime.Now)
                return "";
            
            var remaining = SyncPausedUntil.Value - DateTime.Now;
            if (remaining.TotalHours >= 1)
                return $"Paused for {remaining.Hours}h {remaining.Minutes}m";
            if (remaining.TotalMinutes >= 1)
                return $"Paused for {remaining.Minutes}m";
            return "Resuming soon...";
        }
    }

    /// <summary>
    /// Pauses sync for a specified duration.
    /// </summary>
    public void PauseSyncFor(TimeSpan duration)
    {
        SyncPausedUntil = DateTime.Now.Add(duration);
        Save();
    }

    /// <summary>
    /// Resumes sync immediately by clearing the pause timer.
    /// </summary>
    public void ResumeSync()
    {
        SyncPausedUntil = null;
        Save();
    }

    private static bool IsOnBattery()
    {
        // Use the registered IPowerService if available
        return _powerService?.IsOnBattery ?? false;
    }

    private static bool IsOnMeteredConnection()
    {
        // Metered connection detection is platform-specific
        // For cross-platform, we disable this by default
        return false;
    }

    // Platform service injection
    private static Abstractions.IPowerService? _powerService;

    /// <summary>
    /// Registers a platform-specific power service implementation.
    /// </summary>
    public static void RegisterPowerService(Abstractions.IPowerService powerService)
    {
        _powerService = powerService;
    }

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
            Log.Error(ex, $"Failed to load settings: {ex.Message}");
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
                    settings.TrustedPeers.Add(new Swarm.Core.Models.TrustedPeer { Id = id, Name = "Unknown Peer" });
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
            Log.Error(ex, $"Failed to save settings: {ex.Message}");
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
            ShowTransferComplete = ShowTransferComplete,
            VersioningEnabled = VersioningEnabled,
            MaxVersionsPerFile = MaxVersionsPerFile,
            MaxVersionAgeDays = MaxVersionAgeDays,
            MaxUploadSpeedKBps = MaxUploadSpeedKBps,
            MaxDownloadSpeedKBps = MaxDownloadSpeedKBps,
            RescanIntervalMinutes = RescanIntervalMinutes,
            RescanMode = RescanMode,
            ConflictResolution = ConflictResolution,
            SyncPausedUntil = SyncPausedUntil,
            PauseOnBattery = PauseOnBattery,
            PauseOnMeteredNetwork = PauseOnMeteredNetwork,
            CloseToTray = CloseToTray,
            EncryptionAutoLockMinutes = EncryptionAutoLockMinutes,
            SyncSchedule = new Models.SyncSchedule
            {
                IsEnabled = SyncSchedule.IsEnabled,
                Mode = SyncSchedule.Mode,
                TimeWindows = SyncSchedule.TimeWindows.Select(w => new Models.SyncTimeWindow
                {
                    Name = w.Name,
                    StartTime = w.StartTime,
                    EndTime = w.EndTime,
                    Days = new List<DayOfWeek>(w.Days)
                }).ToList()
            }
        };

        // Clone encrypted folders (without runtime state)
        foreach (var folder in EncryptedFolders)
        {
            clone.EncryptedFolders.Add(new Models.EncryptedFolder
            {
                FolderPath = folder.FolderPath,
                Salt = folder.Salt,
                Verifier = folder.Verifier
            });
        }

        foreach (var peer in TrustedPeers)
        {
            clone.TrustedPeers.Add(new Swarm.Core.Models.TrustedPeer { Id = peer.Id, Name = peer.Name });
        }

        foreach (var kvp in TrustedPeerPublicKeys)
        {
            clone.TrustedPeerPublicKeys[kvp.Key] = kvp.Value;
        }

        foreach (var folder in ExcludedFolders)
        {
            clone.ExcludedFolders.Add(folder);
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
        VersioningEnabled = source.VersioningEnabled;
        MaxVersionsPerFile = source.MaxVersionsPerFile;
        MaxVersionAgeDays = source.MaxVersionAgeDays;
        MaxUploadSpeedKBps = source.MaxUploadSpeedKBps;
        MaxDownloadSpeedKBps = source.MaxDownloadSpeedKBps;
        RescanIntervalMinutes = source.RescanIntervalMinutes;
        RescanMode = source.RescanMode;
        ConflictResolution = source.ConflictResolution;
        SyncPausedUntil = source.SyncPausedUntil;
        PauseOnBattery = source.PauseOnBattery;
        PauseOnMeteredNetwork = source.PauseOnMeteredNetwork;
        CloseToTray = source.CloseToTray;
        
        SyncSchedule.IsEnabled = source.SyncSchedule.IsEnabled;
        SyncSchedule.Mode = source.SyncSchedule.Mode;
        SyncSchedule.TimeWindows.Clear();
        foreach (var window in source.SyncSchedule.TimeWindows)
        {
            SyncSchedule.TimeWindows.Add(new Models.SyncTimeWindow
            {
                Name = window.Name,
                StartTime = window.StartTime,
                EndTime = window.EndTime,
                Days = new List<DayOfWeek>(window.Days)
            });
        }

        EncryptionAutoLockMinutes = source.EncryptionAutoLockMinutes;
        EncryptedFolders.Clear();
        foreach (var folder in source.EncryptedFolders)
        {
            EncryptedFolders.Add(new Models.EncryptedFolder
            {
                FolderPath = folder.FolderPath,
                Salt = folder.Salt,
                Verifier = folder.Verifier
            });
        }
        
        TrustedPeers.Clear();
        foreach (var peer in source.TrustedPeers)
        {
            TrustedPeers.Add(new Swarm.Core.Models.TrustedPeer { Id = peer.Id, Name = peer.Name });
        }

        TrustedPeerPublicKeys.Clear();
        foreach (var kvp in source.TrustedPeerPublicKeys)
        {
            TrustedPeerPublicKeys[kvp.Key] = kvp.Value;
        }

        ExcludedFolders.Clear();
        foreach (var folder in source.ExcludedFolders)
        {
            ExcludedFolders.Add(folder);
        }
    }

    /// <summary>
    /// Adds a peer to the trusted peers list and stores their public key.
    /// </summary>
    public void TrustPeer(Models.Peer peer)
    {
        if (string.IsNullOrEmpty(peer.Id) || string.IsNullOrEmpty(peer.PublicKeyBase64))
            return;

        // Add to trusted peers list if not already present
        if (!TrustedPeers.Any(p => p.Id == peer.Id))
        {
            TrustedPeers.Add(new Models.TrustedPeer 
            { 
                Id = peer.Id, 
                Name = peer.Name 
            });
        }

        // Store public key for verification
        TrustedPeerPublicKeys[peer.Id] = peer.PublicKeyBase64;
        
        // Mark peer as trusted
        peer.IsTrusted = true;
    }

    /// <summary>
    /// Removes a peer from the trusted list.
    /// </summary>
    public void UntrustPeer(string peerId)
    {
        TrustedPeers.RemoveAll(p => p.Id == peerId);
        TrustedPeerPublicKeys.Remove(peerId);
    }
}

