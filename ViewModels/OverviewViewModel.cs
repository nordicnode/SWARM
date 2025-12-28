using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Swarm.Core.Models;
using Swarm.Core.Services;
using Swarm.Core.Helpers;
using Swarm.Core.ViewModels;

namespace Swarm.ViewModels;

public class OverviewViewModel : BaseViewModel, IDisposable
{
    private readonly SyncService _syncService;
    private readonly VersioningService _versioningService;
    private readonly ActivityLogService _activityLogService;
    private readonly DiscoveryService _discoveryService;
    private readonly Settings _settings;
    private readonly Dispatcher _dispatcher;
    private FileSystemWatcher? _watcher;
    private readonly System.Timers.Timer _debounceTimer;
    private readonly System.Timers.Timer _timeRefreshTimer;
    private bool _refreshPending;

    private string _filesSynced = "0";
    private string _dataTransferred = "0 B";
    private string _connectedPeers = "0";
    private string _peerStatus = "No peers connected";
    private string _healthText = "Unknown";
    private System.Windows.Media.Brush _healthColor = System.Windows.Media.Brushes.Gray;
    private string _lastSyncText = "Last sync: Unknown";
    private string _syncFolderPath = "";
    private string _syncFolderSize = "Calculating...";
    private string _versionStorageSize = "0 B";
    private string _versionCountText = "0 versions";

    public ObservableCollection<RecentActivityItem> RecentActivity { get; } = new();

    public string FilesSynced { get => _filesSynced; set => SetProperty(ref _filesSynced, value); }
    public string DataTransferred { get => _dataTransferred; set => SetProperty(ref _dataTransferred, value); }
    public string ConnectedPeers { get => _connectedPeers; set => SetProperty(ref _connectedPeers, value); }
    public string PeerStatus { get => _peerStatus; set => SetProperty(ref _peerStatus, value); }
    public string HealthText { get => _healthText; set => SetProperty(ref _healthText, value); }
    public System.Windows.Media.Brush HealthColor { get => _healthColor; set => SetProperty(ref _healthColor, value); }
    public string LastSyncText { get => _lastSyncText; set => SetProperty(ref _lastSyncText, value); }
    public string SyncFolderPath { get => _syncFolderPath; set => SetProperty(ref _syncFolderPath, value); }
    public string SyncFolderSize { get => _syncFolderSize; set => SetProperty(ref _syncFolderSize, value); }
    public string VersionStorageSize { get => _versionStorageSize; set => SetProperty(ref _versionStorageSize, value); }
    public string VersionCountText { get => _versionCountText; set => SetProperty(ref _versionCountText, value); }

    public ICommand RefreshCommand { get; }
    public ICommand ClearRecentActivityCommand { get; }

    public OverviewViewModel(
        SyncService syncService,
        VersioningService versioningService,
        ActivityLogService activityLogService,
        DiscoveryService discoveryService,
        Settings settings)
    {
        _syncService = syncService;
        _versioningService = versioningService;
        _activityLogService = activityLogService;
        _discoveryService = discoveryService;
        _settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;

        // Setup debounce timer (500ms delay to batch rapid changes)
        _debounceTimer = new System.Timers.Timer(500);
        _debounceTimer.AutoReset = false;
        _debounceTimer.Elapsed += (s, e) => _dispatcher.Invoke(() =>
        {
            if (_refreshPending)
            {
                _refreshPending = false;
                LoadStorageOverview();
                LoadFileStats();
            }
        });
        
        // Setup time refresh timer (10 seconds) to keep timestamps accurate
        _timeRefreshTimer = new System.Timers.Timer(10000);
        _timeRefreshTimer.AutoReset = true;
        _timeRefreshTimer.Elapsed += (s, e) => _dispatcher.Invoke(() => LoadRecentActivity());
        _timeRefreshTimer.Start();

        RefreshCommand = new RelayCommand(_ => LoadDashboardData());
        ClearRecentActivityCommand = new RelayCommand(_ => ClearRecentActivity());
        
        // Subscribe to activity log changes for auto-refresh
        _activityLogService.EntryAdded += OnActivityEntryAdded;
        
        LoadDashboardData();
        InitializeFileWatcher();
    }
    
    private void OnActivityEntryAdded(ActivityLogEntry entry)
    {
        _dispatcher.Invoke(() => LoadRecentActivity());
    }
    
    private void InitializeFileWatcher()
    {
        try
        {
            if (!Directory.Exists(_settings.SyncFolderPath)) return;
            
            _watcher = new FileSystemWatcher(_settings.SyncFolderPath)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | 
                               NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };
            
            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.Renamed += OnFileRenamed;
            _watcher.Changed += OnFileChanged;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Overview] Failed to initialize file watcher: {ex.Message}");
        }
    }
    
    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        RequestRefresh();
    }
    
    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        RequestRefresh();
    }
    
    private void RequestRefresh()
    {
        _refreshPending = true;
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }
    
    public void Dispose()
    {
        _activityLogService.EntryAdded -= OnActivityEntryAdded;
        _timeRefreshTimer?.Stop();
        _timeRefreshTimer?.Dispose();
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
    }

    public void LoadDashboardData()
    {
        LoadFileStats();
        LoadPeerStats();
        LoadHealthStatus();
        LoadRecentActivity();
        LoadStorageOverview();
    }
    
    private void ClearRecentActivity()
    {
        _activityLogService.ClearAll();
        RecentActivity.Clear();
    }

    private void LoadFileStats()
    {
        try
        {
            var fileCount = _syncService.GetTrackedFileCount();
            FilesSynced = fileCount.ToString("N0");

            var bytesTransferred = _syncService.GetSessionBytesTransferred();
            DataTransferred = FileHelpers.FormatBytes(bytesTransferred);
        }
        catch
        {
            FilesSynced = "N/A";
            DataTransferred = "N/A";
        }
    }

    private void LoadPeerStats()
    {
        try
        {
            var peerCount = _discoveryService.Peers.Count;
            ConnectedPeers = peerCount.ToString();

            if (peerCount == 0)
            {
                PeerStatus = "No peers connected";
            }
            else if (peerCount == 1)
            {
                var peer = _discoveryService.Peers.FirstOrDefault();
                PeerStatus = peer != null ? $"Connected: {peer.Name}" : "1 peer online";
            }
            else
            {
                PeerStatus = $"{peerCount} peers online";
            }
        }
        catch
        {
            ConnectedPeers = "0";
            PeerStatus = "Unable to check peers";
        }
    }

    private void LoadHealthStatus()
    {
        try
        {
            var isPaused = _settings.IsSyncCurrentlyPaused;
            var hasErrors = false;
            var lastSync = _syncService.LastSyncTime;

            if (isPaused)
            {
                HealthText = "Paused";
                HealthColor = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F59E0B"));
            }
            else if (hasErrors)
            {
                HealthText = "Issues";
                HealthColor = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F85149"));
            }
            else
            {
                HealthText = "Healthy";
                HealthColor = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3FB950"));
            }

            if (lastSync.HasValue)
            {
                var ago = DateTime.Now - lastSync.Value;
                if (ago.TotalMinutes < 1)
                    LastSyncText = "Last sync: Just now";
                else if (ago.TotalHours < 1)
                    LastSyncText = $"Last sync: {(int)ago.TotalMinutes}m ago";
                else if (ago.TotalDays < 1)
                    LastSyncText = $"Last sync: {(int)ago.TotalHours}h ago";
                else
                    LastSyncText = $"Last sync: {lastSync.Value:MMM d, h:mm tt}";
            }
            else
            {
                LastSyncText = "Last sync: Never";
            }
        }
        catch
        {
            HealthText = "Unknown";
            LastSyncText = "Last sync: Unknown";
        }
    }

    private void LoadRecentActivity()
    {
        try
        {
            var entries = _activityLogService.GetRecentEntries(10);
            RecentActivity.Clear();
            foreach (var e in entries)
            {
                RecentActivity.Add(new RecentActivityItem
                {
                    Type = MapActivityType(e.Type),
                    Message = e.Message,
                    TimeAgo = FormatTimeAgo(e.Timestamp),
                    Source = string.IsNullOrEmpty(e.PeerName) ? "You" : e.PeerName,
                    FilePath = e.FilePath ?? "",
                    Icon = GetActivityIcon(e.Type)
                });
            }
        }
        catch
        {
            // Ignore errors
        }
    }
    
    private string GetActivityIcon(ActivityType type) => type switch
    {
        ActivityType.FileCreated => "📝",
        ActivityType.FileModified => "✏️",
        ActivityType.FileDeleted => "🗑️",
        ActivityType.FileRenamed => "📋",
        ActivityType.FileSynced => "🔄",
        ActivityType.TransferStarted => "📤",
        ActivityType.TransferCompleted => "✅",
        ActivityType.TransferFailed => "❌",
        ActivityType.PeerConnected => "🔗",
        ActivityType.PeerDisconnected => "🔌",
        ActivityType.ConflictDetected => "⚠️",
        ActivityType.ConflictResolved => "✔️",
        ActivityType.Error => "🚨",
        ActivityType.Warning => "⚡",
        _ => "📄"
    };

    private void LoadStorageOverview()
    {
        try
        {
            var syncPath = _settings.SyncFolderPath;
            SyncFolderPath = syncPath;

            if (Directory.Exists(syncPath))
            {
                // Calculating size can be slow, might want to run async in future
                var size = CalculateDirectorySize(syncPath);
                SyncFolderSize = FileHelpers.FormatBytes(size);
            }
            else
            {
                SyncFolderSize = "Not found";
            }

            var versionSize = _versioningService.GetTotalVersionsSize();
            var versionCount = _versioningService.GetTotalVersionCount();

            VersionStorageSize = FileHelpers.FormatBytes(versionSize);
            VersionCountText = versionCount == 1 ? "1 version stored" : $"{versionCount} versions stored";
        }
        catch
        {
            SyncFolderSize = "N/A";
            VersionStorageSize = "N/A";
        }
    }

    private static long CalculateDirectorySize(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }

    private static string MapActivityType(ActivityType type)
    {
        return type switch
        {
            ActivityType.FileSynced or ActivityType.FileCreated or ActivityType.FileModified => "FileSync",
            ActivityType.ConflictResolved or ActivityType.ConflictDetected => "Conflict",
            ActivityType.Error or ActivityType.TransferFailed => "Error",
            _ => "Info"
        };
    }

    private static string FormatTimeAgo(DateTime timestamp)
    {
        // Timestamps are stored as UTC, so compare with UTC
        var ago = DateTime.UtcNow - timestamp;

        if (ago.TotalSeconds < 5) return "just now";
        if (ago.TotalSeconds < 60) return $"{(int)ago.TotalSeconds}s ago";
        if (ago.TotalMinutes < 60) return $"{(int)ago.TotalMinutes}m ago";
        if (ago.TotalHours < 24) return $"{(int)ago.TotalHours}h ago";
        if (ago.TotalDays < 7) return $"{(int)ago.TotalDays}d ago";
        return timestamp.ToLocalTime().ToString("MMM d");
    }
}

public class RecentActivityItem
{
    public string Type { get; set; } = "Info";
    public string Message { get; set; } = "";
    public string TimeAgo { get; set; } = "";
    public string Source { get; set; } = ""; // "You" or peer name
    public string FilePath { get; set; } = "";
    public string Icon { get; set; } = "📄"; // Emoji icon for the activity type
}
