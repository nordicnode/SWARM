using System.IO;
using Swarm.Core.Services;

namespace Swarm.Core.Helpers;

/// <summary>
/// Service for parsing and matching .swarmignore patterns.
/// Similar to .gitignore, allows users to exclude files/folders from sync.
/// </summary>
public class SwarmIgnoreService
{
    private readonly Settings _settings;
    private readonly string _syncFolderPath;
    private readonly List<IgnorePattern> _patterns = new();
    private DateTime _lastLoadTime = DateTime.MinValue;
    private string? _lastIgnoreFilePath;

    private const string SWARMIGNORE_FILENAME = ".swarmignore";

    public SwarmIgnoreService(Settings settings)
    {
        _settings = settings;
        _syncFolderPath = settings.SyncFolderPath;
        ReloadPatterns();
    }

    // Constructor for testing
    public SwarmIgnoreService(string syncFolderPath)
    {
        _settings = null!; // Settings not available in this mode
        _syncFolderPath = syncFolderPath;
        ReloadPatterns();
    }

    /// <summary>
    /// Check if a relative path should be ignored based on .swarmignore patterns
    /// and user-selected excluded folders.
    /// </summary>
    /// <param name="relativePath">Path relative to sync folder root</param>
    /// <returns>True if the path should be ignored</returns>
    public bool IsIgnored(string relativePath)
    {
        // Normalize path separators to forward slashes for matching
        var normalizedPath = relativePath.Replace('\\', '/');

        // Check user-selected excluded folders first (Selective Sync)
        if (_settings != null && _settings.ExcludedFolders.Count > 0)
        {
            foreach (var excludedFolder in _settings.ExcludedFolders)
            {
                var normalizedExcluded = excludedFolder.Replace('\\', '/').TrimEnd('/');
                // Check if path starts with excluded folder or is the excluded folder itself
                if (normalizedPath.Equals(normalizedExcluded, StringComparison.OrdinalIgnoreCase) ||
                    normalizedPath.StartsWith(normalizedExcluded + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        // Reload patterns if file changed
        ReloadPatternsIfNeeded();

        if (_patterns.Count == 0)
            return false;
        
        bool isIgnored = false;

        foreach (var pattern in _patterns)
        {
            if (pattern.Matches(normalizedPath))
            {
                // Negation patterns un-ignore, regular patterns ignore
                isIgnored = !pattern.IsNegation;
            }
        }

        return isIgnored;
    }

    /// <summary>
    /// Force reload of patterns from .swarmignore file.
    /// </summary>
    public void ReloadPatterns()
    {
        _patterns.Clear();
        
        var ignoreFilePath = Path.Combine(_syncFolderPath, SWARMIGNORE_FILENAME);
        _lastIgnoreFilePath = ignoreFilePath;

        if (!File.Exists(ignoreFilePath))
        {
            _lastLoadTime = DateTime.UtcNow;
            return;
        }

        try
        {
            var lines = File.ReadAllLines(ignoreFilePath);
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                
                // Skip empty lines and comments
                if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith('#'))
                    continue;

                _patterns.Add(new IgnorePattern(trimmedLine));
            }

            _lastLoadTime = File.GetLastWriteTimeUtc(ignoreFilePath);
            System.Diagnostics.Debug.WriteLine($"[SwarmIgnore] Loaded {_patterns.Count} patterns from {ignoreFilePath}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SwarmIgnore] Failed to load patterns: {ex.Message}");
        }
    }

    private void ReloadPatternsIfNeeded()
    {
        if (string.IsNullOrEmpty(_lastIgnoreFilePath))
        {
            ReloadPatterns();
            return;
        }

        try
        {
            if (File.Exists(_lastIgnoreFilePath))
            {
                var currentWriteTime = File.GetLastWriteTimeUtc(_lastIgnoreFilePath);
                if (currentWriteTime > _lastLoadTime)
                {
                    System.Diagnostics.Debug.WriteLine("[SwarmIgnore] Detected .swarmignore change, reloading patterns");
                    ReloadPatterns();
                }
            }
            else if (_patterns.Count > 0)
            {
                // File was deleted, clear patterns
                System.Diagnostics.Debug.WriteLine("[SwarmIgnore] .swarmignore deleted, clearing patterns");
                _patterns.Clear();
                _lastLoadTime = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SwarmIgnore] Error checking for pattern updates: {ex.Message}");
        }
    }

    /// <summary>
    /// Represents a single ignore pattern from .swarmignore
    /// </summary>
    private class IgnorePattern
    {
        public string Pattern { get; }
        public bool IsNegation { get; }
        public bool IsDirectoryOnly { get; }
        public bool HasPathSeparator { get; }

        private readonly string _matchPattern;

        public IgnorePattern(string rawPattern)
        {
            // Check for negation prefix
            if (rawPattern.StartsWith('!'))
            {
                IsNegation = true;
                rawPattern = rawPattern[1..];
            }

            // Check for directory-only pattern (trailing /)
            if (rawPattern.EndsWith('/'))
            {
                IsDirectoryOnly = true;
                rawPattern = rawPattern.TrimEnd('/');
            }

            // Check if pattern contains path separator (should match from root)
            HasPathSeparator = rawPattern.Contains('/');

            Pattern = rawPattern;
            _matchPattern = ConvertToGlobPattern(rawPattern);
        }

        public bool Matches(string path)
        {
            // For directory-only patterns, we need to check if path is or is under a directory
            // For now, we check if the path matches or starts with pattern + /
            
            if (HasPathSeparator)
            {
                // Pattern has path separator - match from root
                return MatchesGlob(path, _matchPattern) || 
                       (IsDirectoryOnly && path.StartsWith(Pattern + "/", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                // Pattern has no separator - match against any path segment
                var fileName = Path.GetFileName(path);
                if (MatchesGlob(fileName, _matchPattern))
                    return true;

                // Also check if any directory in path matches
                var segments = path.Split('/');
                foreach (var segment in segments)
                {
                    if (MatchesGlob(segment, _matchPattern))
                        return true;
                }

                return false;
            }
        }

        private static string ConvertToGlobPattern(string pattern)
        {
            // Convert gitignore-style pattern to a regex-compatible pattern
            // For simplicity, we use FileSystemName for basic matching
            // Handle ** (match across directories) by keeping it for special processing
            return pattern;
        }

        private static bool MatchesGlob(string input, string pattern)
        {
            // Use .NET's built-in glob matching
            // FileSystemName.MatchesSimpleExpression handles * and ?
            try
            {
                // Handle ** pattern (match any path depth)
                if (pattern.Contains("**"))
                {
                    // Split pattern on ** and check if input matches the segments
                    var parts = pattern.Split(new[] { "**" }, StringSplitOptions.None);
                    if (parts.Length == 2)
                    {
                        var prefix = parts[0].TrimEnd('/');
                        var suffix = parts[1].TrimStart('/');

                        // Check prefix
                        if (!string.IsNullOrEmpty(prefix) && !input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            return false;

                        // Check suffix
                        if (!string.IsNullOrEmpty(suffix) && !input.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                            return false;

                        return true;
                    }
                }

                // Use FileSystemName for simple pattern matching with * and ?
                return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, input, ignoreCase: true);
            }
            catch
            {
                // Fallback to simple comparison
                return string.Equals(input, pattern, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}

