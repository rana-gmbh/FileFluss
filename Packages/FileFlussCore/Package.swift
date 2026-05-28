// swift-tools-version: 6.0
import PackageDescription

/// Shared core that both the macOS app (FileFluss) and the iOS app
/// (FileFluss Mobile, in a separate private repo) consume. Holds models,
/// cloud-provider implementations, the search index, the sync engine, and
/// the keychain service — everything that's pure Foundation / URLSession /
/// Security / SQLite and has no AppKit or UIKit dependency.
///
/// Phase 0 lands the package as infrastructure only. File moves into
/// `Sources/FileFlussCore/` happen incrementally per follow-up commit so
/// each move is small and verifiable. While the package is empty, the
/// macOS app continues to compile its sources directly and the only effect
/// of importing FileFlussCore is to materialize the dependency.
///
/// Cross-platform: macOS 14+ and iOS 17+. The OAuth flow providers go
/// through `BrowserOpener` (a registry the host app populates at startup
/// — macOS plugs in NSWorkspace, iOS will plug in ASWebAuthenticationSession),
/// so the package itself has no AppKit or UIKit dependency.
let package = Package(
    name: "FileFlussCore",
    platforms: [
        .macOS(.v14),
        .iOS(.v17),
    ],
    products: [
        .library(name: "FileFlussCore", targets: ["FileFlussCore"]),
    ],
    targets: [
        .target(name: "FileFlussCore"),
        .testTarget(name: "FileFlussCoreTests", dependencies: ["FileFlussCore"]),
    ]
)
