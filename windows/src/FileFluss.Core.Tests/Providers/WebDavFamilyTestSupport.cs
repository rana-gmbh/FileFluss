// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Text;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers.WebDavFamily;

/// <summary>Helpers shared by the WebDAV, NextCloud, GMX, Koofr and kDrive provider tests.</summary>
internal static class WebDavFamilyTestSupport
{
    public static readonly HttpMethod Propfind = new("PROPFIND");
    public static readonly HttpMethod Proppatch = new("PROPPATCH");
    public static readonly HttpMethod Mkcol = new("MKCOL");
    public static readonly HttpMethod Move = new("MOVE");
    public static readonly HttpMethod Copy = new("COPY");
    public static readonly HttpMethod Search = new("SEARCH");

    public static HttpResponseMessage Multistatus(string xml)
        => new((HttpStatusCode)207) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };

    public static HttpResponseMessage Status(HttpStatusCode status, string body = "")
        => new(status) { Content = new StringContent(body) };

    public static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(string.Empty) };
        response.Headers.Location = new Uri(location);
        return response;
    }

    public static FakeHttpHandler.RecordedRequest[] All(this FakeHttpHandler http, HttpMethod method)
        => http.Requests.Where(r => r.Method == method).ToArray();

    public static string? Header(this FakeHttpHandler.RecordedRequest request, string name)
        => request.Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>A scratch folder with files for upload/download tests, deleted afterwards.</summary>
internal sealed class DavTempFolder : IDisposable
{
    public DavTempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ff-dav-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name, int bytes, DateTime? modifiedUtc = null)
    {
        var path = System.IO.Path.Combine(Path, name);
        var data = new byte[bytes];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)('a' + (i % 26));
        }

        System.IO.File.WriteAllBytes(path, data);
        if (modifiedUtc is { } modified)
        {
            System.IO.File.SetLastWriteTimeUtc(path, modified);
        }

        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
