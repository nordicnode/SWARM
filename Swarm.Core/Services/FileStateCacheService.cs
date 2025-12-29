using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Swarm.Core.Models;

namespace Swarm.Core.Services;

public class FileStateCacheService
{
    private const string CacheFileName = ".swarm-cache";
    private readonly Settings _settings;
    private readonly ILogger<FileStateCacheService> _logger;
    private readonly string _cachePath;
    private readonly object _lock = new();

    public FileStateCacheService(Settings settings, ILogger<FileStateCacheService> logger)
    {
        _settings = settings;
        _logger = logger;
        _cachePath = Path.Combine(settings.SyncFolderPath, CacheFileName);
    }

    public Dictionary<string, SyncedFile> LoadCache()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_cachePath))
                {
                    return new Dictionary<string, SyncedFile>(StringComparer.OrdinalIgnoreCase);
                }

                var json = File.ReadAllText(_cachePath);
                var cache = JsonSerializer.Deserialize<Dictionary<string, SyncedFile>>(json);
                _logger.LogInformation($"Loaded {cache?.Count ?? 0} items from file state cache");
                
                // Use case-insensitive dictionary
                if (cache != null)
                {
                    return new Dictionary<string, SyncedFile>(cache, StringComparer.OrdinalIgnoreCase);
                }

                return new Dictionary<string, SyncedFile>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load file state cache");
                return new Dictionary<string, SyncedFile>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public void SaveCache(IDictionary<string, SyncedFile> fileStates)
    {
        lock (_lock)
        {
            try
            {
                // Ensure directory exists
                var dir = Path.GetDirectoryName(_cachePath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(fileStates, options);
                
                File.WriteAllText(_cachePath, json);
                
                // Set file as hidden on Windows
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    var fileInfo = new FileInfo(_cachePath);
                    fileInfo.Attributes |= FileAttributes.Hidden;
                }
                
                _logger.LogDebug($"Saved {fileStates.Count} items to file state cache");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save file state cache");
            }
        }
    }
}
