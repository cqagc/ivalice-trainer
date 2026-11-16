using FftTrainer.Core;
using Xunit;

namespace FftTrainer.Core.Tests;

// 2026-11-16 live roster: special characters carry their own character code, so gender
// must come from the flag byte (roster +0x04, battle +0x06), not from code 0x81.
public sealed partial class TrainerTests
{
    [Theory]
    [InlineData(0x80, 0x90, UnitGender.Male)]     // generic male (live: 0x90)
    [InlineData(0x81, 0x50, UnitGender.Female)]   // generic female (live: 0x50)
    [InlineData(0x2A, 0x50, UnitGender.Female)]   // Meliadoul (live)
    [InlineData(0x0D, 0x90, UnitGender.Male)]     // special male (live)
    [InlineData(0x82, 0x20, UnitGender.Monster)]  // monster (live)
    [InlineData(0x82, 0x30, UnitGender.Monster)]  // special monster (live)
    [InlineData(0x81, 0x00, UnitGender.Female)]   // no flags: fall back to character code
    [InlineData(0x2A, 0x00, UnitGender.Male)]
    public void GenderComesFromFlagsBeforeCharacterCode(byte character, byte flags, UnitGender expected) =>
        Assert.Equal(expected, GameData.Gender(flags, character));

    [Fact]
    public void SpecialFemaleUnitShowsFemaleAndGetsDancerNotBard()
    {
        var (memory, model) = Create();
        memory.Seed(FftLayout.Roster + FftLayout.Character, [0x2A]);
        memory.Seed(FftLayout.Roster + FftLayout.GenderFlags, [0x50]);
        model.ApplySnapshot(memory.Snapshot());
        var unit = model.SelectedUnit!;
        Assert.Equal(Localizer.T("core.gender.female"), unit.Gender);
        Assert.False(unit.Jobs[17].IsAvailable);
        Assert.True(unit.Jobs[18].IsAvailable);
        Assert.Equal("female", unit.Portrait.Gender);
    }

    [Fact]
    public void BattleUnitGenderUsesFlagsAtOffset6()
    {
        var data = new byte[BattleUnit.Stride];
        data[0] = 0x2A;
        data[FftLayout.BattleGenderFlags] = 0x40;
        Assert.Equal(UnitGender.Female, new BattleUnit(16, data).Gender);
        data[FftLayout.BattleGenderFlags] = 0x80;
        Assert.Equal(UnitGender.Male, new BattleUnit(16, data).Gender);
    }
}
