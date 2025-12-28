using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.Win32;

namespace Swarm.Core;

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
    
    public ShareLinkService(Settings settings)
    {
        _settings = settings;
        
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
    public ShareLink CreateShareLink(string relativePath, TimeSpan? expiration = null, string? password = null, bool requiresTrust = true)
    {
        var fullPath = Path.Combine(_settings.SyncFolderPath, relativePath);
        
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new FileNotFoundException($"File or folder not found: {relativePath}");
        }
        
        var isFile = File.Exists(fullPath);
        var fileSize = isFile ? new FileInfo(fullPath).Length : 0;
        var contentHash = isFile ? ComputeFileHash(fullPath) : "";
        
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
        
        System.Diagnostics.Debug.WriteLine($"[ShareLink] Created: {shareLink.Uri} -> {relativePath}");
        
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
                System.Diagnostics.Debug.WriteLine($"[ShareLink] Link expired: {shareId}");
                return null;
            }
            
            // Check password if required
            if (shareLink.Password != null)
            {
                if (password == null || HashPassword(password) != shareLink.Password)
                {
                    System.Diagnostics.Debug.WriteLine($"[ShareLink] Invalid password: {shareId}");
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
                System.Diagnostics.Debug.WriteLine($"[ShareLink] Revoked: {shareId}");
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
                System.Diagnostics.Debug.WriteLine($"[ShareLink] Cleaned up {expired.Count} expired links");
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
    /// Registers the swarm:// protocol handler (requires admin on Windows).
    /// </summary>
    public static bool RegisterProtocolHandler()
    {
        try
        {
            var exePath = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exePath)) return false;
            
            using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Classes\swarm");
            key?.SetValue("", "URL:Swarm Protocol");
            key?.SetValue("URL Protocol", "");
            
            using var iconKey = key?.CreateSubKey("DefaultIcon");
            iconKey?.SetValue("", $"\"{exePath}\",0");
            
            using var commandKey = key?.CreateSubKey(@"shell\open\command");
            commandKey?.SetValue("", $"\"{exePath}\" --uri \"%1\"");
            
            System.Diagnostics.Debug.WriteLine("[ShareLink] Protocol handler registered");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ShareLink] Failed to register protocol: {ex.Message}");
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
    
    private static string ComputeFileHash(string filePath)
    {
        try
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hashBytes = sha256.ComputeHash(stream);
            return Convert.ToHexString(hashBytes)[..16]; // First 16 chars
        }
        catch
        {
            return "";
        }
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
                System.Diagnostics.Debug.WriteLine($"[ShareLink] Loaded {links.Count} share links");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ShareLink] Failed to load: {ex.Message}");
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
            System.Diagnostics.Debug.WriteLine($"[ShareLink] Failed to save: {ex.Message}");
        }
    }
    
    #endregion
    
    public void Dispose()
    {
        // Cleanup on dispose
        CleanupExpiredLinks();
    }
}
