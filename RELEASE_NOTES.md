# FileFluss 1.5.1

A fix release for 1.5.

## Internal partitions now appear under Drives

If you have a second partition or a second APFS volume on your Mac's built-in disk, FileFluss didn't show it anywhere — the Drives section listed external and network drives only, and every internal volume that wasn't the startup disk was skipped. There was no way to open it from the sidebar.

Those volumes now appear in **Drives**, with their own icon so you can tell them apart from an external disk at a glance. The startup volume, the system's own volumes and Time Machine's local snapshots stay hidden, as before.

Thanks to Bernd for reporting it.

## Install

```bash
brew install --cask rana-gmbh/filefluss/filefluss
```

Already have FileFluss? `brew upgrade --cask filefluss`, or let the app update itself.
