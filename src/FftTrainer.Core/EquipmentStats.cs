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
        const int commonStart = 0x80CE90, weaponStart = 0x80DA90,
            armorStart = 0x80DEB0, bonusStart = 0x80E2A0;
        // The complete common table is required, and each referenced record is
        // bounds-checked below. A truncated image must not produce partial stats.
        if (bytes.Length < commonStart + 256 * 12)
            return null;
        var result = new Dictionary<int, EquipmentStats>();
        for (var id = 1; id < 0xF0; id++)
        {
            var common = bytes.Slice(commonStart + id * 12, 12);
            var type = common[3];
            var weapon = (type & 0x80) != 0;
            var armor = (type & 0x30) != 0;
            // Accessories use 0x08 (boots verified in the supported EXE); shields
            // use 0x40. Both only expose common bonuses, never guessed evasion.
            // Unrecognized or contradictory types do not authorize secondary reads.
            if (weapon && armor || !weapon && !armor && type is not (0x08 or 0x40))
                continue;
            var bonusOffset = bonusStart + common[7] * 26;
            if (bonusOffset + 26 > bytes.Length)
                return null;
            var bonus = bytes.Slice(bonusOffset, 26);
            int? wp = null, evade = null, hp = null, mp = null;
            if (weapon)
            {
                var offset = weaponStart + common[4] * 8;
                // Do not let an invalid index spill into the next table.
                if (offset + 8 > armorStart || offset + 8 > bytes.Length)
                    return null;
                wp = bytes[offset + 4];
                evade = bytes[offset + 5];
            }
            if (armor)
            {
                var offset = armorStart + common[4] * 2;
                if (offset + 2 > bytes.Length)
                    return null;
                hp = bytes[offset];
                mp = bytes[offset + 1];
            }
            result[id] = new(wp, evade, hp, mp, bonus[0], bonus[1], bonus[2], bonus[3], bonus[4]);
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<int, EquipmentStats>(result);
    }
}
