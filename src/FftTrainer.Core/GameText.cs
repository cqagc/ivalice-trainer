using System.Text.Json;
using System.Text.RegularExpressions;
using FftTrainer.GameFiles;
using Microsoft.Win32;

namespace FftTrainer.Core;

// 一种语言的游戏文本（只留修改器用得到的名称），可以序列化成缓存文件
public sealed record GameTextData(
    Dictionary<int, string> Units,
    Dictionary<int, string> Jobs,
    Dictionary<int, string> Commands,
    Dictionary<int, string> Items,
    Dictionary<int, string> ItemCategories,
    Dictionary<int, string> Abilities)
{
    // 本地化 Nex 表名（layout 在程序目录 Nex/Layouts/ffto/ 下）；字段对应见 docs/text-research.md
    public static IReadOnlyList<string> Tables { get; } = ["job", "jobcommand", "item", "uiitemcategory", "ability", "charaname"];

    // 名称里的排版标记：法文物品类别用小型大写字母（"P<scale=0.8>OIGNARD<scale>"），还原成普通大小写；其余标签直接去掉
    public static string? Clean(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('<'))
            return text?.Trim();
        text = Regex.Replace(text, "<scale=[^>]*>(.*?)<scale>", match => match.Groups[1].Value.ToLowerInvariant());
        return Regex.Replace(text, "<[^>]*>", "").Trim();
    }

    public static GameTextData FromTables(IReadOnlyDictionary<string, IReadOnlyList<GameTableRow>> tables)
    {
        IReadOnlyList<GameTableRow> Rows(string table) => tables.TryGetValue(table, out var rows) ? rows : [];
        // 有些行 Name 为空、名字在 NameSingular（如韩文 item 1）
        static string? Name(GameTableRow row) => Clean(row.Text("Name")) is { Length: > 0 } name ? name
            : Clean(row.Text("NameSingular")) is { Length: > 0 } singular ? singular : null;
        static Dictionary<int, string> Names(IEnumerable<GameTableRow> rows)
        {
            var result = new Dictionary<int, string>();
            foreach (var row in rows)
                if (Name(row) is { } name)
                    result[row.Key] = name;
            return result;
        }

        var jobs = Names(Rows("job"));
        var commandNames = Names(Rows("jobcommand"));
        // 只收职业表引用到的行动技能组合
        var commands = new Dictionary<int, string>();
        foreach (var row in Rows("job"))
            if (row.Number("jobcommand+Id") is { } command && commandNames.TryGetValue(command, out var commandName))
                commands[command] = commandName;
        var categoryNames = Names(Rows("uiitemcategory"));
        var items = new Dictionary<int, string>();
        var categories = new Dictionary<int, string>();
        foreach (var row in Rows("item"))
        {
            if (Name(row) is not { } name)
                continue;
            items[row.Key] = name;
            if (row.Number("UiItemCategoryId") is { } category && categoryNames.TryGetValue(category, out var categoryName))
                categories[row.Key] = categoryName;
        }
        return new GameTextData(Names(Rows("charaname")), jobs, commands, items, categories, Names(Rows("ability")));
    }
}

// 找游戏目录、按界面语言读游戏文本并换入 GameTextCatalog。
// 读出的结果缓存在 %LOCALAPPDATA%\FftTrainer\gametext\<exe 大小-修改时间>\<语言>.json，游戏更新后自动作废。
// 任何失败（目录找不到、封包或表结构变了、DirectStorage dll 缺失）都只记 session.log，目录清空后走编号兜底，不抛异常。
public sealed class GameTextService
{
    public const string SteamAppId = "1004640";
    private const int CacheVersion = 2;
    private readonly GameTextCatalog catalog;
    private readonly string cacheDirectory;
    private readonly Func<string, string, GameTextData> reader;
    private string? loadedKey;

    public GameTextService(GameTextCatalog catalog, string cacheDirectory,
        Func<string, string, GameTextData>? reader = null)
    {
        this.catalog = catalog;
        this.cacheDirectory = cacheDirectory;
        this.reader = reader ?? ((root, language) =>
            GameTextData.FromTables(GameFileReader.ReadLocalizedTables(root, language, GameTextData.Tables)));
    }

    public static string DefaultCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FftTrainer", "gametext");

    public string? GameRoot { get; private set; }

    // 设定游戏根目录；返回 true 表示目录有变化（需要重新 Load）
    public bool SetGameRoot(string? root)
    {
        if (root != null && !File.Exists(Path.Combine(root, GameFileReader.ExecutableName)))
            root = null;
        if (root == null || string.Equals(root, GameRoot, StringComparison.OrdinalIgnoreCase))
            return false;
        GameRoot = root;
        return true;
    }

    // 连上游戏进程后，用进程的 FFT_enhanced.exe 路径定位游戏目录
    public bool SetGameRootFromExecutable(string? executablePath) =>
        executablePath != null && SetGameRoot(Path.GetDirectoryName(executablePath));

    public void Load(UiLanguage language)
    {
        var key = GameRoot == null ? null : GameRoot + "|" + language.GameCode;
        if (key != null && key == loadedKey)
            return;
        loadedKey = key;
        if (GameRoot == null)
        {
            catalog.Load(null);
            return;
        }
        try
        {
            catalog.Load(ReadCached(GameRoot, language.GameCode));
        }
        catch (Exception ex)
        {
            GameSession.LogLine($"游戏文本读取失败（{language.GameCode}，{GameRoot}）：{ex.GetType().Name}: {ex.Message}");
            catalog.Load(null);
        }
    }

    private GameTextData ReadCached(string root, string language)
    {
        string? path = null;
        try
        {
            var exe = new FileInfo(Path.Combine(root, GameFileReader.ExecutableName));
            path = Path.Combine(cacheDirectory, $"v{CacheVersion}-{exe.Length:x}-{exe.LastWriteTimeUtc.Ticks:x}", language + ".json");
            if (File.Exists(path) && JsonSerializer.Deserialize<GameTextData>(File.ReadAllText(path)) is { } cached
                && cached.Jobs != null && cached.Items != null && cached.Units != null && cached.Abilities != null
                && cached.Commands != null && cached.ItemCategories != null)
                return cached;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // 缓存坏了就重读
        }
        var data = reader(root, language);
        if (path != null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(data));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 存不下就每次现读
            }
        }
        return data;
    }

    // 游戏没开时，按 Steam 的库目录找安装位置（registry SteamPath → libraryfolders.vdf → appmanifest_1004640.acf）
    public static string? FindSteamInstall()
    {
        try
        {
            var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
            if (string.IsNullOrEmpty(steam))
                return null;
            var libraries = new List<string> { steam };
            var folders = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(folders))
                libraries.AddRange(VdfValues(File.ReadAllText(folders), "path"));
            foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var manifest = Path.Combine(library, "steamapps", $"appmanifest_{SteamAppId}.acf");
                if (!File.Exists(manifest))
                    continue;
                if (VdfValues(File.ReadAllText(manifest), "installdir").FirstOrDefault() is not { } folder)
                    continue;
                var root = Path.Combine(library, "steamapps", "common", folder);
                if (File.Exists(Path.Combine(root, GameFileReader.ExecutableName)))
                    return root;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
        return null;
    }

    // Valve KeyValues 文本里取 "key" "value"；值里的 \\ 还原成 \
    public static IEnumerable<string> VdfValues(string text, string key) =>
        System.Text.RegularExpressions.Regex.Matches(text, "\"" + key + "\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value.Replace(@"\\", @"\"));
}
