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
    public void TheSupportLogStoresOnlyTheMaskedText()
    {
        var marker = Guid.NewGuid().ToString("N");
        var since = DateTimeOffset.Now.AddSeconds(-1);
        SupportLogger.Shared.Log($"{marker} failed: {{\"access_token\":\"leaked-{marker}\"}}", "Test");

        var entry = Assert.Single(SupportLogger.Shared.Snapshot(since), e => e.Message.StartsWith(marker, StringComparison.Ordinal));
        Assert.DoesNotContain("leaked-", entry.Message, StringComparison.Ordinal);
    }
}
