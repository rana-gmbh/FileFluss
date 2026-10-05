// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Resources;

namespace FileFluss.Core;

/// <summary>Language choices offered in Settings, matching the macOS <c>AppLanguage</c>.</summary>
public enum AppLanguage
{
    System,
    English,
    German,
    SimplifiedChinese,
    TraditionalChinese,
}

/// <summary>
/// String lookup. Keys are the English source strings, exactly as on macOS: the .resx files
/// are generated from the app's and FileFlussCore's Localizable.strings catalogues (plus the
/// Windows-only catalogue) by <c>windows/tools/StringsToResx</c>, so a key that works on the
/// Mac works here. Cocoa specifiers become composite items: <c>"Item not found: %@"</c> is
/// looked up as <c>L("Item not found: {0}", path)</c>.
///
/// <para>A missing key returns the key itself, which is what <c>NSLocalizedString</c> does
/// and what keeps a half-translated build readable rather than blank.</para>
/// </summary>
public static class Localization
{
    private static readonly ResourceManager Resources =
        new("FileFluss.Core.Resources.Strings", typeof(Localization).Assembly);

    private static CultureInfo? _override;
    private static bool _resourcesMissing;

    /// <summary>
    /// .NET folds resource names by case; macOS keys do not. The generator stores the later
    /// key of a case-colliding group under "key~2", "key~3", … and lookups probe those on a
    /// miss. Must equal <c>CollisionLimit</c> in the generator.
    /// </summary>
    private const char CollisionSuffix = '~';

    private const int CollisionLimit = 9;

    public static AppLanguage Current { get; private set; } = AppLanguage.System;

    /// <summary>Raised after <see cref="Use"/> so open windows can re-render their text.</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>The culture text should be formatted in: the chosen language, or Windows' own.</summary>
    public static CultureInfo Culture => _override ?? CultureInfo.CurrentCulture;

    public static void Use(AppLanguage language)
    {
        Current = language;
        _override = CultureFor(language);
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Localized string for <paramref name="key"/>, or the key when untranslated.</summary>
    public static string L(string key)
    {
        if (_resourcesMissing || string.IsNullOrEmpty(key))
        {
            return key;
        }

        var culture = _override ?? CultureInfo.CurrentUICulture;
        try
        {
            var value = Resources.GetString(key, culture);
            if (value is not null)
            {
                return value;
            }

            for (var ordinal = 2; ordinal <= CollisionLimit; ordinal++)
            {
                value = Resources.GetString($"{key}{CollisionSuffix}{ordinal}", culture);
                if (value is not null)
                {
                    return value;
                }
            }
        }
        catch (MissingManifestResourceException)
        {
            _resourcesMissing = true;
        }

        return key;
    }

    /// <summary>Localized string with composite formatting, e.g. <c>L("Item not found: {0}", path)</c>.</summary>
    public static string L(string key, params object?[] args)
    {
        var format = L(key);
        try
        {
            return string.Format(Culture, format, args);
        }
        catch (FormatException)
        {
            // A translator can break the placeholders; the unformatted string beats a crash.
            return format;
        }
    }

    public static CultureInfo? CultureFor(AppLanguage language) => language switch
    {
        AppLanguage.English => CultureInfo.GetCultureInfo("en"),
        AppLanguage.German => CultureInfo.GetCultureInfo("de"),
        AppLanguage.SimplifiedChinese => CultureInfo.GetCultureInfo("zh-Hans"),
        AppLanguage.TraditionalChinese => CultureInfo.GetCultureInfo("zh-Hant"),
        _ => null,
    };

    public static string DisplayName(AppLanguage language) => language switch
    {
        AppLanguage.System => L("System Default"),
        AppLanguage.English => "English",
        AppLanguage.German => "Deutsch",
        AppLanguage.SimplifiedChinese => "简体中文",
        AppLanguage.TraditionalChinese => "繁體中文",
        _ => language.ToString(),
    };
}
