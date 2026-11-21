import Testing
import Foundation
@testable import FileFlussCore

private func components(_ date: Date) -> DateComponents {
    Calendar.current.dateComponents([.year, .month, .day, .hour, .minute], from: date)
}

/// Rows from servers that trim the usual `ls -l` format. RFC 959 doesn't fix
/// what `LIST` sends, and small FTP servers on hardware boards leave fields
/// out (issue #70).
@Suite("Listings with fields left out")
struct TrimmedListingTests {

    /// Exactly as OpenHASP's FTP server on a "Cheap Yellow Display" sends
    /// them: no group column, fields separated by tabs.
    private let openHASP = [
        "-rw-rw-r--\t1\tUserName\t775\tNov 21 12:41\tconfig.json",
        "-rw-rw-r--\t1\tUserName\t76\tJan 01  1970\toffline.cmd",
        "-rw-rw-r--\t1\tUserName\t73\tJan 01  1970\tonline.cmd",
        "-rw-rw-r--\t1\tUserName\t20388\tNov 21 11:50\tpages.jsonl",
    ]

    @Test("rows without a group column are read — issue #70")
    func noGroupColumn() throws {
        let rows = try openHASP.map { try #require(SFTPAPIClient.parseListingLine($0)) }
        #expect(rows.map(\.name) == ["config.json", "offline.cmd", "online.cmd", "pages.jsonl"])
        #expect(rows.map(\.size) == [775, 76, 73, 20388])
        #expect(rows.allSatisfy { !$0.isDirectory })

        let old = components(rows[1].modDate)
        #expect(old.year == 1970 && old.month == 1 && old.day == 1)
        let recent = components(rows[0].modDate)
        #expect(recent.month == 11 && recent.day == 21 && recent.hour == 12 && recent.minute == 41)
    }

    @Test("the whole issue #70 listing makes a folder, not an error")
    func wholeListing() throws {
        let output = openHASP.joined(separator: "\r\n") + "\r\n"
        let items = try FTPAPIClient.parseListing(output: output, basePath: "/")
        #expect(items.map(\.path) == ["/config.json", "/offline.cmd", "/online.cmd", "/pages.jsonl"])
    }

    @Test("a folder without a group column")
    func folderWithoutGroup() {
        let row = SFTPAPIClient.parseListingLine("drwxrwxr-x\t1\tUserName\t0\tNov 21 12:41\tpages")
        #expect(row?.name == "pages")
        #expect(row?.isDirectory == true)
    }

    @Test("rows without owner and group, or without a link count")
    func moreFieldsLeftOut() {
        let noOwner = SFTPAPIClient.parseListingLine("-rw-r--r-- 1 1234 Nov 21 12:41 a.txt")
        #expect(noOwner?.name == "a.txt")
        #expect(noOwner?.size == 1234)

        let noLinks = SFTPAPIClient.parseListingLine("-rw-r--r-- owner group 99 Nov 21 12:41 b.txt")
        #expect(noLinks?.name == "b.txt")
        #expect(noLinks?.size == 99)

        let noGroupISO = SFTPAPIClient.parseListingLine("-rw-r--r-- 1 owner 512 2026-11-21 12:41 c.txt")
        #expect(noGroupISO?.name == "c.txt")
        #expect(noGroupISO?.size == 512)
    }

    @Test("numeric owners aren't taken for the size")
    func numericOwners() {
        let row = SFTPAPIClient.parseListingLine("-rw-r--r-- 1 1000 775 Nov 21 12:41 d.txt")
        #expect(row?.size == 775)
        #expect(row?.name == "d.txt")
    }

    @Test("the day before the month, and a month in capitals")
    func otherDateOrders() {
        let dayFirst = SFTPAPIClient.parseListingLine("-rw-r--r-- 1 owner 42 21 Nov 12:41 e.txt")
        #expect(dayFirst?.name == "e.txt")
        #expect(dayFirst.map { components($0.modDate).day } == 21)

        let capitals = SFTPAPIClient.parseListingLine("-rw-r--r-- 1 owner 42 NOV 21 2025 f.txt")
        #expect(capitals.map { components($0.modDate).month } == 11)
        #expect(capitals.map { components($0.modDate).year } == 2025)
    }

    @Test("names with spaces survive")
    func namesWithSpaces() {
        let row = SFTPAPIClient.parseListingLine("-rw-rw-r--\t1\tUserName\t10\tNov 21 12:41\tmy notes 2026.txt")
        #expect(row?.name == "my notes 2026.txt")
    }

    @Test("rows the strict patterns read come out exactly as before")
    func strictRowsUnchanged() {
        let classic = SFTPAPIClient.parseListingLine("-rw-r--r--   1 owner group 12345 Jan 31 12:34 file.txt")
        #expect(classic?.name == "file.txt")
        #expect(classic?.size == 12345)
        let iso = SFTPAPIClient.parseListingLine("-rw-r--r-- 1 owner group 12345 2026-01-31 12:34:56 file.txt")
        #expect(iso?.name == "file.txt")
        #expect(iso?.size == 12345)
    }

    @Test("a row that still can't be read stops the listing")
    func unreadableStillFails() {
        let output = "-rw-r--r-- 1 owner group size-unknown sometime file.txt"
        #expect(throws: CloudProviderError.self) {
            try FTPAPIClient.parseListing(output: output, basePath: "/")
        }
    }
}

/// The MS-DOS style IIS and other Windows FTP servers send.
@Suite("Windows-style FTP listings")
struct WindowsFTPListingTests {

    @Test("folders and files with a 12-hour time")
    func iisListing() throws {
        let output = """
        10-28-26  09:30PM       <DIR>          Photos
        10-28-26  09:31AM              1024 notes.txt
        01-05-99  12:05AM                 7 old file.txt
        """
        let items = try FTPAPIClient.parseListing(output: output, basePath: "/share")
        #expect(items.map(\.name) == ["Photos", "notes.txt", "old file.txt"])
        #expect(items.map(\.path) == ["/share/Photos", "/share/notes.txt", "/share/old file.txt"])
        #expect(items.map(\.isDirectory) == [true, false, false])
        #expect(items.map(\.size) == [0, 1024, 7])

        let photos = components(items[0].modificationDate)
        #expect(photos.year == 2026 && photos.month == 10 && photos.day == 28 && photos.hour == 21 && photos.minute == 30)
        let old = components(items[2].modificationDate)
        #expect(old.year == 1999 && old.hour == 0)
    }

    @Test("a four-digit year and a 24-hour time")
    func twentyFourHour() throws {
        let row = try #require(FTPAPIClient.parseWindowsListingLine("11-21-2026  17:05         20388 pages.jsonl"))
        let date = components(row.modDate)
        #expect(row.name == "pages.jsonl")
        #expect(date.year == 2026 && date.hour == 17 && date.minute == 5)
    }

    @Test("a Windows-style row that can't be read stops the listing")
    func unreadableWindowsRow() {
        let output = """
        10-28-26  09:30PM       <DIR>          Photos
        10-28-26  09:31PM       ???            notes.txt
        """
        #expect(throws: CloudProviderError.self) {
            try FTPAPIClient.parseListing(output: output, basePath: "/")
        }
    }

    @Test("unknown output with nothing readable is still an error, not an empty folder")
    func unknownFormat() {
        #expect(throws: CloudProviderError.self) {
            try FTPAPIClient.parseListing(output: "+i8388621.29609,m824255902,/,\tdev\n", basePath: "/")
        }
    }

    @Test("an empty folder is empty")
    func emptyFolder() throws {
        #expect(try FTPAPIClient.parseListing(output: "", basePath: "/").isEmpty)
        #expect(try FTPAPIClient.parseListing(output: "total 0\n", basePath: "/").isEmpty)
    }
}
