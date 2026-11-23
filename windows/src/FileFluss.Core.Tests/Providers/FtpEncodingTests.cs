// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Net.Sockets;
using System.Text;
using FileFluss.Core.Providers.Ftp;

namespace FileFluss.Core.Tests.Providers;

/// <summary>
/// The real FluentFTP client against a minimal control-channel server on loopback that, like
/// many NAS, router and camera servers, doesn't list UTF8 in FEAT. File names must still go
/// out as UTF-8 bytes (macOS/curl does that), not as ASCII "??".
/// </summary>
public sealed class FtpEncodingTests
{
    [Fact]
    public async Task NamesGoOutAsUtf8EvenWhenTheServerDoesNotAdvertiseIt()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = new List<byte[]>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = ServeAsync(listener, received, cts.Token);

        var credentials = new FtpCredentials { Host = "127.0.0.1", Port = port, Username = "u", Password = "p" };
        using (var client = FluentFtpConnection.CreateClient(credentials))
        {
            await client.Connect(cts.Token);
            await client.Execute("MKD /Grüße", cts.Token);
            await client.Disconnect(cts.Token);
        }

        await cts.CancelAsync();
        try
        {
            await server;
        }
        catch (OperationCanceledException)
        {
        }

        var mkd = received.Select(line => line).LastOrDefault(line => line.AsSpan().StartsWith("MKD"u8));
        Assert.NotNull(mkd);
        Assert.Equal(Encoding.UTF8.GetBytes("MKD /Grüße"), mkd);
    }

    /// <summary>Answers one control connection: login, a FEAT without UTF8, then 2xx for the rest.</summary>
    private static async Task ServeAsync(TcpListener listener, List<byte[]> received, CancellationToken cancellationToken)
    {
        using var socket = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = socket.GetStream();

        async Task Reply(string text) => await stream.WriteAsync(Encoding.ASCII.GetBytes(text + "\r\n"), cancellationToken);

        await Reply("220 test server");
        var buffer = new List<byte>();
        var one = new byte[1];
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await stream.ReadAsync(one, cancellationToken) == 0)
            {
                return;
            }

            if (one[0] != '\n')
            {
                buffer.Add(one[0]);
                continue;
            }

            var line = buffer.ToArray();
            buffer.Clear();
            if (line.Length > 0 && line[^1] == '\r')
            {
                line = line[..^1];
            }

            received.Add(line);
            var command = Encoding.ASCII.GetString(line).Split(' ')[0].ToUpperInvariant();
            switch (command)
            {
                case "USER": await Reply("331 password please"); break;
                case "PASS": await Reply("230 logged in"); break;
                case "FEAT": await Reply("211-Features:\r\n SIZE\r\n MDTM\r\n211 End"); break;
                case "SYST": await Reply("215 UNIX Type: L8"); break;
                case "PWD": await Reply("257 \"/\" is the current directory"); break;
                case "MKD": await Reply("257 created"); break;
                case "QUIT": await Reply("221 bye"); return;
                default: await Reply("200 ok"); break;
            }
        }
    }
}
