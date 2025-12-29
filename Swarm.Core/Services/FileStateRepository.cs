using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Swarm.Core.Abstractions;
using Swarm.Core.Models;

namespace Swarm.Core.Services;

/// <summary>
/// Thread-safe file state repository with JSON persistence.
/// </summary>
public class FileStateRepository : IFileStateRepository
{
    private const string CacheFileName = ".swarm-cache";
    private readonly ConcurrentDictionary<string, SyncedFile> _states;
    private readonly ILogger<FileStateRepository> _logger;
    private readonly string _cachePath;
    private readonly object _persistLock = new();

    public FileStateRepository(Settings settings, ILogger<FileStateRepository> logger)
    {
        _states = new ConcurrentDictionary<string, SyncedFile>(StringComparer.OrdinalIgnoreCase);
        _logger = logger;
        _cachePath = Path.Combine(settings.SyncFolderPath, CacheFileName);
    }

    public int Count => _states.Count;

    public SyncedFile? Get(string relativePath)
    {
        return _states.TryGetValue(relativePath, out var file) ? file : null;
    }

    public IReadOnlyList<SyncedFile> GetAll()
    {
        return _states.Values.ToList();
    }

    public void AddOrUpdate(SyncedFile file)
    {
        _states[file.RelativePath] = file;
    }

    public bool Remove(string relativePath)
    {
        return _states.TryRemove(relativePath, out _);
    }

    public bool Exists(string relativePath)
    {
        return _states.ContainsKey(relativePath);
    }

    public void Clear()
    {
        _states.Clear();
    }

    public IReadOnlyDictionary<string, SyncedFile> AsReadOnlyDictionary()
    {
        return _states;
    }

    public void Load()
    {
        lock (_persistLock)
        {
            try
            {
                if (!File.Exists(_cachePath))
                {
                    _logger.LogDebug("No file state cache found, starting fresh");
                    return;
                }

                var json = File.ReadAllText(_cachePath);
                var cache = JsonSerializer.Deserialize<Dictionary<string, SyncedFile>>(json);

                if (cache != null)
                {
                    foreach (var kvp in cache)
                    {
                        _states[kvp.Key] = kvp.Value;
                    }
                    _logger.LogInformation("Loaded {Count} items from file state cache", cache.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load file state cache");
            }
        }
    }

    public void SaveChanges()
    {
        lock (_persistLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_cachePath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(_states, options);
                File.WriteAllText(_cachePath, json);

                // Set file as hidden on Windows
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    var fileInfo = new FileInfo(_cachePath);
                    fileInfo.Attributes |= FileAttributes.Hidden;
                }

                _logger.LogDebug("Saved {Count} items to file state cache", _states.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save file state cache");
            }
        }
    }
}
