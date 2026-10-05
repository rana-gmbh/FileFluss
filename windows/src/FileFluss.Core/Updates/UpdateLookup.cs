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

    /// <summary>Opens <paramref name="path"/> against changes and hashes it through that handle; deletes it and throws on a mismatch.</summary>
    internal static VerifiedInstaller Open(string path, string expectedSha256)
    {
        var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        try
        {
            var actual = Convert.ToHexStringLower(SHA256.HashData(hold));
            if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
            {
                throw new UpdateVerificationException($"{System.IO.Path.GetFileName(path)} changed after it was verified.");
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
/// <para>Both apps release from the same repository, and the Mac's "latest release" (which
/// its Sparkle feed, <c>releases/latest/download/appcast.xml</c>, depends on) would hand a
/// Windows user a macOS DMG whenever the Mac shipped last. Windows releases are tagged
/// <c>win-vX.Y.Z</c> and created with <c>--latest=false</c>, so the release list is filtered
/// by that prefix and only a Windows release can ever be offered.</para>
///
/// <para>The checksum is not optional. Sparkle refuses an update without a valid EdDSA
/// signature; here, an installer whose bytes do not match the hash the release's
/// SHA256SUMS.txt lists for it is deleted, never run.</para>
/// </summary>
public static class UpdateLookup
{
    public const string TagPrefix = "win-v";

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

    /// <summary>The newest non-draft, non-prerelease Windows release newer than <paramref name="currentVersion"/>.</summary>
    public static AvailableUpdate? Newest(string releasesJson, string currentVersion, string? architecture = null)
    {
        architecture ??= CurrentArchitecture;
        using var document = JsonDocument.Parse(releasesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        AvailableUpdate? best = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object || Bool(release, "draft") || Bool(release, "prerelease"))
            {
                continue;
            }

            var tag = String(release, "tag_name");
            if (tag is null || !tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The version names a download folder and asset files: digits, letters, '.', '-', '+' only.
            var version = tag[TagPrefix.Length..];
            if (version.Length is 0 or > MaxVersionLength || !version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+') ||
                !IsNewer(version, currentVersion) || (best is not null && !IsNewer(version, best.Version)))
            {
                continue;
            }

            if (!Uri.TryCreate(String(release, "html_url"), UriKind.Absolute, out var page) || !IsInRepository(page, RepositoryPrefix))
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

    /// <summary>Numeric, component-wise; missing components count as zero, as on macOS.</summary>
    public static bool IsNewer(string latest, string current)
    {
        static int[] Parts(string version) =>
        [
            .. version.Split('.', '-', '+')
                .TakeWhile(part => int.TryParse(part, out _))
                .Select(int.Parse),
        ];

        var a = Parts(latest);
        var b = Parts(current);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y)
            {
                return x > y;
            }
        }

        return false;
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
    /// </summary>
    public static async Task<VerifiedInstaller> DownloadVerifiedInstallerAsync(
        AvailableUpdate update,
        string folder,
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
            var verified = VerifiedInstaller.Open(path, expected);
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
