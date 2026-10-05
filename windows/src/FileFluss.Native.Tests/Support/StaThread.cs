// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace FileFluss.Native.Tests.Support;

/// <summary>
/// An OLE-initialised single-threaded apartment with a message loop — what a UI thread is to
/// COM. Calls from other apartments into objects that live here are delivered as window
/// messages, so the loop keeps pumping while work items run elsewhere.
/// </summary>
internal sealed class StaThread : IDisposable
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private volatile bool _stop;

    public StaThread(string name)
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<T> InvokeAsync<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        });
        _signal.Set();

        // Bounded: a shell component stuck in a modal state must fail the test, not hang the run.
        return done.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }

    public Task InvokeAsync(Action work) => InvokeAsync(() =>
    {
        work();
        return true;
    });

    /// <summary>Pumps this thread's messages until <paramref name="task"/> completes; call from inside a work item.</summary>
    public static bool PumpUntil(Task task, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        var handles = new[] { ((IAsyncResult)task).AsyncWaitHandle.SafeWaitHandle.DangerousGetHandle() };
        while (!task.IsCompleted)
        {
            var left = deadline - Environment.TickCount64;
            if (left <= 0)
            {
                return false;
            }

            _ = MsgWaitForMultipleObjectsEx(1, handles, (uint)Math.Min(left, 100), 0x04FF /* QS_ALLINPUT */, 0x0004 /* MWMO_INPUTAVAILABLE */);
            PumpPending();
        }

        return true;
    }

    public void Dispose()
    {
        _stop = true;
        _signal.Set();
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    private void Loop()
    {
        _ = OleInitialize(IntPtr.Zero);
        var handles = new[] { _signal.SafeWaitHandle.DangerousGetHandle() };
        while (!_stop)
        {
            while (_queue.TryDequeue(out var work))
            {
                work();
            }

            PumpPending();
            _ = MsgWaitForMultipleObjectsEx(1, handles, 100, 0x04FF, 0x0004);
        }

        OleUninitialize();
    }

    private static void PumpPending()
    {
        while (PeekMessageW(out var message, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
        {
            TranslateMessage(message);
            DispatchMessageW(message);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr[] handles, uint milliseconds, uint wakeMask, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out Msg message, IntPtr window, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(in Msg message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(in Msg message);
}
