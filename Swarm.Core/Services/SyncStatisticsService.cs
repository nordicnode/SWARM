using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Swarm.Core.Services;

/// <summary>
/// Statistics for a single day.
/// </summary>
public class DailyStats
{
    public DateOnly Date { get; set; }
    public int FilesSynced { get; set; }
    public long BytesUploaded { get; set; }
    public long BytesDownloaded { get; set; }
    public int ConflictsResolved { get; set; }
    public int FilesAdded { get; set; }
    public int FilesModified { get; set; }
    public int FilesDeleted { get; set; }
}

/// <summary>
/// Bandwidth usage for a specific peer.
/// </summary>
public class PeerBandwidthStats
{
    public string PeerId { get; set; } = "";
    public string PeerName { get; set; } = "";
    public long BytesUploaded { get; set; }
    public long BytesDownloaded { get; set; }
    public int FilesExchanged { get; set; }
    public DateTime LastActivity { get; set; }
}

/// <summary>
/// Persisted statistics data.
/// </summary>
public class SyncStatisticsData
{
    public List<DailyStats> DailyStats { get; set; } = new();
    public Dictionary<string, PeerBandwidthStats> PeerStats { get; set; } = new();
    public DateTime FirstRecordedDate { get; set; } = DateTime.Now;
    public long TotalBytesUploaded { get; set; }
    public long TotalBytesDownloaded { get; set; }
    public int TotalFilesSynced { get; set; }
    public int TotalConflictsResolved { get; set; }
}

/// <summary>
/// Service for tracking and persisting sync statistics over time.
/// </summary>
public class SyncStatisticsService : IDisposable
{
    private readonly ILogger<SyncStatisticsService> _logger;
    private readonly string _statsFilePath;
    private SyncStatisticsData _data;
    private readonly object _lock = new();
    private readonly System.Timers.Timer _saveTimer;
    private bool _isDirty;
    
    // Events for real-time updates
    public event Action? StatsUpdated;

    public SyncStatisticsService(ILogger<SyncStatisticsService> logger, string appDataPath)
    {
        _logger = logger;
        _statsFilePath = Path.Combine(appDataPath, "sync_statistics.json");
        _data = LoadStats();
        
        // Auto-save every 5 minutes
        _saveTimer = new System.Timers.Timer(300000) { AutoReset = true };
        _saveTimer.Elapsed += (s, e) => SaveIfDirty();
        _saveTimer.Start();
    }
    
    #region Public Statistics APIs
    
    /// <summary>
    /// Gets the daily statistics for the last N days.
    /// </summary>
    public List<DailyStats> GetDailyStats(int days = 30)
    {
        lock (_lock)
        {
            var cutoff = DateOnly.FromDateTime(DateTime.Now.AddDays(-days));
            return _data.DailyStats
                .Where(d => d.Date >= cutoff)
                .OrderBy(d => d.Date)
                .ToList();
        }
    }
    
    /// <summary>
    /// Gets bandwidth statistics per peer.
    /// </summary>
    public List<PeerBandwidthStats> GetPeerBandwidthStats()
    {
        lock (_lock)
        {
            return _data.PeerStats.Values
                .OrderByDescending(p => p.BytesUploaded + p.BytesDownloaded)
                .ToList();
        }
    }
    
    /// <summary>
    /// Gets total lifetime statistics.
    /// </summary>
    public (long uploaded, long downloaded, int filesSynced, int conflicts, DateTime firstDate) GetTotalStats()
    {
        lock (_lock)
        {
            return (_data.TotalBytesUploaded, 
                    _data.TotalBytesDownloaded, 
                    _data.TotalFilesSynced, 
                    _data.TotalConflictsResolved,
                    _data.FirstRecordedDate);
        }
    }
    
    /// <summary>
    /// Gets today's statistics.
    /// </summary>
    public DailyStats GetTodayStats()
    {
        lock (_lock)
        {
            return GetOrCreateTodayStats();
        }
    }
    
    #endregion
    
    #region Recording Methods
    
    /// <summary>
    /// Records a file sync event.
    /// </summary>
    public void RecordFileSync(string peerId, string peerName, bool isUpload, long bytesTransferred)
    {
        lock (_lock)
        {
            var today = GetOrCreateTodayStats();
            today.FilesSynced++;
            
            if (isUpload)
            {
                today.BytesUploaded += bytesTransferred;
                _data.TotalBytesUploaded += bytesTransferred;
            }
            else
            {
                today.BytesDownloaded += bytesTransferred;
                _data.TotalBytesDownloaded += bytesTransferred;
            }
            
            _data.TotalFilesSynced++;
            
            // Update peer stats
            UpdatePeerStats(peerId, peerName, isUpload, bytesTransferred);
            
            _isDirty = true;
        }
        
        StatsUpdated?.Invoke();
    }
    
    /// <summary>
    /// Records a file addition.
    /// </summary>
    public void RecordFileAdded()
    {
        lock (_lock)
        {
            var today = GetOrCreateTodayStats();
            today.FilesAdded++;
            _isDirty = true;
        }
        StatsUpdated?.Invoke();
    }
    
    /// <summary>
    /// Records a file modification.
    /// </summary>
    public void RecordFileModified()
    {
        lock (_lock)
        {
            var today = GetOrCreateTodayStats();
            today.FilesModified++;
            _isDirty = true;
        }
        StatsUpdated?.Invoke();
    }
    
    /// <summary>
    /// Records a file deletion.
    /// </summary>
    public void RecordFileDeleted()
    {
        lock (_lock)
        {
            var today = GetOrCreateTodayStats();
            today.FilesDeleted++;
            _isDirty = true;
        }
        StatsUpdated?.Invoke();
    }
    
    /// <summary>
    /// Records a conflict resolution.
    /// </summary>
    public void RecordConflictResolved()
    {
        lock (_lock)
        {
            var today = GetOrCreateTodayStats();
            today.ConflictsResolved++;
            _data.TotalConflictsResolved++;
            _isDirty = true;
        }
        StatsUpdated?.Invoke();
    }
    
    /// <summary>
    /// Records bandwidth transfer for a peer (can be called incrementally).
    /// </summary>
    public void RecordBandwidth(string peerId, string peerName, bool isUpload, long bytes)
    {
        lock (_lock)
        {
            UpdatePeerStats(peerId, peerName, isUpload, bytes, incrementFiles: false);
            _isDirty = true;
        }
    }
    
    #endregion
    
    #region Private Methods
    
    private DailyStats GetOrCreateTodayStats()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var stats = _data.DailyStats.FirstOrDefault(d => d.Date == today);
        
        if (stats == null)
        {
            stats = new DailyStats { Date = today };
            _data.DailyStats.Add(stats);
            
            // Keep only last 365 days
            if (_data.DailyStats.Count > 365)
            {
                _data.DailyStats = _data.DailyStats
                    .OrderByDescending(d => d.Date)
                    .Take(365)
                    .ToList();
            }
        }
        
        return stats;
    }
    
    private void UpdatePeerStats(string peerId, string peerName, bool isUpload, long bytes, bool incrementFiles = true)
    {
        if (!_data.PeerStats.TryGetValue(peerId, out var peerStats))
        {
            peerStats = new PeerBandwidthStats
            {
                PeerId = peerId,
                PeerName = peerName
            };
            _data.PeerStats[peerId] = peerStats;
        }
        
        peerStats.PeerName = peerName; // Update name in case it changed
        peerStats.LastActivity = DateTime.Now;
        
        if (isUpload)
            peerStats.BytesUploaded += bytes;
        else
            peerStats.BytesDownloaded += bytes;
        
        if (incrementFiles)
            peerStats.FilesExchanged++;
    }
    
    private SyncStatisticsData LoadStats()
    {
        try
        {
            if (File.Exists(_statsFilePath))
            {
                var json = File.ReadAllText(_statsFilePath);
                var data = JsonSerializer.Deserialize<SyncStatisticsData>(json);
                if (data != null)
                {
                    _logger.LogInformation("Loaded sync statistics with {Days} days of data", data.DailyStats.Count);
                    return data;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load sync statistics");
        }
        
        return new SyncStatisticsData { FirstRecordedDate = DateTime.Now };
    }
    
    private void SaveIfDirty()
    {
        if (!_isDirty) return;
        SaveStats();
    }
    
    public void SaveStats()
    {
        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_statsFilePath, json);
                _isDirty = false;
                _logger.LogDebug("Saved sync statistics");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save sync statistics");
            }
        }
    }
    
    #endregion
    
    public void Dispose()
    {
        _saveTimer.Stop();
        _saveTimer.Dispose();
        SaveStats();
    }
}
