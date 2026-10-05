// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Providers.Jottacloud;

namespace FileFluss.Core.Tests.Providers;

/// <summary>
/// Port of the macOS <c>JottacloudXMLParserTests</c>: pins the JFS XML shapes FileFluss parses.
/// The API is undocumented, so a format regression should fail here rather than surface as an
/// empty panel or a wrong quota.
/// </summary>
public class JottacloudXmlParserTests
{
    private const string ListingXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <folder name="Archive" time="2024-01-15-T10:30:00Z" host="dn-xxx">
          <path xmlns:xlink="http://www.w3.org/1999/xlink">/user/Jotta</path>
          <abspath xmlns:xlink="http://www.w3.org/1999/xlink">/user/Jotta</abspath>
          <folders>
            <folder name="Documents"></folder>
            <folder name="Photos"></folder>
            <folder name="Trashed" deleted="2024-02-01-T00:00:00Z"></folder>
          </folders>
          <files>
            <file name="report.pdf" uuid="aaaa">
              <currentRevision>
                <number>1</number>
                <state>COMPLETED</state>
                <created>2024-01-10-T09:00:00Z</created>
                <modified>2024-01-15-T10:30:00Z</modified>
                <mime>application/pdf</mime>
                <size>20480</size>
                <md5>0123456789abcdef0123456789abcdef</md5>
                <updated>2024-01-15-T10:30:00Z</updated>
              </currentRevision>
            </file>
            <file name="incomplete.bin" uuid="bbbb">
              <latestRevision>
                <number>1</number>
                <state>INCOMPLETE</state>
                <size>999</size>
              </latestRevision>
            </file>
          </files>
        </folder>
        """;

    [Fact]
    public void ListsChildFoldersAndCompletedFiles()
    {
        var names = JottacloudXmlParser.ParseListing(ListingXml, "/").Select(i => i.Name).ToHashSet();
        Assert.Contains("Documents", names);
        Assert.Contains("Photos", names);
        Assert.Contains("report.pdf", names);

        // Trashed folder and incomplete (non-COMPLETED) file are hidden.
        Assert.DoesNotContain("Trashed", names);
        Assert.DoesNotContain("incomplete.bin", names);
    }

    [Fact]
    public void FileMetadataSizeMd5DirectoryFlagPath()
    {
        var items = JottacloudXmlParser.ParseListing(ListingXml, "/");
        var file = Assert.Single(items, i => i.Name == "report.pdf");
        Assert.False(file.IsDirectory);
        Assert.Equal(20480, file.Size);
        Assert.Equal("0123456789abcdef0123456789abcdef", file.Checksum);
        Assert.Equal("/report.pdf", file.Path);

        var folder = Assert.Single(items, i => i.Name == "Documents");
        Assert.True(folder.IsDirectory);
        Assert.Equal("/Documents", folder.Path);
        Assert.False(folder.HasKnownModificationDate);
    }

    [Fact]
    public void FileModifiedDateParses()
    {
        // JFS sends "2024-01-15-T10:30:00Z" — must parse, not fall back.
        var file = Assert.Single(JottacloudXmlParser.ParseListing(ListingXml, "/"), i => i.Name == "report.pdf");
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.Zero), file.ModificationDate);
    }

    [Fact]
    public void NestedBasePathIsJoinedOntoChildNames()
    {
        var items = JottacloudXmlParser.ParseListing(ListingXml, "/Pictures");
        Assert.Equal("/Pictures/report.pdf", Assert.Single(items, i => i.Name == "report.pdf").Path);
        Assert.Equal("/Pictures/Documents", Assert.Single(items, i => i.Name == "Documents").Path);
    }

    [Fact]
    public void AccountUsageWithAFiniteCapacity()
    {
        const string xml = """
            <user>
              <username>user</username>
              <account-type>free</account-type>
              <capacity>5368709120</capacity>
              <usage>1073741824</usage>
              <devices><device><name>Jotta</name><usage>1073741824</usage></device></devices>
            </user>
            """;
        var usage = JottacloudXmlParser.ParseAccountUsage(xml);
        Assert.NotNull(usage);
        Assert.Equal(5368709120, usage.Capacity);
        Assert.Equal(1073741824, usage.Usage);
    }

    [Fact]
    public void UnlimitedCapacityReportedAsMinusOne()
    {
        const string xml = """
            <user>
              <username>user</username>
              <account-type>unlimited</account-type>
              <capacity>-1</capacity>
              <usage>42</usage>
            </user>
            """;
        var usage = JottacloudXmlParser.ParseAccountUsage(xml);
        Assert.NotNull(usage);
        Assert.Equal(-1, usage.Capacity);
        Assert.Equal(42, usage.Usage);
    }

    [Fact]
    public void DeviceUsageDoesNotOverwriteTheAccountUsage()
    {
        const string xml = "<user><devices><device><usage>7</usage></device></devices><usage>42</usage><capacity>100</capacity></user>";
        var usage = JottacloudXmlParser.ParseAccountUsage(xml);
        Assert.NotNull(usage);
        Assert.Equal(42, usage.Usage);
        Assert.Equal(100, usage.Capacity);
    }

    [Fact]
    public void CurrentRevisionWinsOverAPendingReupload()
    {
        // While a new version uploads, the file keeps its finished currentRevision.
        const string xml = """
            <folder name="x"><files>
              <file name="a.txt">
                <currentRevision><state>COMPLETED</state><size>10</size><md5>aaa</md5><modified>2024-01-01-T00:00:00Z</modified></currentRevision>
                <latestRevision><state>INCOMPLETE</state><size>99</size><md5>bbb</md5></latestRevision>
              </file>
            </files></folder>
            """;
        var file = Assert.Single(JottacloudXmlParser.ParseListing(xml, "/"));
        Assert.Equal(10, file.Size);
        Assert.Equal("aaa", file.Checksum);
    }

    [Fact]
    public void FolderOwnModifiedDateComesFromTheTopLevel()
    {
        const string xml = """
            <folder name="Documents" time="2023-05-05-T05:05:05Z">
              <modified>2024-03-01-T12:00:00Z</modified>
              <files><file name="a"><currentRevision><modified>1999-01-01-T00:00:00Z</modified></currentRevision></file></files>
            </folder>
            """;
        Assert.Equal(new DateTimeOffset(2024, 3, 1, 12, 0, 0, TimeSpan.Zero), JottacloudXmlParser.ParseFolderModified(xml));

        const string onlyAttribute = "<folder name=\"D\" time=\"2023-05-05-T05:05:05Z\"><folders/></folder>";
        Assert.Equal(new DateTimeOffset(2023, 5, 5, 5, 5, 5, TimeSpan.Zero), JottacloudXmlParser.ParseFolderModified(onlyAttribute));
        Assert.Null(JottacloudXmlParser.ParseFolderModified("<folder name=\"D\"/>"));
    }

    [Fact]
    public void PublicShareFieldsAndErrorMessage()
    {
        var share = JottacloudXmlParser.ParsePublicShare("<file name=\"a\"><publicURI>abc123</publicURI><publicSharePath>/s/xyz</publicSharePath></file>");
        Assert.Equal(new JottacloudXmlParser.PublicShare("abc123", "/s/xyz"), share);
        Assert.Null(JottacloudXmlParser.ParsePublicShare("<file name=\"a\"><publicURI></publicURI></file>"));
        Assert.Equal("Sharing disabled", JottacloudXmlParser.ParseError("<error><code>403</code><message>Sharing disabled</message><reason>Forbidden</reason></error>"));
        Assert.Equal("Forbidden", JottacloudXmlParser.ParseError("<error><message></message><reason>Forbidden</reason></error>"));
    }

    [Fact]
    public void TruncatedXmlKeepsWhatParsed()
    {
        const string xml = "<folder><folders><folder name=\"A\"></folder><folder name=\"B\"></folder><fol";
        var names = JottacloudXmlParser.ParseListing(xml, "/").Select(i => i.Name).ToList();
        Assert.Equal(["A", "B"], names);
    }

    [Theory]
    [InlineData("2024-01-15-T10:30:00Z", 2024, 10)]
    [InlineData("2024-01-15T10:30:00.123+01:00", 2024, 9)]
    [InlineData("2024-01-15T10:30:00", 2024, 10)]
    public void DatesParseInTheirVariants(string value, int year, int utcHour)
    {
        var date = JottacloudXmlParser.ParseDate(value);
        Assert.Equal(year, date.UtcDateTime.Year);
        Assert.Equal(utcHour, date.UtcDateTime.Hour);
    }

    [Fact]
    public void UnparseableDateIsUnknown()
        => Assert.Equal(CloudFileItem.UnknownDate, JottacloudXmlParser.ParseDate("not a date"));
}
