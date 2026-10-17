using FftTrainer.Core;
using Xunit;

namespace FftTrainer.Core.Tests;

public class GameTextServiceTests
{
    private static GameTextData Data(string job) =>
        new(Units: new() { [1] = "Ramza" }, Jobs: new() { [74] = job }, Commands: [], Items: new() { [240] = "Potion" },
            ItemCategories: [], Abilities: []);

    private static string FakeGame(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "FFT_enhanced.exe"), [1, 2, 3]);
        return root;
    }

    [Fact]
    public void LoadsCurrentLanguageCachesItAndSwitchesInPlace()
    {
        var temp = Path.Combine(Path.GetTempPath(), "fft-gametext-" + Guid.NewGuid().ToString("N"));
        try
        {
            var root = FakeGame(Path.Combine(temp, "game"));
            var calls = new List<string>();
            GameTextData Reader(string gameRoot, string language)
            {
                Assert.Equal(root, gameRoot);
                calls.Add(language);
                return Data(language == "cs" ? "见习战士" : "Squire");
            }
            var catalog = new GameTextCatalog();
            var service = new GameTextService(catalog, Path.Combine(temp, "cache"), Reader);
            Assert.False(service.SetGameRoot(Path.Combine(temp, "missing")));
            Assert.True(service.SetGameRoot(root));
            Assert.False(service.SetGameRoot(root));
            service.Load(UiLanguages.Source);
            Assert.Equal("见习战士", catalog.JobName(74));
            service.Load(UiLanguages.Source);          // 同一目录、同一语言不重读
            Assert.Equal(["cs"], calls);
            service.Load(UiLanguages.English);         // 切换语言：同一个目录对象就地换内容
            Assert.Equal("Squire", catalog.JobName(74));
            Assert.Equal(["cs", "en"], calls);

            // 重启后读缓存，不再读封包
            var restarted = new GameTextCatalog();
            var again = new GameTextService(restarted, Path.Combine(temp, "cache"), Reader);
            Assert.True(again.SetGameRootFromExecutable(Path.Combine(root, "FFT_enhanced.exe")));
            again.Load(UiLanguages.Source);
            Assert.Equal("见习战士", restarted.JobName(74));
            Assert.Equal(["cs", "en"], calls);

            // 游戏更新（exe 变了）缓存作废
            File.WriteAllBytes(Path.Combine(root, "FFT_enhanced.exe"), [1, 2, 3, 4]);
            var updated = new GameTextService(new GameTextCatalog(), Path.Combine(temp, "cache"), Reader);
            updated.SetGameRoot(root);
            updated.Load(UiLanguages.Source);
            Assert.Equal(["cs", "en", "cs"], calls);
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public void ReadFailureFallsBackToNumbersWithoutThrowing()
    {
        var temp = Path.Combine(Path.GetTempPath(), "fft-gametext-" + Guid.NewGuid().ToString("N"));
        try
        {
            var root = FakeGame(Path.Combine(temp, "game"));
            var catalog = new GameTextCatalog();
            catalog.Load(Data("旧数据"));
            var service = new GameTextService(catalog, Path.Combine(temp, "cache"),
                (_, _) => throw new DllNotFoundException("dstorage.dll"));
            service.SetGameRoot(root);
            service.Load(UiLanguages.Source);
            Assert.True(catalog.IsEmpty);
            Assert.Equal("职业 0x4A", catalog.JobName(74));

            // 找不到游戏目录：清空，走编号兜底
            var none = new GameTextCatalog();
            none.Load(Data("旧数据"));
            new GameTextService(none, Path.Combine(temp, "cache"), (_, _) => Data("x")).Load(UiLanguages.Source);
            Assert.True(none.IsEmpty);
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public void BrokenCacheIsReadAgain()
    {
        var temp = Path.Combine(Path.GetTempPath(), "fft-gametext-" + Guid.NewGuid().ToString("N"));
        try
        {
            var root = FakeGame(Path.Combine(temp, "game"));
            var cache = Path.Combine(temp, "cache");
            var reads = 0;
            var first = new GameTextService(new GameTextCatalog(), cache, (_, _) => { reads++; return Data("A"); });
            first.SetGameRoot(root);
            first.Load(UiLanguages.Source);
            foreach (var file in Directory.GetFiles(cache, "*.json", SearchOption.AllDirectories))
                File.WriteAllText(file, "{broken");
            var catalog = new GameTextCatalog();
            var second = new GameTextService(catalog, cache, (_, _) => { reads++; return Data("B"); });
            second.SetGameRoot(root);
            second.Load(UiLanguages.Source);
            Assert.Equal(2, reads);
            Assert.Equal("B", catalog.JobName(74));
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [Theory]
    [InlineData("P<scale=0.8>OIGNARD<scale>", "Poignard")]
    [InlineData("É<scale=0.8>PÉE<scale>", "Épée")]
    [InlineData("V<scale=0.8>IDE<scale>", "Vide")]
    [InlineData("<b>Objets</b>", "Objets")]
    [InlineData("见习战士", "见习战士")]
    [InlineData(" Squire ", "Squire")]
    public void NamesDropLayoutMarkup(string raw, string expected) => Assert.Equal(expected, GameTextData.Clean(raw));

    [Fact]
    public void ParsesSteamLibraryFolders()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0" { "path"  "C:\\Program Files (x86)\\Steam" }
                "1" { "path"  "E:\\SteamLibrary"  "apps" { "1004640" "123" } }
            }
            """;
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"E:\SteamLibrary"], GameTextService.VdfValues(vdf, "path").ToArray());
        Assert.Equal(["FINAL FANTASY TACTICS - The Ivalice Chronicles"],
            GameTextService.VdfValues("\"installdir\"\t\t\"FINAL FANTASY TACTICS - The Ivalice Chronicles\"", "installdir").ToArray());
    }
}
