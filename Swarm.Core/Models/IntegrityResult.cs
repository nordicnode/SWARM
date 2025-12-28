namespace Swarm.Core.Models;

/// <summary>
/// Represents the result of an integrity verification check.
/// </summary>
public class IntegrityResult
{
    /// <summary>
    /// Total number of files checked.
    /// </summary>
    public int TotalFiles { get; set; }

    /// <summary>
    /// Number of files whose hash matches the stored state.
    /// </summary>
    public int HealthyFiles { get; set; }

    /// <summary>
    /// Files where the recalculated hash differs from the stored hash (potential corruption).
    /// </summary>
    public List<IntegrityIssue> CorruptedFiles { get; set; } = [];

    /// <summary>
    /// Files that exist in the stored state but not on disk.
    /// </summary>
    public List<string> MissingFiles { get; set; } = [];

    /// <summary>
    /// Files that exist on disk but not in the stored state.
    /// </summary>
    public List<string> UnknownFiles { get; set; } = [];

    /// <summary>
    /// When the integrity check started.
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// How long the integrity check took.
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Whether the check completed successfully without errors.
    /// </summary>
    public bool CompletedSuccessfully { get; set; }

    /// <summary>
    /// Error message if the check failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Returns true if all files are healthy (no issues found).
    /// </summary>
    public bool IsAllHealthy => CorruptedFiles.Count == 0 && MissingFiles.Count == 0;
}

/// <summary>
/// Represents a file with an integrity issue.
/// </summary>
public class IntegrityIssue
{
    /// <summary>
    /// Relative path of the file.
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// The expected hash from stored state.
    /// </summary>
    public string ExpectedHash { get; set; } = string.Empty;

    /// <summary>
    /// The actual hash computed from the file on disk.
    /// </summary>
    public string ActualHash { get; set; } = string.Empty;

    /// <summary>
    /// Description of the issue.
    /// </summary>
    public string Description => $"Hash mismatch: expected {ExpectedHash[..8]}..., got {ActualHash[..8]}...";
}

