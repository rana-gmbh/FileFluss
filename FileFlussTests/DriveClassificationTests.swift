import Testing
import Foundation
@testable import FileFluss

/// Which mounted volumes belong in the sidebar's Drives section.
@Suite("Drive classification")
@MainActor
struct DriveClassificationTests {

    private func kind(
        _ path: String,
        name: String,
        browsable: Bool = true,
        root: Bool = false,
        local: Bool = true,
        internalDisk: Bool
    ) -> Drive.Kind? {
        DriveMonitor.kind(
            mountPath: path,
            name: name,
            isBrowsable: browsable,
            isRootFileSystem: root,
            isLocal: local,
            isInternal: internalDisk
        )
    }

    /// Reported by a user with a second partition on his internal SSD: it
    /// appeared nowhere in FileFluss and there was no way to open it. Every
    /// internal volume that wasn't the startup disk used to fall through.
    @Test("A second partition on the internal disk is a drive")
    func secondInternalPartitionIsShown() {
        #expect(kind("/Volumes/Daten", name: "Daten", internalDisk: true) == .internalVolume)
    }

    @Test("An external disk is still an external drive")
    func externalDisk() {
        #expect(kind("/Volumes/Video HD", name: "Video HD", internalDisk: false) == .external)
    }

    @Test("A network share is still a network drive")
    func networkShare() {
        #expect(kind("/Volumes/share", name: "share", local: false, internalDisk: false) == .network)
    }

    @Test("The startup volume is not listed")
    func startupVolumeExcluded() {
        #expect(kind("/", name: "Macintosh HD", root: true, internalDisk: true) == nil)
        // Also when only one of the two signals says so.
        #expect(kind("/", name: "Macintosh HD", internalDisk: true) == nil)
        #expect(kind("/Volumes/Macintosh HD", name: "Macintosh HD", root: true, internalDisk: true) == nil)
    }

    /// What makes showing internal volumes safe: macOS keeps its own parts
    /// of the startup disk under /System/Volumes, and they are internal,
    /// local and browsable like any other.
    @Test("The system's own volumes stay hidden")
    func systemVolumesExcluded() {
        #expect(kind("/System/Volumes/Data", name: "Data", internalDisk: true) == nil)
        #expect(kind("/System/Volumes/Preboot", name: "Preboot", internalDisk: true) == nil)
        #expect(kind("/System/Volumes/VM", name: "VM", internalDisk: true) == nil)
        #expect(kind("/private/var/folders/x/disk", name: "disk", internalDisk: true) == nil)
    }

    @Test("Time Machine's local snapshots are not drives")
    func timeMachineSnapshotsExcluded() {
        #expect(kind(
            "/Volumes/com.apple.TimeMachine.localsnapshots",
            name: "com.apple.TimeMachine.localsnapshots",
            internalDisk: true
        ) == nil)
        #expect(kind(
            "/Volumes/.timemachine/ABC",
            name: ".timemachine",
            internalDisk: true
        ) == nil)
    }

    @Test("A volume the system says isn't browsable is skipped")
    func nonBrowsableExcluded() {
        #expect(kind("/Volumes/Hidden", name: "Hidden", browsable: false, internalDisk: true) == nil)
        #expect(kind("/Volumes/Hidden", name: "Hidden", browsable: false, internalDisk: false) == nil)
    }
}
