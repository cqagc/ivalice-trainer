using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace FftTrainer.Core;

// 修改器支持的界面语言，和游戏自身支持的 7 种语言一一对应。
// GameCode 是游戏封包的语言后缀（data/enhanced/0004.<GameCode>.pac）。
public sealed record UiLanguage(string Code, string GameCode, string NativeName, string XmlLanguage,
    string SansFonts, string SerifFonts)
{
    public override string ToString() => NativeName;
}

public static class UiLanguages
{
    public static IReadOnlyList<UiLanguage> All { get; } =
    [
        new("zh-Hans", "cs", "简体中文", "zh-CN", "Microsoft YaHei UI", "Noto Serif SC, SimSun, Source Han Serif SC"),
        new("zh-Hant", "ct", "繁體中文", "zh-TW", "Microsoft JhengHei UI", "Noto Serif TC, PMingLiU, Source Han Serif TC"),
        new("en", "en", "English", "en-US", "Segoe UI", "Noto Serif, Georgia, Noto Serif SC"),
        new("fr", "fr", "Français", "fr-FR", "Segoe UI", "Noto Serif, Georgia, Noto Serif SC"),
        new("de", "de", "Deutsch", "de-DE", "Segoe UI", "Noto Serif, Georgia, Noto Serif SC"),
        new("ja", "ja", "日本語", "ja-JP", "Yu Gothic UI, Meiryo UI", "Noto Serif JP, Yu Mincho, MS Mincho"),
        new("ko", "ko", "한국어", "ko-KR", "Malgun Gothic", "Noto Serif KR, Batang, Malgun Gothic"),
    ];

    public static UiLanguage Source => All[0];
    public static UiLanguage English => All[2];

    public static UiLanguage? Find(string? code) =>
        All.FirstOrDefault(language => string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase));

    // 按 Windows 界面语言挑默认值；不在 7 种之内的一律英文
    public static UiLanguage FromCulture(CultureInfo culture)
    {
        for (var current = culture; !string.IsNullOrEmpty(current.Name); current = current.Parent)
        {
            var name = current.Name;
            if (name is "zh-Hans" or "zh-CN" or "zh-SG")
                return All[0];
            if (name is "zh-Hant" or "zh-TW" or "zh-HK" or "zh-MO")
                return All[1];
            if (Find(name) is { } exact)
                return exact;
        }
        return English;
    }
}

// 界面文字：每种语言一组嵌入资源 Locales/<区域>.<语言>.json（扁平的 key → 文字）。
// 查找顺序：当前语言 → 英文 → 简体中文 → key 本身，所以缺译不会显示空白。
// WPF 通过索引器绑定（Localizer.Instance[key]），切换语言时发出 Item[] 变化通知，界面即时刷新。
public sealed class Localizer : INotifyPropertyChanged
{
    private static readonly Assembly Resources = typeof(Localizer).Assembly;
    private readonly Dictionary<string, Dictionary<string, string>> cache = [];

    public static Localizer Instance { get; } = new();

    public UiLanguage Language { get; private set; } = UiLanguages.Source;
    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("zh-CN");

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LanguageChanged;

    public string this[string key] => Get(key);

    public static string T(string key) => Instance.Get(key);

    public static string F(string key, params object?[] args) =>
        string.Format(Instance.Culture, Instance.Get(key), args);

    public string Get(string key)
    {
        foreach (var language in new[] { Language, UiLanguages.English, UiLanguages.Source })
            if (Strings(language.Code).TryGetValue(key, out var text))
                return text;
        return key;
    }

    public void SetLanguage(UiLanguage language)
    {
        if (language == Language)
            return;
        Language = language;
        Culture = CultureInfo.GetCultureInfo(language.XmlLanguage);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke();
    }

    public IReadOnlyDictionary<string, string> Strings(string code)
    {
        lock (cache)
        {
            if (cache.TryGetValue(code, out var strings))
                return strings;
            strings = [];
            foreach (var name in ResourceNames(code))
            {
                using var stream = Resources.GetManifestResourceStream(name)!;
                foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [])
                    strings[key] = value;
            }
            cache[code] = strings;
            return strings;
        }
    }

    // 资源名形如 Locales.app.zh-Hans.json / Locales.core.zh-Hans.json
    public static IEnumerable<string> ResourceNames(string code) => Resources.GetManifestResourceNames()
        .Where(name => name.StartsWith("Locales.", StringComparison.Ordinal)
            && name.EndsWith("." + code + ".json", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal);
}

// 界面设置（语言），存 %LOCALAPPDATA%\FftTrainer\ui-settings.json。读写失败当作没有设置。
public sealed record UiSettings(string? Language = null)
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FftTrainer", "ui-settings.json");

    public static UiSettings Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new();
        }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
