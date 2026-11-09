// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Security.Cryptography;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Net;
using SharpCompress.Archives.Rar;
using SharpCompress.Readers;

namespace FileFluss.Core.Archives;

/// <summary>
/// RARLAB's command-line rar tool: where it is, and fetching it (macOS <c>RarInstaller</c>).
/// RAR's compression is proprietary — there is no free encoder — so it is the only way to
/// make a RAR archive.
///
/// <para>On macOS FileFluss downloads RARLAB's command-line tarball. RARLAB publishes no
/// such package for Windows: the console <c>Rar.exe</c> ships inside the WinRAR installer,
/// which is itself a RAR self-extracting archive. So FileFluss downloads that installer from
/// rarlab.com, checks it against a pinned SHA-256, and <b>reads</b> it as an archive to take
/// out <c>Rar.exe</c> and its licence — the installer is never run, nothing is installed
/// system-wide, and no administrator rights are needed. The pin is version, URL and hash
/// together (the hash is the one winget publishes for the same file); when RARLAB stops
/// serving 7.23 the download fails cleanly and the user is pointed at the download page.</para>
/// </summary>
public static class RarTool
{
    public const string Version = "7.23";

    public static readonly Uri DownloadPage = new("https://www.rarlab.com/download.htm");

    internal static readonly Uri InstallerUrl = new("https://www.rarlab.com/rar/winrar-x64-723.exe");

    internal const string InstallerSha256 = "8ff0daf3ed564cc743c0e23ff2e253997ffc74460f9673f0b6dd037b2db4ce7b";

    private const string ToolName = "Rar.exe";

    /// <summary>The files taken out of the installer: the tool, and what RARLAB ships beside it.</summary>
    private static readonly string[] KeptFiles = [ToolName, "License.txt", "Rar.txt", "WhatsNew.txt"];

    /// <summary>Where FileFluss keeps its own copy: <c>%LOCALAPPDATA%\FileFluss\Tools\rar</c> unless the app says otherwise.</summary>
    public static string InstallDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileFluss", "Tools", "rar");

    public static string InstalledTool => Path.Combine(InstallDirectory, ToolName);

    /// <summary>Extra places to look, for tests. Null: FileFluss's copy, then WinRAR's own install folders.</summary>
    internal static IReadOnlyList<string>? SearchPathsOverride { get; set; }

    /// <summary>The rar tool, if one is installed: FileFluss's own copy first, then WinRAR's install folders.</summary>
    public static string? Find()
    {
        IEnumerable<string> candidates = SearchPathsOverride ?? DefaultCandidates();
        return candidates.FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> DefaultCandidates()
    {
        yield return InstalledTool;
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var root = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(root))
            {
                yield return Path.Combine(root, "WinRAR", ToolName);
            }
        }
    }

    /// <summary>Downloads, verifies and unpacks the tool. Returns its path.</summary>
    public static async Task<string> InstallAsync(HttpMessageHandler? handler = null, CancellationToken cancellationToken = default)
    {
        var staging = IO.StagingLocation.StagingDirectory("rar-install");
        try
        {
            var installer = Path.Combine(staging, "winrar.exe.download");
            using (var http = handler is null ? CloudHttp.Create() : new HttpClient(handler, disposeHandler: false))
            {
                HttpResponseMessage response;
                try
                {
                    response = await http.GetAsync(InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException e)
                {
                    throw Failure(Localization.L("The rar tool could not be downloaded: {0}", e.Message));
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw Failure(Localization.L("The rar tool could not be downloaded: {0}", $"{(int)response.StatusCode} {response.ReasonPhrase}"));
                    }

                    await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using var file = File.Create(installer);
                    await body.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }
            }

            // Checked before anything is unpacked, let alone run.
            string digest;
            await using (var file = File.OpenRead(installer))
            {
                digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
            }

            if (!string.Equals(digest, InstallerSha256, StringComparison.Ordinal))
            {
                SupportLogger.Shared.Log($"rar install: checksum mismatch ({digest})", "archive", LogLevel.Error);
                throw Failure(Localization.L("The downloaded rar tool did not match its expected fingerprint, so it was discarded. You can download it from rarlab.com instead."));
            }

            var unpacked = Path.Combine(staging, "rar");
            Directory.CreateDirectory(unpacked);
            ExtractTool(installer, unpacked, cancellationToken);
            if (!File.Exists(Path.Combine(unpacked, ToolName)))
            {
                throw Failure(Localization.L("The download did not contain the rar tool."));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(InstallDirectory)!);
            if (Directory.Exists(InstallDirectory))
            {
                Directory.Delete(InstallDirectory, recursive: true);
            }

            Directory.Move(unpacked, InstallDirectory);
            SupportLogger.Shared.Log($"rar {Version} installed at {InstalledTool}", "archive");
            return InstalledTool;
        }
        finally
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Reads the (already verified) installer as the RAR self-extracting archive it is and
    /// writes the kept files, by their bare names, into <paramref name="destination"/>.
    /// </summary>
    internal static void ExtractTool(string installer, string destination, CancellationToken cancellationToken)
    {
        try
        {
            // Read front to back: RARLAB's installers are solid, and a solid archive's entries
            // can only be reached through the ones before them.
            using var archive = RarArchive.OpenArchive(installer, new ReaderOptions { LookForHeader = true });
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = reader.Entry;
                if (entry.IsDirectory || entry.Key is not { } key)
                {
                    continue;
                }

                // Top-level files only: the installer's own Rar.exe, not one in a subfolder.
                var normalized = key.Replace('\\', '/');
                if (normalized.Contains('/') || KeptFiles.FirstOrDefault(k => k.Equals(normalized, StringComparison.OrdinalIgnoreCase)) is not { } name)
                {
                    continue;
                }

                using var data = reader.OpenEntryStream();
                using var output = File.Create(Path.Combine(destination, name));
                data.CopyTo(output);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException and not ArchiveException)
        {
            SupportLogger.Shared.Log($"rar install: couldn't read the installer: {e.Message}", "archive", LogLevel.Error);
            throw Failure(Localization.L("The download did not contain the rar tool."));
        }
    }

    private static ArchiveException Failure(string message) => ArchiveException.ToolInstall(message);
}
