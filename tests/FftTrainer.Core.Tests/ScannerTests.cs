using FftTrainer.Core;
using Xunit;

namespace FftTrainer.Core.Tests;

public sealed class ScannerTests
{
    private sealed class ArrayMemory(byte[] data) : IMemorySource
    {
        public byte[] Data { get; } = data;

        public IEnumerable<MemoryRegion> GetRegions(bool privateOnly = false, MemoryRegion? range = null)
        {
            yield return range ?? new MemoryRegion(0x1000, (ulong)Data.Length);
        }

        public byte[] ReadBytes(ulong address, int length)
        {
            if (address < 0x1000 || address - 0x1000 + (ulong)length > (ulong)Data.Length)
                throw new IOException("Outside test memory.");
            return Data.AsSpan((int)(address - 0x1000), length).ToArray();
        }
    }

    [Fact]
    public void PatternParsesWildcardsAndOverlappingMatches()
    {
        var pattern = new AobPattern("48 8b ? ?? 89");
        Assert.Equal(5, pattern.Length);
        Assert.Equal(1, pattern.FindFirst(new byte[] { 0, 0x48, 0x8B, 1, 2, 0x89 }));
        Assert.Equal(-1, pattern.FindFirst(new byte[] { 0x48, 0x8B }));
        Assert.Equal(new[] { 0, 1 }, new AobPattern("AA AA").FindAll(new byte[] { 0xAA, 0xAA, 0xAA }));
        Assert.Throws<FormatException>(() => new AobPattern(""));
        Assert.Throws<FormatException>(() => new AobPattern("GG"));
        Assert.Throws<FormatException>(() => new AobPattern("A"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void PatternFindsBoundaryAndFinalMatchWithoutDuplicates(int blockSize)
    {
        var memory = new ArrayMemory(new byte[] { 0, 0, 0, 0x48, 0x8B, 5, 0x48, 0x8B, 6 });
        Assert.Equal(new ulong[] { 0x1003, 0x1006 },
            AobScanner.Scan(memory, new AobPattern("48 8B ??"), memory.GetRegions(), blockSize));
    }

    [Fact]
    public void RipRelativeSupportsNegativeDisplacement()
    {
        var memory = new ArrayMemory(new byte[] { 0x90, 0xFC, 0xFF, 0xFF, 0xFF });
        Assert.Equal(0x1001UL, AobScanner.ResolveRipRelative(memory, 0x1000, 1, 5));
    }

    [Theory]
    [InlineData("eq", 20, 10, 20, true)]
    [InlineData("ne", 20, 10, 20, false)]
    [InlineData("changed", 20, 10, 0, true)]
    [InlineData("unchanged", 20, 20, 0, true)]
    [InlineData("increased", 20, 10, 0, true)]
    [InlineData("decreased", 10, 20, 0, true)]
    [InlineData("gt", 20, 0, 10, true)]
    [InlineData("lt", 10, 0, 20, true)]
    public void ComparisonsCoverAllOperations(string op, int current, int previous, int target, bool expected)
    {
        foreach (var type in Enum.GetValues<ValueTypeCode>())
        {
            Assert.Equal(expected, Values.Compare(type, Values.Parse(current.ToString(), type),
                Values.Parse(previous.ToString(), type), op, Values.Parse(target.ToString(), type)));
        }
    }

    [Fact]
    public void SignedUnsignedAndFloatComparisonsKeepPrecision()
    {
        Assert.True(Values.Compare(ValueTypeCode.U64, ulong.MaxValue, ulong.MaxValue - 1, "increased"));
        Assert.True(Values.Compare(ValueTypeCode.I32, Values.Parse("-1", ValueTypeCode.I32), 0, "decreased"));
        Assert.True(Values.Compare(ValueTypeCode.F32, Values.Parse("1.25", ValueTypeCode.F32),
            Values.Parse("1.5", ValueTypeCode.F32), "decreased"));
        Assert.False(Values.Compare(ValueTypeCode.F32, Values.Parse("NaN", ValueTypeCode.F32), 0, "gt", 0));
        Assert.True(Values.Compare(ValueTypeCode.F32, Values.Parse("NaN", ValueTypeCode.F32), 0, "ne", 0));
        Assert.Throws<OverflowException>(() => Values.Parse("256", ValueTypeCode.U8));
        Assert.Throws<ArgumentException>(() => Values.Compare(ValueTypeCode.U8, 1, 1, "invalid"));
    }

    [Fact]
    public void SequenceChecksMixedTypesOffsetsAndBoundaries()
    {
        var memory = new ArrayMemory(new byte[] { 0, 0, 0, 70, 65, 0, 0, 70, 64, 0 });
        var fields = new[] { SequenceField.Parse("u8:70@0"), SequenceField.Parse("u16:65@1") };
        Assert.Equal(new ulong[] { 0x1003 }, SequenceScanner.Scan(memory, fields, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => SequenceField.Parse("u8:1@-1"));
        Assert.Throws<FormatException>(() => SequenceField.Parse("bad"));
    }

    [Theory]
    [InlineData(ValueTypeCode.U8, "70")]
    [InlineData(ValueTypeCode.U16, "500")]
    [InlineData(ValueTypeCode.U32, "100000")]
    [InlineData(ValueTypeCode.I32, "-123")]
    [InlineData(ValueTypeCode.U64, "18446744073709551615")]
    [InlineData(ValueTypeCode.F32, "1.25")]
    public void ValueScansCrossBlocksAndPersistAcrossCalls(ValueTypeCode type, string text)
    {
        var path = Path.GetTempFileName();
        try
        {
            var value = Values.Parse(text, type);
            var data = new byte[32];
            Values.ToBytes(value, type).CopyTo(data, 3);
            Values.ToBytes(value, type).CopyTo(data, 19);
            var memory = new ArrayMemory(data);
            Assert.Equal(2, new ValueScanner(memory, 4).Scan(path, type, value));
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                Assert.Equal(type, ResultFile.ReadHeader(reader));
                Assert.Equal(new[] { new ScanResult(0x1003, value), new ScanResult(0x1013, value) },
                    ResultFile.Read(reader).ToArray());
            }
            Values.ToBytes(0, type).CopyTo(data, 19);
            Assert.Equal(1, new ValueScanner(memory, 4).Next(path, "changed"));
            using var changed = new BinaryReader(File.OpenRead(path));
            Assert.Equal(type, ResultFile.ReadHeader(changed));
            Assert.Equal(new ScanResult(0x1013, 0), Assert.Single(ResultFile.Read(changed)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("eq", 20, 1)]
    [InlineData("ne", 20, 2)]
    [InlineData("changed", 0, 2)]
    [InlineData("unchanged", 0, 1)]
    [InlineData("increased", 0, 1)]
    [InlineData("decreased", 0, 1)]
    [InlineData("gt", 10, 1)]
    [InlineData("lt", 10, 1)]
    public void NextFiltersAndReplacesSession(string op, int target, int expected)
    {
        var path = Path.GetTempFileName();
        try
        {
            var memory = new ArrayMemory(new byte[] { 10, 10, 10 });
            var scanner = new ValueScanner(memory, 2);
            Assert.Equal(3, scanner.Scan(path, ValueTypeCode.U8, 10));
            memory.Data[0] = 5;
            memory.Data[2] = 20;
            Assert.Equal(expected, scanner.Next(path, op, (ulong)target));
            Assert.Equal(expected, scanner.Next(path, "unchanged"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResultFileRejectsTruncation()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        writer.Write(0x31544646U);
        writer.Write((byte)ValueTypeCode.U8);
        writer.Write((byte)0);
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        Assert.Throws<InvalidDataException>(() => ResultFile.ReadHeader(reader));
    }

    [Fact]
    public void MillionResultsAreReadAsAStream()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var writer = ResultFile.Create(path, ValueTypeCode.U64))
            {
                for (ulong i = 0; i < 1_000_000; i++)
                    ResultFile.Write(writer, new ScanResult(i, ulong.MaxValue - i));
            }
            using var reader = new BinaryReader(File.OpenRead(path));
            Assert.Equal(ValueTypeCode.U64, ResultFile.ReadHeader(reader));
            ulong count = 0;
            foreach (var result in ResultFile.Read(reader))
            {
                Assert.Equal(count, result.Address);
                Assert.Equal(ulong.MaxValue - count, result.Previous);
                count++;
            }
            Assert.Equal(1_000_000UL, count);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
