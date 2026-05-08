# FileFluss 1.0

**FileFluss leaves beta.** This is the first full, non-prerelease version — a milestone focused on usability and a polished out-of-beta experience.

## What's new in 1.0

### A new app icon

A brand-new Light/Dark app icon designed by **[@JohnnyFireOne](https://github.com/JohnnyFireOne)** — thank you! The dock icon swaps live whenever macOS toggles between Light and Dark mode.

### Compare Folders

A new toolbar action that opens a non-destructive side-by-side diff between the two panels.

- Status chips for **Different**, **Only on Left**, **Only on Right**, **Identical**, with counts.
- **Collapsible folders** — a folder that exists only on one side, or contains many differing files, becomes one expandable row instead of a wall of children.
- Optional **Compare Date** — flags files with matching size but diverging modification dates, shows both modify and create timestamps, and tags whichever side is newer.
- Movable, resizable window; **Compare Again** button re-runs against the current folders without re-scanning while you browse.

### Usability improvements

- **Open files in the default app** — double-click any file in a local *or* cloud panel and it opens in Word, Preview, Photos, etc. Cloud files are cached on first open so reopening is instant.
- **Copy Mode and Move Mode** — two toolbar toggles that force every drag & drop to that action and skip the *Move or Copy?* prompt. Mutually exclusive and reset on relaunch.
- **Drop onto path-bar breadcrumbs** — files can be dropped on any ancestor folder right from the path bar, no need to navigate to it first.
- **Independent panels for the same cloud account** — opening kDrive (or any cloud) on both panels now navigates each side independently, just like local folders. Dragging between them does an in-cloud move/copy without re-uploading.
- **Movable Search and Compare windows** — both run as proper windows now, draggable so the panels behind them stay visible. Their size and position are remembered across launches.
- **Search stays open after picking a result**, so you can act on multiple hits without reopening the window.
- **Real Quick Look thumbnails** for local images and PDFs; **colourful generic icons** for cloud files based on the file extension.
- **Built-in FileFluss Help** under the Help menu (⌘?), with documentation for every feature.

## Installation

### Homebrew

```bash
brew tap rana-gmbh/filefluss
brew install --cask filefluss
```

### Manual

Download `FileFluss-v1.0.dmg` below and drag FileFluss.app into your Applications folder.

## Requirements

- macOS 14.0 (Sonoma) or later
