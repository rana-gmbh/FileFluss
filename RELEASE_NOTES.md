# FileFluss 0.4.0 Beta

## New Cloud Storage Providers

### Google Drive
Full integration with Google Drive via OAuth2 with PKCE. Browse, upload, download, rename, and delete files directly from FileFluss. Google Workspace files (Docs, Sheets, Slides, Drawings) are automatically exported to compatible formats on download.

### Nextcloud
Connect to any Nextcloud server using app password authentication. All file operations are supported over WebDAV, including folder creation, rename, and recursive uploads.

### Dropbox
Full Dropbox integration via OAuth2 with PKCE. Supports path-based file access, cursor-based pagination for large directories, and upload sessions for files over 150 MB. Non-ASCII file names in paths are properly escaped for Dropbox's API header requirements.

## UI Improvements

- **Redesigned rename dialog** — the rename popup now uses a clean SwiftUI alert consistent with the Create Folder dialog
- **Copy-first drag & drop** — drop confirmation dialogs now default to "Copy Here" (highlighted) instead of "Move Here"
- **Removed unused menus** — the Edit menu and Sync settings tab have been removed for a cleaner interface
- **Upload overwrite protection** — uploading files to cloud storage now shows a confirmation dialog when files with the same name already exist at the destination

## Transfer Reliability

- **Improved error reporting** — failed transfers now display "Failed" in the sidebar instead of "Done", with the full error message visible in the transfer details popover
- **Automatic token refresh on 401** — cloud file operations automatically retry with a refreshed token if authentication expires mid-transfer
- **Graceful folder conflict handling** — uploading folders that already exist on the cloud no longer causes errors; existing folders are reused

## Full Changelog

See the [commit history](https://github.com/rana-gmbh/filefluss/commits/main) for the complete list of changes.
