// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using FileFluss.Core;

namespace FileFluss.Core.Tests;

/// <summary>
/// The generated .resx files as a whole: every language has every key, no translation drops or
/// invents a placeholder, and the Windows catalogue's overrides of Mac wording are in effect.
/// </summary>
[Collection("Localization")]
public partial class LocalizationWindowsTests
{
    private static readonly ResourceManager Resources = new("FileFluss.Core.Resources.Strings", typeof(Localization).Assembly);

    public static TheoryData<string> Translations => new() { "de", "zh-Hans", "zh-Hant" };

    public static TheoryData<string> AllLanguages => new() { string.Empty, "de", "zh-Hans", "zh-Hant" };

    [Theory]
    [MemberData(nameof(Translations))]
    public void EveryEnglishKeyIsTranslated(string language)
    {
        var english = Entries(string.Empty).Keys;
        var translated = Entries(language).Keys;

        Assert.Empty(english.Except(translated).Order(StringComparer.Ordinal));
        Assert.Empty(translated.Except(english).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void EveryValueKeepsTheKeysPlaceholders(string language)
    {
        var broken = Entries(language)
            .Where(entry => !Placeholders(KeyOf(entry.Key)).SetEquals(Placeholders(entry.Value)))
            .Select(entry => $"{entry.Key} => {entry.Value}")
            .ToList();

        Assert.Empty(broken);
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void NoValueKeepsMacVocabulary(string language)
    {
        string[] macTerms =
        [
            "⌘", "⌥", "Finder", "Keychain", "keychain", "Schlüsselbund", "访达", "钥匙串", "鑰匙圈", "废纸篓", "垃圾桶", "Credential Manager",
            "this Mac", "diesem Mac", "这台 Mac", "這台 Mac", "OSStatus", "NetFS", "Systemeinstellungen", "系统设置", "系統設定",
        ];
        var offenders = Entries(language)
            .Where(entry => macTerms.Any(entry.Value.Contains))
            .Select(entry => $"{entry.Key} => {entry.Value}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void WindowsWordingInEveryLanguage()
    {
        try
        {
            Localization.Use(AppLanguage.English);
            Assert.Equal("Move to Recycle Bin", Localization.L("Move to Trash"));
            Assert.Equal("Show in Explorer", Localization.L("Reveal in Finder"));
            Assert.Equal("Search all sources (Ctrl+F)", Localization.L("Search all sources (⌘F)"));
            Assert.Equal("2. Open DevTools (F12 in Chrome/Edge/Firefox)", Localization.L("2. Open DevTools (⌥⌘I in Chrome/Safari/Firefox)"));
            Assert.DoesNotContain("50 MB", Localization.L("Click Connect to sign in with your Box account. Your browser will open for authentication. Uploads are capped at 50 MB per file in this version."));
            Assert.Equal("12% of the total", Localization.L("{0}% of the total", 12));

            Localization.Use(AppLanguage.German);
            Assert.Equal("In den Papierkorb verschieben", Localization.L("Move to Recycle Bin"));
            Assert.Equal("Im Explorer anzeigen", Localization.L("Show in Explorer"));
            Assert.Equal("Eigenschaften", Localization.L("Properties"));
            Assert.Equal("Möchtest du „a.txt“ wirklich in den Papierkorb verschieben?", Localization.L("Are you sure you want to move \"{0}\" to the Recycle Bin?", "a.txt"));
            Assert.Equal("Alle Quellen durchsuchen (Strg+F)", Localization.L("Search all sources (⌘F)"));
            Assert.Equal("Netzlaufwerk trennen (Z:)", Localization.L("Disconnect Network Drive ({0})", "Z:"));
            Assert.Equal("Als Netzlaufwerk verbinden", Localization.L("Mount in Finder"));
            Assert.Equal("Benötigt auf diesem PC:", Localization.L("Needs on this Mac:"));

            Localization.Use(AppLanguage.SimplifiedChinese);
            Assert.Equal("移到回收站", Localization.L("Move to Recycle Bin"));
            Assert.Equal("在文件资源管理器中显示", Localization.L("Show in Explorer"));
            Assert.Equal("设置", Localization.L("Settings"));
            Assert.Equal("无法将 a.txt 移到回收站：x", Localization.L("Failed to move {0} to the Trash: {1}", "a.txt", "x"));

            // The translation puts the arguments the other way round ("%2$@ … %1$@").
            Assert.Equal("确定要从 右侧 删除“a.txt”吗？", Localization.L("Are you sure you want to delete \"{0}\" from {1}?", "a.txt", "右侧"));

            Localization.Use(AppLanguage.TraditionalChinese);
            Assert.Equal("移到資源回收筒", Localization.L("Move to Recycle Bin"));
            Assert.Equal("在檔案總管中顯示", Localization.L("Show in Explorer"));
            Assert.Equal("設定", Localization.L("Settings"));
        }
        finally
        {
            Localization.Use(AppLanguage.English);
        }
    }

    /// <summary>Resource name → value for exactly one .resx (no fallback to the neutral file).</summary>
    private static Dictionary<string, string> Entries(string language)
    {
        var culture = language.Length == 0 ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(language);
        var set = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        Assert.NotNull(set);

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in set)
        {
            entries[(string)entry.Key] = (string)entry.Value!;
        }

        return entries;
    }

    /// <summary>The key a resource name stands for: case-colliding keys carry a "~N" suffix.</summary>
    private static string KeyOf(string name) => CollisionSuffix().Replace(name, string.Empty);

    private static HashSet<string> Placeholders(string text)
        => Placeholder().Matches(text.Replace("{{", string.Empty, StringComparison.Ordinal).Replace("}}", string.Empty, StringComparison.Ordinal))
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"~\d$")]
    private static partial Regex CollisionSuffix();
}
