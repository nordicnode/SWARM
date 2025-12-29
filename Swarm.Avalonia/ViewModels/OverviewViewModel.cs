using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Serilog;
using Swarm.Core.Models;
using Swarm.Core.Services;
using Swarm.Core.Helpers;

namespace Swarm.Avalonia.ViewModels;

/// <summary>
/// ViewModel for the Overview/Dashboard view.
/// </summary>
public class OverviewViewModel : ViewModelBase, IDisposable
{
    private readonly SyncService _syncService = null!;
    private readonly DiscoveryService _discoveryService = null!;
    private readonly ActivityLogService? _activityLogService;
    private readonly Settings _settings = null!;

    private string _syncFolderPath = "";
    private int _totalFiles;
    private string _totalSize = "0 KB";
    private int _connectedPeers;
    private string _syncStatus = "Active";
    private string _syncStatusColor = "#34d399"; // Green

    private readonly System.Timers.Timer _debounceTimer = null!;
    private CancellationTokenSource? _statsCts;
    private bool _isLoading;

    public OverviewViewModel()
    {
        // Design-time constructor
        _syncFolderPath = "C:\\Users\\Demo\\Sync";
        _totalFiles = 42;
        _totalSize = "150 MB";
        _connectedPeers = 3;
        _syncStatus = "Active";
        
        // Sample design-time activities
        RecentActivities.Add(new RecentActivityItem("File Synced", "document.pdf synced with Laptop", DateTime.Now.AddMinutes(-2)));
        RecentActivities.Add(new RecentActivityItem("Peer Connected", "Laptop connected", DateTime.Now.AddMinutes(-5)));
    }

    public OverviewViewModel(
        SyncService syncService,
        DiscoveryService discoveryService,
        Settings settings,
        ActivityLogService? activityLogService = null)
    {
        _syncService = syncService;
        _discoveryService = discoveryService;
        _settings = settings;
        _activityLogService = activityLogService;

        // Initialize debounce timer (2 seconds)
        _debounceTimer = new System.Timers.Timer(2000);
        _debounceTimer.AutoReset = false;
        _debounceTimer.Elapsed += OnDebounceTimerElapsed;

        // Subscribe to updates
        _discoveryService.Peers.CollectionChanged += OnPeersCollectionChanged;
        _syncService.FileChanged += OnFileChanged;
        _syncService.SyncStatusChanged += OnSyncStatusChanged;

        // Initial load
        SyncFolderPath = _settings.SyncFolderPath;
        ConnectedPeers = _discoveryService.Peers.Count;
        UpdateSyncStatus();
        RequestUpdateStats();
        RefreshRecentActivities();
        
        // Subscribe to activity log updates
        if (_activityLogService != null)
        {
            _activityLogService.EntryAdded += OnActivityEntryAdded;
        }
    }

    private void OnDebounceTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        RequestUpdateStats();
    }

    private void OnPeersCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => ConnectedPeers = _discoveryService.Peers.Count);
    }

    private void OnFileChanged(SyncedFile file)
    {
        DebounceUpdateStats();
    }

    private void DebounceUpdateStats()
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void RequestUpdateStats()
    {
        _statsCts?.Cancel();
        _statsCts = new CancellationTokenSource();
        var token = _statsCts.Token;

        Task.Run(() => CalculateStats(token));
    }

    /// <summary>
    /// Public method to request a refresh of stats (called via F5).
    /// </summary>
    public void RequestRefresh()
    {
        RequestUpdateStats();
        RefreshRecentActivities();
    }

    private void CalculateStats(CancellationToken ct)
    {
        Dispatcher.UIThread.Post(() => IsLoading = true);
        try
        {
            if (ct.IsCancellationRequested) return;

            var path = _settings.SyncFolderPath;
            if (!Directory.Exists(path))
            {
                UpdateUI(0, "0 KB");
                return;
            }

            // Calculate stats off UI thread
            var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
            var count = files.Length;

            long size = 0;
            foreach (var f in files)
            {
                if (ct.IsCancellationRequested) return;
                try { size += new FileInfo(f).Length; } catch { }
            }

            var sizeStr = FileHelpers.FormatBytes(size);

            if (!ct.IsCancellationRequested)
            {
                UpdateUI(count, sizeStr);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to calculate sync folder stats for {Path}", _settings.SyncFolderPath);
            if (!ct.IsCancellationRequested)
            {
                UpdateUI(0, "Error");
            }
        }
    }

    private void UpdateUI(int count, string size)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TotalFiles = count;
            TotalSize = size;
            IsLoading = false;
        });
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string SyncFolderPath
    {
        get => _syncFolderPath;
        set => SetProperty(ref _syncFolderPath, value);
    }

    public int TotalFiles
    {
        get => _totalFiles;
        set => SetProperty(ref _totalFiles, value);
    }

    public string TotalSize
    {
        get => _totalSize;
        set => SetProperty(ref _totalSize, value);
    }

    public int ConnectedPeers
    {
        get => _connectedPeers;
        set => SetProperty(ref _connectedPeers, value);
    }

    public string SyncStatus
    {
        get => _syncStatus;
        set => SetProperty(ref _syncStatus, value);
    }

    public string SyncStatusColor
    {
        get => _syncStatusColor;
        set => SetProperty(ref _syncStatusColor, value);
    }

    private void OnSyncStatusChanged(string status)
    {
        Dispatcher.UIThread.Post(UpdateSyncStatus);
    }

    private void UpdateSyncStatus()
    {
        if (_syncService == null) return;
        
        if (!_settings.IsSyncEnabled)
        {
            SyncStatus = "Disabled";
            SyncStatusColor = "#6b7280"; // Gray
        }
        else if (_syncService.IsRunning)
        {
            SyncStatus = "Active";
            SyncStatusColor = "#34d399"; // Green
        }
        else
        {
            SyncStatus = "Paused";
            SyncStatusColor = "#fbbf24"; // Yellow
        }
    }

    public void Dispose()
    {
        // Unsubscribe from all events to prevent memory leaks
        if (_syncService != null)
        {
            _syncService.SyncStatusChanged -= OnSyncStatusChanged;
            _syncService.FileChanged -= OnFileChanged;
        }
        
        if (_discoveryService != null)
        {
            _discoveryService.Peers.CollectionChanged -= OnPeersCollectionChanged;
        }
        
        if (_activityLogService != null)
        {
            _activityLogService.EntryAdded -= OnActivityEntryAdded;
        }
        
        if (_debounceTimer != null)
        {
            _debounceTimer.Elapsed -= OnDebounceTimerElapsed;
            _debounceTimer.Stop();
            _debounceTimer.Dispose();
        }
        
        _statsCts?.Cancel();
        _statsCts?.Dispose();
    }

    #region Recent Activities

    /// <summary>
    /// Recent activity items for display on dashboard.
    /// </summary>
    public ObservableCollection<RecentActivityItem> RecentActivities { get; } = new();

    /// <summary>
    /// Whether there are any recent activities to display.
    /// </summary>
    public bool HasRecentActivities => RecentActivities.Count > 0;

    private void OnActivityEntryAdded(ActivityLogEntry entry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Add new entry at the top
            RecentActivities.Insert(0, new RecentActivityItem(entry));
            
            // Keep only the last 5 entries
            while (RecentActivities.Count > 5)
            {
                RecentActivities.RemoveAt(RecentActivities.Count - 1);
            }
            
            OnPropertyChanged(nameof(HasRecentActivities));
        });
    }

    private void RefreshRecentActivities()
    {
        if (_activityLogService == null) return;

        Dispatcher.UIThread.Post(() =>
        {
            RecentActivities.Clear();
            
            foreach (var entry in _activityLogService.GetRecentEntries(5))
            {
                RecentActivities.Add(new RecentActivityItem(entry));
            }
            
            OnPropertyChanged(nameof(HasRecentActivities));
        });
    }

    #endregion
}

/// <summary>
/// View model item for displaying recent activity in the overview.
/// </summary>
public class RecentActivityItem
{
    public string Title { get; }
    public string Description { get; }
    public string TimeAgo { get; }
    public string IconKind { get; }
    public string IconColor { get; }

    public RecentActivityItem(string title, string description, DateTime timestamp)
    {
        Title = title;
        Description = description;
        TimeAgo = FormatTimeAgo(timestamp);
        IconKind = "InformationCircle";
        IconColor = "#60a5fa"; // Blue
    }

    public RecentActivityItem(ActivityLogEntry entry)
    {
        Title = entry.TypeDisplay;
        Description = TruncateMessage(entry.Message, 50);
        TimeAgo = FormatTimeAgo(entry.Timestamp);
        
        (IconKind, IconColor) = GetIconForType(entry.Type, entry.Severity);
    }

    private static string TruncateMessage(string message, int maxLength)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        return message.Length <= maxLength ? message : message[..(maxLength - 3)] + "...";
    }

    private static string FormatTimeAgo(DateTime timestamp)
    {
        var localTime = timestamp.Kind == DateTimeKind.Utc ? timestamp.ToLocalTime() : timestamp;
        var diff = DateTime.Now - localTime;

        return diff.TotalMinutes switch
        {
            < 1 => "Just now",
            < 60 => $"{(int)diff.TotalMinutes}m ago",
            < 1440 => $"{(int)diff.TotalHours}h ago",
            _ => $"{(int)diff.TotalDays}d ago"
        };
    }

    private static (string Kind, string Color) GetIconForType(ActivityType type, ActivitySeverity severity)
    {
        // Color based on severity
        var color = severity switch
        {
            ActivitySeverity.Success => "#34d399", // Green
            ActivitySeverity.Warning => "#fbbf24", // Yellow
            ActivitySeverity.Error => "#f87171",   // Red
            _ => "#60a5fa"                         // Blue
        };

        // Icon based on type
        var icon = type switch
        {
            ActivityType.FileCreated or ActivityType.FileModified or ActivityType.FileSynced => "Document",
            ActivityType.FileDeleted => "Delete",
            ActivityType.FileRenamed => "Rename",
            ActivityType.PeerConnected => "Link",
            ActivityType.PeerDisconnected => "LinkOff",
            ActivityType.TransferStarted or ActivityType.TransferCompleted => "CloudUpload",
            ActivityType.TransferFailed => "CloudOffOutline",
            ActivityType.ConflictDetected or ActivityType.ConflictResolved => "AlertCircle",
            ActivityType.Error => "AlertCircle",
            ActivityType.Warning => "Alert",
            _ => "InformationCircle"
        };

        return (icon, color);
    }
}
