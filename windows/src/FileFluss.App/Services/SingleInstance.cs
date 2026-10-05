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
    private static Mutex? _mutex;
    private static CancellationTokenSource? _stop;

    /// <summary>Raised on the thread pool with a forwarded command line.</summary>
    public static event Action<string[]>? Forwarded;

    private static string PipeName => $"FileFluss.Commands.{System.Diagnostics.Process.GetCurrentProcess().SessionId}.{Environment.UserName}";

    /// <summary>True when this process is the one to run; otherwise its arguments were handed over.</summary>
    public static bool TryClaim(Action activate)
    {
        var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        _mutex = new Mutex(initiallyOwned: true, $@"Local\FileFluss.Instance.{session}", out var created);
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
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var payload = await reader.ReadToEndAsync(token).ConfigureAwait(false);
                    var args = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(a => a.TrimEnd('\r')).Take(16).ToArray();
                    activate();
                    Forwarded?.Invoke(args);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                }
            }
        }, token);
        return true;
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
