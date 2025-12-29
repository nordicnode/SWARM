using Swarm.Core.Models;
using Swarm.Core.Services;
using Xunit;

namespace Swarm.Core.Tests;

/// <summary>
/// Unit tests for DeltaSyncService.
/// Tests block signature generation and delta instruction creation.
/// </summary>
public class DeltaSyncServiceTests : IDisposable
{
    private readonly string _testFolder;

    public DeltaSyncServiceTests()
    {
        _testFolder = Path.Combine(Path.GetTempPath(), $"SwarmDeltaTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testFolder);
    }

    [Fact]
    public async Task ComputeBlockSignatures_ReturnsSignatures_ForValidFile()
    {
        // Arrange
        var testFile = Path.Combine(_testFolder, "test.bin");
        var data = new byte[32768]; // 32KB - multiple blocks
        new Random(42).NextBytes(data);
        await File.WriteAllBytesAsync(testFile, data);
        
        // Act
        var signatures = await DeltaSyncService.ComputeBlockSignaturesAsync(testFile);
        
        // Assert
        Assert.NotEmpty(signatures);
    }

    [Fact]
    public async Task ComputeBlockSignatures_ReturnsEmpty_ForEmptyFile()
    {
        // Arrange
        var testFile = Path.Combine(_testFolder, "empty.bin");
        await File.WriteAllBytesAsync(testFile, Array.Empty<byte>());
        
        // Act
        var signatures = await DeltaSyncService.ComputeBlockSignaturesAsync(testFile);
        
        // Assert
        Assert.Empty(signatures);
    }

    [Fact]
    public async Task ComputeBlockSignatures_ReturnsSingleBlock_ForSmallFile()
    {
        // Arrange
        var testFile = Path.Combine(_testFolder, "small.bin");
        var data = new byte[100]; // Less than block size
        new Random(42).NextBytes(data);
        await File.WriteAllBytesAsync(testFile, data);
        
        // Act
        var signatures = await DeltaSyncService.ComputeBlockSignaturesAsync(testFile);
        
        // Assert
        Assert.Single(signatures);
    }

    [Fact]
    public async Task BlockSignature_ContainsRequiredFields()
    {
        // Arrange
        var testFile = Path.Combine(_testFolder, "fields.bin");
        var data = new byte[8192];
        new Random(42).NextBytes(data);
        await File.WriteAllBytesAsync(testFile, data);
        
        // Act
        var signatures = await DeltaSyncService.ComputeBlockSignaturesAsync(testFile);
        
        // Assert
        var sig = signatures.First();
        Assert.True(sig.BlockIndex >= 0);
        Assert.True(sig.WeakChecksum != 0);
        Assert.NotNull(sig.StrongChecksum);
        Assert.NotEmpty(sig.StrongChecksum);
    }

    [Fact]
    public async Task ComputeDelta_ReturnsCopyInstruction_ForIdenticalFiles()
    {
        // Arrange
        var sourceFile = Path.Combine(_testFolder, "source.bin");
        var targetFile = Path.Combine(_testFolder, "target.bin");
        var data = new byte[16384];
        new Random(42).NextBytes(data);
        await File.WriteAllBytesAsync(sourceFile, data);
        await File.WriteAllBytesAsync(targetFile, data);
        
        var signatures = await DeltaSyncService.ComputeBlockSignaturesAsync(sourceFile);
        
        // Act
        var delta = await DeltaSyncService.ComputeDeltaAsync(targetFile, signatures);
        
        // Assert
        Assert.NotEmpty(delta);
        Assert.True(delta.All(d => d.Type == DeltaType.Copy));
    }

    [Fact]
    public async Task ComputeDelta_ReturnsInsertInstruction_ForNewContent()
    {
        // Arrange
        var sourceFile = Path.Combine(_testFolder, "source_new.bin");
        var targetFile = Path.Combine(_testFolder, "target_new.bin");
        
        var sourceData = new byte[8192];
        new Random(42).NextBytes(sourceData);
        await File.WriteAllBytesAsync(sourceFile, sourceData);
        
        var targetData = new byte[8192];
        new Random(123).NextBytes(targetData); // Different random seed = different content
        await File.WriteAllBytesAsync(targetFile, targetData);
        
        var signatures = await DeltaSyncService.ComputeBlockSignaturesAsync(sourceFile);
        
        // Act
        var delta = await DeltaSyncService.ComputeDeltaAsync(targetFile, signatures);
        
        // Assert
        Assert.NotEmpty(delta);
        Assert.Contains(delta, d => d.Type == DeltaType.Insert);
    }

    [Fact]
    public async Task ApplyDelta_ReconstructsFile()
    {
        // Arrange
        var baseFile = Path.Combine(_testFolder, "base.bin");
        var newFile = Path.Combine(_testFolder, "new.bin");
        var outputFile = Path.Combine(_testFolder, "output.bin");
        
        var baseData = new byte[8192];
        new Random(42).NextBytes(baseData);
        await File.WriteAllBytesAsync(baseFile, baseData);
        
        // Create a modified file (same base with some changes)
        var newData = (byte[])baseData.Clone();
        newData[100] = 0xFF; // Modify some bytes
        newData[101] = 0xFE;
        await File.WriteAllBytesAsync(newFile, newData);
        
        var signatures = await DeltaSyncService.ComputeBlockSignaturesAsync(baseFile);
        var delta = await DeltaSyncService.ComputeDeltaAsync(newFile, signatures);
        
        // Act
        await DeltaSyncService.ApplyDeltaAsync(baseFile, outputFile, delta);
        
        // Assert
        var output = await File.ReadAllBytesAsync(outputFile);
        Assert.Equal(newData, output);
    }

    [Fact]
    public void EstimateDeltaSize_ReturnsPositive_ForInstructions()
    {
        // Arrange
        var instructions = new List<DeltaInstruction>
        {
            new DeltaInstruction { Type = DeltaType.Copy, SourceBlockIndex = 0, Length = 1024 },
            new DeltaInstruction { Type = DeltaType.Insert, Data = new byte[100], Length = 100 }
        };
        
        // Act
        var size = DeltaSyncService.EstimateDeltaSize(instructions);
        
        // Assert
        Assert.True(size > 0);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testFolder))
            {
                Directory.Delete(_testFolder, recursive: true);
            }
        }
        catch { /* Ignore cleanup errors */ }
    }
}
