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
}
