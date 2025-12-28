using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Swarm.Core.Models;

namespace Swarm.Core.Services;

/// <summary>
/// Service for managing file version history.
/// Stores versions in .swarm-versions folder within the sync folder.
/// </summary>
public class VersioningService : IDisposable
{
    private const string VERSIONS_FOLDER = ".swarm-versions";
    private const string MANIFEST_FILE = "manifest.json";
    
    private readonly Settings _settings;
    private readonly string _versionsBasePath;
    private readonly string _manifestPath;
    private readonly object _manifestLock = new();
    private List<VersionInfo> _versions = [];
    private bool _isInitialized;

    public VersioningService(Settings settings)
    {
        _settings = settings;
        _versionsBasePath = Path.Combine(settings.SyncFolderPath, VERSIONS_FOLDER);
        _manifestPath = Path.Combine(_versionsBasePath, MANIFEST_FILE);
    }

    /// <summary>
    /// Gets the settings used by this service.
    /// </summary>
    public Settings Settings => _settings;

    /// <summary>
    /// Initializes the versioning service and loads existing versions.
    /// </summary>
    public void Initialize()
    {
        if (_isInitialized) return;

        try
        {
            Directory.CreateDirectory(_versionsBasePath);
            
            // Set folder as hidden on Windows
            var dirInfo = new DirectoryInfo(_versionsBasePath);
            dirInfo.Attributes |= FileAttributes.Hidden;

            LoadManifest();
            _isInitialized = true;

            System.Diagnostics.Debug.WriteLine($"VersioningService initialized with {_versions.Count} versions");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to initialize VersioningService: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates the base path when sync folder changes.
    /// </summary>
    public void UpdateBasePath(string newSyncFolderPath)
    {
        lock (_manifestLock)
        {
            var newVersionsPath = Path.Combine(newSyncFolderPath, VERSIONS_FOLDER);
            
            // Note: We don't migrate old versions, just start fresh at the new location
            _versions.Clear();
            _isInitialized = false;
        }
    }

    /// <summary>
    /// Creates a new version of a file before it gets overwritten.
    /// </summary>
    /// <param name="relativePath">Relative path of the file within sync folder</param>
    /// <param name="sourcePath">Full path to the current file to version</param>
    /// <param name="reason">Reason for creating version (Conflict, BeforeSync, Manual)</param>
    /// <param name="sourcePeerId">ID of peer that triggered the version (optional)</param>
    /// <returns>The created VersionInfo, or null if versioning failed</returns>
    public async Task<VersionInfo?> CreateVersionAsync(string relativePath, string sourcePath, string reason, string? sourcePeerId = null)
    {
        if (!_settings.VersioningEnabled) return null;
        if (!File.Exists(sourcePath)) return null;

        Initialize();

        try
        {
            var fileInfo = new FileInfo(sourcePath);
            var versionId = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var contentHash = await ComputeFileHashAsync(sourcePath);

            // Check for duplicate (same content already versioned)
            var existingVersion = _versions.FirstOrDefault(v => 
                v.RelativePath == relativePath && v.ContentHash == contentHash);
            if (existingVersion != null)
            {
                System.Diagnostics.Debug.WriteLine($"Skipping duplicate version for {relativePath}");
                return existingVersion;
            }

            // Create version folder structure: .swarm-versions/{relative-path-sanitized}/
            var sanitizedPath = SanitizePathForStorage(relativePath);
            var versionFolder = Path.Combine(_versionsBasePath, sanitizedPath);
            Directory.CreateDirectory(versionFolder);

            // Store versioned file as {versionId}.dat
            var extension = Path.GetExtension(relativePath);
            var versionFileName = $"{versionId}{extension}";
            var versionFilePath = Path.Combine(versionFolder, versionFileName);

            await CopyFileAsync(sourcePath, versionFilePath);

            var versionInfo = new VersionInfo
            {
                RelativePath = relativePath,
                VersionId = versionId,
                CreatedAt = DateTime.UtcNow,
                FileSize = fileInfo.Length,
                ContentHash = contentHash,
                Reason = reason,
                SourcePeerId = sourcePeerId
            };

            lock (_manifestLock)
            {
                _versions.Add(versionInfo);
            }

            SaveManifest();

            // Prune old versions for this file
            await PruneVersionsForFileAsync(relativePath);

            System.Diagnostics.Debug.WriteLine($"Created version {versionId} for {relativePath} (Reason: {reason})");
            return versionInfo;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to create version for {relativePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gets all versions for a specific file.
    /// </summary>
    public IEnumerable<VersionInfo> GetVersions(string relativePath)
    {
        Initialize();
        
        lock (_manifestLock)
        {
            return _versions
                .Where(v => v.RelativePath == relativePath)
                .OrderByDescending(v => v.CreatedAt)
                .ToList();
        }
    }

    /// <summary>
    /// Gets all versions grouped by file.
    /// </summary>
    public IEnumerable<IGrouping<string, VersionInfo>> GetAllVersionsGrouped()
    {
        Initialize();
        
        lock (_manifestLock)
        {
            return _versions
                .OrderByDescending(v => v.CreatedAt)
                .GroupBy(v => v.RelativePath)
                .ToList();
        }
    }

    /// <summary>
    /// Gets all files that have versions.
    /// </summary>
    public IEnumerable<string> GetFilesWithVersions()
    {
        Initialize();
        
        lock (_manifestLock)
        {
            return _versions.Select(v => v.RelativePath).Distinct().ToList();
        }
    }

    /// <summary>
    /// Gets the total count of all versions.
    /// </summary>
    public int GetTotalVersionCount()
    {
        lock (_manifestLock)
        {
            return _versions.Count;
        }
    }

    /// <summary>
    /// Restores a version to the original file location.
    /// </summary>
    public async Task<bool> RestoreVersionAsync(VersionInfo version)
    {
        Initialize();

        try
        {
            var sanitizedPath = SanitizePathForStorage(version.RelativePath);
            var extension = Path.GetExtension(version.RelativePath);
            var versionFileName = $"{version.VersionId}{extension}";
            var versionFilePath = Path.Combine(_versionsBasePath, sanitizedPath, versionFileName);

            if (!File.Exists(versionFilePath))
            {
                System.Diagnostics.Debug.WriteLine($"Version file not found: {versionFilePath}");
                return false;
            }

            var targetPath = Path.Combine(_settings.SyncFolderPath, version.RelativePath);
            
            // Create backup of current file before restoring
            if (File.Exists(targetPath))
            {
                await CreateVersionAsync(version.RelativePath, targetPath, "BeforeRestore", null);
            }

            // Ensure target directory exists
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            await CopyFileAsync(versionFilePath, targetPath);

            System.Diagnostics.Debug.WriteLine($"Restored version {version.VersionId} of {version.RelativePath}");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to restore version: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Deletes a specific version.
    /// </summary>
    public bool DeleteVersion(VersionInfo version)
    {
        Initialize();

        try
        {
            var sanitizedPath = SanitizePathForStorage(version.RelativePath);
            var extension = Path.GetExtension(version.RelativePath);
            var versionFileName = $"{version.VersionId}{extension}";
            var versionFilePath = Path.Combine(_versionsBasePath, sanitizedPath, versionFileName);

            if (File.Exists(versionFilePath))
            {
                File.Delete(versionFilePath);
            }

            lock (_manifestLock)
            {
                _versions.RemoveAll(v => 
                    v.RelativePath == version.RelativePath && v.VersionId == version.VersionId);
            }

            SaveManifest();
            CleanupEmptyFolders();

            System.Diagnostics.Debug.WriteLine($"Deleted version {version.VersionId} of {version.RelativePath}");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to delete version: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Prunes versions for a specific file based on settings.
    /// </summary>
    private async Task PruneVersionsForFileAsync(string relativePath)
    {
        var fileVersions = _versions
            .Where(v => v.RelativePath == relativePath)
            .OrderByDescending(v => v.CreatedAt)
            .ToList();

        var toDelete = new List<VersionInfo>();

        // Remove versions exceeding max count
        if (fileVersions.Count > _settings.MaxVersionsPerFile)
        {
            toDelete.AddRange(fileVersions.Skip(_settings.MaxVersionsPerFile));
        }

        // Remove versions older than max age
        var cutoffDate = DateTime.UtcNow.AddDays(-_settings.MaxVersionAgeDays);
        toDelete.AddRange(fileVersions.Where(v => v.CreatedAt < cutoffDate));

        foreach (var version in toDelete.Distinct())
        {
            DeleteVersion(version);
        }

        if (toDelete.Count > 0)
        {
            System.Diagnostics.Debug.WriteLine($"Pruned {toDelete.Count} old versions for {relativePath}");
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Prunes all old versions based on settings.
    /// </summary>
    public async Task PruneAllVersionsAsync()
    {
        Initialize();

        var files = GetFilesWithVersions().ToList();
        foreach (var file in files)
        {
            await PruneVersionsForFileAsync(file);
        }
    }

    /// <summary>
    /// Gets the total size of all versions in bytes.
    /// </summary>
    public long GetTotalVersionsSize()
    {
        lock (_manifestLock)
        {
            return _versions.Sum(v => v.FileSize);
        }
    }

    /// <summary>
    /// Deletes all versions for a specific file.
    /// </summary>
    public void DeleteAllVersionsForFile(string relativePath)
    {
        var fileVersions = GetVersions(relativePath).ToList();
        foreach (var version in fileVersions)
        {
            DeleteVersion(version);
        }
    }

    /// <summary>
    /// Opens the versions folder in file explorer.
    /// </summary>
    public void OpenVersionsFolder()
    {
        Initialize();

        try
        {
            if (Directory.Exists(_versionsBasePath))
            {
                System.Diagnostics.Process.Start("explorer.exe", _versionsBasePath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open versions folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the full file path of a stored version.
    /// </summary>
    /// <param name="version">The version to get the file path for.</param>
    /// <returns>The file path if it exists, null otherwise.</returns>
    public string? GetVersionFilePath(VersionInfo version)
    {
        var sanitizedPath = SanitizePathForStorage(version.RelativePath);
        var extension = Path.GetExtension(version.RelativePath);
        var versionFileName = $"{version.VersionId}{extension}";
        var versionFilePath = Path.Combine(_versionsBasePath, sanitizedPath, versionFileName);

        return File.Exists(versionFilePath) ? versionFilePath : null;
    }

    /// <summary>
    /// Opens the folder containing a specific version.
    /// </summary>
    public void OpenVersionLocation(VersionInfo version)
    {
        try
        {
            var sanitizedPath = SanitizePathForStorage(version.RelativePath);
            var extension = Path.GetExtension(version.RelativePath);
            var versionFileName = $"{version.VersionId}{extension}";
            var versionFilePath = Path.Combine(_versionsBasePath, sanitizedPath, versionFileName);

            if (File.Exists(versionFilePath))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{versionFilePath}\"");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open version location: {ex.Message}");
        }
    }

    #region Private Methods

    private void LoadManifest()
    {
        lock (_manifestLock)
        {
            try
            {
                if (File.Exists(_manifestPath))
                {
                    var json = File.ReadAllText(_manifestPath);
                    _versions = JsonSerializer.Deserialize<List<VersionInfo>>(json) ?? [];
                }
                else
                {
                    _versions = [];
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load version manifest: {ex.Message}");
                _versions = [];
            }
        }
    }

    private void SaveManifest()
    {
        lock (_manifestLock)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(_versions, options);
                File.WriteAllText(_manifestPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save version manifest: {ex.Message}");
            }
        }
    }

    private static string SanitizePathForStorage(string relativePath)
    {
        // Replace path separators and invalid chars with underscores
        var sanitized = relativePath
            .Replace(Path.DirectorySeparatorChar, '_')
            .Replace(Path.AltDirectorySeparatorChar, '_');
        
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(c, '_');
        }

        return sanitized;
    }

    private static async Task<string> ComputeFileHashAsync(string filePath)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream);
        return Convert.ToHexString(hash);
    }

    private static async Task CopyFileAsync(string source, string destination)
    {
        await using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using var destStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await sourceStream.CopyToAsync(destStream);
    }

    private void CleanupEmptyFolders()
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(_versionsBasePath))
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to cleanup empty folders: {ex.Message}");
        }
    }

    #endregion

    public void Dispose()
    {
        // Save manifest on dispose
        if (_isInitialized)
        {
            SaveManifest();
        }
    }
}

