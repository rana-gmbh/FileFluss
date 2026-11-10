## FileFluss for Windows — 1.6 Beta

FileFluss now runs on Windows too: a native Windows 10 and 11 app with the feature set of **FileFluss 1.6 for Mac** — two independent panels, every cloud account side by side with your local folders, and copy, move, sync, compare and search between them.

> **On a Mac?** Nothing changes for you. This release is for Windows only.

This is a **beta**. Everything below is in, but it hasn't yet been used as widely as the Mac app. Please keep backups of anything important, and tell me what you run into: [open an issue](https://github.com/rana-gmbh/FileFluss/issues/new) and mention Windows.

---

## Download

| Your PC | Installer | Portable |
|---|---|---|
| Intel or AMD (x64) | `FileFluss-Setup-1.6.0-beta.1-x64.exe` | `FileFluss-1.6.0-beta.1-x64-portable.zip` |
| ARM (Snapdragon, ARM64) | `FileFluss-Setup-1.6.0-beta.1-arm64.exe` | `FileFluss-1.6.0-beta.1-arm64-portable.zip` |

- **The installer** needs no administrator rights. It installs for your user into `%LOCALAPPDATA%\Programs\FileFluss`, adds a Start menu entry and an uninstaller, and **keeps FileFluss up to date** by itself.
- **The portable version** runs from any folder, a USB stick included. Unzip it and start `FileFluss.exe`; it doesn't update itself.
- Both are self-contained: no .NET install is needed. Windows 10 (version 2004) or Windows 11.
- `SHA256SUMS.txt` lists the checksum of every file.

**The beta isn't code-signed yet**, so Windows SmartScreen asks you to confirm the first start: **More info → Run anyway**.

Settings, accounts and the search index live in `%LOCALAPPDATA%\FileFluss`, so neither an update nor uninstalling touches them. Sign-in details are encrypted for your Windows user account.

---

## What's in it

### Two panels, every cloud

- A left and a right panel, each showing a local folder, a drive or a cloud account — independently, even the same account in both. Or one panel, if you prefer.
- **Every service the Mac app supports:** Google Drive, Dropbox, OneDrive, Box, iCloud Drive, pCloud, MEGA, Filen, Internxt, Jottacloud, kDrive, Koofr, Nextcloud, Seafile, GMX Cloud, Drime, Amazon S3 and S3-compatible storage, Synology C2 and Synology Drive, **QNAP, UGREEN and ASUSTOR** NAS, WebDAV, SFTP, FTP/FTPS, WordPress, GoPro cameras and **GitHub** (read-only, with snapshot ZIPs to back your repositories up to another cloud).
- Copy and move between any two of them by drag and drop, F5/F6 or copy and paste, with progress, speed and time left in the sidebar and the taskbar. Conflicts ask **Replace, Keep Both or Skip**, one question at a time.

### Made for Windows

- **Map as Network Drive:** any account appears as a drive letter in File Explorer.
- **Drag or paste cloud files straight into Explorer**, the desktop or any other app. They download only when they're dropped.
- **Open With** submenu with the apps Windows knows for the file, the Recycle Bin, Explorer's icons and Properties, **Open in Terminal** (Windows Terminal, PowerShell, cmd, Git Bash), thumbnails, Quick Look previews with Windows' own preview handlers.
- **Explorer or Commander keyboard layouts**, every shortcut customisable. Light and dark mode, and English, German, Simplified and Traditional Chinese.

### Archives

ZIP, RAR, 7z and TAR archives open in the panel like a folder, on your PC and on any cloud account. Preview or open a single file inside, drag files out, **Extract Here** or to the other panel, and **Compress** to ZIP, 7-Zip, TAR.GZ, TAR.BZ2 or TAR.XZ. Entries that try to escape their folder are refused.

### Tools

- **Search** across your local folders, every drive and every connected account at once.
- **Offline index:** index a drive or an account, and browse and search it even while it's unplugged or offline.
- **Compare Folders**, **Sync** (mirror, update newer or add only, with a preview of every change), **Storage Analysis** of folders, drives and accounts, and **share links** in one click.

### Built not to lose a file

Everything 1.6 for Mac does to keep your files safe is in: a move deletes the source only once everything has arrived, Replace swaps the new copy in only when it's complete, Mirror stops instead of guessing when a folder can't be read, and a repeated sync has nothing to do. The PC stays awake while files are on their way.

---

## Known limits

- Not code-signed yet (see above).
- **Map as Network Drive** uses Windows' WebDAV client, which refuses files over 50 MB unless an administrator raises its limit. For large files, copy through the FileFluss panels.
- Creating **RAR** archives needs RARLAB's own tool; FileFluss offers to fetch it from rarlab.com, checked against a known fingerprint.
