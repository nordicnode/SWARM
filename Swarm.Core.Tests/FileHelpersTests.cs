using Swarm.Core.Helpers;

namespace Swarm.Core.Tests;

/// <summary>
/// Unit tests for the FileHelpers utility class.
/// </summary>
public class FileHelpersTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741824, "1 GB")]
    public void FormatBytes_ReturnsCorrectFormat(long bytes, string expected)
    {
        var result = FileHelpers.FormatBytes(bytes);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatBytes_LargeFile_ReturnsGB()
    {
        // 5GB
        var result = FileHelpers.FormatBytes(5L * 1024 * 1024 * 1024);
        Assert.Equal("5 GB", result);
    }

    [Fact]
    public void FormatBytes_VeryLargeFile_ReturnsTB()
    {
        // 2TB
        var result = FileHelpers.FormatBytes(2.0 * 1024 * 1024 * 1024 * 1024);
        Assert.Equal("2 TB", result);
    }

    [Theory]
    [InlineData(@"C:\Users\Test\Sync", @"C:\Users\Test\Sync")]
    [InlineData(@"C:\Users\Test\Sync\", @"c:\users\test\sync")]
    [InlineData(@"C:/Users/Test/Sync", @"c:\users\test\sync")]
    public void NormalizePath_ReturnsConsistentPath(string input, string expected)
    {
        var result = FileHelpers.NormalizePath(input);
        Assert.Equal(expected.ToLowerInvariant(), result.ToLowerInvariant());
    }

    [Fact]
    public void NormalizePath_RemovesTrailingSlashes()
    {
        var input = @"C:\Users\Test\Sync\";
        var result = FileHelpers.NormalizePath(input);
        Assert.False(result.EndsWith("\\") || result.EndsWith("/"));
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_SucceedsOnFirstTry()
    {
        var callCount = 0;
        
        await FileHelpers.ExecuteWithRetryAsync(async () =>
        {
            callCount++;
            await Task.CompletedTask;
        });

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_WithResult_ReturnsValue()
    {
        var result = await FileHelpers.ExecuteWithRetryAsync(async () =>
        {
            await Task.CompletedTask;
            return 42;
        });

        Assert.Equal(42, result);
    }
}
