// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;

namespace FileFluss.Core.Tests.Security;

public sealed class LogRedactorTests
{
    [Theory]
    [InlineData("Authorization: Bearer eyJhbGciOiJSUzI1NiJ9.payload.sig", "eyJhbGci")]
    [InlineData("header Bearer ya29.a0AfH6SMBx-secret", "ya29.a0AfH6SMBx-secret")]
    [InlineData("sent Basic dXNlcjpwYXNz to the server", "dXNlcjpwYXNz")]
    [InlineData("""[Dropbox] Token exchange failed: HTTP 400: {"access_token": "sl.ABCDEF123", "token_type": "bearer"}""", "sl.ABCDEF123")]
    [InlineData("""{"refresh_token":"1//0gXYZ","expires_in":3599}""", "1//0gXYZ")]
    [InlineData("""{"password":"hunter2 with spaces"}""", "hunter2")]
    [InlineData("""{"client_secret":"GOCSPX-abc"}""", "GOCSPX-abc")]
    [InlineData("GET https://eapi.pcloud.com/listfolder?auth=SECRETAUTH&folderid=0", "SECRETAUTH")]
    [InlineData("https://bucket.s3.amazonaws.com/a.txt?X-Amz-Credential=AKIA%2F2026&X-Amz-Signature=deadbeef01", "deadbeef01")]
    [InlineData("https://bucket.s3.amazonaws.com/a.txt?X-Amz-Credential=AKIAEXAMPLE&X-Amz-Signature=x", "AKIAEXAMPLE")]
    [InlineData("callback /?code=4/0AY0e-g7&state=abc", "4/0AY0e-g7")]
    [InlineData("https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=ADPycdv123", "ADPycdv123")]
    [InlineData("Connecting to https://robert:s3cr3t@dav.example.com/remote.php", "s3cr3t")]
    [InlineData("request had Cookie: session=abcdef; other=1", "abcdef")]
    [InlineData("X-Auth-Token: 0123456789abcdef", "0123456789abcdef")]
    [InlineData("access_token=abc123&refresh_token=def456", "def456")]
    [InlineData("GET /webapi/entry.cgi?api=SYNO.API.Info&_sid=SYNOSESSION42&version=1", "SYNOSESSION42")]
    [InlineData("""{"data":{"sid":"x","synotoken":"SYNOTOK99"}}""", "SYNOTOK99")]
    [InlineData("X-SYNO-TOKEN: ABCDEF0123", "ABCDEF0123")]
    [InlineData("QTS login ok: qtoken=QT0KEN77&vtoken=VT0KEN88", "QT0KEN77")]
    [InlineData("QTS login ok: qtoken=QT0KEN77&vtoken=VT0KEN88", "VT0KEN88")]
    [InlineData("""[Mega] answer {"csid":"CSIDSECRET","tsid":"TSIDSECRET"}""", "CSIDSECRET")]
    [InlineData("""[Mega] answer {"csid":"CSIDSECRET","tsid":"TSIDSECRET"}""", "TSIDSECRET")]
    [InlineData("""{"secretAccessKey":"wJalrXUtnFEMI"}""", "wJalrXUtnFEMI")]
    [InlineData("X-SEAFILE-OTP: 123456", "123456")]
    public void SecretsAreMasked(string message, string secret)
    {
        var redacted = LogRedactor.Redact(message);
        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains(LogRedactor.Mask, redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Transfer of report.pdf failed: The request timed out.")]
    [InlineData("[Box] Token exchange failed: HTTP 400")]
    [InlineData("[WebDAV] PROPFIND /Documents/Projects → HTTP 207")]
    [InlineData("The server uses Basic authentication over plain HTTP")]
    [InlineData("Connect to sftp.example.com:22 failed: SocketException: No such host is known.")]
    [InlineData("Listing /a=b/c failed")]
    public void OrdinaryMessagesAreUntouched(string message)
        => Assert.Equal(message, LogRedactor.Redact(message));

    [Fact]
    public void LongMessagesAreCutAfterMasking()
    {
        // A server's HTML error page, with a token near where the cut falls.
        var page = new string('x', LogRedactor.MaxMessageLength - 10) + "access_token=SPLITTOKEN123456&more" + new string('y', 50_000);

        var stored = LogRedactor.Cap(LogRedactor.Redact(page));

        Assert.True(stored.Length < LogRedactor.MaxMessageLength + 100);
        Assert.DoesNotContain("SPLIT", stored, StringComparison.Ordinal);
        Assert.EndsWith("more characters)", stored, StringComparison.Ordinal);
        Assert.Equal("short", LogRedactor.Cap("short"));
    }

    [Fact]
    public void TheSupportLogStoresOnlyTheMaskedText()
    {
        var marker = Guid.NewGuid().ToString("N");
        var since = DateTimeOffset.Now.AddSeconds(-1);
        SupportLogger.Shared.Log($"{marker} failed: {{\"access_token\":\"leaked-{marker}\"}}", "Test");

        var entry = Assert.Single(SupportLogger.Shared.Snapshot(since), e => e.Message.StartsWith(marker, StringComparison.Ordinal));
        Assert.DoesNotContain("leaked-", entry.Message, StringComparison.Ordinal);
    }
}
