// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Reflection;
using System.Runtime.InteropServices;
using Xunit.Abstractions;

namespace FileFluss.Core.Tests.Mount;

/// <summary>
/// The real thing: the mount server mapped to a drive letter through Windows' WebDAV
/// redirector, listed and written with ordinary file APIs, then unmapped.
///
/// <para>Opt-in, because it changes the machine's state for a few seconds (a drive letter
/// and a per-user Explorer label): set <c>FILEFLUSS_LIVE_DRIVE_MAPPING=1</c>. It also needs
/// the WebClient service to be running already — the test never starts it (open any
/// <c>\\host@SSL\…</c> path or run <c>net start webclient</c> as admin beforehand).</para>
///
/// <para>This project targets plain net10.0 and can't reference FileFluss.Native
/// (net10.0-windows), so <c>DriveMapper</c> is loaded by reflection from Native's build
/// output; build the solution first.</para>
/// </summary>
public sealed class DriveMappingLiveTests(ITestOutputHelper output)
{
    private const string OptInVariable = "FILEFLUSS_LIVE_DRIVE_MAPPING";

    [Fact]
    public void ReadOnlyQueriesWork()
    {
        if (LoadDriveMapper() is not { } mapper)
        {
            output.WriteLine("FileFluss.Native not built or not on Windows; skipped.");
            return;
        }

        var state = Call(mapper, "QueryWebClient")!.ToString();
        output.WriteLine($"WebClient: {state}");
        Assert.NotEqual("Unknown", state);
        output.WriteLine($"FileSizeLimitInBytes: {Call(mapper, "FileSizeLimitBytes")}");

        var parse = mapper.GetMethod("ParseLoopbackRemote", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.NotNull(parse.Invoke(null, [@"\\127.0.0.1@54321\FileFluss-0123456789abcdef0123456789abcdef"]));
        Assert.NotNull(parse.Invoke(null, [@"\\localhost@54321\DavWWWRoot\FileFluss-0123456789ABCDEF0123456789ABCDEF\"]));
        Assert.NotNull(parse.Invoke(null, ["http://127.0.0.1:54321/FileFluss-0123456789abcdef0123456789abcdef/"]));
        Assert.Null(parse.Invoke(null, [@"\\nas@5005\FileFluss-0123456789abcdef0123456789abcdef"]));
        Assert.Null(parse.Invoke(null, [@"\\127.0.0.1@54321\share"]));

        var label = mapper.GetMethod("LabelKeyName", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal("##127.0.0.1@54321#FileFluss-ab", label.Invoke(null, [@"\\127.0.0.1@54321\FileFluss-ab"]));
    }

    [Fact]
    public async Task MapsListsWritesAndUnmapsARealDrive()
    {
        if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
        {
            output.WriteLine($"Set {OptInVariable}=1 to run.");
            return;
        }

        var mapper = LoadDriveMapper() ?? throw new InvalidOperationException("Build FileFluss.Native first (dotnet build FileFluss.sln).");
        if (Call(mapper, "QueryWebClient")!.ToString() != "Running")
        {
            output.WriteLine("The WebClient service isn't running; this test doesn't start it. Skipped.");
            return;
        }

        await using var t = await MountTestServer.StartAsync();
        var drive = await (Task<string>)mapper.GetMethod("MapAsync")!.Invoke(null, [t.Server.UncPath, "FileFluss Test", CancellationToken.None])!;
        output.WriteLine($"Mapped {drive} → {t.Server.UncPath}");
        try
        {
            var root = drive + "\\";
            var files = Directory.GetFiles(root).Select(Path.GetFileName).ToList();
            var folders = Directory.GetDirectories(root).Select(Path.GetFileName).ToList();
            output.WriteLine("Files: " + string.Join(", ", files));
            Assert.Contains("hello.txt", files);
            Assert.Contains("folder", folders);
            Assert.Equal("hi", await File.ReadAllTextAsync(Path.Combine(root, "hello.txt")));
            Assert.Equal("nested", await File.ReadAllTextAsync(Path.Combine(root, "folder", "inside.txt")));

            await File.WriteAllTextAsync(Path.Combine(root, "from-explorer.txt"), "written through the redirector");
            Directory.CreateDirectory(Path.Combine(root, "made here"));
            File.Move(Path.Combine(root, "from-explorer.txt"), Path.Combine(root, "made here", "moved.txt"));
            Assert.Equal("written through the redirector", t.Provider.Read("/made here/moved.txt"));

            var drives = (System.Collections.IEnumerable)Call(mapper, "LoopbackDrives")!;
            Assert.Contains(drives.Cast<object>(), d => d.ToString()!.Contains(drive, StringComparison.Ordinal));
        }
        finally
        {
            await (Task)mapper.GetMethod("UnmapAsync")!.Invoke(null, [drive, t.Server.UncPath])!;
            output.WriteLine($"Unmapped {drive}");
        }

        Assert.False(Directory.Exists(drive + "\\"));
    }

    private static object? Call(Type type, string method) => type.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);

    /// <summary>FileFluss.Native's <c>DriveMapper</c> from the solution's build output, or null.</summary>
    private static Type? LoadDriveMapper()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        var configuration = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))))!;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && directory.Name != "src")
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            return null;
        }

        var nativeBin = Path.Combine(directory.FullName, "FileFluss.Native", "bin");
        var dll = new[] { configuration, "Debug", "Release" }
            .Select(c => Path.Combine(nativeBin, c, "net10.0-windows", "FileFluss.Native.dll"))
            .FirstOrDefault(File.Exists);
        return dll is null ? null : Assembly.LoadFrom(dll).GetType("FileFluss.Native.DriveMapper");
    }
}
