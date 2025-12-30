using System;
using System.Collections.ObjectModel;
using System.Timers;
using Avalonia.Threading;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Swarm.Core.Helpers;
using Swarm.Core.Models;
using Swarm.Core.Services;

namespace Swarm.Avalonia.ViewModels;

/// <summary>
/// ViewModel for the Bandwidth Dashboard view.
/// </summary>
public class BandwidthViewModel : ViewModelBase, IDisposable
{
    private readonly BandwidthTrackingService? _bandwidthService;
    private readonly System.Timers.Timer _refreshTimer;

    // Speed chart data
    private readonly ObservableCollection<ObservableValue> _uploadSpeeds = new();
    private readonly ObservableCollection<ObservableValue> _downloadSpeeds = new();

    // Properties
    private string _currentUploadSpeed = "0 B/s";
    private string _currentDownloadSpeed = "0 B/s";
    private string _sessionUploadTotal = "0 B";
    private string _sessionDownloadTotal = "0 B";
    private string _peakUploadSpeed = "0 B/s";
    private string _peakDownloadSpeed = "0 B/s";
    private string _avgUploadSpeed = "0 B/s";
    private string _avgDownloadSpeed = "0 B/s";
    private int _activeUploadCount;
    private int _activeDownloadCount;
    private ObservableCollection<ActiveTransferItem> _activeTransfers = new();
    private ObservableCollection<TransferHistoryItem> _transferHistory = new();

    public BandwidthViewModel()
    {
        // Design-time constructor
        _refreshTimer = new System.Timers.Timer(1000) { AutoReset = true };
        
        // Add sample data for design
        for (int i = 0; i < 30; i++)
        {
            _uploadSpeeds.Add(new ObservableValue(Random.Shared.NextDouble() * 1024 * 1024));
            _downloadSpeeds.Add(new ObservableValue(Random.Shared.NextDouble() * 2 * 1024 * 1024));
        }
        
        _currentUploadSpeed = "1.5 MB/s";
        _currentDownloadSpeed = "3.2 MB/s";
        _sessionUploadTotal = "150 MB";
        _sessionDownloadTotal = "420 MB";
        _peakUploadSpeed = "2.1 MB/s";
        _peakDownloadSpeed = "4.5 MB/s";
    }

    public BandwidthViewModel(BandwidthTrackingService bandwidthService)
    {
        _bandwidthService = bandwidthService;
        
        // Initialize with empty data points
        for (int i = 0; i < 60; i++)
        {
            _uploadSpeeds.Add(new ObservableValue(0));
            _downloadSpeeds.Add(new ObservableValue(0));
        }
        
        // Subscribe to updates
        _bandwidthService.SpeedUpdated += OnSpeedUpdated;
        
        // Refresh timer for active transfers
        _refreshTimer = new System.Timers.Timer(1000) { AutoReset = true };
        _refreshTimer.Elapsed += (s, e) => RefreshActiveTransfers();
        _refreshTimer.Start();
        
        // Initial load
        RefreshAll();
    }

    #region Chart Series

    public ISeries[] SpeedSeries => new ISeries[]
    {
        new LineSeries<ObservableValue>
        {
            Values = _uploadSpeeds,
            Name = "Upload",
            Stroke = new SolidColorPaint(new SKColor(251, 191, 36)) { StrokeThickness = 2 }, // #fbbf24
            Fill = new SolidColorPaint(new SKColor(251, 191, 36, 40)),
            GeometrySize = 0,
            LineSmoothness = 0.5
        },
        new LineSeries<ObservableValue>
        {
            Values = _downloadSpeeds,
            Name = "Download",
            Stroke = new SolidColorPaint(new SKColor(45, 212, 191)) { StrokeThickness = 2 }, // #2dd4bf
            Fill = new SolidColorPaint(new SKColor(45, 212, 191, 40)),
            GeometrySize = 0,
            LineSmoothness = 0.5
        }
    };

    public Axis[] XAxes => new Axis[]
    {
        new Axis
        {
            IsVisible = false,
            ShowSeparatorLines = false
        }
    };

    public Axis[] YAxes => new Axis[]
    {
        new Axis
        {
            Labeler = value => FileHelpers.FormatBytes((long)value) + "/s",
            MinLimit = 0,
            LabelsPaint = new SolidColorPaint(new SKColor(156, 163, 175)),
            SeparatorsPaint = new SolidColorPaint(new SKColor(55, 65, 81)) { StrokeThickness = 1 }
        }
    };

    #endregion

    #region Properties

    public string CurrentUploadSpeed
    {
        get => _currentUploadSpeed;
        set => SetProperty(ref _currentUploadSpeed, value);
    }

    public string CurrentDownloadSpeed
    {
        get => _currentDownloadSpeed;
        set => SetProperty(ref _currentDownloadSpeed, value);
    }

    public string SessionUploadTotal
    {
        get => _sessionUploadTotal;
        set => SetProperty(ref _sessionUploadTotal, value);
    }

    public string SessionDownloadTotal
    {
        get => _sessionDownloadTotal;
        set => SetProperty(ref _sessionDownloadTotal, value);
    }

    public string PeakUploadSpeed
    {
        get => _peakUploadSpeed;
        set => SetProperty(ref _peakUploadSpeed, value);
    }

    public string PeakDownloadSpeed
    {
        get => _peakDownloadSpeed;
        set => SetProperty(ref _peakDownloadSpeed, value);
    }

    public string AvgUploadSpeed
    {
        get => _avgUploadSpeed;
        set => SetProperty(ref _avgUploadSpeed, value);
    }

    public string AvgDownloadSpeed
    {
        get => _avgDownloadSpeed;
        set => SetProperty(ref _avgDownloadSpeed, value);
    }

    public int ActiveUploadCount
    {
        get => _activeUploadCount;
        set => SetProperty(ref _activeUploadCount, value);
    }

    public int ActiveDownloadCount
    {
        get => _activeDownloadCount;
        set => SetProperty(ref _activeDownloadCount, value);
    }

    public ObservableCollection<ActiveTransferItem> ActiveTransfers
    {
        get => _activeTransfers;
        set => SetProperty(ref _activeTransfers, value);
    }

    public ObservableCollection<TransferHistoryItem> TransferHistory
    {
        get => _transferHistory;
        set => SetProperty(ref _transferHistory, value);
    }

    public bool HasActiveTransfers => ActiveTransfers.Count > 0;
    public bool HasTransferHistory => TransferHistory.Count > 0;

    #endregion

    #region Methods

    private void OnSpeedUpdated()
    {
        Dispatcher.UIThread.Post(RefreshAll);
    }

    private void RefreshAll()
    {
        if (_bandwidthService == null) return;

        // Update current speeds
        CurrentUploadSpeed = FileHelpers.FormatBytes((long)_bandwidthService.CurrentUploadSpeed) + "/s";
        CurrentDownloadSpeed = FileHelpers.FormatBytes((long)_bandwidthService.CurrentDownloadSpeed) + "/s";

        // Update session totals
        SessionUploadTotal = FileHelpers.FormatBytes(_bandwidthService.SessionUploadTotal);
        SessionDownloadTotal = FileHelpers.FormatBytes(_bandwidthService.SessionDownloadTotal);

        // Update peak speeds
        PeakUploadSpeed = FileHelpers.FormatBytes((long)_bandwidthService.PeakUploadSpeed) + "/s";
        PeakDownloadSpeed = FileHelpers.FormatBytes((long)_bandwidthService.PeakDownloadSpeed) + "/s";

        // Update average speeds (10-second rolling average)
        AvgUploadSpeed = FileHelpers.FormatBytes((long)_bandwidthService.GetAverageUploadSpeed()) + "/s";
        AvgDownloadSpeed = FileHelpers.FormatBytes((long)_bandwidthService.GetAverageDownloadSpeed()) + "/s";

        // Update active transfer counts
        ActiveUploadCount = _bandwidthService.ActiveUploadCount;
        ActiveDownloadCount = _bandwidthService.ActiveDownloadCount;

        // Update chart data - shift and add new values
        if (_uploadSpeeds.Count > 0 && _downloadSpeeds.Count > 0)
        {
            _uploadSpeeds.Add(new ObservableValue(_bandwidthService.CurrentUploadSpeed));
            _downloadSpeeds.Add(new ObservableValue(_bandwidthService.CurrentDownloadSpeed));
            
            if (_uploadSpeeds.Count > 60) _uploadSpeeds.RemoveAt(0);
            if (_downloadSpeeds.Count > 60) _downloadSpeeds.RemoveAt(0);
        }

        // Update transfer history
        RefreshTransferHistory();
        RefreshActiveTransfers();
    }

    private void RefreshActiveTransfers()
    {
        if (_bandwidthService == null) return;

        Dispatcher.UIThread.Post(() =>
        {
            var active = _bandwidthService.ActiveTransfers;
            ActiveTransfers.Clear();
            foreach (var t in active)
            {
                ActiveTransfers.Add(new ActiveTransferItem(t));
            }
            OnPropertyChanged(nameof(HasActiveTransfers));
        });
    }

    private void RefreshTransferHistory()
    {
        if (_bandwidthService == null) return;

        var history = _bandwidthService.TransferHistory;
        TransferHistory.Clear();
        foreach (var t in history.Take(20))
        {
            TransferHistory.Add(new TransferHistoryItem(t));
        }
        OnPropertyChanged(nameof(HasTransferHistory));
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        if (_bandwidthService != null)
        {
            _bandwidthService.SpeedUpdated -= OnSpeedUpdated;
        }
    }

    #endregion
}

/// <summary>
/// View model item for active transfers.
/// </summary>
public class ActiveTransferItem
{
    public string FileName { get; }
    public string Direction { get; }
    public string DirectionIcon { get; }
    public double Progress { get; }
    public string ProgressText { get; }
    public string Speed { get; }
    public string PeerName { get; }

    public ActiveTransferItem(FileTransfer transfer)
    {
        FileName = transfer.FileName;
        Direction = transfer.Direction == TransferDirection.Outgoing ? "Upload" : "Download";
        DirectionIcon = transfer.Direction == TransferDirection.Outgoing ? "↑" : "↓";
        Progress = transfer.Progress;
        ProgressText = $"{transfer.Progress:F0}%";
        Speed = transfer.SpeedDisplay;
        PeerName = transfer.RemotePeer?.Name ?? "Unknown";
    }
}

/// <summary>
/// View model item for transfer history.
/// </summary>
public class TransferHistoryItem
{
    public string FileName { get; }
    public string Direction { get; }
    public string DirectionIcon { get; }
    public string FileSize { get; }
    public string PeerName { get; }
    public string TimeAgo { get; }
    public string AverageSpeed { get; }

    public TransferHistoryItem(TransferRecord record)
    {
        FileName = record.FileName;
        Direction = record.Direction == TransferDirection.Outgoing ? "Upload" : "Download";
        DirectionIcon = record.Direction == TransferDirection.Outgoing ? "↑" : "↓";
        FileSize = FileHelpers.FormatBytes(record.FileSize);
        PeerName = record.PeerName;
        TimeAgo = FormatTimeAgo(record.EndTime);
        AverageSpeed = FileHelpers.FormatBytes((long)record.AverageSpeed) + "/s";
    }

    private static string FormatTimeAgo(DateTime time)
    {
        var span = DateTime.Now - time;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        return $"{(int)span.TotalDays}d ago";
    }
}
