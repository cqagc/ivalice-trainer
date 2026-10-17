using FftTrainer.Core;
using Xunit;

namespace FftTrainer.Core.Tests;

public sealed class BonusItemWriteTests
{
    private sealed class Mem : IMemorySource
    {
        public readonly Dictionary<ulong, byte> Bytes = [];
        public IEnumerable<MemoryRegion> GetRegions(bool privateOnly = false, MemoryRegion? range = null) => [];
        public byte[] ReadBytes(ulong address, int length) => Enumerable.Range(0, length)
            .Select(i => Bytes.GetValueOrDefault(address + (ulong)i)).ToArray();
    }

    [Theory]
    [InlineData(0x101)]
    [InlineData(0x104)]
    public void BonusItemQuantityWriteSucceeds(int id)
    {
        var memory = new Mem();
        var edit = new MemoryEdit(FftLayout.Inventory + (ulong)id, 1, 0, 1);
        var result = Assert.Single(GameSession.ApplyEdits(memory,
            (address, data) => { for (var i = 0; i < data.Length; i++) memory.Bytes[address + (ulong)i] = data[i]; }, 0, [edit]));
        Assert.True(result.Error == null, result.Error);
        Assert.Equal(1, memory.ReadBytes(FftLayout.Inventory + (ulong)id, 1)[0]);
    }
}
