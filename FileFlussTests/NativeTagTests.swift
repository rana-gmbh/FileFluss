import Testing
import Foundation
import FileFlussCore
@testable import FileFluss

private let red = FileTag(name: "Red", color: .red)
private let blue = FileTag(name: "Blue", color: .blue)
private let green = FileTag(name: "Green", color: .green)
private let project = FileTag(name: "Projekt X", color: .purple)
private let plain = FileTag(name: "Steuer 2026", color: .none)
private let standard = TagColor.menuOrder.map { FileTag(name: $0.englishName, color: $0) }

/// What a one-colour service reports for a colour: the colour, under no
/// name of its own.
private func label(_ color: TagColor) -> [FileTag] {
    [FileTag(name: color.englishName, color: color)]
}

@Suite("Service labels and tags in step")
struct NativeTagSyncTests {

    // MARK: One colour per item (MEGA)

    @Test("The service is asked to hold the last colour, the one a folder shows")
    func nativeFormOneColour() {
        #expect(NativeTagSync.nativeForm([red, plain, blue], support: .oneColor) == [blue])
        #expect(NativeTagSync.nativeForm([plain], support: .oneColor) == [])
        #expect(NativeTagSync.nativeForm([red, blue], support: .full) == [red, blue])
        #expect(NativeTagSync.nativeForm([red], support: .none) == [])
    }

    @Test("When both sides agree nothing changes and nothing is sent")
    func agreement() {
        let outcome = NativeTagSync.reconcile(stored: [red, blue], native: label(.blue), support: .oneColor, pending: false, standard: standard)
        #expect(outcome.tags == [red, blue])
        #expect(outcome.push == nil)
        let untagged = NativeTagSync.reconcile(stored: [], native: [], support: .oneColor, pending: false, standard: standard)
        #expect(untagged.tags.isEmpty)
        #expect(untagged.push == nil)
    }

    @Test("A label given in the service's own app becomes the item's last tag")
    func labelGivenElsewhere() {
        let fresh = NativeTagSync.reconcile(stored: [], native: label(.green), support: .oneColor, pending: false, standard: standard)
        #expect(fresh.tags == [green])
        #expect(fresh.push == nil)

        // The tags it already had stay, behind the new colour.
        let added = NativeTagSync.reconcile(stored: [red, plain], native: label(.blue), support: .oneColor, pending: false, standard: standard)
        #expect(added.tags == [red, plain, blue])
    }

    @Test("A colour the item already has under its own name keeps that name")
    func keepsOwnName() {
        let outcome = NativeTagSync.reconcile(stored: [project, red], native: label(.purple), support: .oneColor, pending: false, standard: standard)
        #expect(outcome.tags == [red, project])
        #expect(outcome.tags.folderTint == .purple)
    }

    @Test("A label taken off elsewhere takes the colours off here; tags without one stay")
    func labelRemovedElsewhere() {
        let outcome = NativeTagSync.reconcile(stored: [red, plain, blue], native: [], support: .oneColor, pending: false, standard: standard)
        #expect(outcome.tags == [plain])
        #expect(outcome.push == nil)
    }

    @Test("An item FileFluss has just written keeps its tags and the service is told")
    func pendingOneColour() {
        let outcome = NativeTagSync.reconcile(stored: [red, blue], native: [], support: .oneColor, pending: true, standard: standard)
        #expect(outcome.tags == [red, blue])
        #expect(outcome.push == [blue])
    }

    @Test("The names in use here are given to a colour that arrives without one")
    func usesLocalNames() {
        let german = [FileTag(name: "Rot", color: .red), FileTag(name: "Blau", color: .blue)]
        let outcome = NativeTagSync.reconcile(stored: [], native: label(.red), support: .oneColor, pending: false, standard: german)
        #expect(outcome.tags == [FileTag(name: "Rot", color: .red)])
    }

    // MARK: Named labels, several per item (kDrive)

    @Test("Where the service holds everything, what it has is what is shown")
    func fullServiceWins() {
        let invoices = FileTag(name: "Invoices", color: .orange)
        let outcome = NativeTagSync.reconcile(stored: [red], native: [invoices, blue], support: .full, pending: false, standard: standard)
        #expect(outcome.tags == [invoices, blue])
        #expect(outcome.push == nil)

        let cleared = NativeTagSync.reconcile(stored: [red], native: [], support: .full, pending: false, standard: standard)
        #expect(cleared.tags.isEmpty)
    }

    @Test("An item just written adds its tags to what the service has")
    func pendingFull() {
        let outcome = NativeTagSync.reconcile(stored: [red, blue], native: [FileTag(name: "blue", color: .blue)], support: .full, pending: true, standard: standard)
        // "Blue" is there already, whatever its capitals.
        #expect(outcome.tags == [FileTag(name: "blue", color: .blue), red])
        #expect(outcome.push == outcome.tags)

        let nothingNew = NativeTagSync.reconcile(stored: [red], native: [red], support: .full, pending: true, standard: standard)
        #expect(nothingNew.push == nil)
    }

    @Test("Without labels of its own a service leaves the record alone")
    func noSupport() {
        let outcome = NativeTagSync.reconcile(stored: [red], native: [], support: .none, pending: true, standard: standard)
        #expect(outcome.tags == [red])
        #expect(outcome.push == nil)
    }

    @Test("Any colour is shown as the nearest of the seven")
    func nearestColour() {
        #expect(NativeTagSync.nearestColor(toHex: "#FF0000") == .red)
        #expect(NativeTagSync.nearestColor(toHex: "#ff1493") == .red)
        #expect(NativeTagSync.nearestColor(toHex: "0098ff") == .blue)
        #expect(NativeTagSync.nearestColor(toHex: "#0A0") == .green)
        #expect(NativeTagSync.nearestColor(toHex: "#888888") == .gray)
        #expect(NativeTagSync.nearestColor(toHex: "not a colour") == .gray)
        // What FileFluss creates comes back as what it meant.
        for color in TagColor.menuOrder {
            #expect(NativeTagSync.nearestColor(toHex: NativeTagSync.hexColor(for: color)) == color)
        }
    }

    @Test("A record written by a transfer is told apart from one the user made")
    func newlySeen() async {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("FileFlussNativeTags_\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = TagStore(directory: directory)
        let account = UUID()
        await store.setTags([red], account: account, path: "/uploaded.txt")
        await store.setTags([blue], account: account, path: "/tagged.txt", itemID: "id-2")

        let first = await store.resolveDetailed(
            account: account,
            items: [(id: "id-1", path: "/uploaded.txt"), (id: "id-2", path: "/tagged.txt")],
            stableIDs: true
        )
        #expect(first.tags == ["/uploaded.txt": [red], "/tagged.txt": [blue]])
        #expect(first.newlySeen == ["/uploaded.txt"])

        // Only the first time: after that the service has been told.
        let second = await store.resolveDetailed(
            account: account,
            items: [(id: "id-1", path: "/uploaded.txt"), (id: "id-2", path: "/tagged.txt")],
            stableIDs: true
        )
        #expect(second.newlySeen.isEmpty)
    }
}

@Suite("MEGA labels")
struct MegaLabelTests {

    @Test("MEGA numbers its seven colours in its own order")
    func mapping() {
        let expected: [(Int, TagColor)] = [(1, .red), (2, .orange), (3, .yellow), (4, .green), (5, .blue), (6, .purple), (7, .gray)]
        for (label, color) in expected {
            #expect(MegaNodeAttributes.tagColor(forLabel: label) == color)
            #expect(MegaNodeAttributes.label(for: color) == label)
        }
        #expect(MegaNodeAttributes.tagColor(forLabel: 0) == .none)
        #expect(MegaNodeAttributes.tagColor(forLabel: 9) == .none)
        #expect(MegaNodeAttributes.label(for: .none) == 0)
    }

    @Test("A label is read as MEGA's apps write it, and as a bare number")
    func reading() {
        #expect(MegaNodeAttributes.label(in: #"{"n":"a.txt","lbl":"3"}"#) == 3)
        #expect(MegaNodeAttributes.label(in: #"{"n":"a.txt","lbl":3}"#) == 3)
        #expect(MegaNodeAttributes.label(in: #"{"n":"a.txt"}"#) == 0)
        #expect(MegaNodeAttributes.label(in: #"{"n":"a.txt","lbl":"0"}"#) == 0)
        #expect(MegaNodeAttributes.label(in: #"{"n":"a.txt","lbl":"12"}"#) == 0)
        #expect(MegaNodeAttributes.label(in: nil) == 0)
        #expect(MegaNodeAttributes.label(in: "not json") == 0)
    }

    private func parsed(_ json: String) throws -> [String: Any] {
        try #require(try JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any])
    }

    @Test("Setting a label keeps every other attribute as it was")
    func settingKeepsTheRest() throws {
        let before = #"{"n":"Report.pdf","c":"AbCdEf123","fav":"1","t":"work,2026","lbl":"1"}"#
        let after = try parsed(MegaNodeAttributes.json(updating: before, name: "Report.pdf", label: 5))
        #expect(after["lbl"] as? String == "5")
        #expect(after["n"] as? String == "Report.pdf")
        #expect(after["c"] as? String == "AbCdEf123")
        #expect(after["fav"] as? String == "1")
        #expect(after["t"] as? String == "work,2026")
        #expect(after.count == 5)
    }

    @Test("Taking a label off removes the key, as MEGA does")
    func removing() throws {
        let after = try parsed(MegaNodeAttributes.json(updating: #"{"n":"a.txt","lbl":"4","fav":"1"}"#, name: "a.txt", label: 0))
        #expect(after["lbl"] == nil)
        #expect(after["fav"] as? String == "1")
    }

    @Test("A rename keeps the label and everything else")
    func renameKeepsTheLabel() throws {
        let after = try parsed(MegaNodeAttributes.json(updating: #"{"n":"old.txt","lbl":"6","c":"xyz"}"#, name: "new.txt"))
        #expect(after["n"] as? String == "new.txt")
        #expect(after["lbl"] as? String == "6")
        #expect(after["c"] as? String == "xyz")
    }

    @Test("Names with quotes, backslashes, slashes and umlauts survive the round trip")
    func awkwardNames() throws {
        for name in [#"He said "hi".txt"#, #"back\slash.txt"#, "Größe ½ – 日本語.txt", "a/b.txt"] {
            let json = MegaNodeAttributes.json(updating: nil, name: name, label: 2)
            #expect(try parsed(json)["n"] as? String == name)
            #expect(MegaNodeAttributes.label(in: json) == 2)
            // No optional "\/" escape, which MEGA's reader may not undo.
            #expect(!json.contains(#"\/"#))
        }
    }

    @Test("An item with no attributes yet gets a block with just its name")
    func fromNothing() throws {
        let after = try parsed(MegaNodeAttributes.json(updating: nil, name: "new folder"))
        #expect(after["n"] as? String == "new folder")
        #expect(after.count == 1)
    }
}

@Suite("kDrive categories")
struct KDriveCategoryTests {
    private let categories = Data(#"""
    {"result":"success","data":[
      {"id":1,"name":"Avertissement","color":"#FF9800","is_predefined":true,"created_at":1600000000,"user_usage_count":0},
      {"id":7,"name":"Red","color":"#FF453A","is_predefined":false,"created_at":1700000000,"user_usage_count":3},
      {"id":9,"name":"Invoices","color":"#0098ff","is_predefined":false,"created_at":1700000001,"user_usage_count":1}
    ]}
    """#.utf8)

    @Test("A folder listing's categories become tags under their own names")
    func listing() throws {
        let page = Data(#"""
        {"result":"success","data":[
          {"id":101,"name":"Trip","type":"dir","categories":[{"category_id":7,"user_validation":"","is_generated_by_ai":false,"user_id":5,"added_at":1700000500}]},
          {"id":102,"name":"bill.pdf","type":"file","categories":[{"category_id":9,"added_at":1700000600},{"category_id":7,"added_at":1700000700}]},
          {"id":103,"name":"plain.txt","type":"file","categories":[]},
          {"id":104,"name":"older-api.txt","type":"file"},
          {"id":105,"name":"gone.txt","type":"file","categories":[{"category_id":999}]}
        ],"cursor":"abc","has_more":false,"response_at":1700001000}
        """#.utf8)

        let tags = try KDriveCategoryPayloads.tagsByName(listingPage: page, categoryList: categories)
        #expect(tags["Trip"] == [FileTag(name: "Red", color: .red)])
        #expect(tags["bill.pdf"] == [FileTag(name: "Invoices", color: .blue), FileTag(name: "Red", color: .red)])
        #expect(tags["plain.txt"] == nil)
        #expect(tags["older-api.txt"] == nil)
        #expect(tags["gone.txt"] == nil)
        #expect(try KDriveCategoryPayloads.nextCursor(listingPage: page) == nil)
    }

    @Test("A long folder is asked for page by page")
    func paging() throws {
        let more = Data(#"{"result":"success","data":[],"cursor":"next-page","has_more":true}"#.utf8)
        #expect(try KDriveCategoryPayloads.nextCursor(listingPage: more) == "next-page")
    }
}

@Suite("Which services have labels of their own")
struct NativeTagSupportTests {
    @Test("MEGA holds one colour, kDrive everything, the others nothing")
    func support() {
        #expect(MegaProvider().nativeTagSupport == .oneColor)
        #expect(KDriveProvider().nativeTagSupport == .full)
        #expect(DropboxProvider().nativeTagSupport == .none)
    }
}
