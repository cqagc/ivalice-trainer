using System.Buffers.Binary;
using FftTrainer.Core;
using Xunit;

namespace FftTrainer.Core.Tests;

public sealed partial class TrainerTests
{
    private sealed class TestMemory : IMemorySource, IGameSession, IBattleSession
    {
        private readonly Dictionary<ulong, byte> bytes = [];
        public int WriteCount { get; private set; }
        public Action? BeforeWrite { get; set; }
        public Action<ulong, int>? BeforeRead { get; set; }
        public ulong? IgnoreWrite { get; set; }
        public ulong? FailWrite { get; set; }
        public long Generation { get; set; } = 1;
        public long OptionsVersion { get; private set; }
        public bool KeepOptions { get; private set; }
        public List<(long Version, int Experience, int Jp)> BattleOptionsCalls { get; } = [];

        public void SetBattleOptions(bool lockHp, bool infiniteMp, bool guestLockHp, bool freezeCountdown, bool lockCt,
            int experienceMultiplier = 1, int jpMultiplier = 1, bool alwaysMove = false, bool alwaysAct = false,
            bool maxMoveJump = false, bool instantCast = false, long optionsVersion = 0, bool keepOptions = false)
        {
            OptionsVersion = optionsVersion;
            KeepOptions = keepOptions;
            BattleOptionsCalls.Add((optionsVersion, experienceMultiplier, jpMultiplier));
        }

        public IEnumerable<MemoryRegion> GetRegions(bool privateOnly = false, MemoryRegion? range = null) => [];
        public List<(ulong Address, int Length)> Reads { get; } = [];
        public byte[] ReadBytes(ulong address, int length)
        {
            Reads.Add((address, length));
            BeforeRead?.Invoke(address, length);
            return Enumerable.Range(0, length)
                .Select(i => bytes.GetValueOrDefault(address + (ulong)i)).ToArray();
        }

        public void Seed(ulong address, byte[] data)
        {
            for (var i = 0; i < data.Length; i++)
                bytes[address + (ulong)i] = data[i];
        }

        public void WriteBytes(ulong address, byte[] data)
        {
            BeforeWrite?.Invoke();
            WriteCount++;
            if (address == FailWrite)
                throw new IOException("模拟写入失败");
            if (address != IgnoreWrite)
                Seed(address, data);
        }

        public IReadOnlyList<WriteResult> Write(long generation, IReadOnlyList<MemoryEdit> edits) =>
            generation == Generation
                ? GameSession.ApplyEdits(this, WriteBytes, 0, edits)
                : edits.Select(edit => new WriteResult(edit, "连接已变化")).ToArray();

        public GameSnapshot Snapshot(FftLayout? layout = null) =>
            GameSession.ReadSnapshot(this, 0, Generation, layout ?? new FftLayout()) with { OptionsVersion = OptionsVersion };
    }

    [Fact]
    public void CommunityRegionsReadExactBoundsAndRejectGapWrites()
    {
        var memory = new TestMemory();
        memory.Seed(FftLayout.BonusItems, [1, 2, 3, 4]);
        memory.Seed(FftLayout.Poaching + 95, [7]);
        var snapshot = memory.Snapshot(new FftLayout { RosterCount = 0 });
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, snapshot.Inventory[0x101..]);
        Assert.Equal(96, snapshot.Poaching!.Length);
        Assert.Equal(7, snapshot.Poaching[95]);
        Assert.DoesNotContain(memory.Reads, read => read.Address < FftLayout.Roster + 1
            && read.Address + (ulong)read.Length > FftLayout.InventoryLast + 1);
        memory.Reads.Clear();
        var invalid = Enumerable.Range(0x105, 11)
            .Select(id => new MemoryEdit(FftLayout.Inventory + (ulong)id, 1, 0, 99))
            .Concat(new[]
            {
                new MemoryEdit(FftLayout.InventoryLast, 2, 4, 99),
                new MemoryEdit(FftLayout.Inventory + 0xFF, 4, 0, 99),
                new MemoryEdit(FftLayout.Poaching - 1, 1, 0, 99),
                new MemoryEdit(FftLayout.Poaching + 96, 1, 0, 99),
                new MemoryEdit(FftLayout.Poaching + 95, 2, 7, 99),
                new MemoryEdit(FftLayout.BonusItems, 1, 1, 100)
            }).ToArray();
        Assert.All(memory.Write(1, invalid), result => Assert.NotNull(result.Error));
        Assert.Empty(memory.Reads);
        Assert.Equal(0, memory.WriteCount);
        Assert.All(memory.Write(1, [new(FftLayout.InventoryLast, 1, 4, 99),
            new(FftLayout.Poaching + 95, 1, 7, 99)]), result => Assert.Null(result.Error));
        Assert.Equal(2, memory.WriteCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => new InventoryItemViewModel(0x105, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InventoryItemViewModel(96, 0, isPoaching: true));
    }

    // 社区 CE 表（xyzjw 2026-06-28）的猎物顺序，简中名称与游戏职业表核对过
    private static readonly string[] PoachingNames =
    [
        "陆行鸟", "黑陆行鸟", "红陆行鸟", "哥布林", "黑哥布林", "胡诌哥布林",
        "食尸鬼", "妖鬼", "亡魂", "红色野豹", "夸尔", "吸血豹",
        "炸弹", "榴弹", "爆弹", "骸骨", "掠骨者", "复生骸骨",
        "远古怪鸟", "铁喙怪鹰", "石化鸡蛇", "树人", "树妖", "树精",
        "牛鬼", "牛头怪", "赛克雷特", "夺灵魔", "夺魄魔", "夺心魔",
        "飘浮眼魔", "恶精灵", "灾祸之瞳", "毛尔波尔", "奥丘", "大毛尔波尔",
        "山猪波奇", "波奇猪", "野猪波奇", "凶恶巨兽", "帝王凶恶巨兽", "黑暗凶恶巨兽",
        "龙", "蓝龙", "红龙", "修德拉", "海德拉", "提亚马特"
    ];

    [Fact]
    public async Task CommunityCategoriesMapAllNamesAndStageOnlyTheirOwnRegion()
    {
        var memory = new TestMemory();
        var text = new GameTextCatalog();
        text.Parse("items", """[{"id":257,"name":"学院之剑","equipmentGroup":"武器"}]""");
        // 猎物名称取怪物职业名：按 PoachingJobIds 喂职业表
        text.Parse("jobs", System.Text.Json.JsonSerializer.Serialize(GameData.PoachingJobIds
            .Select((job, i) => new { id = job, name = PoachingNames[i] })));
        var model = new TrainerViewModel(memory, text: text);
        model.ApplySnapshot(memory.Snapshot());
        model.ShowAllItems = true;
        model.ShowBonusItems.Execute(null);
        Assert.Equal(4, model.VisibleItems.Count);
        Assert.Equal("学院之剑", model.VisibleItems[0].Name);
        model.MaxVisibleItems.Execute(null);
        Assert.Equal(4, model.Pending.Count);
        Assert.All(model.Pending, field => Assert.InRange(field.Offset, FftLayout.BonusItems, FftLayout.InventoryLast));
        model.Undo.Execute(null);
        model.ShowPoaching.Execute(null);
        Assert.Equal(96, model.VisibleItems.Count);
        Assert.Equal(48, GameData.PoachingJobIds.Count);
        Assert.Equal(48, GameData.PoachingJobIds.Distinct().Count());
        for (var i = 0; i < 48; i++)
        {
            Assert.Equal(PoachingNames[i], model.VisibleItems[i * 2].Name);
            Assert.Equal(PoachingNames[i] + " ★", model.VisibleItems[i * 2 + 1].Name);
        }
        Assert.Equal("陆行鸟", model.VisibleItems[0].Name);
        Assert.Equal("提亚马特 ★", model.VisibleItems[95].Name);
        model.MaxVisibleItems.Execute(null);
        Assert.Equal(96, model.Pending.Count);
        Assert.All(model.Pending, field => Assert.InRange(field.Offset, FftLayout.Poaching, FftLayout.Poaching + 95));
        model.VisibleItems[0].Quantity.Value = 100;
        Assert.Equal(99u, model.VisibleItems[0].Quantity.Value);
        await model.WriteAsync();
        Assert.Equal(96, memory.WriteCount);
        Assert.All(memory.ReadBytes(FftLayout.Poaching, 96), value => Assert.Equal(99, value));
        Assert.All(memory.ReadBytes(FftLayout.InventoryLast + 1, 11), value => Assert.Equal(0, value));
        model.ShowEquipment.Execute(null);
        Assert.DoesNotContain(model.VisibleItems, item => item.IsPoaching || item.Group == "特典道具");
    }

    private static TestMemory BattleFixture()
    {
        var memory = new TestMemory();
        void Seed(int slot, byte faction, ushort hp, ushort maxHp, ushort mp, ushort maxMp,
            byte character = 0x80, byte job = 0x4A, byte marker = 0)
        {
            var data = new byte[BattleUnit.Stride];
            data[0] = character;
            data[1] = marker;
            data[3] = job;
            // 沿用旧的阵营代号：0 我方、3 敌方、其余未知 → +0x05 队伍位 0 / 1 / 2
            data[5] = faction switch { 0 => 0x08, 3 => 0x10, _ => 0x20 };
            data[0x29] = 1;
            data[0x2A] = data[0x2B] = 72;
            data[0x2C] = data[0x2D] = 73;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x30), hp);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x32), maxHp);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x34), mp);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x36), maxMp);
            memory.Seed(BattleUnit.Start + (ulong)(slot * BattleUnit.Stride), data);
        }
        Seed(2, 3, 37, 37, 7, 7, job: 0x53);
        Seed(3, 3, 0, 29, 11, 11);
        Seed(4, 3, 35, 35, 10, 10);
        Seed(5, 3, 6, 23, 11, 11);
        Seed(6, 3, 35, 35, 10, 10);
        Seed(7, 3, 42, 42, 4, 4, 0x82, 0xA9);
        memory.Seed(BattleUnit.Start + 7 * BattleUnit.Stride + 6, [0x20]);
        Seed(16, 0, 44, 44, 17, 17, 1, 1);
        Seed(17, 0, 44, 44, 11, 11);
        Seed(18, 0, 42, 42, 11, 11, 0x81);
        Seed(19, 0, 36, 36, 11, 11, 0x81, 0x4B);
        Seed(20, 0, 36, 36, 11, 11, 0x81, 0x4B, 0xFF);
        Seed(22, 2, 18, 30, 2, 10);
        Seed(23, 3, 99, 99, 99, 99);
        Seed(0, 0, 52, 52, 10, 10, 0x04, 0x04);
        Seed(1, 0, 52, 52, 10, 10, 0x07, 0x07);
        memory.Seed(BattleUnit.Start + 0x41, [22]);
        memory.Seed(BattleUnit.Start + BattleUnit.Stride + 0x41, [2]);
        memory.Seed(BattleUnit.Start + 2, [0x32]);
        memory.Seed(BattleUnit.Start + BattleUnit.Stride + 2, [0xFF]);
        foreach (var (slot, index) in new[] { (16, 0), (17, 1), (18, 4), (19, 6) })
            memory.Seed(BattleUnit.Start + (ulong)(slot * BattleUnit.Stride) + 2, [(byte)index]);
        memory.Seed(BattleRules.InBattleFlag, [1]);
        return memory;
    }

    private static void RewardTick(TestMemory memory, BattleRewards rewards) =>
        rewards.Apply(memory, memory.WriteBytes, 0, BattleRules.Read(memory, 0));

    [Fact]
    public void BattleRewardsWorkInStoryBattleWhereEncounterFlagIsZero()
    {
        // 剧情战实测遭遇战标志一直是 0；倍率不能因此被当成「战斗结束」重置
        var memory = BattleFixture();
        memory.Seed(BattleRules.InBattleFlag, [0]);
        var start = BattleUnit.Start + 16 * BattleUnit.Stride;
        SeedReward(memory, 16, 0, 100, 100, 10);
        var rewards = new BattleRewards();
        rewards.Configure(2, 5);
        RewardTick(memory, rewards);
        SeedReward(memory, 16, 0, 110, 110, 20);
        RewardTick(memory, rewards);
        Assert.Equal(2, rewards.ExperienceMultiplier);
        Assert.Equal(5, rewards.JpMultiplier);
        Assert.Equal(150, BinaryPrimitives.ReadUInt16LittleEndian(memory.ReadBytes(start + 0x11E, 2)));
        Assert.Equal(30, memory.ReadBytes(start + 0x28, 1)[0]);
    }

    private static void SeedReward(TestMemory memory, int slot, int job, ushort current, ushort total, byte experience = 0)
    {
        var start = BattleUnit.Start + (ulong)(slot * BattleUnit.Stride);
        memory.Seed(start + (ulong)(0xF0 + job * 2), Values.ToBytes(current, ValueTypeCode.U16));
        memory.Seed(start + (ulong)(0x11E + job * 2), Values.ToBytes(total, ValueTypeCode.U16));
        memory.Seed(start + 0x28, [experience]);
    }

    [Theory]
    [InlineData(2, 120, 60)]
    [InlineData(5, 150, 90)]
    public void BattleRewardsMultiplyOnlyGameGainsAndOnlyPlayers(int multiplier, uint jp, byte exp)
    {
        var memory = BattleFixture();
        foreach (var slot in new[] { 0, 2, 16 })
            SeedReward(memory, slot, 0, 100, 100, 40);
        var rewards = new BattleRewards();
        rewards.Configure(multiplier, multiplier);
        RewardTick(memory, rewards);
        Assert.Equal(0, memory.WriteCount);
        foreach (var slot in new[] { 0, 2, 16 })
            SeedReward(memory, slot, 0, 110, 110, 50);
        RewardTick(memory, rewards);
        Assert.Null(rewards.Notice);
        var units = BattleRules.Read(memory, 0);
        var player = units.Single(unit => unit.Slot == 16);
        Assert.Equal(jp, player.Read(0xF0, 2));
        Assert.Equal(jp, player.Read(0x11E, 2));
        Assert.Equal(exp, player.Data[0x28]);
        foreach (var slot in new[] { 0, 2 })
        {
            var unit = units.Single(unit => unit.Slot == slot);
            Assert.Equal(110u, unit.Read(0xF0, 2));
            Assert.Equal(110u, unit.Read(0x11E, 2));
            Assert.Equal(50, unit.Data[0x28]);
        }
        var writes = memory.WriteCount;
        RewardTick(memory, rewards);
        RewardTick(memory, rewards);
        Assert.Equal(writes, memory.WriteCount);
        SeedReward(memory, 16, 0, (ushort)(jp + 3), (ushort)(jp + 3), exp);
        RewardTick(memory, rewards);
        Assert.Equal(jp + (uint)(3 * multiplier), BattleRules.Read(memory, 0).Single(unit => unit.Slot == 16).Read(0x11E, 2));
    }

    [Fact]
    public void BattleRewardsCapJpAndExpWithoutChangingLevelOrStats()
    {
        var memory = BattleFixture();
        SeedReward(memory, 16, 0, 9980, 9980, 90);
        var rewards = new BattleRewards();
        rewards.Configure(5, 5);
        RewardTick(memory, rewards);
        SeedReward(memory, 16, 0, 9990, 9990, 95);
        var before = memory.ReadBytes(BattleUnit.Start + 16 * BattleUnit.Stride, BattleUnit.Stride);
        RewardTick(memory, rewards);
        var after = memory.ReadBytes(BattleUnit.Start + 16 * BattleUnit.Stride, BattleUnit.Stride);
        Assert.Equal(9999, BinaryPrimitives.ReadUInt16LittleEndian(after.AsSpan(0xF0)));
        Assert.Equal(9999, BinaryPrimitives.ReadUInt16LittleEndian(after.AsSpan(0x11E)));
        Assert.Equal(99, after[0x28]);
        Assert.Equal(0x80, after[0xE4]);
        foreach (var field in Enumerable.Range(0, BattleUnit.Stride).Except(new[] { 0x28, 0xE4, 0xF0, 0xF1, 0x11E, 0x11F }))
            Assert.Equal(before[field], after[field]);
    }

    [Fact]
    public void BattleRewardsSkipSpendingAndIdentityChangesButKeepJpOnLevelUp()
    {
        var memory = BattleFixture();
        var start = BattleUnit.Start + 16 * BattleUnit.Stride;
        SeedReward(memory, 16, 0, 100, 100, 90);
        var rewards = new BattleRewards();
        rewards.Configure(5, 5);
        RewardTick(memory, rewards);
        SeedReward(memory, 16, 0, 20, 100, 90);
        RewardTick(memory, rewards);
        Assert.Equal(0, memory.WriteCount);
        // 升级那一帧：JP 照常放大（+10 → 再补 40），经验不补（被游戏清零）
        SeedReward(memory, 16, 0, 30, 110, 0);
        memory.Seed(start + 0x29, [2]);
        RewardTick(memory, rewards);
        Assert.Equal(70, BinaryPrimitives.ReadUInt16LittleEndian(memory.ReadBytes(start + 0xF0, 2)));
        Assert.Equal(150, BinaryPrimitives.ReadUInt16LittleEndian(memory.ReadBytes(start + 0x11E, 2)));
        Assert.Equal(0, memory.ReadBytes(start + 0x28, 1)[0]);
        SeedReward(memory, 16, 0, 80, 160, 10);
        RewardTick(memory, rewards);
        Assert.Equal(50, memory.ReadBytes(start + 0x28, 1)[0]);
        Assert.Equal(120, BinaryPrimitives.ReadUInt16LittleEndian(memory.ReadBytes(start + 0xF0, 2)));
        var writes = memory.WriteCount;
        memory.Seed(start + 1, [9]);
        SeedReward(memory, 16, 0, 200, 200, 60);
        RewardTick(memory, rewards);
        Assert.Equal(writes, memory.WriteCount);
    }

    [Fact]
    public void BattleRewardsCombineNibblesAndNeverLowerExistingJobLevels()
    {
        var memory = BattleFixture();
        var start = BattleUnit.Start + 16 * BattleUnit.Stride;
        SeedReward(memory, 16, 0, 180, 180);
        SeedReward(memory, 16, 1, 380, 380);
        SeedReward(memory, 16, 2, 10, 10);
        memory.Seed(start + 0xE4, [0x11, 0x70]);
        var rewards = new BattleRewards();
        rewards.Configure(1, 2);
        RewardTick(memory, rewards);
        SeedReward(memory, 16, 0, 195, 195);
        SeedReward(memory, 16, 1, 395, 395);
        SeedReward(memory, 16, 2, 20, 20);
        RewardTick(memory, rewards);
        Assert.Null(rewards.Notice);
        Assert.Equal(new byte[] { 0x23, 0x70 }, memory.ReadBytes(start + 0xE4, 2));
    }

    [Fact]
    public void BattleRewardsSkipUnusedAndGenderRestrictedJobs()
    {
        var memory = BattleFixture();
        var rewards = new BattleRewards();
        rewards.Configure(1, 5);
        RewardTick(memory, rewards);
        foreach (var (slot, job) in new[] { (16, 18), (18, 17), (16, 20), (16, 21), (16, 22) })
            SeedReward(memory, slot, job, 100, 100);
        RewardTick(memory, rewards);
        Assert.Equal(0, memory.WriteCount);
        SeedReward(memory, 16, 17, 100, 100);
        SeedReward(memory, 18, 18, 100, 100);
        RewardTick(memory, rewards);
        Assert.Null(rewards.Notice);
        Assert.Equal(500, BinaryPrimitives.ReadUInt16LittleEndian(memory.ReadBytes(BattleUnit.Start + 16 * BattleUnit.Stride + 0xF0 + 34, 2)));
        Assert.Equal(500, BinaryPrimitives.ReadUInt16LittleEndian(memory.ReadBytes(BattleUnit.Start + 18 * BattleUnit.Stride + 0xF0 + 36, 2)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BattleRewardsResetOnlyAfterRepeatedWriteOrReadbackFailures(bool ignore)
    {
        var memory = BattleFixture();
        var rewards = new BattleRewards();
        rewards.Configure(2, 5);
        RewardTick(memory, rewards);
        SeedReward(memory, 16, 0, 10, 10, 10);
        var address = BattleUnit.Start + 16 * BattleUnit.Stride + 0xF0;
        if (ignore)
            memory.IgnoreWrite = address;
        else
            memory.FailWrite = address;
        // 偶发失败（读写之间游戏改了值）不复位，连续 5 次才当真
        for (var i = 0; i < 4; i++)
        {
            RewardTick(memory, rewards);
            Assert.Equal(5, rewards.JpMultiplier);
            Assert.Null(rewards.Notice);
        }
        RewardTick(memory, rewards);
        Assert.Equal(1, rewards.ExperienceMultiplier);
        Assert.Equal(1, rewards.JpMultiplier);
        Assert.Contains("失败", rewards.Notice);
        var writes = memory.WriteCount;
        RewardTick(memory, rewards);
        Assert.Equal(writes, memory.WriteCount);
    }

    [Fact]
    public void BattleRewardsResetOnExitAndRebuildBaselineWhenEnabled()
    {
        var memory = BattleFixture();
        var rewards = new BattleRewards();
        rewards.Configure(2, 2);
        RewardTick(memory, rewards);
        EndBattle(memory);
        RewardTick(memory, rewards);   // 每轮都用重新读取的单位列表判断战斗是否结束
        Assert.Equal(1, rewards.ExperienceMultiplier);
        Assert.Equal(1, rewards.JpMultiplier);
        Assert.Contains("战斗结束", rewards.Notice);
        memory.Seed(BattleRules.InBattleFlag, [1]);
        SeedReward(memory, 16, 0, 100, 100, 40);
        rewards.Configure(5, 5);
        RewardTick(memory, rewards);
        Assert.Equal(0, memory.WriteCount);
        rewards.Configure(1, 1);
        SeedReward(memory, 16, 0, 200, 200, 50);
        rewards.Configure(2, 2);
        RewardTick(memory, rewards);
        Assert.Equal(0, memory.WriteCount);
    }

    [Theory]
    [InlineData(0x28, 1, 100)]
    [InlineData(0xF0, 2, 10000)]
    [InlineData(0x11E, 2, 10000)]
    [InlineData(0xF1, 2, 10)]
    [InlineData(0xE4, 1, 0x19)]
    [InlineData(0xEE, 1, 0x11)]
    [InlineData(0xEF, 1, 0x10)]
    [InlineData(0x118, 2, 10)]
    [InlineData(0x146, 2, 10)]
    [InlineData(0xF0 + 36, 2, 10)]
    public void BattleRewardWhitelistRejectsInvalidWrites(int offset, int width, uint value)
    {
        var memory = BattleFixture();
        var player = BattleRules.Read(memory, 0).Single(unit => unit.Slot == 16);
        Assert.NotNull(Assert.Single(memory.Write(1, [player.Edit(offset, width, value)])).Error);
        Assert.Equal(0, memory.WriteCount);
    }

    [Fact]
    public void BattleRewardWhitelistRejectsNonPlayersAndLowerJobLevels()
    {
        var memory = BattleFixture();
        foreach (var unit in BattleRules.Read(memory, 0).Where(unit => unit.Side is BattleSide.Guest or BattleSide.Enemy))
            foreach (var (offset, width, value) in new[] { (0x28, 1, 50u), (0xF0, 2, 100u), (0x11E, 2, 100u), (0xE4, 1, 0x11u) })
                Assert.NotNull(Assert.Single(memory.Write(1, [unit.Edit(offset, width, value)])).Error);
        memory.Seed(BattleUnit.Start + 16 * BattleUnit.Stride + 0xE4, [0x71]);
        var player = BattleRules.Read(memory, 0).Single(unit => unit.Slot == 16);
        Assert.NotNull(Assert.Single(memory.Write(1, [player.Edit(0xE4, 1, 0x61)])).Error);
        Assert.Equal(0, memory.WriteCount);
    }

    [Fact]
    public void BattleMultiplierViewModelResetsAndSpecialJobsHaveOneLabel()
    {
        var memory = BattleFixture();
        var model = new TrainerViewModel(memory, text: TestText.Legacy());
        model.ApplySnapshot(memory.Snapshot() with { ExperienceMultiplier = 2, JpMultiplier = 5 });
        Assert.Equal(2, model.ExperienceMultiplier);
        Assert.Equal(5, model.JpMultiplier);
        Assert.All(model.Guests, unit => Assert.Equal("专属职业", unit.JobProgressText));
        Assert.Equal("", model.Allies[0].CtLabel);
        model.DisableBattleOptions.Execute(null);
        Assert.Equal(1, model.ExperienceMultiplier);
        Assert.Equal(1, model.JpMultiplier);
        model.ApplySnapshot(memory.Snapshot() with { ExperienceMultiplier = 2, JpMultiplier = 5 });
        model.ApplySnapshot(null);
        Assert.Equal(1, model.ExperienceMultiplier);
        Assert.Equal(1, model.JpMultiplier);
        Assert.Contains("1×", model.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaleBattleSnapshotCannotOverwriteMultipliersBeforeAnotherToggle(bool staleBattleEnded)
    {
        var memory = BattleFixture();
        var model = new TrainerViewModel(memory, text: TestText.Legacy());
        var stale = memory.Snapshot();
        model.ApplySnapshot(stale);
        model.ExperienceMultiplier = 5;
        model.JpMultiplier = 5;
        model.AlwaysMove = true;
        var current = memory.Snapshot() with { ExperienceMultiplier = 5, JpMultiplier = 5, AlwaysMove = true };
        var notified = new List<string?>();
        model.PropertyChanged += (_, args) => notified.Add(args.PropertyName);

        model.ApplySnapshot(staleBattleEnded ? stale with { Battle = [] } : stale);

        Assert.Equal(5, model.ExperienceMultiplier);
        Assert.Equal(5, model.JpMultiplier);
        Assert.True(model.AlwaysMove);
        foreach (var property in new[] { nameof(model.LockHp), nameof(model.InfiniteMp), nameof(model.GuestLockHp),
            nameof(model.FreezeCountdown), nameof(model.LockCt), nameof(model.AlwaysMove), nameof(model.AlwaysAct),
            nameof(model.MaxMoveJump), nameof(model.InstantCast), nameof(model.ExperienceMultiplier), nameof(model.JpMultiplier) })
            Assert.Contains(property, notified);
        model.LockHp = true;
        Assert.Equal(4L, memory.OptionsVersion);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, memory.BattleOptionsCalls.Select(call => call.Version));
        Assert.All(memory.BattleOptionsCalls.Skip(1), call =>
        {
            Assert.Equal(5, call.Experience);
            Assert.Equal(5, call.Jp);
        });
        model.ApplySnapshot(current);
        Assert.Equal(5, model.ExperienceMultiplier);
        Assert.Equal(5, model.JpMultiplier);
    }

    [Fact]
    public void CurrentBattleSnapshotReflectsAutomaticShutdownWithoutSendingOptionsBack()
    {
        var memory = BattleFixture();
        var model = new TrainerViewModel(memory, text: TestText.Legacy());
        model.ApplySnapshot(memory.Snapshot());
        model.ExperienceMultiplier = model.JpMultiplier = 5;
        model.LockHp = model.InfiniteMp = model.GuestLockHp = model.FreezeCountdown = model.LockCt = true;
        model.AlwaysMove = model.AlwaysAct = model.MaxMoveJump = model.InstantCast = true;
        var version = memory.OptionsVersion;
        var calls = memory.BattleOptionsCalls.Count;

        model.ApplySnapshot(memory.Snapshot() with { BattleNotice = "写入失败，已自动关闭" });

        Assert.True(model.InBattle);
        Assert.All(new[] { model.LockHp, model.InfiniteMp, model.GuestLockHp, model.FreezeCountdown, model.LockCt,
            model.AlwaysMove, model.AlwaysAct, model.MaxMoveJump, model.InstantCast }, value => Assert.False(value));
        Assert.Equal(1, model.ExperienceMultiplier);
        Assert.Equal(1, model.JpMultiplier);
        Assert.Contains("自动关闭", model.Status);
        Assert.Equal(version, memory.OptionsVersion);
        Assert.Equal(calls, memory.BattleOptionsCalls.Count);
    }

    private static string BackupTestDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "backup-test-" + Guid.NewGuid().ToString("N"));

    private static string CreateSave(string root, string steamId = "123")
    {
        var directory = Path.Combine(root, "documents", "My Games", "FINAL FANTASY TACTICS - The Ivalice Chronicles", "Steam", steamId);
        Directory.CreateDirectory(Path.Combine(directory, "nested"));
        File.WriteAllBytes(Path.Combine(directory, "save.png"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(directory, "nested", "settings.dat"), "unchanged");
        return directory;
    }

    [Fact]
    public void SaveBackupsCopyEveryAccountThrottlePersistAndKeepTwenty()
    {
        var root = BackupTestDirectory();
        try
        {
            var save = CreateSave(root);
            CreateSave(root, "456");
            var now = new DateTime(2026, 10, 17, 10, 0, 0);
            var backups = new SaveBackups(Path.Combine(root, "documents"), root, () => now);
            Assert.Null(backups.LastBackup);
            Assert.Null(backups.EnsureBackup());
            var first = Path.Combine(backups.BackupDirectory, "20261017-100000");
            foreach (var id in new[] { "123", "456" })
            {
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(first, id, "save.png")));
                Assert.Equal("unchanged", File.ReadAllText(Path.Combine(first, id, "nested", "settings.dat")));
            }
            now = now.AddMinutes(10);
            Assert.Null(backups.EnsureBackup());
            Assert.Single(Directory.GetDirectories(backups.BackupDirectory));
            backups = new SaveBackups(Path.Combine(root, "documents"), root, () => now);
            Assert.Equal(new DateTime(2026, 10, 17, 10, 0, 0), backups.LastBackup);
            Assert.Null(backups.EnsureBackup());
            for (var i = 0; i < 21; i++)
            {
                now = now.AddMinutes(11);
                Assert.Null(backups.EnsureBackup());
            }
            Assert.Equal(20, Directory.GetDirectories(backups.BackupDirectory).Length);
            Assert.False(Directory.Exists(first));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(save, "save.png")));
            Assert.Equal("unchanged", File.ReadAllText(Path.Combine(save, "nested", "settings.dat")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task BackupFailureDoesNotBlockWriteAndIsShownInStatus()
    {
        var root = BackupTestDirectory();
        try
        {
            var backups = new SaveBackups(Path.Combine(root, "missing"), root);
            var memory = BattleFixture();
            var model = new TrainerViewModel(memory, text: TestText.Legacy(), backups: backups);
            model.ApplySnapshot(memory.Snapshot());
            model.Gil.Value = 1234;
            await model.WriteAsync();
            Assert.Equal(1, memory.WriteCount);
            Assert.Contains("存档备份失败", model.Status);
            Assert.Null(backups.LastBackup);
            Assert.False(Directory.Exists(backups.BackupDirectory));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task BackupCompletesBeforeGameWriteAndUpdatesRecentTime()
    {
        var root = BackupTestDirectory();
        try
        {
            CreateSave(root);
            var backups = new SaveBackups(Path.Combine(root, "documents"), root);
            var memory = BattleFixture();
            memory.BeforeWrite = () =>
            {
                Assert.NotNull(backups.LastBackup);
                Assert.Single(Directory.GetDirectories(backups.BackupDirectory));
            };
            var model = new TrainerViewModel(memory, text: TestText.Legacy(), backups: backups);
            model.ApplySnapshot(memory.Snapshot());
            model.Gil.Value = 1234;
            await model.WriteAsync();
            Assert.Equal(1, memory.WriteCount);
            Assert.DoesNotContain("尚无备份", model.BackupText);
            Assert.DoesNotContain("失败", model.Status);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public void FailedCopyIsNotCountedAndDoesNotDeleteOlderBackups()
    {
        var root = BackupTestDirectory();
        try
        {
            var save = CreateSave(root);
            var now = new DateTime(2026, 10, 17, 10, 0, 0);
            var backups = new SaveBackups(Path.Combine(root, "documents"), root, () => now);
            Assert.Null(backups.EnsureBackup());
            now = now.AddMinutes(11);
            using var locked = new FileStream(Path.Combine(save, "save.png"), FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.Contains("存档备份失败", backups.EnsureBackup());
            Assert.Equal(new DateTime(2026, 10, 17, 10, 0, 0), backups.LastBackup);
            Assert.Single(Directory.GetDirectories(backups.BackupDirectory));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    // 模拟战斗真正结束：遭遇战标志回到 0，且敌人全部倒下（与实测的战后残留状态一致）
    private static void EndBattle(TestMemory memory)
    {
        memory.Seed(BattleRules.InBattleFlag, [0]);
        foreach (var slot in Enumerable.Range(0, BattleUnit.Count))
        {
            var start = BattleUnit.Start + (ulong)slot * BattleUnit.Stride;
            if ((memory.ReadBytes(start + 5, 1)[0] & 0x30) == 0x10)
            {
                memory.Seed(start + 0x30, [0, 0]);
                memory.Seed(start + 0x61, [0x20]);
            }
        }
    }

    [Fact]
    public void BattleFlagZeroMeansNotInBattleEvenIfArrayPersists()
    {
        // 实测：胜利后战斗数组原样保留，只有 base+0x1853C08 回到 0
        // 遭遇战胜利后：标志为 0 且敌人全倒 → 不在战斗
        var memory = BattleFixture();
        memory.Seed(BattleRules.InBattleFlag, [0]);
        foreach (var slot in new[] { 2, 4, 5, 6, 7 })
            memory.Seed(BattleUnit.Start + (ulong)slot * BattleUnit.Stride + 0x30, [0, 0]);
        var units = BattleRules.Read(memory, 0);
        Assert.Empty(units);
        Assert.False(BattleRules.IsInBattle(units));
    }

    [Fact]
    public void StoryBattleWithFlagZeroStillCountsWhileBothSidesAlive()
    {
        // 剧情战实测标志为 0，但双方都有活着的单位 → 仍算战斗中
        var memory = BattleFixture();
        memory.Seed(BattleRules.InBattleFlag, [0]);
        Assert.True(BattleRules.IsInBattle(BattleRules.Read(memory, 0)));
    }

    [Fact]
    public void BattleReadsOnlyTwentyThreeSlotsAndSkipsInvalidCopy()
    {
        var units = BattleRules.Read(BattleFixture(), 0);
        Assert.Equal(13, units.Count);
        Assert.DoesNotContain(units, unit => unit.Slot is 20 or 23);
        Assert.Contains(units, unit => unit.Slot == 3 && unit.Hp == 0);
        Assert.True(BattleRules.IsInBattle(units));
        Assert.False(BattleRules.IsInBattle(units.Where(unit => unit.Faction != 0).ToArray()));
        // 标志为 1 时，敌人全倒（打完最后一只到场景结束之间）仍算战斗中，与实测一致
        Assert.True(BattleRules.IsInBattle(units.Where(unit => unit.Faction != 1).ToArray()));
        Assert.False(BattleRules.IsInBattle([]));
    }

    [Fact]
    public void BattleMaintenanceWritesOnlyMissingFriendlyResourcesWithoutRevival()
    {
        var memory = BattleFixture();
        var ally = BattleUnit.Start + 16 * BattleUnit.Stride;
        var down = BattleUnit.Start + 17 * BattleUnit.Stride;
        memory.Seed(ally + 0x30, [10, 0]);
        memory.Seed(ally + 0x34, [3, 0]);
        memory.Seed(down + 0x30, [0, 0]);
        var units = BattleRules.Read(memory, 0);
        Assert.Single(BattleRules.Maintain(units, true, false));
        Assert.Single(BattleRules.Maintain(units, false, true));
        Assert.Empty(BattleRules.Maintain(units, false, false));
        var edits = BattleRules.Maintain(units, true, true);
        Assert.Equal(2, edits.Count);
        Assert.All(memory.Write(1, edits), result => Assert.Null(result.Error));
        Assert.Equal(2, memory.WriteCount);
        Assert.Equal(new byte[] { 0, 0 }, memory.ReadBytes(down + 0x30, 2));
        Assert.Empty(BattleRules.Maintain(BattleRules.Read(memory, 0), true, true));
        EndBattle(memory);
        Assert.Empty(BattleRules.Maintain(BattleRules.Read(memory, 0), true, true));
    }

    [Theory]
    [InlineData(16, 1, 0, 44, true)]
    [InlineData(16, 0, 0, 44, false)]
    [InlineData(16, 2, 0, 44, false)]
    [InlineData(16, 1, 0x20, 44, false)]
    [InlineData(16, 1, 0, 0, false)]
    [InlineData(0, 1, 0, 52, false)]
    [InlineData(2, 1, 0, 37, false)]
    [InlineData(22, 1, 0, 18, false)]
    public void AlwaysMoveAndActOnlyAffectCurrentLivingPlayer(int slot, byte active, byte status, byte hp, bool expected)
    {
        var memory = BattleFixture();
        var start = BattleUnit.Start + (ulong)slot * BattleUnit.Stride;
        memory.Seed(start + 0x1B8, [active, 0xA3, 1]);
        memory.Seed(start + 0x61, [status]);
        memory.Seed(start + 0x30, [hp, 0]);
        var units = BattleRules.Read(memory, 0);
        var edits = BattleRules.Maintain(units, false, false, alwaysMove: true, alwaysAct: true);
        Assert.Equal(expected ? 2 : 0, edits.Count);
        Assert.All(edits, edit => Assert.True(edit.Offset == start + 0x1B9 || edit.Offset == start + 0x1BA));
        var results = GameSession.ApplyEdits(memory, memory.WriteBytes, 0, edits);
        Assert.All(results, result => Assert.Null(result.Error));
        Assert.Equal(expected ? (byte)0xA0 : (byte)0xA3, memory.ReadBytes(start + 0x1B9, 1)[0]);
        Assert.Equal(expected ? (byte)0 : (byte)1, memory.ReadBytes(start + 0x1BA, 1)[0]);
    }

    [Theory]
    [InlineData(16, 1, 0, 44, true)]
    [InlineData(16, 0, 0, 44, false)]
    [InlineData(16, 2, 0, 44, false)]
    [InlineData(16, 1, 0x20, 44, false)]
    [InlineData(16, 1, 0, 0, false)]
    [InlineData(0, 1, 0, 52, false)]
    [InlineData(2, 1, 0, 37, false)]
    [InlineData(22, 1, 0, 18, false)]
    public void FastTurnTickOnlyWritesCurrentLivingPlayer(int slot, byte active, byte status, byte hp, bool expected)
    {
        var memory = BattleFixture();
        var units = BattleRules.Read(memory, 0);
        var start = BattleUnit.Start + (ulong)slot * BattleUnit.Stride;
        memory.Seed(start + 0x1B8, [active, 0xA3, 7]);
        memory.Seed(start + 0x61, [status]);
        memory.Seed(start + 0x30, [hp, 0]);
        memory.Reads.Clear();
        BattleRules.TurnTick(memory, memory.WriteBytes, 0, units, true, true);
        Assert.Equal(expected ? 2 : 0, memory.WriteCount);
        Assert.All(memory.Reads, read => Assert.InRange(read.Length, 1, 5));
        Assert.Equal(new byte[] { active, expected ? (byte)0xA0 : (byte)0xA3, expected ? (byte)0 : (byte)7 },
            memory.ReadBytes(start + 0x1B8, 3));
    }

    [Fact]
    public void FastTurnTickStillClearsWhenUnitLevelsUpMidAction()
    {
        // 实测：击杀敌人当场升级时，等级变化曾让身份校验挡下清除，回合被判定为已行动
        var memory = BattleFixture();
        var units = BattleRules.Read(memory, 0);
        var start = BattleUnit.Start + 16 * BattleUnit.Stride;
        memory.Seed(start + 0x1B8, [1, 0x03, 1]);
        memory.Seed(start + 0x29, [(byte)(memory.ReadBytes(start + 0x29, 1)[0] + 1)]);
        memory.Seed(start + 0x28, [5]);
        BattleRules.TurnTick(memory, memory.WriteBytes, 0, units, true, true);
        Assert.Equal(new byte[] { 1, 0, 0 }, memory.ReadBytes(start + 0x1B8, 3));
    }

    [Fact]
    public void FastTurnTransformationNeverSetsBits()
    {
        foreach (var move in new[] { false, true })
        foreach (var act in new[] { false, true })
        for (var value = 0; value <= 255; value++)
        {
            var after = BattleRules.ClearTurnFlags(1, (byte)value, (byte)value, move, act);
            Assert.Equal(value & ~((move ? 1 : 0) | (act ? 2 : 0)), (int)after.Flags);
            Assert.Equal(act ? 0 : value, (int)after.Done);
            Assert.Equal(0, after.Flags & ~value);
            Assert.Equal(((byte)value, (byte)value),
                BattleRules.ClearTurnFlags(0, (byte)value, (byte)value, move, act));
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 2)]
    [InlineData(1, 0xA0)]
    [InlineData(1, 0xB3)]
    [InlineData(2, 2)]
    public void FastTurnTickRechecksActiveAndTargetBeforeEachWrite(int index, byte replacement)
    {
        var memory = BattleFixture();
        var start = BattleUnit.Start + 16 * BattleUnit.Stride;
        memory.Seed(start + 0x1B8, [1, 0xA3, 1]);
        var units = BattleRules.Read(memory, 0);
        var reads = 0;
        memory.BeforeRead = (address, length) =>
        {
            if (address == start + 0x1B8 && length == 3 && ++reads == (index == 2 ? 3 : 2))
                memory.Seed(start + 0x1B8 + (ulong)index, [replacement]);
        };
        BattleRules.TurnTick(memory, memory.WriteBytes, 0, units, true, true);
        Assert.Equal(index == 0 ? 0 : 1, memory.WriteCount);
        Assert.Equal(replacement, memory.ReadBytes(start + 0x1B8 + (ulong)index, 1)[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(0x1FC)]
    public void FastTurnTickRejectsReusedSlot(int field)
    {
        var memory = BattleFixture();
        var start = BattleUnit.Start + 16 * BattleUnit.Stride;
        var units = BattleRules.Read(memory, 0);
        memory.Seed(start + 0x1B8, [1, 3, 1]);
        memory.Seed(start + (ulong)field, [(byte)(memory.ReadBytes(start + (ulong)field, 1)[0] ^ 1)]);
        BattleRules.TurnTick(memory, memory.WriteBytes, 0, units, true, true);
        Assert.Equal(0, memory.WriteCount);
    }

    [Fact]
    public void FastTurnTickStopsOnWriteFailureAndDoesNothingWhenDisabled()
    {
        var memory = BattleFixture();
        var start = BattleUnit.Start + 16 * BattleUnit.Stride;
        var units = BattleRules.Read(memory, 0);
        memory.Seed(start + 0x1B8, [1, 3, 1]);
        memory.Reads.Clear();
        BattleRules.TurnTick(memory, memory.WriteBytes, 0, units, false, false);
        Assert.Empty(memory.Reads);
        BattleRules.TurnTick(memory, memory.WriteBytes, 0, [], true, true);
        Assert.Empty(memory.Reads);
        memory.FailWrite = start + 0x1B9;
        Assert.Throws<IOException>(() => BattleRules.TurnTick(memory, memory.WriteBytes, 0, units, true, true));
        Assert.Equal(1, memory.WriteCount);
        Assert.Equal((byte)1, memory.ReadBytes(start + 0x1BA, 1)[0]);
    }

    [Theory]
    [InlineData(false, false, 0xA3, 1, 0xA3, 1)]
    [InlineData(true, false, 0xA3, 1, 0xA2, 1)]
    [InlineData(false, true, 0xA3, 1, 0xA1, 0)]
    [InlineData(true, true, 0xA3, 1, 0xA0, 0)]
    [InlineData(true, true, 0xA0, 0, 0xA0, 0)]
    [InlineData(false, true, 0xA0, 1, 0xA0, 0)]
    public void TurnFlagsOnlyClearSelectedBitsAndCombineWrites(bool move, bool act, byte flags, byte done,
        byte expectedFlags, byte expectedDone)
    {
        var memory = BattleFixture();
        var start = BattleUnit.Start + 16 * BattleUnit.Stride;
        memory.Seed(start + 0x1B8, [1, flags, done]);
        var edits = BattleRules.Maintain(BattleRules.Read(memory, 0), false, false, alwaysMove: move, alwaysAct: act);
        Assert.Equal(flags == expectedFlags ? 0 : 1, edits.Count(edit => edit.Offset == start + 0x1B9));
        Assert.Equal(done == expectedDone ? 0 : 1, edits.Count(edit => edit.Offset == start + 0x1BA));
        Assert.All(GameSession.ApplyEdits(memory, memory.WriteBytes, 0, edits), result => Assert.Null(result.Error));
        Assert.Equal(new byte[] { 1, expectedFlags, expectedDone }, memory.ReadBytes(start + 0x1B8, 3));
        Assert.Empty(BattleRules.Maintain(BattleRules.Read(memory, 0), false, false, alwaysMove: move, alwaysAct: act));
    }

    [Theory]
    [InlineData(0x1B9, 1, 0xA0, 0xA1)]
    [InlineData(0x1B9, 1, 0xA3, 0x83)]
    [InlineData(0x1B9, 1, 0xA3, 0xE0)]
    [InlineData(0x1B9, 2, 0xA3, 0xA0)]
    [InlineData(0x1BA, 1, 1, 2)]
    [InlineData(0x1BA, 2, 1, 0)]
    public void TurnWhitelistRejectsSettingBitsOtherBitsAndWrongWidths(int field, int width, byte before, uint after)
    {
        var memory = BattleFixture();
        var start = BattleUnit.Start + 16 * BattleUnit.Stride;
        memory.Seed(start + 0x1B8, [1]);
        memory.Seed(start + (ulong)field, [before]);
        var player = BattleRules.Read(memory, 0).Single(unit => unit.Slot == 16);
        Assert.Throws<IOException>(() => BattleRules.Validate(memory, 0, player.Edit(field, width, after)));
        Assert.Equal(0, memory.WriteCount);
    }

    [Theory]
    [InlineData(16, 0, 0, 44)]
    [InlineData(16, 2, 0, 44)]
    [InlineData(16, 1, 0x20, 44)]
    [InlineData(16, 1, 0, 0)]
    [InlineData(0, 1, 0, 52)]
    [InlineData(2, 1, 0, 37)]
    public void TurnWhitelistRechecksCurrentLivingPlayer(int slot, byte active, byte status, byte hp)
    {
        var memory = BattleFixture();
        var start = BattleUnit.Start + (ulong)slot * BattleUnit.Stride;
        memory.Seed(start + 0x1B8, [1, 3, 1]);
        var unit = BattleRules.Read(memory, 0).Single(unit => unit.Slot == slot);
        var edits = new[] { unit.Edit(0x1B9, 1, 0), unit.Edit(0x1BA, 1, 0) };
        memory.Seed(start + 0x1B8, [active]);
        memory.Seed(start + 0x61, [status]);
        memory.Seed(start + 0x30, [hp, 0]);
        Assert.All(edits, edit => Assert.Throws<IOException>(() => BattleRules.Validate(memory, 0, edit)));
        Assert.All(GameSession.ApplyEdits(memory, memory.WriteBytes, 0, edits), result => Assert.NotNull(result.Error));
        Assert.Equal(0, memory.WriteCount);
    }

    [Fact]
    public void TurnTogglesResetOnDisableExitDisconnectAndFailureSnapshot()
    {
        var memory = BattleFixture();
        var model = new TrainerViewModel(memory, text: TestText.Legacy());
        var snapshot = memory.Snapshot();
        model.AlwaysMove = model.AlwaysAct = model.MaxMoveJump = model.InstantCast = true;
        Assert.False(model.AlwaysMove);
        Assert.False(model.AlwaysAct);
        Assert.False(model.MaxMoveJump);
        Assert.False(model.InstantCast);
        model.ApplySnapshot(snapshot);
        model.AlwaysMove = model.AlwaysAct = model.MaxMoveJump = model.InstantCast = true;
        Assert.True(model.AlwaysMove);
        Assert.True(model.AlwaysAct);
        Assert.True(model.MaxMoveJump);
        Assert.True(model.InstantCast);
        model.DisableBattleOptions.Execute(null);
        Assert.False(model.AlwaysMove);
        Assert.False(model.AlwaysAct);
        Assert.False(model.MaxMoveJump);
        Assert.False(model.InstantCast);
        snapshot = snapshot with { OptionsVersion = memory.OptionsVersion };
        foreach (var next in new GameSnapshot?[]
        {
            snapshot with { Battle = [] },
            null,
            snapshot with { BattleNotice = "战斗持续效果写入失败，已自动关闭" }
        })
        {
            model.ApplySnapshot(snapshot with { AlwaysMove = true, AlwaysAct = true, MaxMoveJump = true, InstantCast = true });
            Assert.True(model.AlwaysMove);
            Assert.True(model.AlwaysAct);
            Assert.True(model.MaxMoveJump);
            Assert.True(model.InstantCast);
            model.ApplySnapshot(next);
            Assert.False(model.AlwaysMove);
            Assert.False(model.AlwaysAct);
            Assert.False(model.MaxMoveJump);
            Assert.False(model.InstantCast);
            Assert.Contains("自动关闭", model.Status);
        }
    }

    [Fact]
    public void WeakenStagesOnlyLivingEnemiesAndWritesOnlyBattleArray()
    {
        var memory = BattleFixture();
        var model = new TrainerViewModel(memory, text: TestText.Legacy());
        model.ApplySnapshot(memory.Snapshot());
        model.WeakenEnemies.Execute(null);
        Assert.Equal(5, model.Pending.Count);
        Assert.Equal(0, memory.WriteCount);
        Assert.Contains(model.Pending, field => field.ChangeText.Contains("敌方 盗贼 HP")
            && field.Original == 37 && field.Value == 1);
        var edits = model.Pending.Select(field => field.ToEdit()).ToArray();
        Assert.All(edits, edit => Assert.Equal((byte)1, edit.BattleIdentity!.Faction));
        Assert.All(memory.Write(1, edits), result => Assert.Null(result.Error));
        Assert.Equal(5, memory.WriteCount);
        Assert.Equal(0u, BattleRules.Read(memory, 0).Single(unit => unit.Slot == 3).Hp);
        Assert.Equal(18u, BattleRules.Read(memory, 0).Single(unit => unit.Slot == 22).Hp);
    }

    [Theory]
    [InlineData(1, 0xFF)]
    [InlineData(1, 1)]
    [InlineData(5, 0)]
    [InlineData(0, 0x81)]
    public void BattleWritesRecheckIdentity(int offset, byte replacement)
    {
        var memory = BattleFixture();
        var edit = BattleRules.Read(memory, 0).First(unit => unit.Side == BattleSide.Enemy).Edit(0x30, 2, 1);
        memory.Seed(BattleUnit.Start + 2 * BattleUnit.Stride + (ulong)offset, [replacement]);
        Assert.NotNull(Assert.Single(memory.Write(1, [edit])).Error);
        Assert.Equal(0, memory.WriteCount);
    }

    [Fact]
    public void FailureStreakTripsOnlyOnConsecutiveFailures()
    {
        var streak = new FailureStreak(3);
        Assert.False(streak.Register(true));
        Assert.False(streak.Register(true));
        Assert.False(streak.Register(false));   // 中间成功一次就清零
        Assert.False(streak.Register(true));
        Assert.False(streak.Register(true));
        Assert.True(streak.Register(true));
        Assert.Equal(0, streak.Count);
        Assert.False(streak.Register(true));
    }

    [Fact]
    public void RewardWriteRaceRecoversOnNextTick()
    {
        // 第一次写入时游戏刚好改了值 → 失败；下一轮按新值重算，获得量不丢
        var memory = BattleFixture();
        var rewards = new BattleRewards();
        rewards.Configure(2, 2);
        SeedReward(memory, 16, 0, 100, 100, 40);
        RewardTick(memory, rewards);
        SeedReward(memory, 16, 0, 110, 110, 40);
        var address = BattleUnit.Start + 16 * BattleUnit.Stride + 0xF0;
        memory.FailWrite = address;
        RewardTick(memory, rewards);
        memory.FailWrite = null;
        SeedReward(memory, 16, 0, 120, 120, 40);
        RewardTick(memory, rewards);
        Assert.Equal(2, rewards.JpMultiplier);
        var player = BattleRules.Read(memory, 0).Single(unit => unit.Slot == 16);
        Assert.Equal(140u, player.Read(0xF0, 2));
    }

    [Fact]
    public void KeepBattleOptionsSurvivesBattleEndAndCanBeSetOutsideBattle()
    {
        var memory = BattleFixture();
        var model = new TrainerViewModel(memory, text: TestText.Legacy());
        model.ApplySnapshot(memory.Snapshot());
        model.KeepBattleOptions = true;
        model.AlwaysAct = true;
        model.JpMultiplier = 5;
        Assert.True(memory.KeepOptions);
        // 会话保留时，战斗结束的快照仍带着这些设置
        EndBattle(memory);
        model.ApplySnapshot(memory.Snapshot() with { AlwaysAct = true, JpMultiplier = 5 });
        Assert.False(model.InBattle);
        Assert.True(model.AlwaysAct);
        Assert.Equal(5, model.JpMultiplier);
        Assert.True(model.CanSetBattleOptions);
        model.LockHp = true;
        Assert.True(model.LockHp);
        // 断开连接也不清
        model.ApplySnapshot(null);
        Assert.True(model.AlwaysAct);
        // 不在战斗时关掉保留：恢复旧行为，全部关闭
        model.ApplySnapshot(memory.Snapshot() with { AlwaysAct = true, LockHp = true, JpMultiplier = 5 });
        model.KeepBattleOptions = false;
        Assert.False(model.AlwaysAct);
        Assert.False(model.LockHp);
        Assert.Equal(1, model.JpMultiplier);
        Assert.False(memory.KeepOptions);
        Assert.False(model.CanSetBattleOptions);
        model.LockHp = true;
        Assert.False(model.LockHp);
    }

    [Fact]
    public void KeepBattleOptionsPersistsAcrossRestarts()
    {
        var path = Path.Combine(Path.GetTempPath(), "fft-keep-" + Guid.NewGuid().ToString("N"), "battle-options.json");
        try
        {
            var memory = BattleFixture();
            var model = new TrainerViewModel(memory, preferencesPath: path);
            model.ApplySnapshot(memory.Snapshot());
            model.KeepBattleOptions = true;
            model.AlwaysMove = true;
            model.ExperienceMultiplier = 10;
            var restarted = new TrainerViewModel(memory, preferencesPath: path);
            Assert.True(restarted.KeepBattleOptions);
            Assert.True(restarted.AlwaysMove);
            Assert.Equal(10, restarted.ExperienceMultiplier);
            Assert.True(memory.KeepOptions);
            // 关掉保留后，下次启动什么都不恢复
            restarted.KeepBattleOptions = false;
            var again = new TrainerViewModel(memory, preferencesPath: path);
            Assert.False(again.KeepBattleOptions);
            Assert.False(again.AlwaysMove);
            // 文件损坏当作没有设置
            File.WriteAllText(path, "{not json");
            Assert.False(new TrainerViewModel(memory, preferencesPath: path).KeepBattleOptions);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void KeptMultipliersSurviveBattleEndButBaselinesReset()
    {
        var memory = BattleFixture();
        var rewards = new BattleRewards { KeepMultipliers = true };
        rewards.Configure(2, 5);
        RewardTick(memory, rewards);
        EndBattle(memory);
        RewardTick(memory, rewards);
        Assert.Equal(2, rewards.ExperienceMultiplier);
        Assert.Equal(5, rewards.JpMultiplier);
        Assert.Null(rewards.Notice);
        // 下一场重新建基线：开场的数值不能被当成「获得」
        memory.Seed(BattleRules.InBattleFlag, [1]);
        SeedReward(memory, 16, 0, 100, 100, 40);
        RewardTick(memory, rewards);
        Assert.Equal(0, memory.WriteCount);
        // 写入失败的复位不受保留影响
        rewards.Reset("写入失败");
        Assert.Equal(1, rewards.JpMultiplier);
    }

    [Fact]
    public void DownUnitWithNonZeroHpNeverStagesAnEdit()
    {
        // 2026-10-17 雪地剧情战：倒地的人质蒂塔 HP 是 1，界面每轮刷新都凭空生成「蒂塔 HP 1 → 0」
        var memory = BattleFixture();
        memory.Seed(BattleUnit.Start + 4 * BattleUnit.Stride + 0x61, [0x20]);
        var model = new TrainerViewModel(memory, text: TestText.Legacy());
        model.ApplySnapshot(memory.Snapshot());
        model.ApplySnapshot(memory.Snapshot());
        Assert.Empty(model.Pending);
        var down = model.Enemies.Single(unit => unit.Unit.Slot == 4);
        Assert.Equal(35u, down.Hp.Value);
        down.Hp.Value = 0;
        Assert.Equal(35u, down.Hp.Value);
        // 活着时建的卡片，后来倒下：收紧上限不能抛异常，也不能生成改动
        var alive = model.Enemies.Single(unit => unit.Unit.Slot == 5);
        memory.Seed(BattleUnit.Start + 5 * BattleUnit.Stride + 0x61, [0x20]);
        model.ApplySnapshot(memory.Snapshot());
        Assert.Empty(model.Pending);
        alive.Hp.Value = 1;
        Assert.Equal(6u, alive.Hp.Value);
    }

    [Fact]
    public void BattleRejectsRevivalUnknownFactionAndOverflow()
    {
        var memory = BattleFixture();
        var units = BattleRules.Read(memory, 0);
        MemoryEdit[] edits = [units.Single(unit => unit.Slot == 3).Edit(0x30, 2, 1),
            units.Single(unit => unit.Slot == 22).Edit(0x30, 2, 1),
            units.Single(unit => unit.Slot == 2).Edit(0x41, 1, 101), units.Single(unit => unit.Slot == 2).Edit(0x2A, 1, 101),
            units.Single(unit => unit.Slot == 2).Edit(0x30, 2, 38), units.Single(unit => unit.Slot == 2).Edit(0x34, 2, 8),
            units.Single(unit => unit.Slot == 2).Edit(0x30, 2, 1) with { BattleIdentity = null }];
        Assert.All(memory.Write(1, edits), result => Assert.NotNull(result.Error));
        Assert.Equal(0, memory.WriteCount);
        var model = new TrainerViewModel(memory, text: TestText.Legacy());
        model.ApplySnapshot(memory.Snapshot());
        var down = model.Enemies.Single(unit => unit.Unit.Slot == 3);
        down.Hp.Value = 10;
        Assert.Equal(0u, down.Hp.Value);
        Assert.False(down.CanChangeHp);
    }

    [Fact]
    public void BattlePendingIsDiscardedOnExitAndSlotReplacement()
    {
        var memory = BattleFixture();
        var model = new TrainerViewModel(memory, text: TestText.Legacy());
        model.ApplySnapshot(memory.Snapshot());
        model.Enemies[0].Hp.Value = 1;
        memory.Seed(BattleUnit.Start + 2 * BattleUnit.Stride + 1, [1]);
        model.ApplySnapshot(memory.Snapshot());
        Assert.Empty(model.Pending);
        model.Enemies[0].Hp.Value = 1;
        foreach (var ally in model.Allies)
            memory.Seed(ally.Unit.Offset + 1, [0xFF]);
        model.ApplySnapshot(memory.Snapshot());
        Assert.False(model.InBattle);
        Assert.Empty(model.Pending);
        Assert.Empty(model.Allies);
        Assert.Empty(model.Enemies);
    }

    [Fact]
    public void BattleDisconnectClearsTogglesAndExplainsWhy()
    {
        var memory = BattleFixture();
        var model = new TrainerViewModel(memory, text: TestText.Legacy());
        model.ApplySnapshot(memory.Snapshot() with { LockHp = true, InfiniteMp = true, GuestLockHp = true, FreezeCountdown = true });
        Assert.True(model.LockHp);
        Assert.True(model.InfiniteMp);
        Assert.True(model.GuestLockHp);
        Assert.True(model.FreezeCountdown);
        model.ApplySnapshot(null);
        Assert.False(model.LockHp);
        Assert.False(model.InfiniteMp);
        Assert.False(model.GuestLockHp);
        Assert.False(model.FreezeCountdown);
        Assert.False(model.InBattle);
        Assert.Contains("自动关闭", model.Status);
    }

    [Theory]
    [InlineData(0, 0, BattleSide.Player)]
    [InlineData(0, 49, BattleSide.Player)]
    [InlineData(0, 50, BattleSide.Guest)]
    [InlineData(0, 255, BattleSide.Guest)]
    [InlineData(1, 0, BattleSide.Enemy)]
    [InlineData(1, 255, BattleSide.Enemy)]
    [InlineData(2, 0, BattleSide.Other)]
    [InlineData(3, 50, BattleSide.Other)]
    public void BattleClassificationUsesFactionAndRosterIndex(byte faction, byte rosterIndex, BattleSide expected)
    {
        var data = new byte[BattleUnit.Stride];
        data[2] = rosterIndex;
        data[5] = (byte)(faction << 4);
        // 调色板不影响阵营（2026-10-17 雪地战敌人调色板是 1）
        data[4] = 1;
        data[0x32] = 1;
        Assert.Equal(expected, BattleRules.Classify(new BattleUnit(0, data)));
        data[1] = 0xFF;
        Assert.Equal(BattleSide.Other, BattleRules.Classify(new BattleUnit(0, data)));
    }

    [Fact]
    public void PlayerAndGuestLocksAreIndependentAndGuestsNeverReceiveInfiniteMp()
    {
        var memory = BattleFixture();
        foreach (var slot in new[] { 0, 1, 16 })
        {
            var start = BattleUnit.Start + (ulong)(slot * BattleUnit.Stride);
            memory.Seed(start + 0x30, [12, 0]);
            memory.Seed(start + 0x34, [2, 0]);
        }
        var units = BattleRules.Read(memory, 0);
        var playerEdits = BattleRules.Maintain(units, true, true);
        Assert.Equal(2, playerEdits.Count);
        Assert.All(playerEdits, edit => Assert.Equal(16, (int)((edit.Offset - BattleUnit.Start) / BattleUnit.Stride)));
        var guestEdits = BattleRules.Maintain(units, false, false, true);
        Assert.Equal(2, guestEdits.Count);
        Assert.All(guestEdits, edit => Assert.True((edit.Offset - BattleUnit.Start) / BattleUnit.Stride < 2));
        Assert.All(memory.Write(1, guestEdits), result => Assert.Null(result.Error));
        Assert.Equal(12u, BattleRules.Read(memory, 0).Single(unit => unit.Slot == 16).Hp);
        memory.Seed(BattleUnit.Start + 0x30, [0, 0]);
        Assert.Empty(BattleRules.Maintain(BattleRules.Read(memory, 0), false, false, true));
        var model = new TrainerViewModel(memory, text: TestText.Legacy());
        model.ApplySnapshot(memory.Snapshot());
        Assert.Equal(4, model.Allies.Count);
        Assert.Equal(2, model.Guests.Count);
        Assert.Equal(6, model.Enemies.Count);
        Assert.Equal(new uint[] { 22, 2 }, model.Guests.Select(unit => unit.Ct.Value));
    }

    [Fact]
    public void BattleWritesRejectChangedPlayerGuestClassification()
    {
        var memory = BattleFixture();
        var unit = BattleRules.Read(memory, 0).Single(unit => unit.Slot == 16);
        var edit = unit.Edit(0x30, 2, 1);
        memory.Seed(unit.Offset + 2, [50]);
        Assert.NotNull(Assert.Single(memory.Write(1, [edit])).Error);
        Assert.Equal(0, memory.WriteCount);
    }

    [Fact]
    public void BattleNamesUseConfirmedNameIdAndGuestJobFallbackWhileCtRefreshes()
    {
        var memory = BattleFixture();
        var text = new GameTextCatalog();
        text.Parse("jobs", """
            [{"id":4,"name":"迪利塔"},{"id":7,"name":"阿加斯"}]
            """);
        text.Parse("unit-names", """
            [{"id":363,"name":"测试盗贼"}]
            """);
        memory.Seed(BattleUnit.Start + 2 * BattleUnit.Stride + 0x1FC, [0x6B, 0x01]);
        var model = new TrainerViewModel(memory, text: text);
        model.ApplySnapshot(memory.Snapshot());
        Assert.Equal("测试盗贼", model.Enemies[0].Name);
        Assert.Equal(new[] { "迪利塔", "阿加斯" }, model.Guests.Select(unit => unit.Name));
        Assert.Equal(22u, model.Guests[0].Ct.Value);
        Assert.Contains(model.Fields, field => field.Offset == BattleUnit.Start + 0x41);
        memory.Seed(BattleUnit.Start + 0x41, [0]);
        model.ApplySnapshot(memory.Snapshot());
        Assert.Equal(0u, model.Guests[0].Ct.Value);
        Assert.Empty(model.Pending);
    }

    [Theory]
    [InlineData(16, 0x20, 0, true)]
    [InlineData(16, 0x20, 1, true)]
    [InlineData(16, 0x20, 2, true)]
    [InlineData(16, 0x21, 2, true)]
    [InlineData(16, 0x20, 3, false)]
    [InlineData(16, 0x20, 4, false)]
    [InlineData(16, 0x20, 0xFF, false)]
    [InlineData(16, 0, 2, false)]
    [InlineData(0, 0x20, 2, false)]
    [InlineData(1, 0x20, 2, false)]
    [InlineData(2, 0x20, 2, false)]
    [InlineData(22, 0x20, 2, false)]
    public void CountdownFreezeOnlyWritesApplicableDownPlayers(int slot, byte status, byte countdown, bool expected)
    {
        var memory = BattleFixture();
        var start = BattleUnit.Start + (ulong)(slot * BattleUnit.Stride);
        memory.Seed(start + 0x61, [status]);
        memory.Seed(start + 0x07, [countdown]);
        var units = BattleRules.Read(memory, 0);
        var edits = BattleRules.Maintain(units, false, false, freezeCountdown: true);
        Assert.Equal(expected ? 1 : 0, edits.Count);
        if (expected)
        {
            var edit = Assert.Single(edits);
            Assert.Equal(start + 0x07, edit.Offset);
            Assert.Equal(1, edit.Width);
            Assert.Equal(3u, edit.After);
        }
        Assert.All(memory.Write(1, edits), result => Assert.Null(result.Error));
        Assert.Equal(expected ? 1 : 0, memory.WriteCount);
        Assert.Equal(expected ? (byte)3 : countdown, memory.ReadBytes(start + 0x07, 1)[0]);
        Assert.Empty(BattleRules.Maintain(units, false, false));
        // 游戏战斗标志回到 0（战斗场景结束）后不再写入
        EndBattle(memory);
        Assert.Empty(BattleRules.Maintain(BattleRules.Read(memory, 0), false, false, freezeCountdown: true));
    }

    [Fact]
    public void CountdownWriteRechecksDownStatus()
    {
        var memory = BattleFixture();
        var start = BattleUnit.Start + 16 * BattleUnit.Stride;
        memory.Seed(start + 0x61, [0x20]);
        memory.Seed(start + 0x07, [2]);
        var edit = Assert.Single(BattleRules.Maintain(BattleRules.Read(memory, 0), false, false, freezeCountdown: true));
        memory.Seed(start + 0x61, [0]);
        Assert.NotNull(Assert.Single(memory.Write(1, [edit])).Error);
        Assert.Equal(0, memory.WriteCount);
    }

    [Theory]
    [InlineData(16, 0x08, 44, 2, true)]
    [InlineData(16, 0x08, 44, 4, true)]
    [InlineData(16, 0x08, 44, 0xFE, true)]
    [InlineData(16, 0x08, 44, 0xFF, false)]
    [InlineData(16, 0x08, 44, 1, false)]
    [InlineData(16, 0x08, 44, 0, false)]
    [InlineData(16, 0, 44, 4, false)]
    [InlineData(16, 0x28, 44, 4, false)]
    [InlineData(16, 0x08, 0, 4, false)]
    [InlineData(0, 0x08, 52, 4, false)]
    [InlineData(1, 0x08, 52, 4, false)]
    [InlineData(2, 0x08, 37, 4, false)]
    [InlineData(22, 0x08, 18, 4, false)]
    public void InstantCastOnlyShortensLivingPlayerCasts(int slot, byte status, byte hp, byte remaining, bool expected)
    {
        var memory = BattleFixture();
        var start = BattleUnit.Start + (ulong)slot * BattleUnit.Stride;
        memory.Seed(start + 0x61, [status]);
        memory.Seed(start + 0x30, [hp, 0]);
        memory.Seed(start + 0x18D, [remaining]);
        var units = BattleRules.Read(memory, 0);
        var unit = units.Single(unit => unit.Slot == slot);
        var edits = BattleRules.Maintain(units, false, false, instantCast: true);
        Assert.Equal(expected ? 1 : 0, edits.Count);
        if (expected)
        {
            var edit = Assert.Single(edits);
            Assert.Equal(start + 0x18D, edit.Offset);
            Assert.Equal(1, edit.Width);
            Assert.Equal(1u, edit.After);
            BattleRules.Validate(memory, 0, edit);
        }
        else
            Assert.Throws<IOException>(() => BattleRules.Validate(memory, 0, unit.Edit(0x18D, 1, 1)));
        Assert.All(GameSession.ApplyEdits(memory, memory.WriteBytes, 0, edits), result => Assert.Null(result.Error));
        Assert.Equal(expected ? 1 : 0, memory.WriteCount);
        Assert.Equal(expected ? (byte)1 : remaining, memory.ReadBytes(start + 0x18D, 1)[0]);
        Assert.Equal(status, memory.ReadBytes(start + 0x61, 1)[0]);
        Assert.Empty(BattleRules.Maintain(BattleRules.Read(memory, 0), false, false, instantCast: true));
        Assert.Empty(BattleRules.Maintain(units, false, false));
        EndBattle(memory);
        Assert.Empty(BattleRules.Maintain(BattleRules.Read(memory, 0), false, false, instantCast: true));
    }

    [Fact]
    public void InstantCastValidationRejectsOtherValuesWidthsAndStaleCasts()
    {
        var memory = BattleFixture();
        var start = BattleUnit.Start + 16 * BattleUnit.Stride;
        memory.Seed(start + 0x61, [0x08]);
        memory.Seed(start + 0x18D, [4]);
        var unit = BattleRules.Read(memory, 0).Single(unit => unit.Slot == 16);
        for (uint after = 0; after <= 0xFF; after++)
        {
            if (after != 1)
                Assert.Throws<IOException>(() => BattleRules.Validate(memory, 0, unit.Edit(0x18D, 1, after)));
        }
        Assert.Throws<IOException>(() => BattleRules.Validate(memory, 0, unit.Edit(0x18D, 2, 1)));
        var edit = unit.Edit(0x18D, 1, 1);
        memory.Seed(start + 0x61, [0]);
        Assert.Throws<IOException>(() => BattleRules.Validate(memory, 0, edit));
        memory.Seed(start + 0x61, [0x08]);
        memory.Seed(start + 0x18D, [0xFF]);
        Assert.Throws<IOException>(() => BattleRules.Validate(memory, 0, edit));
        Assert.Equal(0, memory.WriteCount);
    }

    [Theory]
    [InlineData(16, 0, 44, 3, 4, 2)]
    [InlineData(16, 0, 44, 9, 7, 0)]
    [InlineData(16, 0, 44, 12, 10, 0)]
    [InlineData(16, 0, 44, 12, 4, 1)]
    [InlineData(16, 0, 44, 3, 10, 1)]
    [InlineData(16, 0x20, 44, 3, 4, 0)]
    [InlineData(16, 0, 0, 3, 4, 0)]
    [InlineData(0, 0, 52, 3, 4, 0)]
    [InlineData(1, 0, 52, 3, 4, 0)]
    [InlineData(2, 0, 37, 3, 4, 0)]
    [InlineData(22, 0, 18, 3, 4, 0)]
    public void MaxMoveJumpOnlyRaisesLivingPlayers(int slot, byte status, byte hp, byte move, byte jump, int count)
    {
        var memory = BattleFixture();
        foreach (var playerSlot in new[] { 16, 17, 18, 19 })
            memory.Seed(BattleUnit.Start + (ulong)playerSlot * BattleUnit.Stride + 0x42, [9, 7]);
        var start = BattleUnit.Start + (ulong)slot * BattleUnit.Stride;
        memory.Seed(start + 0x61, [status]);
        memory.Seed(start + 0x30, [hp, 0]);
        memory.Seed(start + 0x42, [move, jump]);
        var units = BattleRules.Read(memory, 0);
        var edits = BattleRules.Maintain(units, false, false, maxMoveJump: true);
        Assert.Equal(count, edits.Count);
        Assert.All(edits, edit =>
        {
            Assert.Equal(1, edit.Width);
            Assert.InRange(edit.Offset, start + 0x42, start + 0x43);
            Assert.True(edit.After > edit.Before);
            Assert.Equal(edit.Offset == start + 0x42 ? 9u : 7u, edit.After);
        });
        Assert.All(GameSession.ApplyEdits(memory, memory.WriteBytes, 0, edits), result => Assert.Null(result.Error));
        Assert.Equal(count, memory.WriteCount);
        Assert.Equal(count == 0 ? move : (byte)Math.Max((int)move, 9), memory.ReadBytes(start + 0x42, 1)[0]);
        Assert.Equal(count == 0 ? jump : (byte)Math.Max((int)jump, 7), memory.ReadBytes(start + 0x43, 1)[0]);
        Assert.Empty(BattleRules.Maintain(BattleRules.Read(memory, 0), false, false, maxMoveJump: true));
        Assert.Empty(BattleRules.Maintain(units, false, false));
        EndBattle(memory);
        Assert.Empty(BattleRules.Maintain(BattleRules.Read(memory, 0), false, false, maxMoveJump: true));
    }

    [Theory]
    [InlineData(16, 0, 44, 0x42, 1, 10)]
    [InlineData(16, 0, 44, 0x43, 1, 8)]
    [InlineData(16, 0, 44, 0x42, 2, 9)]
    [InlineData(16, 0, 44, 0x43, 2, 7)]
    [InlineData(16, 0x20, 44, 0x42, 1, 9)]
    [InlineData(16, 0x20, 44, 0x43, 1, 7)]
    [InlineData(16, 0, 0, 0x42, 1, 9)]
    [InlineData(16, 0, 0, 0x43, 1, 7)]
    [InlineData(0, 0, 52, 0x42, 1, 9)]
    [InlineData(0, 0, 52, 0x43, 1, 7)]
    [InlineData(2, 0, 37, 0x42, 1, 9)]
    [InlineData(2, 0, 37, 0x43, 1, 7)]
    public void MaxMoveJumpValidationRejectsUnsafeWrites(int slot, byte status, byte hp, int field, int width, uint after)
    {
        var memory = BattleFixture();
        var start = BattleUnit.Start + (ulong)slot * BattleUnit.Stride;
        memory.Seed(start + 0x61, [status]);
        memory.Seed(start + 0x30, [hp, 0]);
        var unit = BattleRules.Read(memory, 0).Single(unit => unit.Slot == slot);
        Assert.Throws<IOException>(() => BattleRules.Validate(memory, 0, unit.Edit(field, width, after)));
        Assert.Equal(0, memory.WriteCount);
    }

    [Theory]
    [InlineData(16, 0, 44, 4, true)]
    [InlineData(16, 0, 44, 99, true)]
    [InlineData(16, 0, 44, 100, false)]
    [InlineData(16, 0, 44, 104, false)]
    [InlineData(16, 0x20, 44, 4, false)]
    [InlineData(16, 0, 0, 4, false)]
    [InlineData(0, 0, 52, 4, false)]
    [InlineData(1, 0, 52, 4, false)]
    [InlineData(2, 0, 37, 4, false)]
    [InlineData(22, 0, 18, 4, false)]
    public void LockCtOnlyRaisesLivingPlayersBelowFull(int slot, byte status, byte hp, byte ct, bool expected)
    {
        var memory = BattleFixture();
        foreach (var playerSlot in new[] { 16, 17, 18, 19 })
            memory.Seed(BattleUnit.Start + (ulong)playerSlot * BattleUnit.Stride + 0x41, [104]);
        var start = BattleUnit.Start + (ulong)slot * BattleUnit.Stride;
        memory.Seed(start + 0x61, [status]);
        memory.Seed(start + 0x30, [hp, 0]);
        memory.Seed(start + 0x41, [ct]);
        var units = BattleRules.Read(memory, 0);
        var edits = BattleRules.Maintain(units, false, false, lockCt: true);
        Assert.Equal(expected ? 1 : 0, edits.Count);
        if (expected)
        {
            var edit = Assert.Single(edits);
            Assert.Equal(start + 0x41, edit.Offset);
            Assert.Equal(1, edit.Width);
            Assert.Equal(100u, edit.After);
            Assert.Null(Assert.Single(GameSession.ApplyEdits(memory, memory.WriteBytes, 0, edits)).Error);
            Assert.Equal((byte)100, memory.ReadBytes(start + 0x41, 1)[0]);
        }
        Assert.Empty(BattleRules.Maintain(units, false, false));
        EndBattle(memory);
        Assert.Empty(BattleRules.Maintain(BattleRules.Read(memory, 0), false, false, lockCt: true));
    }

    [Theory]
    [InlineData(0x53, 9, 1, 38, 198, "2")]
    [InlineData(0x4A, 0, 1, 103, 193, "7")]
    [InlineData(0x01, 0, 2, 222, 222, "178")]
    [InlineData(0x5D, 19, 8, 999, 3000, "MAX")]
    public void BattleJobProgressUsesConfirmedReadOnlyFields(byte job, int index, byte level,
        ushort current, ushort total, string next)
    {
        var memory = BattleFixture();
        var start = BattleUnit.Start + 16 * BattleUnit.Stride;
        memory.Seed(start + 3, [job]);
        var packed = (byte)(index % 2 == 0 ? (level << 4) | 7 : 0x70 | level);
        memory.Seed(start + 0xE4 + (ulong)(index / 2), [packed]);
        memory.Seed(start + 0xF0 + (ulong)(index * 2), BitConverter.GetBytes(current));
        memory.Seed(start + 0x11E + (ulong)(index * 2), BitConverter.GetBytes(total));
        var model = new TrainerViewModel(memory, text: TestText.Legacy());
        model.ApplySnapshot(memory.Snapshot());
        var unit = model.Allies[0];
        Assert.Equal(level.ToString(), unit.JobLevel);
        Assert.Equal(current.ToString(), unit.JobJp);
        Assert.Equal(next, unit.JobNext);
        Assert.All(model.Guests, guest =>
        {
            Assert.Equal("—", guest.JobLevel);
            Assert.Equal("—", guest.JobNext);
            Assert.Equal("—", guest.JobJp);
        });
        memory.Seed(start + 0xF0 + (ulong)(index * 2), BitConverter.GetBytes((ushort)(current + 1)));
        model.ApplySnapshot(memory.Snapshot());
        Assert.Equal((current + 1).ToString(), unit.JobJp);
        Assert.False(model.HasChanges);
        Assert.Equal(0, memory.WriteCount);
    }

    [Fact]
    public void BattlePanelCapsCtAndKeepsInvalidJobLevelUnknown()
    {
        var memory = BattleFixture();
        memory.Seed(BattleUnit.Start + 16 * BattleUnit.Stride + 0x41, [104]);
        memory.Seed(BattleUnit.Start + 16 * BattleUnit.Stride + 0x61, [0x20]);
        var model = new TrainerViewModel(memory, text: TestText.Legacy());
        model.ApplySnapshot(memory.Snapshot() with { LockCt = true });
        Assert.True(model.LockCt);
        var player = model.Allies[0];
        Assert.Equal("100", player.Ct.Text);
        Assert.Equal(104u, player.Ct.Value);
        player.Ct.Text = "100";
        Assert.False(player.Ct.IsDirty);
        Assert.Equal(104u, player.Ct.Value);
        Assert.Contains("待行动", player.CtLabel);
        Assert.False(player.CanChangeHp);
        // 倒下单位 HP 锁在当前值（不再强行压到 0）
        Assert.Equal(44u, player.Hp.Maximum);
        Assert.Equal(44u, player.Hp.Minimum);
        Assert.Equal("—", player.JobLevel);
        Assert.Equal("—", player.JobNext);
        Assert.Equal("0", player.JobJp);
        Assert.Equal("", player.FactionLabel);
        Assert.All(model.Guests, unit =>
        {
            Assert.Equal("GUEST", unit.FactionLabel);
            Assert.False(BattleRules.IsTarget(unit.Unit));
        });
        Assert.All(model.Enemies, unit =>
        {
            Assert.Equal("ENEMY", unit.FactionLabel);
            Assert.NotNull(unit.Portrait);
            Assert.Equal(unit.Unit.Data[0], unit.Portrait!.CharacterCode);
            Assert.Equal(unit.Unit.Data[3], unit.Portrait.JobId);
        });
        model.DisableBattleOptions.Execute(null);
        Assert.False(model.LockCt);
        model.ApplySnapshot(memory.Snapshot() with { LockCt = true });
        model.ApplySnapshot(null);
        Assert.False(model.LockCt);
        Assert.Contains("锁定我方 CT", model.Status);
    }

    [Fact]
    public void FillPlayerCtNeverLowersQueuedUnits()
    {
        // 实战里拉姆萨 CT 4、其余三人 104：按钮只能把 4 提到 100，不能把 104 降成 100
        var memory = BattleFixture();
        memory.Seed(BattleUnit.Start + 16 * BattleUnit.Stride + 0x41, [4]);
        foreach (var slot in new[] { 17, 18, 19 })
            memory.Seed(BattleUnit.Start + (ulong)slot * BattleUnit.Stride + 0x41, [104]);
        var model = new TrainerViewModel(memory, text: TestText.Legacy());
        model.ApplySnapshot(memory.Snapshot());
        model.FillPlayerCt.Execute(null);
        var field = Assert.Single(model.Pending);
        Assert.Equal(16, (int)((field.Offset - BattleUnit.Start) / BattleUnit.Stride));
        Assert.Equal(100u, field.Value);
    }

    [Fact]
    public void FillPlayerCtStagesOnlyLivingPlayers()
    {
        var memory = BattleFixture();
        memory.Seed(BattleUnit.Start + 17 * BattleUnit.Stride + 0x61, [0x20]);
        memory.Seed(BattleUnit.Start + 18 * BattleUnit.Stride + 0x30, [0, 0]);
        var model = new TrainerViewModel(memory, text: TestText.Legacy());
        model.ApplySnapshot(memory.Snapshot());
        model.FillPlayerCt.Execute(null);
        Assert.Equal(0, memory.WriteCount);
        Assert.Equal(new[] { 16, 19 }, model.Pending.Select(field =>
            (int)((field.Offset - BattleUnit.Start) / BattleUnit.Stride)));
        Assert.All(model.Pending, field =>
        {
            Assert.Equal(100u, field.Value);
            Assert.Equal(0x41ul, (field.Offset - BattleUnit.Start) % BattleUnit.Stride);
        });
        Assert.Contains("倒计时", model.Allies.Single(unit => unit.Unit.Slot == 17).State);
        Assert.All(memory.Write(1, model.Pending.Select(field => field.ToEdit()).ToArray()),
            result => Assert.Null(result.Error));
        Assert.Equal(2, memory.WriteCount);
        model.ApplySnapshot(null);
        Assert.False(model.FillPlayerCt.CanExecute(null));
    }

    [Fact]
    public void CtEditsClampStageUndoAndWrite()
    {
        var memory = BattleFixture();
        var model = new TrainerViewModel(memory, text: TestText.Legacy());
        model.ApplySnapshot(memory.Snapshot());
        var ct = model.Guests[0].Ct;
        ct.Value = 101;
        Assert.Equal(100u, ct.Value);
        Assert.Same(ct, Assert.Single(model.Pending));
        model.ApplySnapshot(memory.Snapshot());
        Assert.Equal(100u, ct.Value);
        model.Undo.Execute(null);
        Assert.Equal(22u, ct.Value);
        Assert.Empty(model.Pending);
        ct.Value = 0;
        Assert.Null(Assert.Single(memory.Write(1, [ct.ToEdit()])).Error);
        Assert.Equal((byte)0, memory.ReadBytes(BattleUnit.Start + 0x41, 1)[0]);
    }

    private static (TestMemory Memory, TrainerViewModel Model) Create()
    {
        var memory = new TestMemory();
        for (var slot = 0; slot < 50; slot++)
            memory.Seed(FftLayout.Roster + (ulong)(slot * 0x258) + FftLayout.SlotMarker, [0xFF]);
        var unit = new byte[FftLayout.UnitSize];
        unit[0] = 1;
        unit[FftLayout.Job] = 1;
        unit[FftLayout.NameId] = 1;
        unit[FftLayout.Level] = 9;
        unit[FftLayout.Experience] = 42;
        unit[FftLayout.Brave] = 70;
        unit[FftLayout.Faith] = 65;
        BinaryPrimitives.WriteUInt16LittleEndian(unit.AsSpan(FftLayout.CurrentJp), 179);
        BinaryPrimitives.WriteUInt16LittleEndian(unit.AsSpan(FftLayout.TotalJp), 700);
        BinaryPrimitives.WriteUInt16LittleEndian(unit.AsSpan(FftLayout.Equipment), 0xFF);
        memory.Seed(FftLayout.Roster, unit);
        foreach (var mirror in FftLayout.RosterMirrors)
            memory.Seed(mirror, unit);
        memory.Seed(FftLayout.Gil, BitConverter.GetBytes(12400u));
        var model = new TrainerViewModel(memory, text: TestText.Legacy());
        model.ApplySnapshot(memory.Snapshot());
        return (memory, model);
    }

    [Fact]
    public void RosterFieldsWriteAllCopiesAtSameSlotAndOffset()
    {
        var memory = new TestMemory();
        const ulong moduleBase = 0x140000000;
        var layout = new FftLayout();
        var slotOffset = (ulong)(49 * layout.RosterStride);
        ulong[] rosters = [FftLayout.Roster, .. FftLayout.RosterMirrors];
        foreach (var roster in rosters)
        {
            memory.Seed(moduleBase + roster + slotOffset + FftLayout.SlotMarker, [49]);
            memory.Seed(moduleBase + roster + slotOffset + FftLayout.NameId, [0x49, 0x01]);
        }
        var edits = new[] { FftLayout.Level, FftLayout.Experience, FftLayout.Brave, FftLayout.Faith }
            .Select(offset => new MemoryEdit(FftLayout.Roster + slotOffset + (ulong)offset, 1, 0, 75))
            .Concat(Enumerable.Range(0, FftLayout.JobCount).Select(job =>
                new MemoryEdit(FftLayout.Roster + slotOffset + (ulong)(FftLayout.CurrentJp + job * 2), 2, 0, 9999)))
            .Concat(Enumerable.Range(0, FftLayout.JobCount).Select(job =>
                new MemoryEdit(FftLayout.Roster + slotOffset + (ulong)(FftLayout.TotalJp + job * 2), 2, 0, 3000)))
            .Concat(Enumerable.Range(0, 11).Select(i =>
                new MemoryEdit(FftLayout.Roster + slotOffset + (ulong)(FftLayout.JobLevels + i), 1, 0, 0x88)))
            .ToArray();
        // 镜像的 JP 可以比 R1 旧，不能用 R1 的 Before 拒绝同步。
        foreach (var mirror in FftLayout.RosterMirrors)
            memory.Seed(moduleBase + mirror + slotOffset + FftLayout.CurrentJp, [179, 0]);
        var results = GameSession.ApplyEdits(memory, memory.WriteBytes, moduleBase, edits, layout);
        Assert.All(results, result =>
        {
            Assert.Null(result.Error);
            Assert.Null(result.Warning);
        });
        Assert.Equal(edits.Length * 3, memory.WriteCount);
        foreach (var edit in edits)
            foreach (var roster in rosters)
                Assert.Equal(memory.ReadBytes(moduleBase + edit.Offset, edit.Width),
                    memory.ReadBytes(moduleBase + roster + edit.Offset - FftLayout.Roster, edit.Width));
    }

    [Theory]
    [InlineData(0, FftLayout.SlotMarker)]
    [InlineData(1, FftLayout.SlotMarker)]
    [InlineData(0, FftLayout.NameId + 1)]
    [InlineData(1, FftLayout.NameId + 1)]
    public void MirrorIdentityMismatchWritesOnlyPrimaryAndWarns(int mirrorIndex, int identityOffset)
    {
        var (memory, model) = Create();
        memory.Seed(FftLayout.RosterMirrors[mirrorIndex] + (ulong)identityOffset, [99]);
        var field = model.SelectedUnit!.Jobs[0].Jp;
        field.Value = 999;
        var result = Assert.Single(memory.Write(1, [field.ToEdit()]));
        Assert.Null(result.Error);
        Assert.Contains("仅写入 R1", result.Warning);
        Assert.Equal(1, memory.WriteCount);
        Assert.Equal(999, BinaryPrimitives.ReadUInt16LittleEndian(memory.ReadBytes(field.Offset, 2)));
        foreach (var mirror in FftLayout.RosterMirrors)
            Assert.Equal(179, BinaryPrimitives.ReadUInt16LittleEndian(memory.ReadBytes(mirror + FftLayout.CurrentJp, 2)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReadbackFailureInAnyCopyIsReported(int copy)
    {
        var (memory, model) = Create();
        ulong[] rosters = [FftLayout.Roster, .. FftLayout.RosterMirrors];
        memory.IgnoreWrite = rosters[copy] + FftLayout.CurrentJp;
        model.SelectedUnit!.Jobs[0].Jp.Value = 999;
        await model.WriteAsync();
        var pending = Assert.Single(model.Pending);
        Assert.Contains("写入后回读不一致", pending.Error);
        Assert.Contains($"0x{memory.IgnoreWrite:X}", pending.Error);
        Assert.Equal(179u, pending.Original);
        Assert.Equal(3, memory.WriteCount);
    }

    [Fact]
    public async Task SuccessNoticeSurvivesPollingUntilNextEdit()
    {
        var (memory, model) = Create();
        model.SelectedUnit!.Jobs[0].Jp.Value = 999;
        await model.WriteAsync();
        const string notice = "已写入。游戏里正打开的队员界面需退出再进入才会刷新。";
        Assert.Equal(notice, model.Status);
        model.ApplySnapshot(memory.Snapshot());
        Assert.Equal(notice, model.Status);
        model.Gil.Value = 500;
        Assert.DoesNotContain(notice, model.Status);
    }

    [Fact]
    public async Task MirrorWarningIsShownAfterPrimaryWrite()
    {
        var (memory, model) = Create();
        memory.Seed(FftLayout.RosterMirrors[0] + FftLayout.NameId, [99]);
        model.SelectedUnit!.Level.Value = 50;
        await model.WriteAsync();
        Assert.Empty(model.Pending);
        Assert.Contains("仅写入 R1", model.Status);
        Assert.Contains("仅写入 R1", model.SelectedUnit.Level.Error);
    }

    [Fact]
    public void EditingStagesWithoutWritingAndReturningToOriginalRemovesChip()
    {
        var (memory, model) = Create();
        model.Gil.Text = "99,999";
        model.Gil.Value = 100000;
        Assert.Single(model.Pending);
        Assert.Equal(12400u, model.Gil.Original);
        Assert.Equal("100,000", model.Gil.Text);
        Assert.Equal(0, memory.WriteCount);
        model.Gil.Value = 12400;
        Assert.Empty(model.Pending);
    }

    [Fact]
    public void PollingPreservesFocusedAndDirtyFieldsButUpdatesOthers()
    {
        var (memory, model) = Create();
        model.Gil.IsEditing = true;
        model.SelectedUnit!.Brave.Value = 80;
        memory.Seed(FftLayout.Gil, BitConverter.GetBytes(13000u));
        memory.Seed(FftLayout.Roster + FftLayout.Brave, [75]);
        memory.Seed(FftLayout.Roster + FftLayout.Level, [10]);
        model.ApplySnapshot(memory.Snapshot());
        Assert.Equal(12400u, model.Gil.Value);
        Assert.Equal(80u, model.SelectedUnit.Brave.Value);
        Assert.Equal(70u, model.SelectedUnit.Brave.Original);
        Assert.Equal(10u, model.SelectedUnit.Level.Value);
        model.Gil.IsEditing = false;
        model.ApplySnapshot(memory.Snapshot());
        Assert.Equal(13000u, model.Gil.Value);
        model.Undo.Execute(null);
        Assert.Equal(75u, model.SelectedUnit.Brave.Value);
        Assert.Empty(model.Pending);
    }

    [Fact]
    public async Task BatchWritesCorrectWidthsAndAcceptsOnlyVerifiedFields()
    {
        var (memory, model) = Create();
        model.Gil.Value = 99999999;
        model.SelectedUnit!.Level.Value = 99;
        model.SelectedUnit.Jobs[0].Jp.Value = 9999;
        memory.IgnoreWrite = model.SelectedUnit.Level.Offset;
        await model.WriteAsync();
        Assert.Equal(7, memory.WriteCount);
        Assert.Equal(99999999u, BinaryPrimitives.ReadUInt32LittleEndian(memory.ReadBytes(FftLayout.Gil, 4)));
        Assert.Equal(9999, BinaryPrimitives.ReadUInt16LittleEndian(memory.ReadBytes(model.SelectedUnit.Jobs[0].Jp.Offset, 2)));
        Assert.Single(model.Pending);
        Assert.True(model.SelectedUnit.Level.HasError);
        Assert.Equal(9u, model.SelectedUnit.Level.Original);
        Assert.Equal(42, memory.ReadBytes(FftLayout.Roster + FftLayout.Experience, 1)[0]);
        memory.IgnoreWrite = null;
        await model.WriteAsync();
        Assert.Empty(model.Pending);
        Assert.Equal(99u, model.SelectedUnit.Level.Original);
    }

    [Fact]
    public async Task ConflictAndWriteFailureAreRetainedAndUndoClearsErrors()
    {
        var (memory, model) = Create();
        model.Gil.Value = 400;
        model.SelectedUnit!.Level.Value = 50;
        memory.Seed(FftLayout.Gil, BitConverter.GetBytes(123u));
        memory.FailWrite = model.SelectedUnit.Level.Offset;
        await model.WriteAsync();
        Assert.Equal(1, memory.WriteCount);
        Assert.Equal(2, model.Pending.Count);
        Assert.All(model.Pending, item => Assert.True(item.HasError));
        model.ApplySnapshot(memory.Snapshot());
        model.Undo.Execute(null);
        Assert.Empty(model.Pending);
        Assert.Equal(123u, model.Gil.Value);
        Assert.False(model.Gil.HasError);
    }

    [Theory]
    [InlineData(9, 94, true)]
    [InlineData(10, 94, false)]
    [InlineData(70, 95, true)]
    public async Task DangerousValuesRequireExplicitConfirmation(int brave, int faith, bool dangerous)
    {
        var (memory, model) = Create();
        model.SelectedUnit!.Brave.Value = (uint)brave;
        model.SelectedUnit.Faith.Value = (uint)faith;
        Assert.Equal(dangerous, model.NeedsConfirmation);
        var changedCount = model.Pending.Count;
        await model.WriteAsync();
        Assert.Equal(dangerous ? 0 : changedCount * 3, memory.WriteCount);
        if (dangerous)
        {
            await model.WriteAsync(true);
            Assert.Equal(changedCount * 3, memory.WriteCount);
        }
        Assert.Empty(model.Pending);
    }

    [Fact]
    public async Task DisconnectDisablesWriteAndNewProcessDiscardsOldEdits()
    {
        var (memory, model) = Create();
        model.Gil.Value = 500;
        model.SelectedUnit!.Level.Value = 20;
        model.ApplySnapshot(null);
        await model.WriteAsync();
        Assert.Equal(0, memory.WriteCount);
        Assert.False(model.CanEdit);
        memory.Generation++;
        model.ApplySnapshot(memory.Snapshot());
        Assert.Empty(model.Pending);
        Assert.Equal(12400u, model.Gil.Value);
        Assert.Equal(9u, model.SelectedUnit!.Level.Value);
        Assert.True(model.CanEdit);
    }

    [Fact]
    public async Task SessionGenerationChangeBetweenPollAndWriteRejectsBatch()
    {
        var (memory, model) = Create();
        model.Gil.Value = 500;
        memory.Generation++;
        await model.WriteAsync();
        Assert.Equal(0, memory.WriteCount);
        Assert.True(model.Gil.HasError);
        Assert.Single(model.Pending);
    }

    [Fact]
    public void ConfirmedRosterParsesSevenMembersAndSkipsOnlyFfMarkers()
    {
        var memory = new TestMemory();
        var layout = new FftLayout();
        Assert.Equal(0x258, layout.RosterStride);
        Assert.Equal(50, layout.RosterCount);
        int[] names = [1, 329, 468, 459, 625, 695, 601];
        string[] expected = ["拉姆萨", "齐克", "麦克尼尔", "霍特曼", "香奈尔", "纽特", "桑德拉"];
        for (var slot = 0; slot < 50; slot++)
        {
            var data = new byte[0x258];
            data[1] = slot < 7 ? (byte)slot : (byte)0xFF;
            data[0] = slot == 0 ? (byte)1 : slot < 4 ? (byte)0x80 : (byte)0x81;
            data[2] = slot == 0 ? (byte)1 : (byte)(0x4A + slot);
            if (slot < 7)
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x230), (ushort)names[slot]);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x0E), 0x9D);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x10), 0xFF);
            memory.Seed(FftLayout.Roster + (ulong)(slot * 0x258), data);
        }
        memory.Seed(FftLayout.Inventory + 0xF0, [12]);
        memory.Seed(FftLayout.Inventory + 0x9D, [2]);
        var snapshot = memory.Snapshot();
        Assert.Equal(Enumerable.Range(0, 7), snapshot.Units.Select(unit => unit.Slot));
        Assert.Equal(0x105, snapshot.Inventory.Length);
        var model = new TrainerViewModel(memory, text: TestText.Legacy());
        model.ApplySnapshot(snapshot);
        Assert.Equal("7 / 50", model.RosterCountText);
        Assert.Equal(expected, model.Units.Select(unit => unit.Name));
        Assert.Equal(new[] { "男", "男", "男", "男", "女", "女", "女" }, model.Units.Select(unit => unit.Gender));
        Assert.True(model.Units[0].IsProtagonist);
        Assert.Contains("主角", model.Units[0].Number);
        Assert.All(model.Units.Skip(1), unit => Assert.False(unit.IsProtagonist));
        for (var i = 0; i < 7; i++)
        {
            Assert.Equal(TestText.Jobs[i], model.Units[i].Job);
            Assert.True(model.Units[i].Jobs[i].IsCurrent);
            Assert.Single(model.Units[i].Jobs, job => job.IsCurrent);
        }
        Assert.Equal("皮帽", model.Units[0].Equipment[0].ItemName);
        Assert.Equal("0x9D", model.Units[0].Equipment[0].Id);
        Assert.Equal("空", model.Units[0].Equipment[1].ItemName);
        Assert.Equal(2, model.VisibleItems.Count);
        Assert.Equal(0, memory.WriteCount);

        memory.Seed(FftLayout.Roster + 7 * 0x258, new byte[0x258]);
        snapshot = memory.Snapshot();
        Assert.Equal(8, snapshot.Units.Count);
        model.ApplySnapshot(snapshot);
        Assert.Equal("队员 #0", model.Units[7].Name);
        Assert.Equal("职业 0x00", model.Units[7].Job);
        Assert.Equal("男", model.Units[7].Gender);
        Assert.DoesNotContain(model.Units[7].Jobs, job => job.IsCurrent);
    }

    [Theory]
    [InlineData(0x01, 0)]
    [InlineData(0x4A, 0)]
    [InlineData(0x5D, 19)]
    [InlineData(0x49, -1)]
    [InlineData(0x5E, -1)]
    public void JobIdsMapToJpCards(int id, int index)
    {
        Assert.Equal(index, GameData.JobIndex(id));
        // GameData 只剩编号兜底；名称来自游戏文本
        Assert.Equal($"职业 0x{id:X2}", GameData.JobName(id));
        Assert.Equal(index >= 0 ? TestText.Jobs[index] : $"职业 0x{id:X2}", TestText.Legacy().JobName(id));
    }

    [Fact]
    public async Task InventoryFiltersStagesClampsAndWritesOnlyVisibleItems()
    {
        var (memory, model) = Create();
        memory.Seed(FftLayout.Inventory + 0xF0, [12]);
        memory.Seed(FftLayout.Inventory + 0x9D, [2]);
        model.ApplySnapshot(memory.Snapshot());
        Assert.Equal(new[] { 0x9D, 0xF0 }, model.VisibleItems.Select(item => item.Id));
        model.ShowAllItems = true;
        Assert.Equal(0x105 + 96, model.VisibleItems.Count);
        model.ShowConsumables.Execute(null);
        Assert.Equal(14, model.VisibleItems.Count);
        model.ItemSearch = "药水";
        Assert.Equal(new[] { 0xF0, 0xF1, 0xF2 }, model.VisibleItems.Select(item => item.Id));
        model.MaxVisibleItems.Execute(null);
        Assert.Equal(3, model.Pending.Count);
        Assert.Equal(0, memory.WriteCount);
        model.Items[0xF1].Quantity.Value = 100;
        Assert.Equal(99u, model.Items[0xF1].Quantity.Value);
        model.ApplySnapshot(memory.Snapshot());
        Assert.Equal(99u, model.Items[0xF0].Quantity.Value);
        await model.WriteAsync();
        Assert.Equal(3, memory.WriteCount);
        Assert.Equal(new byte[] { 99, 99, 99, 0 }, memory.ReadBytes(FftLayout.Inventory + 0xF0, 4));
        Assert.Empty(model.Pending);
        model.ShowEquipment.Execute(null);
        model.ItemSearch = "0x9d";
        Assert.Equal("皮帽", Assert.Single(model.VisibleItems).Name);
        model.Items[0x9D].Quantity.Value = 0;
        model.ShowAllItems = false;
        Assert.Empty(model.VisibleItems);
        model.Undo.Execute(null);
        Assert.Equal(2u, Assert.Single(model.VisibleItems).Quantity.Value);
        Assert.Equal("物品 #0x00", model.Items[0].Name);
        Assert.Equal("装备", model.Items[0xFF].Category);
        model.Items[0x9D].Quantity.Value = 99;
        memory.Generation++;
        model.ApplySnapshot(memory.Snapshot());
        Assert.Empty(model.Pending);
        Assert.Equal(2u, model.Items[0x9D].Quantity.Value);
    }

    [Fact]
    public void GrantAllJpStagesProgressAndClampsCharacterFields()
    {
        var (_, model) = Create();
        var unit = model.SelectedUnit!;
        Assert.Equal(20, unit.Jobs.Count);
        Assert.Equal(4, unit.Jobs[0].Diamonds.Count(value => value));
        unit.AddAll500.Execute(null);
        Assert.Equal(19, model.Pending.Count);
        Assert.Equal(679u, unit.Jobs[0].Jp.Value);
        Assert.Equal(1200u, unit.Jobs[0].Total.Value);
        Assert.Equal(5, unit.Jobs[0].Diamonds.Count(value => value));
        unit.Level.Value = 0;
        Assert.Equal(1u, unit.Level.Value);
        unit.Experience.Value = 100;
        Assert.Equal(99u, unit.Experience.Value);
        unit.Brave.Value = 101;
        Assert.Equal(100u, unit.Brave.Value);
    }

    [Theory]
    [InlineData(0x01, 18, false)]
    [InlineData(0x80, 18, false)]
    [InlineData(0x81, 17, false)]
    [InlineData(0x00, 18, false)]
    [InlineData(0x01, 18, true)]
    [InlineData(0x80, 18, true)]
    [InlineData(0x81, 17, true)]
    public async Task BulkJobsSkipHiddenAndGenderUnavailableSlots(byte character, int unavailable, bool level8)
    {
        var (memory, model) = Create();
        memory.Seed(FftLayout.Roster + FftLayout.Character, [character]);
        memory.Seed(FftLayout.Roster + FftLayout.JobLevels + 8, [0x12, 0x34, 0x56]);
        foreach (var index in new[] { unavailable, 20, 21 })
        {
            memory.Seed(FftLayout.Roster + (ulong)(FftLayout.CurrentJp + index * 2), [123, 0]);
            memory.Seed(FftLayout.Roster + (ulong)(FftLayout.TotalJp + index * 2), [234, 0]);
        }
        model.ApplySnapshot(memory.Snapshot());
        var unit = model.SelectedUnit!;
        Assert.Equal(TestText.Jobs, unit.Jobs.Select(job => job.Name));
        Assert.DoesNotContain(unit.Fields, field => field.Offset == FftLayout.Roster + FftLayout.JobLevels + 10);
        var blocked = unit.Jobs[unavailable];
        Assert.False(blocked.IsAvailable);
        foreach (var command in new[] { blocked.Add100, blocked.Add500, blocked.LevelUp, blocked.Level8 })
        {
            Assert.False(command.CanExecute(null));
            command.Execute(null);
        }
        blocked.Grant(500);
        Assert.False(blocked.IsDirty);
        (level8 ? unit.AllLevel8 : unit.AddAll500).Execute(null);
        Assert.Equal(19, model.Pending.Count);
        Assert.False(blocked.IsDirty);
        Assert.All(unit.Jobs.Where(job => job.IsAvailable), job => Assert.True(job.IsDirty));
        await model.WriteAsync();
        Assert.Empty(model.Pending);
        foreach (var index in new[] { unavailable, 20, 21 })
        {
            Assert.Equal(new byte[] { 123, 0 }, memory.ReadBytes(FftLayout.Roster + (ulong)(FftLayout.CurrentJp + index * 2), 2));
            Assert.Equal(new byte[] { 234, 0 }, memory.ReadBytes(FftLayout.Roster + (ulong)(FftLayout.TotalJp + index * 2), 2));
        }
        var packed = memory.ReadBytes(FftLayout.Roster + (ulong)(FftLayout.JobLevels + unavailable / 2), 1)[0];
        Assert.Equal(unavailable == 17 ? 2 : 3, (packed >> ((1 - unavailable % 2) * 4)) & 15);
        Assert.Equal(0x56, memory.ReadBytes(FftLayout.Roster + FftLayout.JobLevels + 10, 1)[0]);
    }

    [Theory]
    [InlineData(699, 174, 0x11, 0, 100, 799, 274, 0x21)]
    [InlineData(699, 174, 0x31, 1, 100, 799, 274, 0x32)]
    [InlineData(699, 174, 0x16, 1, 100, 799, 274, 0x16)]
    [InlineData(9900, 9950, 0x11, 20, 500, 9999, 9999, 0x81)]
    [InlineData(9999, 174, 0x11, 0, 100, 9999, 274, 0x21)]
    [InlineData(10, 9999, 0x11, 21, 100, 110, 9999, 0x18)]
    [InlineData(699, 174, 0x17, 0, 100, 799, 274, 0x27)]
    [InlineData(0, 0, 0x11, 0, int.MaxValue, 9999, 9999, 0x81)]
    public void GrantPreservesOtherNibbleAndNeverLowersLevel(int cur, int total, int packed,
        int job, int amount, int expectedCur, int expectedTotal, int expectedPacked)
    {
        var result = JobProgress.Grant((uint)cur, (uint)total, (byte)packed, job, amount);
        Assert.Equal((uint)expectedCur, result.Current);
        Assert.Equal((uint)expectedTotal, result.Total);
        Assert.Equal((byte)expectedPacked, result.LevelByte);
    }

    [Theory]
    [InlineData(0, 50, 0, 0, false)]     // 迪利塔 GUEST
    [InlineData(0, 50, 0x35, 5, true)]   // 护卫关的阿加斯（友军 TARGET）
    [InlineData(0, 50, 0, 5, true)]      // +0x60 无关
    [InlineData(0, 50, 0x35, 0, false)]
    [InlineData(1, 0xFF, 0, 8, true)]    // 击杀关的敌方骑士（敌方 TARGET）
    [InlineData(1, 0xFF, 0, 0, false)]   // 普通敌人
    [InlineData(0, 0, 0, 0, false)]      // 我方
    [InlineData(2, 50, 0, 5, false)]     // 未知队伍不标
    public void TargetMarkerIsNonZero1F8OnKnownSides(int faction, int rosterIndex,
        int field60, int field1F8, bool expected)
    {
        var data = new byte[BattleUnit.Stride];
        data[2] = (byte)rosterIndex;
        data[5] = (byte)(faction << 4);
        data[0x32] = 40;
        data[0x60] = (byte)field60;
        data[0x1F8] = (byte)field1F8;
        Assert.Equal(expected, BattleRules.IsTarget(new BattleUnit(0, data)));
        data[1] = 0xFF;
        Assert.False(BattleRules.IsTarget(new BattleUnit(0, data)));
    }

    [Fact]
    public void ProtectTargetLabelRefreshesWithoutChangingCharacterIdentity()
    {
        var memory = BattleFixture();
        var units = BattleRules.Read(memory, 0);
        var delita = new BattleUnitViewModel(units.Single(unit => unit.Slot == 0), [], TestText.Legacy());
        var argath = new BattleUnitViewModel(units.Single(unit => unit.Slot == 1), [], TestText.Legacy());
        Assert.Equal("GUEST", delita.FactionLabel);
        Assert.Equal("GUEST", argath.FactionLabel);
        var notified = false;
        argath.PropertyChanged += (_, args) => notified |= args.PropertyName == nameof(BattleUnitViewModel.FactionLabel);
        var data = argath.Unit.Data.ToArray();
        data[0x60] = 0x35;
        data[0x1F8] = 5;
        argath.Refresh(new BattleUnit(1, data));
        Assert.True(notified);
        Assert.Equal("TARGET", argath.FactionLabel);
        Assert.Equal("GUEST", delita.FactionLabel);
        data = data.ToArray();
        data[0x1F8] = 0;   // 下一场剧情战阿加斯变回 GUEST 时 +0x1F8 = 0（+0x60 不影响）
        argath.Refresh(new BattleUnit(1, data));
        Assert.Equal("GUEST", argath.FactionLabel);
    }

    private const string PortraitJson = """
        {
          "rosterMappings": [
            { "characterCode": "0x01", "jobId": "0x01", "gender": "male",
              "face": "face/ramza.png", "jobIllustration": null },
            { "characterCode": "0x80", "jobId": "0x4A", "gender": "male",
              "face": "face/squire.png", "jobIllustration": "job/squire.png" },
            { "characterCode": "0x81", "jobId": "0x4B", "gender": "female",
              "face": "face/chemist.png", "jobIllustration": "job/chemist.png" },
            { "characterCode": "0x82", "jobId": "0xA9", "gender": "monster",
              "face": "monster/wldface_137_08_uitx.png", "jobIllustration": null }
          ]
        }
        """;

    [Theory]
    [InlineData(0x01, 0x01, "male", "ramza", false)]
    [InlineData(0x80, 0x4A, "male", "squire", true)]
    [InlineData(0x81, 0x4B, "female", "chemist", true)]
    public void PortraitsUseExactJsonMapping(int character, int job, string gender, string name, bool illustration)
    {
        var catalog = PortraitCatalog.Load("app", "local", _ => PortraitJson);
        var paths = catalog.Resolve(new PortraitIdentity((byte)character, (byte)job, gender), _ => true);
        Assert.Equal(Path.Combine("app", "assets", "portraits", "face", name + ".png"), paths.Face);
        Assert.Equal(illustration ? Path.Combine("app", "assets", "portraits", "job", name + ".png") : null,
            paths.JobIllustration);
    }

    [Theory]
    [InlineData(0x80, 0x4A, "female")]
    [InlineData(0x82, 0xA9, "male")]
    [InlineData(0x82, 0xFF, "monster")]
    [InlineData(0x80, 0x5C, "male")]
    public void UnknownPortraitCombinationsRemainEmpty(int character, int job, string gender)
    {
        var catalog = PortraitCatalog.Load("app", "local", _ => PortraitJson);
        Assert.Equal(new PortraitPaths(null, null),
            catalog.Resolve(new PortraitIdentity((byte)character, (byte)job, gender), _ => true));
    }

    [Theory]
    [InlineData(0x01, 0x4A, "001")]
    [InlineData(0x04, 0x02, "004")]
    [InlineData(0x07, 0x03, "007")]
    [InlineData(0x7F, 0x4A, "127")]
    public void SpecialPortraitsUseDefaultVariantOnlyWhenNoExactMapping(int character, int job, string number)
    {
        var catalog = PortraitCatalog.Load("app", "local", _ => PortraitJson);
        var identity = new PortraitIdentity((byte)character, (byte)job, "male");
        var filename = $"wldface_{number}_08_uitx.png";
        Assert.Equal(Path.Combine("app", "assets", "portraits", "face", filename),
            catalog.Resolve(identity, _ => true).Face);
        Assert.Equal(Path.Combine("local", "FftTrainer", "portraits", "face", filename),
            catalog.Resolve(identity, path => path.StartsWith("local")).Face);
        Assert.Equal(new PortraitPaths(null, null), catalog.Resolve(identity, _ => false));
        Assert.Null(catalog.Resolve(new PortraitIdentity(1, 1, "male"),
            path => path.EndsWith("wldface_001_08_uitx.png")).Face);
    }

    [Fact]
    public void BattleGuestsAndMonstersUseTheSharedPortraitCatalog()
    {
        var catalog = PortraitCatalog.Load("app", "local", _ => PortraitJson);
        var units = BattleRules.Read(BattleFixture(), 0);
        foreach (var unit in units.Where(unit => unit.Side == BattleSide.Guest || unit.Slot == 7))
        {
            var model = new BattleUnitViewModel(unit, [], TestText.Legacy());
            var monster = unit.Slot == 7;
            Assert.Equal(monster ? "monster" : "male", model.Portrait!.Gender);
            var filename = monster ? "wldface_137_08_uitx.png" : $"wldface_{unit.Data[0]:000}_08_uitx.png";
            Assert.Equal(Path.Combine("app", "assets", "portraits", monster ? "monster" : "face", filename),
                catalog.Resolve(model.Portrait, _ => true).Face);
            Assert.Equal(new PortraitPaths(null, null), catalog.Resolve(model.Portrait, _ => false));
        }
        var unsafeJson = PortraitJson.Replace("monster/wldface_137_08_uitx.png", "monster/../../outside.png");
        var unsafeCatalog = PortraitCatalog.Load("app", "local", _ => unsafeJson);
        Assert.Null(unsafeCatalog.Resolve(new PortraitIdentity(0x82, 0xA9, "monster"), _ => true).Face);
    }

    [Fact]
    public void PortraitFilesFallBackIndividuallyAndMissingFilesRemainEmpty()
    {
        var catalog = PortraitCatalog.Load("app", "local", _ => PortraitJson);
        var identity = new PortraitIdentity(0x80, 0x4A, "male");
        var paths = catalog.Resolve(identity, path => path.StartsWith("local"));
        Assert.Equal(Path.Combine("local", "FftTrainer", "portraits", "face", "squire.png"), paths.Face);
        Assert.Equal(Path.Combine("local", "FftTrainer", "portraits", "job", "squire.png"), paths.JobIllustration);
        Assert.Equal(new PortraitPaths(null, null), catalog.Resolve(identity, _ => false));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"rosterMappings\": [null, {}]}")]
    public void InvalidPrimaryCatalogFallsBack(string json)
    {
        var catalog = PortraitCatalog.Load("app", "local", path => path.StartsWith("app") ? json : PortraitJson);
        var paths = catalog.Resolve(new PortraitIdentity(0x01, 0x01, "male"), _ => true);
        Assert.Equal(Path.Combine("local", "FftTrainer", "portraits", "face", "ramza.png"), paths.Face);
        Assert.Null(paths.JobIllustration);
    }

    [Fact]
    public void MissingCatalogAndUnsafeAssetPathsRemainEmpty()
    {
        var identity = new PortraitIdentity(0x80, 0x4A, "male");
        var missing = PortraitCatalog.Load("app", "local", _ => throw new FileNotFoundException());
        Assert.Equal(new PortraitPaths(null, null), missing.Resolve(identity, _ => true));
        var unsafeJson = PortraitJson.Replace("face/squire.png", "face/../../outside.png")
            .Replace("job/squire.png", "atlas/squire.png");
        var catalog = PortraitCatalog.Load("app", "local", _ => unsafeJson);
        Assert.Equal(new PortraitPaths(null, null), catalog.Resolve(identity, _ => true));
    }

    [Fact]
    public void PortraitIdentityTracksRosterJobAndGenderChanges()
    {
        var data = new byte[FftLayout.UnitSize];
        data[FftLayout.Character] = 0x80;
        data[FftLayout.Job] = 0x4A;
        var snapshot = new UnitSnapshot(0, data);
        var unit = new UnitViewModel(snapshot, new FftLayout(), TestText.Legacy());
        Assert.Equal(new PortraitIdentity(0x80, 0x4A, "male"), unit.Portrait);
        var changed = false;
        unit.PropertyChanged += (_, args) => changed |= args.PropertyName == nameof(UnitViewModel.Portrait);
        data[FftLayout.Character] = 0x81;
        data[FftLayout.Job] = 0x4B;
        unit.Refresh(snapshot, new FftLayout());
        Assert.True(changed);
        Assert.Equal(new PortraitIdentity(0x81, 0x4B, "female"), unit.Portrait);
    }

    [Fact]
    public void Packed12DecodesSquireLevel1AndChemistLevel2()
    {
        var data = new byte[FftLayout.UnitSize];
        data[FftLayout.JobLevels] = 0x12;
        var unit = new UnitViewModel(new UnitSnapshot(0, data), new FftLayout(), TestText.Legacy());
        Assert.Equal("见习战士", unit.Jobs[0].Name);
        Assert.Equal(1, unit.Jobs[0].Level);
        Assert.Equal("道具士", unit.Jobs[1].Name);
        Assert.Equal(2, unit.Jobs[1].Level);
        unit.Jobs[0].LevelByte.Value = 0x13;
        Assert.False(unit.Jobs[0].IsDirty);
        Assert.True(unit.Jobs[1].IsDirty);
    }

    [Theory]
    [InlineData(200, 2)]
    [InlineData(400, 3)]
    [InlineData(700, 4)]
    [InlineData(1100, 5)]
    [InlineData(1600, 6)]
    [InlineData(2200, 7)]
    [InlineData(3000, 8)]
    public void GrantCrossesEachThresholdExactly(int threshold, int level)
    {
        var before = JobProgress.Grant(0, (uint)(threshold - 2), 0x11, 0, 1);
        var at = JobProgress.Grant(0, (uint)(threshold - 1), 0x11, 0, 1);
        Assert.Equal(level - 1, before.LevelByte >> 4);
        Assert.Equal(level, at.LevelByte >> 4);
        Assert.Equal(1, JobProgress.ToNextLevel((int)before.Total));
    }

    [Fact]
    public async Task GrantSynchronizesEvenUnchangedLevelByte()
    {
        var (memory, model) = Create();
        memory.Seed(FftLayout.Roster + FftLayout.JobLevels, [0x41]);
        model.ApplySnapshot(memory.Snapshot());
        model.SelectedUnit!.Jobs[0].Add100.Execute(null);
        await model.WriteAsync();
        Assert.Equal(9, memory.WriteCount);
        foreach (var mirror in FftLayout.RosterMirrors)
            Assert.Equal(0x41, memory.ReadBytes(mirror + FftLayout.JobLevels, 1)[0]);
    }

    [Fact]
    public void AllLevel8StagesEveryJobOnceAndPreservesPollingBaseline()
    {
        var (memory, model) = Create();
        model.SelectedUnit!.AllLevel8.Execute(null);
        Assert.Equal(19, model.Pending.Count);
        Assert.All(model.SelectedUnit.Jobs.Where(job => job.IsAvailable), job =>
        {
            Assert.Equal(3000u, job.Total.Value);
            Assert.Equal(8, job.Level);
        });
        model.SelectedUnit.AllLevel8.Execute(null);
        Assert.Equal(19, model.Pending.Count);
        model.ApplySnapshot(memory.Snapshot());
        Assert.All(model.SelectedUnit.Jobs.Where(job => job.IsAvailable), job => Assert.Equal(3000u, job.Total.Value));
    }

    [Fact]
    public async Task FailedTotalRemainsOneReadableChipAndRetryDoesNotGrantTwice()
    {
        var (memory, model) = Create();
        var job = model.SelectedUnit!.Jobs[0];
        job.Add500.Execute(null);
        memory.FailWrite = job.Total.Offset;
        await model.WriteAsync();
        var pending = Assert.Single(model.Pending);
        Assert.Equal("拉姆萨 见习战士 +500 JP", pending.ChangeText);
        Assert.True(pending.HasError);
        Assert.True(model.HasJpChanges);
        memory.FailWrite = null;
        await model.WriteAsync();
        Assert.Empty(model.Pending);
        Assert.Equal(679u, job.Jp.Value);
        Assert.Equal(1200u, job.Total.Value);
    }

    [Fact]
    public void GrantRejectsInvalidArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => JobProgress.Grant(0, 0, 0, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => JobProgress.Grant(0, 0, 0, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => JobProgress.Grant(0, 0, 0, 22, 1));
    }

    [Fact]
    public async Task GrantsMergeChipsAndSharedLevelByteThenWriteThreeCopies()
    {
        var (memory, model) = Create();
        var unit = model.SelectedUnit!;
        unit.Jobs[0].Add100.Execute(null);
        unit.Jobs[0].Add100.Execute(null);
        Assert.Equal("拉姆萨 见习战士 +200 JP", Assert.Single(model.Pending).ChangeText);
        unit.Jobs[1].Add500.Execute(null);
        Assert.Equal(2, model.Pending.Count);
        Assert.True(model.HasJpChanges);
        Assert.Equal(0x43u, unit.Jobs[0].LevelByte.Value);
        Assert.Same(unit.Jobs[0].LevelByte, unit.Jobs[1].LevelByte);
        Assert.Equal(0, memory.WriteCount);
        model.ApplySnapshot(memory.Snapshot());
        Assert.Equal(379u, unit.Jobs[0].Jp.Value);
        await model.WriteAsync();
        Assert.Empty(model.Pending);
        Assert.False(model.HasJpChanges);
        Assert.Equal(15, memory.WriteCount);
        foreach (var roster in new[] { FftLayout.Roster }.Concat(FftLayout.RosterMirrors))
        {
            Assert.Equal(0x43, memory.ReadBytes(roster + FftLayout.JobLevels, 1)[0]);
            Assert.Equal(900, BinaryPrimitives.ReadUInt16LittleEndian(memory.ReadBytes(roster + FftLayout.TotalJp, 2)));
            Assert.Equal(500, BinaryPrimitives.ReadUInt16LittleEndian(memory.ReadBytes(roster + FftLayout.TotalJp + 2, 2)));
        }
    }

    [Fact]
    public void LevelButtonsUseRemainingTotalAndUndoClearsJpNotice()
    {
        var (_, model) = Create();
        var job = model.SelectedUnit!.Jobs[0];
        Assert.Equal(400, job.Next);
        job.LevelUp.Execute(null);
        Assert.Equal(1100u, job.Total.Value);
        Assert.Equal(579u, job.Jp.Value);
        job.Level8.Execute(null);
        Assert.Equal(3000u, job.Total.Value);
        Assert.Equal(2479u, job.Jp.Value);
        Assert.Equal(8, job.Level);
        Assert.Equal(0, job.Next);
        job.Level8.Execute(null);
        Assert.Single(model.Pending);
        model.Undo.Execute(null);
        Assert.Empty(model.Pending);
        Assert.False(model.HasJpChanges);
        model.SelectedUnit.Level.Value = 20;
        Assert.False(model.HasJpChanges);
    }

    [Fact]
    public void CappedCurrentStillStagesTotalAndLevel()
    {
        var (memory, model) = Create();
        memory.Seed(FftLayout.Roster + FftLayout.CurrentJp, BitConverter.GetBytes((ushort)9999));
        model.ApplySnapshot(memory.Snapshot());
        model.SelectedUnit!.Jobs[0].Add500.Execute(null);
        Assert.Single(model.Pending);
        Assert.True(model.HasJpChanges);
        Assert.Equal(1200u, model.SelectedUnit.Jobs[0].Total.Value);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(199, 1)]
    [InlineData(200, 2)]
    [InlineData(400, 3)]
    [InlineData(700, 4)]
    [InlineData(1100, 5)]
    [InlineData(1600, 6)]
    [InlineData(2200, 7)]
    [InlineData(3000, 8)]
    public void JobLevelUsesOriginalThresholds(int total, int level) =>
        Assert.Equal(level, GameData.JobLevel(total));

    private static TestMemory StatMemory()
    {
        var memory = new TestMemory();
        var dos = new byte[64];
        BinaryPrimitives.WriteUInt16LittleEndian(dos, 0x5A4D);
        BinaryPrimitives.WriteUInt32LittleEndian(dos.AsSpan(0x3C), 0x80);
        memory.Seed(0, dos);
        var pe = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(pe, 0x4550);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(6), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(20), 0xF0);
        memory.Seed(0x80, pe);
        var sections = new byte[80];
        for (var i = 0; i < 2; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(sections.AsSpan(i * 40 + 12), (uint)(0x900000 + i * 0x100000));
            BinaryPrimitives.WriteUInt32LittleEndian(sections.AsSpan(i * 40 + 16), 0x10000);
            BinaryPrimitives.WriteUInt32LittleEndian(sections.AsSpan(i * 40 + 20), (uint)(0x780000 + i * 0x80000));
        }
        memory.Seed(0x188, sections);
        void Table(uint file, byte[] bytes) => memory.Seed(PeFileMap.Read(memory, 0).ToRva(file, bytes.Length), bytes);
        foreach (var (id, values) in new (int, byte[])[]
        {
            (1, [11, 120, 11, 105, 95, 100, 50, 110, 48, 100, 4, 3, 10]),
            (74, [11, 100, 15, 75, 100, 100, 60, 90, 50, 80, 4, 3, 5]),
            (75, [12, 80, 16, 75, 100, 100, 75, 75, 50, 80, 3, 3, 5])
        })
        {
            var job = new byte[49];
            values.CopyTo(job, 14);
            Table(0x784230u + (uint)id * 49, job);
        }
        foreach (var (id, type, secondary, bonus) in new[]
        {
            (1, 0x80, 1, 0), (2, 0x80, 2, 0), (19, 0x80, 19, 0),
            (157, 0x20, 13, 0), (186, 0x10, 42, 0), (208, 0, 0, 42)
        })
        {
            var common = new byte[12];
            common[3] = (byte)type;
            common[4] = (byte)secondary;
            common[7] = (byte)bonus;
            Table(0x80CE90u + (uint)id * 12, common);
        }
        Table(0x80DEB0 + 13 * 2, [8, 0]);
        Table(0x80DEB0 + 42 * 2, [5, 0]);
        var boots = new byte[26];
        boots[3] = 1;
        Table(0x80E2A0 + 42 * 26, boots);
        return memory;
    }

    private static byte[] StatUnit(int slot)
    {
        uint[][] raw =
        [
            [503494, 247480, 98304, 81920, 81920],
            [511876, 241138, 98304, 81920, 65536],
            [515667, 233509, 98304, 81920, 65536],
            [515373, 242563, 98304, 81920, 65536],
            [486880, 256596, 98304, 65536, 81920],
            [466120, 250708, 98304, 65536, 81920],
            [475933, 253187, 98304, 65536, 81920]
        ];
        int[] names = [1, 329, 468, 459, 625, 695, 601];
        int[] jobs = [1, 74, 74, 75, 74, 74, 75];
        int[] weapons = [19, 2, 1, 1, 19, 1, 1];
        var data = new byte[FftLayout.UnitSize];
        data[0] = slot == 0 ? (byte)1 : slot < 4 ? (byte)128 : (byte)129;
        data[1] = (byte)slot;
        data[2] = (byte)jobs[slot];
        data[FftLayout.Level] = 1;
        data[FftLayout.Experience] = 40;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(FftLayout.NameId), (ushort)names[slot]);
        int[] equipment = [slot == 0 ? 255 : 157, 186, slot == 0 ? 208 : 255, weapons[slot], 255, 255, 255];
        for (var i = 0; i < equipment.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(FftLayout.Equipment + i * 2), (ushort)equipment[i]);
        for (var i = 0; i < 5; i++)
            Values.ToBytes(raw[slot][i], ValueTypeCode.U24).CopyTo(data, FftLayout.RawStats + i * 3);
        return data;
    }

    public static IEnumerable<object[]> ScreenshotValues()
    {
        // Independent screenshot expectations in HP, MP, SP, PA, MA, Move, Jump order.
        int[][] expected =
        [
            [41, 15, 6, 5, 5, 5, 3], [44, 11, 6, 4, 3, 4, 3],
            [44, 10, 6, 4, 3, 4, 3], [38, 11, 6, 3, 3, 3, 3],
            [42, 11, 6, 3, 4, 4, 3], [41, 11, 6, 3, 4, 4, 3],
            [36, 11, 6, 3, 4, 3, 3]
        ];
        for (var slot = 0; slot < 7; slot++)
            for (var stat = 0; stat < 7; stat++)
                yield return new object[] { slot, stat, expected[slot][stat] };
    }

    [Theory]
    [MemberData(nameof(ScreenshotValues))]
    public void Screenshot49ValuesMatchThroughPeAndEquipmentTables(int slot, int stat, int expected)
    {
        var memory = StatMemory();
        var data = StatUnit(slot);
        var rules = StatTables.TryLoad(memory, 0)!.ReadUnit(data)!;
        var raw = Enumerable.Range(0, 5).Select(i => (uint)Values.Bits(data.AsSpan(32 + i * 3), ValueTypeCode.U24)).ToArray();
        Assert.Equal(expected, UnitStats.Calculate(raw, rules)[stat]);
        Assert.Equal(0, memory.WriteCount);
    }

    [Fact]
    public void PeMappingUsesEachSectionsRawPointerAndRejectsUnmappedOffsets()
    {
        var memory = StatMemory();
        var map = PeFileMap.Read(memory, 0);
        Assert.Equal(0x904230UL, map.ToRva(0x784230, 49));
        Assert.Equal(0xA0CE90UL, map.ToRva(0x80CE90, 12));
        Assert.Throws<InvalidDataException>(() => map.ToRva(0x790000, 1));
        Assert.Throws<InvalidDataException>(() => map.ToRva(0x78FFFF, 2));
        memory.Seed(0, [0, 0]);
        Assert.Null(StatTables.TryLoad(memory, 0));
    }

    [Fact]
    public void MissingTablesDisableStatsAndGrowth()
    {
        var unit = new UnitViewModel(new UnitSnapshot(0, StatUnit(0)), new FftLayout(), TestText.Legacy());
        Assert.All(unit.Stats, stat => Assert.Equal("—", stat.Text));
        Assert.Equal("—", unit.Move);
        Assert.Equal("—", unit.Jump);
        Assert.False(unit.Gain1.CanExecute(null));
        unit.Stats[0].Text = "100";
        Assert.False(unit.IsDirty);
    }

    [Fact]
    public void TextCatalogUsesIdsCategoriesAndIndependentFallbacks()
    {
        // 游戏表的行（与运行时从封包读出的结构相同）→ GameTextData → 换入目录
        static FftTrainer.GameFiles.GameTableRow Row(int key, params (string Column, object? Value)[] cells) =>
            new(key, cells.ToDictionary(cell => cell.Column, cell => cell.Value));
        var data = GameTextData.FromTables(new Dictionary<string, IReadOnlyList<FftTrainer.GameFiles.GameTableRow>>
        {
            ["charaname"] = [Row(625, ("Name", "测试队员")), Row(1, ("Name", "测试主角")), Row(2, ("Name", ""))],
            ["job"] = [Row(74, ("Name", "测试职业"), ("jobcommand+Id", 5u)), Row(93, ("Name", "末尾职业"), ("jobcommand+Id", 24u)),
                Row(1, ("Name", "专属职业"), ("jobcommand+Id", 25u))],
            ["jobcommand"] = [Row(5, ("Name", "基本技")), Row(25, ("Name", "斗志")), Row(99, ("Name", "未被职业引用"))],
            ["uiitemcategory"] = [Row(3, ("Name", "消耗品")), Row(7, ("Name", "帽子"))],
            ["item"] = [Row(240, ("Name", "恢复药"), ("NameSingular", "恢复药"), ("UiItemCategoryId", 3)),
                Row(253, ("Name", ""), ("NameSingular", "不死鸟之尾")),
                Row(157, ("Name", "测试帽"), ("UiItemCategoryId", 7)),
                Row(260, ("Name", "扩展物品"), ("UiItemCategoryId", 42))],
            ["ability"] = [Row(1, ("Name", "测试技能"))],
        });
        Assert.Equal("不死鸟之尾", data.Items[253]);          // Name 为空时取 NameSingular
        Assert.False(data.Units.ContainsKey(2));              // 空名称不收
        Assert.Equal([5, 25], data.Commands.Keys.Order());    // 只收职业表引用到的组合（24 在 jobcommand 里没有）
        Assert.False(data.ItemCategories.ContainsKey(260));   // 类别编号不存在
        var catalog = new GameTextCatalog();
        catalog.Load(data);
        var memory = StatMemory();
        var model = new TrainerViewModel(memory, text: catalog);
        model.ApplySnapshot(new GameSnapshot(1, 0, [new UnitSnapshot(0, StatUnit(0))], new byte[256]));
        Assert.Equal("测试主角", model.SelectedUnit!.Name);
        Assert.Equal("专属职业", model.SelectedUnit.Job);
        Assert.Equal("测试职业", model.SelectedUnit.Jobs[0].Name);
        Assert.Equal("末尾职业", model.SelectedUnit.Jobs[19].Name);
        Assert.Equal("恢复药", model.Items[240].Name);
        Assert.Equal("不死鸟之尾", model.Items[253].Name);
        Assert.Equal("帽子", model.Items[157].Category);
        Assert.Equal("装备", model.Items[157].Group);
        Assert.Equal("扩展物品", catalog.ItemName(260));
        Assert.Equal("测试技能", catalog.AbilityName(1));
        Assert.Equal("斗志", catalog.SkillGroupName(25));
        Assert.Equal("技能组 #99", catalog.SkillGroupName(99));
        Assert.Equal(256, model.Items.Count);
        Assert.Equal(GameData.UnitName(329), catalog.UnitName(329));
        Assert.Equal(GameData.ItemName(19), catalog.ItemName(19));
        // 读不到游戏文本：全部走编号兜底
        var missing = new GameTextCatalog();
        missing.Load(null);
        Assert.True(missing.IsEmpty);
        Assert.Equal(GameData.JobName(74), missing.JobName(74));
        Assert.Equal("职业 0x4A", missing.JobName(74));
        Assert.Equal("猎物 #1 ★", missing.PoachingName(1));
        catalog.Load(null);
        Assert.Equal(GameData.UnitName(1), catalog.UnitName(1));
        catalog.Parse("jobs", """[{"id":"bad","name":"bad"},{"id":74,"name":""},null]""");
        Assert.Equal(GameData.JobName(74), catalog.JobName(74));
    }

    [Theory]
    [InlineData(100, 503494, 120, 5, 999, true, 1297067)]
    [InlineData(41, 503494, 120, 5, 999, true, 503494)]
    [InlineData(1, 100000, 100, 0, 99, true, 32767)]
    [InlineData(1, 0, 100, 0, 99, true, 0)]
    [InlineData(12, 486880, 100, 13, 999, false, 486880)]
    [InlineData(99, 0, 100, 0, 99, true, 1622016)]
    [InlineData(999, 0, 1, 0, 999, false, 0)]
    [InlineData(51, 98304, 100, 0, 50, false, 98304)]
    [InlineData(1, 123, 0, 0, 99, true, 123)]
    [InlineData(2, 123, 0, 0, 99, false, 123)]
    [InlineData(99, 123, 0, 100, 99, true, 123)]
    public void InverseKeepsHiddenProgressOrChoosesNearestReachableEndpoint(int target, uint old,
        int multiplier, int bonus, int cap, bool reachable, uint expected)
    {
        Assert.Equal(reachable, UnitStats.TryInverse(target, old, multiplier, bonus, cap, out var raw));
        Assert.Equal(expected, raw);
        if (reachable)
            Assert.Equal(target, UnitStats.Display(raw, multiplier, bonus, cap));
    }

    [Fact]
    public void InverseMatchesBothEndpointsAtEveryDisplayBoundary()
    {
        foreach (var multiplier in new[] { 1, 75, 120, 255 })
            foreach (var cap in new[] { 50, 99, 999 })
                foreach (var bonus in new[] { 0, 13, 1000 })
                    for (var target = 1; target <= cap; target++)
                    {
                        var reachable = UnitStats.TryInverse(target, 0, multiplier, bonus, cap, out var low);
                        Assert.Equal(reachable, UnitStats.TryInverse(target, UnitStats.RawMaximum,
                            multiplier, bonus, cap, out var high));
                        if (!reachable)
                            continue;
                        Assert.Equal(target, UnitStats.Display(low, multiplier, bonus, cap));
                        Assert.Equal(target, UnitStats.Display(high, multiplier, bonus, cap));
                        if (low > 0)
                            Assert.NotEqual(target, UnitStats.Display(low - 1, multiplier, bonus, cap));
                        if (high < UnitStats.RawMaximum)
                            Assert.NotEqual(target, UnitStats.Display(high + 1, multiplier, bonus, cap));
                    }
    }

    [Fact]
    public async Task ExperimentalEditsAndGrowthStageReadableChipsAndWriteOnlyThreeBytes()
    {
        var memory = StatMemory();
        var data = StatUnit(0);
        foreach (var roster in new[] { FftLayout.Roster }.Concat(FftLayout.RosterMirrors))
            memory.Seed(roster, data);
        var rules = StatTables.TryLoad(memory, 0)!.ReadUnit(data);
        var model = new TrainerViewModel(memory, text: TestText.Legacy());
        model.ApplySnapshot(new GameSnapshot(1, 0, [new UnitSnapshot(0, data, rules)], new byte[256]));
        var unit = model.SelectedUnit!;
        unit.Gain1.Execute(null);
        Assert.Equal(new uint[] { 41957, 20623, 1024, 1606, 1671 },
            unit.Stats.Select(stat => stat.Raw.Value - stat.Raw.Original));
        Assert.Equal(new[] { "44", "17", "5", "5", "6" }, unit.DisplayStats.Select(stat => stat.Text));
        Assert.Equal(2u, unit.Level.Value);
        Assert.Equal(40u, unit.Experience.Value);
        Assert.Equal(6, model.Pending.Count);
        Assert.All(unit.Stats, stat => Assert.True(stat.IsPreview));
        Assert.Equal(0, memory.WriteCount);
        await model.WriteAsync();
        Assert.Equal(18, memory.WriteCount);
        Assert.Empty(model.Pending);
        foreach (var roster in new[] { FftLayout.Roster }.Concat(FftLayout.RosterMirrors))
        {
            var after = memory.ReadBytes(roster, data.Length);
            Assert.Equal(40, after[FftLayout.Experience]);
            Assert.Equal(2, after[FftLayout.Level]);
            for (var i = 0; i < data.Length; i++)
                if (i != FftLayout.Level && (i < 0x20 || i > 0x2E))
                    Assert.Equal(data[i], after[i]);
        }
        unit.Stats[0].Text = "100";
        Assert.Equal("拉姆萨 最大HP 44 → 100", Assert.Single(model.Pending).ChangeText);
        var staged = unit.Stats[0].Raw.Value;
        unit.Stats[0].Text = "1";
        Assert.Equal(staged, unit.Stats[0].Raw.Value);
        Assert.True(unit.Stats[0].HasError);
        model.Undo.Execute(null);
        Assert.Empty(model.Pending);
    }

    [Fact]
    public void GrowthIsPerLevelUsesMinimumConstantAndSaturates()
    {
        uint[] raw = [503494, 247480, 98304, 81920, 81920];
        byte[] growth = [11, 11, 95, 50, 48];
        var oneAtATime = raw;
        for (var level = 1; level < 6; level++)
            oneAtATime = UnitStats.Grow(oneAtATime, level, level + 1, growth);
        Assert.Equal(oneAtATime, UnitStats.Grow(raw, 1, 6, growth));
        Assert.Equal(new uint[] { 150, 150, 150, 150, 150 },
            UnitStats.Grow([100, 100, 100, 100, 100], 1, 2, [0, 0, 0, 0, 0]));
        Assert.All(UnitStats.Grow(Enumerable.Repeat(UnitStats.RawMaximum, 5).ToArray(), 1, 99, growth),
            value => Assert.Equal(UnitStats.RawMaximum, value));
        var memory = StatMemory();
        var data = StatUnit(0);
        var unit = new UnitViewModel(new UnitSnapshot(0, data, StatTables.TryLoad(memory, 0)!.ReadUnit(data)), new FftLayout(), TestText.Legacy());
        unit.Gain5.Execute(null);
        Assert.Equal(6u, unit.Level.Value);
        Assert.Equal(oneAtATime, unit.Stats.Select(stat => stat.Raw.Value));
        unit.Gain99.Execute(null);
        Assert.Equal(99u, unit.Level.Value);
        Assert.False(unit.Gain1.CanExecute(null));
        Assert.Equal(40u, unit.Experience.Value);
    }

    [Fact]
    public void RawWritesRejectMisalignmentAndOverflow()
    {
        var memory = StatMemory();
        foreach (var edit in new[]
        {
            new MemoryEdit(FftLayout.Roster + 0x21, 3, 0, 1),
            new MemoryEdit(FftLayout.Roster + 0x20, 3, 0, 0x1000000),
            new MemoryEdit(FftLayout.Gil, 3, 0, 1)
        })
            Assert.NotNull(Assert.Single(GameSession.ApplyEdits(memory, memory.WriteBytes, 0, [edit])).Error);
        Assert.Equal(0, memory.WriteCount);
    }


    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void SingleRawWritePreservesEveryAdjacentByteInAllThreeRosters(int index)
    {
        var memory = StatMemory();
        var before = StatUnit(4);
        before[0x2F] = 0xAB;
        ulong[] rosters = [FftLayout.Roster, .. FftLayout.RosterMirrors];
        foreach (var roster in rosters)
            memory.Seed(roster, before);
        var offset = FftLayout.RawStats + index * 3;
        var old = (uint)Values.Bits(before.AsSpan(offset), ValueTypeCode.U24);
        var edit = new MemoryEdit(FftLayout.Roster + (ulong)offset, 3, old, 0xA1B2C3);
        var lengths = new List<int>();
        var result = Assert.Single(GameSession.ApplyEdits(memory, (address, bytes) =>
        {
            lengths.Add(bytes.Length);
            memory.WriteBytes(address, bytes);
        }, 0, [edit]));
        Assert.Null(result.Error);
        Assert.Equal(new[] { 3, 3, 3 }, lengths);
        foreach (var roster in rosters)
        {
            var after = memory.ReadBytes(roster, before.Length);
            Assert.Equal(new byte[] { 0xC3, 0xB2, 0xA1 }, after[offset..(offset + 3)]);
            Assert.Equal(before[..offset], after[..offset]);
            Assert.Equal(before[(offset + 3)..], after[(offset + 3)..]);
        }
    }

    [Fact]
    public void AttributeBonusesStackAndJumpIsCappedWithoutAddingWeaponPower()
    {
        var memory = StatMemory();
        var map = PeFileMap.Read(memory, 0);
        var bonus = new byte[26];
        bonus[0] = 2;
        bonus[1] = 3;
        bonus[2] = 4;
        bonus[3] = 2;
        bonus[4] = 8;
        memory.Seed(map.ToRva(0x80E2A0 + 42 * 26, 26), bonus);
        var unit = StatUnit(0);
        var rules = StatTables.TryLoad(memory, 0)!.ReadUnit(unit)!;
        Assert.Equal(new[] { 41, 15, 10, 7, 8, 6, 7 },
            UnitStats.Calculate([503494, 247480, 98304, 81920, 81920], rules));
    }

    [Fact]
    public async Task UpgradeIncludesAllFiveRawFieldsEvenWhenTheyAreCapped()
    {
        var memory = StatMemory();
        var data = StatUnit(0);
        for (var i = 0; i < 5; i++)
            Values.ToBytes(UnitStats.RawMaximum, ValueTypeCode.U24).CopyTo(data, 32 + i * 3);
        foreach (var roster in new[] { FftLayout.Roster }.Concat(FftLayout.RosterMirrors))
            memory.Seed(roster, data);
        var model = new TrainerViewModel(memory, text: TestText.Legacy());
        model.ApplySnapshot(new GameSnapshot(1, 0,
            [new UnitSnapshot(0, data, StatTables.TryLoad(memory, 0)!.ReadUnit(data))], new byte[256]));
        model.SelectedUnit!.Gain1.Execute(null);
        await model.WriteAsync();
        Assert.Equal(18, memory.WriteCount);
        Assert.Equal(2u, model.SelectedUnit.Level.Value);
    }


    [Fact]
    public void PollingDoesNotReplaceFocusedStatTextAndUndoUsesLatestRaw()
    {
        var memory = StatMemory();
        var data = StatUnit(4);
        var rules = StatTables.TryLoad(memory, 0)!.ReadUnit(data);
        var model = new TrainerViewModel(memory, text: TestText.Legacy());
        model.ApplySnapshot(new GameSnapshot(1, 0, [new UnitSnapshot(4, data, rules)], new byte[256]));
        var stat = model.SelectedUnit!.Stats[0];
        var textChanges = 0;
        stat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StatViewModel.Text))
                textChanges++;
        };
        stat.Raw.IsEditing = true;
        model.ApplySnapshot(new GameSnapshot(1, 0, [new UnitSnapshot(4, data, rules)], new byte[256]));
        Assert.Equal(0, textChanges);
        stat.Text = "100";
        stat.Raw.IsEditing = false;
        Assert.Equal("香奈尔 最大HP 42 → 100", Assert.Single(model.Pending).ChangeText);
        Values.ToBytes(500000, ValueTypeCode.U24).CopyTo(data, 32);
        model.ApplySnapshot(new GameSnapshot(1, 0, [new UnitSnapshot(4, data, rules)], new byte[256]));
        Assert.Equal("100", stat.Text);
        model.Undo.Execute(null);
        Assert.Equal(500000u, stat.Raw.Value);
        Assert.Empty(model.Pending);
    }


    // docs/skills-research.md §2：2026-10-17 00:45:33 的七人快照。
    // roster SHA256 5d4cba724c51f554d800d4c4bdcca4321ee3af2905ec659554c68afb0235fcb2。
    // 保留 +00..+73（身份、槽值、职业可用及全部习得位）与 +230 名字编号；无文件/游戏依赖。
    private static readonly string[] SkillRosterHex =
    [
        "0100010080E24000000000000000FF00BA00D0001300FF00FF00FF0028024746AB5208471704008401464601874601C00000040000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
        "80014A00907D10000000000000009D00BA00FF000200FF00FF00FF001101474884CF07F2AD03008001004001000001F00000F000F0820000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
        "80024A0090E540000000E00100009D00BA00FF000100FF00FF00FF001301343E53DE07259003008001004001000001C00000200010820010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
        "80034B0090F850000000000000009D00BA00FF000100FF00FF00FF003B01413F2DDD0783B303008001004001000001C00000400000800000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
        "81044A0050AF30000000000000009D00BA00FF001300FF00FF00FF0033014931E06D0754EA03008001000001004001C00000200000820000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
        "81054A00505300000000000000009D00BA00FF000100FF00FF00FF0061013640C81C0754D303008001000001004001F00000600000800400000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
        "81064B00500B90000000E00100009D00BA00FF000100FF00FF00FF00530144381D430703DD03008001000001004001C00000400000800410000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
    ];

    private static byte[] SkillUnit(int slot)
    {
        var unit = new byte[FftLayout.UnitSize];
        Convert.FromHexString(SkillRosterHex[slot]).CopyTo(unit, 0);
        ushort[] names = [1, 329, 468, 459, 625, 695, 601];
        BinaryPrimitives.WriteUInt16LittleEndian(unit.AsSpan(FftLayout.NameId), names[slot]);
        return unit;
    }

    private static TestMemory SkillMemory()
    {
        var memory = new TestMemory();
        var dos = new byte[64];
        BinaryPrimitives.WriteUInt16LittleEndian(dos, 0x5A4D);
        BinaryPrimitives.WriteUInt32LittleEndian(dos.AsSpan(0x3C), 0x80);
        memory.Seed(0, dos);
        var pe = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(pe, 0x4550);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(6), 2);
        memory.Seed(0x80, pe);
        var sections = new byte[80];
        BinaryPrimitives.WriteUInt32LittleEndian(sections.AsSpan(12), 0x67E000);
        BinaryPrimitives.WriteUInt32LittleEndian(sections.AsSpan(16), 0x3000);
        BinaryPrimitives.WriteUInt32LittleEndian(sections.AsSpan(20), 0x67D000);
        BinaryPrimitives.WriteUInt32LittleEndian(sections.AsSpan(52), 0x781C00);
        BinaryPrimitives.WriteUInt32LittleEndian(sections.AsSpan(56), 0x10000);
        BinaryPrimitives.WriteUInt32LittleEndian(sections.AsSpan(60), 0x780000);
        memory.Seed(0x98, sections);
        // 运行时职业记录与普通技能组原字节，包含空位置和第九位标志。
        foreach (var (id, hex) in new (int, string)[]
        {
            (1, "190000000000000000D04006FF000B780B695F64326E306404030A00000000000040000000000000000000000000000000"),
            (74, "050000000000000000D24006BF000B640F4B64643C5A325004030500000000000000000000000000000000000000000000"),
            (75, "06DA01000000000000C02006BF000C50104B64644B4B325003030500000000000000000000000000000000000000000000"),
            (76, "07000000000000000098001B7F000A780F5064642878325003030A00000000000000000000000000000000000000000000"),
            (77, "080000000000000000801816BF000B64104164642D6E325003030A00000000000000000000000000000000000000000000"),
            (78, "09D801000000000000800002BF0009870D50646E3081325003041400000000000000000000000000000000000000000000"),
            (79, "0A0000000000000000808006FF000A500A78646E325A326E03030500000000000000000000000000000000000000000000"),
            (80, "0B0000000000000000810006FF000C4B097864643C3C329603030500000000000000000000000000000000000000000000"),
            (81, "0C0000000000000000808006FF000C4B0A7864644132328203030500000000000000000000000000000000000000000000"),
            (82, "0D0000000000000000818006FF000D46087D645A4632327D03030500000000000000000000000000000000000000000000"),
            (83, "0E0000000000000000C00006BF000B5A10325A6E3264323C04041900000000000000000000000000000000000000000000"),
            (84, "0FD901000000000000C02006FF000B5012466464374B324B03030500000000000000000000000000000000000000000000"),
            (85, "100000000000000000818286FF000C4B0A6E64643C32327803030500000000000000000000000000000000000000000000"),
            (86, "110000000000000000920016FF000A6E0B5F64642D6E326904030A00000000000000000000000000000000000000000000"),
            (87, "12000000000000000080011B7F000A780F3264642878323203040F00000000000000000000000000000000000000000000"),
            (88, "13000000000000000084000B7F000C4B0E5A64642D80325A03031400000000000000000000000000000000000000000000"),
            (89, "14DD01000000000000E04006BF000C460D3250782B7A324B04041E00000000000000000000000000000000000000000000"),
            (90, "150000000000000000800286FF000E410A5064324632324603030500000000000000000000000000000000000000000000"),
            (91, "160000000000000000800446BF00143714326464501E327303030500000000000000000000000000000000000000000000"),
            (92, "170000000000000000C00066BF00143C14326464326E325F03030500000000000000000000000000000000000000000000"),
            (93, "18D501D801DE0100008000000000068C1E3264782378287304040500000000000000000000000000000000000000000000"),
        })
            memory.Seed(0x785E30 + (ulong)(id * 49), Convert.FromHexString(hex));
        foreach (var (id, hex) in new (int, string)[]
        {
            (5, "0000FC92939495000000000000000000000000B4CCDEDFCFE6"),
            (6, "FFFCF8707172737475767778797A7B7C7D0000B9DADBE0FD00"),
            (7, "0000F08A8B8C8D8E8F90910000000000000000BFC6C7C80000"),
            (8, "FF00F8969798999A9B9C9D0000000000000000A8C4CAD5E900"),
            (9, "0000F86465666768696A6B0000000000000000AFBAC5D8ED00"),
            (10, "0000C00102030405060708090A0B0C0D0E0F00ACD400000000"),
            (11, "0000C0101112131415161718191A1B1C1D1E1FB3D300000000"),
            (12, "0000F82021222324252627292A2B2C00000000B1BDE2F2FA00"),
            (13, "0000C03C3D3E3F404142434445464748494A4BB0CE00000000"),
            (14, "0000FC6C6D6E6F707172730000000000000000AAB7C2D7E7EA"),
            (15, "0000F07475767778797A7B7C7D000000000000C0CDD6D90000"),
            (16, "0000F02E2F303132333435363738393A3B0000B6D2F4EE0000"),
            (17, "0000F07E7F8081828384858687888900000000B5D1F5F80000"),
            (18, "FFF0E08A8B8C8D8E8F90919293949500000000ABCBEC000000"),
            (19, "0000F84C4D4E4F505152535455000000000000B2C3C9DCF700"),
            (20, "FFF0F07E897F80818283848586878800000000A9C1DDF60000"),
            (21, "FF00F89E9FA0A1A2A3A4A50000000000000000BCBED0EFF000"),
            (22, "0000F0565758595A5B5C000000000000000000A7AEE8FB0000"),
            (23, "0000F05D5E5F60616263000000000000000000A6ADEBFB0000"),
            (24, "00000000000000000000000000000000000000000000000000"),
            (25, "0000FC9293949596989A000000000000000000B4CCDEDFCFE6"),
        })
            memory.Seed(0x67E210 + (ulong)(id * 25), Convert.FromHexString(hex));
        for (var slot = 0; slot < 50; slot++)
            memory.Seed(FftLayout.Roster + (ulong)(slot * 600) + 1, [0xFF]);
        for (var slot = 0; slot < 7; slot++)
            foreach (var roster in new[] { FftLayout.Roster }.Concat(FftLayout.RosterMirrors))
                memory.Seed(roster + (ulong)(slot * 600), SkillUnit(slot));
        return memory;
    }

    private static GameTextCatalog SkillText()
    {
        var text = new GameTextCatalog();
        text.Parse("unit-names", """[{"id":1,"name":"拉姆萨"},{"id":329,"name":"齐克"},{"id":468,"name":"麦克尼尔"},{"id":459,"name":"霍特曼"},{"id":625,"name":"香奈尔"},{"id":695,"name":"纽特"},{"id":601,"name":"桑德拉"}]""");
        text.Parse("abilities", """
            [
              {"id":436,"name":"冲撞反击"},
              {"id":460,"name":"装备斧"},
              {"id":478,"name":"怪物驯化师"},
              {"id":479,"name":"闪避姿态"},
              {"id":480,"name":"更换战斗装备"},
              {"id":486,"name":"移动+1"}
            ]
            """);
        return text;
    }

    [Fact]
    public void SevenLiveSkillSnapshotsMatchResearchCandidatesAndSlotValues()
    {
        var memory = SkillMemory();
        var tables = SkillTables.TryLoad(memory, 0)!;
        int[][] secondary = [[0], [0, 6], [0, 6], [0, 5], [0, 6], [0, 6], [0, 5]];
        int[][] reactions = [[0], [0, 436], [0], [0], [0], [0], [0]];
        int[][] supports = [[0], [0, 460, 478, 479], [0, 479, 480], [0], [0], [0], [0, 480]];
        for (var slot = 0; slot < 7; slot++)
        {
            var unit = SkillUnit(slot);
            var rules = tables.ReadUnit(unit)!;
            Assert.True(rules.Supported);
            Assert.Equal(slot == 0 ? 25 : slot is 3 or 6 ? 6 : 5, rules.Primary);
            Assert.Equal(secondary[slot], rules.Candidates(SkillSlot.Secondary));
            Assert.Equal(reactions[slot], rules.Candidates(SkillSlot.Reaction));
            Assert.Equal(supports[slot], rules.Candidates(SkillSlot.Support));
            Assert.Equal(new[] { 0 }, rules.Candidates(SkillSlot.Movement));
            Assert.Equal(slot is 2 or 6 ? 480 : 0, BinaryPrimitives.ReadUInt16LittleEndian(unit.AsSpan(10)));
        }
        var sigurdh = tables.ReadUnit(SkillUnit(1))!;
        Assert.Equal(new[] { 146, 147, 148, 149, 436, 460, 478, 479 }, sigurdh.Jobs[0].Learned);
        Assert.Equal(10, sigurdh.Jobs[0].Abilities.Length);
        Assert.Equal(new[] { 368, 374 }, sigurdh.Jobs[1].Learned);
        Assert.Equal(new[] { 152 }, tables.ReadUnit(SkillUnit(0))!.Jobs[0].Learned);
    }

    [Theory]
    [InlineData(SkillSlot.Secondary, 6, 1)]
    [InlineData(SkillSlot.Reaction, 436, 2)]
    [InlineData(SkillSlot.Support, 478, 2)]
    [InlineData(SkillSlot.Movement, 486, 2)]
    public async Task SkillSelectionStagesAndWritesOnlyExactSlotInThreeCopies(SkillSlot slot, int id, int width)
    {
        var memory = SkillMemory();
        if (slot == SkillSlot.Movement)
            memory.Seed(FftLayout.Roster + 600 + 0x34, [0xF4]);
        var model = new TrainerViewModel(memory, text: SkillText());
        model.ApplySnapshot(memory.Snapshot());
        var unit = model.Units[1];
        Assert.Equal(slot == SkillSlot.Movement ? "已学 9 / 共 10" : "已学 8 / 共 10", unit.Jobs[0].LearnedText);
        var skill = unit.SkillSlots[(int)slot];
        var before = new[] { FftLayout.Roster }.Concat(FftLayout.RosterMirrors)
            .Select(roster => memory.ReadBytes(roster + 600, 600)).ToArray();
        Assert.True(skill.Select(id));
        Assert.Equal(0, memory.WriteCount);
        Assert.Single(model.Pending);
        Assert.Equal(width, skill.Field.ToEdit().Width);
        if (slot == SkillSlot.Reaction)
            Assert.Equal("齐克 触发 未装备 → 冲撞反击", model.Pending[0].ChangeText);
        await model.WriteAsync();
        Assert.Equal(3, memory.WriteCount);
        Assert.Empty(model.Pending);
        var index = 0;
        foreach (var roster in new[] { FftLayout.Roster }.Concat(FftLayout.RosterMirrors))
        {
            var expected = before[index++];
            Values.ToBytes((uint)id, width == 1 ? ValueTypeCode.U8 : ValueTypeCode.U16)
                .CopyTo(expected, UnitSkillRules.Offset(slot));
            Assert.Equal(expected, memory.ReadBytes(roster + 600, 600));
        }
    }

    [Theory]
    [InlineData(510)]
    [InlineData(511)]
    [InlineData(65535)]
    [InlineData(436)]
    public void SpecialAndWrongCategorySlotsAreReadOnly(int value)
    {
        var memory = SkillMemory();
        memory.Seed(FftLayout.Roster + 600 + 12, BitConverter.GetBytes((ushort)value));
        var model = new TrainerViewModel(memory, text: SkillText());
        model.ApplySnapshot(memory.Snapshot());
        var slot = model.Units[1].SkillSlots[3];
        Assert.Contains(value.ToString(), slot.IdText);
        Assert.False(slot.CanChange);
        Assert.False(slot.Select(0));
        Assert.Empty(model.Pending);
    }

    [Fact]
    public void SkillNamesUseIdsAndGroupNamesNeverUseJobAssociationAsRuntimeTruth()
    {
        var text = SkillText();
        // 技能组名只从游戏的 JobCommand 表查（经职业表关联导出），关联不决定运行时组 ID
        text.Parse("jobs", """[{"id":74,"name":"见习战士","jobCommandId":5,"jobCommandName":"基本技"},{"id":1,"name":"见习战士","jobCommandId":25,"jobCommandName":"斗志"}]""");
        Assert.Equal("更换战斗装备", text.AbilityName(480));
        Assert.Equal("技能 #481", text.AbilityName(481));
        Assert.Equal("未装备", text.AbilityName(0));
        Assert.Equal("基本技", text.SkillGroupName(5));
        Assert.Equal("斗志", text.SkillGroupName(25));
        Assert.Equal("技能组 #175", text.SkillGroupName(175));
        var memory = SkillMemory();
        var model = new TrainerViewModel(memory, text: text);
        model.ApplySnapshot(memory.Snapshot());
        Assert.Equal("基本技  · #5", model.Units[1].PrimarySkill);
        Assert.Equal("更换战斗装备", model.Units[2].SkillSlots[2].DisplayName);
        Assert.False(model.Units[1].SkillSlots[2].Select(463));
        Assert.False(model.Units[1].SkillSlots[3].Select(486));
        Assert.True(model.Units[2].SkillSlots[2].Select(0));
        Assert.Equal("麦克尼尔 辅助 更换战斗装备 → 未装备", model.Pending[0].ChangeText);
        model.Undo.Execute(null);
        Assert.Empty(model.Pending);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task SkillWriteRechecksIdentityLearningAndBothMirrors(int change)
    {
        var memory = SkillMemory();
        var model = new TrainerViewModel(memory, text: SkillText());
        model.ApplySnapshot(memory.Snapshot());
        Assert.True(model.Units[1].SkillSlots[1].Select(436));
        var start = FftLayout.Roster + 600;
        switch (change)
        {
            case 0: memory.Seed(start + 0x34, [0]); break;
            case 1: memory.Seed(start + 2, [75]); break;
            case 2: memory.Seed(start + FftLayout.NameId, [1, 0]); break;
            case 3: memory.Seed(FftLayout.RosterMirrors[0] + 600, [0x81]); break;
            case 4: memory.Seed(FftLayout.RosterMirrors[1] + 600 + 8, [0xB4, 1]); break;
            case 5: memory.Seed(start + 8, [0xFE, 1]); break;
        }
        await model.WriteAsync();
        Assert.Equal(0, memory.WriteCount);
        Assert.Single(model.Pending);
        Assert.NotNull(model.Pending[0].Error);
    }

    [Fact]
    public void SkillRulesKeepUnlockGenderInnateAndUnsupportedBoundaries()
    {
        var memory = SkillMemory();
        var tables = SkillTables.TryLoad(memory, 0)!;
        var unit = SkillUnit(1);
        unit[0x2F] = 0x80;
        Assert.Equal(new[] { 0 }, tables.ReadUnit(unit)!.Candidates(SkillSlot.Secondary));
        unit[0] = 2;
        Assert.False(tables.ReadUnit(unit)!.Supported);
        unit = SkillUnit(1);
        unit[2] = 75;
        unit[0x37] = 0x40; // 道具士已学投掷道具，但它也是本职自带技能。
        Assert.DoesNotContain(474, tables.ReadUnit(unit)!.Candidates(SkillSlot.Support));
        unit = SkillUnit(1);
        unit[0x31] |= 0x60;
        unit[0x32 + 17 * 3] = 0x80;
        unit[0x32 + 18 * 3] = 0x80;
        unit[4] = 0x40;
        Assert.DoesNotContain(0x16, tables.ReadUnit(unit)!.Candidates(SkillSlot.Secondary));
        unit[4] = 0x80;
        Assert.DoesNotContain(0x17, tables.ReadUnit(unit)!.Candidates(SkillSlot.Secondary));
        memory.Seed(0, [0, 0]);
        Assert.Null(SkillTables.TryLoad(memory, 0));
    }


    [Fact]
    public async Task SkillRefreshInvalidatesStagedCandidateAndUnknownTablesStayReadOnly()
    {
        var memory = SkillMemory();
        var model = new TrainerViewModel(memory, text: SkillText());
        model.ApplySnapshot(memory.Snapshot());
        var skill = model.Units[1].SkillSlots[1];
        Assert.True(skill.Select(436));
        memory.Seed(FftLayout.Roster + 600 + 0x34, [0]);
        model.ApplySnapshot(memory.Snapshot());
        Assert.False(skill.ValidPending);
        Assert.Single(skill.Candidates);
        await model.WriteAsync();
        Assert.Equal(0, memory.WriteCount);
        Assert.Single(model.Pending);
        model.Undo.Execute(null);
        memory.Seed(0, [0, 0]);
        model.ApplySnapshot(memory.Snapshot());
        Assert.False(skill.CanChange);
        Assert.Empty(skill.Candidates);
        Assert.Equal("已学 — / 共 —", model.Units[1].Jobs[0].LearnedText);
    }

    [Fact]
    public async Task EquippedSupportClearsBothBytesInEveryMirror()
    {
        var memory = SkillMemory();
        var model = new TrainerViewModel(memory, text: SkillText());
        model.ApplySnapshot(memory.Snapshot());
        Assert.True(model.Units[2].SkillSlots[2].Select(0));
        await model.WriteAsync();
        Assert.Equal(3, memory.WriteCount);
        Assert.Empty(model.Pending);
        foreach (var roster in new[] { FftLayout.Roster }.Concat(FftLayout.RosterMirrors))
            Assert.Equal(new byte[] { 0, 0 }, memory.ReadBytes(roster + 1200 + 10, 2));
    }

}
