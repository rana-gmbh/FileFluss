// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Tests.Support;
using FileFluss.Core.VirtualFiles;
using FileFluss.Native.Tests.Support;
using FileFluss.Native.VirtualFiles;
using Xunit.Abstractions;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace FileFluss.Native.Tests;

/// <summary>
/// Cloud files handed to the shell as virtual files, end to end through real COM: the data
/// object lives on a "UI" STA, consumers reach it through proxies from other apartments, and
/// the Shell's own folder drop target (the code Explorer runs on a drop or paste) copies the
/// files to disk. The clipboard round trip is opt-in (FILEFLUSS_CLIPBOARD_TESTS=1) because it
/// briefly replaces the user's clipboard.
/// </summary>
public sealed class VirtualFileDataObjectTests : IDisposable
{
    private static readonly DateTimeOffset Date = new(2023, 6, 1, 8, 30, 0, TimeSpan.Zero);

    private readonly ITestOutputHelper _output;
    private readonly string _target = Directory.CreateTempSubdirectory("ff-dragout-target-").FullName;
    private readonly StaThread _ui = new("test UI thread");
    private readonly StaThread _receiver = new("test receiver thread");
    private readonly ConcurrentBag<bool> _readsOnSta = [];

    public VirtualFileDataObjectTests(ITestOutputHelper output)
    {
        _output = output;
        VirtualFileStream.ReadObserved = onSta => _readsOnSta.Add(onSta);
    }

    public void Dispose()
    {
        VirtualFileStream.ReadObserved = null;
        _ui.Dispose();
        _receiver.Dispose();
        try
        {
            Directory.Delete(_target, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static TestCloud Cloud()
    {
        var cloud = new TestCloud();
        cloud.Inner.AddFile("/Project/readme.txt", "Read me first.", Date);
        cloud.Inner.AddFile("/Project/src/main.c", "int main(void) { return 0; }", Date);
        cloud.Inner.AddFolder("/Project/empty");
        cloud.Inner.AddFile("/Project/Report", "PK exported document", Date); // a Google doc: arrives as Report.docx
        cloud.Inner.AddFile("/photo.jpg", new string('x', 100_000), Date);
        return cloud;
    }

    private static async Task<(VirtualFileSource Source, VirtualFileDataObject Data)> CreateAsync(TestCloud cloud, bool hideWhenUnavailable = false)
    {
        var items = (await cloud.ListDirectoryAsync("/")).Where(i => i.Name is "Project" or "photo.jpg").ToList();
        var source = new VirtualFileSource(cloud, items, PanelSide.Left);
        var data = new VirtualFileDataObject(source) { ShowProgressUi = false, HideFormatsWhenUnavailable = hideWhenUnavailable };
        return (source, data);
    }

    private void AssertExtracted(string folder)
    {
        Assert.Equal("Read me first.", File.ReadAllText(Path.Combine(folder, "Project", "readme.txt")));
        Assert.Equal("int main(void) { return 0; }", File.ReadAllText(Path.Combine(folder, "Project", "src", "main.c")));
        Assert.True(Directory.Exists(Path.Combine(folder, "Project", "empty")));
        Assert.Equal("PK exported document", File.ReadAllText(Path.Combine(folder, "Project", "Report.docx")));
        Assert.Equal(100_000, new FileInfo(Path.Combine(folder, "photo.jpg")).Length);
    }

    /// <summary>
    /// Reads go to the MTA even though the data object (and the receiver) are on STAs: the UI
    /// thread never blocks on a download.
    /// </summary>
    private void AssertReadsOffTheUiThread()
    {
        Assert.NotEmpty(_readsOnSta);
        Assert.DoesNotContain(true, _readsOnSta);
    }

    [Fact]
    public async Task OffersTheVirtualFormatsWithoutTouchingTheNetwork()
    {
        var cloud = Cloud();
        var (source, data) = await CreateAsync(cloud);
        using var _ = source;

        var formats = new List<short>();
        var enumerator = data.EnumFormatEtc(ComTypes.DATADIR.DATADIR_GET);
        var one = new ComTypes.FORMATETC[1];
        var fetched = new int[1];
        while (enumerator.Next(1, one, fetched) == 0)
        {
            formats.Add(one[0].cfFormat);
        }

        Assert.Contains(VirtualFileDataObject.CfFileGroupDescriptorW, formats);
        Assert.Contains(VirtualFileDataObject.CfFileContents, formats);
        Assert.Contains(VirtualFileDataObject.CfPreferredDropEffect, formats);

        var contents = new ComTypes.FORMATETC { cfFormat = VirtualFileDataObject.CfFileContents, lindex = -1, tymed = ComTypes.TYMED.TYMED_ISTREAM, dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT };
        Assert.Equal(0, data.QueryGetData(ref contents));
        var hdrop = new ComTypes.FORMATETC { cfFormat = 15 /* CF_HDROP */, lindex = -1, tymed = ComTypes.TYMED.TYMED_HGLOBAL, dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT };
        Assert.NotEqual(0, data.QueryGetData(ref hdrop));

        Assert.False(source.IsTreeStarted);
        Assert.Equal(0, cloud.Downloads);
    }

    [Fact]
    public async Task AReceiverInAnotherApartmentPullsDescriptorAndContentsThroughProxies()
    {
        var cloud = Cloud();
        var (source, data) = await CreateAsync(cloud);
        using var _ = source;

        // Marshal from the "UI" thread like DoDragDrop/OleSetClipboard do; unmarshal in the receiver's apartment.
        var marshalled = await _ui.InvokeAsync(() =>
        {
            var unknown = Marshal.GetIUnknownForObject(data);
            try
            {
                Marshal.ThrowExceptionForHR(CoMarshalInterThreadInterfaceInStream(typeof(ComTypes.IDataObject).GUID, unknown, out var stream));
                return stream;
            }
            finally
            {
                Marshal.Release(unknown);
            }
        });

        var descriptors = await _receiver.InvokeAsync(() =>
        {
            Marshal.ThrowExceptionForHR(CoGetInterfaceAndReleaseStream(marshalled, typeof(ComTypes.IDataObject).GUID, out var pointer));
            var proxy = Marshal.GetObjectForIUnknown(pointer);
            Marshal.Release(pointer);
            Assert.False(proxy is VirtualFileDataObject, "the receiver must hold a COM proxy, not our object");
            try
            {
                return ShellReceiver.Extract((ComTypes.IDataObject)proxy, _target);
            }
            finally
            {
                Marshal.ReleaseComObject(proxy);
            }
        });

        AssertExtracted(_target);
        AssertReadsOffTheUiThread();
        Assert.Equal(
            new[] { "Project", "Project\\readme.txt", "Project\\src", "Project\\src\\main.c", "Project\\empty", "Project\\Report.docx", "photo.jpg" }.Order(),
            descriptors.Select(d => d.Name).Order());
        Assert.All(descriptors.Where(d => !d.IsDirectory && d.Name != "Project\\Report.docx"), d => Assert.Equal(Date, d.Modified));
        Assert.Null(descriptors.Single(d => d.Name == "Project\\Report.docx").Size);
        Assert.Equal(4, cloud.Downloads);
    }

    [Fact]
    public async Task TheShellDropTargetCopiesTheFilesAndOnlyDownloadsOnDrop()
    {
        var cloud = Cloud();
        var (source, data) = await CreateAsync(cloud);
        using var _ = source;
        await source.TreeAsync();
        var ended = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        data.OperationEnded += (hr, _) => ended.TrySetResult(hr);
        var started = false;
        data.OperationStarted += () => started = true;

        var outcome = await _ui.InvokeAsync(() =>
        {
            if (ShellReceiver.FolderDropTarget(_target) is not { } target)
            {
                return "no shell drop target in this session";
            }

            var point = default(ShellReceiver.PointL);
            var effect = ShellReceiver.DropEffectCopy;
            Marshal.ThrowExceptionForHR(target.DragEnter(data, ShellReceiver.MkLButton, point, ref effect));
            Assert.Equal(ShellReceiver.DropEffectCopy, effect & ShellReceiver.DropEffectCopy);
            effect = ShellReceiver.DropEffectCopy;
            Marshal.ThrowExceptionForHR(target.DragOver(ShellReceiver.MkLButton, point, ref effect));

            // Hovering is free: nothing has been downloaded yet.
            Assert.Equal(0, cloud.Downloads);

            effect = ShellReceiver.DropEffectCopy;
            Marshal.ThrowExceptionForHR(target.Drop(data, 0, point, ref effect));
            if (started)
            {
                // Explorer took the asynchronous route; keep the "UI" thread pumping until it is done.
                Assert.True(StaThread.PumpUntil(ended.Task, TimeSpan.FromSeconds(60)), "the shell never ended its operation");
            }

            Marshal.ReleaseComObject(target);
            return started ? "asynchronous" : "synchronous";
        });

        _output.WriteLine($"Shell drop: {outcome}");
        if (outcome.StartsWith("no shell", StringComparison.Ordinal))
        {
            return;
        }

        AssertExtracted(_target);
        AssertReadsOffTheUiThread();
        Assert.Equal(Date.UtcDateTime, File.GetLastWriteTimeUtc(Path.Combine(_target, "Project", "readme.txt")));
        Assert.Equal(4, cloud.Downloads);
        if (started)
        {
            Assert.Equal(0, await ended.Task);
        }
    }

    [Fact]
    public async Task ATooLargeTreeIsRefusedAndReportedOnce()
    {
        var cloud = new TestCloud();
        for (var i = 0; i < 12; i++)
        {
            cloud.Inner.AddFile($"/Big/{i}.txt", "x");
        }

        var items = await cloud.ListDirectoryAsync("/");
        using var source = new VirtualFileSource(cloud, items, PanelSide.Left, maxEntries: 10);
        var data = new VirtualFileDataObject(source) { HideFormatsWhenUnavailable = true };
        var reports = new List<Exception>();
        data.TreeUnavailable += reports.Add;

        var format = new ComTypes.FORMATETC { cfFormat = VirtualFileDataObject.CfFileGroupDescriptorW, lindex = -1, tymed = ComTypes.TYMED.TYMED_HGLOBAL, dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT };
        Assert.Equal(0, data.QueryGetData(ref format)); // not known yet: offered
        Assert.Throws<COMException>(() => data.GetData(ref format, out _));
        Assert.Throws<COMException>(() => data.GetData(ref format, out _));

        Assert.Single(reports);
        Assert.IsType<VirtualFileTreeException>(reports[0]);
        Assert.IsType<VirtualFileTreeException>(data.TreeFailure);
        Assert.NotEqual(0, data.QueryGetData(ref format)); // a drag now shows "no drop"
    }

    [Fact]
    public async Task ShellDataSetOnTheObjectIsKeptAndReturned()
    {
        var (source, data) = await CreateAsync(Cloud());
        using var _ = source;
        var cf = Ole.RegisterFormat("Performed DropEffect");
        var format = new ComTypes.FORMATETC { cfFormat = cf, lindex = -1, tymed = ComTypes.TYMED.TYMED_HGLOBAL, dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT };
        var medium = new ComTypes.STGMEDIUM { tymed = ComTypes.TYMED.TYMED_HGLOBAL, unionmember = Ole.ToHGlobal(BitConverter.GetBytes(1)) };

        data.SetData(ref format, ref medium, release: true);
        data.GetData(ref format, out var back);

        Assert.Equal(1, BitConverter.ToInt32(Ole.FromHGlobal(back.unionmember)));
        Ole.ReleaseStgMedium(ref back);
    }

    [Fact]
    public async Task TheOleClipboardRoundTripsAndIsRestored()
    {
        if (Environment.GetEnvironmentVariable("FILEFLUSS_CLIPBOARD_TESTS") != "1")
        {
            _output.WriteLine("Skipped: set FILEFLUSS_CLIPBOARD_TESTS=1 to run (it briefly replaces the clipboard).");
            return;
        }

        var cloud = Cloud();
        var (source, data) = await CreateAsync(cloud);
        using var _ = source;

        var snapshot = await _ui.InvokeAsync(ClipboardSnapshot.Take);
        try
        {
            Assert.True(await _ui.InvokeAsync(() => OleDragDrop.SetClipboard(data)), "OleSetClipboard failed");
            Assert.True(await _ui.InvokeAsync(() => OleDragDrop.IsOnClipboard(data)));

            // Paste in another apartment, as Explorer would: OleGetClipboard, then pull.
            await _receiver.InvokeAsync(() =>
            {
                Marshal.ThrowExceptionForHR(OleGetClipboard(out var clipboard));
                try
                {
                    ShellReceiver.Extract(clipboard, _target);
                }
                finally
                {
                    Marshal.ReleaseComObject(clipboard);
                }
            });

            AssertExtracted(_target);
            AssertReadsOffTheUiThread();

            // Explorer's Paste: the folder's drop target fed with the clipboard's data object.
            var pasted = Path.Combine(_target, "pasted");
            Directory.CreateDirectory(pasted);
            var outcome = await _receiver.InvokeAsync(() =>
            {
                if (ShellReceiver.FolderDropTarget(pasted) is not { } target)
                {
                    return false;
                }

                Marshal.ThrowExceptionForHR(OleGetClipboard(out var clipboard));
                try
                {
                    var effect = ShellReceiver.DropEffectCopy;
                    Marshal.ThrowExceptionForHR(target.DragEnter(clipboard, 0, default, ref effect));
                    effect = ShellReceiver.DropEffectCopy;
                    Marshal.ThrowExceptionForHR(target.Drop(clipboard, 0, default, ref effect));
                }
                finally
                {
                    Marshal.ReleaseComObject(clipboard);
                    Marshal.ReleaseComObject(target);
                }

                return true;
            });

            _output.WriteLine(outcome ? "Shell paste from the clipboard: done" : "Shell paste from the clipboard: no drop target in this session");
            if (outcome)
            {
                for (var i = 0; i < 100 && !File.Exists(Path.Combine(pasted, "photo.jpg")); i++)
                {
                    await Task.Delay(100);
                }

                AssertExtracted(pasted);
            }

            await _ui.InvokeAsync(() => OleDragDrop.ClearClipboardIfHolding(data));
            Assert.False(await _ui.InvokeAsync(() => OleDragDrop.IsOnClipboard(data)));
        }
        finally
        {
            await _ui.InvokeAsync(() =>
            {
                OleDragDrop.ClearClipboardIfHolding(data);
                snapshot.Restore();
            });
        }
    }

    /// <summary>
    /// A receiver in another process (Windows PowerShell, i.e. .NET Framework, so this also
    /// covers managed receivers) takes the clipboard and pulls every file as Explorer's drop
    /// target does: descriptor, then FileContents stream by stream. All calls cross the process
    /// boundary; the data object answers on the pumping "UI" thread, the streams in the MTA.
    /// </summary>
    [Fact]
    public async Task AReceiverInAnotherProcessPullsTheFilesFromTheClipboard()
    {
        if (Environment.GetEnvironmentVariable("FILEFLUSS_CLIPBOARD_TESTS") != "1")
        {
            _output.WriteLine("Skipped: set FILEFLUSS_CLIPBOARD_TESTS=1 to run (it briefly replaces the clipboard).");
            return;
        }

        var cloud = Cloud();
        var (source, data) = await CreateAsync(cloud);
        using var _ = source;
        var snapshot = await _ui.InvokeAsync(ClipboardSnapshot.Take);
        try
        {
            Assert.True(await _ui.InvokeAsync(() => OleDragDrop.SetClipboard(data)), "OleSetClipboard failed");

            var script = ReceiverScript
                .Replace("%TARGET%", _target.Replace("'", "''"), StringComparison.Ordinal)

                .Replace("%FC%", VirtualFileDataObject.CfFileContents.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                ArgumentList = { "-NoProfile", "-NonInteractive", "-STA", "-Command", script },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var receiver = System.Diagnostics.Process.Start(info)!;
            var stdout = receiver.StandardOutput.ReadToEndAsync();
            var stderr = receiver.StandardError.ReadToEndAsync();
            try
            {
                await receiver.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            }
            finally
            {
                if (!receiver.HasExited)
                {
                    receiver.Kill();
                }
            }

            _output.WriteLine($"receiver: {await stdout} {await stderr}");

            AssertExtracted(_target);
            AssertReadsOffTheUiThread();
            Assert.Equal(4, cloud.Downloads);
        }
        finally
        {
            await _ui.InvokeAsync(() =>
            {
                OleDragDrop.ClearClipboardIfHolding(data);
                snapshot.Restore();
            });
        }
    }

    /// <summary>Explorer's side of a paste, in PowerShell: OleGetClipboard (via WinForms), descriptor, FileContents.</summary>
    private const string ReceiverScript = """
        $ErrorActionPreference = 'Stop'
        trap { "error: $($_.Exception.ToString())"; exit 1 }
        Add-Type -AssemblyName System.Windows.Forms
        $data = [System.Windows.Forms.Clipboard]::GetDataObject()
        $getData = [System.Runtime.InteropServices.ComTypes.IDataObject].GetMethod('GetData')
        $read = [System.Runtime.InteropServices.ComTypes.IStream].GetMethod('Read')
        $T = [System.Runtime.InteropServices.ComTypes.TYMED]
        function Get-Medium([int16]$cf, [int]$index, $tymed) {
          $format = New-Object System.Runtime.InteropServices.ComTypes.FORMATETC
          $format.cfFormat = $cf; $format.lindex = $index; $format.tymed = $tymed
          $format.dwAspect = [System.Runtime.InteropServices.ComTypes.DVASPECT]::DVASPECT_CONTENT
          $medium = New-Object System.Runtime.InteropServices.ComTypes.STGMEDIUM
          $call = [object[]]@($format, $medium)
          $getData.Invoke($data, $call) | Out-Null
          return $call[1]
        }
        $descriptor = $data.GetData('FileGroupDescriptorW').ToArray()
        $count = [BitConverter]::ToUInt32($descriptor, 0)
        $buffer = [byte[]]::new(65536)
        $countRead = [System.Runtime.InteropServices.Marshal]::AllocCoTaskMem(8)
        for ($i = 0; $i -lt $count; $i++) {
          $offset = 4 + $i * 592
          $attributes = [BitConverter]::ToUInt32($descriptor, $offset + 36)
          $name = [Text.Encoding]::Unicode.GetString($descriptor, $offset + 72, 520).Split([char]0)[0]
          $path = Join-Path '%TARGET%' $name
          if ($attributes -band 0x10) { New-Item -ItemType Directory -Force $path | Out-Null; continue }
          New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
          $contents = Get-Medium %FC% $i ($T::TYMED_ISTREAM -bor $T::TYMED_HGLOBAL)
          $stream = [System.Runtime.InteropServices.Marshal]::GetObjectForIUnknown($contents.unionmember)
          $file = [System.IO.File]::Create($path)
          try {
            do {
              $readArgs = [object[]]::new(3); $readArgs[0] = $buffer; $readArgs[1] = [int]65536; $readArgs[2] = [IntPtr]$countRead
              $read.Invoke($stream, $readArgs) | Out-Null
              $n = [System.Runtime.InteropServices.Marshal]::ReadInt32($countRead)
              $file.Write($buffer, 0, $n)
            } while ($n -gt 0)
          } finally { $file.Close() }
          [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($stream)
          "$name"
        }
        """;

    [DllImport("ole32.dll")]
    private static extern int CoMarshalInterThreadInterfaceInStream(in Guid riid, IntPtr unknown, out IntPtr stream);

    [DllImport("ole32.dll")]
    private static extern int CoGetInterfaceAndReleaseStream(IntPtr stream, in Guid riid, out IntPtr result);

    [DllImport("ole32.dll")]
    private static extern int OleGetClipboard(out ComTypes.IDataObject dataObject);
}

/// <summary>A memory cloud that counts downloads and, like Google Drive, exports extensionless files as .docx.</summary>
internal sealed class TestCloud : FileProvider
{
    private int _downloads;

    public MemoryCloudProvider Inner { get; } = new();

    public int Downloads => _downloads;

    public override ValueTask<string?> ExportExtensionAsync(CloudFileItem item, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(!item.IsDirectory && !item.Name.Contains('.') ? "docx" : null);

    public override Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default) => Inner.ListDirectoryAsync(path, cancellationToken);

    public override async Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _downloads);
        var target = GetName(remotePath).Contains('.') ? localPath : localPath + ".docx";
        await Inner.DownloadFileAsync(remotePath, target, onBytes, cancellationToken);
    }

    public override Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default) => Inner.UploadFileAsync(localPath, remotePath, onBytes, cancellationToken);

    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default) => Inner.DeleteItemAsync(path, cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => Inner.CreateDirectoryAsync(path, cancellationToken);

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default) => Inner.RenameItemAsync(path, newName, cancellationToken);

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default) => Inner.GetFileMetadataAsync(path, cancellationToken);
}
