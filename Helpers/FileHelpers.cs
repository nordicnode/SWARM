using System.IO;

namespace Swarm.Helpers;

public static class FileHelpers
{
    /// <summary>
    /// Formats a byte count into a human-readable string (e.g., 1.2 MB).
    /// </summary>
    public static string FormatBytes(double bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        int order = 0;
        while (bytes >= 1024 && order < sizes.Length - 1)
        {
            order++;
            bytes /= 1024;
        }
        return $"{bytes:0.##} {sizes[order]}";
    }

    /// <summary>
    /// Normalizes a file path for consistent comparison (absolute, trimmed, lowercase).
    /// </summary>
    public static string NormalizePath(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();
    }

    /// <summary>
    /// Executes an async file operation with retry logic for handling transient locks.
    /// </summary>
    public static async Task ExecuteWithRetryAsync(Func<Task> action, int maxRetries = 3, int delayMs = 500)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                await action();
                return;
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                await Task.Delay(delayMs);
            }
        }
        // If we exhausted retries, let the last exception bubble up
    }

    /// <summary>
    /// Executes an async file operation with retry logic for handling transient locks.
    /// </summary>
    public static async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, int maxRetries = 3, int delayMs = 500)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                return await action();
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                await Task.Delay(delayMs);
            }
        }
        throw new IOException($"Operation failed after {maxRetries} retries");
    }
}
