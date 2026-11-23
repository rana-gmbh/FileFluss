# FileFluss 1.7 Beta 3

The Mac and Windows apps are **feature-identical again**: the gallery view, reconnecting an account from its panel, and pCloud's browser sign-in, new on the Mac in 1.7 Beta 2, now come to Windows too. From now on every feature arrives on both platforms in the same release.

This is a **beta**. Please keep backups of anything important, and tell me what you run into: [open an issue](https://github.com/rana-gmbh/FileFluss/issues/new) and say whether it's the Mac or the Windows app.

- **On a Mac:** download `FileFluss-v1.7.0-beta.3.dmg` below. The in-app updater and Homebrew only offer final releases, so they stay on 1.6 until 1.7 is final.
- **On Windows:** Help → Check for Updates… offers this beta. Otherwise download the installer below.

## New in 1.7, on Mac and Windows

### Gallery view

Each panel can now show its folder as a **gallery of thumbnails** instead of a list. Local images, videos, PDFs and documents show a real preview; cloud files show the icon of their file type.

- Switch with the **List / Gallery switch at the right end of each panel's path bar**, or with **View → as List** and **View → as Gallery**: ⌃⌘1 / ⌃⌘2 on the Mac, Ctrl+Shift+1 / Ctrl+Shift+2 on Windows, changeable in Settings → Keyboard Map. Each panel remembers its own choice, so a photo folder can be a gallery on one side while the other side stays a list. The list stays the default.
- **⌘+ and ⌘-** on the Mac, **Ctrl+Plus and Ctrl+Minus** on Windows, make the thumbnails bigger and smaller, as they do with the rows of the list.
- Everything works in the gallery as it does in the list: selecting several items, double-clicking, the right-click menu, drag and drop, Quick Look with the arrow keys, and cut, copy and paste.

### Reconnect an account right where it stopped working

When FileFluss can no longer sign in to an account (a locked Keychain or a credential Windows can no longer read at launch, an access prompt dismissed after an update, credentials that are gone), the panel used to say "Cloud account not connected" and offer only Try Again.

- FileFluss now **reads the saved sign-in again** before giving up, so an account that was only unreadable at launch comes back by itself.
- Otherwise the panel shows **Sign-In Required** with the way back for that kind of account: the provider's page in the browser for Google Drive, Dropbox, OneDrive, Box and pCloud; the account's sign-in details, filled in with what's still saved, for password and token accounts; System Settings on the Mac and iCloud for Windows on Windows for iCloud.
- The sidebar marks such accounts with a grey dot and offers **Sign In Again** or **Reconnect…** for every kind of account.
- GitHub, Jottacloud and Drime take a **new token**, and TeraBox a fresh QR code, without removing and re-adding the account.
- After a new sign-in, every panel showing the account lists its folder again, not only the one you signed in from.

### pCloud signs in through pCloud's own page

pCloud stopped letting third-party apps sign in with a password. **Add Account now opens pCloud's sign-in page in your browser**, as it does for Box, and an existing account can be re-authorized from its settings. Accounts added the old way keep working.

## Fixed on Windows

- **Thumbnails are the right way up.** List rows and gallery tiles showed every image upside down.
- **pCloud keeps umlauts and other non-ASCII characters in file names.** "Brückenstr.docx" was stored as `=?utf-8?B?…?=`.
- **Replacing a file on Internxt no longer fails now and then.** Internxt can take a moment to recognise a file it has just created; the replacement used to give up and stay under a temporary `.fileflusspart` name.
- **FTP keeps umlauts in file names on every server.** On servers that don't announce UTF-8, "Grüße.txt" was listed as "Gr??e.txt" and uploaded under that name.
- **Replacing a read-only file works.** It failed and left a `.fileflusspart` file next to it.
- **Sync keeps both files whose names are one name on the PC,** such as `README.md` and `Readme.md` from a cloud that tells them apart. One used to overwrite the other while Sync still reported success; now the second arrives as `Readme (2).md`, and later runs leave both alone.
- **Esc closes Quick Look** while you're still in the file list, as on the Mac. It used to work only after clicking into the preview window.
- **Copying and syncing between local folders keeps names exactly as they are.** A name with a leading space or longer than 200 characters was shortened, and a Mirror then deleted the correctly named copy.

## Download

| | File |
|---|---|
| **Mac**: Apple silicon and Intel, macOS 14 or later | `FileFluss-v1.7.0-beta.3.dmg` |
| **Windows**: Intel or AMD (x64) | `FileFluss-Setup-1.7.0-beta.3-x64.exe`, or portable: `FileFluss-1.7.0-beta.3-x64-portable.zip` |
| **Windows**: ARM (Snapdragon, ARM64) | `FileFluss-Setup-1.7.0-beta.3-arm64.exe`, or portable: `FileFluss-1.7.0-beta.3-arm64-portable.zip` |

- The Mac app is signed by Rana GmbH and notarized by Apple. Open the DMG and drag FileFluss into Applications.
- The Windows installer needs no administrator rights and keeps FileFluss up to date by itself; the portable version runs from any folder and doesn't update itself. Both are signed by Rana GmbH. Windows 10 (version 2004) or Windows 11.
- `SHA256SUMS.txt` lists the checksum of every file in this release.
