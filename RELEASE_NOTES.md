# FileFluss 0.5.0 Beta

## Universal Search Across All Sources

FileFluss now includes a powerful search function that lets you find files across **all connected cloud accounts and local files** from a single search popup.

- **Search everything at once** — one search query scans your local file system and every connected cloud provider simultaneously
- **Grouped results** — results are organized by source (Local Files, Google Drive, Dropbox, Nextcloud, etc.) for easy orientation
- **Source filtering** — quickly filter results by source using the chip-based filter bar
- **Open anywhere** — right-click any result to open it in the left or right panel, or double-click to open in the active panel
- **Progressive results** — results stream in as each source responds, no waiting for the slowest provider

## Cloud Provider Search APIs

Each cloud provider now supports native server-side search for fast, accurate results:

- **Google Drive** — Files.list with query parameter
- **Dropbox** — /files/search_v2 endpoint
- **OneDrive** — Microsoft Graph search API
- **Nextcloud** — WebDAV SEARCH method

## Smart Search Infrastructure

- **Spotlight integration** — uses macOS Spotlight (NSMetadataQuery) for fast recursive local file search
- **SQLite FTS5 index cache** — cloud file metadata is cached locally for instant prefix-match lookups, updated automatically as you browse
- **Debounced input** — search triggers after a short delay to avoid unnecessary API calls while typing

## Other Improvements

- Cleaner toolbar with dedicated search button (Cmd+F)
- Improved CloudFileListView performance by splitting complex SwiftUI view bodies
- Various internal optimizations for snappier UI response

## Full Changelog

See the [commit history](https://github.com/rana-gmbh/filefluss/commits/main) for the complete list of changes.
