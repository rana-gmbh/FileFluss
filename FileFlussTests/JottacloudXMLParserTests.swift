import Testing
import Foundation
import FileFlussCore

/// Pins the two Jottacloud "JFS" XML shapes FileFluss parses — a single-level
/// folder listing and the account-root capacity/usage. The JFS API is
/// undocumented, so a server-format regression should fail here rather than
/// surface as an empty panel or a wrong quota.
@Suite("Jottacloud XML parser")
struct JottacloudXMLParserTests {

    private let listingXML = """
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
    """

    @Test("Lists child folders and completed files")
    func parsesListing() {
        let items = JottacloudXMLParser.parseListing(data: Data(listingXML.utf8), basePath: "/")
        let names = Set(items.map(\.name))
        #expect(names.contains("Documents"))
        #expect(names.contains("Photos"))
        #expect(names.contains("report.pdf"))
        // Trashed folder and incomplete (non-COMPLETED) file are hidden.
        #expect(!names.contains("Trashed"))
        #expect(!names.contains("incomplete.bin"))
    }

    @Test("File metadata: size, md5, directory flag, path")
    func parsesFileMetadata() {
        let items = JottacloudXMLParser.parseListing(data: Data(listingXML.utf8), basePath: "/")
        let file = items.first { $0.name == "report.pdf" }
        #expect(file?.isDirectory == false)
        #expect(file?.size == 20480)
        #expect(file?.checksum == "0123456789abcdef0123456789abcdef")
        #expect(file?.path == "/report.pdf")

        let folder = items.first { $0.name == "Documents" }
        #expect(folder?.isDirectory == true)
        #expect(folder?.path == "/Documents")
    }

    @Test("File modified date parses (regression: broken JFS date format)")
    func parsesModifiedDate() {
        let items = JottacloudXMLParser.parseListing(data: Data(listingXML.utf8), basePath: "/")
        let file = items.first { $0.name == "report.pdf" }
        // JFS sends "2024-01-15-T10:30:00Z" — must parse, not fall back to epoch.
        let comps = Calendar(identifier: .gregorian).dateComponents(
            in: TimeZone(identifier: "UTC")!, from: file?.modificationDate ?? .distantPast)
        #expect(comps.year == 2024)
        #expect(comps.month == 1)
        #expect(comps.day == 15)
        #expect((file?.modificationDate.timeIntervalSince1970 ?? 0) > 1_000_000_000)
    }

    @Test("Nested base path is joined onto child names")
    func parsesNestedPaths() {
        let items = JottacloudXMLParser.parseListing(data: Data(listingXML.utf8), basePath: "/Pictures")
        #expect(items.first { $0.name == "report.pdf" }?.path == "/Pictures/report.pdf")
        #expect(items.first { $0.name == "Documents" }?.path == "/Pictures/Documents")
    }

    @Test("Account usage with a finite capacity")
    func parsesFiniteQuota() {
        let xml = """
        <user>
          <username>user</username>
          <account-type>free</account-type>
          <capacity>5368709120</capacity>
          <usage>1073741824</usage>
          <devices><device><name>Jotta</name><usage>1073741824</usage></device></devices>
        </user>
        """
        let usage = JottacloudXMLParser.parseAccountUsage(data: Data(xml.utf8))
        #expect(usage?.capacity == 5368709120)
        #expect(usage?.usage == 1073741824)
    }

    @Test("Unlimited capacity reported as -1")
    func parsesUnlimitedQuota() {
        let xml = """
        <user>
          <username>user</username>
          <account-type>unlimited</account-type>
          <capacity>-1</capacity>
          <usage>42</usage>
        </user>
        """
        let usage = JottacloudXMLParser.parseAccountUsage(data: Data(xml.utf8))
        #expect(usage?.capacity == -1)
        #expect(usage?.usage == 42)
    }
}
