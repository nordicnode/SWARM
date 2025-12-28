using System;
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
    private readonly VersioningService _versioningService = null!;
    private readonly ActivityLogService _activityLogService = null!;
    private readonly DiscoveryService _discoveryService = null!;
    private readonly Settings _settings = null!;

    private string _syncFolderPath = "";
    private int _totalFiles;
    private string _totalSize = "0 KB";
    private int _connectedPeers;

    private readonly System.Timers.Timer _debounceTimer = null!;
    private CancellationTokenSource? _statsCts;

    public OverviewViewModel()
    {
        // Design-time constructor
        _syncFolderPath = "C:\\Users\\Demo\\Sync";
        _totalFiles = 42;
        _totalSize = "150 MB";
        _connectedPeers = 3;
    }

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

        // Initialize debounce timer (2 seconds)
        _debounceTimer = new System.Timers.Timer(2000);
        _debounceTimer.AutoReset = false;
        _debounceTimer.Elapsed += (s, e) => RequestUpdateStats();

        // Subscribe to updates
        _discoveryService.Peers.CollectionChanged += (s, e) =>
        {
            Dispatcher.UIThread.Post(() => ConnectedPeers = _discoveryService.Peers.Count);
        };

        _syncService.FileChanged += _ => DebounceUpdateStats();

        // Initial load
        SyncFolderPath = _settings.SyncFolderPath;
        ConnectedPeers = _discoveryService.Peers.Count;
        RequestUpdateStats();
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

    private void CalculateStats(CancellationToken ct)
    {
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
        });
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

    public void Dispose()
    {
        _debounceTimer?.Stop();
        _debounceTimer?.Dispose();
        _statsCts?.Cancel();
        _statsCts?.Dispose();
    }
}
