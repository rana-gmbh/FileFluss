# FileFluss 1.5.1

A small feature and three fixes, all from reports after 1.5. Thank you for them.

## Open a terminal at the folder you're in

Right-click a folder and choose **Open in Terminal**, or press **⌃⌘T** — rebindable like every other command in Settings → Keyboard.

One selected folder opens that folder; a selected file, no selection, or several items open the folder you're looking at, the same way Finder decides it.

It works on cloud accounts too. A terminal can only open a folder that exists on this Mac, so an account that isn't mounted yet offers to mount itself first and then opens inside the volume.

**Which terminal** is up to you, in Settings → General: Terminal by default, with iTerm, Ghostty, kitty, WezTerm and Warp offered when they're installed, and *Choose…* for anything else.

## Fixes

- **Other partitions on your built-in disk now appear under Drives.** The sidebar listed external and network drives only, so a second partition or APFS volume on the internal SSD showed up nowhere and couldn't be opened at all. The startup volume, macOS's own volumes and Time Machine's local snapshots stay hidden, as before. Thanks to Bernd for reporting it.
- **"The folders from last time" restores cloud panels properly.** A cloud panel — SFTP most visibly — came back at the account root instead of the folder it was left in. Restoring was issuing two navigations at once and the wrong one was winning.
- **macOS no longer asks about an unsecured connection on every Finder mount.** Mounting an account bridges it to Finder through a local server, and the request to keep that quiet was being sent in a form macOS ignored. Your accounts' own connections were never affected by this.

## Install

```bash
brew trust rana-gmbh/filefluss && brew install --cask rana-gmbh/filefluss/filefluss
```

Already have FileFluss? `brew upgrade --cask filefluss`, or let the app update itself.
