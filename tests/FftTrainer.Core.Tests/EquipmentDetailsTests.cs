using System.Text.Json;
using System.Text.Json.Nodes;
using FftTrainer.Core;
using FftTrainer.GameFiles;
using Xunit;

namespace FftTrainer.Core.Tests;

[Collection("Localizer")]
public sealed class EquipmentDetailsTests
{
    // Independent fixtures/expectations from stats-research.md, not production
    // constants or an implementation-derived expected-value generator.
    private static byte[] Image()
    {
        var bytes = new byte[12_158_720];
        foreach (var (id, type, secondary, bonus) in new[]
        {
            (1, 0x80, 1, 0), (2, 0x80, 2, 0), (19, 0x80, 19, 0),
            (157, 0x20, 13, 0), (186, 0x10, 42, 0), (208, 0x08, 0, 42)
        })
        {
            bytes[0x80CE90 + id * 12 + 3] = (byte)type;
            bytes[0x80CE90 + id * 12 + 4] = (byte)secondary;
            bytes[0x80CE90 + id * 12 + 7] = (byte)bonus;
        }
        bytes[0x80DA90 + 1 * 8 + 4] = 3;
        bytes[0x80DA90 + 1 * 8 + 5] = 5;
        bytes[0x80DA90 + 2 * 8 + 4] = 4;
        bytes[0x80DA90 + 2 * 8 + 5] = 5;
        bytes[0x80DA90 + 19 * 8 + 4] = 4;
        bytes[0x80DA90 + 19 * 8 + 5] = 5;
        bytes[0x80DEB0 + 13 * 2] = 8;
        bytes[0x80DEB0 + 42 * 2] = 5;
        bytes[0x80E2A0 + 42 * 26 + 3] = 1;
        return bytes;
    }

    [Theory]
    [InlineData(1, 3, 5, null, null, 0)]
    [InlineData(2, 4, 5, null, null, 0)]
    [InlineData(19, 4, 5, null, null, 0)]
    [InlineData(157, null, null, 8, 0, 0)]
    [InlineData(186, null, null, 5, 0, 0)]
    [InlineData(208, null, null, null, null, 1)]
    public void SixResearchSamples(int id, int? wp, int? evade, int? hp, int? mp, int move)
    {
        var table = EquipmentStatsReader.Parse(Image());
        Assert.NotNull(table);
        Assert.Equal(new EquipmentStats(wp, evade, hp, mp, 0, 0, 0, move, 0), table[id]);
    }

    [Fact]
    public void ReadsTablesNotJustSixSamplesAndDoesNotConfuseWeaponPowerWithPa()
    {
        var bytes = Image();
        bytes[0x80CE90 + 7 * 12 + 3] = 0x80;
        bytes[0x80CE90 + 7 * 12 + 4] = 6;
        bytes[0x80CE90 + 7 * 12 + 7] = 50;
        bytes[0x80DA90 + 6 * 8 + 4] = 17;
        bytes[0x80DA90 + 6 * 8 + 5] = 12;
        new byte[] { 2, 3, 4, 5, 6 }.CopyTo(bytes, 0x80E2A0 + 50 * 26);
        Assert.Equal(new EquipmentStats(17, 12, null, null, 2, 3, 4, 5, 6), EquipmentStatsReader.Parse(bytes)![7]);
    }

    [Fact]
    public void ExcludesReservedConsumablesBonusIdsAndUnknownTypes()
    {
        var bytes = Image();
        foreach (var id in new[] { 0, 0xF0, 0xFD, 0xFF })
        {
            bytes[0x80CE90 + id * 12 + 3] = 0x80;
            bytes[0x80CE90 + id * 12 + 4] = 255; // would overrun weapon table if followed
        }
        bytes[0x80CE90 + 3 * 12 + 3] = 1;
        bytes[0x80CE90 + 3 * 12 + 4] = 255;
        bytes[0x80CE90 + 4 * 12 + 3] = 0xB0; // contradictory type
        var table = EquipmentStatsReader.Parse(bytes)!;
        foreach (var id in new[] { 0, 3, 4, 0xF0, 0xFD, 0xFF, 0x101, 0x104 })
            Assert.False(table.ContainsKey(id));
        // No weapon record for boots or shield, even when secondary points at a weapon.
        bytes[0x80CE90 + 5 * 12 + 3] = 0x40;
        bytes[0x80CE90 + 5 * 12 + 4] = 19;
        table = EquipmentStatsReader.Parse(bytes)!;
        Assert.Null(table[5].WeaponPower);
        Assert.Null(table[5].WeaponEvade);
        Assert.Null(table[208].WeaponPower);
    }

    [Fact]
    public void TruncationAndInvalidSecondaryIndexDoNotYieldPartialTables()
    {
        var bytes = Image();
        Assert.Null(EquipmentStatsReader.Parse([]));
        Assert.Null(EquipmentStatsReader.Parse(bytes.AsSpan(0, 0x80CE90 + 255 * 12)));
        Assert.Null(EquipmentStatsReader.Parse(bytes.AsSpan(0, 0x80E2A0 + 42 * 26 + 25)));
        bytes[0x80CE90 + 1 * 12 + 4] = 255;
        Assert.Null(EquipmentStatsReader.Parse(bytes));
    }

    [Theory]
    [InlineData(" <color=red>说明</color>\r\n装备时“移动”会提升。\n【特典道具】 ", "说明\n装备时“移动”会提升。\n【特典道具】")]
    [InlineData("P<scale=0.8>OWER<scale><br/>WP < 10 > 0", "POWER\nWP < 10 > 0")]
    [InlineData("<b>Text</b> <literal>keep</literal>", "Text <literal>keep</literal>")]
    [InlineData(null, "")]
    public void DescriptionsPreserveLinesCaseAndLiteralText(string? raw, string expected) =>
        Assert.Equal(expected, GameTextData.CleanDescription(raw));

    private static GameTextData TextData(string description) => new([], [], [],
        new() { [1] = "匕首", [208] = "战靴", [257] = "特典", [240] = "Potion" }, [], [])
    {
        ItemDescriptions = new() { [1] = description, [208] = "战斗用鞋。\n装备时“移动”会提升。", [257] = "实战用剑。\n【特典道具】", [240] = "Restores HP." }
    };

    [Fact]
    public void TableDescriptionSurvivesWithoutANameAndDoesNotChangeNameCleaning()
    {
        var data = GameTextData.FromTables(new Dictionary<string, IReadOnlyList<GameTableRow>>
        {
            ["item"] = new GameTableRow[]
            {
                new(1, new Dictionary<string, object?> { ["Name"] = "P<scale=0.8>OIGNARD<scale>", ["Description"] = "<b>POWER</b>\nSecond line." }),
                new(2, new Dictionary<string, object?> { ["Description"] = "Description only." }),
                new(3, new Dictionary<string, object?> { ["Name"] = "Name only", ["Description"] = 42 })
            }
        });
        Assert.Equal("Poignard", data.Items[1]);
        Assert.Equal("POWER\nSecond line.", data.ItemDescriptions[1]);
        Assert.Equal("Description only.", data.ItemDescriptions[2]);
        Assert.Equal("Name only", data.Items[3]);
        Assert.False(data.ItemDescriptions.ContainsKey(3));
    }

    [Fact]
    public void PublicVmContractKeepsPoachingSeparateAndBonusStatsUnknown()
    {
        var catalog = new GameTextCatalog();
        catalog.Load(TextData("专为护身用途打造的短剑。"));
        catalog.LoadEquipment(EquipmentStatsReader.Parse(Image()));
        var dagger = new InventoryItemViewModel(1, 1, catalog);
        Assert.Equal("专为护身用途打造的短剑。", dagger.Description);
        Assert.Contains("WP 3", dagger.StatSummary);
        Assert.Contains("5%", dagger.StatSummary);
        Assert.DoesNotContain("物理攻击力", dagger.StatSummary);
        Assert.True(dagger.HasDetails);
        Assert.Contains("FFT_enhanced.exe", dagger.DetailsText);
        Assert.Contains("WP 不等于 PA", dagger.DetailsText);
        var boots = new InventoryItemViewModel(208, 1, catalog);
        Assert.Equal("移动 +1", boots.StatSummary);
        Assert.Contains("\n装备时", boots.Description);
        Assert.DoesNotContain("WP", boots.StatSummary);
        var bonus = new InventoryItemViewModel(257, 1, catalog);
        Assert.Equal("数值未知", bonus.StatSummary);
        Assert.Contains("【特典道具】", bonus.DetailsText);
        Assert.DoesNotContain("静态装备表", bonus.DetailsText);
        var potion = new InventoryItemViewModel(240, 1, catalog);
        Assert.Equal("", potion.StatSummary);
        Assert.Equal("Potion\n\nRestores HP.", potion.DetailsText);
        var poaching = new InventoryItemViewModel(1, 1, catalog, isPoaching: true);
        Assert.Equal("", poaching.Description);
        Assert.Equal("", poaching.StatSummary);
        Assert.Equal(poaching.Name, poaching.DetailsText);
        Assert.False(poaching.HasDetails);
        var noDetails = new InventoryItemViewModel(241, 1, catalog);
        Assert.Equal(noDetails.Name, noDetails.DetailsText);
        Assert.False(noDetails.HasDetails);
        Assert.StartsWith(dagger.Name + "\n", dagger.DetailsText);
        catalog.Load(null); // text failure does not erase independent numeric tables
        Assert.Equal("", dagger.Description);
        Assert.Contains("WP 3", dagger.StatSummary);
        catalog.LoadEquipment(null);
        Assert.Equal("数值未知", dagger.StatSummary);
        Assert.DoesNotContain(" 0", dagger.DetailsText);
    }

    [Fact]
    public void KnownZeroBonusesAreNotClaimedToMeanNoSpecialEffects()
    {
        var catalog = new GameTextCatalog();
        var bytes = Image();
        bytes[0x80CE90 + 6 * 12 + 3] = 0x08;
        catalog.LoadEquipment(EquipmentStatsReader.Parse(bytes));
        var item = new InventoryItemViewModel(6, 1, catalog);
        Assert.Equal("", item.StatSummary);
        Assert.True(item.HasDetails);
        Assert.Contains("没有数值不代表没有特殊效果", item.DetailsText);
    }

    [Fact]
    public void AllLanguagesUpdateDescriptionsLabelsAndNotificationsInPlace()
    {
        var catalog = new GameTextCatalog();
        catalog.LoadEquipment(EquipmentStatsReader.Parse(Image()));
        var item = new InventoryItemViewModel(1, 1, catalog);
        item.Quantity.Value = 7;
        var notifications = new List<string?>();
        item.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        try
        {
            foreach (var language in UiLanguages.All)
            {
                Localizer.Instance.SetLanguage(language);
                catalog.Load(TextData(language.GameCode + "\nDescription"));
                item.RefreshLanguage();
                Assert.Equal(language.GameCode + "\nDescription", item.Description);
                Assert.Contains(Localizer.T("core.item.stats.wp") + " 3", item.StatSummary);
                Assert.Contains(Localizer.T("core.item.stats.weaponEvade") + " 5%", item.StatSummary);
                Assert.Contains(Localizer.T("core.item.stats.source"), item.DetailsText);
                foreach (var key in new[] { "core.item.stats.wp", "core.item.stats.weaponEvade", "core.item.stats.unknown", "core.item.stats.source", "core.item.stats.limitations", "core.stat.jump" })
                    Assert.True(Localizer.Instance.Strings(language.Code).ContainsKey(key));
            }
            Assert.Equal(UiLanguages.All.Count, notifications.Count(n => n == ""));
            Assert.Equal(7u, item.Quantity.Value);
            Assert.True(item.Quantity.IsDirty);
        }
        finally { Localizer.Instance.SetLanguage(UiLanguages.Source); }
    }

    [Fact]
    public void ProductionLoaderRejectsCorrectLengthWrongHashAndResetsForOtherRoots()
    {
        WithTemp(temp =>
        {
            var first = Path.Combine(temp, "first");
            var second = Path.Combine(temp, "second");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            // Even valid-looking parsed tables and an MZ header cannot bypass the hash gate.
            var bytes = Image();
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            File.WriteAllBytes(Path.Combine(first, "FFT_enhanced.exe"), bytes);
            File.WriteAllBytes(Path.Combine(second, "FFT_enhanced.exe"), [1, 2]);
            var reader = new EquipmentStatsReader();
            Assert.Null(reader.Load(first));
            var identity = reader.FileIdentity;
            Assert.NotNull(identity);
            Assert.Null(reader.Load(first));
            Assert.Equal(identity, reader.FileIdentity);
            Assert.Null(reader.Load(second));
            Assert.NotEqual(identity, reader.FileIdentity);
            Assert.Null(reader.Load(null));
            Assert.Null(reader.FileIdentity);
            Assert.Null(reader.Load(Path.Combine(temp, "missing")));
            File.WriteAllBytes(Path.Combine(first, "FFT_enhanced.exe"), [1]);
            Assert.Null(reader.Load(first));
            Assert.NotEqual(identity, reader.FileIdentity);
        });
    }

    [Fact]
    public void CacheVersionMigrationAndMissingDescriptionFieldReloadThenRoundTrip()
    {
        WithTemp(temp =>
        {
            var root = Path.Combine(temp, "game");
            var cache = Path.Combine(temp, "cache");
            Directory.CreateDirectory(root);
            var exePath = Path.Combine(root, "FFT_enhanced.exe");
            File.WriteAllBytes(exePath, [1, 2, 3]);
            var exe = new FileInfo(exePath);
            var oldDirectory = Path.Combine(cache, $"v2-{exe.Length:x}-{exe.LastWriteTimeUtc.Ticks:x}");
            Directory.CreateDirectory(oldDirectory);
            var old = JsonSerializer.SerializeToNode(TextData("stale"))!;
            old.AsObject().Remove("ItemDescriptions");
            File.WriteAllText(Path.Combine(oldDirectory, "cs.json"), old.ToJsonString());
            var calls = 0;
            GameTextData Reader(string _, string language) { calls++; return TextData(language + "\nline 2"); }
            var catalog = new GameTextCatalog();
            GameTextService Service()
            {
                var service = new GameTextService(catalog, cache, Reader);
                service.SetGameRoot(root);
                service.Load(UiLanguages.Source);
                return service;
            }
            Service();
            Assert.Equal(1, calls);
            Assert.Equal("cs\nline 2", catalog.ItemDescription(1));
            Service();
            Assert.Equal(1, calls);
            Assert.Equal("cs\nline 2", catalog.ItemDescription(1));
            var currentDirectory = Assert.Single(Directory.GetDirectories(cache, "v3-*"));
            var currentFile = Path.Combine(currentDirectory, "cs.json");
            var current = JsonNode.Parse(File.ReadAllText(currentFile))!;
            current.AsObject().Remove("ItemDescriptions");
            File.WriteAllText(currentFile, current.ToJsonString());
            var active = Service();
            Assert.Equal(2, calls);
            active.Load(UiLanguages.English);
            Assert.Equal("en\nline 2", catalog.ItemDescription(1));
            Assert.Equal(3, calls);
            Assert.True(active.SetGameRoot(Path.Combine(temp, "missing")));
            Assert.Equal("", catalog.ItemDescription(1));
            active.Load(UiLanguages.Source);
            Assert.True(catalog.IsEmpty);
        });
    }

    [Fact]
    public void UnsupportedNumericVersionDoesNotHideNamesOrDescriptionsAndRootChangesClear()
    {
        WithTemp(temp =>
        {
            var root = Path.Combine(temp, "game");
            Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, "FFT_enhanced.exe"), [1]);
            var catalog = new GameTextCatalog();
            catalog.LoadEquipment(EquipmentStatsReader.Parse(Image()));
            var service = new GameTextService(catalog, Path.Combine(temp, "cache"), (_, _) => TextData("first\nsecond"));
            service.SetGameRoot(root);
            service.Load(UiLanguages.Source);
            var item = new InventoryItemViewModel(1, 1, catalog);
            Assert.Equal("匕首", item.Name);
            Assert.Equal("first\nsecond", item.Description);
            Assert.Equal("数值未知", item.StatSummary);
            service.SetGameRoot(null);
            Assert.Equal("", item.Description);
            Assert.Null(catalog.ItemStats(1));
        });
    }

    private static void WithTemp(Action<string> action)
    {
        var temp = Path.Combine(Path.GetTempPath(), "fft-details-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try { action(temp); }
        finally { Directory.Delete(temp, true); }
    }
}
