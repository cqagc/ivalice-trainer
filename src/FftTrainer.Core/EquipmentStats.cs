using System.Security.Cryptography;
using FftTrainer.GameFiles;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("FftTrainer.Core.Tests")]

namespace FftTrainer.Core;

// Static item contributions, not character totals or damage. Null means that a
// field is not verified/applicable, not zero. Shield/accessory evasion and effects
// are deliberately absent. Only this immutable in-memory record reaches getters.
public sealed record EquipmentStats(int? WeaponPower, int? WeaponEvade, int? Hp, int? Mp,
    int Pa, int Ma, int Speed, int Move, int Jump)
{
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (WeaponPower is { } wp)
                parts.Add($"{Localizer.T("core.item.stats.wp")} {wp}");
            if (WeaponEvade is { } evade)
                parts.Add($"{Localizer.T("core.item.stats.weaponEvade")} {evade}%");
            void Bonus(string key, int? value)
            {
                if (value is > 0)
                    parts.Add($"{Localizer.T(key)} +{value}");
            }
            Bonus("core.battle.field.hp", Hp);
            Bonus("core.battle.field.mp", Mp);
            Bonus("core.stat.pa", Pa);
            Bonus("core.stat.ma", Ma);
            Bonus("core.stat.speed", Speed);
            Bonus("core.skill.movement", Move);
            Bonus("core.stat.jump", Jump);
            return string.Join(" · ", parts);
        }
    }
}

// No process access, PE heuristics, configurable trusted hash, or public unchecked
// parser. Tests use the internal byte parser; production always hashes the same
// bytes that it parses. Cache success AND failure for the current file identity.
public sealed class EquipmentStatsReader
{
    private const int SupportedLength = 12_158_720;
    private const string SupportedHash = "937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5";
    private IReadOnlyDictionary<int, EquipmentStats>? cached;
    public string? FileIdentity { get; private set; }

    public IReadOnlyDictionary<int, EquipmentStats>? Load(string? gameRoot)
    {
        if (gameRoot == null)
        {
            FileIdentity = null;
            return cached = null;
        }
        string? identity = null;
        try
        {
            var path = Path.GetFullPath(Path.Combine(gameRoot, GameFileReader.ExecutableName));
            var file = new FileInfo(path);
            identity = $"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{file.CreationTimeUtc.Ticks}";
            if (identity == FileIdentity)
                return cached;
            FileIdentity = identity;
            cached = null;
            if (file.Length != SupportedLength)
                return null;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length != SupportedLength || Convert.ToHexString(SHA256.HashData(bytes)) != SupportedHash)
                return null;
            cached = Parse(bytes);
            return cached;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            // Never retain values from an old installation after a failed read.
            cached = null;
            // If metadata was readable, memoize the failed read as well. Metadata
            // failures have no reliable identity and can be retried on the next Load.
            FileIdentity = identity;
            return null;
        }
    }

    internal static IReadOnlyDictionary<int, EquipmentStats>? Parse(ReadOnlySpan<byte> bytes)
    {
        // The complete common table is required, and each referenced record is
        // bounds-checked below. A truncated image must not produce partial stats.
        if (bytes.Length < ItemTables.Common + 256 * 12)
            return null;
        var result = new Dictionary<int, EquipmentStats>();
        foreach (var id in Enumerable.Range(1, 0xEF).Concat(Enumerable.Range(ItemTables.FirstExtended,
            ItemTables.End - ItemTables.FirstExtended)))
        {
            var commonOffset = ItemTables.CommonOffset(id);
            if (commonOffset + 12 > bytes.Length)
                return null;
            var common = bytes.Slice((int)commonOffset, 12);
            // 0x02 is an extra flag on some weapons, armor and accessories (all five
            // enhanced-only items carry it); it does not change the record layout.
            var type = common[3] & ~0x02;
            var weapon = (type & 0x80) != 0;
            var armor = (type & 0x30) != 0;
            // Accessories use 0x08 (boots verified in the supported EXE); shields
            // use 0x40. Both only expose common bonuses, never guessed evasion.
            // Unrecognized or contradictory types do not authorize secondary reads.
            if (weapon && armor || !weapon && !armor && type is not (0x08 or 0x40))
                continue;
            if (common[7] >= ItemTables.BonusCount)
                return null;
            var bonusOffset = ItemTables.Bonus + common[7] * 26;
            if (bonusOffset + 26 > bytes.Length)
                return null;
            var bonus = bytes.Slice((int)bonusOffset, 26);
            int? wp = null, evade = null, hp = null, mp = null;
            if (weapon)
            {
                var offset = ItemTables.WeaponOffset(id, common);
                // Do not let an invalid index spill into the next table.
                if (offset is not { } at || at + 8 > bytes.Length
                    || id < ItemTables.FirstExtended && at + 8 > ItemTables.Armor)
                    return null;
                wp = bytes[(int)at + 4];
                evade = bytes[(int)at + 5];
            }
            if (armor)
            {
                var offset = ItemTables.ArmorOffset(id, common);
                if (offset + 2 > bytes.Length)
                    return null;
                hp = bytes[(int)offset];
                mp = bytes[(int)offset + 1];
            }
            result[id] = new(wp, evade, hp, mp, bonus[0], bonus[1], bonus[2], bonus[3], bonus[4]);
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<int, EquipmentStats>(result);
    }
}

// EXE file offsets (docs/stats-research.md). Item IDs 0x100..0x104 exist only in the
// enhanced build (Akademy Blade/Beret/Tunic, Ring of Aptitude, ...); the game keeps
// them in separate .rdata tables (lookups at RVA 0x2B8C44 / 0x2B8C74 / 0x2B8D3C) and
// ignores IDs from 0x105 upward when it adds equipment stats.
internal static class ItemTables
{
    public const uint Common = 0x80CE90, Weapon = 0x80DA90, Armor = 0x80DEB0, Bonus = 0x80E2A0;
    public const uint ExtendedCommon = 0x67E910, ExtendedWeapon = 0x67F500, ExtendedArmor = 0x6722FC;
    public const int FirstExtended = 0x100, End = 0x105, BonusCount = 0x55;

    // The extended common table is indexed by the full ID, like the main one.
    public static uint CommonOffset(int id) => (id < FirstExtended ? Common : ExtendedCommon) + (uint)id * 12;

    // Main items use the secondary index in common[4]; extended items are indexed by
    // their position in the game's ID ranges (weapons 0x100.., armor 0x102..).
    public static uint? WeaponOffset(int id, ReadOnlySpan<byte> common) => id < FirstExtended
        ? Weapon + common[4] * 8u
        : id < 0x102 ? ExtendedWeapon + (uint)(id - FirstExtended) * 8 : null;

    public static uint ArmorOffset(int id, ReadOnlySpan<byte> common) => id < FirstExtended
        ? Armor + common[4] * 2u
        : id is 0x102 or 0x103 ? ExtendedArmor + (uint)(id - 0x102) * 2
        : throw new InvalidDataException($"Item 0x{id:X} has no armor record");
}
