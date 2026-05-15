// FileFlussCore — placeholder. Real types land per follow-up commit during
// the Phase 0 → Phase 1 migration (see Packages/FileFlussCore/Package.swift
// for the rationale).
//
// Order of incoming moves, smallest blast radius first:
//   1. SyncRule + SyncDirection  ✅ landed
//   2. CloudAccount + CloudProviderType  ✅ landed
//   3. CloudProviderError  ✅ landed
//   4. CloudFileItem + CloudProvider protocol + ByteProgressHandler  ✅ landed
//      (enforceUploadSizeLimit helper folded back into CloudProviderError.swift)
//   5. KeychainService  ✅ landed (reordered earlier — providers reference it
//      directly, so it had to come before step 6's provider moves)
//   6. Provider implementations — in batches by AppKit dependency.
//      First wave (no AppKit): pCloud, kDrive, Koofr, Mega, Filen, GMX,
//      Seafile, SFTP, WebDAV, WordPress, S3, S3Compatible, Synology C2,
//      Synology Drive, iCloud  ✅ landed (6a iCloud, 6b the rest, plus the
//      S3 XML parsers and the WebDAV PROPFIND parser pulled out of
//      NextCloudAPIClient since WebDAV consumes it).
//      Second wave (uses the BrowserOpener shim — macOS app registers
//      an NSWorkspace.shared.open handler at startup; iOS will plug in
//      ASWebAuthenticationSession in Phase 1):
//      Box, Dropbox, Google Drive, OneDrive, NextCloud browser-OAuth
//      ✅ landed.
//   7. SearchIndex, SyncEngine, CacheManager — pure-Swift services with no AppKit.
//
// FileItem stays in the macOS app (it ties to NSWorkspace icons and the
// dual-panel local-folder browser, both macOS-specific). Mobile uses
// CloudFileItem + iOS-only `LocalFolderItem` to represent picked Files-app
// folders.
