using System.IO;
using System.Security.Cryptography;
using Swarm.Models;

namespace Swarm.Core;

/// <summary>
/// Service for computing block signatures and deltas for efficient file synchronization.
/// Uses a rolling checksum (Adler-32) for fast matching and SHA256 for verification.
/// </summary>
public static class DeltaSyncService
{
    /// <summary>
    /// Block size for delta sync (64KB).
    /// </summary>
    public const int BlockSize = ProtocolConstants.DELTA_BLOCK_SIZE;

    /// <summary>
    /// Minimum file size to use delta sync. Smaller files are faster to re-transfer entirely.
    /// </summary>
    public const long DeltaThreshold = 1024 * 1024; // 1MB

    /// <summary>
    /// Computes block signatures for a file.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <returns>List of block signatures.</returns>
    public static async Task<List<BlockSignature>> ComputeBlockSignaturesAsync(string filePath)
    {
        var signatures = new List<BlockSignature>();
        var buffer = new byte[BlockSize];

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BlockSize, useAsync: true);
        using var sha256 = SHA256.Create();

        int blockIndex = 0;
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory())) > 0)
        {
            var blockData = buffer.AsSpan(0, bytesRead);

            signatures.Add(new BlockSignature
            {
                BlockIndex = blockIndex,
                WeakChecksum = ComputeAdler32(blockData),
                StrongChecksum = Convert.ToHexString(sha256.ComputeHash(buffer, 0, bytesRead))
            });

            blockIndex++;
        }

        return signatures;
    }

    /// <summary>
    /// Computes a delta between a new file and a set of base signatures.
    /// </summary>
    /// <param name="newFilePath">Path to the new (modified) file.</param>
    /// <param name="baseSignatures">Signatures from the base (old) file.</param>
    /// <returns>List of delta instructions to transform base into new.</returns>
    public static async Task<List<DeltaInstruction>> ComputeDeltaAsync(string newFilePath, List<BlockSignature> baseSignatures)
    {
        var instructions = new List<DeltaInstruction>();

        // Build lookup tables for fast matching
        var weakToSignatures = new Dictionary<int, List<BlockSignature>>();
        foreach (var sig in baseSignatures)
        {
            if (!weakToSignatures.TryGetValue(sig.WeakChecksum, out var list))
            {
                list = [];
                weakToSignatures[sig.WeakChecksum] = list;
            }
            list.Add(sig);
        }

        var buffer = new byte[BlockSize];
        var pendingData = new List<byte>();

        await using var stream = new FileStream(newFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, BlockSize, useAsync: true);
        using var sha256 = SHA256.Create();

        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory())) > 0)
        {
            var blockData = buffer.AsSpan(0, bytesRead);
            var weakChecksum = ComputeAdler32(blockData);

            BlockSignature? matchedSig = null;

            // Check if weak checksum matches any base block
            if (weakToSignatures.TryGetValue(weakChecksum, out var candidates))
            {
                var strongChecksum = Convert.ToHexString(sha256.ComputeHash(buffer, 0, bytesRead));

                foreach (var candidate in candidates)
                {
                    if (candidate.StrongChecksum == strongChecksum)
                    {
                        matchedSig = candidate;
                        break;
                    }
                }
            }

            if (matchedSig != null)
            {
                // Flush any pending literal data first
                if (pendingData.Count > 0)
                {
                    instructions.Add(new DeltaInstruction
                    {
                        Type = DeltaType.Insert,
                        Data = [.. pendingData],
                        Length = pendingData.Count
                    });
                    pendingData.Clear();
                }

                // Add copy instruction
                instructions.Add(new DeltaInstruction
                {
                    Type = DeltaType.Copy,
                    SourceBlockIndex = matchedSig.BlockIndex,
                    Length = bytesRead
                });
            }
            else
            {
                // No match - add to pending literal data
                pendingData.AddRange(blockData.ToArray());
            }
        }

        // Flush remaining pending data
        if (pendingData.Count > 0)
        {
            instructions.Add(new DeltaInstruction
            {
                Type = DeltaType.Insert,
                Data = [.. pendingData],
                Length = pendingData.Count
            });
        }

        return instructions;
    }

    /// <summary>
    /// Applies delta instructions to reconstruct a file.
    /// </summary>
    /// <param name="baseFilePath">Path to the base (old) file.</param>
    /// <param name="targetFilePath">Path to write the reconstructed file.</param>
    /// <param name="instructions">Delta instructions.</param>
    public static async Task ApplyDeltaAsync(string baseFilePath, string targetFilePath, List<DeltaInstruction> instructions)
    {
        await using var baseStream = new FileStream(baseFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, BlockSize, useAsync: true);
        await using var targetStream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.None, BlockSize, useAsync: true);

        var buffer = new byte[BlockSize];

        foreach (var instruction in instructions)
        {
            switch (instruction.Type)
            {
                case DeltaType.Copy:
                    // Seek to the source block in base file
                    baseStream.Position = (long)instruction.SourceBlockIndex * BlockSize;
                    var bytesToRead = instruction.Length;
                    var bytesRead = await baseStream.ReadAsync(buffer.AsMemory(0, bytesToRead));
                    await targetStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                    break;

                case DeltaType.Insert:
                    if (instruction.Data != null)
                    {
                        await targetStream.WriteAsync(instruction.Data.AsMemory());
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Computes an Adler-32 rolling checksum for fast block matching.
    /// </summary>
    public static int ComputeAdler32(ReadOnlySpan<byte> data)
    {
        const int MOD_ADLER = 65521;
        int a = 1, b = 0;

        foreach (byte c in data)
        {
            a = (a + c) % MOD_ADLER;
            b = (b + a) % MOD_ADLER;
        }

        return (b << 16) | a;
    }

    /// <summary>
    /// Estimates the size of a delta in bytes (for progress reporting).
    /// </summary>
    public static long EstimateDeltaSize(List<DeltaInstruction> instructions)
    {
        long size = 0;
        foreach (var instruction in instructions)
        {
            // Copy instructions are just references (small overhead)
            // Insert instructions carry actual data
            size += instruction.Type == DeltaType.Insert ? instruction.Length : 8;
        }
        return size;
    }
}
