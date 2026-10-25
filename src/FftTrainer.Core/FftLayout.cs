namespace FftTrainer.Core;

public sealed class FftLayout
{
    // 来源：2026-10-17 实测战斗槽 +0x42/+0x43 为 u8 移动/跳跃；移动写 8 后范围立即变大。
    // 移动目标 9 按功能要求；跳跃上限 7 来自 docs/stats-research.md 的游戏代码记录。
    // 2026-10-17 实测：+0x61 bit 0x08 为咏唱中，+0x18D 为剩余时间；写 1 后下个时间单位发动。
    public const int BattleCastTime = 0x18D;
    public const int BattleMove = 0x42;
    public const int BattleJump = 0x43;
    public const byte MaxBattleMove = 9;
    public const byte MaxBattleJump = 7;
    public const ulong Gil = 0xD40110;
    public const ulong Roster = 0x11A7D10;
    // 来源：docs/memory-map.md，2026-10-16 真实游戏二分实验。
    // 只写 R1 的 JP 会被 R2/R5 冲回；三份同写后刷新及学习技能均保留正确值。
    public static IReadOnlyList<ulong> RosterMirrors { get; } = Array.AsReadOnly<ulong>([0x2C82198, 0x2E80968]);
    // R3 = base+0x2C8BE88、R4 = base+0x2C95B6C 是旧快照，不要写。
    public const ulong Inventory = 0x11A7C00;
    // 来源：社区 CE 表 xyzjw 2026-06-28。特典 2026-10-17 实机验证：+11A7D01 写 1 后，游戏武器列表出现学院剑（持有 1、攻击 +6）。
    // 猎物区仍待验证（写 1 后没找到游戏里显示的地方，已改回 0）。
    public const ulong BonusItems = 0x11A7D01;
    public const ulong Poaching = 0x11A7A1B;
    public const int PoachingCount = 48 * 2;
    public const int InventoryCount = 0x105;
    public const ulong InventoryLast = 0x11A7D04;
    public const int Character = 0x00;
    public const int SlotMarker = 0x01;
    public const int Job = 0x02;
    public const int NameId = 0x230;
    public const int Experience = 0x1C;
    public const int Level = 0x1D;
    public const int RawStats = 0x20;
    public const int Brave = 0x1E;
    public const int Faith = 0x1F;
    public const int SecondarySkill = 0x07;
    public const int ReactionSkill = 0x08;
    public const int SupportSkill = 0x0A;
    public const int MovementSkill = 0x0C;
    public const int AvailableJobs = 0x2F;
    public const int LearnedSkills = 0x32;
    public const int Equipment = 0x0E;
    public const int JobLevels = 0x74;
    public const int CurrentJp = 0x80;
    public const int TotalJp = 0xAE;
    public const int JobCount = 22;
    public const int UnitSize = NameId + 2;

    public int RosterStride { get; init; } = 0x258;
    public int RosterCount { get; init; } = 50;
}

// build 24304444：只读写界面使用的战斗数组，不同步下游拷贝。
// 只用战斗中不会变的字段核对身份。不能含等级：击杀敌人当场升级时，等级会变，
// 「总能行动」的高频清除因此被身份校验挡下，回合被判定为已行动（2026-10-17 遭遇战实测）。
// 原始 Brave / Faith 在战斗中也可能被改动，同样不放进来。
public sealed record BattleIdentity(byte Marker, byte Faction, byte Character, byte Job,
    byte RosterIndex, int NameId);

public sealed record BattleUnit(int Slot, byte[] Data)
{
    public const ulong Start = 0x1853CE0;
    public const int Stride = 0x200;
    public const int Count = 23;
    public ulong Offset => Start + (ulong)(Slot * Stride);
    // 队伍：+0x05 的 0x30 位（0 = 蓝方，含我方与 GUEST；非 0 = 敌方）。+0x05 的 0x08 位是玩家操控，+0x04 只是调色板。
    // 2026-10-17 雪地剧情战：敌人调色板全是 1、+0x05 为 94/90/D0/10；我方 08/0B、GUEST 04。
    // 之前按 +0x04 == 3 认敌人，只是前几场敌人恰好用红色调色板 3，这一场一个敌人都没认出来，被判成「未在战斗中」。
    public byte Faction => (byte)((Data[5] >> 4) & 3);
    public BattleSide Side => BattleRules.Classify(this);
    public int NameId => (int)Read(0x1FC, 2);
    // 2026-10-17：丹尼尔（槽 2）盗贼 Lv.1 / Next 2 / JP 38 与游戏面板核对。
    public int JobIndex => Data[3] == 0x01 ? 0 : Data[3] is >= 0x4A and <= 0x5D ? Data[3] - 0x4A : -1;
    public int? JobLevel => JobIndex < 0 ? null : (Data[0xE4 + JobIndex / 2] >> ((1 - JobIndex % 2) * 4)) & 15;
    public uint? CurrentJp => JobIndex < 0 ? null : Read(0xF0 + JobIndex * 2, 2);
    public uint? TotalJp => JobIndex < 0 ? null : Read(0x11E + JobIndex * 2, 2);
    public byte Ct => Data[0x41];
    public byte DeathCountdown => Data[0x07];
    public bool IsDown => (Data[0x61] & 0x20) != 0;
    public uint Hp => Read(0x30, 2);
    public uint MaxHp => Read(0x32, 2);
    public uint Mp => Read(0x34, 2);
    public uint MaxMp => Read(0x36, 2);
    public bool Valid => Data[1] != 0xFF && MaxHp > 0;
    public BattleIdentity Identity => new(Data[1], Faction, Data[0], Data[3], Data[2], NameId);
    public uint Read(int offset, int width) => (uint)Values.Bits(Data.AsSpan(offset),
        width == 1 ? ValueTypeCode.U8 : ValueTypeCode.U16);
    public MemoryEdit Edit(int offset, int width, uint after) =>
        new(Offset + (ulong)offset, width, Read(offset, width), after, BattleIdentity: Identity);
}

public enum BattleSide { Player, Guest, Enemy, Other }

public static class BattleRules
{
    // TARGET 标记：+0x1F8 不为 0。两场剧情战样本（2026-10-17）：
    // 护卫关里阿加斯（友军 TARGET）+0x1F8=0x05；击杀关里敌方骑士（敌方 TARGET）+0x1F8=0x08；
    // 其余单位（包括下一场又变回 GUEST 的阿加斯）都是 0。+0x60 只是第一场的巧合，跟 TARGET 无关。
    public static bool IsTarget(BattleUnit unit) => unit.Side != BattleSide.Other && unit.Data[0x1F8] != 0;

    public static BattleSide Classify(BattleUnit unit) => !unit.Valid ? BattleSide.Other
        : unit.Faction == 1 ? BattleSide.Enemy
        : unit.Faction == 0 ? unit.Data[2] < 50 ? BattleSide.Player : BattleSide.Guest
        : BattleSide.Other;

    // 遭遇战（野外随机战）的「战斗中」标志：战斗全程为 1（打倒最后一个敌人时仍是 1），战斗场景结束后回到 0。
    // 注意：剧情战里它保持 0，不能单独依赖。
    // 2026-10-17 用 3 张模块快照筛出、再用一场野外战斗逐秒监视确认。为 0 时战斗数组可能还残留上一场的数据，一律当作不在战斗中。
    public const ulong InBattleFlag = 0x1853C08;

    public static IReadOnlyList<BattleUnit> Read(IMemorySource source, ulong moduleBase)
    {
        var flag = source.ReadBytes(moduleBase + InBattleFlag, 1)[0] == 1;
        var data = source.ReadBytes(moduleBase + BattleUnit.Start, BattleUnit.Count * BattleUnit.Stride);
        var units = Enumerable.Range(0, BattleUnit.Count).Select(slot => new BattleUnit(slot,
            data.AsSpan(slot * BattleUnit.Stride, BattleUnit.Stride).ToArray()))
            .Where(unit => unit.Valid).ToArray();
        if (flag)
            return units;
        // 剧情战里这个标志是 0（2026-10-17 实测，伊古罗斯城一带的剧情战）：退回到「双方都有活着的单位」。
        // 战斗结束后数组会残留，敌人全倒时这条自然不成立。
        var livingPlayer = units.Any(unit => unit.Side == BattleSide.Player && !unit.IsDown && unit.Hp > 0);
        var livingEnemy = units.Any(unit => unit.Side == BattleSide.Enemy && !unit.IsDown && unit.Hp > 0);
        return livingPlayer && livingEnemy ? units : [];
    }

    // Read 已按游戏战斗标志过滤；这里只要求场上有我方单位（防止标志为 1 但数组尚未初始化的过渡帧）。
    public static bool IsInBattle(IReadOnlyList<BattleUnit> units) =>
        units.Any(unit => unit.Side == BattleSide.Player);

    public static IReadOnlyList<MemoryEdit> Maintain(IReadOnlyList<BattleUnit> units, bool lockHp, bool infiniteMp, bool guestLockHp = false, bool freezeCountdown = false, bool lockCt = false, bool alwaysMove = false, bool alwaysAct = false, bool maxMoveJump = false, bool instantCast = false)
    {
        var edits = new List<MemoryEdit>();
        if (!IsInBattle(units))
            return edits;
        foreach (var unit in units.Where(unit => unit.Side is BattleSide.Player or BattleSide.Guest))
        {
            if ((unit.Side == BattleSide.Player ? lockHp : guestLockHp) && unit.Hp > 0 && unit.Hp < unit.MaxHp)
                edits.Add(unit.Edit(0x30, 2, unit.MaxHp));
            if (freezeCountdown && unit.Side == BattleSide.Player && unit.IsDown
                && unit.DeathCountdown != 0xFF && unit.DeathCountdown < 3)
                edits.Add(unit.Edit(0x07, 1, 3));
            if (lockCt && unit.Side == BattleSide.Player && !unit.IsDown && unit.Hp > 0 && unit.Ct < 100)
                edits.Add(unit.Edit(0x41, 1, 100));
            if (instantCast && unit.Side == BattleSide.Player && !unit.IsDown && unit.Hp > 0
                && (unit.Data[0x61] & 0x08) != 0 && unit.Data[FftLayout.BattleCastTime] is >= 2 and <= 0xFE)
                edits.Add(unit.Edit(FftLayout.BattleCastTime, 1, 1));
            if (maxMoveJump && unit.Side == BattleSide.Player && !unit.IsDown && unit.Hp > 0)
            {
                if (unit.Data[FftLayout.BattleMove] < FftLayout.MaxBattleMove)
                    edits.Add(unit.Edit(FftLayout.BattleMove, 1, FftLayout.MaxBattleMove));
                if (unit.Data[FftLayout.BattleJump] < FftLayout.MaxBattleJump)
                    edits.Add(unit.Edit(FftLayout.BattleJump, 1, FftLayout.MaxBattleJump));
            }
            if (infiniteMp && unit.Side == BattleSide.Player && unit.Mp < unit.MaxMp)
                edits.Add(unit.Edit(0x34, 2, unit.MaxMp));
            // 剧情战实测：仅当前行动单位可清回合标记，其他位必须保留。
            if (unit.Side == BattleSide.Player && !unit.IsDown && unit.Hp > 0 && unit.Data[0x1B8] == 1)
            {
                var flags = unit.Data[0x1B9];
                var after = ClearTurnFlags(1, flags, unit.Data[0x1BA], alwaysMove, alwaysAct).Flags;
                if (after != flags)
                    edits.Add(unit.Edit(0x1B9, 1, after));
                if (alwaysAct && unit.Data[0x1BA] != 0)
                    edits.Add(unit.Edit(0x1BA, 1, 0));
            }
        }
        return edits;
    }

    // Pure bit transformation shared by the fast tick and the 200 ms fallback.
    public static (byte Flags, byte Done) ClearTurnFlags(byte active, byte flags, byte done,
        bool alwaysMove, bool alwaysAct)
    {
        if (active != 1)
            return (flags, done);
        var mask = (alwaysMove ? 1 : 0) | (alwaysAct ? 2 : 0);
        return ((byte)(flags & ~mask), alwaysAct ? (byte)0 : done);
    }

    // Caller holds the session gate and supplies the current battle's cached living players.
    // No full battle-array reads or general-purpose ApplyEdits validation on this hot path.
    public static void TurnTick(IMemorySource source, Action<ulong, byte[]> write, ulong moduleBase,
        IReadOnlyList<BattleUnit> players, bool alwaysMove, bool alwaysAct)
    {
        if (!alwaysMove && !alwaysAct)
            return;
        foreach (var unit in players)
        {
            if (unit.Slot is < 0 or >= BattleUnit.Count || unit.Side != BattleSide.Player
                || unit.IsDown || unit.Hp == 0)
                continue;
            var start = checked(moduleBase + unit.Offset);
            bool Matches()
            {
                var header = source.ReadBytes(start, 5);
                var identity = new BattleIdentity(header[1], header[4], header[0], header[3], header[2],
                    (int)Values.Bits(source.ReadBytes(start + 0x1FC, 2), ValueTypeCode.U16));
                return identity == unit.Identity
                    && Values.Bits(source.ReadBytes(start + 0x30, 2), ValueTypeCode.U16) > 0
                    && (source.ReadBytes(start + 0x61, 1)[0] & 0x20) == 0;
            }
            var before = source.ReadBytes(start + 0x1B8, 3);
            var after = ClearTurnFlags(before[0], before[1], before[2], alwaysMove, alwaysAct);
            void Clear(int index, byte value)
            {
                if (before[index] == value || !Matches())
                    return;
                // Re-read immediately before each single-byte write; never restore stale bits.
                var current = source.ReadBytes(start + 0x1B8, 3);
                if (current[0] != 1 || current[index] != before[index])
                    return;
                write(start + 0x1B8 + (ulong)index, [value]);
            }
            Clear(1, after.Flags);
            Clear(2, after.Done);
        }
    }

    public static IReadOnlyList<MemoryEdit> Weaken(IReadOnlyList<BattleUnit> units) =>
        !IsInBattle(units) ? [] : units.Where(unit => unit.Side == BattleSide.Enemy && unit.Hp > 1)
            .Select(unit => unit.Edit(0x30, 2, 1)).ToArray();

    private static bool ValidJobLevels(BattleUnit unit, int field, uint after)
    {
        for (var half = 0; half < 2; half++)
        {
            var shift = (1 - half) * 4;
            var before = (unit.Data[field] >> shift) & 15;
            var next = (after >> shift) & 15;
            var job = (field - 0xE4) * 2 + half;
            if (next > 8 || next < before
                || !GameData.IsJobAvailable(job, unit.Data[0]) && next != before)
                return false;
        }
        return true;
    }

    public static void Validate(IMemorySource source, ulong moduleBase, MemoryEdit edit)
    {
        var relative = edit.Offset - BattleUnit.Start;
        var slot = (int)(relative / BattleUnit.Stride);
        var field = (int)(relative % BattleUnit.Stride);
        var units = Read(source, moduleBase);
        var unit = units.FirstOrDefault(unit => unit.Slot == slot);
        if (!IsInBattle(units) || unit == null || unit.Identity != edit.BattleIdentity
            || unit.Side == BattleSide.Other)
            throw new IOException(Localizer.T("core.error.battleChanged"));
        var maximum = field switch
        {
            0x1B9 when edit.Width == 1 && unit.Side == BattleSide.Player
                && !unit.IsDown && unit.Hp > 0 && unit.Data[0x1B8] == 1
                && edit.Before == unit.Data[0x1B9]
                && (edit.After & ~3u) == (edit.Before & ~3u)
                && (edit.After & ~edit.Before) == 0 => 255u,
            0x1BA when edit.Width == 1 && unit.Side == BattleSide.Player
                && !unit.IsDown && unit.Hp > 0 && unit.Data[0x1B8] == 1 && edit.After == 0 => 0u,
            FftLayout.BattleCastTime when edit.Width == 1 && edit.After == 1
                && unit.Side == BattleSide.Player && !unit.IsDown && unit.Hp > 0
                && (unit.Data[0x61] & 0x08) != 0
                && unit.Data[FftLayout.BattleCastTime] is >= 2 and <= 0xFE
                && edit.Before == unit.Data[FftLayout.BattleCastTime] => 1u,
            FftLayout.BattleMove when edit.Width == 1 && unit.Side == BattleSide.Player
                && !unit.IsDown && unit.Hp > 0 => (uint)FftLayout.MaxBattleMove,
            FftLayout.BattleJump when edit.Width == 1 && unit.Side == BattleSide.Player
                && !unit.IsDown && unit.Hp > 0 => (uint)FftLayout.MaxBattleJump,
            0x28 when edit.Width == 1 && unit.Side == BattleSide.Player => 99u,
            >= 0xE4 and <= 0xED when edit.Width == 1 && unit.Side == BattleSide.Player
                && ValidJobLevels(unit, field, edit.After) => 0x88u,
            >= 0xF0 and < 0x118 when edit.Width == 2 && (field - 0xF0) % 2 == 0
                && unit.Side == BattleSide.Player && GameData.IsJobAvailable((field - 0xF0) / 2, unit.Data[0]) => 9999u,
            >= 0x11E and < 0x146 when edit.Width == 2 && (field - 0x11E) % 2 == 0
                && unit.Side == BattleSide.Player && GameData.IsJobAvailable((field - 0x11E) / 2, unit.Data[0]) => 9999u,
            0x30 when edit.Width == 2 => unit.IsDown || unit.Hp == 0 ? 0u : unit.MaxHp,
            0x34 when edit.Width == 2 => unit.MaxMp,
            0x2A or 0x2C or 0x41 when edit.Width == 1 => 100u,
            0x07 when edit.Width == 1 && unit.Side == BattleSide.Player && unit.IsDown
                && unit.DeathCountdown != 0xFF && unit.DeathCountdown < 3 && edit.After == 3 => 3u,
            _ => throw new IOException(Localizer.T("core.error.battleField"))
        };
        if (edit.After > maximum)
            throw new IOException(Localizer.T("core.error.battleLimit"));
        if (field == 0x30 && edit.After == 0 && !(unit.IsDown || unit.Hp == 0))
            throw new IOException(Localizer.T("core.error.battleHpZero"));
    }
}

// Baselines include our successful writes, so the next poll only multiplies game rewards.
public sealed class BattleRewards
{
    private readonly Dictionary<int, BattleUnit> baselines = [];
    public int ExperienceMultiplier { get; private set; } = 1;
    public int JpMultiplier { get; private set; } = 1;
    public string? Notice { get; private set; }
    // 「保留战斗设置」打开时，战斗结束 / 断开只清基线，倍率留给下一场
    public bool KeepMultipliers { get; set; }
    private readonly FailureStreak writeFailures = new(5);

    // 运行时诊断：每当检测到我方单位的 JP / 经验有变化，记下这一轮的判断；由 GameSession 写入 rewards.log
    public List<string> Trace { get; } = [];

    public void Configure(int experience, int jp)
    {
        if (experience is not (1 or 2 or 5 or 10) || jp is not (1 or 2 or 5 or 10))
            throw new ArgumentOutOfRangeException(nameof(experience));
        if (experience != ExperienceMultiplier || jp != JpMultiplier)
            baselines.Clear();
        ExperienceMultiplier = experience;
        JpMultiplier = jp;
        Notice = null;
    }

    public void Reset(string? notice = null)
    {
        if (KeepMultipliers && notice == null)
        {
            baselines.Clear();
            return;
        }
        Configure(1, 1);
        baselines.Clear();
        Notice = notice;
    }

    public void Apply(IMemorySource source, Action<ulong, byte[]> write, ulong moduleBase,
        IReadOnlyList<BattleUnit> units)
    {
        // 只用统一的战斗判断：剧情战里遭遇战标志一直是 0，单独检查它会让倍率一设上就被重置（2026-10-17）
        if (!BattleRules.IsInBattle(units))
        {
            if (KeepMultipliers)
            {
                baselines.Clear();
                return;
            }
            var active = ExperienceMultiplier != 1 || JpMultiplier != 1;
            Reset(active ? Localizer.T("core.rewards.battleEnded") : Notice);
            return;
        }
        if (ExperienceMultiplier == 1 && JpMultiplier == 1)
            return;
        var players = units.Where(unit => unit.Side == BattleSide.Player).ToArray();
        foreach (var slot in baselines.Keys.Where(slot => players.All(unit => unit.Slot != slot)).ToArray())
            baselines.Remove(slot);
        foreach (var unit in players)
        {
            var next = new BattleUnit(unit.Slot, unit.Data.ToArray());
            // 等级变了（升级那一帧）只重建基线、不补发；身份里已不含等级，这里要单独判断
            // 升级那一帧：JP 照常放大，只跳过经验（升级时 EXP 被游戏清零，差值没有意义）
            if (!baselines.TryGetValue(unit.Slot, out var previous) || previous.Identity != unit.Identity)
            {
                if (previous != null && (previous.Data[0x28] != unit.Data[0x28]
                    || !previous.Data.AsSpan(0xF0, 40).SequenceEqual(unit.Data.AsSpan(0xF0, 40))
                    || !previous.Data.AsSpan(0x11E, 40).SequenceEqual(unit.Data.AsSpan(0x11E, 40))))
                    Trace.Add($"slot {unit.Slot}: 重建基线 identityChanged={previous.Identity != unit.Identity} level {previous.Data[0x29]}->{unit.Data[0x29]}");
                baselines[unit.Slot] = next;
                continue;
            }
            var changed = previous.Data[0x28] != unit.Data[0x28]
                || !previous.Data.AsSpan(0xF0, 40).SequenceEqual(unit.Data.AsSpan(0xF0, 40))
                || !previous.Data.AsSpan(0x11E, 40).SequenceEqual(unit.Data.AsSpan(0x11E, 40));
            var edits = new List<MemoryEdit>();
            void Add(int field, int width, uint value)
            {
                if (unit.Read(field, width) == value)
                    return;
                edits.Add(unit.Edit(field, width, value));
                var bytes = Values.ToBytes(value, width == 1 ? ValueTypeCode.U8 : ValueTypeCode.U16);
                bytes.CopyTo(next.Data, field);
            }
            for (var job = 0; job < 20 && JpMultiplier > 1; job++)
            {
                if (!GameData.IsJobAvailable(job, unit.Data[0]))
                    continue;
                var current = unit.Read(0xF0 + job * 2, 2);
                var total = unit.Read(0x11E + job * 2, 2);
                var oldCurrent = previous.Read(0xF0 + job * 2, 2);
                var oldTotal = previous.Read(0x11E + job * 2, 2);
                if (current != oldCurrent || total != oldTotal)
                    Trace.Add($"slot {unit.Slot}: job {job} JP {oldCurrent}->{current} total {oldTotal}->{total} multiplier={JpMultiplier}");
                // 消费 / 回退不补；累计未封顶时仍需累计增长，避免把当前 JP 单独恢复当作奖励。
                if (current < oldCurrent || total < oldTotal || (total == oldTotal && total != 9999))
                    continue;
                // 两种 JP 各自封顶：取较大的正差，不能用被 9999 截断的那一项算奖励。
                var gain = Math.Max(current - oldCurrent, total - oldTotal);
                if (gain == 0)
                    continue;
                var field = 0xE4 + job / 2;
                var reward = JobProgress.Grant(current, total, next.Data[field], job,
                    checked((int)gain * (JpMultiplier - 1)));
                Add(0xF0 + job * 2, 2, reward.Current);
                Add(0x11E + job * 2, 2, reward.Total);
                next.Data[field] = reward.LevelByte;
            }
            // One edit per packed byte preserves both jobs when they gain JP in the same frame.
            for (var field = 0xE4; field <= 0xED; field++)
                Add(field, 1, next.Data[field]);
            var experience = unit.Data[0x28];
            if (ExperienceMultiplier > 1 && unit.Data[0x29] == previous.Data[0x29]
                && experience > previous.Data[0x28] && experience < 99)
                Add(0x28, 1, (uint)Math.Min(99,
                    experience + (ExperienceMultiplier - 1) * (experience - previous.Data[0x28])));
            if (changed)
                Trace.Add($"slot {unit.Slot}: EXP {previous.Data[0x28]}->{unit.Data[0x28]} edits={edits.Count} [{string.Join(", ", edits.Select(e => $"+0x{e.Offset - (ulong)unit.Offset:X} {e.Before}->{e.After}"))}]");
            var results = GameSession.ApplyEdits(source, write, moduleBase, edits);
            if (results.FirstOrDefault(result => result.Error != null) is { } failure)
            {
                // 偶发失败多半是读写之间游戏改了值：基线不动，下一轮按新值重算差额；连续失败才复位
                Trace.Add($"slot {unit.Slot}: 写入失败 {failure.Error}");
                if (writeFailures.Register(true))
                    Reset(Localizer.F("core.rewards.repeatedFailure", failure.Error));
                return;
            }
            writeFailures.Register(false);
            baselines[unit.Slot] = next;
        }
    }
}
