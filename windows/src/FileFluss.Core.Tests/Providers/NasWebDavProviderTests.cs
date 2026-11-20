// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Text;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.WebDav;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;
using static FileFluss.Core.Tests.Providers.WebDavFamily.WebDavFamilyTestSupport;

namespace FileFluss.Core.Tests.Providers.WebDavFamily;

/// <summary>UGREEN and ASUSTOR: WebDAV with each maker's port and a self-signed certificate allowed (macOS NASProviderTests).</summary>
public sealed class NasWebDavProviderTests
{
    private const string RootSelf = """
        <d:multistatus xmlns:d="DAV:"><d:response><d:href>/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response></d:multistatus>
        """;

    private readonly FakeHttpHandler _http = new();
    private readonly MemoryCredentialStore _store = new();
    private readonly Guid _id = Guid.NewGuid();

    private static ConnectValues Values(string server, string password = "s3cret", bool? allowSelfSigned = null)
    {
        var values = new Dictionary<string, string> { ["serverUrl"] = server, ["username"] = " robert ", ["password"] = password };
        if (allowSelfSigned is { } allow)
        {
            values["allowSelfSigned"] = allow.ToString();
        }

        return new ConnectValues("default", values);
    }

    private static HttpResponseMessage Writable()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty) };
        response.Content.Headers.Allow.Add("MKCOL");
        return response;
    }

    [Theory]
    [InlineData("nas.local", "https://nas.local:5006")]
    [InlineData(" 192.168.1.20 ", "https://192.168.1.20:5006")]
    [InlineData("https://nas.local/", "https://nas.local:5006")]
    [InlineData("http://nas.local", "http://nas.local:5005")]
    [InlineData("https://nas.local:443", "https://nas.local:443")]
    [InlineData("https://nas.local:8443/dav/", "https://nas.local:8443/dav")]
    [InlineData("https://nas.local/dav", "https://nas.local:5006/dav")]
    public void UgreenFillsInItsWebDavPorts(string typed, string expected)
        => Assert.Equal(expected, NasWebDavProvider.NormalizeServerUrl(typed, NasWebDavBrand.Ugreen));

    [Theory]
    [InlineData("nas.local", "https://nas.local:9802")]
    [InlineData("http://nas.local", "http://nas.local:9800")]
    [InlineData("HTTP://NAS.local", "HTTP://NAS.local:9800")]
    [InlineData("https://[fd00::1]", "https://[fd00::1]:9802")]
    [InlineData("nas.local:9443", "https://nas.local:9443")]
    public void AsustorFillsInItsWebDavPorts(string typed, string expected)
        => Assert.Equal(expected, NasWebDavProvider.NormalizeServerUrl(typed, NasWebDavBrand.Asustor));

    [Fact]
    public void FormMirrorsTheMacSheet()
    {
        using var ugreen = new UgreenNasProvider(_id, _store, _http);
        using var asustor = new AsustorNasProvider(_id, _store, _http);

        var fields = ugreen.ConnectSpec.Modes.Single().Fields;
        Assert.Equal(["serverUrl", "username", "password", "allowSelfSigned"], fields.Where(f => f.HasValue).Select(f => f.Key));
        Assert.Equal("Server URL (e.g. https://my-nas.local:5006)", fields.Single(f => f.Key == "serverUrl").Placeholder);
        Assert.Equal(ConnectFieldKind.Url, fields.Single(f => f.Key == "serverUrl").Kind);
        Assert.Equal("true", fields.Single(f => f.Key == "allowSelfSigned").DefaultValue);
        Assert.Contains("5006", fields[0].Label, StringComparison.Ordinal);
        Assert.Contains("9802", asustor.ConnectSpec.Modes.Single().Fields[0].Label, StringComparison.Ordinal);
        Assert.Equal(4_000_000_000, ugreen.MaxUploadFileSize);
        Assert.Equal(ProviderCategory.Nas, CloudProviderType.Ugreen.Category());
        Assert.Equal("UGREEN NAS", CloudProviderType.Ugreen.DisplayName());
        Assert.Equal("ASUSTOR", CloudProviderType.Asustor.DisplayName());
    }

    [Fact]
    public async Task ConnectReachesTheDefaultPortAndStoresTheCertificateChoice()
    {
        // Routes match by URL fragment in order, so the more specific one goes first.
        _http.On(Propfind, "https://nas.local:5006/Photos", (_, _) => Multistatus("""
            <d:multistatus xmlns:d="DAV:">
              <d:response><d:href>/Photos/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
              <d:response><d:href>/Photos/a.jpg</d:href><d:propstat><d:prop><d:getcontentlength>7</d:getcontentlength></d:prop></d:propstat></d:response>
            </d:multistatus>
            """));
        _http.On(Propfind, "https://nas.local:5006/", (_, _) => Multistatus(RootSelf));
        _http.On(HttpMethod.Options, "https://nas.local:5006/", (_, _) => Writable());
        using var provider = new UgreenNasProvider(_id, _store, _http);

        var result = await provider.ConnectAsync(Values("nas.local"), new ScriptedConnectPrompt());

        Assert.Equal("robert@nas.local", result.AccountLabel);
        Assert.Equal("UGREEN NAS (robert@nas.local)", CloudAccount.ComposeDisplayName(CloudProviderType.Ugreen, result.AccountLabel));
        var stored = _store.Load<WebDavCredentials>($"ugreen.{_id}")!;
        Assert.Equal("https://nas.local:5006", stored.ServerUrl);
        Assert.Equal("robert", stored.Username);
        Assert.True(stored.AllowSelfSignedCertificate);
        Assert.Equal(
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("robert:s3cret")),
            _http.Single(Propfind, "https://nas.local:5006/").Header("Authorization"));
        Assert.Equal(
            new Dictionary<string, string> { ["serverUrl"] = "https://nas.local:5006", ["username"] = "robert", ["allowSelfSigned"] = "true" },
            provider.GetEditableConnectValues());

        // The session is used straight away.
        var item = Assert.Single(await provider.ListDirectoryAsync("/Photos"));
        Assert.Equal("/Photos/a.jpg", item.Path);
        Assert.Equal(7, item.Size);
    }

    [Fact]
    public async Task TurningTheCertificateExemptionOffIsRemembered()
    {
        _http.On(Propfind, "https://nas.local:9802/", (_, _) => Multistatus(RootSelf));
        _http.On(HttpMethod.Options, "https://nas.local:9802/", (_, _) => Writable());
        using var provider = new AsustorNasProvider(_id, _store, _http);

        await provider.ConnectAsync(Values("nas.local", allowSelfSigned: false), new ScriptedConnectPrompt());

        Assert.Null(_store.Load<WebDavCredentials>($"asustor.{_id}")!.AllowSelfSignedCertificate);
        Assert.Equal("false", provider.GetEditableConnectValues()["allowSelfSigned"]);
    }

    [Fact]
    public async Task NothingAnsweringSaysWhatToCheck()
    {
        _http.On(Propfind, "nas.local:9802", (_, _) => throw new HttpRequestException("Connection refused"));
        using var provider = new AsustorNasProvider(_id, _store, _http);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(Values("nas.local"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Contains("https://nas.local:9802", error.Message, StringComparison.Ordinal);
        Assert.Contains("WebDAV", error.Message, StringComparison.Ordinal);
        Assert.False(provider.IsAuthenticated);
        Assert.Null(_store.Load($"asustor.{_id}"));
    }

    [Fact]
    public async Task WrongPasswordLeavesStoredCredentialsUntouched()
    {
        var before = new WebDavCredentials("https://nas.local:5006", "robert", "old", "robert@nas.local", true);
        _store.Save($"ugreen.{_id}", before);
        _http.On(Propfind, "https://nas.local:5006/", (_, _) => Status(HttpStatusCode.Unauthorized));
        using var provider = new UgreenNasProvider(_id, _store, _http);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(Values("nas.local", password: "wrong"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.InvalidCredentials, error.Kind);
        Assert.Equal(before, _store.Load<WebDavCredentials>($"ugreen.{_id}"));
        Assert.True(provider.IsAuthenticated);
    }

    [Fact]
    public void CredentialsSavedBeforeTheCertificateOptionStillLoad()
    {
        // A macOS WebDAVCredentials from before the option existed: no allowSelfSignedCertificate key.
        _store.Save($"ugreen.{_id}", Encoding.UTF8.GetBytes("""{"serverURL":"https://nas.local:5006","username":"robert","password":"pw","displayName":"robert@nas.local"}"""));
        using var provider = new UgreenNasProvider(_id, _store, _http);

        Assert.True(provider.IsAuthenticated);
        Assert.Equal("false", provider.GetEditableConnectValues()["allowSelfSigned"]);
    }

    [Fact]
    public async Task DisconnectForgetsTheAccount()
    {
        _store.Save($"asustor.{_id}", new WebDavCredentials("https://nas.local:9802", "robert", "pw", "robert@nas.local", true));
        using var provider = new AsustorNasProvider(_id, _store, _http);

        await provider.DisconnectAsync();

        Assert.False(provider.IsAuthenticated);
        Assert.Null(_store.Load($"asustor.{_id}"));
    }
}
