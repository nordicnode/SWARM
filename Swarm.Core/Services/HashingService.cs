using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Swarm.Core.Abstractions;

namespace Swarm.Core.Services;

public class HashingService : IHashingService
{
    private const int BUFFER_SIZE = 81920; // 80KB buffer

    public async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct = default)
    {
        return await Swarm.Core.Helpers.FileHelpers.ExecuteWithRetryAsync(async () =>
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BUFFER_SIZE, useAsync: true);
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(stream, ct);
            return Convert.ToHexString(hash);
        });
    }
}
