# FileFluss

A dual-panel file manager for macOS that lets you conveniently handle files across cloud storage providers.

![FileFluss main window](Screenshots/FileFluss%20main.webp)

## What FileFluss does

FileFluss puts a left and a right panel side by side. Each panel can show a local folder or a connected cloud account, and the two are independent — even when you have the same cloud account loaded in both panels you can browse to different folders on each side. Drag and drop, copy, move, sync, compare, search — all between the two panels with native macOS performance.

## Features

### Two independent panels

- Open a different local folder, cloud account, or favourite on each side.
- Same cloud account on both panels? They navigate independently — and dragging between them does an **in-cloud** move/copy without re-uploading.
- Per-panel sidebar with favourites, locations, and connected cloud accounts.
- Active panel is marked with a thin accent line; toolbar actions and shortcuts target it.

### Multi-cloud, in one place

Connect once in Settings → Cloud Accounts and the account appears in both panels' sidebars.

- pCloud
- kDrive (Infomaniak)
- OneDrive (Microsoft)
- Google Drive
- Dropbox
- Mega
- Koofr
- NextCloud
- WebDAV
- SFTP
- WordPress (Media Library)
- GMX Cloud (MediaCenter)

Cloud-to-cloud transfers, Google Workspace files auto-converted to DOCX/XLSX/PPTX on download, modification dates preserved.

### Drag & drop, with shortcuts

- Drop files between panels — a *Move or Copy?* prompt asks once.
- Drop directly onto a folder row, the table background, or **any breadcrumb in the path bar** — files land where you expect.
- Right-click → *Copy to Other Panel* / *Move to Other Panel* for the no-prompt version.
- Side-by-side conflict resolution dialog with newer/older labels and Apply-to-All.

### Copy Mode and Move Mode

Two toolbar toggles for when you're in the flow and don't want the *Move or Copy?* prompt every time:

- **Copy Mode** forces every drag & drop to copy.
- **Move Mode** forces every drag & drop to move.

Mutually exclusive, and both reset on relaunch so a stuck Move Mode can't quietly destroy files across sessions.

### Sync

![FileFluss Sync](Screenshots/FileFluss%20sync.webp)

Make one folder match another — local↔local, local↔cloud, or cloud↔cloud.

- **Newer** — copy files that are newer on the source.
- **Mirror** — make the destination identical, including deletes (with explicit confirmation).
- **Additive** — only copy files that are missing; never overwrite.

A pre-flight plan shows file counts, total bytes, and direction before a single byte moves. Byte-weighted progress so a single large file doesn't stall the bar. Cancel any transfer with confirmation.

### Compare Folders

![FileFluss Compare](Screenshots/FileFluss%20compare.webp)

A non-destructive side-by-side diff of two folders.

- Color-coded statuses: **Different**, **Only on Left**, **Only on Right**, **Identical**.
- **Collapsible folders** — a folder that exists only on one side, or contains many differing files, shows a single summary row that you expand to drill in.
- **Filter chips** narrow to one status (e.g. show only differences).
- **Compare Date** — opt-in checkbox flagging files whose size matches but modification date differs, with both modify and create timestamps and a *Left newer* / *Right newer* badge.
- **Compare Again** — runs against the panels' *current* folders. Browsing the panels while the compare window is open does not retrigger the scan, so no surprise re-enumerations of cloud trees.
- The compare window is movable and resizable, so you can keep both panels visible behind it.

### Universal search

![FileFluss Search](Screenshots/FileFluss%20search.webp)

Search the active local folder and every connected cloud account at once.

- Press **⌘F** or click Search in the toolbar.
- Results stream in per source as each one responds.
- **Double-click** a result to navigate the active panel to its folder.
- **Right-click** to open a result explicitly in the Left or Right panel — the search window stays open so you can pick more.
- Movable, resizable window; size and position remembered across launches.

### Open files like local

- Double-click a file in any panel — local or cloud — and it opens in the system default app (Word, Preview, Photos, etc.).
- Cloud files are downloaded to a temp cache the first time, so reopening is instant.
- **Quick Look** with Space, just like Finder.
- Local image and PDF rows show a real Quick Look thumbnail of the file content; cloud files show a colourful generic icon based on the extension.

### Other niceties

- Folder size calculation in the background, never blocking the UI.
- Favourites pin frequently-used local and cloud folders to the sidebar.
- Hidden-files toggle per panel.
- Customisable toolbar (right-click → Customize Toolbar…).
- Light/Dark app icon variants — the dock icon swaps live when macOS toggles appearance.
- Built-in **FileFluss Help** under the Help menu (⌘?).

## Requirements

- macOS 14.0 (Sonoma) or later

## Installation

### Homebrew (recommended)

```bash
brew tap rana-gmbh/filefluss
brew install --cask filefluss
```

### Manual

Download the latest DMG from the [Releases](https://github.com/rana-gmbh/filefluss/releases) page and drag FileFluss.app into your Applications folder.

## Building from source

FileFluss uses [XcodeGen](https://github.com/yonaskolb/XcodeGen) to generate the Xcode project.

```bash
brew install xcodegen
xcodegen generate
xcodebuild build -project FileFluss.xcodeproj -scheme FileFluss -destination 'platform=macOS'
```

## Credits

- App icon (Light & Dark variants) — thanks to [@JohnnyFireOne](https://github.com/JohnnyFireOne).
- File-type icons — [redbooth/free-file-icons](https://github.com/redbooth/free-file-icons), © 2009 Teambox Technologies, S.L. (MIT License).

## Support

If you want to support the FileFluss project, please consider [buying me a coffee](https://buymeacoffee.com/robertrudolph).

## License

FileFluss is released under the [GNU General Public License v3.0](LICENSE).

Copyright &copy; 2026 Rana GmbH.
