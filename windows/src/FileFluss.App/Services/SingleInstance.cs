// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using System.IO.Pipes;
using System.Text;
using FileFluss.Core.Diagnostics;

namespace FileFluss.App.Services;

/// <summary>
/// One FileFluss per user session (from the NetFluss Windows port). macOS activates a running
/// app on a second launch; on Windows a second double-click starts a second process — two
/// windows racing on the same settings, accounts and index. The second process forwards its
/// command line (a folder to open, or nothing) over a current-user-only pipe and exits.
///
/// <para>A running copy that doesn't answer is closing (it stops listening first and lets go
/// of the lock last): the new launch then waits for it to finish and starts normally, instead
/// of vanishing without a window.</para>
/// </summary>
internal static class SingleInstance
{
    /// <summary>How long a launch waits for a closing FileFluss (unmapping drives is bounded at 10 s).</summary>
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(20);

    /// <summary>A command line is a folder path or two; anything longer isn't one of ours.</summary>
    private const int MaxPayloadChars = 32 * 1024;

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private static Mutex? _mutex;
    private static CancellationTokenSource? _stop;

    /// <summary>Raised on the thread pool with a forwarded command line.</summary>
    public static event Action<string[]>? Forwarded;

    /// <summary>
    /// Session and user SID: another account signed in to the same session (runas) can't
    /// squat the names and keep this user's FileFluss from starting or receiving paths.
    /// </summary>
    private static string Scope { get; } =
        $"{System.Diagnostics.Process.GetCurrentProcess().SessionId}.{System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName}";

    private static string PipeName => $"FileFluss.Commands.{Scope}";

    /// <summary>True when this process is the one to run; otherwise its arguments were handed over.</summary>
    /// <param name="activate">Called on the thread pool when another launch asks this one to come forward; must not block.</param>
    public static bool TryClaim(Action activate)
    {
        var mutex = new Mutex(initiallyOwned: true, $@"Local\FileFluss.Instance.{Scope}", out var created);
        if (!created)
        {
            if (Forward(Environment.GetCommandLineArgs().Skip(1).ToArray()))
            {
                mutex.Dispose();
                return false;
            }

            // Nobody listening: the running copy is on its way out. Take over once it is gone.
            if (!Acquire(mutex))
            {
                // Still there and still not answering: hung. Leave a trace of why this launch ends.
                CrashLog.Write("startup", new TimeoutException($"Another FileFluss in this session holds the single-instance lock but doesn't answer; this launch (process {Environment.ProcessId}) gives up."));
                mutex.Dispose();
                return false;
            }
        }

        _mutex = mutex;
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _ = Task.Run(() => ListenAsync(activate, token), token);
        return true;
    }

    private static bool Acquire(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(ExitWait);
        }
        catch (AbandonedMutexException)
        {
            // The other copy ended without letting go (killed or crashed): the lock is ours now.
            return true;
        }
    }

    /// <summary>
    /// Accepts forwarded launches until <see cref="StopListening"/>. Nothing a forwarded
    /// launch does may end the loop: a dead listener would turn every later launch into one
    /// that waits and then gives up.
    /// </summary>
    private static async Task ListenAsync(Action activate, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                // Bounded in size and time: a client that never finishes (or never stops)
                // must not hold the one pipe instance, or memory, forever.
                using var read = CancellationTokenSource.CreateLinkedTokenSource(token);
                read.CancelAfter(ReadTimeout);
                if (await ReadPayloadAsync(server, read.Token).ConfigureAwait(false) is not { } payload)
                {
                    continue;
                }

                var args = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(a => a.TrimEnd('\r')).Take(16).ToArray();
                SupportLogger.Shared.Log($"Another launch handed over to this one ({args.Length} argument(s))", "App");
                activate();
                Forwarded?.Invoke(args);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                SupportLogger.Shared.Log($"A forwarded launch failed: {e.GetType().Name}: {e.Message}", "App", LogLevel.Notice);

                // A pipe that can't be created (another listener still holds the name) shouldn't spin.
                try
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>The whole message, or null when it is longer than <see cref="MaxPayloadChars"/>.</summary>
    private static async Task<string?> ReadPayloadAsync(Stream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8);
        var buffer = new char[4096];
        var payload = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            payload.Append(buffer, 0, count);
            if (payload.Length > MaxPayloadChars)
            {
                return null;
            }
        }

        return payload.ToString();
    }

    /// <summary>True when the running copy took the arguments.</summary>
    private static bool Forward(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.Write(string.Join('\n', args));
            writer.Flush();
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The first step of exiting: later launches stop handing over to this process.</summary>
    public static void StopListening() => _stop?.Cancel();

    /// <summary>The last step of exiting: a waiting launch may start now. Call on the thread that claimed.</summary>
    public static void Release()
    {
        StopListening();
        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _mutex.Dispose();
            _mutex = null;
        }
    }
}
