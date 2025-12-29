using System.Threading;
using System.Threading.Tasks;

namespace Swarm.Core.Abstractions;

/// <summary>
/// Service for computing file hashes.
/// </summary>
public interface IHashingService
{
    /// <summary>
    /// Computes the SHA256 hash of a file asynchronously.
    /// </summary>
    /// <param name="filePath">The full path to the file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The hexadecimal string representation of the hash.</returns>
    Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct = default);
}
