namespace Swarm.Models;

/// <summary>
/// Represents the signature of a single block for delta sync.
/// </summary>
public class BlockSignature
{
    /// <summary>
    /// Index of this block in the file (0-based).
    /// </summary>
    public int BlockIndex { get; set; }

    /// <summary>
    /// Weak rolling checksum (Adler-32) for fast comparison.
    /// </summary>
    public int WeakChecksum { get; set; }

    /// <summary>
    /// Strong checksum (SHA256 hex) for definitive match verification.
    /// </summary>
    public string StrongChecksum { get; set; } = string.Empty;
}
