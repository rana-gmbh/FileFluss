import Testing
@testable import FileFlussCore

/// Path → id caches and what a delete has to take with it.
@Suite("Path cache eviction")
struct PathCacheEvictionTests {

    private func cache() -> [String: String] {
        [
            "/": "0",
            "/Test": "10",
            "/Test/Documents": "11",
            "/Test/Documents/a.txt": "12",
            "/Test/Immobilien": "13",
            "/Testing": "20",
            "/Other": "30",
        ]
    }

    /// The version-test failure: a folder deleted in one run left its
    /// children cached, so the next run's `createFolder` was answered "it's
    /// already there" with a dead id and every upload into it was rejected
    /// for a parent that doesn't exist.
    @Test("Deleting a folder takes its children with it")
    func evictsDescendants() {
        var c = cache()

        c.removePathSubtree("/Test")

        #expect(c["/Test"] == nil)
        #expect(c["/Test/Documents"] == nil)
        #expect(c["/Test/Documents/a.txt"] == nil)
        #expect(c["/Test/Immobilien"] == nil)
    }

    @Test("A sibling with a longer name is not a descendant")
    func respectsComponentBoundaries() {
        var c = cache()

        c.removePathSubtree("/Test")

        #expect(c["/Testing"] == "20")
        #expect(c["/Other"] == "30")
    }

    @Test("The root's id survives — the walk starts from it")
    func keepsRoot() {
        var c = cache()

        c.removePathSubtree("/Test")
        #expect(c["/"] == "0")

        // Even evicting the root itself, which means "forget everything".
        c.removePathSubtree("/")
        #expect(c["/"] == "0")
        #expect(c.count == 1)
    }

    @Test("A trailing slash means the same folder")
    func toleratesTrailingSlash() {
        var c = cache()

        c.removePathSubtree("/Test/")

        #expect(c["/Test"] == nil)
        #expect(c["/Test/Documents"] == nil)
        #expect(c["/Testing"] == "20")
    }

    @Test("Evicting a file touches only that file")
    func evictsSingleFile() {
        var c = cache()

        c.removePathSubtree("/Test/Documents/a.txt")

        #expect(c["/Test/Documents/a.txt"] == nil)
        #expect(c["/Test/Documents"] == "11")
        #expect(c["/Test"] == "10")
    }

    /// The Internxt failure: these caches were only ever added to, so an
    /// entry that vanished stayed cached for the life of the session — and
    /// `createFolder`, which asks the cache first, then reported a folder as
    /// already there and created nothing.
    @Test("A listing drops cached children it no longer contains")
    func listingIsAuthoritativeForItsLevel() {
        var c = cache()

        // "/Test" is gone from the root listing; "/Other" is still there.
        c.retainPathChildren(of: "/", named: ["Testing", "Other"])

        #expect(c["/Test"] == nil)
        #expect(c["/Test/Documents"] == nil)
        #expect(c["/Test/Documents/a.txt"] == nil)
        #expect(c["/Testing"] == "20")
        #expect(c["/Other"] == "30")
        #expect(c["/"] == "0")
    }

    @Test("A listing says nothing about levels below it")
    func listingLeavesGrandchildrenAlone() {
        var c = cache()

        // Listing /Test: Documents is still there, Immobilien is not.
        c.retainPathChildren(of: "/Test", named: ["Documents"])

        #expect(c["/Test/Documents"] == "11")
        #expect(c["/Test/Documents/a.txt"] == "12")
        #expect(c["/Test/Immobilien"] == nil)
        #expect(c["/Test"] == "10")
        #expect(c["/Testing"] == "20")
    }

    @Test("An empty listing empties that level")
    func emptyListingClearsTheLevel() {
        var c = cache()

        c.retainPathChildren(of: "/Test", named: [])

        #expect(c["/Test"] == "10")
        #expect(c["/Test/Documents"] == nil)
        #expect(c["/Test/Immobilien"] == nil)
    }

    @Test("Evicting a path that was never cached changes nothing")
    func unknownPathIsHarmless() {
        var c = cache()
        let before = c

        c.removePathSubtree("/Nowhere")

        #expect(c == before)
    }
}
