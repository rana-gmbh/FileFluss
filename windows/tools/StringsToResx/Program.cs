// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

// Generates the FileFluss.Core .resx files from the macOS Localizable.strings catalogues:
// the app's (FileFluss/Resources) and FileFlussCore's (Packages/.../Resources), merged in
// that order — the app wins when both define a key.
//
// The macOS .strings files stay the single source of truth for every string the two apps
// share: 700-odd keys across en/de/zh-Hans/zh-Hant already exist and are reviewed, and
// forking them would guarantee the two apps drift apart. Strings that only exist on
// Windows — "Show in Explorer", "Recycle Bin" — live in a second catalogue in the same
// format under windows/Localization/<lang>.lproj/Windows.strings, so they get the same four
// languages and the same tooling without putting Windows vocabulary into the Mac bundle.
//
// A key in the Windows catalogue that also exists in a Mac one *overrides* it. That is the
// precise fix for wording that is wrong on Windows ("Reveal in Finder"), where the blanket
// PlatformOverrides substitution below can only do a word swap.
//
// Ported from the NetFluss Windows generator. Three things are translated on the way
// across:
//
//   * Format specifiers. Cocoa writes %@ / %d / %.0f; .NET writes {0} / {1}. Replacement
//     is positional and in source order; a catalogue string using explicit %1$@ indexes
//     would need care.
//
//   * Platform vocabulary. A string such as "System Default follows the language selected
//     in macOS." is wrong on Windows. Every string matching a platform term is written to
//     a review report and, where an override exists, rewritten.
//
//   * Case collisions. .NET resource names fold case; macOS .strings keys do not. Keys
//     that differ only in capitalization are stored under a "~N" suffixed name and
//     reassembled at lookup time by Localization.L.
//
// Usage: dotnet run --project windows/tools/StringsToResx [-- --check | -- --missing]

using System.Text;
using System.Text.RegularExpressions;

namespace FileFluss.Tools.StringsToResx;

internal static class Program
{
    /// <summary>lproj folder → .resx culture suffix. The empty suffix is the neutral fallback.</summary>
    private static readonly (string Language, string Suffix)[] Languages =
    [
        ("en", ""),
        ("de", ".de"),
        ("zh-Hans", ".zh-Hans"),
        ("zh-Hant", ".zh-Hant"),
    ];

    private static readonly Regex EntryPattern = new(
        """^\s*"((?:[^"\\]|\\.)*)"\s*=\s*"((?:[^"\\]|\\.)*)"\s*;\s*$""",
        RegexOptions.CultureInvariant);

    private static readonly Regex SpecifierPattern = new(
        """%(?:\d+\$)?(?:[-+ #0]*\d*(?:\.\d+)?)?(?:@|ll[du]|l[du]|[dfsu@])""",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// .NET resource names fold case, and resgen answers two names differing only in
    /// capitalization by dropping the later one with a warning (MSB3568) — a green build
    /// silently missing a string in every language. Must stay equal to
    /// <c>CollisionLimit</c> in Localization.cs; LocalizationCaseCollisionTests pins both.
    /// </summary>
    private const string CollisionSuffix = "~";
    private const int CollisionLimit = 9;

    private static readonly string[] PlatformTerms =
    [
        "macOS", "Mac ", "the Mac", "menu bar", "Menu bar", "Menu Bar",
        "Keychain", "keychain", "Finder", "Dock", "System Settings", "AirDrop",
        "Mac's", "Trash", "Preview", "traffic", "Customize Toolbar", "⌘", "⌥", "⇧", "⌃",
    ];

    /// <summary>
    /// Word swaps for Mac strings that have no Windows.strings override. A last resort: the
    /// translations around the swapped word were written for the Mac, so every string that
    /// matters should carry an explicit override instead.
    /// </summary>
    private static readonly (string Term, string Substitute)[] PlatformOverrides =
    [
        ("macOS", "Windows"),
        ("System Settings", "Settings"),
        ("Keychain", "credential store"),
        ("Finder", "Explorer"),
    ];

    /// <summary>
    /// "⌘F" → "Ctrl+F", but only for a lone ⌘. A chord such as "⌥⌘I" has no mechanical
    /// Windows equivalent ("⌥Ctrl+I" means nothing on a PC keyboard), so it is left as is —
    /// the review report flags it and a Windows.strings override supplies the real shortcut.
    /// Cocoa writes modifiers in the order ⌃⌥⇧⌘, so a chord always has its other modifiers
    /// before the ⌘.
    /// </summary>
    private static readonly Regex LoneCommandKey = new("(?<![⌃⌥⇧])⌘(?=[A-Z0-9,.])", RegexOptions.CultureInvariant);

    private const string ResxHeader = """
        <?xml version="1.0" encoding="utf-8"?>
        <root>
          <xsd:schema id="root" xmlns="" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:msdata="urn:schemas-microsoft-com:xml-msdata">
            <xsd:import namespace="http://www.w3.org/XML/1998/namespace" />
            <xsd:element name="root" msdata:IsDataSet="true">
              <xsd:complexType>
                <xsd:choice maxOccurs="unbounded">
                  <xsd:element name="data">
                    <xsd:complexType>
                      <xsd:sequence>
                        <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
                        <xsd:element name="comment" type="xsd:string" minOccurs="0" msdata:Ordinal="2" />
                      </xsd:sequence>
                      <xsd:attribute name="name" type="xsd:string" use="required" msdata:Ordinal="1" />
                      <xsd:attribute name="type" type="xsd:string" msdata:Ordinal="3" />
                      <xsd:attribute name="mimetype" type="xsd:string" msdata:Ordinal="4" />
                      <xsd:attribute ref="xml:space" />
                    </xsd:complexType>
                  </xsd:element>
                  <xsd:element name="resheader">
                    <xsd:complexType>
                      <xsd:sequence>
                        <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
                      </xsd:sequence>
                      <xsd:attribute name="name" type="xsd:string" use="required" />
                    </xsd:complexType>
                  </xsd:element>
                </xsd:choice>
              </xsd:complexType>
            </xsd:element>
          </xsd:schema>
          <resheader name="resmimetype">
            <value>text/microsoft-resx</value>
          </resheader>
          <resheader name="version">
            <value>2.0</value>
          </resheader>
          <resheader name="reader">
            <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
          </resheader>
          <resheader name="writer">
            <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
          </resheader>

        """;

    private static int Main(string[] args)
    {
        var check = args.Contains("--check");
        var root = FindRepositoryRoot();
        if (root is null)
        {
            Console.Error.WriteLine("error: cannot find the repository root (no FileFluss/Resources above the current directory).");
            return 2;
        }

        string[] sourceDirectories =
        [
            Path.Combine(root, "FileFluss", "Resources"),
            Path.Combine(root, "Packages", "FileFlussCore", "Sources", "FileFlussCore", "Resources"),
        ];
        var windowsDirectory = Path.Combine(root, "windows", "Localization");
        var outputDirectory = Path.Combine(root, "windows", "src", "FileFluss.Core", "Resources");
        var reportPath = Path.Combine(outputDirectory, "platform-review.md");

        try
        {
            return Run(check, root, sourceDirectories, windowsDirectory, outputDirectory, reportPath);
        }
        catch (CatalogueException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    private static int Run(
        bool check,
        string root,
        string[] sourceDirectories,
        string windowsDirectory,
        string outputDirectory,
        string reportPath)
    {
        Directory.CreateDirectory(outputDirectory);

        var mac = Languages.ToDictionary(
            l => l.Language,
            l => Merge(sourceDirectories.Select(d => ParseStrings(Path.Combine(d, $"{l.Language}.lproj", "Localizable.strings")))));

        // Absent is fine — it is how the generator behaved before the Windows catalogue
        // existed, and keeping that path identical is what proved the port correct.
        var windows = Languages.ToDictionary(
            l => l.Language,
            l =>
            {
                var path = Path.Combine(windowsDirectory, $"{l.Language}.lproj", "Windows.strings");
                return File.Exists(path) ? ParseStrings(path) : new Catalogue();
            });

        // One name map shared by every language, derived in English-first order. Computing
        // it per language would let a German file that lists a colliding pair in the
        // opposite order pick the opposite winner, and the German lookup would then miss.
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var catalogue in mac.Values.Concat(windows.Values))
        {
            foreach (var (key, _) in catalogue.Entries)
            {
                var converted = ConvertSpecifiers(key);
                if (seen.Add(converted))
                {
                    ordered.Add(converted);
                }
            }
        }

        if (Environment.GetCommandLineArgs().Contains("--missing"))
        {
            return ReportMissing(root, seen);
        }

        var names = ResolveCaseCollisions(ordered);
        var collisions = names.Where(pair => pair.Key != pair.Value).ToList();

        var englishKeys = mac["en"].Keys.Concat(windows["en"].Keys).ToHashSet(StringComparer.Ordinal);
        var review = new List<string>();
        var generated = new List<(string Path, string Content)>();

        foreach (var (language, suffix) in Languages)
        {
            var macEntries = mac[language];
            var windowsEntries = windows[language];
            var keys = macEntries.Keys.Concat(windowsEntries.Keys).ToHashSet(StringComparer.Ordinal);

            var missing = englishKeys.Except(keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var extra = keys.Except(englishKeys).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            {
                Console.Error.WriteLine($"warning: {language} is missing {missing.Count} key(s): {string.Join(", ", missing.Take(3))}...");
            }

            if (extra.Count > 0)
            {
                Console.Error.WriteLine($"warning: {language} has {extra.Count} key(s) not in English: {string.Join(", ", extra.Take(3))}...");
            }

            var converted = new List<KeyValuePair<string, string>>();

            foreach (var (key, macValue) in macEntries.Entries)
            {
                if (windowsEntries.TryGetValue(key, out var windowsValue))
                {
                    // A deliberate Windows wording beats the blanket substitution: it was
                    // written for this platform, so it neither needs nor gets rewriting.
                    converted.Add(new(names[ConvertSpecifiers(key)], ConvertSpecifiers(windowsValue)));
                    review.Add(ReviewRow(language, key, "windows override", windowsValue));
                    continue;
                }

                var (value, wasOverridden) = ApplyPlatformOverrides(macValue);
                converted.Add(new(names[ConvertSpecifiers(key)], ConvertSpecifiers(value)));

                if (PlatformTerms.Any(value.Contains) || wasOverridden)
                {
                    review.Add(ReviewRow(language, key, wasOverridden ? "auto-rewritten" : "needs review", value));
                }
            }

            foreach (var (key, value) in windowsEntries.Entries)
            {
                if (!macEntries.ContainsKey(key))
                {
                    converted.Add(new(names[ConvertSpecifiers(key)], ConvertSpecifiers(value)));
                }
            }

            // Keys that differ only in their Cocoa specifier ("%d files" and "%@ files") become
            // the same .NET key ("{0} files"). They mean the same text, so one entry each: a
            // Windows wording wins, otherwise the first the catalogues list.
            var windowsNames = windowsEntries.Keys.Select(k => names[ConvertSpecifiers(k)]).ToHashSet(StringComparer.Ordinal);
            var byName = new Dictionary<string, int>(StringComparer.Ordinal);
            var unique = new List<KeyValuePair<string, string>>();
            foreach (var entry in converted)
            {
                if (byName.TryGetValue(entry.Key, out var index))
                {
                    if (windowsNames.Contains(entry.Key) && unique[index].Value != entry.Value && windowsEntries.Entries.Any(w => names[ConvertSpecifiers(w.Key)] == entry.Key && ConvertSpecifiers(w.Value) == entry.Value))
                    {
                        unique[index] = entry;
                    }

                    continue;
                }

                byName[entry.Key] = unique.Count;
                unique.Add(entry);
            }

            converted = unique;

            // Defence in depth: whatever the map said, never hand resgen a file it would
            // have to silently drop entries from.
            var folded = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, _) in converted)
            {
                var fold = CaseFold(name);
                if (folded.TryGetValue(fold, out var clash) && clash != name)
                {
                    throw new CatalogueException(
                        $"error: {language} would emit '{name}' and '{clash}', which .NET treats as the same resource name.");
                }

                folded[fold] = name;
            }

            generated.Add((Path.Combine(outputDirectory, $"Strings{suffix}.resx"), RenderResx(converted)));
        }

        var report = new List<string>
        {
            "# Platform vocabulary review",
            "",
            "Generated by `windows/tools/StringsToResx`. Every string below mentions a",
            "platform-specific concept that may not be correct on Windows. Rows marked",
            "*auto-rewritten* were changed by `PlatformOverrides`; *windows override* rows use the",
            "wording from windows/Localization/*.lproj/Windows.strings; rows marked *needs review*",
            "were left alone and want a human decision.",
            "",
            "| Language | Key | Status | Value |",
            "| --- | --- | --- | --- |",
        };
        report.AddRange(review);
        report.Add(string.Empty);
        generated.Add((reportPath, string.Join("\n", report)));

        var stale = new List<string>();
        foreach (var (path, content) in generated)
        {
            // Compared with line endings normalised: git may check these out with CRLF on
            // a machine that overrides the repository's eol=lf, and that is not staleness.
            var existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n") : null;
            if (existing == content)
            {
                continue;
            }

            stale.Add(path);
            if (!check)
            {
                File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }

        if (check && stale.Count > 0)
        {
            Console.Error.WriteLine("error: generated resources are stale: " +
                                    string.Join(", ", stale.Select(p => Path.GetRelativePath(root, p))));
            Console.Error.WriteLine("run: dotnet run --project windows/tools/StringsToResx");
            return 1;
        }

        Console.WriteLine(
            $"{(check ? "checked" : "wrote")} {generated.Count - 1} .resx file(s) from {englishKeys.Count} English keys; " +
            $"{review.Count} platform review row(s); {collisions.Count} case collision(s) disambiguated");

        foreach (var (key, name) in collisions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  case collision: '{key}' stored as '{name}'");
        }

        return 0;
    }

    private static string? FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "FileFluss", "Resources")) &&
                Directory.Exists(Path.Combine(directory.FullName, "windows")))
            {
                return directory.FullName;
            }
        }

        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "FileFluss", "Resources")))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    private static string ReviewRow(string language, string key, string status, string value)
        => $"| `{language}` | `{key.Replace("|", "\\|")}` | {status} | {value.Replace("|", "\\|")} |";

    /// <summary>
    /// The same sequence of replacements as the Python original, in the same order — the
    /// order is observable for a value such as <c>\\n</c>, and matching it is what keeps
    /// the output byte-identical.
    /// </summary>
    private static string Unescape(string text)
        => text.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\\", "\\");

    /// <summary>
    /// Calls whose first string argument is a localization key the callee looks up — L itself,
    /// the connect form's Info/Warning/Link rows, and the help book's P/H/B/T blocks. (No
    /// "Header": in FileFluss that is the WebDAV server reading an HTTP header, not a key.)
    /// </summary>
    private static readonly Regex KeyCallPattern = new(
        """(?<![A-Za-z0-9_])(?:L|Tile|InfoCard|DetailRow|Option|SectionTitle|Section|SliceColumn|Requirement|Info|Warning|Link|P|H|B|T)\(\s*"((?:[^"\\]|\\.)*)"(?=\s*[,)])""",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Connect-form keys that are not a first argument: a field's label (after its kind), a
    /// sign-in mode's title (after its id), and the Placeholder/Help initialisers.
    /// </summary>
    private static readonly Regex ConnectKeyPattern = new(
        """(?:ConnectFieldKind\.\w+\s*,\s*|ConnectMode\(\s*"[^"]*"\s*,\s*|(?<![A-Za-z0-9_])(?:Placeholder|Help)\s*=\s*)"((?:[^"\\]|\\.)*)"(?=\s*[,)}\r\n])""",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Keys that reach L() one step removed: the arms of a ternary (<c>L(on ? "a" : "b")</c>)
    /// and the first element of a tuple table (<c>("Import…", VpnProtocol.OpenVpn)</c>).
    /// Only sentence-like literals — a capital letter and a space — to keep ids out, and not
    /// the first argument of calls that never take a key (log lines, clipboard formats, …).
    /// </summary>
    private static readonly Regex SecondaryKeyPattern = new(
        """(?:[?:]\s*|(?<!(?:Log|NotFound|SetData|GetData|FontFamily|Contains|StartsWith|EndsWith|Exception)\s*)\(\s*)"([A-Z][^"\\]*\s[^"\\]*)"(?=\s*[,):?])""",
        RegexOptions.CultureInvariant);

    private static readonly Regex XamlKeyPattern = new(
        """\{u:Loc\s+(?:'([^']*)'|([^}\s]+))\}""",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// "--missing": the keys the Windows sources look up that neither catalogue has, written
    /// as Windows.strings lines ("{0}" back to "%@") ready to paste into
    /// windows/Localization/en.lproj/Windows.strings. Literal keys only; a key computed at
    /// run time cannot be found this way and still needs a reviewer's eye.
    /// </summary>
    private static int ReportMissing(string root, HashSet<string> known)
    {
        var sources = Path.Combine(root, "windows", "src");
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(sources, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sources, file);
            if (relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                relative.Contains(".Tests", StringComparison.Ordinal) || relative.StartsWith("FileFluss.Core.Tests", StringComparison.Ordinal))
            {
                continue;
            }

            // Comment lines quote example values ("Dropbox (Robert)") that are not keys.
            var text = string.Join('\n', File.ReadAllLines(file).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            var matches = KeyCallPattern.Matches(text).Concat(ConnectKeyPattern.Matches(text)).Concat(
                text.Contains("L(", StringComparison.Ordinal) ? SecondaryKeyPattern.Matches(text) : Enumerable.Empty<Match>());
            foreach (var match in matches)
            {
                var key = Regex.Unescape(match.Groups[1].Value);
                if (key.Length > 1 && key.Any(char.IsLetter) && !known.Contains(key))
                {
                    missing.Add(key);
                }
            }
        }

        // XAML looks keys up through the {u:Loc Key} / {u:Loc 'Two words'} markup extension.
        foreach (var file in Directory.EnumerateFiles(sources, "*.xaml", SearchOption.AllDirectories))
        {
            foreach (Match match in XamlKeyPattern.Matches(File.ReadAllText(file)))
            {
                var key = System.Net.WebUtility.HtmlDecode(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
                if (!known.Contains(key))
                {
                    missing.Add(key);
                }
            }
        }

        foreach (var key in missing)
        {
            var cocoa = Regex.Replace(key.Replace("%", "%%", StringComparison.Ordinal), @"\{\d+\}", "%@").Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
            Console.WriteLine($"\"{cocoa}\" = \"{cocoa}\";");
        }

        Console.Error.WriteLine($"{missing.Count} key(s) missing.");
        return 0;
    }

    private static readonly Regex LiteralPercentOrSpecifier = new("%%|" + SpecifierPattern, RegexOptions.CultureInvariant);

    /// <summary>Cocoa positional specifiers → composite format items, in source order.</summary>
    private static string ConvertSpecifiers(string text)
    {
        var index = 0;

        // Braces are literal in .NET composite formatting and must be doubled first,
        // otherwise a string containing "{" would throw at runtime.
        text = text.Replace("{", "{{").Replace("}", "}}");

        // "%%" is Cocoa's literal percent sign ("100%% scaling"); without it, "% s" in
        // "100% scaling" reads as a space-flagged %s and becomes a placeholder.
        return LiteralPercentOrSpecifier.Replace(text, match => match.Value == "%%" ? "%" : "{" + index++ + "}");
    }

    /// <summary>
    /// Several catalogues as one, in order: the first to define a key wins. The app and
    /// FileFlussCore catalogues overlap on a few keys, which is normal and not an error.
    /// </summary>
    private static Catalogue Merge(IEnumerable<Catalogue> catalogues)
    {
        var merged = new Catalogue();
        foreach (var catalogue in catalogues)
        {
            foreach (var (key, value) in catalogue.Entries)
            {
                merged.TryAdd(key, value);
            }
        }

        return merged;
    }

    private static Catalogue ParseStrings(string path)
    {
        if (!File.Exists(path))
        {
            return new Catalogue();
        }

        var catalogue = new Catalogue();
        var inBlockComment = false;
        var lineNumber = 0;

        foreach (var raw in File.ReadAllText(path, Encoding.UTF8).ReplaceLineEndings("\n").Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();

            if (inBlockComment)
            {
                if (line.Contains("*/", StringComparison.Ordinal))
                {
                    inBlockComment = false;
                }

                continue;
            }

            if (line.StartsWith("/*", StringComparison.Ordinal))
            {
                if (!line.Contains("*/", StringComparison.Ordinal))
                {
                    inBlockComment = true;
                }

                continue;
            }

            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var match = EntryPattern.Match(line);
            if (!match.Success)
            {
                throw new CatalogueException($"{path}:{lineNumber}: cannot parse: '{raw}'");
            }

            var key = Unescape(match.Groups[1].Value);
            var value = Unescape(match.Groups[2].Value);
            if (!catalogue.TryAdd(key, value))
            {
                // The macOS catalogues repeat a few keys (e.g. "Sync" under two comment groups);
                // Apple's tooling keeps the first, and so does this.
                Console.Error.WriteLine($"warning: {path}:{lineNumber}: duplicate key '{key}' ignored");
            }
        }

        return catalogue;
    }

    /// <summary>
    /// Maps each converted key to the .resx resource name it is stored under: identity for
    /// everything except keys that fold together with an earlier one.
    /// </summary>
    private static Dictionary<string, string> ResolveCaseCollisions(List<string> keys)
    {
        var groups = new List<List<string>>();
        var groupIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var fold = CaseFold(key);
            if (!groupIndex.TryGetValue(fold, out var index))
            {
                index = groups.Count;
                groupIndex[fold] = index;
                groups.Add([]);
            }

            groups[index].Add(key);
        }

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = keys.Select(CaseFold).ToHashSet(StringComparer.Ordinal);

        foreach (var members in groups)
        {
            // First in English source order wins the bare name, so the common case keeps a
            // resource name that is readable and diffable against the .strings catalogue.
            names[members[0]] = members[0];

            for (var i = 1; i < members.Count; i++)
            {
                var ordinal = i + 1;
                var key = members[i];
                if (ordinal > CollisionLimit)
                {
                    throw new CatalogueException(
                        $"error: {members.Count} keys fold to '{members[0]}', more than the {CollisionLimit} that " +
                        "Localization.L probes for. Raise CollisionLimit here and in Localization.cs together.");
                }

                var name = $"{key}{CollisionSuffix}{ordinal}";
                if (!taken.Add(CaseFold(name)))
                {
                    throw new CatalogueException(
                        $"error: disambiguating '{key}' produces '{name}', which collides with a real key. " +
                        "Rename the offending key in the .strings catalogues.");
                }

                names[key] = name;
            }
        }

        return names;
    }

    private static string CaseFold(string text) => text.ToUpperInvariant().ToLowerInvariant();

    private static (string Value, bool Overridden) ApplyPlatformOverrides(string value)
    {
        var replaced = value;
        foreach (var (term, substitute) in PlatformOverrides)
        {
            replaced = replaced.Replace(term, substitute, StringComparison.Ordinal);
        }

        replaced = LoneCommandKey.Replace(replaced, "Ctrl+");
        return (replaced, replaced != value);
    }

    /// <summary>xml.sax.saxutils.escape: ampersand first, then the angle brackets.</summary>
    private static string Escape(string text)
        => text.Replace("&", "&amp;").Replace(">", "&gt;").Replace("<", "&lt;");

    /// <summary>
    /// Attribute escaping. Several keys quote a UI label, and an unescaped double
    /// quote inside a double-quoted attribute produces a .resx that will not parse.
    /// </summary>
    private static string EscapeAttribute(string text)
        => Escape(text).Replace("\"", "&quot;").Replace("'", "&apos;").Replace("\n", "&#10;").Replace("\t", "&#9;");

    private static string RenderResx(List<KeyValuePair<string, string>> entries)
    {
        var builder = new StringBuilder(ResxHeader.ReplaceLineEndings("\n"));
        foreach (var (key, value) in entries)
        {
            // xml:space="preserve" keeps leading/trailing spaces, which several strings rely on.
            builder.Append("  <data name=\"").Append(EscapeAttribute(key)).Append("\" xml:space=\"preserve\">\n");
            builder.Append("    <value>").Append(Escape(value)).Append("</value>\n");
            builder.Append("  </data>\n");
        }

        builder.Append("</root>\n");
        return builder.ToString();
    }

    /// <summary>An ordered key → value catalogue; insertion order is the source order.</summary>
    private sealed class Catalogue
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public List<KeyValuePair<string, string>> Entries { get; } = [];

        public IEnumerable<string> Keys => Entries.Select(entry => entry.Key);

        public bool TryAdd(string key, string value)
        {
            if (!_values.TryAdd(key, value))
            {
                return false;
            }

            Entries.Add(new(key, value));
            return true;
        }

        public bool ContainsKey(string key) => _values.ContainsKey(key);

        public bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);
    }

    private sealed class CatalogueException(string message) : Exception(message);
}
