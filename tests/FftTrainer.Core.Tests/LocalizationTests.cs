using System.Globalization;
using System.Text.RegularExpressions;
using FftTrainer.Core;
using Xunit;

namespace FftTrainer.Core.Tests;

public class LocalizationTests
{
    public static TheoryData<string> Languages()
    {
        var data = new TheoryData<string>();
        foreach (var language in UiLanguages.All.Skip(1))
            data.Add(language.Code);
        return data;
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryLanguageHasTheSameKeysAsSimplifiedChinese(string code)
    {
        var source = Localizer.Instance.Strings(UiLanguages.Source.Code);
        var target = Localizer.Instance.Strings(code);
        Assert.Empty(source.Keys.Except(target.Keys).Order());
        Assert.Empty(target.Keys.Except(source.Keys).Order());
        Assert.DoesNotContain(target, pair => string.IsNullOrWhiteSpace(pair.Value));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void PlaceholdersMatchSimplifiedChinese(string code)
    {
        static string Holes(string text) => string.Join(",", Regex.Matches(text, @"\{\d+(:[^}]*)?\}")
            .Select(match => match.Value).Order(StringComparer.Ordinal));
        var source = Localizer.Instance.Strings(UiLanguages.Source.Code);
        var target = Localizer.Instance.Strings(code);
        foreach (var (key, text) in source)
            if (target.TryGetValue(key, out var translated))
                Assert.True(Holes(text) == Holes(translated), $"{code} {key}: {Holes(text)} ≠ {Holes(translated)}");
    }

    [Fact]
    public void EachLanguageShipsBothResourceAreas()
    {
        foreach (var language in UiLanguages.All)
            Assert.Equal(["Locales.app." + language.Code + ".json", "Locales.core." + language.Code + ".json"],
                Localizer.ResourceNames(language.Code).ToArray());
    }

    [Theory]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("ja-JP", "ja")]
    [InlineData("ko-KR", "ko")]
    [InlineData("fr-CA", "fr")]
    [InlineData("de-AT", "de")]
    [InlineData("en-GB", "en")]
    [InlineData("es-ES", "en")]
    [InlineData("", "en")]
    public void DefaultLanguageFollowsWindowsCulture(string culture, string expected) =>
        Assert.Equal(expected, UiLanguages.FromCulture(CultureInfo.GetCultureInfo(culture)).Code);

    [Fact]
    public void MissingKeyFallsBackToKey()
    {
        Assert.Equal("no.such.key", Localizer.T("no.such.key"));
        Assert.Equal("伊瓦利斯战记", Localizer.Instance.Strings("zh-Hans")["app.name"]);
    }

    [Fact]
    public void GameCodesMatchPackSuffixes() =>
        Assert.Equal(["cs", "ct", "en", "fr", "de", "ja", "ko"], UiLanguages.All.Select(language => language.GameCode).ToArray());
}
