# FileFluss

A dual-panel file manager for macOS that lets you conveniently handle files across cloud storage providers.

## Features

- **Dual-panel file browser** — navigate two directories side by side, copy and move files between panels with keyboard shortcuts
![FileFluss Main window](FileFluss%20Screenshot%20Main%20Window.png)
- **Multi-cloud support** — connect multiple cloud storage accounts and browse them directly alongside your local files
- **Cloud-to-cloud transfers** — move or copy files between cloud providers without downloading to your machine first
- **Native performance** — built with SwiftUI and AppKit for a fast, responsive experience on macOS
- **Quick Look previews** — preview files inline without leaving the app
- **Keyboard-driven workflow** — navigate, select, copy, move, rename, and delete with shortcuts
- **Folder size calculation** — calculate folder sizes in the background without blocking the UI
- **Favorites** — pin frequently accessed local and cloud folders to the sidebar
- **Hidden files toggle** — show or hide hidden files per panel

## Supported Cloud Providers

- **pCloud**
- **kDrive** (Infomaniak)
- **OneDrive** (Microsoft)
- **Koofr**

## Requirements

- macOS 14.0 (Sonoma) or later

## Installation

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
