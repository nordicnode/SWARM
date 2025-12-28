using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Swarm.Core.Models;
using Swarm.Core.Services;
using Swarm.Core.Helpers;
using MediaColor = System.Windows.Media.Color;

namespace Swarm.UI;

/// <summary>
/// Sync Dashboard showing overview statistics and recent activity.
/// </summary>
public partial class SyncDashboard : Window
{
    private readonly SyncService _syncService;
    private readonly VersioningService _versioningService;
    private readonly ActivityLogService _activityLogService;
    private readonly DiscoveryService _discoveryService;
    private readonly Settings _settings;

    public SyncDashboard(
        SyncService syncService,
        VersioningService versioningService,
        ActivityLogService activityLogService,
        DiscoveryService discoveryService,
        Settings settings)
    {
        InitializeComponent();
        
        _syncService = syncService;
        _versioningService = versioningService;
        _activityLogService = activityLogService;
        _discoveryService = discoveryService;
        _settings = settings;
        
        Loaded += SyncDashboard_Loaded;
    }

    private void SyncDashboard_Loaded(object sender, RoutedEventArgs e)
    {
        LoadDashboardData();
    }

    private void LoadDashboardData()
    {
        LoadFileStats();
        LoadPeerStats();
        LoadHealthStatus();
        LoadRecentActivity();
        LoadStorageOverview();
    }

    private void LoadFileStats()
    {
        try
        {
            var fileCount = _syncService.GetTrackedFileCount();
            FilesSyncedText.Text = fileCount.ToString("N0");
            
            // Get session data transferred (from transfer service if available)
            var bytesTransferred = _syncService.GetSessionBytesTransferred();
            DataTransferredText.Text = FileHelpers.FormatBytes(bytesTransferred);
        }
        catch
        {
            FilesSyncedText.Text = "N/A";
            DataTransferredText.Text = "N/A";
        }
    }

    private void LoadPeerStats()
    {
        try
        {
            var peerCount = _discoveryService.Peers.Count;
            ConnectedPeersText.Text = peerCount.ToString();
            
            if (peerCount == 0)
            {
                PeerStatusText.Text = "No peers connected";
            }
            else if (peerCount == 1)
            {
                var peer = _discoveryService.Peers.First();
                PeerStatusText.Text = $"Connected: {peer.Name}";
            }
            else
            {
                PeerStatusText.Text = $"{peerCount} peers online";
            }
        }
        catch
        {
            ConnectedPeersText.Text = "0";
            PeerStatusText.Text = "Unable to check peers";
        }
    }

    private void LoadHealthStatus()
    {
        try
        {
            var isPaused = _settings.IsSyncCurrentlyPaused;
            var hasErrors = false; // Could track errors from activity log
            var lastSync = _syncService.LastSyncTime;
            
            if (isPaused)
            {
                HealthText.Text = "Paused";
                HealthIndicator.Fill = new SolidColorBrush((MediaColor)System.Windows.Media.ColorConverter.ConvertFromString("#F59E0B")!);
            }
            else if (hasErrors)
            {
                HealthText.Text = "Issues";
                HealthIndicator.Fill = new SolidColorBrush((MediaColor)System.Windows.Media.ColorConverter.ConvertFromString("#F85149")!);
            }
            else
            {
                HealthText.Text = "Healthy";
                HealthIndicator.Fill = new SolidColorBrush((MediaColor)System.Windows.Media.ColorConverter.ConvertFromString("#3FB950")!);
            }
            
            if (lastSync.HasValue)
            {
                var ago = DateTime.Now - lastSync.Value;
                if (ago.TotalMinutes < 1)
                    LastSyncText.Text = "Last sync: Just now";
                else if (ago.TotalHours < 1)
                    LastSyncText.Text = $"Last sync: {(int)ago.TotalMinutes}m ago";
                else if (ago.TotalDays < 1)
                    LastSyncText.Text = $"Last sync: {(int)ago.TotalHours}h ago";
                else
                    LastSyncText.Text = $"Last sync: {lastSync.Value:MMM d, h:mm tt}";
            }
            else
            {
                LastSyncText.Text = "Last sync: Never";
            }
        }
        catch
        {
            HealthText.Text = "Unknown";
            LastSyncText.Text = "Last sync: Unknown";
        }
    }

    private void LoadRecentActivity()
    {
        try
        {
            var recentEntries = _activityLogService.GetRecentEntries(10)
                .Select(e => new RecentActivityItem
                {
                    Type = MapActivityType(e.Type),
                    Message = e.Message,
                    TimeAgo = FormatTimeAgo(e.Timestamp)
                })
                .ToList();
            
            if (recentEntries.Any())
            {
                RecentActivityList.ItemsSource = recentEntries;
                RecentActivityList.Visibility = Visibility.Visible;
                NoActivityText.Visibility = Visibility.Collapsed;
            }
            else
            {
                RecentActivityList.Visibility = Visibility.Collapsed;
                NoActivityText.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            RecentActivityList.Visibility = Visibility.Collapsed;
            NoActivityText.Visibility = Visibility.Visible;
        }
    }

    private void LoadStorageOverview()
    {
        try
        {
            // Sync folder size
            var syncPath = _settings.SyncFolderPath;
            SyncFolderPathText.Text = syncPath;
            SyncFolderPathText.ToolTip = syncPath;
            
            if (Directory.Exists(syncPath))
            {
                var size = CalculateDirectorySize(syncPath);
                SyncFolderSizeText.Text = FileHelpers.FormatBytes(size);
            }
            else
            {
                SyncFolderSizeText.Text = "Not found";
            }
            
            // Version storage
            var versionSize = _versioningService.GetTotalVersionsSize();
            var versionCount = _versioningService.GetTotalVersionCount();
            
            VersionStorageText.Text = FileHelpers.FormatBytes(versionSize);
            VersionCountText.Text = versionCount == 1 ? "1 version stored" : $"{versionCount} versions stored";
        }
        catch
        {
            SyncFolderSizeText.Text = "N/A";
            VersionStorageText.Text = "N/A";
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
        var ago = DateTime.Now - timestamp;
        
        if (ago.TotalSeconds < 60)
            return "just now";
        if (ago.TotalMinutes < 60)
            return $"{(int)ago.TotalMinutes}m ago";
        if (ago.TotalHours < 24)
            return $"{(int)ago.TotalHours}h ago";
        if (ago.TotalDays < 7)
            return $"{(int)ago.TotalDays}d ago";
        return timestamp.ToString("MMM d");
    }

    #region Event Handlers

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        else
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ViewAllActivity_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ActivityLogDialog(_activityLogService)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    #endregion
}

/// <summary>
/// View model for recent activity items in the dashboard.
/// </summary>
public class RecentActivityItem
{
    public string Type { get; set; } = "Info";
    public string Message { get; set; } = "";
    public string TimeAgo { get; set; } = "";
}
