// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.WebDav;

namespace FileFluss.Core.Tests.Providers.WebDavFamily;

public class WebDavParserTests
{
    /// <summary>Nextcloud 30 answering a Depth-1 PROPFIND with the oc: properties (sabre/dav).</summary>
    internal const string NextcloudListing = """
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:" xmlns:s="http://sabredav.org/ns" xmlns:oc="http://owncloud.org/ns" xmlns:nc="http://nextcloud.org/ns">
         <d:response>
          <d:href>/remote.php/dav/files/jane.doe/Documents/</d:href>
          <d:propstat>
           <d:prop>
            <d:getlastmodified>Tue, 07 Jan 2025 10:15:30 GMT</d:getlastmodified>
            <d:resourcetype><d:collection/></d:resourcetype>
            <oc:size>3150000</oc:size>
           </d:prop>
           <d:status>HTTP/1.1 200 OK</d:status>
          </d:propstat>
          <d:propstat>
           <d:prop>
            <d:getcontentlength/>
            <d:getcontenttype/>
            <d:displayname/>
            <oc:checksums/>
           </d:prop>
           <d:status>HTTP/1.1 404 Not Found</d:status>
          </d:propstat>
         </d:response>
         <d:response>
          <d:href>/remote.php/dav/files/jane.doe/Documents/Projekt%20%C3%9Cbersicht%20%26%20Plan.pdf</d:href>
          <d:propstat>
           <d:prop>
            <d:getlastmodified>Mon, 06 Jan 2025 08:00:00 GMT</d:getlastmodified>
            <d:getcontentlength>1048576</d:getcontentlength>
            <d:getcontenttype>application/pdf</d:getcontenttype>
            <d:resourcetype/>
            <oc:checksums>
             <oc:checksum>SHA1:da39a3ee5e6b4b0d3255bfef95601890afd80709 MD5:d41d8cd98f00b204e9800998ecf8427e</oc:checksum>
            </oc:checksums>
            <oc:size>1048576</oc:size>
           </d:prop>
           <d:status>HTTP/1.1 200 OK</d:status>
          </d:propstat>
          <d:propstat>
           <d:prop>
            <d:displayname/>
           </d:prop>
           <d:status>HTTP/1.1 404 Not Found</d:status>
          </d:propstat>
         </d:response>
         <d:response>
          <d:href>/remote.php/dav/files/jane.doe/Documents/Photos/</d:href>
          <d:propstat>
           <d:prop>
            <d:getlastmodified>Sun, 05 Jan 2025 21:00:00 GMT</d:getlastmodified>
            <d:resourcetype><d:collection/></d:resourcetype>
            <oc:size>2101424</oc:size>
           </d:prop>
           <d:status>HTTP/1.1 200 OK</d:status>
          </d:propstat>
          <d:propstat>
           <d:prop><d:getcontentlength/><d:getcontenttype/><d:displayname/><oc:checksums/></d:prop>
           <d:status>HTTP/1.1 404 Not Found</d:status>
          </d:propstat>
         </d:response>
         <d:response>
          <d:href>/remote.php/dav/files/jane.doe/Documents/.directory</d:href>
          <d:propstat>
           <d:prop>
            <d:getlastmodified>Sat, 04 Jan 2025 12:00:00 GMT</d:getlastmodified>
            <d:getcontentlength>0</d:getcontentlength>
            <d:resourcetype/>
           </d:prop>
           <d:status>HTTP/1.1 200 OK</d:status>
          </d:propstat>
         </d:response>
        </d:multistatus>
        """;

    /// <summary>
    /// Apache httpd mod_dav on the share root: per-response lp1/lp2 namespace prefixes, no
    /// displayname at all — so the root's own entry has no name.
    /// </summary>
    internal const string ApacheRootListing = """
        <?xml version="1.0" encoding="utf-8"?>
        <D:multistatus xmlns:D="DAV:" xmlns:ns0="DAV:">
        <D:response xmlns:lp1="DAV:" xmlns:lp2="http://apache.org/dav/props/">
        <D:href>/dav/</D:href>
        <D:propstat>
        <D:prop>
        <lp1:resourcetype><D:collection/></lp1:resourcetype>
        <lp1:getlastmodified>Wed, 15 Jan 2025 12:00:00 GMT</lp1:getlastmodified>
        </D:prop>
        <D:status>HTTP/1.1 200 OK</D:status>
        </D:propstat>
        <D:propstat>
        <D:prop>
        <D:displayname/>
        <D:getcontentlength/>
        </D:prop>
        <D:status>HTTP/1.1 404 Not Found</D:status>
        </D:propstat>
        </D:response>
        <D:response xmlns:lp1="DAV:" xmlns:lp2="http://apache.org/dav/props/">
        <D:href>/dav/report%202024.txt</D:href>
        <D:propstat>
        <D:prop>
        <lp1:resourcetype/>
        <lp1:getlastmodified>Thu, 02 Jan 2025 09:30:00 GMT</lp1:getlastmodified>
        <lp1:getcontentlength>42</lp1:getcontentlength>
        <lp2:executable>F</lp2:executable>
        <D:getcontenttype>text/plain</D:getcontenttype>
        </D:prop>
        <D:status>HTTP/1.1 200 OK</D:status>
        </D:propstat>
        </D:response>
        <D:response xmlns:lp1="DAV:" xmlns:lp2="http://apache.org/dav/props/">
        <D:href>/dav/Archive/</D:href>
        <D:propstat>
        <D:prop>
        <lp1:resourcetype><D:collection/></lp1:resourcetype>
        <lp1:getlastmodified>Fri, 20 Dec 2024 16:45:00 GMT</lp1:getlastmodified>
        </D:prop>
        <D:status>HTTP/1.1 200 OK</D:status>
        </D:propstat>
        </D:response>
        </D:multistatus>
        """;

    /// <summary>GMX MediaCenter style: default DAV namespace, absolute hrefs, displaynames, single-digit days.</summary>
    internal const string GmxListing = """
        <?xml version="1.0" encoding="UTF-8"?>
        <multistatus xmlns="DAV:">
          <response>
            <href>https://webdav.mc.gmx.net/Bilder/</href>
            <propstat>
              <prop>
                <displayname>Bilder</displayname>
                <resourcetype><collection/></resourcetype>
                <getlastmodified>Fri, 3 Jan 2025 17:45:10 GMT</getlastmodified>
              </prop>
              <status>HTTP/1.1 200 OK</status>
            </propstat>
          </response>
          <response>
            <href>https://webdav.mc.gmx.net/Bilder/Urlaub%202024.jpg</href>
            <propstat>
              <prop>
                <displayname>Urlaub 2024.jpg</displayname>
                <resourcetype/>
                <getcontentlength>2457600</getcontentlength>
                <getcontenttype>image/jpeg</getcontenttype>
                <getlastmodified>Sat, 4 Jan 2025 07:05:00 GMT</getlastmodified>
              </prop>
              <status>HTTP/1.1 200 OK</status>
            </propstat>
          </response>
          <response>
            <href>https://webdav.mc.gmx.net/Bilder/Neuer%20Ordner/</href>
            <propstat>
              <prop>
                <displayname>Neuer Ordner</displayname>
                <resourcetype><collection/></resourcetype>
              </prop>
              <status>HTTP/1.1 200 OK</status>
            </propstat>
          </response>
        </multistatus>
        """;

    [Fact]
    public void NextcloudListingParsesNamesSizesDatesAndChecksums()
    {
        var entries = WebDavResponseParser.Parse(NextcloudListing, "https://cloud.example.com/remote.php/dav/files/jane.doe");
        var items = WebDavClient.ListingItems(entries, "/Documents");

        Assert.Equal(3, items.Count);

        var pdf = items[0];
        Assert.Equal("Projekt Übersicht & Plan.pdf", pdf.Name);
        Assert.Equal("/Documents/Projekt Übersicht & Plan.pdf", pdf.Path);
        Assert.Equal(pdf.Path, pdf.Id);
        Assert.False(pdf.IsDirectory);
        Assert.Equal(1_048_576, pdf.Size);
        Assert.Equal(new DateTimeOffset(2025, 1, 6, 8, 0, 0, TimeSpan.Zero), pdf.ModificationDate);
        Assert.Equal("SHA1:da39a3ee5e6b4b0d3255bfef95601890afd80709 MD5:d41d8cd98f00b204e9800998ecf8427e", pdf.Checksum);

        var photos = items[1];
        Assert.True(photos.IsDirectory);
        Assert.Equal("/Documents/Photos", photos.Path);
        Assert.Equal(2_101_424, photos.Size); // oc:size, not getcontentlength

        var dotFile = items[2];
        Assert.Equal(".directory", dotFile.Name);
        Assert.True(dotFile.IsHidden);
    }

    [Fact]
    public void EmptyPropertiesFromA404PropstatDontOverwriteFoundOnes()
    {
        var entries = WebDavResponseParser.Parse(NextcloudListing, "https://cloud.example.com/remote.php/dav/files/jane.doe");
        var folder = entries[0];
        Assert.Equal("/Documents", folder.Path);
        Assert.Equal("Documents", folder.Name);
        Assert.True(folder.IsDirectory);
        Assert.Equal(3_150_000, folder.Size);
        Assert.Null(folder.Checksum);
    }

    [Fact]
    public void ApacheRootWithoutDisplaynameKeepsEveryChild()
    {
        // macOS dropped the first parsed item blindly; with the nameless root already skipped
        // by the parser, that cost a real child its place.
        var entries = WebDavResponseParser.Parse(ApacheRootListing, "http://files.example.org/dav");
        var items = WebDavClient.ListingItems(entries, "/");

        Assert.Equal(["report 2024.txt", "Archive"], items.Select(i => i.Name));
        var report = items[0];
        Assert.Equal("/report 2024.txt", report.Path);
        Assert.Equal(42, report.Size);
        Assert.Equal(new DateTimeOffset(2025, 1, 2, 9, 30, 0, TimeSpan.Zero), report.ModificationDate);
        Assert.True(items[1].IsDirectory);
        Assert.Equal("/Archive", items[1].Path);
    }

    [Fact]
    public void GmxAbsoluteHrefsInTheDefaultNamespace()
    {
        var entries = WebDavResponseParser.Parse(GmxListing, "https://webdav.mc.gmx.net");
        var items = WebDavClient.ListingItems(entries, "/Bilder");

        Assert.Equal(2, items.Count);
        Assert.Equal("Urlaub 2024.jpg", items[0].Name);
        Assert.Equal("/Bilder/Urlaub 2024.jpg", items[0].Path);
        Assert.Equal(2_457_600, items[0].Size);
        Assert.Equal(new DateTimeOffset(2025, 1, 4, 7, 5, 0, TimeSpan.Zero), items[0].ModificationDate);

        Assert.Equal("/Bilder/Neuer Ordner", items[1].Path);
        Assert.True(items[1].IsDirectory);
        Assert.False(items[1].HasKnownModificationDate);
    }

    [Fact]
    public void ChildrenLandUnderTheRequestedFolderEvenWhenHrefsMissTheBase()
    {
        // A server echoing the base in another form: the self entry falls back to "first",
        // children still land under the folder that was listed.
        const string xml = """
            <d:multistatus xmlns:d="DAV:">
              <d:response><d:href>/webdav/Docs/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
              <d:response><d:href>/webdav/Docs/a.txt</d:href><d:propstat><d:prop><d:getcontentlength>5</d:getcontentlength></d:prop></d:propstat></d:response>
            </d:multistatus>
            """;
        var entries = WebDavResponseParser.Parse(xml, "https://dav.example.com/remote/files");
        var items = WebDavClient.ListingItems(entries, "/Docs");

        var only = Assert.Single(items);
        Assert.Equal("/Docs/a.txt", only.Path);
        Assert.Equal(5, only.Size);
    }

    [Fact]
    public void BaseIsOnlyStrippedAtAComponentBoundary()
    {
        const string xml = """
            <d:multistatus xmlns:d="DAV:">
              <d:response><d:href>/davx/file.txt</d:href></d:response>
              <d:response><d:href>/DAV/Other.txt</d:href></d:response>
            </d:multistatus>
            """;
        var entries = WebDavResponseParser.Parse(xml, "https://h/dav/");
        Assert.Equal("/davx/file.txt", entries[0].Path);
        Assert.Equal("/Other.txt", entries[1].Path); // case-insensitive fallback (IIS)
    }

    [Fact]
    public void AMalformedBodyIsAnErrorNotAShorterFolder()
    {
        // macOS 1.6: what parsed before the error is only part of the folder.
        var truncated = ApacheRootListing[..ApacheRootListing.IndexOf("<D:href>/dav/Archive/", StringComparison.Ordinal)];
        var error = Assert.Throws<CloudProviderException>(() => WebDavResponseParser.Parse(truncated, "http://files.example.org/dav"));
        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
    }

    [Fact]
    public void DtdsAreIgnored()
    {
        const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE d:multistatus [<!ENTITY xxe SYSTEM "file:///c:/windows/win.ini">]>
            <d:multistatus xmlns:d="DAV:"><d:response><d:href>/a.txt</d:href></d:response></d:multistatus>
            """;
        var entries = WebDavResponseParser.Parse(xml, "https://h");
        Assert.Equal("/a.txt", Assert.Single(entries).Path);
    }

    [Theory]
    [InlineData("Tue, 07 Jan 2025 10:15:30 GMT")]
    [InlineData("Tue, 7 Jan 2025 10:15:30 GMT")]
    [InlineData("Tue, 07 Jan 2025 10:15:30 +0000")]
    [InlineData("Tue, 07 Jan 2025 11:15:30 +0100")]
    [InlineData("2025-01-07T10:15:30Z")]
    public void HttpDatesInTheWild(string value)
        => Assert.Equal(new DateTimeOffset(2025, 1, 7, 10, 15, 30, TimeSpan.Zero), WebDavResponseParser.ParseHttpDate(value));

    [Fact]
    public void UnparseableDateIsUnknown()
    {
        Assert.Null(WebDavResponseParser.ParseHttpDate("yesterday-ish"));
        Assert.Null(WebDavResponseParser.ParseHttpDate(""));
    }

    [Fact]
    public void Rfc1123ForProppatch()
        => Assert.Equal("Tue, 07 Jan 2025 10:15:30 GMT", WebDavResponseParser.FormatHttpDate(new DateTimeOffset(2025, 1, 7, 11, 15, 30, TimeSpan.FromHours(1))));

    [Fact]
    public void QuotaIsUsedPlusAvailable()
    {
        const string xml = """
            <D:multistatus xmlns:D="DAV:"><D:response><D:href>/dav/</D:href><D:propstat><D:prop><D:quota-available-bytes>750</D:quota-available-bytes><D:quota-used-bytes>250</D:quota-used-bytes></D:prop></D:propstat></D:response></D:multistatus>
            """;
        var quota = WebDavResponseParser.ParseQuota(xml);
        Assert.Equal(new CloudStorageQuota(250, 1000) { FetchedAt = quota!.FetchedAt }, quota);
        Assert.Equal(750, quota.FreeBytes);
    }

    [Fact]
    public void NextcloudUnlimitedQuotaHasNoTotal()
    {
        const string xml = """
            <d:multistatus xmlns:d="DAV:"><d:response><d:href>/remote.php/dav/files/x/</d:href><d:propstat><d:prop><d:quota-used-bytes>123456</d:quota-used-bytes><d:quota-available-bytes>-3</d:quota-available-bytes></d:prop></d:propstat></d:response></d:multistatus>
            """;
        var quota = WebDavResponseParser.ParseQuota(xml);
        Assert.Equal(123_456, quota!.UsedBytes);
        Assert.Null(quota.TotalBytes);
    }

    [Fact]
    public void ServerWithoutRfc4331HasNoQuota()
    {
        const string xml = """
            <d:multistatus xmlns:d="DAV:"><d:response><d:href>/</d:href><d:propstat><d:prop><d:quota-used-bytes/><d:quota-available-bytes/></d:prop><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat></d:response></d:multistatus>
            """;
        Assert.Null(WebDavResponseParser.ParseQuota(xml));
    }
}
