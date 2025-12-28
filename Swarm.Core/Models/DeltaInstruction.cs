namespace Swarm.Core.Models;

/// <summary>
/// Represents a delta instruction for reconstructing a file.
/// </summary>
public class DeltaInstruction
{
    /// <summary>
    /// Type of instruction: Copy from base file or Insert new data.
    /// </summary>
    public DeltaType Type { get; set; }

    /// <summary>
    /// For Copy: The block index in the base file to copy from.
    /// </summary>
    public int SourceBlockIndex { get; set; }

    /// <summary>
    /// For Insert: The raw byte data to insert.
    /// </summary>
    public byte[]? Data { get; set; }

    /// <summary>
    /// Length of this instruction's data (block size for Copy, Data.Length for Insert).
    /// </summary>
    public int Length { get; set; }
}

/// <summary>
/// Types of delta instructions.
/// </summary>
public enum DeltaType
{
    /// <summary>
    /// Copy a block from the base (old) file.
    /// </summary>
    Copy,

    /// <summary>
    /// Insert new literal data.
    /// </summary>
    Insert
}

