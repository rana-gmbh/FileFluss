// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using System.IO.Pipes;
using System.Text;

namespace FileFluss.App.Services;

/// <summary>
/// One FileFluss per user session (from the NetFluss Windows port). macOS activates a running
/// app on a second launch; on Windows a second double-click starts a second process — two
/// windows racing on the same settings, accounts and index. The second process forwards its
/// command line (a folder to open, or nothing) over a current-user-only pipe and exits.
/// </summary>
internal static class SingleInstance
{
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
    public static bool TryClaim(Action activate)
    {
        _mutex = new Mutex(initiallyOwned: true, $@"Local\FileFluss.Instance.{Scope}", out var created);
        if (!created)
        {
            Forward(Environment.GetCommandLineArgs().Skip(1).ToArray());
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _ = Task.Run(async () =>
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
                    var payload = await ReadPayloadAsync(server, read.Token).ConfigureAwait(false);
                    if (payload is null)
                    {
                        continue;
                    }

                    var args = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(a => a.TrimEnd('\r')).Take(16).ToArray();
                    activate();
                    Forwarded?.Invoke(args);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // A client that stalled, sent garbage or hung up: wait for the next one.
                }
            }
        }, token);
        return true;
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

    private static void Forward(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.Write(string.Join('\n', args));
            writer.Flush();
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
        }
    }

    public static void Release()
    {
        _stop?.Cancel();
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
