using FftTrainer.Core;
using Xunit;

namespace FftTrainer.Core.Tests;

public sealed partial class TrainerTests
{
    // JOB_DATA / JOB_COMMAND_DATA from SHA256
    // 937233f7fe76182a665c487c8802f5cec6662ddd09967e87cd09fb146fc6b5d5.
    // All nine base identities independently observed in the 2026-10-26 roster.
    public static TheoryData<int, int, string, string> VerifiedSpecialSkills => new()
    {
        { 0x0D, 74, "4A0000000000000000BC001FFF000AA00B78626E2A7A2A6404031400000000000040000000000000000000000000000000", "0000FC9B9C9D9E9FA0A1A2A3A4A50000000000B4CCDEDFCFE6" },
        { 0x0F, 43, "2BD901DD01DE01D6018000420D00058C0A735F782778266E03030700000000000040000000000000000000000000000000", "0000FCF8F9FAFBFCFDFEFF0000000000000000B4CCDEDFCFE6" },
        { 0x16, 38, "260000000000000000802006FF000B640D4B6473325F326403041200000000000040000000000000000000000000000000", "0000FCD5D6D700000000000000000000000000B4CCDEDFCFE6" },
        { 0x1A, 46, "2E0000000000000000808086FF000A640B6E646E3269326404030800000000000040000000000000000000000000000000", "0000FCAFB0B1B2B3B400000000000000000000B4CCDEDFCFE6" },
        { 0x1E, 40, "28000000000000000098001B7F000A8C0B6464643264326403031900000000000040000000000000000000000000000000", "0000FC9B9C9D9E9F0000000000000000000000B4CCDEDFCFE6" },
        { 0x1F, 69, "450000000000000000D8001B7F000A7A0B916469307D2D6904030E00000000000040000000000000000000000000000000", "0002FCEAEBECEDEEEFF0F1F2F3F4F5F6F70000B4CCDEDFCFE6" },
        { 0x29, 50, "320000000000000000808086FF000B640B6464733264326403030A00000000000040000000000000000000000000000000", "0000FCA9AAABACADAE00000000000000000000B4CCDEDFCFE6" },
        { 0x2A, 67, "43000000000000000098111BFF000A7D0F5064692778325A04030C00000000000040000000000000000000000000000000", "0000FCA0A1A2A3000000000000000000000000B4CCDEDFCFE6" },
        { 0x32, 41, "290000000000000000900006BF000B7D0A646464326E326403031400000000000040000000000000000000000000000000", "FF00FC01020304050607080000000000000000B4CCDEDFCFE6" }
    };

    private static TestMemory SpecialSkillMemory()
    {
        var memory = SkillMemory();
        foreach (var row in VerifiedSpecialSkills)
        {
            memory.Seed(0x785E30 + (ulong)((int)row[0] * 49), Convert.FromHexString((string)row[2]));
            memory.Seed(0x67E210 + (ulong)((int)row[1] * 25), Convert.FromHexString((string)row[3]));
        }
        return memory;
    }

    private static byte[] SpecialSkillUnit(int identity, int equipped = 0)
    {
        var unit = SkillUnit(1);
        unit[0] = (byte)identity;
        unit[FftLayout.Job] = 0x58; // Samurai, primary command 19, regardless of base identity.
        unit[FftLayout.SecondarySkill] = (byte)equipped;
        unit[FftLayout.LearnedSkills] = 0x80; // Exactly the base job's first action.
        unit[FftLayout.LearnedSkills + 1] = 0;
        unit[FftLayout.LearnedSkills + 2] = 0;
        return unit;
    }

    private static void SeedSpecialUnit(TestMemory memory, byte[] unit)
    {
        foreach (var roster in new[] { FftLayout.Roster }.Concat(FftLayout.RosterMirrors))
            memory.Seed(roster + 600, unit);
    }

    [Theory]
    [MemberData(nameof(VerifiedSpecialSkills))]
    public void SpecialBaseCommandRemainsLearnedAfterChangingToOrdinaryJob(
        int identity, int group, string jobHex, string commandHex)
    {
        Assert.Equal(49, Convert.FromHexString(jobHex).Length);
        Assert.Equal(25, Convert.FromHexString(commandHex).Length);
        var tables = SkillTables.TryLoad(SpecialSkillMemory(), 0)!;
        var unit = SpecialSkillUnit(identity);
        var rules = tables.ReadUnit(unit)!;
        Assert.True(rules.Supported);
        Assert.Equal(19, rules.Primary);
        Assert.Equal(identity, rules.Jobs[0].JobId);
        Assert.Equal(group, rules.Jobs[0].GroupId);
        Assert.Single(rules.Jobs[0].Learned);
        Assert.Contains(group, rules.Candidates(SkillSlot.Secondary));
        Assert.Contains(6, rules.Candidates(SkillSlot.Secondary)); // Existing learned ordinary command.
        Assert.DoesNotContain(19, rules.Candidates(SkillSlot.Secondary));
        Assert.DoesNotContain(5, rules.Candidates(SkillSlot.Secondary)); // k=0 is not generic Squire.

        unit[FftLayout.Job] = (byte)identity;
        rules = tables.ReadUnit(unit)!;
        Assert.True(rules.Supported);
        Assert.Equal(group, rules.Primary);
        Assert.DoesNotContain(group, rules.Candidates(SkillSlot.Secondary));
    }

    [Theory]
    [InlineData(0x0F, 43, 248)]
    [InlineData(0x16, 38, 213)]
    public void DragonAndAimedShotRequireTheirOwnActionLearning(int identity, int group, int firstAbility)
    {
        var tables = SkillTables.TryLoad(SpecialSkillMemory(), 0)!;
        var unit = SpecialSkillUnit(identity, group);
        Assert.Equal(new[] { firstAbility }, tables.ReadUnit(unit)!.Jobs[0].Learned);
        unit[FftLayout.LearnedSkills] = 0;
        unit[FftLayout.LearnedSkills + 2] = 0xFC; // Passives alone do not unlock the command.
        Assert.DoesNotContain(group, tables.ReadUnit(unit)!.Candidates(SkillSlot.Secondary));
        unit[FftLayout.LearnedSkills] = 0x80;
        unit[FftLayout.AvailableJobs] &= 0x7F;
        Assert.DoesNotContain(group, tables.ReadUnit(unit)!.Candidates(SkillSlot.Secondary));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(0x0D)]
    public void OtherIdentitiesCannotBorrowDragonOrAimedShotLearning(int identity)
    {
        var tables = SkillTables.TryLoad(SpecialSkillMemory(), 0)!;
        var unit = SpecialSkillUnit(identity, 43); // Even an already equipped value is not proof.
        var rules = tables.ReadUnit(unit)!;
        Assert.True(rules.Supported);
        Assert.DoesNotContain(43, rules.Candidates(SkillSlot.Secondary));
        Assert.DoesNotContain(38, rules.Candidates(SkillSlot.Secondary));
    }

    [Theory]
    [InlineData(0x0F, 43)]
    [InlineData(0x16, 38)]
    public async Task SpecialSkillPickerCanWriteAndReplaceOnlyVerifiedOwnCommand(int identity, int group)
    {
        var memory = SpecialSkillMemory();
        var unit = SpecialSkillUnit(identity, group);
        SeedSpecialUnit(memory, unit);
        var text = SkillText();
        text.Parse("jobs", """[{"id":15,"jobCommandId":43,"jobCommandName":"龙"},{"id":22,"jobCommandId":38,"jobCommandName":"狙击"}]""");
        var model = new TrainerViewModel(memory, text: text);
        model.ApplySnapshot(memory.Snapshot());
        var skill = model.Units[1].SkillSlots[0];
        Assert.Equal(identity == 0x0F ? "龙" : "狙击", skill.DisplayName);
        Assert.True(skill.CanChange);
        Assert.Contains(skill.Candidates, candidate => candidate.Id == group);
        Assert.False(skill.Select(19));
        Assert.False(skill.Select(identity == 0x0F ? 38 : 43));
        Assert.False(skill.Select(37));
        Assert.False(skill.Select(68));
        Assert.False(skill.Select(176));
        Assert.True(skill.Select(6));
        await model.WriteAsync();
        Assert.Equal(3, memory.WriteCount);
        Assert.Empty(model.Pending);
        model.ApplySnapshot(memory.Snapshot());
        Assert.True(skill.Select(group));
        Assert.Equal(3, memory.WriteCount); // Staging is not a write.
        await model.WriteAsync();
        Assert.Equal(6, memory.WriteCount);
        Assert.Empty(model.Pending);
        foreach (var roster in new[] { FftLayout.Roster }.Concat(FftLayout.RosterMirrors))
            Assert.Equal(unit, memory.ReadBytes(roster + 600, FftLayout.UnitSize));
    }

    [Theory]
    [InlineData(0)] // Learned action removed after staging.
    [InlineData(1)] // Character identity changed.
    [InlineData(2)] // Base job unavailable.
    [InlineData(3)] // Mirror identity changed.
    [InlineData(4)] // Command table mapping no longer matches verified build.
    [InlineData(5)] // Current job changed to the special base job.
    public async Task SpecialSkillWriteRevalidatesCurrentRules(int change)
    {
        var memory = SpecialSkillMemory();
        SeedSpecialUnit(memory, SpecialSkillUnit(0x0F));
        var model = new TrainerViewModel(memory, text: SkillText());
        model.ApplySnapshot(memory.Snapshot());
        Assert.True(model.Units[1].SkillSlots[0].Select(43));
        var start = FftLayout.Roster + 600;
        switch (change)
        {
            case 0: memory.Seed(start + FftLayout.LearnedSkills, [0]); break;
            case 1: memory.Seed(start, [128]); break;
            case 2: memory.Seed(start + FftLayout.AvailableJobs, [0]); break;
            case 3: memory.Seed(FftLayout.RosterMirrors[0] + 600, [128]); break;
            case 4: memory.Seed(0x785E30 + 15 * 49, [44]); break;
            case 5: memory.Seed(start + FftLayout.Job, [15]); break;
        }
        await model.WriteAsync();
        Assert.Equal(0, memory.WriteCount);
        Assert.NotNull(Assert.Single(model.Pending).Error);
    }

    [Theory]
    [InlineData(0x0F, 38)]
    [InlineData(0x16, 43)]
    [InlineData(0x16, 37)]
    [InlineData(0x16, 68)]
    [InlineData(0x0F, 19)]
    [InlineData(0x0F, 44)]
    [InlineData(0x0F, 176)]
    [InlineData(0x0F, 224)]
    [InlineData(0x0F, 255)]
    [InlineData(128, 43)]
    public void DirectCoreWriteCannotBypassSpecialCandidateValidation(int identity, int target)
    {
        var memory = SpecialSkillMemory();
        var unit = SpecialSkillUnit(identity);
        SeedSpecialUnit(memory, unit);
        var results = memory.Write(1, [new MemoryEdit(FftLayout.Roster + 600 + FftLayout.SecondarySkill,
            1, 0, (uint)target, SkillIdentity.Read(unit))]);
        Assert.NotNull(Assert.Single(results).Error);
        Assert.Equal(0, memory.WriteCount);
    }

    [Fact]
    public void UnverifiedStoryIdentityMonsterAndForeignSpecialJobRemainReadOnly()
    {
        var tables = SkillTables.TryLoad(SpecialSkillMemory(), 0)!;
        foreach (var identity in new[] { 2, 0x22, 0x2B, 0x48, 0x82, 255 })
            Assert.False(tables.ReadUnit(SpecialSkillUnit(identity))!.Supported);
        var unit = SpecialSkillUnit(0x0F);
        unit[4] |= 0x20;
        Assert.False(tables.ReadUnit(unit)!.Supported);
        unit[4] &= 0xDF;
        unit[FftLayout.Job] = 0x16;
        Assert.False(tables.ReadUnit(unit)!.Supported);
    }

    [Theory]
    [InlineData(38)]
    [InlineData(43)]
    [InlineData(74)]
    [InlineData(46)]
    [InlineData(40)]
    [InlineData(69)]
    [InlineData(50)]
    [InlineData(67)]
    [InlineData(41)]
    [InlineData(5)]
    [InlineData(22)]
    [InlineData(23)]
    public async Task RamzaHackOnlyChangesSecondaryByteInAllThreeMirrors(int group)
    {
        var memory = SpecialSkillMemory();
        var unit = SpecialSkillUnit(1);
        Array.Clear(unit, FftLayout.LearnedSkills, 60);
        Array.Clear(unit, FftLayout.AvailableJobs, 3);
        SeedSpecialUnit(memory, unit);
        var model = new TrainerViewModel(memory, text: SkillText());
        model.ApplySnapshot(memory.Snapshot());
        var skill = model.Units[1].SkillSlots[0];
        Assert.False(skill.SecondaryHackEnabled);
        Assert.False(skill.Select(group));
        skill.SecondaryHackEnabled = true;
        Assert.True(skill.Select(group));
        Assert.True(skill.ValidPending);
        Assert.True(model.NeedsConfirmation);
        Assert.NotNull(skill.Field.ToEdit().SecondaryHack);
        Assert.Equal(0, memory.WriteCount);
        await model.WriteAsync(); // Hack always requires explicit dangerous-write confirmation.
        Assert.Equal(0, memory.WriteCount);
        await model.WriteAsync(true, model.Pending.Select(field => field.ToEdit()).ToArray());
        Assert.Equal(3, memory.WriteCount);
        Assert.Empty(model.Pending);
        unit[FftLayout.SecondarySkill] = (byte)group;
        foreach (var roster in new[] { FftLayout.Roster }.Concat(FftLayout.RosterMirrors))
            Assert.Equal(unit, memory.ReadBytes(roster + 600, FftLayout.UnitSize)); // Includes all learned bits.
    }

    [Fact]
    public async Task TurningHackOffRevokesCapturedEditsAndDoesNotAuthorizeOnReenable()
    {
        var memory = SpecialSkillMemory();
        SeedSpecialUnit(memory, SpecialSkillUnit(1));
        var model = new TrainerViewModel(memory, text: SkillText());
        model.ApplySnapshot(memory.Snapshot());
        var skill = model.Units[1].SkillSlots[0];
        skill.SecondaryHackEnabled = true;
        Assert.True(skill.Select(38));
        var edit = skill.Field.ToEdit();
        skill.SecondaryHackEnabled = false;
        Assert.False(skill.ValidPending);
        await model.WriteAsync(true);
        Assert.NotNull(Assert.Single(memory.Write(1, [edit])).Error);
        skill.SecondaryHackEnabled = true;
        Assert.False(skill.ValidPending); // Toggle alone never grants permission to a pending value.
        await model.WriteAsync(true);
        Assert.Equal(0, memory.WriteCount);
        Assert.True(skill.Select(38));
        await model.WriteAsync(true, [edit]); // Old dialog cannot approve the new capability.
        Assert.Equal(0, memory.WriteCount);
        await model.WriteAsync(true, [skill.Field.ToEdit()]);
        Assert.Equal(3, memory.WriteCount);
    }

    [Theory]
    [InlineData(19)] // Current primary.
    [InlineData(24)] // Mime has no actions.
    [InlineData(37)] // Unverified NPC Aimed Shot variants.
    [InlineData(68)]
    [InlineData(44)] // Monster Breath despite being in the ordinary numerical range.
    [InlineData(175)]
    [InlineData(176)]
    [InlineData(224)]
    [InlineData(255)]
    public void HackWhitelistAndBoundCapabilityRejectUnverifiedCommands(int target)
    {
        var memory = SpecialSkillMemory();
        SeedSpecialUnit(memory, SpecialSkillUnit(1));
        var model = new TrainerViewModel(memory, text: SkillText());
        model.ApplySnapshot(memory.Snapshot());
        var skill = model.Units[1].SkillSlots[0];
        skill.SecondaryHackEnabled = true;
        Assert.DoesNotContain(skill.Candidates, item => item.Id == target);
        Assert.False(skill.Select(target));
        Assert.True(skill.Select(38));
        var edit = skill.Field.ToEdit() with { After = (uint)target };
        Assert.NotNull(Assert.Single(memory.Write(1, [edit])).Error);
        Assert.Equal(0, memory.WriteCount);
    }

    [Theory]
    [InlineData(FftLayout.Job, 1)] // Main command is derived from job; never writable by this capability.
    [InlineData(FftLayout.ReactionSkill, 2)]
    [InlineData(FftLayout.SupportSkill, 2)]
    [InlineData(FftLayout.MovementSkill, 2)]
    [InlineData(FftLayout.Level, 1)]
    public void SecondaryHackCannotAuthorizeOtherFields(int offset, int width)
    {
        var memory = SpecialSkillMemory();
        SeedSpecialUnit(memory, SpecialSkillUnit(1));
        var model = new TrainerViewModel(memory, text: SkillText());
        model.ApplySnapshot(memory.Snapshot());
        var unit = model.Units[1];
        foreach (var other in unit.SkillSlots.Skip(1))
        {
            var before = other.Candidates.Select(item => item.Id).ToArray();
            other.SecondaryHackEnabled = true;
            Assert.False(other.SecondaryHackEnabled);
            Assert.Equal(before, other.Candidates.Select(item => item.Id));
        }
        var skill = unit.SkillSlots[0];
        skill.SecondaryHackEnabled = true;
        Assert.True(skill.Select(38));
        var edit = skill.Field.ToEdit() with { Offset = FftLayout.Roster + 600 + (ulong)offset, Width = width };
        Assert.NotNull(Assert.Single(memory.Write(1, [edit])).Error);
        Assert.Equal(0, memory.WriteCount);
    }

    [Theory]
    [InlineData(0)] // Current identity.
    [InlineData(1)] // Generation.
    [InlineData(2)] // Mirror identity.
    [InlineData(3)] // Mirror before-value.
    [InlineData(4)] // Live command mapping.
    [InlineData(5)] // Monster flag.
    [InlineData(6)] // Empty command record.
    public async Task HackWriteRevalidatesLiveTablesIdentityGenerationAndMirrors(int change)
    {
        var memory = SpecialSkillMemory();
        SeedSpecialUnit(memory, SpecialSkillUnit(1));
        var model = new TrainerViewModel(memory, text: SkillText());
        model.ApplySnapshot(memory.Snapshot());
        var skill = model.Units[1].SkillSlots[0];
        skill.SecondaryHackEnabled = true;
        Assert.True(skill.Select(38));
        switch (change)
        {
            case 0: memory.Seed(FftLayout.Roster + 600, [128]); break;
            case 1: memory.Generation++; break;
            case 2: memory.Seed(FftLayout.RosterMirrors[0] + 600, [128]); break;
            case 3: memory.Seed(FftLayout.RosterMirrors[0] + 600 + FftLayout.SecondarySkill, [6]); break;
            case 4: memory.Seed(0x785E30 + 0x16 * 49, [37]); break;
            case 5: memory.Seed(FftLayout.Roster + 600 + 4, [0x20]); break;
            case 6: memory.Seed(0x67E210 + 38 * 25, new byte[25]); break;
        }
        await model.WriteAsync(true);
        Assert.Equal(0, memory.WriteCount);
        Assert.NotNull(Assert.Single(model.Pending).Error);
    }

    [Theory]
    [InlineData(0)] // Disconnect, even if a reconnect unexpectedly reuses the generation.
    [InlineData(1)] // Switch unit.
    [InlineData(2)] // Reopen/cancel picker.
    [InlineData(3)] // New generation.
    [InlineData(4)] // Identity changed during polling.
    [InlineData(5)] // Unit removed.
    [InlineData(6)] // Undo.
    public async Task HackAuthorizationNeverSurvivesContextReset(int reset)
    {
        var memory = SpecialSkillMemory();
        SeedSpecialUnit(memory, SpecialSkillUnit(1));
        var model = new TrainerViewModel(memory, text: SkillText());
        model.ApplySnapshot(memory.Snapshot());
        model.SelectedUnit = model.Units[1];
        var skill = model.Units[1].SkillSlots[0];
        skill.SecondaryHackEnabled = true;
        Assert.True(skill.Select(38));
        var edit = skill.Field.ToEdit();
        switch (reset)
        {
            case 0: model.ApplySnapshot(null); model.ApplySnapshot(memory.Snapshot()); break;
            case 1: model.SelectedUnit = model.Units[0]; break;
            case 2: skill.ResetSecondaryHack(); break;
            case 3: memory.Generation++; model.ApplySnapshot(memory.Snapshot()); break;
            case 4: memory.Seed(FftLayout.Roster + 600, [128]); model.ApplySnapshot(memory.Snapshot()); break;
            case 5: model.ApplySnapshot(memory.Snapshot() with { Units = [] }); break;
            case 6: model.Undo.Execute(null); break;
        }
        Assert.Null(skill.Field.SecondaryHack);
        Assert.NotNull(Assert.Single(memory.Write(memory.Generation, [edit])).Error);
        await model.WriteAsync(true);
        Assert.Equal(0, memory.WriteCount);
    }

    private sealed class BeforeApplySession(TestMemory memory) : IGameSession
    {
        public Action? BeforeApply { get; set; }
        public IReadOnlyList<WriteResult> Write(long generation, IReadOnlyList<MemoryEdit> edits)
        {
            BeforeApply?.Invoke();
            return memory.Write(generation, edits);
        }
    }

    [Fact]
    public async Task RevocationAfterConfirmationAndEditCaptureStillRejectsWrite()
    {
        var memory = SpecialSkillMemory();
        SeedSpecialUnit(memory, SpecialSkillUnit(1));
        var session = new BeforeApplySession(memory);
        var model = new TrainerViewModel(session, text: SkillText());
        model.ApplySnapshot(memory.Snapshot());
        var skill = model.Units[1].SkillSlots[0];
        skill.SecondaryHackEnabled = true;
        Assert.True(skill.Select(38));
        session.BeforeApply = () =>
        {
            Assert.False(model.CanEdit); // Picker controls use this busy guard.
            skill.SecondaryHackEnabled = false;
        };
        await model.WriteAsync(true, [skill.Field.ToEdit()]);
        Assert.True(model.CanEdit);
        Assert.Equal(0, memory.WriteCount);
        Assert.NotNull(Assert.Single(model.Pending).Error);
    }

    [Fact]
    public void CapturedAuthorizationIsBoundToGenerationEvenWithoutAnotherSnapshot()
    {
        var memory = SpecialSkillMemory();
        SeedSpecialUnit(memory, SpecialSkillUnit(1));
        var model = new TrainerViewModel(memory, text: SkillText());
        model.ApplySnapshot(memory.Snapshot());
        var skill = model.Units[1].SkillSlots[0];
        skill.SecondaryHackEnabled = true;
        Assert.True(skill.Select(38));
        var edit = skill.Field.ToEdit();
        memory.Generation++;
        Assert.NotNull(Assert.Single(memory.Write(memory.Generation, [edit])).Error);
        Assert.NotNull(Assert.Single(GameSession.ApplyEdits(memory, memory.WriteBytes, 0, [edit])).Error);
        Assert.Equal(0, memory.WriteCount);
    }

    [Theory]
    [InlineData(38, "狙击", "Aimed Shot")]
    [InlineData(43, "龙", "Dragon")]
    public void HackUsesCatalogCommandNamesAndLanguageRefreshPreservesExplicitSelection(int group, string first, string second)
    {
        var memory = SpecialSkillMemory();
        SeedSpecialUnit(memory, SpecialSkillUnit(1));
        var text = SkillText();
        void LoadName(string name) => text.Parse("jobs",
            $"[{{\"id\":22,\"jobCommandId\":{group},\"jobCommandName\":\"{name}\"}}]");
        LoadName(first);
        var model = new TrainerViewModel(memory, text: text);
        model.ApplySnapshot(memory.Snapshot());
        var skill = model.Units[1].SkillSlots[0];
        skill.SecondaryHackEnabled = true;
        Assert.True(skill.Select(group));
        var authorization = skill.Field.SecondaryHack;
        Assert.Equal(first, skill.DisplayName);
        LoadName(second);
        model.RefreshLanguage();
        Assert.Equal(second, skill.DisplayName);
        Assert.Equal(second, Assert.Single(skill.Candidates, item => item.Id == group).Name);
        Assert.Same(authorization, skill.Field.SecondaryHack);
        Assert.True(skill.ValidPending);
        Assert.Equal(0, memory.WriteCount);
    }

    [Fact]
    public void MonsterAndUnknownIdentityCannotEnableSecondaryHack()
    {
        var memory = SpecialSkillMemory();
        foreach (var identity in new[] { 1, 0x82, 0x22 })
        {
            var unit = SpecialSkillUnit(identity);
            if (identity == 1) unit[4] |= 0x20;
            SeedSpecialUnit(memory, unit);
            var model = new TrainerViewModel(memory, text: SkillText());
            model.ApplySnapshot(memory.Snapshot());
            var skill = model.Units[1].SkillSlots[0];
            skill.SecondaryHackEnabled = true;
            Assert.False(skill.SecondaryHackEnabled);
            Assert.False(skill.Select(38));
            Assert.Empty(skill.Candidates);
        }
    }

    [Fact]
    public void SpecialCandidateNamesRefreshWithoutChangingIdsOrPendingValue()
    {
        var memory = SpecialSkillMemory();
        SeedSpecialUnit(memory, SpecialSkillUnit(0x0F));
        var text = SkillText();
        text.Parse("jobs", """[{"id":15,"jobCommandId":43,"jobCommandName":"龙"}]""");
        var model = new TrainerViewModel(memory, text: text);
        model.ApplySnapshot(memory.Snapshot());
        var skill = model.Units[1].SkillSlots[0];
        Assert.True(skill.Select(43));
        var ids = skill.Candidates.Select(candidate => candidate.Id).ToArray();
        text.Parse("jobs", """[{"id":15,"jobCommandId":43,"jobCommandName":"Dragon"}]""");
        skill.RefreshLanguage();
        Assert.Equal(ids, skill.Candidates.Select(candidate => candidate.Id));
        Assert.Equal("Dragon", skill.DisplayName);
        Assert.Equal("Dragon", Assert.Single(skill.Candidates, candidate => candidate.Id == 43).Name);
        Assert.Equal(43u, skill.Field.Value);
        Assert.True(skill.ValidPending);
        memory.Seed(FftLayout.Roster + 600 + FftLayout.LearnedSkills, [0]);
        model.ApplySnapshot(memory.Snapshot());
        Assert.False(skill.ValidPending);
        Assert.DoesNotContain(skill.Candidates, candidate => candidate.Id == 43);
        Assert.Equal(0, memory.WriteCount);
    }
}
