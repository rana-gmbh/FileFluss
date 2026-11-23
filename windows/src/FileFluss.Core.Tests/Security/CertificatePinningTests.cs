// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FileFluss.Core.Net;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.Seafile;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Security;

/// <summary>
/// "Allow self-signed certificate" as trust on first use: the first untrusted certificate of
/// the typed host is pinned, a different one later is refused with its own error, and editing
/// the account offers to trust the new one.
/// </summary>
public sealed class CertificatePinningTests
{
    // MARK: - The decision

    [Theory]
    [InlineData(SslPolicyErrors.None, false, null, "SHA256:a", TrustDecision.Accept)]
    [InlineData(SslPolicyErrors.None, true, "SHA256:old", "SHA256:new", TrustDecision.Accept)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors, false, null, "SHA256:a", TrustDecision.Refuse)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors, true, null, null, TrustDecision.Refuse)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors, true, null, "SHA256:a", TrustDecision.AcceptAndPin)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch, true, "SHA256:a", "SHA256:a", TrustDecision.Accept)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors, true, "SHA256:a", "SHA256:b", TrustDecision.Changed)]
    public void Decision(SslPolicyErrors errors, bool trustedHost, string? pin, string? presented, TrustDecision expected)
        => Assert.Equal(expected, SelfSignedTrust.Decide(errors, trustedHost, pin, presented));

    [Fact]
    public void TheFirstCertificateIsPinnedOnceAndAnotherIsRefused()
    {
        using var first = SelfSigned("CN=nas");
        using var second = SelfSigned("CN=nas");
        var stored = new List<string>();
        var trust = new SelfSignedTrust("nas.example.test", pin: null, stored.Add);
        const SslPolicyErrors untrusted = SslPolicyErrors.RemoteCertificateChainErrors;

        Assert.True(trust.Validate("NAS.example.test", first, untrusted));
        Assert.True(trust.Validate("nas.example.test", first, untrusted));
        Assert.Equal([SelfSignedTrust.Fingerprint(first)], stored);
        Assert.Equal(SelfSignedTrust.Fingerprint(first), trust.Pin);

        var changed = Assert.Throws<CertificateChangedException>(() => trust.Validate("nas.example.test", second, untrusted));
        Assert.Equal(SelfSignedTrust.Fingerprint(first), changed.Previous);
        Assert.Equal(SelfSignedTrust.Fingerprint(second), changed.Presented);
        Assert.False(trust.Accepts("nas.example.test", second, untrusted));
        Assert.Equal(2, trust.Refusals);

        // Another host never gets the exemption; a valid certificate always passes.
        Assert.False(trust.Validate("elsewhere.example.test", first, untrusted));
        Assert.True(trust.Validate("elsewhere.example.test", second, SslPolicyErrors.None));
    }

    [Fact]
    public void FingerprintsAreThePublicKeysSha256()
    {
        using var certificate = SelfSigned("CN=nas");
        var expected = "SHA256:" + Convert.ToBase64String(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo())).TrimEnd('=');

        Assert.Equal(expected, SelfSignedTrust.Fingerprint(certificate));
    }

    // MARK: - Which addresses default to "allow"

    [Theory]
    [InlineData("https://192.168.1.20:5001", true)]
    [InlineData("http://10.0.0.5", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("https://169.254.10.10", true)]
    [InlineData("https://127.0.0.1:8443", true)]
    [InlineData("https://nas.local:5001", true)]
    [InlineData("https://diskstation:5001", true)]
    [InlineData("https://localhost", true)]
    [InlineData("https://[fe80::1]:5001", true)]
    [InlineData("https://[fd12:3456::1]", true)]
    [InlineData("https://[::1]", true)]
    [InlineData("https://nas.example.com:5001", false)]
    [InlineData("https://myname.myqnapcloud.com", false)]
    [InlineData("https://81.2.69.160", false)]
    [InlineData("https://172.32.0.1", false)]
    [InlineData("https://[2001:db8::1]", false)]
    [InlineData("", false)]
    public void LocalAddresses(string address, bool local) => Assert.Equal(local, CredentialScope.IsLocalAddress(address));

    // MARK: - End to end, over real TLS

    [Fact]
    public async Task AnAccountPinsItsServersCertificateAndRefusesAnotherOne()
    {
        using var original = SelfSigned("CN=seafile");
        using var replaced = SelfSigned("CN=seafile");
        await using var server = new TinyTlsServer(original);
        var store = new MemoryCredentialStore();
        var id = Guid.NewGuid();

        using (var provider = new SeafileProvider(id, store))
        {
            var prompt = new ScriptedConnectPrompt();
            await provider.ConnectAsync(Values(server.Port), prompt);
            Assert.Empty(prompt.Confirmations);
        }

        var pinned = SelfSignedTrust.Fingerprint(original);
        Assert.Contains(pinned, StoredPin(store, id), StringComparison.Ordinal);

        // The same certificate keeps working for the stored account.
        using (var provider = new SeafileProvider(id, store))
        {
            Assert.Empty(await provider.ListDirectoryAsync("/"));
        }

        // Another certificate on the same host: refused with its own message, pin unchanged.
        server.Certificate = replaced;
        using (var provider = new SeafileProvider(id, store))
        {
            var refused = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));
            Assert.Equal(CloudProviderException.CertificateChanged().Message, refused.Message);
        }

        Assert.Contains(pinned, StoredPin(store, id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditingTheAccountAsksBeforeTrustingAChangedCertificate()
    {
        using var original = SelfSigned("CN=seafile");
        using var replaced = SelfSigned("CN=seafile");
        await using var server = new TinyTlsServer(original);
        var store = new MemoryCredentialStore();
        var id = Guid.NewGuid();
        using var provider = new SeafileProvider(id, store);
        await provider.ConnectAsync(Values(server.Port), new ScriptedConnectPrompt());
        server.Certificate = replaced;

        var no = new ScriptedConnectPrompt { ConfirmAnswer = false };
        var declined = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(Values(server.Port), no));
        Assert.Equal(CloudProviderException.CertificateChanged().Message, declined.Message);
        var question = Assert.Single(no.Confirmations).Message;
        Assert.Contains(SelfSignedTrust.Fingerprint(original), question, StringComparison.Ordinal);
        Assert.Contains(SelfSignedTrust.Fingerprint(replaced), question, StringComparison.Ordinal);
        Assert.Contains(SelfSignedTrust.Fingerprint(original), StoredPin(store, id), StringComparison.Ordinal);

        await provider.ConnectAsync(Values(server.Port), new ScriptedConnectPrompt { ConfirmAnswer = true });
        Assert.Contains(SelfSignedTrust.Fingerprint(replaced), StoredPin(store, id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAccountFromBeforePinningPinsOnItsFirstConnection()
    {
        using var certificate = SelfSigned("CN=seafile");
        await using var server = new TinyTlsServer(certificate);
        var store = new MemoryCredentialStore();
        var id = Guid.NewGuid();
        store.Save($"seafile.{id}", new SeafileCredentials($"https://127.0.0.1:{server.Port}", "me@example.test", "t0ken") { AllowSelfSignedCertificate = true });

        using var provider = new SeafileProvider(id, store);
        await provider.ListDirectoryAsync("/");

        Assert.Contains(SelfSignedTrust.Fingerprint(certificate), StoredPin(store, id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutTheOptionAnUntrustedCertificateIsRefusedAndNothingIsPinned()
    {
        using var certificate = SelfSigned("CN=seafile");
        await using var server = new TinyTlsServer(certificate);
        var store = new MemoryCredentialStore();
        var id = Guid.NewGuid();
        using var provider = new SeafileProvider(id, store);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(Values(server.Port, allowSelfSigned: false), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.NetworkError, error.Kind);
        Assert.Null(store.Load($"seafile.{id}.certificate"));
    }

    // MARK: - Helpers

    private static ConnectValues Values(int port, bool allowSelfSigned = true) => new("default", new Dictionary<string, string>
    {
        ["serverUrl"] = $"https://127.0.0.1:{port}",
        ["email"] = "me@example.test",
        ["password"] = "pw",
        ["allowSelfSigned"] = allowSelfSigned ? "true" : "false",
    });

    /// <summary>The pinned fingerprint as stored ("" when none).</summary>
    private static string StoredPin(MemoryCredentialStore store, Guid id)
    {
        if (store.Load($"seafile.{id}.certificate") is not { } data)
        {
            return string.Empty;
        }

        using var document = System.Text.Json.JsonDocument.Parse(data);
        Assert.Equal("127.0.0.1", document.RootElement.GetProperty("host").GetString());
        return document.RootElement.GetProperty("fingerprint").GetString() ?? string.Empty;
    }

    /// <summary>A self-signed certificate with its private key, loadable by SChannel for a server.</summary>
    private static X509Certificate2 SelfSigned(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), password: null);
    }

    /// <summary>
    /// An HTTPS server on loopback answering every request with JSON and closing the
    /// connection, so each request makes a new TLS handshake with the current certificate.
    /// </summary>
    private sealed class TinyTlsServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;

        public TinyTlsServer(X509Certificate2 certificate)
        {
            Certificate = certificate;
            _listener.Start();
            _serving = Task.Run(ServeAsync);
        }

        public X509Certificate2 Certificate { get; set; }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _serving;
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }

            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => AnswerAsync(client));
            }
        }

        private async Task AnswerAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    await using var tls = new SslStream(client.GetStream());
                    await tls.AuthenticateAsServerAsync(Certificate);
                    var head = await ReadHeadAsync(tls);
                    var body = head.Contains("auth-token", StringComparison.Ordinal) ? "{\"token\":\"t0ken\"}" : "[]";
                    var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
                    await tls.WriteAsync(Encoding.UTF8.GetBytes(response));
                    await tls.FlushAsync();
                }
                catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
                {
                    // The client refused the certificate: that's what some tests are about.
                }
            }
        }

        /// <summary>The request line and headers, then whatever body Content-Length announces.</summary>
        private static async Task<string> ReadHeadAsync(Stream stream)
        {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (!EndsWithBlankLine(bytes) && await stream.ReadAsync(one) == 1)
            {
                bytes.Add(one[0]);
            }

            var head = Encoding.ASCII.GetString(bytes.ToArray());
            var length = head.Split("\r\n")
                .Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                .Select(l => int.Parse(l["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture))
                .FirstOrDefault();
            var body = new byte[length];
            var read = 0;
            while (read < length && await stream.ReadAsync(body.AsMemory(read)) is var n and > 0)
            {
                read += n;
            }

            return head;
        }

        private static bool EndsWithBlankLine(List<byte> bytes)
            => bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n';
    }
}
