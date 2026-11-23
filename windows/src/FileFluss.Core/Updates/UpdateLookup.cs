// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace FileFluss.Core.Updates;

/// <summary>A newer Windows release than the running one.</summary>
/// <param name="Installer">The setup for this architecture, which the in-app updater runs.</param>
/// <param name="Portable">The portable zip for this architecture, for copies not installed by the setup.</param>
/// <param name="Checksums">The release's SHA256SUMS.txt; without it nothing is installed.</param>
public sealed record AvailableUpdate(string Version, string ReleaseNotes, Uri ReleasePage, Uri? Installer, Uri? Portable, Uri? Checksums);

/// <summary>An update that could not be matched against the release's SHA256SUMS.txt.</summary>
public sealed class UpdateVerificationException(string message) : Exception(message);

/// <summary>
/// A downloaded installer that matched its checksum, held open (others may read and execute
/// it, but not change, rename or delete it) until disposed — so nothing can swap the file
/// between the check and the launch.
/// </summary>
public sealed class VerifiedInstaller : IDisposable
{
    private readonly FileStream _hold;

    private VerifiedInstaller(string path, FileStream hold)
    {
        Path = path;
        _hold = hold;
    }

    public string Path { get; }

    /// <summary>
    /// Opens <paramref name="path"/> against changes, then hashes it and checks its code signature
    /// through that handle; deletes it and throws when either fails.
    /// </summary>
    internal static VerifiedInstaller Open(string path, string expectedSha256, InstallerSignatureCheck verifySignature)
    {
        var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        try
        {
            var actual = Convert.ToHexStringLower(SHA256.HashData(hold));
            if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
            {
                throw new UpdateVerificationException($"{System.IO.Path.GetFileName(path)} changed after it was verified.");
            }

            // Both must hold: the checksum ties the file to the release, the signature to Rana
            // GmbH (see UpdateSigner) — so replacing a release's files isn't enough to ship code.
            if (verifySignature(path, hold.SafeFileHandle) is { } refusal)
            {
                throw new UpdateVerificationException($"{System.IO.Path.GetFileName(path)} is not signed by {UpdateSigner.PublisherName}: {refusal}");
            }

            return new VerifiedInstaller(path, hold);
        }
        catch
        {
            hold.Dispose();
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    public void Dispose() => _hold.Dispose();
}

/// <summary>
/// Asks GitHub for the newest FileFluss for Windows release — the Windows counterpart of
/// Sparkle's appcast check on macOS, ported from the NetFluss Windows updater.
///
/// <para>Both apps release from the same repository. Since they ship together, a release is
/// tagged <c>vX.Y.Z</c> and carries the Mac's DMG and the Windows installers side by side.
/// Earlier releases were tagged <c>vX.Y</c> for the Mac alone and <c>win-vX.Y.Z</c> for
/// Windows alone, and both kinds are still in the list. So a <c>win-v</c> release always
/// counts, and a <c>v</c> release only when it carries Windows files of its version: a
/// Mac-only "v1.6" would otherwise look newer than "1.6.0-beta.2" and be offered with
/// nothing to install.</para>
///
/// <para>The checksum is not optional. Sparkle refuses an update without a valid EdDSA
/// signature; here, an installer whose bytes do not match the hash the release's
/// SHA256SUMS.txt lists for it is deleted, never run — and so is one that matches but
/// doesn't carry Rana GmbH's Authenticode signature (<see cref="UpdateSigner"/>), because the
/// checksum list comes from the same release as the installer.</para>
/// </summary>
public static class UpdateLookup
{
    /// <summary>Releases of the Windows app alone, as the betas before the joint releases were tagged.</summary>
    public const string WindowsTagPrefix = "win-v";

    /// <summary>Releases of both apps — and, before them, of the Mac app alone.</summary>
    public const string SharedTagPrefix = "v";

    /// <summary>Longest first: "win-v1.0.0" must not be read as a "v" tag.</summary>
    private static readonly string[] TagPrefixes = [WindowsTagPrefix, SharedTagPrefix];

    public const string ChecksumsFileName = "SHA256SUMS.txt";

    /// <summary>How often the automatic check runs — once a day, like Sparkle's default.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    public static readonly Uri ReleasesPage = new("https://github.com/rana-gmbh/FileFluss/releases");

    private static readonly Uri ReleasesApi = new("https://api.github.com/repos/rana-gmbh/FileFluss/releases?per_page=30");

    /// <summary>
    /// Every link taken from the release list must point into this repository on github.com:
    /// the release page under <see cref="RepositoryPrefix"/>, assets under
    /// <c>releases/download/&lt;tag&gt;/</c>. The checksum list is only as trustworthy as the
    /// place it comes from, so neither it nor the installer may come from anywhere else.
    /// </summary>
    private const string RepositoryPrefix = "https://github.com/rana-gmbh/FileFluss/";

    private const int MaxVersionLength = 32;

    /// <summary>"x64" or "arm64": the asset suffix for this PC. An x64 build emulated on Arm is offered the native Arm build.</summary>
    public static string CurrentArchitecture => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>Asset names, as windows/Packaging/build-release.ps1 writes them.</summary>
    public static string InstallerFileName(string version, string architecture) => $"FileFluss-Setup-{version}-{architecture}.exe";

    public static string PortableFileName(string version, string architecture) => $"FileFluss-{version}-{architecture}-portable.zip";

    public static async Task<AvailableUpdate?> FetchAsync(
        string currentVersion,
        HttpMessageHandler? handler = null,
        string? architecture = null,
        CancellationToken cancellation = default)
    {
        using var http = CreateClient(handler, TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("FileFluss-Windows", currentVersion));

        using var response = await http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub returned HTTP {(int)response.StatusCode}.");
        }

        var json = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        return Newest(json, currentVersion, architecture);
    }

    /// <summary>
    /// The newest non-draft Windows release newer than <paramref name="currentVersion"/>. Pre-releases
    /// (betas) are offered only to a build that is itself a pre-release: a beta tester gets the next
    /// beta and then the final release, everyone else only final releases.
    /// </summary>
    public static AvailableUpdate? Newest(string releasesJson, string currentVersion, string? architecture = null)
    {
        architecture ??= CurrentArchitecture;
        using var document = JsonDocument.Parse(releasesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var includePrereleases = IsPrerelease(currentVersion);
        AvailableUpdate? best = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object || Bool(release, "draft") || (Bool(release, "prerelease") && !includePrereleases))
            {
                continue;
            }

            var tag = String(release, "tag_name");
            var prefix = tag is null ? null : TagPrefixOf(tag);
            if (tag is null || prefix is null)
            {
                continue;
            }

            // The version names a download folder and asset files: digits, letters, '.', '-', '+' only.
            var version = tag[prefix.Length..];
            if (version.Length is 0 or > MaxVersionLength || !version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+') ||
                !IsNewer(version, currentVersion) || (best is not null && !IsNewer(version, best.Version)))
            {
                continue;
            }

            if (!Uri.TryCreate(String(release, "html_url"), UriKind.Absolute, out var page) || !IsInRepository(page, RepositoryPrefix))
            {
                continue;
            }

            if (prefix == SharedTagPrefix && !CarriesWindowsFiles(release, version))
            {
                continue;
            }

            var downloads = RepositoryPrefix + "releases/download/" + tag + "/";
            best = new AvailableUpdate(
                version,
                String(release, "body") ?? string.Empty,
                page,
                Asset(release, InstallerFileName(version, architecture), downloads),
                Asset(release, PortableFileName(version, architecture), downloads),
                Asset(release, ChecksumsFileName, downloads));
        }

        return best;
    }

    /// <summary>
    /// The prefix a release's tag starts with, when it is one the Windows app reads: "win-v"
    /// or "v" followed by a digit. Anything else — "windows-tools", "vintage" — is not a release
    /// of the app.
    /// </summary>
    private static string? TagPrefixOf(string tag)
    {
        foreach (var prefix in TagPrefixes)
        {
            if (tag.Length > prefix.Length && tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && char.IsAsciiDigit(tag[prefix.Length]))
            {
                return prefix;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a release lists an installer or a portable zip of this version, for any
    /// architecture — what tells a joint release from one the Mac made alone. The architecture
    /// is left out on purpose: a joint release missing this PC's build behaves like a Windows
    /// release that does, offered with nothing to install rather than not at all.
    /// </summary>
    private static bool CarriesWindowsFiles(JsonElement release, string version)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object || String(asset, "name") is not { } name)
            {
                continue;
            }

            if ((name.StartsWith($"FileFluss-Setup-{version}-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ||
                (name.StartsWith($"FileFluss-{version}-", StringComparison.OrdinalIgnoreCase) && name.EndsWith("-portable.zip", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A version with a pre-release part ("1.6.0-beta.1"); build metadata ("+commit") doesn't count.</summary>
    public static bool IsPrerelease(string version)
    {
        var metadata = version.IndexOf('+', StringComparison.Ordinal);
        return (metadata >= 0 ? version[..metadata] : version).Contains('-', StringComparison.Ordinal);
    }

    /// <summary>
    /// Semantic-version order: numeric, component-wise, missing components counting as zero
    /// as on macOS; then a pre-release sorts below its release, so 1.6.0-beta.1 &lt;
    /// 1.6.0-beta.2 &lt; 1.6.0. Build metadata ("+commit") is ignored.
    /// </summary>
    public static bool IsNewer(string latest, string current) => Compare(latest, current) > 0;

    private static int Compare(string a, string b)
    {
        static (int[] Core, string[]? Pre) Parse(string version)
        {
            var metadata = version.IndexOf('+', StringComparison.Ordinal);
            if (metadata >= 0)
            {
                version = version[..metadata];
            }

            var dash = version.IndexOf('-', StringComparison.Ordinal);
            var core = dash >= 0 ? version[..dash] : version;
            int[] numbers = [.. core.Split('.').TakeWhile(part => int.TryParse(part, out _)).Select(int.Parse)];
            return (numbers, dash >= 0 ? version[(dash + 1)..].Split('.') : null);
        }

        var (coreA, preA) = Parse(a);
        var (coreB, preB) = Parse(b);
        for (var i = 0; i < Math.Max(coreA.Length, coreB.Length); i++)
        {
            var x = i < coreA.Length ? coreA[i] : 0;
            var y = i < coreB.Length ? coreB[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        // Same release: a pre-release is older than the release itself.
        if (preA is null || preB is null)
        {
            return (preA is null ? 1 : 0) - (preB is null ? 1 : 0);
        }

        // Identifier by identifier: numbers numerically and below words, words by ordinal;
        // a longer list wins when the shared part is equal (beta < beta.1).
        for (var i = 0; i < Math.Min(preA.Length, preB.Length); i++)
        {
            var numericA = int.TryParse(preA[i], out var na);
            var numericB = int.TryParse(preB[i], out var nb);
            var order = (numericA, numericB) switch
            {
                (true, true) => na.CompareTo(nb),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(preA[i], preB[i]),
            };

            if (order != 0)
            {
                return Math.Sign(order);
            }
        }

        return preA.Length.CompareTo(preB.Length);
    }

    /// <summary>
    /// Whether the automatic check should run now: switched on, and the last successful check
    /// is a day old (or in the future — a clock that was set back must not stop checks).
    /// </summary>
    public static bool IsDue(bool automaticChecks, DateTimeOffset? lastCheck, DateTimeOffset now)
        => automaticChecks && (lastCheck is not { } last || now - last >= CheckInterval || last > now);

    /// <summary>
    /// The SHA-256 a SHA256SUMS.txt lists for a file ("&lt;hex&gt;  &lt;name&gt;", the sha256sum
    /// format), lowercased; null when the file is not listed or the line is malformed.
    /// </summary>
    public static string? ExpectedHash(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].Trim().TrimStart('*').Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
                parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit))
            {
                return parts[0].ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>
    /// Downloads the update's installer into <paramref name="folder"/> and checks it against
    /// the release's SHA256SUMS.txt. Returns the path of the verified installer; throws
    /// <see cref="UpdateVerificationException"/> when there is nothing to verify against or
    /// the bytes do not match — in which case nothing is left behind.
    ///
    /// <para>The download lands under a <c>.partial</c> name and only gets the installer's
    /// name once verified, so an unverified file never sits there looking runnable. The
    /// result holds the installer open against writes, renames and deletes, and was hashed
    /// again through that handle: the file that gets launched is the file that was checked.
    /// Dispose it once the setup has been started.</para>
    ///
    /// <para>Through the same handle, <paramref name="verifySignature"/> must accept the
    /// installer's code signature (Rana GmbH, see <see cref="UpdateSigner"/>); a file that
    /// matches its checksum but isn't signed by Rana GmbH is deleted like a mismatch.</para>
    /// </summary>
    public static async Task<VerifiedInstaller> DownloadVerifiedInstallerAsync(
        AvailableUpdate update,
        string folder,
        InstallerSignatureCheck verifySignature,
        IProgress<double>? progress = null,
        HttpMessageHandler? handler = null,
        CancellationToken cancellation = default)
    {
        if (update.Installer is not { } installer || update.Checksums is not { } checksums)
        {
            throw new UpdateVerificationException("The release has no installer and checksum list for this PC.");
        }

        using var http = CreateClient(handler, TimeSpan.FromMinutes(10));
        var fileName = Path.GetFileName(Uri.UnescapeDataString(installer.AbsolutePath));
        if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new UpdateVerificationException("The installer's file name is not usable.");
        }

        var sums = await http.GetStringAsync(checksums, cancellation).ConfigureAwait(false);
        if (ExpectedHash(sums, fileName) is not { } expected)
        {
            throw new UpdateVerificationException($"{fileName} is not listed in {ChecksumsFileName}.");
        }

        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        var partial = path + ".partial";
        try
        {
            string actual;
            using (var response = await http.GetAsync(installer, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
                await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    long read = 0;
                    int count;
                    while ((count = await source.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
                    {
                        hash.AppendData(buffer, 0, count);
                        await target.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
                        read += count;
                        if (total > 0)
                        {
                            progress?.Report(Math.Min(1.0, (double)read / total));
                        }
                    }
                }

                actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new UpdateVerificationException($"{fileName} does not match its SHA-256 in {ChecksumsFileName}.");
            }

            File.Move(partial, path, overwrite: true);
            var verified = VerifiedInstaller.Open(path, expected, verifySignature);
            progress?.Report(1.0);
            return verified;
        }
        finally
        {
            TryDelete(partial);
        }
    }

    private static HttpClient CreateClient(HttpMessageHandler? handler, TimeSpan timeout)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = timeout;
        if (http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FileFluss-Windows");
        }

        return http;
    }

    /// <summary>An asset by its exact name, downloaded from this release of this repository over HTTPS only.</summary>
    private static Uri? Asset(JsonElement release, string name, string downloadsPrefix)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind == JsonValueKind.Object &&
                string.Equals(String(asset, "name"), name, StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(String(asset, "browser_download_url"), UriKind.Absolute, out var url) && IsInRepository(url, downloadsPrefix))
            {
                return url;
            }
        }

        return null;
    }

    /// <summary>
    /// HTTPS on github.com's default port, no user info, and (after <see cref="Uri"/> has
    /// resolved any "..") a path below <paramref name="prefix"/>.
    /// </summary>
    internal static bool IsInRepository(Uri url, string prefix)
        => url.Scheme == Uri.UriSchemeHttps
           && url.IsDefaultPort
           && url.UserInfo.Length == 0
           && string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase)
           && url.AbsoluteUri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
