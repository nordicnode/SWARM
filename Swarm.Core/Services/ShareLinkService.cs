using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging;
using Serilog;
using Swarm.Core.Abstractions;

namespace Swarm.Core.Services;

/// <summary>
/// Represents a share link for a file or folder.
/// </summary>
public class ShareLink
{
    public string Id { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime? Expires { get; set; }
    public bool RequiresTrust { get; set; } = true;
    public string? Password { get; set; }
    public int AccessCount { get; set; }
    public long FileSize { get; set; }
    
    /// <summary>
    /// Gets the swarm:// protocol URI for this share.
    /// </summary>
    public string Uri => $"swarm://share/{Id}";
    
    /// <summary>
    /// Gets whether this link has expired.
    /// </summary>
    public bool IsExpired => Expires.HasValue && Expires.Value < DateTime.UtcNow;
}

/// <summary>
/// Service for creating and managing file sharing links.
/// Handles swarm:// protocol URLs for easy file sharing between peers.
/// </summary>
public class ShareLinkService : IDisposable
{
    private const string ShareLinksFileName = "share_links.json";
    
    private readonly Settings _settings;
    private readonly IHashingService _hashingService;
    private readonly ILogger<ShareLinkService> _logger;
    private readonly string _shareLinksPath;
    private readonly Dictionary<string, ShareLink> _shareLinks = new();
    private readonly object _lock = new();
    
    /// <summary>
    /// Raised when a share link is accessed.
    /// </summary>
    public event Action<ShareLink>? ShareLinkAccessed;
    
    /// <summary>
    /// Raised when a share request is received from a peer.
    /// </summary>
#pragma warning disable CS0067 // Event is never used - reserved for future peer-to-peer share handling
    public event Action<string, IPEndPoint>? ShareRequested;
#pragma warning restore CS0067
    
    public ShareLinkService(Settings settings, IHashingService hashingService, ILogger<ShareLinkService> logger)
    {
        _settings = settings;
        _hashingService = hashingService;
        _logger = logger;
        
        // Store share links in settings directory
        var settingsDir = Settings.IsPortableMode
            ? Path.GetDirectoryName(Environment.ProcessPath) ?? ""
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Swarm");
        
        _shareLinksPath = Path.Combine(settingsDir, ShareLinksFileName);
        
        LoadShareLinks();
    }
    
    /// <summary>
    /// Creates a share link for a file or folder.
    /// </summary>
    public async Task<ShareLink> CreateShareLinkAsync(string relativePath, TimeSpan? expiration = null, string? password = null, bool requiresTrust = true)
    {
        var fullPath = Path.Combine(_settings.SyncFolderPath, relativePath);
        
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new FileNotFoundException($"File or folder not found: {relativePath}");
        }
        
        var isFile = File.Exists(fullPath);
        var fileSize = isFile ? new FileInfo(fullPath).Length : 0;
        var contentHash = isFile ? await _hashingService.ComputeFileHashAsync(fullPath) : "";
        
        var shareLink = new ShareLink
        {
            Id = GenerateShareId(),
            RelativePath = relativePath,
            ContentHash = contentHash,
            Created = DateTime.UtcNow,
            Expires = expiration.HasValue ? DateTime.UtcNow.Add(expiration.Value) : null,
            RequiresTrust = requiresTrust,
            Password = password != null ? HashPassword(password) : null,
            FileSize = fileSize
        };
        
        lock (_lock)
        {
            _shareLinks[shareLink.Id] = shareLink;
            SaveShareLinks();
        }
        
        _logger.LogInformation($"[ShareLink] Created: {shareLink.Uri} -> {relativePath}");
        
        return shareLink;
    }
    
    /// <summary>
    /// Validates a share link and returns it if valid.
    /// </summary>
    public ShareLink? ValidateShareLink(string shareId, string? password = null)
    {
        lock (_lock)
        {
            if (!_shareLinks.TryGetValue(shareId, out var shareLink))
            {
                return null;
            }
            
            // Check expiration
            if (shareLink.IsExpired)
            {
                _logger.LogInformation($"[ShareLink] Link expired: {shareId}");
                return null;
            }
            
            // Check password if required
            if (shareLink.Password != null)
            {
                if (password == null || HashPassword(password) != shareLink.Password)
                {
                    _logger.LogWarning($"[ShareLink] Invalid password: {shareId}");
                    return null;
                }
            }
            
            // Increment access count
            shareLink.AccessCount++;
            SaveShareLinks();
            
            // Raise event
            ShareLinkAccessed?.Invoke(shareLink);
            
            return shareLink;
        }
    }
    
    /// <summary>
    /// Revokes (deletes) a share link.
    /// </summary>
    public bool RevokeShareLink(string shareId)
    {
        lock (_lock)
        {
            if (_shareLinks.Remove(shareId))
            {
                SaveShareLinks();
                _logger.LogInformation($"[ShareLink] Revoked: {shareId}");
                return true;
            }
            return false;
        }
    }
    
    /// <summary>
    /// Gets all active (non-expired) share links.
    /// </summary>
    public IEnumerable<ShareLink> GetActiveShareLinks()
    {
        lock (_lock)
        {
            return _shareLinks.Values
                .Where(sl => !sl.IsExpired)
                .OrderByDescending(sl => sl.Created)
                .ToList();
        }
    }
    
    /// <summary>
    /// Cleans up expired share links.
    /// </summary>
    public int CleanupExpiredLinks()
    {
        lock (_lock)
        {
            var expired = _shareLinks
                .Where(kvp => kvp.Value.IsExpired)
                .Select(kvp => kvp.Key)
                .ToList();
            
            foreach (var key in expired)
            {
                _shareLinks.Remove(key);
            }
            
            if (expired.Count > 0)
            {
                SaveShareLinks();
                _logger.LogInformation($"[ShareLink] Cleaned up {expired.Count} expired links");
            }
            
            return expired.Count;
        }
    }
    
    /// <summary>
    /// Parses a swarm:// URI and extracts the share ID.
    /// </summary>
    public static string? ParseSwarmUri(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return null;
            
        try
        {
            // Handle different formats: swarm://share/ABC123 or swarm:share:ABC123
            if (uri.StartsWith("swarm://share/", StringComparison.OrdinalIgnoreCase))
            {
                return uri.Substring("swarm://share/".Length).Split('?')[0];
            }
            
            if (uri.StartsWith("swarm:share:", StringComparison.OrdinalIgnoreCase))
            {
                return uri.Substring("swarm:share:".Length).Split('?')[0];
            }
            
            var parsedUri = new Uri(uri);
            if (parsedUri.Scheme.Equals("swarm", StringComparison.OrdinalIgnoreCase))
            {
                var path = parsedUri.AbsolutePath.TrimStart('/');
                if (path.StartsWith("share/", StringComparison.OrdinalIgnoreCase))
                {
                    return path.Substring("share/".Length);
                }
            }
        }
        catch
        {
            // Invalid URI format
        }
        
        return null;
    }
    
    /// <summary>
    /// Generates a clipboard-friendly share link.
    /// </summary>
    public string GetClipboardText(ShareLink shareLink)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"📁 Swarm Share: {Path.GetFileName(shareLink.RelativePath)}");
        sb.AppendLine($"🔗 {shareLink.Uri}");
        
        if (shareLink.Expires.HasValue)
        {
            var remaining = shareLink.Expires.Value - DateTime.UtcNow;
            if (remaining.TotalDays > 1)
                sb.AppendLine($"⏱️ Expires in {(int)remaining.TotalDays} days");
            else if (remaining.TotalHours > 1)
                sb.AppendLine($"⏱️ Expires in {(int)remaining.TotalHours} hours");
            else
                sb.AppendLine($"⏱️ Expires in {(int)remaining.TotalMinutes} minutes");
        }
        
        if (shareLink.Password != null)
        {
            sb.AppendLine("🔒 Password protected");
        }
        
        return sb.ToString();
    }
    
    /// <summary>
    /// Registers the swarm:// protocol handler.
    /// On Windows, this modifies the Registry. On Linux/macOS, returns false (TODO: implement).
    /// </summary>
    public static bool RegisterProtocolHandler()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return RegisterLinuxProtocolHandler();
        }

        // Only supported on Windows/Linux currently
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Log.Warning("[ShareLink] Protocol registration not supported on this platform");
            return false;
        }

        try
        {
            var exePath = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exePath)) return false;
            
            // Use dynamic invocation to avoid compile-time dependency on Windows Registry
            var registryType = Type.GetType("Microsoft.Win32.Registry, Microsoft.Win32.Registry");
            var currentUserProp = registryType?.GetProperty("CurrentUser");
            var currentUser = currentUserProp?.GetValue(null);
            
            if (currentUser == null)
            {
                Log.Error("[ShareLink] Could not access Registry");
                return false;
            }

            var createSubKeyMethod = currentUser.GetType().GetMethod("CreateSubKey", new[] { typeof(string) });
            using var key = createSubKeyMethod?.Invoke(currentUser, new object[] { @"SOFTWARE\Classes\swarm" }) as IDisposable;
            
            if (key != null)
            {
                var setValueMethod = key.GetType().GetMethod("SetValue", new[] { typeof(string), typeof(object) });
                setValueMethod?.Invoke(key, new object[] { "", "URL:Swarm Protocol" });
                setValueMethod?.Invoke(key, new object[] { "URL Protocol", "" });

                using var iconKey = createSubKeyMethod?.Invoke(key, new object[] { "DefaultIcon" }) as IDisposable;
                if (iconKey != null)
                {
                    var iconSetValue = iconKey.GetType().GetMethod("SetValue", new[] { typeof(string), typeof(object) });
                    iconSetValue?.Invoke(iconKey, new object[] { "", $"\"{exePath}\",0" });
                }

                using var commandKey = createSubKeyMethod?.Invoke(key, new object[] { @"shell\open\command" }) as IDisposable;
                if (commandKey != null)
                {
                    var cmdSetValue = commandKey.GetType().GetMethod("SetValue", new[] { typeof(string), typeof(object) });
                    cmdSetValue?.Invoke(commandKey, new object[] { "", $"\"{exePath}\" --uri \"%1\"" });
                }
            }
            
            Log.Information("[ShareLink] Protocol handler registered");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ShareLink] Failed to register protocol: {Message}", ex.Message);
            return false;
        }
    }
    
    #region Private Methods
    
    private static string GenerateShareId()
    {
        // Generate a short, URL-safe ID (8 characters)
        var bytes = new byte[6];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "");
    }
    
    private static string HashPassword(string password)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
        return Convert.ToHexString(bytes);
    }
    

    
    private void LoadShareLinks()
    {
        try
        {
            if (!File.Exists(_shareLinksPath)) return;
            
            var json = File.ReadAllText(_shareLinksPath);
            var links = JsonSerializer.Deserialize<List<ShareLink>>(json);
            
            if (links != null)
            {
                lock (_lock)
                {
                    _shareLinks.Clear();
                    foreach (var link in links)
                    {
                        _shareLinks[link.Id] = link;
                    }
                }
                _logger.LogInformation($"[ShareLink] Loaded {links.Count} share links");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[ShareLink] Failed to load: {ex.Message}");
        }
    }
    
    private void SaveShareLinks()
    {
        try
        {
            var dir = Path.GetDirectoryName(_shareLinksPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            
            var json = JsonSerializer.Serialize(
                _shareLinks.Values.ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            
            File.WriteAllText(_shareLinksPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[ShareLink] Failed to save: {ex.Message}");
        }
    }
    
    private static bool RegisterLinuxProtocolHandler()
    {
        try
        {
            var exePath = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exePath)) return false;

            // 1. Create .desktop file
            var desktopFileContent = $"""
[Desktop Entry]
Type=Application
Name=Swarm URL Handler
Exec="{exePath}" --uri %u
StartupNotify=false
MimeType=x-scheme-handler/swarm;
""";
            
            var localShare = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // Ensure we are in ~/.local/share
            if (!localShare.Contains(".local")) 
            {
                // Fallback if XDG_DATA_HOME not set strictly
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                localShare = Path.Combine(home, ".local", "share");
            }
            
            var appsDir = Path.Combine(localShare, "applications");
            Directory.CreateDirectory(appsDir);
            
            var desktopFilePath = Path.Combine(appsDir, "swarm-handler.desktop");
            File.WriteAllText(desktopFilePath, desktopFileContent);
            
            // 2. Register with xdg-mime
            System.Diagnostics.Process.Start("xdg-mime", "default swarm-handler.desktop x-scheme-handler/swarm");
            
            Log.Information("[ShareLink] Linux protocol handler registered at {Path}", desktopFilePath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ShareLink] Failed to register Linux protocol: {Message}", ex.Message);
            return false;
        }
    }

    #endregion
    
    public void Dispose()
    {
        // Cleanup on dispose
        CleanupExpiredLinks();
    }
}

