# FileFluss

A dual-panel file manager for macOS that lets you conveniently handle files across cloud storage providers.

## Features

- **Dual-panel file browser** — navigate two directories side by side, copy and move files between panels with keyboard shortcuts
![FileFluss Main window](FileFluss%20Screenshot%20Main%20Window.png)
- **Multi-cloud support** — connect multiple cloud storage accounts and browse them directly alongside your local files
![FileFluss Cloud Providers new](FileFluss%20Cloud%20Provider3.png)
- **Cloud-to-cloud transfers** — conveniently move or copy files between cloud providers
![FileFluss transfer details](FileFluss%20Transfer%20Details.png)
- **Conflict resolution** — when files already exist at the destination, a side-by-side comparison shows both files with dates, sizes, and newer/older labels, plus Apply to All, Stop, Skip, Keep Both, and Replace actions
![Conflict Resolution Dialog](fileflussnewdia.png)
- **Universal search** — search across all local files and connected cloud accounts at once, with results grouped by source and right-click to open in either panel
![FileFluss Search](FileFluss%20Search.png)
- **Native performance** — built with SwiftUI and AppKit for a fast, responsive experience on macOS
- **Quick Look previews** — preview files inline without leaving the app
- **Keyboard-driven workflow** — navigate, select, copy, move, rename, and delete with shortcuts
- **Folder size calculation** — calculate folder sizes in the background without blocking the UI
![FileFluss Folder calculation](FileFluss%20Context%20Menu%20and%20Folder%20size%20calculation.png)
- **Favorites** — pin frequently accessed local and cloud folders to the sidebar
- **Hidden files toggle** — show or hide hidden files per panel
- **Automatic Google Workspace conversion** — Google Docs, Sheets, and Slides are automatically converted to DOCX, XLSX, and PPTX when transferring to local folders or other cloud services
- **Preserved modification dates** — files transferred from cloud storage retain their original modification dates

## Supported Cloud Providers

- **pCloud**
- **kDrive** (Infomaniak)
- **OneDrive** (Microsoft)
- **Google Drive** (Google)
- **Nextcloud**
- **Dropbox**
- **Koofr**
- **Mega** — *new in 0.8*
- **WebDAV** — *new in 0.8*
- **SFTP** — *new in 0.8*
- **WordPress** (Media Library) — *new in 0.8*

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
