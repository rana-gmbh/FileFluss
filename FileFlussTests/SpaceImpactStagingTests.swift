import Testing
import Foundation
import FileFlussCore
@testable import FileFluss

/// What a transfer needs on this Mac on its way through, and how that is
/// put to the user.
///
/// Asked for after a reader pointed out that a cloud-to-cloud transfer is
/// "an easy place to forget about a nearly full Mac SSD": the destination's
/// quota was checked, the disk every byte actually crosses was not.
@Suite("Transfer space: local staging")
@MainActor
struct SpaceImpactStagingTests {

    private func impact(
        destination: String = "Dropbox",
        isCloud: Bool = true,
        available: Int64? = 100_000_000_000,
        added: Int64 = 1_000_000_000,
        staging: SpaceImpact.Staging? = nil
    ) -> SpaceImpact {
        SpaceImpact(
            destinationName: destination, isCloud: isCloud,
            available: available, addedBytes: added, staging: staging
        )
    }

    // MARK: Which end is short

    /// The case that used to pass silently: plenty of room in the cloud,
    /// none on the Mac the files have to cross.
    @Test("A full Mac is a warning even when the destination is roomy")
    func stagingAloneWarns() {
        let warned = impact(
            available: 500_000_000_000,
            added: 48_000_000_000,
            staging: .init(volumeName: "Macintosh HD", needed: 48_000_000_000, free: 12_000_000_000)
        )

        #expect(warned.wouldExceed)
        #expect(!warned.destinationWouldExceed)
    }

    @Test("A full destination still warns on its own")
    func destinationAloneWarns() {
        let warned = impact(available: 1_000_000, added: 5_000_000)

        #expect(warned.wouldExceed)
        #expect(warned.destinationWouldExceed)
    }

    @Test("Room at both ends is no warning")
    func roomEverywhere() {
        let fine = impact(
            staging: .init(volumeName: "Macintosh HD", needed: 2_000_000_000, free: 400_000_000_000)
        )

        #expect(!fine.wouldExceed)
    }

    /// A staging volume whose free space couldn't be read must not invent a
    /// problem — the same rule the destination has always followed.
    @Test("An unreadable disk doesn't warn")
    func unknownStagingSpaceIsSilent() {
        let unknown = SpaceImpact.Staging(volumeName: "Macintosh HD", needed: 9_000_000_000, free: nil)

        #expect(!unknown.wouldExceed)
        #expect(!impact(staging: unknown).wouldExceed)
    }

    // MARK: What the user is told

    /// The two shortages have different remedies — free up the Mac, or free
    /// up the account — so the message has to say which one it is.
    @Test("The message names the disk and the setting that moves it")
    func wordingNamesTheDisk() {
        let text = SpaceImpactFormatter.warning(
            impact(
                available: 500_000_000_000,
                added: 48_000_000_000,
                staging: .init(volumeName: "Macintosh HD", needed: 48_000_000_000, free: 12_000_000_000)
            ),
            verb: "Copy"
        )

        #expect(text.contains("Macintosh HD"))
        #expect(text.contains("Settings"))
        #expect(text.contains("Continue anyway?"))
        // The destination is fine, so it isn't blamed.
        #expect(!text.contains("would exceed the available space"))
    }

    @Test("A destination shortage reads as it always did")
    func wordingForDestination() {
        let text = SpaceImpactFormatter.warning(impact(available: 1_000_000, added: 5_000_000), verb: "Copy")

        #expect(text.contains("Dropbox"))
        #expect(text.contains("would exceed the available space"))
    }

    @Test("Both short means both are said")
    func wordingForBoth() {
        let text = SpaceImpactFormatter.warning(
            impact(
                available: 1_000_000,
                added: 48_000_000_000,
                staging: .init(volumeName: "Macintosh HD", needed: 48_000_000_000, free: 12_000_000_000)
            ),
            verb: "Copy"
        )

        #expect(text.contains("would exceed the available space"))
        #expect(text.contains("Macintosh HD"))
    }

    /// Shown before anything is short, so the figures are visible while
    /// there is still a choice to make.
    @Test("The preview shows both figures")
    func previewShowsBoth() {
        let text = SpaceImpactFormatter.preview(
            impact(
                available: 500_000_000_000,
                added: 48_000_000_000,
                staging: .init(volumeName: "Macintosh HD", needed: 48_000_000_000, free: 120_000_000_000)
            )
        )

        #expect(text.contains("Dropbox"))
        #expect(text.contains("Macintosh HD"))
        #expect(text.contains("on the way"))
    }
}
