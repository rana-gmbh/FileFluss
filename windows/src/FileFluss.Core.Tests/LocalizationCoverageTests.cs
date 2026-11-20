// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.RegularExpressions;

namespace FileFluss.Core.Tests;

/// <summary>
/// macOS 1.6 <c>LocalizationCoverageTests</c>: every key the Windows sources look up must be
/// in a catalogue (the macOS Localizable.strings or windows/Localization/*/Windows.strings),
/// or German and Chinese users read it in English. Fix a failure by adding the key to
/// <c>windows/Localization/en.lproj/Windows.strings</c> (and its translations), then running
/// <c>dotnet run --project windows/tools/StringsToResx</c>; that tool's <c>-- --missing</c>
/// prints the lines ready to paste.
/// </summary>
public sealed class LocalizationCoverageTests
{
    /// <summary>
    /// Keys allowed to stay untranslated, each with its reason. Add here only text that is
    /// deliberately not for translation.
    /// </summary>
    private static readonly HashSet<string> AllowedUntranslated = new(StringComparer.Ordinal)
    {
        // HTTP fallback reason phrases of the local WebDAV server: protocol text, not UI.
        "Not Found",
        "Path not found",
        "Resource not found",
    };

    // The patterns below mirror windows/tools/StringsToResx/Program.cs (ReportMissing) — keep
    // them in step. Only the calls that certainly look a key up; the tool's looser heuristics
    // for reviewers (ternary arms, tuple tables) would flag ids and log text here.

    /// <summary>L itself, and the helpers whose first string argument is a key (connect form rows, help book blocks).</summary>
    private static readonly Regex KeyCallPattern = new(
        """(?<![A-Za-z0-9_])(?:L|Tile|InfoCard|DetailRow|Option|SectionTitle|Section|SliceColumn|Requirement|Info|Warning|Link|P|H|B|T)\(\s*"((?:[^"\\]|\\.)*)"(?=\s*[,)])""",
        RegexOptions.CultureInvariant);

    /// <summary>Connect-form keys that are not a first argument: field labels, sign-in mode titles, Placeholder/Help.</summary>
    private static readonly Regex ConnectKeyPattern = new(
        """(?:ConnectFieldKind\.\w+\s*,\s*|ConnectMode\(\s*"[^"]*"\s*,\s*|(?<![A-Za-z0-9_])(?:Placeholder|Help)\s*=\s*)"((?:[^"\\]|\\.)*)"(?=\s*[,)}\r\n])""",
        RegexOptions.CultureInvariant);

    private static readonly Regex XamlKeyPattern = new(
        """\{u:Loc\s+(?:'([^']*)'|([^}\s]+))\}""",
        RegexOptions.CultureInvariant);

    [Fact]
    public void EveryKeyTheWindowsSourcesLookUpIsInACatalogue()
    {
        var missing = MissingKeys();
        Assert.True(
            missing.Count == 0,
            $"{missing.Count} key(s) are looked up but in no catalogue, so they show in English in every language. " +
            "Add them to windows/Localization/*/Windows.strings and run windows/tools/StringsToResx:" +
            Environment.NewLine + string.Join(Environment.NewLine, missing.Select(m => $"  {m.Key}    ({m.File})")));
    }

    [Fact]
    public void TheScanFindsKeys()
    {
        // Guards the test itself: a pattern that stopped matching would pass with nothing found.
        Assert.True(LookedUpKeys().Count > 500);
        Assert.True(Localization.HasKey("Cancel"));
        Assert.False(Localization.HasKey("No catalogue has this key, and none ever should"));
    }

    private static List<(string Key, string File)> MissingKeys()
        => LookedUpKeys()
            .Where(pair => !AllowedUntranslated.Contains(pair.Key) && !Localization.HasKey(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();

    private static List<(string Key, string File)> LookedUpKeys()
    {
        var sources = Path.Combine(RepositoryRoot(), "windows", "src");
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(sources, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sources, file);
            if (IsBuildOutput(relative) || relative.Split(Path.DirectorySeparatorChar)[0].EndsWith(".Tests", StringComparison.Ordinal))
            {
                continue;
            }

            // Comment lines quote example values ("Dropbox (Robert)") that are not keys.
            var text = string.Join('\n', File.ReadAllLines(file).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            foreach (var match in KeyCallPattern.Matches(text).Concat(ConnectKeyPattern.Matches(text)))
            {
                var key = Regex.Unescape(match.Groups[1].Value);
                if (key.Length > 1 && key.Any(char.IsLetter))
                {
                    found.TryAdd(key, relative);
                }
            }
        }

        foreach (var file in Directory.EnumerateFiles(sources, "*.xaml", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sources, file);
            if (IsBuildOutput(relative))
            {
                continue;
            }

            foreach (Match match in XamlKeyPattern.Matches(File.ReadAllText(file)))
            {
                found.TryAdd(System.Net.WebUtility.HtmlDecode(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value), relative);
            }
        }

        return found.Select(pair => (pair.Key, pair.Value)).ToList();
    }

    private static bool IsBuildOutput(string relative)
    {
        var parts = relative.Split(Path.DirectorySeparatorChar);
        return parts.Contains("bin") || parts.Contains("obj");
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "windows", "src")) && Directory.Exists(Path.Combine(directory.FullName, "FileFluss", "Resources")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("The repository root (windows/src beside FileFluss/Resources) is not above the test's folder.");
    }
}
