import Testing
import Foundation
import FileFlussCore

/// Pinned parser tests covering the SFTP server `longname` formats we
/// have to handle. Regressions in `SFTPAPIClient.parseListingLine` show
/// up here before they reach the file panel as empty-folder bugs
/// (issue #31).
@Suite("SFTP listing parser")
struct SFTPListingParserTests {

    @Test("Classic OpenSSH file with HH:MM time")
    func openSSHTimeFile() {
        let line = "-rw-r--r--   1 owner group 12345 Jan 31 12:34 file.txt"
        let parsed = SFTPAPIClient.parseListingLine(line)
        #expect(parsed?.name == "file.txt")
        #expect(parsed?.isDirectory == false)
        #expect(parsed?.size == 12345)
    }

    @Test("Classic OpenSSH file with year (old mtime)")
    func openSSHYearFile() {
        let line = "-rw-r--r-- 1 owner group 12345 Jan 31  2024 old.txt"
        let parsed = SFTPAPIClient.parseListingLine(line)
        #expect(parsed?.name == "old.txt")
        #expect(parsed?.isDirectory == false)
    }

    @Test("Classic OpenSSH directory")
    func openSSHDirectory() {
        let line = "drwxr-xr-x 2 admin users 4096 Feb 14 09:00 some-dir"
        let parsed = SFTPAPIClient.parseListingLine(line)
        #expect(parsed?.name == "some-dir")
        #expect(parsed?.isDirectory == true)
        // Directory size is reset to 0 by parseListing, but the per-line
        // parser still surfaces the raw value for callers that want it.
        #expect(parsed?.size == 4096)
    }

    @Test("SELinux context suffix on permissions (.) — regression for issue #31")
    func selinuxSuffix() {
        let line = "-rw-r--r--.  1 owner group 12345 Mar 12 08:30 selinux.txt"
        let parsed = SFTPAPIClient.parseListingLine(line)
        #expect(parsed?.name == "selinux.txt")
        #expect(parsed?.isDirectory == false)
    }

    @Test("ACL marker (+) on permissions")
    func aclSuffix() {
        let line = "-rw-r--r--+ 1 owner group 12345 Mar 12 08:30 acl.txt"
        let parsed = SFTPAPIClient.parseListingLine(line)
        #expect(parsed?.name == "acl.txt")
    }

    @Test("ProFTPD ISO date with seconds — regression for issue #31")
    func proftpdISOSeconds() {
        let line = "-rw-r--r-- 1 owner group 12345 2026-01-31 12:34:56 file.txt"
        let parsed = SFTPAPIClient.parseListingLine(line)
        #expect(parsed?.name == "file.txt")
        #expect(parsed?.isDirectory == false)
        #expect(parsed?.size == 12345)
    }

    @Test("ProFTPD ISO date without seconds — regression for issue #31")
    func proftpdISONoSeconds() {
        let line = "-rw-r--r-- 1 owner group 12345 2026-01-31 12:34 file.txt"
        let parsed = SFTPAPIClient.parseListingLine(line)
        #expect(parsed?.name == "file.txt")
    }

    @Test("ProFTPD ISO date date-only")
    func proftpdISODateOnly() {
        let line = "-rw-r--r-- 1 owner group 12345 2026-01-31 file.txt"
        let parsed = SFTPAPIClient.parseListingLine(line)
        #expect(parsed?.name == "file.txt")
    }

    @Test("ProFTPD ISO directory")
    func proftpdISODirectory() {
        let line = "drwxr-xr-x 2 admin users 4096 2026-02-14 09:00:00 some-dir"
        let parsed = SFTPAPIClient.parseListingLine(line)
        #expect(parsed?.name == "some-dir")
        #expect(parsed?.isDirectory == true)
    }

    @Test("Symlink with -> target")
    func symlinkStripsTarget() {
        let line = "lrwxrwxrwx 1 owner group 7 Jan 31 12:34 link -> target/"
        let parsed = SFTPAPIClient.parseListingLine(line)
        #expect(parsed?.name == "link")
    }

    @Test("Link count rendered as ? — regression for issue #31")
    func unknownLinkCount() {
        // Servers that omit st_nlink make OpenSSH's sftp client print "?"
        // for the hard-link count (Raspberry Pi OS, OpenWRT, some hosts).
        let file = SFTPAPIClient.parseListingLine("-rw-r--r--    ? root root 51 Dec  8 09:05 VERSION")
        #expect(file?.name == "VERSION")
        #expect(file?.isDirectory == false)
        #expect(file?.size == 51)

        let dir = SFTPAPIClient.parseListingLine("drwxr-xr-x    ? root root 4096 Sep 14  2025 app")
        #expect(dir?.name == "app")
        #expect(dir?.isDirectory == true)
    }

    @Test("Absolute-path name from `ls` of an absolute path — regression for issue #31")
    func absolutePathName() {
        // `ls -la /` and `ls -la /html` print full paths, not basenames.
        let root = SFTPAPIClient.parseListingLine("-rw-r--r--    ? root root 51 Dec  8 09:05 /VERSION")
        #expect(root?.name == "VERSION")

        let nested = SFTPAPIClient.parseListingLine("-rw-r--r--    ? ud09_104 2368 922 Aug 12  2025 /html/.htaccess")
        #expect(nested?.name == ".htaccess")
        #expect(nested?.isDirectory == false)

        let nestedDir = SFTPAPIClient.parseListingLine("drwxr-xr-x    ? ud09_104 2368 4096 Feb 27  2024 /html/cgi-bin")
        #expect(nestedDir?.name == "cgi-bin")
        #expect(nestedDir?.isDirectory == true)
    }

    @Test("Absolute-path . and .. entries are skipped — regression for issue #31")
    func absolutePathDotEntries() {
        #expect(SFTPAPIClient.parseListingLine("drwxr-xr-x    ? root root 4096 May  6 14:30 /.") == nil)
        #expect(SFTPAPIClient.parseListingLine("drwxr-xr-x    ? root root 4096 May  6 14:30 /..") == nil)
        #expect(SFTPAPIClient.parseListingLine("drwxr-x---    ? 0 2368 4096 Jun 21  2021 /html/..") == nil)
    }

    @Test("German month abbreviation — regression for non-C-locale OpenSSH")
    func germanMonthAbbrev() {
        let line = "-rw-r--r-- 1 owner group 12345 Mär 12 08:30 deutsch.txt"
        let parsed = SFTPAPIClient.parseListingLine(line)
        #expect(parsed?.name == "deutsch.txt")
    }

    @Test("Octal-escaped non-ASCII filename")
    func octalEscapedName() {
        // "über.txt" — UTF-8 bytes 0xc3 0xbc for ü.
        let line = #"-rw-r--r-- 1 owner group 12345 Jan 31 12:34 \303\274ber.txt"#
        let parsed = SFTPAPIClient.parseListingLine(line)
        #expect(parsed?.name == "über.txt")
    }

    @Test("Skips . and .. entries")
    func skipsDotEntries() {
        #expect(SFTPAPIClient.parseListingLine("drwxr-xr-x 2 root root 4096 Jan 31 12:34 .") == nil)
        #expect(SFTPAPIClient.parseListingLine("drwxr-xr-x 2 root root 4096 Jan 31 12:34 ..") == nil)
    }

    @Test("Skips prompt and total lines")
    func skipsNonListingLines() {
        #expect(SFTPAPIClient.parseListingLine("sftp> ls -la") == nil)
        #expect(SFTPAPIClient.parseListingLine("total 24") == nil)
        #expect(SFTPAPIClient.parseListingLine("") == nil)
        #expect(SFTPAPIClient.parseListingLine("   ") == nil)
    }

    @Test("Empty output is not flagged as listing-shaped")
    func outputLooksLikeListing_empty() {
        #expect(SFTPAPIClient.outputLooksLikeListing("") == false)
        #expect(SFTPAPIClient.outputLooksLikeListing("sftp> ls\n") == false)
        #expect(SFTPAPIClient.outputLooksLikeListing("total 0\n") == false)
    }

    @Test("ISO-only output is flagged as listing-shaped (warning trigger)")
    func outputLooksLikeListing_iso() {
        // This is the exact scenario behind issue #31 before the fix:
        // the server returned listing-shaped lines but no row parsed.
        let output = """
        -rw-r--r-- 1 owner group 12345 2026-01-31 12:34:56 file.txt
        """
        #expect(SFTPAPIClient.outputLooksLikeListing(output) == true)
    }
}
