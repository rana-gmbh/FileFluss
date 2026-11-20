// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Runtime.InteropServices;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.VirtualFiles;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace FileFluss.Native.VirtualFiles;

/// <summary>
/// Cloud files as Windows "virtual files": an OLE data object offering
/// <c>FileGroupDescriptorW</c> (names, folders, sizes, dates) and, per entry,
/// <c>FileContents</c> as a stream that downloads on first read. Explorer, the desktop, Outlook
/// and any app that accepts files from a zip folder or an e-mail attachment take these.
///
/// <para>Contents are only produced through <see cref="GetData"/>, which receivers call when
/// the drop or paste happens; hovering uses QueryGetData and EnumFormatEtc, which never touch
/// the network. The descriptor waits for the tree listing (normally done while the pointer
/// was on its way to the target).</para>
///
/// <para>Implements IDataObjectAsyncCapability, so Explorer extracts on its own thread and
/// DoDragDrop returns as soon as the drop is accepted. Formats other apps set on us (the shell
/// stores drag-image and drop-result data this way) are kept and handed back.</para>
///
/// <para>Like the streams it hands out, it refuses the free-threaded marshaller: it belongs to
/// the thread that put it on the clipboard or started the drag (FileFluss's UI thread), which
/// is where every other apartment — another process or a shell worker thread in this one —
/// reaches it through a proxy. Its calls are all quick; the slow part, reading, is the
/// streams', and they live in the MTA.</para>
/// </summary>
[ComVisible(true)]
public sealed class VirtualFileDataObject : ComTypes.IDataObject, IDataObjectAsyncCapability, ICustomQueryInterface
{
    public const int DropEffectCopy = 1;
    public const int DropEffectMove = 2;

    public static readonly short CfFileGroupDescriptorW = Ole.RegisterFormat("FileGroupDescriptorW");
    public static readonly short CfFileContents = Ole.RegisterFormat("FileContents");
    public static readonly short CfPreferredDropEffect = Ole.RegisterFormat("Preferred DropEffect");

    /// <summary>
    /// Keeps clipboard history, the cloud clipboard and well-behaved clipboard managers away.
    /// They render every HGLOBAL format the moment something is copied, which would list a
    /// copied folder tree for nothing — and virtual files can't be kept in a history anyway:
    /// their contents only exist while FileFluss serves them.
    /// </summary>
    public static readonly short CfExcludeFromMonitors = Ole.RegisterFormat("ExcludeClipboardContentFromMonitorProcessing");

    public static readonly short CfCanIncludeInHistory = Ole.RegisterFormat("CanIncludeInClipboardHistory");

    public static readonly short CfCanUploadToCloud = Ole.RegisterFormat("CanUploadToCloudClipboard");

    private static readonly short[] MonitorFormats = [CfExcludeFromMonitors, CfCanIncludeInHistory, CfCanUploadToCloud];

    private readonly Dictionary<short, byte[]> _stored = [];
    private readonly Lock _gate = new();
    private byte[]? _descriptor;
    private bool _asyncMode = true;
    private bool _inOperation;
    private int _unavailableReported;

    public VirtualFileDataObject(VirtualFileSource source)
    {
        Source = source;
    }

    public VirtualFileSource Source { get; }

    /// <summary>FD_PROGRESSUI: the receiver shows its own progress dialog (Explorer's copy dialog).</summary>
    public bool ShowProgressUi { get; init; } = true;

    /// <summary>
    /// Once the tree is known to be unavailable (too large, listing failed), stop offering the
    /// virtual formats, so a drag shows "can't drop here" instead of failing on release.
    /// The clipboard keeps offering them, so a paste reaches us and can explain.
    /// </summary>
    public bool HideFormatsWhenUnavailable { get; init; }

    /// <summary>How long a receiver's request for the descriptor waits for the listing.</summary>
    public TimeSpan TreeTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How a call on the UI thread waits for the listing. The app replaces it with a wait that
    /// keeps its window responsive; the default blocks.
    /// </summary>
    public static Func<Task, TimeSpan, bool> Wait { get; set; } = (task, timeout) => ((IAsyncResult)task).AsyncWaitHandle.WaitOne(timeout);

    /// <summary>A receiver asked for the files but the tree couldn't be built (raised once, any thread).</summary>
    public event Action<Exception>? TreeUnavailable;

    /// <summary>The receiver began an asynchronous extraction.</summary>
    public event Action? OperationStarted;

    /// <summary>The receiver finished its asynchronous extraction: HRESULT and the performed drop effect.</summary>
    public event Action<int, int>? OperationEnded;

    public bool InAsyncOperation
    {
        get
        {
            lock (_gate)
            {
                return _inOperation;
            }
        }
    }

    /// <summary>The tree's failure, once the listing has ended in one.</summary>
    public Exception? TreeFailure
    {
        get
        {
            var tree = Source.IsTreeStarted ? Source.TreeAsync() : null;
            return tree is { IsCompleted: true } && !tree.IsCompletedSuccessfully
                ? tree.Exception?.InnerException ?? new OperationCanceledException()
                : null;
        }
    }

    public CustomQueryInterfaceResult GetInterface(ref Guid iid, out IntPtr ppv)
    {
        ppv = IntPtr.Zero;
        return Ole.Refused(iid) ? CustomQueryInterfaceResult.Failed : CustomQueryInterfaceResult.NotHandled;
    }

    // MARK: - IDataObject

    public void GetData(ref ComTypes.FORMATETC format, out ComTypes.STGMEDIUM medium)
    {
        var cf = format.cfFormat;
        if (cf == CfFileGroupDescriptorW)
        {
            RequireTymed(format, ComTypes.TYMED.TYMED_HGLOBAL);
            medium = HGlobalMedium(Descriptor());
            return;
        }

        if (cf == CfFileContents)
        {
            RequireTymed(format, ComTypes.TYMED.TYMED_ISTREAM);
            var tree = Tree();
            var index = format.lindex;
            if (index < 0 || index >= tree.Entries.Count || tree.Entries[index].IsDirectory)
            {
                throw new COMException(null, Ole.DvELindex);
            }

            medium = StreamMedium(new VirtualFileStream(Source, index, tree.Entries[index].Modified));
            return;
        }

        if (cf == CfPreferredDropEffect)
        {
            RequireTymed(format, ComTypes.TYMED.TYMED_HGLOBAL);
            medium = HGlobalMedium(BitConverter.GetBytes(DropEffectCopy));
            return;
        }

        if (MonitorFormats.Contains(cf))
        {
            RequireTymed(format, ComTypes.TYMED.TYMED_HGLOBAL);
            medium = HGlobalMedium(BitConverter.GetBytes(0));
            return;
        }

        byte[]? stored;
        lock (_gate)
        {
            _stored.TryGetValue(cf, out stored);
        }

        if (stored is not null)
        {
            RequireTymed(format, ComTypes.TYMED.TYMED_HGLOBAL);
            medium = HGlobalMedium(stored);
            return;
        }

        throw new COMException(null, Ole.DvEFormatEtc);
    }

    public void GetDataHere(ref ComTypes.FORMATETC format, ref ComTypes.STGMEDIUM medium) => throw new COMException(null, Ole.ENotImpl);

    public int QueryGetData(ref ComTypes.FORMATETC format)
    {
        var cf = format.cfFormat;
        if (cf == CfFileGroupDescriptorW || cf == CfFileContents)
        {
            if (Hidden)
            {
                return Ole.DvEFormatEtc;
            }

            var tymed = cf == CfFileContents ? ComTypes.TYMED.TYMED_ISTREAM : ComTypes.TYMED.TYMED_HGLOBAL;
            return (format.tymed & tymed) != 0 ? Ole.SOk : Ole.DvETymed;
        }

        if (cf == CfPreferredDropEffect || MonitorFormats.Contains(cf) || Stored(cf))
        {
            return (format.tymed & ComTypes.TYMED.TYMED_HGLOBAL) != 0 ? Ole.SOk : Ole.DvETymed;
        }

        return Ole.DvEFormatEtc;
    }

    public int GetCanonicalFormatEtc(ref ComTypes.FORMATETC formatIn, out ComTypes.FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = IntPtr.Zero;
        return Ole.DataSSameFormatEtc;
    }

    public void SetData(ref ComTypes.FORMATETC formatIn, ref ComTypes.STGMEDIUM medium, bool release)
    {
        if (medium.tymed != ComTypes.TYMED.TYMED_HGLOBAL || medium.unionmember == IntPtr.Zero)
        {
            throw new COMException(null, Ole.ENotImpl);
        }

        var bytes = Ole.FromHGlobal(medium.unionmember);
        lock (_gate)
        {
            _stored[formatIn.cfFormat] = bytes;
        }

        if (release)
        {
            Ole.ReleaseStgMedium(ref medium);
        }
    }

    public ComTypes.IEnumFORMATETC EnumFormatEtc(ComTypes.DATADIR direction)
    {
        if (direction != ComTypes.DATADIR.DATADIR_GET)
        {
            throw new COMException(null, Ole.ENotImpl);
        }

        var formats = new List<ComTypes.FORMATETC>();
        if (!Hidden)
        {
            formats.Add(Format(CfFileGroupDescriptorW, ComTypes.TYMED.TYMED_HGLOBAL));
            formats.Add(Format(CfFileContents, ComTypes.TYMED.TYMED_ISTREAM));
        }

        formats.Add(Format(CfPreferredDropEffect, ComTypes.TYMED.TYMED_HGLOBAL));
        formats.AddRange(MonitorFormats.Select(cf => Format(cf, ComTypes.TYMED.TYMED_HGLOBAL)));
        lock (_gate)
        {
            formats.AddRange(_stored.Keys.Where(k => k != CfPreferredDropEffect && !MonitorFormats.Contains(k)).Select(k => Format(k, ComTypes.TYMED.TYMED_HGLOBAL)));
        }

        var array = formats.ToArray();
        Marshal.ThrowExceptionForHR(Ole.SHCreateStdEnumFmtEtc((uint)array.Length, array, out var enumerator));
        return enumerator;
    }

    public int DAdvise(ref ComTypes.FORMATETC pFormatetc, ComTypes.ADVF advf, ComTypes.IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return Ole.OleEAdviseNotSupported;
    }

    public void DUnadvise(int connection) => throw new COMException(null, Ole.OleEAdviseNotSupported);

    public int EnumDAdvise(out ComTypes.IEnumSTATDATA enumAdvise)
    {
        enumAdvise = null!;
        return Ole.OleEAdviseNotSupported;
    }

    // MARK: - IDataObjectAsyncCapability

    public int SetAsyncMode(int fDoOpAsync)
    {
        lock (_gate)
        {
            _asyncMode = fDoOpAsync != 0;
        }

        return Ole.SOk;
    }

    public int GetAsyncMode(out int pfIsOpAsync)
    {
        lock (_gate)
        {
            pfIsOpAsync = _asyncMode ? 1 : 0;
        }

        return Ole.SOk;
    }

    public int StartOperation(IntPtr pbcReserved)
    {
        lock (_gate)
        {
            if (_inOperation)
            {
                return Ole.SOk;
            }

            _inOperation = true;
        }

        Source.BeginOperation();
        OperationStarted?.Invoke();
        return Ole.SOk;
    }

    public int InOperation(out int pfInAsyncOp)
    {
        lock (_gate)
        {
            pfInAsyncOp = _inOperation ? 1 : 0;
        }

        return Ole.SOk;
    }

    public int EndOperation(int hResult, IntPtr pbcReserved, uint dwEffects)
    {
        lock (_gate)
        {
            if (!_inOperation)
            {
                return Ole.EUnexpected;
            }

            _inOperation = false;
        }

        Source.EndOperation();
        OperationEnded?.Invoke(hResult, (int)dwEffects);
        return Ole.SOk;
    }

    // MARK: - Helpers

    private bool Hidden => HideFormatsWhenUnavailable && TreeFailure is not null;

    private bool Stored(short cf)
    {
        lock (_gate)
        {
            return _stored.ContainsKey(cf);
        }
    }

    /// <summary>The listed tree, waiting for the listing if it is still running.</summary>
    private VirtualFileTree Tree()
    {
        var task = Source.TreeAsync();
        if (!task.IsCompleted && !Wait(task, TreeTimeout))
        {
            SupportLogger.Shared.Log("Listing for another app took too long; the request was refused", "dragOut", LogLevel.Error);
            throw new COMException(null, Ole.EFail);
        }

        if (task.IsCompletedSuccessfully)
        {
            return task.Result;
        }

        var failure = task.Exception?.InnerException ?? new OperationCanceledException();
        if (Interlocked.Exchange(ref _unavailableReported, 1) == 0)
        {
            TreeUnavailable?.Invoke(failure);
        }

        throw new COMException(failure.Message, failure is OperationCanceledException ? Ole.HrCancelled : Ole.EFail);
    }

    private byte[] Descriptor()
    {
        var tree = Tree();
        lock (_gate)
        {
            return _descriptor ??= FileGroupDescriptor.Build(tree.Entries, ShowProgressUi);
        }
    }

    private static void RequireTymed(ComTypes.FORMATETC format, ComTypes.TYMED tymed)
    {
        if ((format.tymed & tymed) == 0)
        {
            throw new COMException(null, Ole.DvETymed);
        }
    }

    private static ComTypes.FORMATETC Format(short cf, ComTypes.TYMED tymed) => new()
    {
        cfFormat = cf,
        dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
        lindex = -1,
        ptd = IntPtr.Zero,
        tymed = tymed,
    };

    private static ComTypes.STGMEDIUM HGlobalMedium(byte[] bytes) => new()
    {
        tymed = ComTypes.TYMED.TYMED_HGLOBAL,
        unionmember = Ole.ToHGlobal(bytes),
        pUnkForRelease = null,
    };

    /// <summary>
    /// The stream as an IStream pointer for the receiver. On a UI thread it is first
    /// registered in the MTA (see <see cref="MtaMarshaller"/>) so its blocking reads never
    /// queue on that thread.
    /// </summary>
    private static ComTypes.STGMEDIUM StreamMedium(VirtualFileStream stream)
    {
        var pointer = Marshal.GetComInterfaceForObject<VirtualFileStream, ComTypes.IStream>(stream);
        if (Ole.OnSta())
        {
            try
            {
                pointer = MtaMarshaller.ProxyFromMta(pointer, Ole.IidIStream);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                SupportLogger.Shared.Log($"Couldn't move a stream to the MTA ({e.Message}); it is served from the UI thread", "dragOut", LogLevel.Error);
            }
        }

        return new ComTypes.STGMEDIUM { tymed = ComTypes.TYMED.TYMED_ISTREAM, unionmember = pointer, pUnkForRelease = null };
    }
}
