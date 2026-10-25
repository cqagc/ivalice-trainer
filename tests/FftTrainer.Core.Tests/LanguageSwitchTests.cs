using System.Buffers.Binary;
using FftTrainer.Core;
using Xunit;

namespace FftTrainer.Core.Tests;

// Localizer 是全局静态的：切换语言的测试放进禁止并行的集合，xUnit 会等其他测试跑完再单独跑它们，
// 并且每个测试都在 finally 里切回简体中文，不影响断言中文文字的其他测试。
[CollectionDefinition("Localizer", DisableParallelization = true)]
public sealed class LocalizerCollection;

public sealed partial class TrainerTests
{
    [Collection("Localizer")]
    public sealed class LanguageSwitch
    {
        private static TestMemory RosterAndBattle()
        {
            var memory = BattleFixture();
            var data = new byte[0x258];
            data[0] = 1;
            data[2] = 0x01;
            data[FftLayout.Level] = 5;
            data[FftLayout.Brave] = 70;
            data[FftLayout.Faith] = 60;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(FftLayout.NameId), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x0E), 0x9D);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x10), 0xFF);
            memory.Seed(FftLayout.Roster, data);
            memory.Seed(FftLayout.Inventory + 0xF0, [12]);
            return memory;
        }

        [Fact]
        public void SwitchingLanguageUpdatesTextAndKeepsPendingEdits()
        {
            var memory = RosterAndBattle();
            var model = new TrainerViewModel(memory, text: TestText.Legacy());
            model.ApplySnapshot(memory.Snapshot());
            var unit = model.Units[0];
            unit.Brave.Value = 80;
            var pending = model.Pending.Select(field => field.ToEdit()).ToArray();
            Assert.Single(pending);
            Assert.Equal("拉姆萨 Brave", unit.Brave.Label);
            Assert.Equal("男", unit.Gender);
            try
            {
                Localizer.Instance.SetLanguage(UiLanguages.English);
                model.RefreshLanguage();
                Assert.Equal("拉姆萨 Bravery", unit.Brave.Label);
                Assert.Contains("Brave", Assert.Single(model.Pending).ChangeText);
                Assert.Equal("Male", unit.Gender);
                Assert.Contains("Protagonist", unit.Number);
                Assert.Equal("Head", unit.Equipment[0].Name);
                Assert.Equal("Empty", unit.Equipment[1].ItemName);
                Assert.Equal("Secondary", unit.SkillSlots[0].Name);
                Assert.Equal("Max HP", unit.Stats[0].Name);
                Assert.Equal("Gil", model.Gil.Label);
                Assert.Equal("Connected to FFT_enhanced.exe", model.ConnectionText);
                Assert.StartsWith("In battle", model.BattleStatus);
                Assert.Contains(model.Enemies, enemy => enemy.Name.StartsWith("Enemy · ", StringComparison.Ordinal));
                Assert.Equal("Items", model.Items[0xF0].Group);
                Assert.EndsWith(" quantity", model.Items[0xF0].Quantity.Label);
                // 待写入的改动原样保留
                Assert.Equal(80u, unit.Brave.Value);
                Assert.True(unit.Brave.IsDirty);
                Assert.Equal(pending, model.Pending.Select(field => field.ToEdit()).ToArray());
                // 道具筛选不受显示语言影响
                model.ShowConsumables.Execute(null);
                Assert.Contains(model.VisibleItems, item => item.Id == 0xF0);
                // 新产生的提示用新语言
                model.Undo.Execute(null);
                Assert.Equal("All changes discarded.", model.Status);
            }
            finally
            {
                Localizer.Instance.SetLanguage(UiLanguages.Source);
                model.RefreshLanguage();
            }
            Assert.Equal("拉姆萨 Brave", unit.Brave.Label);
            Assert.Equal("男", unit.Gender);
            Assert.Equal("消耗品", model.Items[0xF0].Group);
        }

        [Fact]
        public void ActiveInventorySearchIsReevaluatedWhenNamesChangeLanguage()
        {
            var text = new GameTextCatalog();
            text.Parse("items", """[{"id":1,"name":"测试剑","equipmentGroup":"武器"}]""");
            var model = new TrainerViewModel(new TestMemory(), text: text);
            model.ApplySnapshot(InventorySnapshot());
            model.ItemSearch = "测试剑";
            var row = Assert.Single(model.VisibleItems);
            row.Quantity.Value = 42;
            try
            {
                Localizer.Instance.SetLanguage(UiLanguages.English);
                // GameTextService replaces the catalog on language switches; simulate
                // its official-name reload without requiring installed game archives.
                text.Parse("items", """[{"id":1,"name":"Test sword","equipmentGroup":"武器"}]""");
                model.RefreshLanguage();
                Assert.Empty(model.VisibleItems);
                Assert.Same(row.Quantity, Assert.Single(model.Pending));
                model.ItemSearch = "test SWORD";
                Assert.Same(row, Assert.Single(model.VisibleItems));
                model.ItemSearch = row.HexId;
                Assert.Contains(row, model.VisibleItems);
                Assert.Equal(42u, row.Quantity.Value);
            }
            finally
            {
                Localizer.Instance.SetLanguage(UiLanguages.Source);
            }
        }

        [Fact]
        public void ErrorsAndNoticesUseTheCurrentLanguage()
        {
            try
            {
                Localizer.Instance.SetLanguage(UiLanguages.English);
                Assert.Equal("Job 0x99", GameData.JobName(0x99));
                Assert.Equal("Unit #7", new GameTextCatalog().UnitName(7));
                Assert.Equal("None", new GameTextCatalog().AbilityName(0));
                Assert.Equal("Ability #511 (special value, read-only)", new GameTextCatalog().AbilityName(511));
                var rewards = new BattleRewards();
                rewards.Configure(2, 2);
                rewards.Apply(new TestMemory(), (_, _) => { }, 0, []);
                Assert.Equal("Battle over. EXP and JP multipliers reset to 1×.", rewards.Notice);
            }
            finally
            {
                Localizer.Instance.SetLanguage(UiLanguages.Source);
            }
            Assert.Equal("队员 #7", new GameTextCatalog().UnitName(7));
        }
    }
}
