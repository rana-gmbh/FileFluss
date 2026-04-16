# FileFluss 0.9 Beta

## New: Sync Mode

A full sync planner that pre-flight-calculates everything it's about to do before a single byte moves. Pick **Mirror**, **Newer**, or **Additive** — then review file counts, total size, and direction for every pair before confirming. Works across local folders and any connected cloud provider.

![FileFluss Sync Mirror with calculation](FileFluss%20Sync%20Mirror%20with%20calculation.png)

## New: Byte-level progress bar

Transfer progress is now byte-weighted, not file-count-based — so a single large file doesn't stall the bar at 0% while a folder full of small files flies past. Cloud-to-cloud transfers split evenly across download and upload phases. Every active transfer has a Cancel button with confirmation, and the new capsule-style bar shows gradient fill with an embedded percentage.

![FileFluss Progress bar](FileFluss%20Progress%20bar.png)

## Improvements

- Drag & drop into subfolder rows on both same-panel and opposite-panel drops
- New **Sync** top-level menu and **Help → Support Log** entry (60-second in-memory ring buffer of file & cloud operations, exportable via Save panel)
- kDrive `listDirectory` now paginates properly — folders with more than 10 items were silently dropping the tail
- OneDrive `listDirectory` follows `@odata.nextLink` past 1000 items
- `createFolder` is idempotent across Dropbox, OneDrive, kDrive, Koofr, SFTP, Nextcloud, MEGA, and Google Drive

## Bug fixes

- **pCloud** — retry transient `2055` metadata locks after bulk uploads, absorb eventual-consistency on `listfolder` after uploads and deletes
- **Google Drive** — cache uploaded file id so Replace doesn't race the query index and create a duplicate
- **MEGA** — uploads now write node keys with the required XOR obfuscation; previously round-tripped files decrypted to gibberish
- **QuickLook** — per-account temp cache is purged on init and invalidated on `size + modificationDate`, so stale previews no longer resurface after a decryption fix

## Under the hood

All five phases (create → upload → replace → delete → cleanup) are now exercised against every connected account on every debug build via the new cross-provider `VersionTestRunner`. This release ships after a clean **502/502 steps** pass across all eleven providers.

## Full Changelog

See the [commit history](https://github.com/rana-gmbh/filefluss/commits/main) for the complete list of changes.
