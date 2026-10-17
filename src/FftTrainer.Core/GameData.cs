using System.Globalization;
using System.Text.Json;

namespace FftTrainer.Core;

public static class GameData
{
    // JP 索引 20、21 为未使用空槽，不显示、不写入。
    public static bool IsJobHidden(int index) => index is 20 or 21;

    public static bool IsJobAvailable(int index, byte character) => !IsJobHidden(index)
        && (index != 17 || character != 0x81)
        && (index != 18 || character == 0x81);

    // 名称一律来自游戏文本（GameTextCatalog，运行时从玩家本机的游戏封包读取）；这里只剩读不到时的编号兜底
    public static string UnitName(int id) => Localizer.F("core.fallback.unit", id);
    public static int JobIndex(int id) => id == 0x01 ? 0 : id is >= 0x4A and <= 0x5D ? id - 0x4A : -1;
    public static string JobName(int id) => Localizer.F("core.fallback.job", id);
    public static string ItemName(int id) => Localizer.F("core.fallback.item", id);
    // 道具分组代码：bonus（特典）/ consumable（道具）/ equipment（装备），显示文字走语言包 core.item.group.*
    public static string ItemGroupKey(int id) => id is >= 0x101 and <= 0x104 ? "bonus" : id is >= 0xF0 and <= 0xFD ? "consumable" : "equipment";
    public static string ItemCategory(int id) => Localizer.T("core.item.group." + ItemGroupKey(id));

    // 猎物数组每两格一种怪物（普通 / ★稀有），顺序沿用社区 CE 表（xyzjw 2026-06-28），和职业表里怪物的顺序不同。
    // 这里记的是每种怪物对应的怪物职业编号（PSX 编号，0x5E 陆行鸟 … 0x8D 提亚马特），名称从游戏职业表取。
    public static IReadOnlyList<int> PoachingJobIds { get; } = Array.AsReadOnly(new[]
    {
        0x5E, 0x5F, 0x60, 0x61, 0x62, 0x63,
        0x70, 0x71, 0x72, 0x67, 0x68, 0x69,
        0x64, 0x65, 0x66, 0x6D, 0x6E, 0x6F,
        0x76, 0x77, 0x78, 0x7C, 0x7D, 0x7E,
        0x7F, 0x80, 0x81, 0x6A, 0x6B, 0x6C,
        0x73, 0x74, 0x75, 0x82, 0x83, 0x84,
        0x79, 0x7A, 0x7B, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8A, 0x8B, 0x8C, 0x8D,
    });

    private static readonly int[] Thresholds = [0, 200, 400, 700, 1100, 1600, 2200, 3000];

    public static int JobLevel(int totalJp) => Thresholds.Count(value => totalJp >= value);
}

public static class JobProgress
{
    private static readonly int[] Thresholds = [0, 200, 400, 700, 1100, 1600, 2200, 3000];

    public static int Level(int total) => Math.Clamp(Thresholds.Count(value => total >= value), 1, 8);

    public static int ToNextLevel(int total) => Thresholds.FirstOrDefault(value => value > total, total) - total;

    public static (uint Current, uint Total, byte LevelByte) Grant(uint cur, uint total,
        byte levelByte, int job, int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        if (job < 0 || job >= FftLayout.JobCount)
            throw new ArgumentOutOfRangeException(nameof(job));
        var current = (uint)Math.Min(9999L, (long)cur + n);
        var accumulated = (uint)Math.Min(9999L, (long)total + n);
        // 实测：游戏把 0x12 规范成 0x21，所以偶数号职业在高 4 位、奇数号在低 4 位
        var shift = (1 - job % 2) * 4;
        var level = Math.Max((levelByte >> shift) & 15, Level((int)accumulated));
        var packed = (byte)((levelByte & ~(15 << shift)) | (level << shift));
        return (current, accumulated, packed);
    }
}

public sealed record PortraitIdentity(byte CharacterCode, byte JobId, string Gender);
public sealed record PortraitPaths(string? Face, string? JobIllustration);

public sealed class PortraitCatalog
{
    private readonly List<string> roots = [];
    private readonly List<(PortraitIdentity Identity, string Root, string? Face, string? Job)> mappings = [];

    public static PortraitCatalog Load(string executableDirectory, string localAppData,
        Func<string, string>? readText = null)
    {
        readText ??= File.ReadAllText;
        var catalog = new PortraitCatalog();
        foreach (var root in new[] { Path.Combine(executableDirectory, "assets", "portraits"),
            Path.Combine(localAppData, "FftTrainer", "portraits") })
        {
            catalog.roots.Add(root);
            try
            {
                catalog.Parse(readText(Path.Combine(root, "portraits.json")), root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Optional local assets must never prevent startup.
            }
        }
        return catalog;
    }

    private void Parse(string json, string root)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("rosterMappings", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
            return;
        foreach (var entry in entries.EnumerateArray())
        {
            var character = Text(entry, "characterCode");
            var job = Text(entry, "jobId");
            var gender = Text(entry, "gender");
            if (!HexByte(character, out var characterCode) || !HexByte(job, out var jobId)
                || gender is not ("male" or "female" or "monster"))
                continue;
            mappings.Add((new PortraitIdentity(characterCode, jobId, gender), root,
                Text(entry, "face"), Text(entry, "jobIllustration")));
        }
    }

    private static string? Text(JsonElement entry, string key) =>
        entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool HexByte(string? value, out byte result)
    {
        result = 0;
        return value != null && value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && byte.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);
    }

    public PortraitPaths Resolve(PortraitIdentity identity, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        string? face = null;
        string? job = null;
        var exact = false;
        foreach (var entry in mappings.Where(entry => entry.Identity == identity))
        {
            exact = true;
            face ??= AssetPath(entry.Root, entry.Face, identity.Gender == "monster" ? "monster" : "face", exists);
            job ??= AssetPath(entry.Root, entry.Job, "job", exists);
        }
        // 特殊角色默认造型；0x80/0x81 通用兵及怪物仍只使用已核对的映射。
        if (!exact && identity.CharacterCode < 0x80)
            foreach (var root in roots)
                face ??= AssetPath(root, $"face/wldface_{identity.CharacterCode:000}_08_uitx.png", "face", exists);
        return new PortraitPaths(face, job);
    }

    private static string? AssetPath(string root, string? relative, string folder, Func<string, bool> exists)
    {
        if (relative == null)
            return null;
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Length != 2 || parts[0] != folder || parts[1].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !parts[1].EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return null;
        var path = Path.Combine(root, folder, parts[1]);
        return exists(path) ? path : null;
    }
}


// 游戏文本（单位名、职业、道具、技能、技能组）。运行时由 GameTextService 从玩家本机的游戏封包读出当前界面语言的官方译名
// 后整体换入（Load）；测试用 Parse 喂 JSON。读不到的表、编号一律显示本地化的编号兜底。
public sealed class GameTextCatalog
{
    private Dictionary<string, Dictionary<int, TextEntry>> tables = [];
    private Dictionary<int, string> commands = [];
    private sealed record TextEntry(string Name, string? Category);

    public bool IsEmpty => tables.Count == 0;

    // 整体替换（切换语言、换游戏目录时）；null 表示读不到，清空后全部走编号兜底
    public void Load(GameTextData? data)
    {
        var next = new Dictionary<string, Dictionary<int, TextEntry>>();
        var nextCommands = new Dictionary<int, string>();
        if (data != null)
        {
            next["unit-names"] = data.Units.ToDictionary(pair => pair.Key, pair => new TextEntry(pair.Value, null));
            next["jobs"] = data.Jobs.ToDictionary(pair => pair.Key, pair => new TextEntry(pair.Value, null));
            next["abilities"] = data.Abilities.ToDictionary(pair => pair.Key, pair => new TextEntry(pair.Value, null));
            next["items"] = data.Items.ToDictionary(pair => pair.Key,
                pair => new TextEntry(pair.Value, data.ItemCategories.GetValueOrDefault(pair.Key)));
            foreach (var (commandId, name) in data.Commands)
                nextCommands[commandId] = name;
        }
        tables = next;
        commands = nextCommands;
    }

    // 测试数据：与旧版 assets/text/*.json 同格式（id、name，jobs 可带 jobCommandId/jobCommandName，items 可带 categoryName）
    public void Parse(string table, string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return;
        var entries = new Dictionary<int, TextEntry>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var key) || key < 0)
                continue;
            if (table == "jobs" && entry.TryGetProperty("jobCommandId", out var command)
                && command.ValueKind == JsonValueKind.Number && command.TryGetInt32(out var commandId) && Text(entry, "jobCommandName") is { Length: > 0 } commandName)
                commands[commandId] = commandName;
            var name = Text(entry, "name");
            if (!string.IsNullOrWhiteSpace(name))
                entries[key] = new TextEntry(name, Text(entry, "categoryName"));
        }
        tables[table] = entries;
    }

    private static string? Text(JsonElement entry, string key) =>
        entry.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private TextEntry? Entry(string table, int id) => tables.TryGetValue(table, out var entries)
        ? entries.GetValueOrDefault(id) : null;

    public bool HasAbility(int id) => Entry("abilities", id) != null;
    public string AbilityName(int id) => id == 0 ? Localizer.T("core.skill.none")
        : id >= 510 ? Localizer.F("core.fallback.abilitySpecial", id) : Entry("abilities", id)?.Name ?? Localizer.F("core.fallback.ability", id);

    // 名称来自 JobCommand 表，只收职业表引用到的行动技能组合；关联只用于查名，不能决定运行时组 ID。未知则原样显示编号。
    public string SkillGroupName(int id) => id == 0 ? Localizer.T("core.skill.none")
        : commands.GetValueOrDefault(id) ?? Localizer.F("core.fallback.skillGroup", id);

    public string? KnownUnitName(int id) => Entry("unit-names", id)?.Name;
    public string UnitName(int id) => KnownUnitName(id) ?? GameData.UnitName(id);
    public string JobName(int id) => Entry("jobs", id)?.Name ?? GameData.JobName(id);
    public string ItemName(int id) => Entry("items", id)?.Name ?? GameData.ItemName(id);
    public string ItemCategory(int id) => id is >= 0x101 and <= 0x104 ? Localizer.T("core.item.group.bonus")
        : Entry("items", id)?.Category ?? GameData.ItemCategory(id);
    public string ItemGroupKey(int id) => GameData.ItemGroupKey(id);
    public string ItemGroup(int id) => Localizer.T("core.item.group." + ItemGroupKey(id));

    // 猎物：每两格一种怪物，奇数格是稀有（★）；名称取该怪物职业在职业表里的名字
    public string PoachingName(int index)
    {
        if (index < 0 || index >= FftLayout.PoachingCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        var job = GameData.PoachingJobIds[index / 2];
        var name = Entry("jobs", job)?.Name ?? Localizer.F("core.fallback.poaching", index / 2 + 1);
        return name + (index % 2 == 1 ? " ★" : "");
    }
}

// Ordinary roster units, without additional ability multipliers; docs/stats-research.md §§4–7.
public sealed record UnitStatRules(byte[] Growth, byte[] Multipliers, int[] Bonuses, int Move, int Jump);

public static class UnitStats
{
    public const uint RawMaximum = 0xFFFFFF;
    public const long Denominator = 1638400;
    // Raw order is HP, MP, SP, PA, MA.
    public static int Cap(int index) => index < 2 ? 999 : index == 2 ? 50 : 99;

    public static int Display(uint raw, int multiplier, int bonus, int cap) =>
        (int)Math.Min(cap, Math.Clamp((long)raw * multiplier / Denominator, 1, cap) + bonus);

    public static int[] Calculate(IReadOnlyList<uint> raw, UnitStatRules rules) =>
        Enumerable.Range(0, 5).Select(i => Display(raw[i], rules.Multipliers[i], rules.Bonuses[i], Cap(i)))
            .Concat(new[] { rules.Move, rules.Jump }).ToArray();

    public static bool TryInverse(int target, uint oldRaw, int multiplier, int bonus, int cap, out uint raw)
    {
        raw = oldRaw;
        if (target < 1 || target > cap || multiplier < 0 || bonus < 0 || oldRaw > RawMaximum)
            return false;
        if (multiplier == 0)
            return Display(oldRaw, multiplier, bonus, cap) == target;
        var basis = target - bonus;
        if (target < cap && basis < 1)
            return false;
        long Ceiling(long value) => (value + multiplier - 1) / multiplier;
        var low = basis <= 1 ? 0 : Ceiling(basis * Denominator);
        var high = target == cap ? RawMaximum
            : Math.Min(RawMaximum, Ceiling((basis + 1) * Denominator) - 1);
        if (low > high)
            return false;
        var candidate = (uint)Math.Clamp(oldRaw, low, high);
        if (Display(candidate, multiplier, bonus, cap) != target)
            return false;
        raw = candidate;
        return true;
    }

    public static uint[] Grow(IReadOnlyList<uint> raw, int level, int target, IReadOnlyList<byte> growth)
    {
        if (level < 1 || level > 99 || target < level || target > 99 || raw.Count != 5 || growth.Count != 5)
            throw new ArgumentOutOfRangeException(nameof(target));
        var result = raw.ToArray();
        for (; level < target; level++)
            for (var i = 0; i < result.Length; i++)
                result[i] = (uint)Math.Min(RawMaximum,
                    (long)result[i] + result[i] / (level + Math.Max(1, (int)growth[i])));
        return result;
    }
}

public enum SkillSlot
{
    Secondary, Reaction, Support, Movement
}

public sealed record SkillJob(int Index, int JobId, int GroupId, int[] Abilities, int[] Learned);
public sealed record SkillIdentity(byte Character, byte Marker, byte Job, ushort Name)
{
    public static SkillIdentity Read(byte[] unit) => new(unit[0], unit[1], unit[2],
        System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(unit.AsSpan(FftLayout.NameId)));
}

public sealed record UnitSkillRules(int Primary, IReadOnlyList<SkillJob> Jobs,
    IReadOnlyList<int> Secondary, IReadOnlyList<int> Innate, bool Supported)
{
    public IEnumerable<int> Candidates(SkillSlot slot) => new[] { 0 }.Concat(slot == SkillSlot.Secondary
        ? Secondary : Jobs.SelectMany(job => job.Learned).Where(id => Matches(slot, id) && !Innate.Contains(id)))
        .Distinct().Order();

    public static bool Matches(SkillSlot slot, int id) => id == 0 || slot switch
    {
        SkillSlot.Secondary => id is >= 1 and < 176,
        SkillSlot.Reaction => id is >= 0x1A6 and <= 0x1C5,
        SkillSlot.Support => id is >= 0x1C6 and <= 0x1E5,
        SkillSlot.Movement => id is >= 0x1E6 and <= 0x1FD,
        _ => false
    };

    public static int Offset(SkillSlot slot) => slot switch
    {
        SkillSlot.Secondary => FftLayout.SecondarySkill,
        SkillSlot.Reaction => FftLayout.ReactionSkill,
        SkillSlot.Support => FftLayout.SupportSkill,
        SkillSlot.Movement => FftLayout.MovementSkill,
        _ => throw new ArgumentOutOfRangeException(nameof(slot))
    };
}


public sealed class SaveBackups
{
    private const string TimestampFormat = "yyyyMMdd-HHmmss";
    private readonly string steamDirectory;
    private readonly Func<DateTime> clock;
    public string BackupDirectory { get; }
    public DateTime? LastBackup { get; private set; }

    public SaveBackups(string? documents = null, string? localAppData = null, Func<DateTime>? clock = null)
    {
        steamDirectory = Path.Combine(documents ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "My Games", "FINAL FANTASY TACTICS - The Ivalice Chronicles", "Steam");
        BackupDirectory = Path.Combine(localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FftTrainer", "save-backups");
        this.clock = clock ?? (() => DateTime.Now);
        try
        {
            LastBackup = Completed().Select(item => (DateTime?)item.Time).Max();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Backup errors are reported when a write is requested, never during startup.
        }
    }

    private IEnumerable<(string Path, DateTime Time)> Completed() => !Directory.Exists(BackupDirectory)
        ? [] : Directory.EnumerateDirectories(BackupDirectory)
            .Where(path => DateTime.TryParseExact(Path.GetFileName(path), TimestampFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .Select(path => (path, DateTime.ParseExact(Path.GetFileName(path), TimestampFormat,
                CultureInfo.InvariantCulture)));

    public string? EnsureBackup()
    {
        var now = clock();
        if (LastBackup is { } last && now - last <= TimeSpan.FromMinutes(10))
            return null;
        string? pending = null;
        try
        {
            var saves = Directory.GetDirectories(steamDirectory);
            if (saves.Length == 0)
                throw new IOException(Localizer.T("core.backup.noSaves"));
            Directory.CreateDirectory(BackupDirectory);
            var destination = Path.Combine(BackupDirectory, now.ToString(TimestampFormat, CultureInfo.InvariantCulture));
            pending = destination + ".partial-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(pending);
            foreach (var save in saves)
                CopyDirectory(save, Path.Combine(pending, Path.GetFileName(save)));
            Directory.Move(pending, destination);
            pending = null;
            LastBackup = now;
            foreach (var old in Completed().OrderByDescending(item => item.Time).Skip(20))
            {
                CheckLinks(old.Path);
                Directory.Delete(old.Path, true);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Localizer.F("core.backup.failed", ex.Message);
        }
        finally
        {
            if (pending != null)
            {
                try
                {
                    Directory.Delete(pending, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Incomplete copies are never counted as successful backups.
                }
            }
        }
    }

    private static void CheckLinks(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException(Localizer.T("core.backup.noLinks"));
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException(Localizer.T("core.backup.noLinks"));
            if ((attributes & FileAttributes.Directory) != 0)
                CheckLinks(entry);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        CheckLinks(source);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}

// 「保留战斗设置」：开关打开时把战场页的开关和倍率存到 %LOCALAPPDATA%\FftTrainer\battle-options.json，
// 下次启动修改器原样恢复。读写失败一律当作没有设置，不影响启动。
public sealed record BattlePreferences(bool Keep = false, bool LockHp = false, bool InfiniteMp = false,
    bool GuestLockHp = false, bool FreezeCountdown = false, bool LockCt = false, bool AlwaysMove = false,
    bool AlwaysAct = false, bool MaxMoveJump = false, bool InstantCast = false,
    int ExperienceMultiplier = 1, int JpMultiplier = 1)
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FftTrainer", "battle-options.json");

    public static BattlePreferences Load(string path)
    {
        try
        {
            var loaded = System.Text.Json.JsonSerializer.Deserialize<BattlePreferences>(File.ReadAllText(path));
            if (loaded == null || !loaded.Keep)
                return new();
            return loaded with
            {
                ExperienceMultiplier = loaded.ExperienceMultiplier is 1 or 2 or 5 or 10 ? loaded.ExperienceMultiplier : 1,
                JpMultiplier = loaded.JpMultiplier is 1 or 2 or 5 or 10 ? loaded.JpMultiplier : 1,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or NotSupportedException)
        {
            return new();
        }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 存不下就只在本次运行生效
        }
    }
}
