// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Net;

namespace FileFluss.Core.Tests.Security;

/// <summary>Port of the macOS 1.6 CredentialScope tests: where login details may go.</summary>
public sealed class CredentialScopeTests
{
    [Theory]
    [InlineData("https://dav.example.com/a", "https://dav.example.com/b", true)]
    [InlineData("https://DAV.example.com/a", "https://dav.example.com/b", true)]
    [InlineData("http://nas.local:5005/", "http://nas.local:5005/x", true)]
    [InlineData("http://example.com/", "https://example.com/", true)] // the usual upgrade
    [InlineData("http://example.com:8080/", "https://example.com/", false)]
    [InlineData("https://example.com/", "http://example.com/", false)] // never down to plain text
    [InlineData("https://example.com/", "https://example.com:8443/", false)]
    [InlineData("https://example.com/", "https://cdn.example.com/", false)]
    [InlineData("https://example.com/", "ftp://example.com/", false)]
    public void CredentialsStayWithTheirHostPortAndScheme(string origin, string target, bool allowed)
        => Assert.Equal(allowed, CredentialScope.Allows(new Uri(target), new Uri(origin)));

    [Fact]
    public void NothingKnownAllowsNothing()
    {
        Assert.False(CredentialScope.Allows(null, new Uri("https://example.com/")));
        Assert.False(CredentialScope.Allows(new Uri("https://example.com/"), null));
    }

    [Theory]
    [InlineData("http://cloud.example.com", true)]
    [InlineData("  HTTP://203.0.113.5/dav  ", true)]
    [InlineData("https://cloud.example.com", false)]
    [InlineData("http://localhost:8080", false)]
    [InlineData("http://nas.local", false)]
    [InlineData("http://192.168.1.20:5005", false)]
    [InlineData("http://10.0.0.2", false)]
    [InlineData("http://172.20.1.1", false)]
    [InlineData("http://172.32.1.1", true)]
    [InlineData("http://169.254.3.4", false)]
    [InlineData("http://127.0.0.1", false)]
    [InlineData("cloud.example.com", false)]
    public void PlainHttpBeyondTheLocalNetworkExposesThePassword(string address, bool exposed)
        => Assert.Equal(exposed, CredentialScope.ExposesPassword(address));
}
