// FileFlussCore — placeholder. Real types land per follow-up commit during
// the Phase 0 → Phase 1 migration (see Packages/FileFlussCore/Package.swift
// for the rationale).
//
// Order of incoming moves, smallest blast radius first:
//   1. SyncRule + SyncDirection  ✅ landed
//   2. CloudAccount + CloudProviderType  (~9 consumers, ~30 enum refs)
//   3. CloudProviderError  (~43 consumers — but a single enum, mostly mechanical)
//   4. CloudFileItem + CloudProvider protocol + ByteProgressHandler
//   5. Provider implementations one at a time, easiest (Dropbox, Box, GoogleDrive…)
//      first because OAuth flows already share a clean abstraction.
//   6. SearchIndex, SyncEngine, CacheManager — pure-Swift services with no AppKit.
//   7. KeychainService (last, since every provider depends on it).
//
// FileItem stays in the macOS app (it ties to NSWorkspace icons and the
// dual-panel local-folder browser, both macOS-specific). Mobile uses
// CloudFileItem + iOS-only `LocalFolderItem` to represent picked Files-app
// folders.
