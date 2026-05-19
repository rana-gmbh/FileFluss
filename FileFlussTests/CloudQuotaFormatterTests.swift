import Testing
import Foundation
import FileFlussCore
@testable import FileFluss

@Suite("CloudQuotaFormatter")
struct CloudQuotaFormatterTests {

    @Test("Renders used and total with whole-percent rounding")
    func nominal() {
        // 4 GB used of 16 GB → 25%.
        let q = CloudStorageQuota(usedBytes: 4 * 1_000_000_000, totalBytes: 16 * 1_000_000_000)
        let s = CloudQuotaFormatter.summary(q, accountDisplayName: "Dropbox")
        #expect(s.contains("Dropbox"))
        #expect(s.contains("(25%)"))
    }

    @Test("nil total renders without percentage")
    func unlimited() {
        let q = CloudStorageQuota(usedBytes: 412_000, totalBytes: nil)
        let s = CloudQuotaFormatter.summary(q, accountDisplayName: "Google Drive")
        #expect(s.contains("Google Drive"))
        #expect(s.contains("used"))
        #expect(s.contains("%") == false)
    }

    @Test("Over-quota clamps to 100%")
    func overQuota() {
        // 1.1 GB used out of 1 GB — Google can briefly report > 100%.
        let q = CloudStorageQuota(usedBytes: 1_100_000_000, totalBytes: 1_000_000_000)
        let percent = CloudQuotaFormatter.percentString(used: q.usedBytes, total: q.totalBytes!)
        #expect(percent == "100%")
    }

    @Test("Sub-1% usage shows one decimal")
    func tinyPercent() {
        // 5 MB used of 1 TB → 0.0005% → rounds to 0.0% but we want >= 0.1%
        // floor — confirm it doesn't collapse to "0%" hiding small usage.
        let percent = CloudQuotaFormatter.percentString(used: 10_000_000, total: 1_000_000_000_000)
        #expect(percent.hasSuffix("%"))
        #expect(percent != "0%")          // 10 MB on 1 TB is ~0.001%, decimal form
        #expect(percent.contains("."))    // sub-1% uses decimal form
    }

    @Test("Zero used renders 0%")
    func zeroUsed() {
        let percent = CloudQuotaFormatter.percentString(used: 0, total: 100_000_000_000)
        #expect(percent == "0%")
    }

    @Test("Zero total is safe — guards against divide-by-zero")
    func zeroTotal() {
        let percent = CloudQuotaFormatter.percentString(used: 1234, total: 0)
        #expect(percent == "0%")
    }
}
