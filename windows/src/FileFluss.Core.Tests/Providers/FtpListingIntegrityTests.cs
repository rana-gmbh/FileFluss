// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Net.Sockets;
using System.Text;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.Ftp;

namespace FileFluss.Core.Tests.Providers;

/// <summary>
/// macOS 1.6: "Mirror stops on … a listing it can't parse". FluentFTP drops a row none of its
/// parsers understands and only logs it, so a folder could come back with entries missing —
/// and a Mirror then deletes them on the other side. Run against a scripted FTP server on the
/// loopback, through the real FluentFTP client.
/// </summary>
public sealed class FtpListingIntegrityTests
{
    private const string GoodRows =
        "total 8\r\n" +
        "drwxr-xr-x   4 owner group     4096 Jan 01  2024 .\r\n" +
        "drwxr-xr-x  12 owner group     4096 Jan 01  2024 ..\r\n" +
        "drwxr-xr-x   2 owner group     4096 Jan 01  2024 Photos\r\n" +
        "-rw-r--r--   1 owner group      123 Jan 01  2024 a.txt\r\n";

    [Fact]
    public async Task AnOrdinaryListingPassesWithItsTotalLine()
    {
        var entries = await ListAsync(GoodRows);
        Assert.Equal(["Photos", "a.txt"], entries.Select(e => e.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ARowTheParserDroppedFailsTheListing()
    {
        var listing = GoodRows + "-rw-r--r-- garbled\r\n";
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => ListAsync(listing));
        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
    }

    [Fact]
    public void TheCheckIgnoresWhatCantBeCopiedAnyway()
    {
        // Device rows, the folder itself and its parent, and the "total" line.
        FtpListingCheck.Validate(
            ["crw-rw-rw-   1 root root  1,   3 Jan 01  2024 null", "drwxr-xr-x 2 o g 4096 Jan 01 2024 .", "drwxr-xr-x 2 o g 4096 Jan 01 2024 ..", "total 0"],
            itemCount: 0);

        // A file, folder or link row that wasn't understood.
        Assert.Throws<CloudProviderException>(() => FtpListingCheck.Validate(["lrwxrwxrwx 1 o g ??? link -> target"], itemCount: 3));

        // An unknown format: nothing parsed and lines left over.
        Assert.Throws<CloudProviderException>(() => FtpListingCheck.Validate(["01-02-24  10:00AM       <DIR>          Photos"], itemCount: 0));
        FtpListingCheck.Validate(["some banner text"], itemCount: 2);
    }

    private static async Task<IReadOnlyList<FtpEntry>> ListAsync(string listing)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var server = new ScriptedFtpServer(listing);
        var serving = server.ServeAsync(timeout.Token);
        try
        {
            using var connection = await FluentFtpConnection.ConnectAsync(
                new FtpCredentials { Host = "127.0.0.1", Port = server.Port, Username = "user", Password = "pass" },
                timeout.Token);
            return await connection.ListAsync("/", timeout.Token);
        }
        finally
        {
            server.Stop();
            await Task.WhenAny(serving, Task.Delay(TimeSpan.FromSeconds(5)));
        }
    }

    /// <summary>Just enough FTP for a login and one LIST over extended passive mode.</summary>
    private sealed class ScriptedFtpServer(string listing) : IDisposable
    {
        private readonly TcpListener _control = StartListener();
        private TcpListener? _data;

        public int Port => ((IPEndPoint)_control.LocalEndpoint).Port;

        private static TcpListener StartListener()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return listener;
        }

        public async Task ServeAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var client = await _control.AcceptTcpClientAsync(cancellationToken);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 Scripted FTP ready");
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    var verb = line.Split(' ', 2)[0].ToUpperInvariant();
                    switch (verb)
                    {
                        case "USER":
                            await writer.WriteLineAsync("331 Password please");
                            break;
                        case "PASS":
                            await writer.WriteLineAsync("230 Logged in");
                            break;
                        case "FEAT":
                            await writer.WriteLineAsync("211-Features:");
                            await writer.WriteLineAsync(" EPSV");
                            await writer.WriteLineAsync("211 End");
                            break;
                        case "SYST":
                            await writer.WriteLineAsync("215 UNIX Type: L8");
                            break;
                        case "PWD":
                            await writer.WriteLineAsync("257 \"/\" is the current directory");
                            break;
                        case "TYPE" or "OPTS" or "NOOP" or "CWD" or "MODE" or "STRU":
                            await writer.WriteLineAsync("200 OK");
                            break;
                        case "EPSV":
                            _data?.Stop();
                            _data = StartListener();
                            await writer.WriteLineAsync($"229 Entering Extended Passive Mode (|||{((IPEndPoint)_data.LocalEndpoint).Port}|)");
                            break;
                        case "LIST":
                            await writer.WriteLineAsync("150 Here comes the listing");
                            using (var data = await _data!.AcceptTcpClientAsync(cancellationToken))
                            {
                                var bytes = Encoding.ASCII.GetBytes(listing);
                                await data.GetStream().WriteAsync(bytes, cancellationToken);
                            }

                            await writer.WriteLineAsync("226 Done");
                            break;
                        case "QUIT":
                            await writer.WriteLineAsync("221 Bye");
                            return;
                        default:
                            await writer.WriteLineAsync("502 Not implemented");
                            break;
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
            }
        }

        public void Stop()
        {
            _control.Stop();
            _data?.Stop();
        }

        public void Dispose() => Stop();
    }
}
