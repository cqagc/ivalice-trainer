using System.Text.Json;
using FftTrainer.Core;
using Xunit;

namespace FftTrainer.Core.Tests;

public sealed partial class TrainerTests
{
    private static (TestMemory Memory, TrainerViewModel Model, UnitViewModel Unit) LearningFixture(int group = 69,
        byte first = 0xF0, byte second = 0, byte passive = 0xFC)
    {
        var memory = SpecialSkillMemory();
        var data = SpecialSkillUnit(128, group);
        data[FftLayout.Job] = 0x53;
        var index = group is >= 5 and <= 23 ? group - 5 : 0;
        data[FftLayout.LearnedSkills + index * 3] = first;
        data[FftLayout.LearnedSkills + index * 3 + 1] = second;
        data[FftLayout.LearnedSkills + index * 3 + 2] = passive;
        SeedSpecialUnit(memory, data);
        var text = SkillText();
        text.Parse("abilities", JsonSerializer.Serialize(Enumerable.Range(1, 509).Select(id => new { id, name = $"Action {id}" })));
        var model = new TrainerViewModel(memory, text: text);
        model.ApplySnapshot(memory.Snapshot());
        model.SelectedUnit = model.Units[1];
        return (memory, model, model.Units[1]);
    }

    private static MemoryEdit[] StageLearning(TrainerViewModel model, UnitViewModel unit)
    {
        Assert.True(unit.Learning.BeginPreview());
        Assert.True(model.StageLearning(unit));
        Assert.True(unit.Learning.ValidPending);
        return unit.Learning.Fields.Select(field => field.ToEdit()).ToArray();
    }

    [Fact]
    public async Task SpellbladeLearnsExactActionBitsIncluding256AcrossThreeRosters()
    {
        var (memory, model, unit) = LearningFixture();
        var before = memory.ReadBytes(FftLayout.Roster + 600, FftLayout.UnitSize);
        Assert.True(unit.Learning.BeginPreview());
        var plan = unit.Learning.Preview!;
        Assert.Equal(0, plan.Index);
        Assert.Equal(0xFEFF, plan.Mask); // Stored little endian, memory mask is FF FE.
        Assert.Equal(15, plan.Actions.Count);
        Assert.Equal(4, plan.LearnedCount);
        Assert.Equal(11, plan.AddedCount);
        Assert.Contains(plan.Actions, action => action.Position == 14 && action.Id == 256);
        Assert.DoesNotContain(plan.Actions, action => action.Position == 15);
        Assert.True(model.StageLearning(unit));
        Assert.Equal(2, model.Pending.Count);
        Assert.True(model.NeedsConfirmation);
        Assert.All(model.Pending, field => Assert.True(field.IsDanger));
        Assert.Equal(0, memory.WriteCount);
        await model.WriteAsync();
        Assert.Equal(0, memory.WriteCount);
        var edits = model.Pending.Select(field => field.ToEdit()).ToArray();
        var writes = new List<(ulong Address, int Length)>();
        memory.BeforeWrite = () => { }; // Only the session's simulated memory is used.
        var result = GameSession.ApplyEdits(memory, (address, bytes) =>
        {
            writes.Add((address, bytes.Length));
            memory.WriteBytes(address, bytes);
        }, 0, edits, generation: 1);
        Assert.All(result, item => { Assert.Null(item.Error); Assert.NotNull(item.Warning); });
        Assert.Equal(6, memory.WriteCount);
        before[FftLayout.LearnedSkills] = 0xFF;
        before[FftLayout.LearnedSkills + 1] = 0xFE;
        foreach (var roster in new[] { FftLayout.Roster }.Concat(FftLayout.RosterMirrors))
        {
            Assert.Equal(before, memory.ReadBytes(roster + 600, FftLayout.UnitSize));
            Assert.Contains((roster + 600 + FftLayout.LearnedSkills, 1), writes);
            Assert.Contains((roster + 600 + FftLayout.LearnedSkills + 1, 1), writes);
        }
        Assert.All(writes, write => Assert.Equal(1, write.Length));
        Assert.All(memory.Write(1, edits), result => Assert.NotNull(result.Error)); // Single attempt, never replay.
        Assert.Equal(6, memory.WriteCount);
    }

    [Theory]
    [InlineData(74, 0xE0FF, 11)]
    [InlineData(43, 0x00FF, 8)]
    [InlineData(38, 0x00E0, 3)]
    [InlineData(46, 0x00FC, 6)]
    [InlineData(40, 0x00F8, 5)]
    [InlineData(69, 0xFEFF, 15)]
    [InlineData(50, 0x00FC, 6)]
    [InlineData(67, 0x00F0, 4)]
    [InlineData(41, 0x00FF, 8)]
    public void EveryVerifiedSpecialCommandBuildsItsOwnMask(int group, int mask, int count)
    {
        var (_, _, unit) = LearningFixture(group, 0, 0);
        Assert.True(unit.Learning.BeginPreview());
        Assert.Equal(mask, unit.Learning.Preview!.Mask);
        Assert.Equal(count, unit.Learning.Preview.AddedCount);
        Assert.Equal(0, unit.Learning.Preview.Index);
    }

    [Fact]
    public async Task SwordplaySharesK0AndDoesNotClaimNewActionsAfterSpellblade()
    {
        var (memory, model, unit) = LearningFixture(74);
        Assert.True(unit.Learning.BeginPreview());
        Assert.Equal(4, unit.Learning.Preview!.LearnedCount);
        Assert.Equal(7, unit.Learning.Preview.AddedCount);
        Assert.Equal(0xE0FF, unit.Learning.Preview.Mask);
        foreach (var roster in new[] { FftLayout.Roster }.Concat(FftLayout.RosterMirrors))
            memory.Seed(roster + 600 + FftLayout.LearnedSkills, [0xFF, 0xFE, 0xFC]);
        model.ApplySnapshot(memory.Snapshot());
        Assert.True(unit.Learning.BeginPreview());
        Assert.Equal(11, unit.Learning.Preview!.LearnedCount);
        Assert.Equal(0, unit.Learning.Preview.AddedCount);
        Assert.Equal(0xFEFF, unit.Learning.Preview.After); // Extra learned positions are preserved.
        Assert.False(model.StageLearning(unit));
        Assert.Empty(model.Pending);
        await model.WriteAsync(true);
        Assert.Equal(0, memory.WriteCount);
    }

    [Theory]
    [InlineData(6, 1)]
    [InlineData(7, 2)]
    [InlineData(10, 5)]
    [InlineData(23, 18)]
    public async Task OrdinaryCommandsUseTheirOwnLearningIndex(int group, int index)
    {
        var (memory, model, unit) = LearningFixture(group, 0, 0, 0xA5);
        var original = memory.ReadBytes(FftLayout.Roster + 600, FftLayout.UnitSize);
        var edits = StageLearning(model, unit);
        var plan = unit.Learning.Preview!;
        Assert.Equal(index, plan.Index);
        await model.WriteAsync(true, edits);
        Assert.Empty(model.Pending);
        Assert.Equal(edits.Length * 3, memory.WriteCount);
        original[plan.Offset] = (byte)plan.After;
        original[plan.Offset + 1] = (byte)(plan.After >> 8);
        foreach (var roster in new[] { FftLayout.Roster }.Concat(FftLayout.RosterMirrors))
            Assert.Equal(original, memory.ReadBytes(roster + 600, FftLayout.UnitSize));
    }

    [Fact]
    public async Task SparseCommandUsesPositionsNotCountAndPreservesEmptyAndPassiveBits()
    {
        var (memory, model, unit) = LearningFixture(69, 0x40, 0x01);
        var record = new byte[25];
        record[0] = 0x20; // p2 high bit + low zero means action 256, not an empty slot.
        record[3] = 234; // p0
        record[6] = 235; // p3, with an empty p1 between real actions.
        memory.Seed(0x67E210 + 69 * 25, record);
        model.ApplySnapshot(memory.Snapshot());
        var edits = StageLearning(model, unit);
        Assert.Equal(new[] { 0, 2, 3 }, unit.Learning.Preview!.Actions.Select(action => action.Position));
        Assert.Equal(0xB0, unit.Learning.Preview.Mask);
        Assert.Single(edits); // No write at all for unchanged second byte.
        await model.WriteAsync(true, edits);
        Assert.Equal(3, memory.WriteCount);
        foreach (var roster in new[] { FftLayout.Roster }.Concat(FftLayout.RosterMirrors))
            Assert.Equal(new byte[] { 0xF0, 0x01, 0xFC }, memory.ReadBytes(roster + 600 + FftLayout.LearnedSkills, 3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(24)]
    [InlineData(37)]
    [InlineData(44)]
    [InlineData(68)]
    [InlineData(176)]
    [InlineData(224)]
    [InlineData(255)]
    public void EmptyMonsterAndUnknownCommandsHaveNoLearningPlan(int group)
    {
        var (_, model, unit) = LearningFixture(group);
        Assert.False(unit.Learning.CanPreview);
        Assert.False(unit.Learning.BeginPreview());
        Assert.False(model.StageLearning(unit));
    }

    [Theory]
    [InlineData(0)] // Character identity.
    [InlineData(1)] // Connection generation.
    [InlineData(2)] // Equipped command.
    [InlineData(3)] // Action id changes but the mask is still the same.
    [InlineData(4)] // Mapping.
    [InlineData(5)] // Primary learned byte.
    [InlineData(6)] // Mirror learned byte.
    [InlineData(7)] // Mirror passive byte.
    [InlineData(8)] // Mirror identity.
    [InlineData(9)] // Mirror equipped command.
    [InlineData(10)] // Monster flag.
    public void LearningPreflightRejectsChangedStateBeforeAnyWrite(int change)
    {
        var (memory, model, unit) = LearningFixture();
        var edits = StageLearning(model, unit);
        var start = FftLayout.Roster + 600;
        var mirror = FftLayout.RosterMirrors[1] + 600;
        switch (change)
        {
            case 0: memory.Seed(start, [129]); break;
            case 1: memory.Generation++; break;
            case 2: memory.Seed(start + FftLayout.SecondarySkill, [74]); break;
            case 3: memory.Seed(0x67E210 + 69 * 25 + 3, [235]); break;
            case 4: memory.Seed(0x785E30 + 0x1F * 49, [68]); break;
            case 5: memory.Seed(start + FftLayout.LearnedSkills, [0xF1]); break;
            case 6: memory.Seed(mirror + FftLayout.LearnedSkills, [0xF1]); break;
            case 7: memory.Seed(mirror + FftLayout.LearnedSkills + 2, [0xF0]); break;
            case 8: memory.Seed(mirror, [129]); break;
            case 9: memory.Seed(mirror + FftLayout.SecondarySkill, [74]); break;
            case 10: memory.Seed(start + 4, [0x20]); break;
        }
        Assert.All(memory.Write(memory.Generation, edits), result => Assert.NotNull(result.Error));
        Assert.Equal(0, memory.WriteCount);
    }

    [Theory]
    [InlineData(0)] // Preview cancel/close.
    [InlineData(1)] // Unit selection.
    [InlineData(2)] // Disconnect.
    [InlineData(3)] // Undo.
    [InlineData(4)] // Secondary edit staged after learning.
    public async Task LearningContextChangesRevokeCapturedAuthorization(int change)
    {
        var (memory, model, unit) = LearningFixture();
        var edits = StageLearning(model, unit);
        switch (change)
        {
            case 0: unit.Learning.Reset(); break;
            case 1: model.SelectedUnit = model.Units[0]; break;
            case 2: model.ApplySnapshot(null); break;
            case 3: model.Undo.Execute(null); break;
            case 4: Assert.True(unit.SkillSlots[0].Select(0)); break;
        }
        Assert.All(memory.Write(1, edits), result => Assert.NotNull(result.Error));
        Assert.Equal(0, memory.WriteCount);
        if (change != 4) await model.WriteAsync(true, edits);
        Assert.Equal(0, memory.WriteCount);
    }

    [Fact]
    public async Task LearningConfirmationCannotApproveAReplacementAuthorization()
    {
        var (memory, model, unit) = LearningFixture();
        var old = StageLearning(model, unit);
        model.Undo.Execute(null);
        var current = StageLearning(model, unit);
        await model.WriteAsync(true, old);
        Assert.Equal(0, memory.WriteCount);
        await model.WriteAsync(true, current);
        Assert.Equal(6, memory.WriteCount);
    }

    [Fact]
    public async Task WrittenSecondaryMustBeRefreshedBeforeLearningItsActions()
    {
        var (memory, model, unit) = LearningFixture();
        var secondary = unit.SkillSlots[0];
        secondary.SecondaryHackEnabled = true;
        Assert.True(secondary.Select(38));
        await model.WriteAsync(true);
        Assert.Equal(3, memory.WriteCount);
        Assert.False(secondary.Field.IsDirty);
        Assert.False(unit.Learning.CanPreview); // Old live plan still belongs to 69.
        Assert.False(unit.Learning.BeginPreview());
        model.ApplySnapshot(memory.Snapshot());
        Assert.True(unit.Learning.BeginPreview());
        Assert.Equal(38, unit.Learning.Preview!.Group);
    }

    [Fact]
    public void PendingSecondaryBlocksLearningAndCoreRejectsMixedBatch()
    {
        var (memory, model, unit) = LearningFixture();
        var learn = StageLearning(model, unit);
        var secondary = unit.SkillSlots[0];
        var mixed = new MemoryEdit(secondary.Field.Offset, 1, 69, 0, secondary.Field.SkillIdentity);
        var results = memory.Write(1, [.. learn, mixed]);
        Assert.All(results.Where(result => result.Edit.LearningHack != null), result => Assert.NotNull(result.Error));
        Assert.Equal(new byte[] { 0xF0, 0, 0xFC }, memory.ReadBytes(FftLayout.Roster + 600 + FftLayout.LearnedSkills, 3));
        model.Undo.Execute(null);
        Assert.True(secondary.Select(0));
        Assert.False(unit.Learning.BeginPreview());
        Assert.False(model.StageLearning(unit));
    }

    [Theory]
    [InlineData(0)] // Drop a required byte.
    [InlineData(1)] // Change to passive byte.
    [InlineData(2)] // Full-mask escalation.
    [InlineData(3)] // Before-value change.
    [InlineData(4)] // Write another character.
    [InlineData(5)] // Width escalation.
    public void LearningCapabilityCannotBeRepurposed(int tamper)
    {
        var (memory, model, unit) = LearningFixture();
        var edits = StageLearning(model, unit);
        switch (tamper)
        {
            case 0: edits = edits.Take(1).ToArray(); break;
            case 1: edits[0] = edits[0] with { Offset = edits[0].Offset + 2 }; break;
            case 2: edits[1] = edits[1] with { After = 255 }; break;
            case 3: edits[0] = edits[0] with { Before = 0 }; break;
            case 4: edits[0] = edits[0] with { Offset = edits[0].Offset + 600 }; break;
            case 5: edits[0] = edits[0] with { Width = 2 }; break;
        }
        Assert.All(memory.Write(1, edits), result => Assert.NotNull(result.Error));
        Assert.Equal(0, memory.WriteCount);
    }

    [Fact]
    public void ArbitraryEditsCannotTouchAnyLearnedByteOrCrossIntoTheRegion()
    {
        var memory = SpecialSkillMemory();
        foreach (var roster in new[] { FftLayout.Roster }.Concat(FftLayout.RosterMirrors))
        {
            for (var offset = 0; offset < FftLayout.JobCount * 3; offset++)
            {
                var address = roster + 600 + FftLayout.LearnedSkills + (ulong)offset;
                var before = memory.ReadBytes(address, 1)[0];
                Assert.NotNull(Assert.Single(memory.Write(1, [new MemoryEdit(address, 1, before, 255)])).Error);
            }
            Assert.NotNull(Assert.Single(memory.Write(1,
                [new MemoryEdit(roster + 600 + FftLayout.LearnedSkills - 1, 2, 0, 65535)])).Error);
        }
        Assert.Equal(0, memory.WriteCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialWriteAndReadbackFailureAreReportedWithoutRebaseOrRetry(bool ignore)
    {
        var (memory, model, unit) = LearningFixture();
        var edits = StageLearning(model, unit);
        var address = FftLayout.RosterMirrors[0] + 600 + FftLayout.LearnedSkills;
        if (ignore) memory.IgnoreWrite = address;
        else memory.FailWrite = address;
        await model.WriteAsync(true, edits);
        Assert.Equal(2, model.Pending.Count);
        Assert.All(model.Pending, field => Assert.NotNull(field.Error));
        Assert.Equal(0xF0u, unit.Learning.Fields[0].Original);
        Assert.Equal(0u, unit.Learning.Fields[1].Original);
        Assert.False(unit.Learning.ValidPending);
        var attempts = memory.WriteCount;
        await model.WriteAsync(true);
        Assert.Equal(attempts, memory.WriteCount);
    }

    [Theory]
    [InlineData(0)] // Unknown human identity.
    [InlineData(1)] // Monster identity/flag.
    [InlineData(2)] // Known group becomes empty.
    [InlineData(3)] // Passive masquerading as an action.
    public void UnsupportedIdentityOrInvalidActionTableCannotPreviewLearning(int change)
    {
        var (memory, model, unit) = LearningFixture();
        switch (change)
        {
            case 0: memory.Seed(FftLayout.Roster + 600, [0x22]); break;
            case 1: memory.Seed(FftLayout.Roster + 600 + 4, [0x20]); break;
            case 2: memory.Seed(0x67E210 + 69 * 25, new byte[25]); break;
            case 3:
                var record = memory.ReadBytes(0x67E210 + 69 * 25, 25);
                record[0] |= 0x80;
                record[3] = 0xA6; // 422, reaction-range ID in an action position.
                memory.Seed(0x67E210 + 69 * 25, record);
                break;
        }
        model.ApplySnapshot(memory.Snapshot());
        Assert.False(unit.Learning.BeginPreview());
        Assert.False(model.StageLearning(unit));
        Assert.Empty(model.Pending);
    }

    [Fact]
    public async Task LearningRevokedAfterEditCaptureBeforeSessionWriteIsRejected()
    {
        var (memory, _, _) = LearningFixture();
        var session = new BeforeApplySession(memory);
        var text = SkillText();
        text.Parse("abilities", JsonSerializer.Serialize(Enumerable.Range(1, 509).Select(id => new { id, name = $"Action {id}" })));
        var model = new TrainerViewModel(session, text: text);
        model.ApplySnapshot(memory.Snapshot());
        model.SelectedUnit = model.Units[1];
        var unit = model.Units[1];
        var edits = StageLearning(model, unit);
        session.BeforeApply = () => { Assert.False(model.CanEdit); unit.Learning.Reset(); };
        await model.WriteAsync(true, edits);
        Assert.Equal(0, memory.WriteCount);
        Assert.All(model.Pending, field => Assert.NotNull(field.Error));
    }

    [Fact]
    public void PreviewInvalidatesWhenTablesOrEquippedCommandChangeDuringPolling()
    {
        var (memory, model, unit) = LearningFixture();
        Assert.True(unit.Learning.BeginPreview());
        memory.Seed(0x67E210 + 69 * 25 + 3, [235]);
        model.ApplySnapshot(memory.Snapshot());
        Assert.Null(unit.Learning.Preview);
        Assert.False(model.StageLearning(unit));
        Assert.True(unit.Learning.BeginPreview());
        memory.Seed(FftLayout.Roster + 600 + FftLayout.SecondarySkill, [74]);
        model.ApplySnapshot(memory.Snapshot());
        Assert.Null(unit.Learning.Preview);
        Assert.False(model.StageLearning(unit));
    }

    [Fact]
    public async Task SimulatedWorkingCopyWritebackDoesNotAutoReapplyLearning()
    {
        var (memory, model, unit) = LearningFixture();
        var edits = StageLearning(model, unit);
        await model.WriteAsync(true, edits);
        Assert.Empty(model.Pending);
        foreach (var roster in new[] { FftLayout.Roster }.Concat(FftLayout.RosterMirrors))
            memory.Seed(roster + 600 + FftLayout.LearnedSkills, [0xF0, 0, 0xFC]);
        model.ApplySnapshot(memory.Snapshot());
        Assert.Empty(model.Pending);
        await model.WriteAsync(true);
        Assert.Equal(6, memory.WriteCount);
        Assert.True(unit.Learning.BeginPreview());
        Assert.Equal(11, unit.Learning.Preview!.AddedCount);
        Assert.All(memory.Write(1, edits), result => Assert.NotNull(result.Error));
    }
}
