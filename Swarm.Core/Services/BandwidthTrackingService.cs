using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Serilog;
using Swarm.Core.Models;

namespace Swarm.Core.Services;

/// <summary>
/// Record of a speed sample at a point in time.
/// </summary>
public record SpeedSample(DateTime Timestamp, double UploadBytesPerSec, double DownloadBytesPerSec);

/// <summary>
/// Record of a completed transfer for history.
/// </summary>
public record TransferRecord(
    string Id,
    string FileName,
    long FileSize,
    TransferDirection Direction,
    string PeerName,
    DateTime StartTime,
    DateTime EndTime,
    double AverageSpeed,
    TransferStatus Status);

/// <summary>
/// Service for tracking bandwidth usage and transfer speeds with accurate byte-level tracking.
/// </summary>
public class BandwidthTrackingService : IDisposable
{
    private readonly TransferService _transferService;
    private readonly System.Timers.Timer _sampleTimer;
    private readonly object _lock = new();
    
    // Rolling window of speed samples (last 60 seconds)
    private readonly List<SpeedSample> _speedHistory = new();
    private const int MaxHistorySeconds = 60;
    
    // Track last known bytes for each transfer to compute deltas
    private readonly ConcurrentDictionary<string, long> _lastBytesTransferred = new();
    
    // Current sample accumulator (bytes transferred since last second)
    private long _currentUploadBytes;
    private long _currentDownloadBytes;
    
    // Session totals
    private long _sessionUploadTotal;
    private long _sessionDownloadTotal;
    
    // Peak speeds
    private double _peakUploadSpeed;
    private double _peakDownloadSpeed;
    
    // Active transfers
    private readonly ConcurrentDictionary<string, FileTransfer> _activeTransfers = new();
    
    // Transfer history (last 100 transfers)
    private readonly List<TransferRecord> _transferHistory = new();
    private const int MaxTransferHistory = 100;

    /// <summary>
    /// Event raised when speed data is updated (every second).
    /// </summary>
    public event Action? SpeedUpdated;

    /// <summary>
    /// Current upload speed in bytes per second.
    /// </summary>
    public double CurrentUploadSpeed { get; private set; }

    /// <summary>
    /// Current download speed in bytes per second.
    /// </summary>
    public double CurrentDownloadSpeed { get; private set; }

    /// <summary>
    /// Peak upload speed achieved this session.
    /// </summary>
    public double PeakUploadSpeed => _peakUploadSpeed;

    /// <summary>
    /// Peak download speed achieved this session.
    /// </summary>
    public double PeakDownloadSpeed => _peakDownloadSpeed;

    /// <summary>
    /// Total bytes uploaded this session.
    /// </summary>
    public long SessionUploadTotal => Interlocked.Read(ref _sessionUploadTotal);

    /// <summary>
    /// Total bytes downloaded this session.
    /// </summary>
    public long SessionDownloadTotal => Interlocked.Read(ref _sessionDownloadTotal);

    /// <summary>
    /// Speed history for graphing (last 60 seconds).
    /// </summary>
    public IReadOnlyList<SpeedSample> SpeedHistory
    {
        get
        {
            lock (_lock)
            {
                return _speedHistory.ToList();
            }
        }
    }

    /// <summary>
    /// Currently active transfers.
    /// </summary>
    public IReadOnlyList<FileTransfer> ActiveTransfers => _activeTransfers.Values.ToList();

    /// <summary>
    /// Number of active uploads.
    /// </summary>
    public int ActiveUploadCount => _activeTransfers.Values.Count(t => t.Direction == TransferDirection.Outgoing);

    /// <summary>
    /// Number of active downloads.
    /// </summary>
    public int ActiveDownloadCount => _activeTransfers.Values.Count(t => t.Direction == TransferDirection.Incoming);

    /// <summary>
    /// Completed transfer history.
    /// </summary>
    public IReadOnlyList<TransferRecord> TransferHistory
    {
        get
        {
            lock (_lock)
            {
                return _transferHistory.ToList();
            }
        }
    }

    public BandwidthTrackingService(TransferService transferService)
    {
        _transferService = transferService;
        
        // Subscribe to transfer progress events
        _transferService.TransferProgress += OnTransferProgress;
        
        // Sample speed every second
        _sampleTimer = new System.Timers.Timer(1000);
        _sampleTimer.Elapsed += OnSampleTimerElapsed;
        _sampleTimer.AutoReset = true;
        _sampleTimer.Start();
        
        Log.Information("BandwidthTrackingService initialized");
    }

    private void OnTransferProgress(FileTransfer transfer)
    {
        // Track active transfer
        _activeTransfers.AddOrUpdate(transfer.Id, transfer, (_, _) => transfer);
        
        // Calculate actual bytes delta since last update for this transfer
        var delta = CalculateBytesDelta(transfer);
        
        if (delta > 0)
        {
            if (transfer.Direction == TransferDirection.Outgoing)
            {
                Interlocked.Add(ref _currentUploadBytes, delta);
            }
            else
            {
                Interlocked.Add(ref _currentDownloadBytes, delta);
            }
        }
        
        // Check if transfer is complete
        if (transfer.Status == TransferStatus.Completed || 
            transfer.Status == TransferStatus.Failed || 
            transfer.Status == TransferStatus.Cancelled)
        {
            // Clean up tracking
            _activeTransfers.TryRemove(transfer.Id, out _);
            _lastBytesTransferred.TryRemove(transfer.Id, out _);
            
            // Add to history
            AddToHistory(transfer);
        }
    }

    private long CalculateBytesDelta(FileTransfer transfer)
    {
        var currentBytes = transfer.BytesTransferred;
        var previousBytes = _lastBytesTransferred.GetOrAdd(transfer.Id, 0);
        
        // Update last known bytes
        _lastBytesTransferred[transfer.Id] = currentBytes;
        
        // Delta is the difference (could be 0 if no new data)
        var delta = currentBytes - previousBytes;
        return delta > 0 ? delta : 0;
    }

    private void AddToHistory(FileTransfer transfer)
    {
        var duration = (transfer.EndTime ?? DateTime.Now) - transfer.StartTime;
        var avgSpeed = duration.TotalSeconds > 0 
            ? transfer.FileSize / duration.TotalSeconds 
            : 0;

        var record = new TransferRecord(
            transfer.Id,
            transfer.FileName,
            transfer.FileSize,
            transfer.Direction,
            transfer.RemotePeer?.Name ?? "Unknown",
            transfer.StartTime,
            transfer.EndTime ?? DateTime.Now,
            avgSpeed,
            transfer.Status);

        lock (_lock)
        {
            _transferHistory.Insert(0, record);
            
            // Trim history
            while (_transferHistory.Count > MaxTransferHistory)
            {
                _transferHistory.RemoveAt(_transferHistory.Count - 1);
            }
        }

        // Update session totals for completed transfers only
        if (transfer.Status == TransferStatus.Completed)
        {
            if (transfer.Direction == TransferDirection.Outgoing)
            {
                Interlocked.Add(ref _sessionUploadTotal, transfer.FileSize);
            }
            else
            {
                Interlocked.Add(ref _sessionDownloadTotal, transfer.FileSize);
            }
        }
    }

    private void OnSampleTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        // Read and reset accumulators atomically
        var uploadBytes = Interlocked.Exchange(ref _currentUploadBytes, 0);
        var downloadBytes = Interlocked.Exchange(ref _currentDownloadBytes, 0);

        // Calculate speeds (bytes per second - timer fires every 1 second)
        var uploadSpeed = (double)uploadBytes;
        var downloadSpeed = (double)downloadBytes;

        CurrentUploadSpeed = uploadSpeed;
        CurrentDownloadSpeed = downloadSpeed;

        // Update peak speeds
        if (uploadSpeed > _peakUploadSpeed)
        {
            _peakUploadSpeed = uploadSpeed;
        }
        if (downloadSpeed > _peakDownloadSpeed)
        {
            _peakDownloadSpeed = downloadSpeed;
        }

        // Add to history
        var now = DateTime.Now;
        lock (_lock)
        {
            _speedHistory.Add(new SpeedSample(now, uploadSpeed, downloadSpeed));

            // Remove old samples
            var cutoff = now.AddSeconds(-MaxHistorySeconds);
            _speedHistory.RemoveAll(s => s.Timestamp < cutoff);
        }

        // Notify listeners
        try
        {
            SpeedUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error in SpeedUpdated handler");
        }
    }

    /// <summary>
    /// Reset peak speed statistics.
    /// </summary>
    public void ResetPeakStats()
    {
        _peakUploadSpeed = 0;
        _peakDownloadSpeed = 0;
    }

    /// <summary>
    /// Get average upload speed over the last N seconds.
    /// </summary>
    public double GetAverageUploadSpeed(int seconds = 10)
    {
        lock (_lock)
        {
            var cutoff = DateTime.Now.AddSeconds(-seconds);
            var recent = _speedHistory.Where(s => s.Timestamp >= cutoff).ToList();
            return recent.Count > 0 ? recent.Average(s => s.UploadBytesPerSec) : 0;
        }
    }

    /// <summary>
    /// Get average download speed over the last N seconds.
    /// </summary>
    public double GetAverageDownloadSpeed(int seconds = 10)
    {
        lock (_lock)
        {
            var cutoff = DateTime.Now.AddSeconds(-seconds);
            var recent = _speedHistory.Where(s => s.Timestamp >= cutoff).ToList();
            return recent.Count > 0 ? recent.Average(s => s.DownloadBytesPerSec) : 0;
        }
    }

    public void Dispose()
    {
        _sampleTimer.Stop();
        _sampleTimer.Dispose();
        _transferService.TransferProgress -= OnTransferProgress;
        Log.Information("BandwidthTrackingService disposed");
    }
}
