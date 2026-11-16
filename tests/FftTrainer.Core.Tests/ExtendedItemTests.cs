using System.Buffers.Binary;
using FftTrainer.Core;
using Xunit;

namespace FftTrainer.Core.Tests;

// Nexus report (2026-11-15): units wearing Akademy Blade / Beret / Tunic or Ring of
// Aptitude could not edit level or stats. Records below are the real bytes from the
// supported FFT_enhanced.exe (SHA-256 9372…B5D5), read through the game's own lookups.
public sealed partial class TrainerTests
{
    private static readonly (int Id, byte[] Common)[] ExtendedCommons =
    [
        (0x101, [0x0D, 0x0C, 0x08, 0x82, 0x14, 0x03, 0x00, 0x51, 0x0A, 0x00, 0x01, 0x00]),
        (0x102, [0x03, 0x5F, 0x05, 0x12, 0x10, 0x15, 0x00, 0x52, 0x0A, 0x00, 0x01, 0x00]),
        (0x103, [0x0D, 0xB2, 0x06, 0x12, 0x1F, 0x18, 0x00, 0x53, 0x0A, 0x00, 0x01, 0x00]),
        (0x104, [0x0C, 0x8F, 0x33, 0x0A, 0x0B, 0x1C, 0x00, 0x50, 0x0A, 0x00, 0x01, 0x00])
    ];

    private static TestMemory AkademyMemory()
    {
        var memory = StatMemory();
        var map = PeFileMap.Read(memory, 0);
        void Table(uint file, byte[] bytes) => memory.Seed(map.ToRva(file, bytes.Length), bytes);
        foreach (var (id, common) in ExtendedCommons)
            Table(0x67E910u + (uint)id * 12, common);
        Table(0x6722FC, [20, 0, 20, 0]);
        var blade = new byte[26];
        blade[2] = 1;
        Table(0x80E2A0 + 0x51 * 26, blade);
        // Deliberately poison the main armor table at the extended items' common[4]
        // indexes: extended armor must not be read from there.
        Table(0x80DEB0 + 0x10 * 2, [99, 99]);
        Table(0x80DEB0 + 0x1F * 2, [99, 99]);
        return memory;
    }

    private static byte[] AkademyUnit()
    {
        var data = StatUnit(0);
        int[] equipment = [0x102, 0x103, 0x104, 0x101, 0xFF, 0xFF, 0xFF];
        for (var i = 0; i < equipment.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(FftLayout.Equipment + i * 2), (ushort)equipment[i]);
        return data;
    }

    [Fact]
    public void AkademySetAndRingOfAptitudeKeepStatsAvailable()
    {
        var memory = AkademyMemory();
        var rules = StatTables.TryLoad(memory, 0)!.ReadUnit(AkademyUnit());
        Assert.NotNull(rules);
        // Ramza base HP 36 (41 - clothing 5; no headgear in this fixture) + Beret 20 + Tunic 20;
        // Akademy Blade gives Speed +1; no boots, so Move is the job's 4.
        Assert.Equal(new[] { 76, 15, 7, 5, 5, 4, 3 },
            UnitStats.Calculate([503494, 247480, 98304, 81920, 81920], rules!));
        Assert.Equal(0, memory.WriteCount);
    }

    [Fact]
    public void AkademyUnitCanLevelUpAndEditStats()
    {
        var memory = AkademyMemory();
        var data = AkademyUnit();
        var unit = new UnitViewModel(new UnitSnapshot(0, data, StatTables.TryLoad(memory, 0)!.ReadUnit(data)),
            new FftLayout(), TestText.Legacy());
        Assert.True(unit.Gain1.CanExecute(null));
        Assert.Equal("76", unit.Stats[0].Text);
        unit.Stats[0].Text = "100";
        Assert.Null(unit.Stats[0].Raw.Error);
        Assert.True(unit.IsDirty);
    }

    [Fact]
    public void ItemIdsFromOx105AreIgnoredLikeTheGameDoes()
    {
        var memory = StatMemory();
        var data = StatUnit(0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(FftLayout.Equipment + 2 * 2), 0x105);
        var rules = StatTables.TryLoad(memory, 0)!.ReadUnit(data);
        Assert.NotNull(rules);
        // Slot 0 normally wears boots (Move +1) in the accessory slot; 0x105 adds nothing.
        Assert.Equal(new[] { 41, 15, 6, 5, 5, 4, 3 },
            UnitStats.Calculate([503494, 247480, 98304, 81920, 81920], rules!));
    }
}
