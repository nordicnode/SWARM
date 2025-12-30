using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Swarm.Core.Helpers;
using Swarm.Core.Services;

namespace Swarm.Avalonia.ViewModels;

/// <summary>
/// ViewModel for the Sync Statistics Dashboard view.
/// </summary>
public class StatsViewModel : ViewModelBase, IDisposable
{
    private readonly SyncStatisticsService? _statsService;
    
    // Summary stats
    private string _totalFilesSynced = "0";
    private string _totalUploaded = "0 B";
    private string _totalDownloaded = "0 B";
    private string _totalConflicts = "0";
    private string _trackingSince = "Today";
    
    // Today's stats
    private string _todayFilesSynced = "0";
    private string _todayUploaded = "0 B";
    private string _todayDownloaded = "0 B";
    private string _todayConflicts = "0";
    
    // Chart data
    private readonly ObservableCollection<DateTimePoint> _filesSyncedData = new();
    private readonly ObservableCollection<DateTimePoint> _uploadBandwidthData = new();
    private readonly ObservableCollection<DateTimePoint> _downloadBandwidthData = new();
    private readonly ObservableCollection<DateTimePoint> _conflictsData = new();
    
    // Peer stats
    private ObservableCollection<PeerStatsItem> _peerStats = new();

    public StatsViewModel()
    {
        // Design-time constructor with sample data
        _totalFilesSynced = "1,234";
        _totalUploaded = "5.2 GB";
        _totalDownloaded = "12.8 GB";
        _totalConflicts = "23";
        _trackingSince = "30 days ago";
        _todayFilesSynced = "45";
        _todayUploaded = "250 MB";
        _todayDownloaded = "1.2 GB";
        _todayConflicts = "2";
        
        // Sample chart data
        var random = new Random();
        var baseDate = DateTime.Now.AddDays(-30);
        for (int i = 0; i < 30; i++)
        {
            var date = baseDate.AddDays(i);
            _filesSyncedData.Add(new DateTimePoint(date, random.Next(10, 100)));
            _uploadBandwidthData.Add(new DateTimePoint(date, random.NextDouble() * 500));
            _downloadBandwidthData.Add(new DateTimePoint(date, random.NextDouble() * 1000));
            _conflictsData.Add(new DateTimePoint(date, random.Next(0, 5)));
        }
        
        // Sample peer stats
        _peerStats = new ObservableCollection<PeerStatsItem>
        {
            new() { PeerName = "Laptop", Uploaded = "1.2 GB", Downloaded = "2.5 GB", FilesExchanged = 456 },
            new() { PeerName = "Desktop", Uploaded = "2.8 GB", Downloaded = "5.1 GB", FilesExchanged = 789 },
            new() { PeerName = "Server", Uploaded = "1.5 GB", Downloaded = "4.2 GB", FilesExchanged = 234 },
        };
    }

    public StatsViewModel(SyncStatisticsService statsService)
    {
        _statsService = statsService;
        _statsService.StatsUpdated += OnStatsUpdated;
        
        RefreshData();
    }

    #region Summary Properties

    public string TotalFilesSynced
    {
        get => _totalFilesSynced;
        set => SetProperty(ref _totalFilesSynced, value);
    }

    public string TotalUploaded
    {
        get => _totalUploaded;
        set => SetProperty(ref _totalUploaded, value);
    }

    public string TotalDownloaded
    {
        get => _totalDownloaded;
        set => SetProperty(ref _totalDownloaded, value);
    }

    public string TotalConflicts
    {
        get => _totalConflicts;
        set => SetProperty(ref _totalConflicts, value);
    }

    public string TrackingSince
    {
        get => _trackingSince;
        set => SetProperty(ref _trackingSince, value);
    }

    public string TodayFilesSynced
    {
        get => _todayFilesSynced;
        set => SetProperty(ref _todayFilesSynced, value);
    }

    public string TodayUploaded
    {
        get => _todayUploaded;
        set => SetProperty(ref _todayUploaded, value);
    }

    public string TodayDownloaded
    {
        get => _todayDownloaded;
        set => SetProperty(ref _todayDownloaded, value);
    }

    public string TodayConflicts
    {
        get => _todayConflicts;
        set => SetProperty(ref _todayConflicts, value);
    }

    #endregion

    #region Peer Stats

    public ObservableCollection<PeerStatsItem> PeerStats
    {
        get => _peerStats;
        set => SetProperty(ref _peerStats, value);
    }

    public bool HasPeerStats => PeerStats.Count > 0;

    #endregion

    #region Charts

    public ISeries[] FilesSyncedSeries => new ISeries[]
    {
        new ColumnSeries<DateTimePoint>
        {
            Values = _filesSyncedData,
            Name = "Files Synced",
            Fill = new SolidColorPaint(new SKColor(45, 212, 191)), // Teal
            MaxBarWidth = 20
        }
    };

    public ISeries[] BandwidthSeries => new ISeries[]
    {
        new LineSeries<DateTimePoint>
        {
            Values = _uploadBandwidthData,
            Name = "↑ Upload",
            Stroke = new SolidColorPaint(new SKColor(251, 191, 36)) { StrokeThickness = 2 },
            Fill = new SolidColorPaint(new SKColor(251, 191, 36, 40)),
            GeometrySize = 0,
            LineSmoothness = 0.5
        },
        new LineSeries<DateTimePoint>
        {
            Values = _downloadBandwidthData,
            Name = "↓ Download",
            Stroke = new SolidColorPaint(new SKColor(45, 212, 191)) { StrokeThickness = 2 },
            Fill = new SolidColorPaint(new SKColor(45, 212, 191, 40)),
            GeometrySize = 0,
            LineSmoothness = 0.5
        }
    };

    public ISeries[] ConflictsSeries => new ISeries[]
    {
        new ColumnSeries<DateTimePoint>
        {
            Values = _conflictsData,
            Name = "Conflicts",
            Fill = new SolidColorPaint(new SKColor(239, 68, 68)), // Red
            MaxBarWidth = 15
        }
    };

    public Axis[] DateXAxes => new Axis[]
    {
        new DateTimeAxis(TimeSpan.FromDays(1), date => date.ToString("MMM d"))
        {
            LabelsRotation = -45,
            LabelsPaint = new SolidColorPaint(new SKColor(156, 163, 175)),
            SeparatorsPaint = new SolidColorPaint(new SKColor(75, 85, 99, 50)),
            TextSize = 10
        }
    };

    public Axis[] CountYAxes => new Axis[]
    {
        new Axis
        {
            LabelsPaint = new SolidColorPaint(new SKColor(156, 163, 175)),
            SeparatorsPaint = new SolidColorPaint(new SKColor(75, 85, 99, 50)),
            TextSize = 11,
            MinLimit = 0
        }
    };

    public Axis[] BandwidthYAxes => new Axis[]
    {
        new Axis
        {
            LabelsPaint = new SolidColorPaint(new SKColor(156, 163, 175)),
            SeparatorsPaint = new SolidColorPaint(new SKColor(75, 85, 99, 50)),
            TextSize = 11,
            MinLimit = 0,
            Labeler = value => FileHelpers.FormatBytes((long)(value * 1024 * 1024))
        }
    };

    #endregion

    #region Methods

    private void OnStatsUpdated()
    {
        Dispatcher.UIThread.Post(RefreshData);
    }

    public void RefreshData()
    {
        if (_statsService == null) return;

        // Get totals
        var (uploaded, downloaded, filesSynced, conflicts, firstDate) = _statsService.GetTotalStats();
        TotalFilesSynced = filesSynced.ToString("N0");
        TotalUploaded = FileHelpers.FormatBytes(uploaded);
        TotalDownloaded = FileHelpers.FormatBytes(downloaded);
        TotalConflicts = conflicts.ToString("N0");

        var daysSince = (DateTime.Now - firstDate).Days;
        TrackingSince = daysSince == 0 ? "Today" : daysSince == 1 ? "Yesterday" : $"{daysSince} days ago";

        // Get today's stats
        var today = _statsService.GetTodayStats();
        TodayFilesSynced = today.FilesSynced.ToString("N0");
        TodayUploaded = FileHelpers.FormatBytes(today.BytesUploaded);
        TodayDownloaded = FileHelpers.FormatBytes(today.BytesDownloaded);
        TodayConflicts = today.ConflictsResolved.ToString("N0");

        // Get daily stats for charts
        var dailyStats = _statsService.GetDailyStats(30);
        
        _filesSyncedData.Clear();
        _uploadBandwidthData.Clear();
        _downloadBandwidthData.Clear();
        _conflictsData.Clear();

        foreach (var day in dailyStats)
        {
            var date = day.Date.ToDateTime(TimeOnly.MinValue);
            _filesSyncedData.Add(new DateTimePoint(date, day.FilesSynced));
            _uploadBandwidthData.Add(new DateTimePoint(date, (double)day.BytesUploaded / (1024 * 1024)));
            _downloadBandwidthData.Add(new DateTimePoint(date, (double)day.BytesDownloaded / (1024 * 1024)));
            _conflictsData.Add(new DateTimePoint(date, day.ConflictsResolved));
        }

        // Get peer stats
        var peerStats = _statsService.GetPeerBandwidthStats();
        PeerStats = new ObservableCollection<PeerStatsItem>(
            peerStats.Select(p => new PeerStatsItem
            {
                PeerName = p.PeerName,
                Uploaded = FileHelpers.FormatBytes(p.BytesUploaded),
                Downloaded = FileHelpers.FormatBytes(p.BytesDownloaded),
                FilesExchanged = p.FilesExchanged,
                LastActivity = p.LastActivity
            }));
        
        OnPropertyChanged(nameof(HasPeerStats));
        OnPropertyChanged(nameof(FilesSyncedSeries));
        OnPropertyChanged(nameof(BandwidthSeries));
        OnPropertyChanged(nameof(ConflictsSeries));
    }

    #endregion

    public void Dispose()
    {
        if (_statsService != null)
        {
            _statsService.StatsUpdated -= OnStatsUpdated;
        }
    }
}

/// <summary>
/// Item for displaying peer bandwidth statistics.
/// </summary>
public class PeerStatsItem
{
    public string PeerName { get; set; } = "";
    public string Uploaded { get; set; } = "";
    public string Downloaded { get; set; } = "";
    public int FilesExchanged { get; set; }
    public DateTime LastActivity { get; set; }
    public string LastActivityDisplay => LastActivity == default 
        ? "Never" 
        : (DateTime.Now - LastActivity).TotalHours < 24 
            ? "Today" 
            : LastActivity.ToString("MMM d");
}
