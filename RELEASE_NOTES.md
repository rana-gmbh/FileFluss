# FileFluss 1.2

A polish release — many small quality-of-life upgrades and more customization across the panels, sidebar, and cloud-account flows.

## Highlights

- **Cloud storage usage at a glance.** Most connected cloud accounts now show their used / free storage in the status bar, and the same numbers appear in the sidebar tooltip for each account.
- **Independently resizable sidebars.** The left and right sidebars resize separately, and either one collapses to an icon-only mode when you want more room for the file list.
- **Finder-style row size toggle.** View menu → row size, or `⌘+` / `⌘-`, to step the rows between compact and roomy without leaving the keyboard.
- **Auto-Resize column toggle.** Right-click the Name column header to flip Auto-Resize on or off per panel — local and cloud panels remember their setting independently.
- **Drag folders into Favorites.** Drop any folder onto the Favorites section of the sidebar; a row-reorder-style insertion indicator shows exactly where the favorite will land.
- **Edit cloud-account credentials in place.** Settings → Cloud Accounts lets you update credentials on an existing account — no need to remove and re-add it.
- **Remove a cloud account from the sidebar.** Opt-in context menu entry on each cloud account row for a one-click removal, without diving into Settings.
- **Resizable Add Cloud Account sheet** that fits the screen instead of clipping, and now recovers cleanly when the OAuth browser flow is cancelled instead of getting stuck.
- **S3: pin a bucket / sub-folder at connection time.** Optional path you set once when adding the account, so the panel opens directly inside the folder you actually care about.
- **SFTP folders no longer appear empty** when the server returns ISO-date timestamps or SELinux longname listings.
- **Cloud delete dialog** now names the actual account instead of a hardcoded provider name.
- **Refreshed cloud-provider logos** — fourteen icons redrawn for a more consistent look across the picker and sidebar.
- **Dark app-icon variant** ships in the asset catalog; the dock icon swaps live when macOS toggles appearance.
- **Sidebar transfer list** inserts the newest transfer at the top so the latest activity is always visible without scrolling.
- **Backend optimization** for faster loading, lower memory usage, and a cleaner foundation for future updates.

## Upgrading from 1.1.1

Your cloud accounts carry over — no need to re-add them after upgrading.

## Installation

### Homebrew

```bash
brew upgrade --cask filefluss
```

### Manual

Download `FileFluss-v1.2.dmg` below and drag FileFluss.app into your Applications folder.

## Requirements

- macOS 14.0 (Sonoma) or later
