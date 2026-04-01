# FileFluss

A dual-panel file manager for macOS that lets you conveniently handle files across cloud storage providers.

## Features

- **Dual-panel file browser** — navigate two directories side by side, copy and move files between panels with keyboard shortcuts
![FileFluss Main window](FileFluss%20Screenshot%20Main%20Window.png)
- **Multi-cloud support** — connect multiple cloud storage accounts and browse them directly alongside your local files
![FileFluss Cloud Providers new](FileFluss%20Cloud%20Provider2.png)
- **Cloud-to-cloud transfers** — conveniently move or copy files between cloud providers
![FileFluss transfer details](FileFluss%20Transfer%20Details.png)
- **Native performance** — built with SwiftUI and AppKit for a fast, responsive experience on macOS
- **Quick Look previews** — preview files inline without leaving the app
- **Keyboard-driven workflow** — navigate, select, copy, move, rename, and delete with shortcuts
- **Folder size calculation** — calculate folder sizes in the background without blocking the UI
![FileFluss Folder calculation](FileFluss%20Context%20Menu%20and%20Folder%20size%20calculation.png)
- **Favorites** — pin frequently accessed local and cloud folders to the sidebar
- **Hidden files toggle** — show or hide hidden files per panel

## Supported Cloud Providers

- **pCloud**
- **kDrive** (Infomaniak)
- **OneDrive** (Microsoft)
- **Google Drive** (Google) — *new in 0.4*
- **Nextcloud** — *new in 0.4*
- **Dropbox** — *new in 0.4*
- **Koofr**

## What's New in 0.4.0 Beta

- **Google Drive integration** — full OAuth2 authentication with PKCE, file browsing, upload/download, Google Workspace file export (Docs, Sheets, Slides)
- **Nextcloud integration** — connect via WebDAV with app password authentication, full file management support
- **Dropbox integration** — OAuth2 with PKCE, path-based file access, upload session support for large files
- **Upload overwrite protection** — confirmation dialog when uploading files that already exist on the cloud
- **Improved transfer error reporting** — failed transfers now show "Failed" instead of "Done", with full error details in the sidebar
- **Redesigned rename dialog** — consistent SwiftUI alert style matching the Create Folder dialog
- **Copy-first drag & drop** — drop dialogs now default to Copy (highlighted) instead of Move
- **Removed unused Edit menu** and Sync settings tab for a cleaner interface

## Requirements

- macOS 14.0 (Sonoma) or later

## Installation

### Homebrew

```bash
brew tap rana-gmbh/filefluss
brew install --cask filefluss
```

### Manual

Download the latest release from the [Releases](https://github.com/rana-gmbh/filefluss/releases) page and drag FileFluss.app to your Applications folder.

## Building from Source

FileFluss uses [XcodeGen](https://github.com/yonaskolb/XcodeGen) to generate the Xcode project.

```bash
# Install XcodeGen if needed
brew install xcodegen

# Generate the Xcode project
xcodegen generate

# Build
xcodebuild build -project FileFluss.xcodeproj -scheme FileFluss -destination 'platform=macOS'
```

## Support

If you want to support the FileFluss project, please consider [buying me a coffee](https://buymeacoffee.com/robertrudolph).

## License

FileFluss is released under the [GNU General Public License v3.0](LICENSE).

Copyright &copy; 2026 Rana GmbH.
