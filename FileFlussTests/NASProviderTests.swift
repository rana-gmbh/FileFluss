import Testing
import Foundation
import FileFlussCore
@testable import FileFluss

/// Pins the parts of the QNAP, UGREEN and ASUSTOR accounts that fail
/// silently: how File Station's replies become panel rows, which login
/// reply asks for a 2-step code, how names with "+" reach the NAS, and
/// which port a bare host name gets.
@Suite("NAS accounts")
struct NASProviderTests {

    // MARK: - QNAP listing

    /// Shaped like the `get_list` reply in QNAP's API reference, with the
    /// system folders QTS keeps beside the user's files.
    private let listingPage = Data("""
    {
      "total": 5, "acl": 7, "is_acl_enable": 1,
      "datas": [
        {"filename": "Photos", "isfolder": 1, "filesize": "4096", "mt": "2021/11/29 16:41:35", "epochmt": 1638175295},
        {"filename": "5MB.mp4", "isfolder": 0, "filesize": "5251468", "mt": "2021/11/29 16:41:35", "epochmt": 1638175295},
        {"filename": "a+b.txt", "isfolder": 0, "filesize": 116, "epochmt": "1638177118"},
        {"filename": "@Recycle", "isfolder": 1, "filesize": "4096", "epochmt": 1638175295},
        {"filename": ".@__thumb", "isfolder": 1, "filesize": "4096", "epochmt": 1638175295}
      ]
    }
    """.utf8)

    @Test("A listing page becomes rows under the folder that was listed")
    func listingRows() throws {
        let page = try QNAPAPIClient.items(fromListing: listingPage, parentPath: "/Public")
        #expect(page.items.map(\.path) == ["/Public/Photos", "/Public/5MB.mp4", "/Public/a+b.txt"])
        #expect(page.items.map(\.isDirectory) == [true, false, false])
        // Sizes come as strings or numbers depending on the firmware.
        #expect(page.items[1].size == 5_251_468)
        #expect(page.items[2].size == 116)
        // A folder's 4096 is its inode, not its contents.
        #expect(page.items[0].size == 0)
        #expect(page.items[1].modificationDate == Date(timeIntervalSince1970: 1_638_175_295))
        #expect(page.items[2].modificationDate == Date(timeIntervalSince1970: 1_638_177_118))
    }

    /// Paging has to move by what QTS sent, or the rows after a hidden
    /// recycle bin would be skipped or fetched twice.
    @Test("System folders are hidden but still count for paging")
    func pagingCount() throws {
        let page = try QNAPAPIClient.items(fromListing: listingPage, parentPath: "/Public")
        #expect(page.rawCount == 5)
        #expect(page.total == 5)
        #expect(!page.items.contains { $0.name.hasPrefix("@") || $0.name.hasPrefix(".@__") })
    }

    @Test("Only real shared folders are offered at the top level")
    func shareTree() throws {
        let tree = Data("""
        [
          {"text": "Public", "id": "/Public", "cls": "w", "iconCls": "folder", "noSupportACL": 0},
          {"text": "android-x86", "id": "/android-x86", "cls": "r", "iconCls": "iso", "noSupportACL": 0},
          {"text": "home", "id": "/home", "cls": "w", "iconCls": "folder", "noSupportACL": 0}
        ]
        """.utf8)
        let shares = try QNAPAPIClient.shares(fromTree: tree)
        #expect(shares.map(\.path) == ["/Public", "/home"])
        #expect(shares.allSatisfy { $0.isDirectory })
    }

    @Test("A refused share listing reports the refusal")
    func refusedShareTree() {
        let denied = Data(#"{"status": 4, "success": "true"}"#.utf8)
        #expect(throws: CloudProviderError.self) {
            _ = try QNAPAPIClient.shares(fromTree: denied)
        }
    }

    // MARK: - QNAP login

    @Test("The password is Base64, as QNAP's own login page sends it")
    func passwordEncoding() {
        // The example in QNAP's authentication reference.
        #expect(QNAPAPIClient.encodePassword("admin") == "YWRtaW4=")
        #expect(QNAPAPIClient.encodePassword("pässwort") == Data("pässwort".utf8).base64EncodedString())
    }

    @Test("A successful login yields the session id")
    func loginSuccess() throws {
        let reply = Data("""
        <?xml version="1.0" encoding="UTF-8" ?>
        <QDocRoot version="1.0">
        <qtoken><![CDATA[1e29b890910e8135f1692ed4030256fe]]></qtoken>
        <authPassed><![CDATA[1]]></authPassed>
        <authSid><![CDATA[ral08opo]]></authSid>
        <isAdmin><![CDATA[1]]></isAdmin>
        </QDocRoot>
        """.utf8)
        #expect(try QNAPAPIClient.sessionId(fromLoginReply: reply, sentCode: false) == "ral08opo")
    }

    /// A 2-step account answers the password with `need_2sv` instead of a
    /// session: that has to become "enter your code", not "wrong password".
    @Test("A 2-step account asks for the code")
    func loginNeedsCode() {
        let reply = Data("""
        <QDocRoot version="1.0">
        <authPassed>0</authPassed>
        <need_2sv>1</need_2sv>
        <lost_phone>1</lost_phone>
        </QDocRoot>
        """.utf8)
        do {
            _ = try QNAPAPIClient.sessionId(fromLoginReply: reply, sentCode: false)
            Issue.record("Expected the login to ask for a code")
        } catch CloudProviderError.twoFactorRequired {
        } catch {
            Issue.record("Expected .twoFactorRequired, got \(error)")
        }

        // With a code sent, the same reply means the code was wrong.
        do {
            _ = try QNAPAPIClient.sessionId(fromLoginReply: reply, sentCode: true)
            Issue.record("Expected the code to be rejected")
        } catch CloudProviderError.commandFailed {
        } catch {
            Issue.record("Expected .commandFailed, got \(error)")
        }
    }

    @Test("A wrong password reads as invalid credentials")
    func loginWrongPassword() {
        let reply = Data("<QDocRoot version=\"1.0\"><authPassed>0</authPassed><errorValue>-1</errorValue></QDocRoot>".utf8)
        do {
            _ = try QNAPAPIClient.sessionId(fromLoginReply: reply, sentCode: false)
            Issue.record("Expected the login to fail")
        } catch CloudProviderError.invalidCredentials {
        } catch {
            Issue.record("Expected .invalidCredentials, got \(error)")
        }
    }

    // MARK: - QNAP plumbing

    /// QTS decodes "+" as a space, so "a+b.txt" has to go out as "a%2Bb.txt".
    @Test("Query values are strictly percent-encoded")
    func formEncoding() {
        let encoded = QNAPAPIClient.formEncode([("source_file", "a+b & c.txt"), ("path", "/Public/Fotos ü")])
        #expect(encoded == "source_file=a%2Bb%20%26%20c.txt&path=%2FPublic%2FFotos%20%C3%BC")
    }

    @Test("File Station status codes map to the errors the app understands")
    func statusCodes() {
        guard case .unauthorized = QNAPAPIClient.mapStatus(4) else { Issue.record("4 is permission denied"); return }
        guard case .notFound = QNAPAPIClient.mapStatus(5, path: "/Public/x") else { Issue.record("5 is not found"); return }
        guard case .quotaExceeded = QNAPAPIClient.mapStatus(9) else { Issue.record("9 is the quota"); return }
        guard case .notAuthenticated = QNAPAPIClient.mapStatus(3) else { Issue.record("3 is a lost session"); return }
    }

    // MARK: - Server addresses

    @Test("A bare QNAP host gets HTTPS; plain HTTP gets QTS's port 8080")
    func qnapAddress() {
        #expect(QNAPAPIClient.normalizeServerURL("nas.local") == "https://nas.local")
        #expect(QNAPAPIClient.normalizeServerURL("http://192.168.1.20/") == "http://192.168.1.20:8080")
        #expect(QNAPAPIClient.normalizeServerURL(" https://nas.local:8081 ") == "https://nas.local:8081")
    }

    /// The web UI ports don't serve WebDAV, so a bare host must land on
    /// the WebDAV service's own port.
    @Test("UGREEN and ASUSTOR hosts get their WebDAV ports")
    func webDAVAddresses() {
        #expect(NASWebDAVProvider.normalizeServerURL("192.168.1.20", brand: .ugreen) == "https://192.168.1.20:5006")
        #expect(NASWebDAVProvider.normalizeServerURL("http://192.168.1.20", brand: .ugreen) == "http://192.168.1.20:5005")
        #expect(NASWebDAVProvider.normalizeServerURL("nas.local/", brand: .asustor) == "https://nas.local:9802")
        #expect(NASWebDAVProvider.normalizeServerURL("http://nas.local", brand: .asustor) == "http://nas.local:9800")
        #expect(NASWebDAVProvider.normalizeServerURL("https://nas.local:1234", brand: .asustor) == "https://nas.local:1234")
    }

    /// WebDAV accounts saved before the certificate option existed must
    /// keep loading.
    @Test("WebDAV credentials without the certificate setting still decode")
    func legacyWebDAVCredentials() throws {
        let legacy = Data(#"{"serverURL": "https://dav.example.com", "username": "u", "password": "p", "displayName": "u"}"#.utf8)
        _ = try JSONDecoder().decode(WebDAVCredentials.self, from: legacy)
    }

    @Test("Each NAS has its own provider type and logo")
    func providerTypes() {
        #expect(NASWebDAVBrand.ugreen.providerType == .ugreen)
        #expect(NASWebDAVBrand.asustor.providerType == .asustor)
        #expect(CloudProviderType.qnap.logoAssetName == "QNAPLogo")
        #expect(CloudProviderType.ugreen.logoAssetName == "UGREENLogo")
        #expect(CloudProviderType.asustor.logoAssetName == "ASUSTORLogo")
    }
}
