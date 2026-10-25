using System.Buffers.Binary;

namespace FftTrainer.Core;

public sealed record UnitSnapshot(int Slot, byte[] Data, UnitStatRules? Stats = null, UnitSkillRules? Skills = null);
public sealed record GameSnapshot(long Generation, uint Gil, IReadOnlyList<UnitSnapshot> Units, byte[] Inventory,
    IReadOnlyList<BattleUnit>? Battle = null, bool LockHp = false, bool InfiniteMp = false, string? BattleNotice = null, bool GuestLockHp = false, bool FreezeCountdown = false, bool LockCt = false, int ExperienceMultiplier = 1, int JpMultiplier = 1, byte[]? Poaching = null, bool AlwaysMove = false, bool AlwaysAct = false, bool MaxMoveJump = false, bool InstantCast = false, long OptionsVersion = 0, string? ModulePath = null);
public sealed record MemoryEdit(ulong Offset, int Width, uint Before, uint After, SkillIdentity? SkillIdentity = null,
    BattleIdentity? BattleIdentity = null, SecondarySkillAuthorization? SecondaryHack = null,
    SkillLearningAuthorization? LearningHack = null);

// A revocable, single-selection capability, never persisted. Locking the capability
// serializes revocation with validation and the three mirror writes.
public sealed class SecondarySkillAuthorization
{
    private readonly ulong offset;
    private readonly SkillIdentity identity;
    private readonly uint target;
    private bool active = true;

    internal long Generation { get; }
    internal SecondarySkillAuthorization(ulong offset, SkillIdentity identity, uint target, long generation)
        => (this.offset, this.identity, this.target, Generation) = (offset, identity, target, generation);

    internal void Revoke() { lock (this) active = false; }
    internal bool Allows(MemoryEdit edit) => active && edit.Offset == offset
        && edit.Width == 1 && edit.SkillIdentity == identity && edit.After == target;
}

public sealed record WriteResult(MemoryEdit Edit, string? Error, string? Warning = null);

public interface IGameSession
{
    IReadOnlyList<WriteResult> Write(long generation, IReadOnlyList<MemoryEdit> edits);
}

public interface IBattleSession
{
    void SetBattleOptions(bool lockHp, bool infiniteMp, bool guestLockHp, bool freezeCountdown, bool lockCt, int experienceMultiplier = 1, int jpMultiplier = 1, bool alwaysMove = false, bool alwaysAct = false, bool maxMoveJump = false, bool instantCast = false, long optionsVersion = 0, bool keepOptions = false);
}

public sealed class GameSession : IGameSession, IBattleSession, IDisposable
{
    private readonly object gate = new();
    private readonly FftLayout layout;
    private readonly CancellationTokenSource cancellation = new();
    private GameProcess? process;
    private long generation;
    private long optionsVersion;
    private Task? loop;
    private Task? turnLoop;
    private readonly SemaphoreSlim turnWake = new(0, 1);
    private IReadOnlyList<BattleUnit> turnPlayers = [];
    private bool refreshRequested;
    private bool lockHp;
    private bool infiniteMp;
    private bool guestLockHp;
    private bool freezeCountdown;
    private bool lockCt;
    private bool alwaysMove;
    private bool alwaysAct;
    private bool maxMoveJump;
    private bool instantCast;
    // 「保留战斗设置」：战斗结束、游戏断开时都不自动关闭开关和倍率（不在战斗时 Maintain / 倍率本来就不写内存）
    private bool keepOptions;
    private bool turnWriteFailed;
    private string? battleNotice;
    private readonly BattleRewards rewards = new();
    // 维持写入（锁血、无限 MP 等）读和写之间隔着 200ms，游戏随时可能改掉那个值（挨打、阵亡、切场景），
    // 偶发的「值已改变 / 身份已改变」只是竞态，下一轮重读就好。连续失败才当真，全部关闭。
    // 2026-10-18：开着「保留战斗设置」刷了很久，一次竞态就把开关和倍率全关了。
    private readonly FailureStreak maintainFailures = new(5);

    public void SetBattleOptions(bool hp, bool mp, bool guestHp, bool freeze, bool ct, int experienceMultiplier = 1, int jpMultiplier = 1, bool alwaysMove = false, bool alwaysAct = false, bool maxMoveJump = false, bool instantCast = false, long optionsVersion = 0, bool keepOptions = false)
    {
        lock (gate)
        {
            this.optionsVersion = optionsVersion;
            this.keepOptions = keepOptions;
            rewards.KeepMultipliers = keepOptions;
            lockHp = hp;
            infiniteMp = mp;
            guestLockHp = guestHp;
            freezeCountdown = freeze;
            lockCt = ct;
            this.alwaysMove = alwaysMove;
            this.alwaysAct = alwaysAct;
            this.maxMoveJump = maxMoveJump;
            this.instantCast = instantCast;
            rewards.Configure(experienceMultiplier, jpMultiplier);
            battleNotice = null;
            turnWriteFailed = false;
            WakeTurnLoop();
        }
    }

    public event Action<GameSnapshot?>? SnapshotChanged;

    private static void LogTrace(IEnumerable<string> lines)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FftTrainer", "rewards.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllLines(path, lines.Select(line => $"[{DateTime.Now:HH:mm:ss.fff}] {line}"));
            if (new FileInfo(path).Length > 1024 * 1024)
                File.Move(path, path + ".1", overwrite: true);
        }
        catch
        {
        }
    }

    internal static void LogLine(string line)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FftTrainer", "session.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {line}\n");
        }
        catch
        {
        }
    }

    private static void LogUnexpected(Exception ex)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FftTrainer", "session.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch
        {
            // 日志失败不影响重连
        }
    }

    public GameSession(FftLayout? layout = null)
    {
        this.layout = layout ?? new FftLayout();
        if (this.layout.RosterStride < FftLayout.UnitSize || this.layout.RosterCount < 1)
            throw new ArgumentOutOfRangeException(nameof(layout));
    }

    public void Start()
    {
        lock (gate)
        {
            turnLoop ??= Task.Run(TurnLoopAsync);
            loop ??= Task.Run(PollAsync);
        }
    }

    private bool TurnLoopActive => !cancellation.IsCancellationRequested && process != null
        && (alwaysMove || alwaysAct) && turnPlayers.Count > 0;

    private void WakeTurnLoop()
    {
        if (TurnLoopActive && turnWake.CurrentCount == 0)
            turnWake.Release();
    }

    private async Task TurnLoopAsync()
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await turnWake.WaitAsync(cancellation.Token);
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
                while (await timer.WaitForNextTickAsync(cancellation.Token))
                {
                    lock (gate)
                    {
                        if (!TurnLoopActive)
                            break;
                        try
                        {
                            var currentProcess = process!;
                            BattleRules.TurnTick(currentProcess, currentProcess.WriteBytes, currentProcess.ModuleBase,
                                turnPlayers, alwaysMove, alwaysAct);
                        }
                        catch (Exception ex) when (ex is IOException or InvalidOperationException
                            or System.ComponentModel.Win32Exception)
                        {
                            alwaysMove = alwaysAct = false;
                            turnWriteFailed = true;
                            battleNotice = Localizer.F("core.battle.turnFailed", ex.Message);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private async Task PollAsync()
    {
        GameSnapshot? cached = null;
        var tick = 0;
        while (!cancellation.IsCancellationRequested)
        {
            GameSnapshot? snapshot;
            lock (gate)
            {
                try
                {
                    if (process is { HasExited: true })
                        throw new InvalidOperationException(Localizer.T("core.error.processExited"));
                    if (process == null)
                    {
                        process = new GameProcess();
                        generation++;
                    }
                    if (cached == null || refreshRequested || tick++ % 5 == 0)
                    {
                        cached = ReadSnapshot(process, process.ModuleBase, generation, layout);
                        refreshRequested = false;
                    }
                    var battle = BattleRules.Read(process, process.ModuleBase);
                    turnPlayers = battle.Where(unit => unit.Side == BattleSide.Player
                        && !unit.IsDown && unit.Hp > 0).ToArray();
                    if (!keepOptions && !BattleRules.IsInBattle(battle) && (lockHp || infiniteMp || guestLockHp || freezeCountdown || lockCt || alwaysMove || alwaysAct || maxMoveJump || instantCast))
                    {
                        lockHp = infiniteMp = guestLockHp = freezeCountdown = lockCt = alwaysMove = alwaysAct = maxMoveJump = instantCast = false;
                        battleNotice = Localizer.T("core.battle.endedAutoOff");
                    }
                    var edits = BattleRules.Maintain(battle, lockHp, infiniteMp, guestLockHp, freezeCountdown, lockCt, alwaysMove, alwaysAct, maxMoveJump, instantCast);
                    var failure = edits.Count == 0 ? null
                        : ApplyEdits(process, process.WriteBytes, process.ModuleBase, edits, layout).FirstOrDefault(result => result.Error != null);
                    if (failure != null)
                        LogLine($"维持写入失败（连续第 {maintainFailures.Count + 1} 次）：{failure.Edit.Offset:X} {failure.Error}");
                    if (maintainFailures.Register(failure != null))
                    {
                        lockHp = infiniteMp = guestLockHp = freezeCountdown = lockCt = alwaysMove = alwaysAct = maxMoveJump = instantCast = false;
                        // 倍率的提示会盖掉 battleNotice，所以原因写在这一条里
                        rewards.Reset(Localizer.F("core.battle.maintainFailed", failure!.Error));
                    }
                    WakeTurnLoop();
                    rewards.Apply(process, process.WriteBytes, process.ModuleBase, battle);
                    if (rewards.Trace.Count > 0)
                    {
                        LogTrace(rewards.Trace);
                        rewards.Trace.Clear();
                    }
                    if (!turnWriteFailed)
                        battleNotice = rewards.Notice ?? battleNotice;
                    snapshot = cached with { Battle = battle, LockHp = lockHp, InfiniteMp = infiniteMp, GuestLockHp = guestLockHp, FreezeCountdown = freezeCountdown, LockCt = lockCt, AlwaysMove = alwaysMove, AlwaysAct = alwaysAct, MaxMoveJump = maxMoveJump, InstantCast = instantCast, ExperienceMultiplier = rewards.ExperienceMultiplier, JpMultiplier = rewards.JpMultiplier, BattleNotice = battleNotice, OptionsVersion = optionsVersion, ModulePath = process.ModulePath };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 任何异常都当作断开，下一轮重新连接；轮询循环绝不能因为意外异常停掉
                    if (ex is not (IOException or InvalidOperationException or System.ComponentModel.Win32Exception))
                        LogUnexpected(ex);
                    process?.Dispose();
                    process = null;
                    turnPlayers = [];
                    snapshot = null;
                    cached = null;
                    rewards.Reset();
                    turnWriteFailed = false;
                    if (!keepOptions)
                        lockHp = infiniteMp = guestLockHp = freezeCountdown = lockCt = alwaysMove = alwaysAct = maxMoveJump = instantCast = false;
                }
            }
            SnapshotChanged?.Invoke(snapshot);
            try
            {
                await Task.Delay(snapshot == null ? 2000 : 200, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public static GameSnapshot ReadSnapshot(IMemorySource source, ulong moduleBase, long generation,
        FftLayout layout)
    {
        var gil = BinaryPrimitives.ReadUInt32LittleEndian(source.ReadBytes(moduleBase + FftLayout.Gil, 4));
        var units = new List<UnitSnapshot>();
        var tables = StatTables.TryLoad(source, moduleBase);
        var skills = SkillTables.TryLoad(source, moduleBase);
        for (var slot = 0; slot < layout.RosterCount; slot++)
        {
            var data = source.ReadBytes(checked(moduleBase + FftLayout.Roster
                + (ulong)(slot * layout.RosterStride)), FftLayout.UnitSize);
            if (data[FftLayout.SlotMarker] != 0xFF)
                units.Add(new UnitSnapshot(slot, data, tables?.ReadUnit(data), skills?.ReadUnit(data)));
        }
        return new GameSnapshot(generation, gil, units,
            source.ReadBytes(moduleBase + FftLayout.Inventory, FftLayout.InventoryCount),
            BattleRules.Read(source, moduleBase),
            Poaching: source.ReadBytes(moduleBase + FftLayout.Poaching, FftLayout.PoachingCount));
    }

    public IReadOnlyList<WriteResult> Write(long expectedGeneration, IReadOnlyList<MemoryEdit> edits)
    {
        lock (gate)
        {
            IReadOnlyList<WriteResult> Failed(string error)
            {
                rewards.Reset(Localizer.T("core.rewards.writeFailedReset"));
                return edits.Select(edit => new WriteResult(edit, error)).ToArray();
            }
            try
            {
                if (process == null || generation != expectedGeneration)
                    return Failed(Localizer.T("core.error.connectionChanged"));
                if (edits.Any(edit => edit.SkillIdentity != null))
                {
                    using var executable = File.OpenRead(process.ModulePath);
                    if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(executable))
                        != "937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5")
                        return Failed(Localizer.T("core.error.versionUnverified"));
                }
                refreshRequested = true;
                var results = ApplyEdits(process, process.WriteBytes, process.ModuleBase, edits, layout, expectedGeneration);
                if (results.Any(result => result.Error != null))
                    rewards.Reset(Localizer.T("core.rewards.writeFailedReset"));
                return results;
            }
            catch
            {
                rewards.Reset(Localizer.T("core.rewards.writeFailedReset"));
                throw;
            }
        }
    }

    // Each field is verified independently; battle writes never target roster mirrors.
    public static IReadOnlyList<WriteResult> ApplyEdits(IMemorySource source,
        Action<ulong, byte[]> write, ulong moduleBase, IReadOnlyList<MemoryEdit> edits, FftLayout? layout = null,
        long? generation = null)
    {
        layout ??= new FftLayout();
        var results = new List<WriteResult>();
        results.AddRange(SkillLearningWrites.Apply(source, write, moduleBase, edits, layout, generation));
        foreach (var edit in edits.Where(edit => edit.LearningHack == null))
        {
            lock (edit.SecondaryHack ?? new object())
            {
                string? warning = null;
                try
                {
                    if (SkillLearningWrites.TouchesLearned(edit, layout))
                        throw new IOException(Localizer.T("core.learn.invalid"));
                    if (edit.SecondaryHack is { } hack && (!hack.Allows(edit) || hack.Generation != generation
                        || edit.Offset < FftLayout.Roster
                        || edit.Offset - FftLayout.Roster >= (ulong)layout.RosterCount * (ulong)layout.RosterStride
                        || (edit.Offset - FftLayout.Roster) % (ulong)layout.RosterStride != FftLayout.SecondarySkill))
                        throw new IOException(Localizer.T("core.error.secondaryHack"));
                    // Inventory and poaching edits are single-byte quantities; reject gaps before any read.
                    var end = checked(edit.Offset + (ulong)edit.Width);
                    if (edit.Offset < FftLayout.Roster && end >= FftLayout.Poaching)
                    {
                        var inventory = edit.Offset >= FftLayout.Inventory && edit.Offset <= FftLayout.InventoryLast;
                        var poaching = edit.Offset >= FftLayout.Poaching
                            && edit.Offset < FftLayout.Poaching + FftLayout.PoachingCount;
                        if ((!inventory && !poaching) || edit.Width != 1 || edit.After > 99)
                            throw new IOException(Localizer.T("core.error.inventoryRange"));
                    }
                    var isBattle = edit.Offset >= BattleUnit.Start
                        && edit.Offset < BattleUnit.Start + BattleUnit.Count * BattleUnit.Stride;
                    if (isBattle)
                        BattleRules.Validate(source, moduleBase, edit);
                    else if (edit.BattleIdentity != null)
                        throw new IOException(Localizer.T("core.error.battleAddress"));
                    var address = checked(moduleBase + edit.Offset);
                    var type = edit.Width switch
                    {
                        1 => ValueTypeCode.U8,
                        2 => ValueTypeCode.U16,
                        3 => ValueTypeCode.U24,
                        4 => ValueTypeCode.U32,
                        _ => throw new ArgumentOutOfRangeException(nameof(edits))
                    };
                    if (edit.Width == 3 && (edit.After > UnitStats.RawMaximum
                        || edit.Offset < FftLayout.Roster
                        || edit.Offset - FftLayout.Roster >= (ulong)layout.RosterCount * (ulong)layout.RosterStride
                        || (edit.Offset - FftLayout.Roster) % (ulong)layout.RosterStride is var rawOffset
                            && (rawOffset < FftLayout.RawStats || rawOffset > FftLayout.RawStats + 12
                                || (rawOffset - FftLayout.RawStats) % 3 != 0)))
                        throw new IOException(Localizer.T("core.error.rawStatRange"));
                    if (Values.Bits(source.ReadBytes(address, edit.Width), type) != edit.Before)
                        throw new IOException(Localizer.T("core.error.valueChanged"));
                    var addresses = new List<ulong> { address };
                    if (edit.Offset >= FftLayout.Roster
                        && edit.Offset - FftLayout.Roster < (ulong)layout.RosterCount * (ulong)layout.RosterStride)
                    {
                        var relative = edit.Offset - FftLayout.Roster;
                        var fieldOffset = relative % (ulong)layout.RosterStride;
                        SkillSlot? skillSlot = fieldOffset switch
                        {
                            FftLayout.SecondarySkill => SkillSlot.Secondary,
                            FftLayout.ReactionSkill => SkillSlot.Reaction,
                            FftLayout.SupportSkill => SkillSlot.Support,
                            FftLayout.MovementSkill => SkillSlot.Movement,
                            _ => null
                        };
                        var isSkill = fieldOffset >= FftLayout.SecondarySkill && fieldOffset < FftLayout.Equipment;
                        if (isSkill)
                        {
                            if (skillSlot == null || edit.Width != (skillSlot == SkillSlot.Secondary ? 1 : 2)
                                || !UnitSkillRules.Matches(skillSlot.Value, (int)edit.Before)
                                || !UnitSkillRules.Matches(skillSlot.Value, (int)edit.After))
                                throw new IOException(Localizer.T("core.error.skillSlotInvalid"));
                            var unit = source.ReadBytes(moduleBase + FftLayout.Roster + relative - fieldOffset, FftLayout.UnitSize);
                            if (edit.SkillIdentity == null || SkillIdentity.Read(unit) != edit.SkillIdentity)
                                throw new IOException(Localizer.T("core.error.unitChanged"));
                            var rules = SkillTables.TryLoad(source, moduleBase)?.ReadUnit(unit);
                            if (rules is not { Supported: true }
                                || !(edit.SecondaryHack != null && skillSlot == SkillSlot.Secondary
                                    ? rules.HackSecondary.Contains((int)edit.After)
                                    : rules.Candidates(skillSlot.Value).Contains((int)edit.After)))
                                throw new IOException(Localizer.T("core.error.skillUnavailable"));
                        }
                        var isRosterField = isSkill || edit.Width == 1
                            && (fieldOffset is FftLayout.Level or FftLayout.Experience or FftLayout.Brave or FftLayout.Faith
                                || fieldOffset >= FftLayout.JobLevels && fieldOffset <= FftLayout.JobLevels + 10)
                            || edit.Width == 2
                            && (fieldOffset >= FftLayout.CurrentJp
                                && fieldOffset < FftLayout.CurrentJp + FftLayout.JobCount * 2
                                && (fieldOffset - FftLayout.CurrentJp) % 2 == 0
                                || fieldOffset >= FftLayout.TotalJp
                                && fieldOffset < FftLayout.TotalJp + FftLayout.JobCount * 2
                                && (fieldOffset - FftLayout.TotalJp) % 2 == 0)
                            || edit.Width == 3 && fieldOffset >= FftLayout.RawStats
                                && fieldOffset <= FftLayout.RawStats + 12
                                && (fieldOffset - FftLayout.RawStats) % 3 == 0;
                        if (isRosterField)
                        {
                            var slotOffset = relative - fieldOffset;
                            var primary = checked(moduleBase + FftLayout.Roster + slotOffset);
                            var marker = source.ReadBytes(primary + FftLayout.SlotMarker, 1);
                            var name = source.ReadBytes(primary + FftLayout.NameId, 2);
                            foreach (var mirror in FftLayout.RosterMirrors)
                            {
                                var start = checked(moduleBase + mirror + slotOffset);
                                if (!source.ReadBytes(start + FftLayout.SlotMarker, 1).SequenceEqual(marker)
                                    || !source.ReadBytes(start + FftLayout.NameId, 2).SequenceEqual(name))
                                    warning = Localizer.T("core.warning.mirrorMismatch");
                                if (isSkill && (SkillIdentity.Read(source.ReadBytes(start, FftLayout.UnitSize)) != edit.SkillIdentity
                                    || Values.Bits(source.ReadBytes(start + fieldOffset, edit.Width), type) != edit.Before))
                                    throw new IOException(Localizer.T("core.error.skillMirror"));
                                addresses.Add(checked(start + fieldOffset));
                            }
                            if (warning != null)
                                addresses.RemoveRange(1, addresses.Count - 1);
                        }
                    }
                    var bytes = Values.ToBytes(edit.After, type);
                    foreach (var target in addresses)
                        write(target, bytes);
                    var errors = new List<string>();
                    foreach (var target in addresses)
                    {
                        try
                        {
                            if (Values.Bits(source.ReadBytes(target, edit.Width), type) != edit.After)
                                errors.Add(Localizer.F("core.error.readbackMismatch", target - moduleBase));
                        }
                        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
                        {
                            errors.Add(Localizer.F("core.error.readbackFailed", target - moduleBase, ex.Message));
                        }
                    }
                    results.Add(new WriteResult(edit, errors.Count == 0 ? null : string.Join("；", errors), warning));
                }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
                {
                    results.Add(new WriteResult(edit, ex.Message, warning));
                }
            }
        }
        return results;
    }

    public void Dispose()
    {
        cancellation.Cancel();
        lock (gate)
        {
            process?.Dispose();
            process = null;
            turnPlayers = [];
        }
    }
}

public sealed class PeFileMap
{
    private readonly (uint File, uint Size, uint Rva)[] sections;

    private PeFileMap((uint File, uint Size, uint Rva)[] sections) => this.sections = sections;

    public static PeFileMap Read(IMemorySource source, ulong moduleBase)
    {
        var dos = source.ReadBytes(moduleBase, 64);
        if (BinaryPrimitives.ReadUInt16LittleEndian(dos) != 0x5A4D)
            throw new InvalidDataException("DOS header missing");
        var peOffset = BinaryPrimitives.ReadUInt32LittleEndian(dos.AsSpan(0x3C));
        if (peOffset < 64 || peOffset > 0x100000)
            throw new InvalidDataException("Invalid PE header offset");
        var pe = source.ReadBytes(checked(moduleBase + peOffset), 24);
        if (BinaryPrimitives.ReadUInt32LittleEndian(pe) != 0x4550)
            throw new InvalidDataException("PE signature missing");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(6));
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(20));
        if (count is < 1 or > 96)
            throw new InvalidDataException("Invalid PE section count");
        var bytes = source.ReadBytes(checked(moduleBase + peOffset + 24u + optionalSize), count * 40);
        var sections = new (uint File, uint Size, uint Rva)[count];
        for (var i = 0; i < count; i++)
        {
            var section = bytes.AsSpan(i * 40, 40);
            sections[i] = (BinaryPrimitives.ReadUInt32LittleEndian(section[20..]),
                BinaryPrimitives.ReadUInt32LittleEndian(section[16..]),
                BinaryPrimitives.ReadUInt32LittleEndian(section[12..]));
        }
        return new PeFileMap(sections);
    }

    public ulong ToRva(uint fileOffset, int length)
    {
        if (length < 1)
            throw new InvalidDataException("Invalid table length");
        foreach (var section in sections)
            if (fileOffset >= section.File && (ulong)fileOffset + (uint)length <= (ulong)section.File + section.Size)
                return (ulong)section.Rva + fileOffset - section.File;
        throw new InvalidDataException("File offset is outside PE raw sections");
    }
}

public sealed class StatTables
{
    private readonly IMemorySource source;
    private readonly ulong moduleBase;
    private readonly PeFileMap map;
    private readonly Dictionary<(uint Offset, int Length), byte[]> records = [];

    private StatTables(IMemorySource source, ulong moduleBase, PeFileMap map)
    {
        this.source = source;
        this.moduleBase = moduleBase;
        this.map = map;
    }

    public static StatTables? TryLoad(IMemorySource source, ulong moduleBase)
    {
        try
        {
            return new StatTables(source, moduleBase, PeFileMap.Read(source, moduleBase));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.ComponentModel.Win32Exception or OverflowException)
        {
            return null;
        }
    }

    private byte[] Read(uint fileOffset, int length)
    {
        var key = (fileOffset, length);
        if (!records.TryGetValue(key, out var bytes))
        {
            bytes = source.ReadBytes(checked(moduleBase + map.ToRva(fileOffset, length)), length);
            if (bytes.Length != length)
                throw new IOException("Truncated stat table");
            records[key] = bytes;
        }
        return bytes;
    }

    public UnitStatRules? ReadUnit(byte[] unit)
    {
        try
        {
            var job = Read(0x784230u + unit[FftLayout.Job] * 0x31u, 0x31);
            var growth = Enumerable.Range(0, 5).Select(i => job[0x0E + i * 2]).ToArray();
            var multipliers = Enumerable.Range(0, 5).Select(i => job[0x0F + i * 2]).ToArray();
            var bonuses = new int[5];
            var move = (int)job[0x18];
            var jump = job[0x19] & 0x7F;
            for (var i = 0; i < 7; i++)
            {
                var id = BinaryPrimitives.ReadUInt16LittleEndian(unit.AsSpan(FftLayout.Equipment + i * 2));
                if (id == 0xFF)
                    continue;
                if (id > 0xFF)
                    return null;
                var common = Read(0x80CE90u + id * 12u, 12);
                var bonus = Read(0x80E2A0u + common[7] * 26u, 26);
                bonuses[3] += bonus[0];
                bonuses[4] += bonus[1];
                bonuses[2] += bonus[2];
                move += bonus[3];
                jump += bonus[4];
                if ((common[3] & 0x30) != 0)
                {
                    var armor = Read(0x80DEB0u + common[4] * 2u, 2);
                    bonuses[0] += armor[0];
                    bonuses[1] += armor[1];
                }
            }
            return new UnitStatRules(growth, multipliers, bonuses, move, Math.Min(7, jump));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.ComponentModel.Win32Exception or OverflowException)
        {
            return null;
        }
    }
}

// docs/skills-research.md：普通组 176 × 25，位图及第九位均按高位优先。
public sealed class SkillTables
{
    private readonly byte[] jobs;
    private readonly byte[] commands;
    private readonly int[] verifiedHumanCommands;
    private readonly string tableFingerprint;

    private SkillTables(byte[] jobs, byte[] commands)
    {
        this.jobs = jobs;
        this.commands = commands;
        tableFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([.. jobs, .. commands]));
        // Ordinary player jobs 0x4A..0x5C map to groups 5..23; Mime has no
        // actions. Special groups use the independently verified base map.
        // A live job mapping and valid action are required, not just a name.
        verifiedHumanCommands = Enumerable.Range(0, 0x5D).Select(job =>
            (Job: job, Group: job >= 0x4A ? job - 0x4A + 5 : VerifiedBaseGroup(job)))
            .Where(item => item.Group > 0 && jobs[item.Job * 49] == item.Group
                && Abilities(item.Group).Take(16).Any(id => id is > 0 and < 0x1A6))
            .Select(item => item.Group).Prepend(0).Distinct().Order().ToArray();
    }

    public static SkillTables? TryLoad(IMemorySource source, ulong moduleBase)
    {
        try
        {
            var map = PeFileMap.Read(source, moduleBase);
            var jobsRva = map.ToRva(0x784230, 174 * 49);
            var commandsRva = map.ToRva(0x67D210, 176 * 25);
            if (jobsRva != 0x785E30 || commandsRva != 0x67E210)
                return null;
            var jobs = source.ReadBytes(moduleBase + jobsRva, 174 * 49);
            var commands = source.ReadBytes(moduleBase + commandsRva, 176 * 25);
            return jobs.Length == 174 * 49 && commands.Length == 176 * 25
                ? new SkillTables(jobs, commands) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.ComponentModel.Win32Exception or OverflowException)
        {
            return null;
        }
    }

    public static bool Learned(byte[] bytes, int start, int position) =>
        (bytes[start + position / 8] & (0x80 >> (position % 8))) != 0;

    private int[] Abilities(int group) => Enumerable.Range(0, 22).Select(position =>
        commands[group * 25 + 3 + position] + (Learned(commands, group * 25, position) ? 256 : 0)).ToArray();

    // Verified build 24304444 (SHA256 937233f7...6b5d5), read-only check 2026-10-26:
    // RVA 0x2864AC uses the base character as the k=0 job (128/129 -> Squire);
    // 0x28682D -> 0x2B8F18 maps special jobs to learned bitmap index 0.
    // These nine identities were also present in the live roster after changing to
    // ordinary jobs. Values below are JOB_DATA[base * 49].JobCommandId, not names
    // or the equipped secondary value. Other story identities remain read-only.
    private static int VerifiedBaseGroup(int identity) => identity switch
    {
        1 => 25,                 // Ramza (previously verified story stage)
        128 or 129 => 5,          // Generic male/female -> job 0x4A
        0x0D => 74,               // Orlandeau -> Swordplay
        0x0F => 43,               // Reis -> Dragon (not monster Breath, 44)
        0x16 => 38,               // Mustadio -> Aimed Shot (not NPC groups 37/68)
        0x1A => 46,               // Marach -> Nether Mantra
        0x1E => 40,               // Agrias -> Holy Sword
        0x1F => 69,               // Beowulf -> Spellblade
        0x29 => 50,               // Rapha -> Sky Mantra
        0x2A => 67,               // Meliadoul -> Unyielding Blade
        0x32 => 41,               // Cloud -> Limit
        _ => -1
    };

    public UnitSkillRules? ReadUnit(byte[] unit)
    {
        if (unit.Length < FftLayout.UnitSize || unit[FftLayout.Job] >= 174)
            return null;
        var currentJob = unit[FftLayout.Job];
        var primary = jobs[currentJob * 49];
        var innate = new[] { 1, 3, 5, 7 }.Select(offset =>
            (int)BinaryPrimitives.ReadUInt16LittleEndian(jobs.AsSpan(currentJob * 49 + offset))).ToArray();
        // k=0 belongs to this character's base job even after changing jobs.
        // Never reinterpret another character's k=0 as Dragon/Aimed Shot, nor
        // infer learning from an already-equipped special command.
        var baseJob = unit[0] is 128 or 129 ? 0x4A : unit[0];
        var expectedBaseGroup = VerifiedBaseGroup(unit[0]);
        var supported = expectedBaseGroup > 0 && baseJob < 174
            && jobs[baseJob * 49] == expectedBaseGroup
            && (unit[4] & 0x20) == 0 // Monster flag: no human skill editing.
            && (currentJob is >= 0x4A and <= 0x5D || currentJob == baseJob)
            && primary is > 0 and < 176;
        var learnedJobs = new List<SkillJob>();
        var secondary = new List<int>();
        if (!supported)
            return new UnitSkillRules(primary, learnedJobs, secondary, innate, false);
        for (var index = 0; index < 20; index++)
        {
            var job = index == 0 ? baseJob : 0x4A + index;
            var group = jobs[job * 49];
            if (group >= 176)
                return new UnitSkillRules(primary, [], [], innate, false);
            var abilities = Abilities(group);
            var learned = abilities.Where((id, position) => id is > 0 and < 510
                && Learned(unit, FftLayout.LearnedSkills + index * 3, position)).ToArray();
            learnedJobs.Add(new SkillJob(index, job, group, abilities.Where(id => id is > 0 and < 510).ToArray(), learned));
            var genderAllowed = !(job == 0x5B && (unit[4] & 0x40) != 0
                || job == 0x5C && (unit[4] & 0x80) != 0);
            if (group != 0 && group != primary && job != 0x5D && genderAllowed
                && Learned(unit, FftLayout.AvailableJobs, index)
                && abilities.Take(16).Where((id, position) => id is > 0 and < 0x1A6
                    && Learned(unit, FftLayout.LearnedSkills + index * 3, position)).Any())
                secondary.Add(group);
        }
        return new UnitSkillRules(primary, learnedJobs, secondary, innate, true)
            { HackSecondary = verifiedHumanCommands.Where(group => group != primary).ToArray(),
                Learning = ReadLearning(unit, primary) };
    }

    private SkillLearningPlan? ReadLearning(byte[] unit, int primary)
    {
        var group = unit[FftLayout.SecondarySkill];
        if (group == 0 || group == primary || !verifiedHumanCommands.Contains(group)) return null;
        var index = group is >= 5 and <= 23 ? group - 5 : 0;
        var offset = FftLayout.LearnedSkills + index * 3;
        var actions = new List<LearningAction>();
        ushort mask = 0;
        var abilities = Abilities(group);
        for (var position = 0; position < 16; position++)
        {
            var id = abilities[position]; // Includes low byte 0 + ninth bit = 256.
            if (id == 0) continue;
            if (id >= 0x1A6) return null;
            mask |= (ushort)((0x80 >> (position % 8)) << (8 * (position / 8)));
            actions.Add(new LearningAction(position, id, Learned(unit, offset, position)));
        }
        return actions.Count == 0 ? null : new SkillLearningPlan(group, index,
            BinaryPrimitives.ReadUInt16LittleEndian(unit.AsSpan(offset)), unit[offset + 2], mask, tableFingerprint, actions);
    }
}

// 连续失败计数：Register(true) 累加，Register(false) 清零；连续达到阈值的那一次返回 true 并清零
public sealed class FailureStreak(int threshold)
{
    public int Count { get; private set; }

    public bool Register(bool failed)
    {
        if (!failed)
        {
            Count = 0;
            return false;
        }
        if (++Count < threshold)
            return false;
        Count = 0;
        return true;
    }
}
