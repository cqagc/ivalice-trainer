using FftTrainer.Core;
using Xunit;

namespace FftTrainer.Core.Tests;

public sealed partial class TrainerTests
{
    [Fact]
    public void ConnectingToTheGameLoadsGameTextFromItsDirectoryAndRenamesExistingCards()
    {
        var temp = Path.Combine(Path.GetTempPath(), "fft-connect-" + Guid.NewGuid().ToString("N"));
        try
        {
            var root = Path.Combine(temp, "game");
            Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, "FFT_enhanced.exe"), [7]);
            var reads = 0;
            var catalog = new GameTextCatalog();
            var service = new GameTextService(catalog, Path.Combine(temp, "cache"), (_, _) =>
            {
                reads++;
                return new GameTextData(Units: new() { [1] = "游戏里的拉姆萨" }, Jobs: new() { [1] = "游戏里的见习战士" },
                    Commands: [], Items: [], ItemCategories: [], Abilities: []);
            });
            var (memory, _) = Create();
            var model = new TrainerViewModel(memory, text: catalog, gameText: service);
            // 先连上但没有进程路径（目录未知）：编号兜底
            model.ApplySnapshot(memory.Snapshot());
            Assert.Equal("队员 #1", model.Units[0].Name);
            // 拿到进程路径：读游戏文本，已有卡片就地改名，待写入改动不受影响
            model.Units[0].Brave.Value = 80;
            model.ApplySnapshot(memory.Snapshot() with { ModulePath = Path.Combine(root, "FFT_enhanced.exe") });
            Assert.Equal("游戏里的拉姆萨", model.Units[0].Name);
            Assert.Equal("游戏里的见习战士", model.Units[0].Job);
            Assert.True(model.Units[0].Brave.IsDirty);
            model.ApplySnapshot(memory.Snapshot() with { ModulePath = Path.Combine(root, "FFT_enhanced.exe") });
            Assert.Equal(1, reads);
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }
}
