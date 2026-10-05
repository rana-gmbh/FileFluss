// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Runtime.InteropServices;
using FileFluss.Core.VirtualFiles;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace FileFluss.Native.VirtualFiles;

/// <summary>
/// The <c>FileContents</c> of one virtual file: a read-only IStream that downloads its file on
/// the first Read (or Stat) and then serves the staged copy. Handing out the stream costs
/// nothing; a receiver that never reads it never causes a download.
///
/// <para>The CCW refuses IMarshal and IAgileObject so COM uses standard marshalling: the
/// stream then lives in the apartment that registered it (the MTA, see
/// <see cref="MtaMarshaller"/>) instead of following the free-threaded marshaller back onto
/// whichever thread passed it along — the UI thread.</para>
/// </summary>
[ComVisible(true)]
public sealed class VirtualFileStream : ComTypes.IStream, ICustomQueryInterface
{
    private const int StgTyStream = 2;

    private readonly VirtualFileSource _source;
    private readonly int _index;
    private readonly DateTimeOffset? _modified;
    private readonly Lock _gate = new();
    private FileStream? _file;
    private long _position;
    private long? _length;

    internal VirtualFileStream(VirtualFileSource source, int index, DateTimeOffset? modified)
    {
        _source = source;
        _index = index;
        _modified = modified;
    }

    /// <summary>Test hook: called on every Read with whether it ran on a single-threaded apartment.</summary>
    internal static Action<bool>? ReadObserved { get; set; }

    public CustomQueryInterfaceResult GetInterface(ref Guid iid, out IntPtr ppv)
    {
        ppv = IntPtr.Zero;
        return Ole.Refused(iid) ? CustomQueryInterfaceResult.Failed : CustomQueryInterfaceResult.NotHandled;
    }

    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        ReadObserved?.Invoke(Ole.OnSta());
        int total;
        try
        {
            lock (_gate)
            {
                total = ReadLocked(pv, cb);
            }

            _source.Touch();
        }
        catch (Exception e)
        {
            throw Translate(e);
        }

        if (pcbRead != IntPtr.Zero)
        {
            Marshal.WriteInt32(pcbRead, total);
        }
    }

    private int ReadLocked(byte[] buffer, int count)
    {
        if (_file is null && _length is { } known && _position >= known)
        {
            // Already read to the end and closed: answer "end of file" without fetching again.
            return 0;
        }

        var file = Open();
        file.Position = _position;
        var total = 0;
        while (total < count)
        {
            var read = file.Read(buffer, total, count - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        _position += total;
        if (total < count)
        {
            // End of file: let the handle go now rather than whenever the receiver releases us.
            Close();
        }

        return total;
    }

    public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
    {
        long position;
        try
        {
            lock (_gate)
            {
                position = dwOrigin switch
                {
                    0 /* STREAM_SEEK_SET */ => dlibMove,
                    1 /* STREAM_SEEK_CUR */ => _position + dlibMove,
                    2 /* STREAM_SEEK_END */ => Length() + dlibMove,
                    _ => throw new COMException(null, Ole.StgEInvalidFunction),
                };
                if (position < 0)
                {
                    throw new COMException(null, Ole.StgEInvalidFunction);
                }

                _position = position;
            }
        }
        catch (Exception e)
        {
            throw Translate(e);
        }

        if (plibNewPosition != IntPtr.Zero)
        {
            Marshal.WriteInt64(plibNewPosition, position);
        }
    }

    public void Stat(out ComTypes.STATSTG pstatstg, int grfStatFlag)
    {
        long length;
        try
        {
            lock (_gate)
            {
                length = Length();
            }
        }
        catch (Exception e)
        {
            throw Translate(e);
        }

        var time = FileGroupDescriptor.ToFileTime(_modified) ?? 0;
        var fileTime = new ComTypes.FILETIME { dwLowDateTime = (int)(time & 0xFFFFFFFF), dwHighDateTime = (int)(time >> 32) };

        // pwcsName stays null: the receiver names the file from the descriptor, and a managed
        // string here would not be marshalled as the CoTaskMem WCHAR* the interface promises.
        pstatstg = new ComTypes.STATSTG
        {
            type = StgTyStream,
            cbSize = length,
            mtime = fileTime,
            ctime = fileTime,
            atime = fileTime,
            grfMode = 0, // STGM_READ
        };
    }

    public void CopyTo(ComTypes.IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
    {
        var buffer = new byte[1 << 16];
        long copied = 0;
        var countRead = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            while (copied < cb)
            {
                var chunk = (int)Math.Min(buffer.Length, cb - copied);
                Read(buffer, chunk, countRead);
                var read = Marshal.ReadInt32(countRead);
                if (read == 0)
                {
                    break;
                }

                pstm.Write(buffer, read, IntPtr.Zero);
                copied += read;
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(countRead);
        }

        if (pcbRead != IntPtr.Zero)
        {
            Marshal.WriteInt64(pcbRead, copied);
        }

        if (pcbWritten != IntPtr.Zero)
        {
            Marshal.WriteInt64(pcbWritten, copied);
        }
    }

    public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new COMException(null, Ole.StgEAccessDenied);

    public void SetSize(long libNewSize) => throw new COMException(null, Ole.StgEAccessDenied);

    public void Commit(int grfCommitFlags)
    {
    }

    public void Revert()
    {
    }

    public void LockRegion(long libOffset, long cb, int dwLockType) => throw new COMException(null, Ole.StgEInvalidFunction);

    public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new COMException(null, Ole.StgEInvalidFunction);

    public void Clone(out ComTypes.IStream ppstm) => throw new COMException(null, Ole.ENotImpl);

    // MARK: - Helpers (under _gate)

    private FileStream Open()
    {
        if (_file is null)
        {
            // Blocks this (RPC worker) thread until the download is staged.
            _file = _source.OpenAsync(_index).GetAwaiter().GetResult();
            _length = _file.Length;
        }

        return _file;
    }

    private long Length() => _length ?? Open().Length;

    private void Close()
    {
        _file?.Dispose();
        _file = null;
    }

    /// <summary>An HRESULT the receiver understands: a cancellation is quiet, anything else a failed read.</summary>
    private static COMException Translate(Exception e) => e switch
    {
        COMException com => com,
        OperationCanceledException or ObjectDisposedException => new COMException(e.Message, Ole.HrCancelled),
        _ => new COMException(e.Message, Ole.EFail),
    };
}
