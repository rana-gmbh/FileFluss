# FileFluss 1.7 Beta 3

The Mac and Windows apps are **feature-identical again**: the gallery view, reconnecting an account from its panel, and pCloud's browser sign-in, new on the Mac in 1.7 Beta 2, now come to Windows too. Both apps also gain **colour tags** for files and folders, and the gallery now shows **cloud photos as thumbnails**. From now on every feature arrives on both platforms in the same release.

This is a **beta**. Please keep backups of anything important, and tell me what you run into: [open an issue](https://github.com/rana-gmbh/FileFluss/issues/new) and say whether it's the Mac or the Windows app.

- **On a Mac:** download `FileFluss-v1.7.0-beta.3.dmg` below. The in-app updater and Homebrew only offer final releases, so they stay on 1.6 until 1.7 is final.
- **On Windows:** Help → Check for Updates… offers this beta. Otherwise download the installer below.

## New in 1.7, on Mac and Windows

### Gallery view

Each panel can now show its folder as a **gallery of thumbnails** instead of a list. Images, videos, PDFs and documents show a real preview, in local folders and in cloud accounts alike.

- Switch with the **List / Gallery switch at the right end of each panel's path bar**, or with **View → as List** and **View → as Gallery**: ⌃⌘1 / ⌃⌘2 on the Mac, Ctrl+Shift+1 / Ctrl+Shift+2 on Windows, changeable in Settings → Keyboard Map. Each panel remembers its own choice, so a photo folder can be a gallery on one side while the other side stays a list. The list stays the default.
- **⌘+ and ⌘-** on the Mac, **Ctrl+Plus and Ctrl+Minus** on Windows, make the thumbnails bigger and smaller, as they do with the rows of the list.
- Everything works in the gallery as it does in the list: selecting several items, double-clicking, the right-click menu, drag and drop, Quick Look with the arrow keys, and cut, copy and paste. On the Mac, double-clicking a folder in the gallery opens it again; in 1.7 Beta 2 it did nothing.
- **Cloud photos get thumbnails too.** Dropbox, Box, pCloud, Google Drive, OneDrive, kDrive, Nextcloud, Seafile, Synology Drive and GoPro supply their own small preview, which is also how videos get one. On every other account, images and PDFs up to 20 MB are downloaded once and drawn on your computer. iCloud Drive files that iCloud has moved off your computer are never downloaded just for a preview.
- Previews are kept on disk (up to 300 MB), so a folder you've seen once shows them at once. Only the tiles you can see are fetched, four at a time.

### Colour tags

Files and folders can now carry **Finder's seven colour tags**, in local folders and in every cloud account.

- **Right-click → Tags** offers the seven colours, shows which ones the selection already has, lists any other tags it carries, and offers Remove All Tags.
- Tagged items show **up to three colour dots**, after the name in the list and before it in the gallery. A tagged folder is drawn in the colour of its last tag, as Finder does on macOS 26.
- **On the Mac, local files and iCloud Drive use Finder's own tags,** so Finder and FileFluss show the same thing. A tag given in Finder shows in its own colour, also when it was given on a Mac set to another language.
- **MEGA and kDrive** have labels of their own; a tag is written there too, and a label set in their apps shows up in FileFluss.
- On **every other account**, and for **local files on Windows**, FileFluss keeps the tags itself, so they show only in FileFluss on that computer.
- **Tags travel with the file** when FileFluss renames, moves, copies, uploads, downloads, transfers between accounts or syncs it. A sync only adds tags, so it never takes off a tag given on the other side.

### Reconnect an account right where it stopped working

When FileFluss can no longer sign in to an account (a locked Keychain or a credential Windows can no longer read at launch, an access prompt dismissed after an update, credentials that are gone), the panel used to say "Cloud account not connected" and offer only Try Again.

- FileFluss now **reads the saved sign-in again** before giving up, so an account that was only unreadable at launch comes back by itself.
- Otherwise the panel shows **Sign-In Required** with the way back for that kind of account: the provider's page in the browser for Google Drive, Dropbox, OneDrive, Box and pCloud; the account's sign-in details, filled in with what's still saved, for password and token accounts; System Settings on the Mac and iCloud for Windows on Windows for iCloud.
- The sidebar marks such accounts with a grey dot and offers **Sign In Again** or **Reconnect…** for every kind of account.
- GitHub, Jottacloud and Drime take a **new token**, and TeraBox a fresh QR code, without removing and re-adding the account.
- After a new sign-in, every panel showing the account lists its folder again, not only the one you signed in from.

### pCloud signs in through pCloud's own page

pCloud stopped letting third-party apps sign in with a password. **Add Account now opens pCloud's sign-in page in your browser**, as it does for Box, and an existing account can be re-authorized from its settings. Accounts added the old way keep working.

## Fixed on Mac and Windows

- **Google Drive copes with files changed in the Drive web app.** A file that was deleted, trashed or moved there no longer keeps receiving uploads it can't take, or receives them in the trash; a folder deleted on the web is looked up again instead of listing as empty.
- **Google Docs, Sheets, Slides and Drawings show under the name they download as** (`Report.docx`), so syncs and copies agree on the name. Forms, Sites, maps and shortcuts, which can't be downloaded, are no longer listed.
- **MEGA shows changes made on the web or your phone** without restarting FileFluss, opens files in folders shared with you, and refuses a download whose key or checksum doesn't match instead of saving unreadable data.
- **GitHub and GoPro:** a folder that no longer exists is reported as missing instead of empty, so a Mirror from it can't delete your copy. A large GitHub folder that can't be listed completely says so.
- **WordPress lists every year and month of a large media library,** keeps uploads under their real name, and only ever deletes the exact item you chose.
- **Filen:** a folder with an item FileFluss can't decrypt reports an error instead of leaving the item out, which a Mirror would have taken as deleted.
- **Internxt:** when the folder can't be listed during a replace, nothing is replaced.
- **A new SFTP account shows the server's key fingerprint and asks before trusting it.** No password or key is sent until you confirm. If the key changes later, FileFluss refuses to connect and, when you edit the account, shows the old and new fingerprints and asks before trusting the new one. On the Mac, existing accounts keep the key they have been trusting.

## Fixed on the Mac

- **Moving a file to the Trash from a Desktop or Documents folder synced with iCloud is no longer refused without explanation.** Such files go to iCloud Drive's Trash, which macOS may keep FileFluss from using. FileFluss now opens iCloud Drive so macOS can ask for access, then tries again. If the Trash is still refused, it says where to allow access instead of pointing only at Full Disk Access.
- **SFTP keeps passwords and private keys off the disk.** They used to reach OpenSSH through files in the temporary folder. Now the password or passphrase is handed over through a private connection, and the key is loaded into a short-lived agent. FileFluss also no longer adds SFTP servers to your own `~/.ssh/known_hosts`.
- **SFTP handles names containing `$` or a backtick.** `Price $5.txt` was uploaded as `Price \$5.txt`, and such files couldn't be downloaded. A name containing a line break, which a server can list, is refused instead of being passed on.
- **FTP servers whose listings leave out a field now open** ([#70](https://github.com/rana-gmbh/FileFluss/issues/70)). A server that lists files without a group column, such as OpenHASP on small display boards, was refused as unreadable. FTP also reads Windows-style listings (`10-28-26  09:30PM  <DIR>  Photos`), and SFTP benefits from the same change. Windows already read both.
- **Mirror deletes again in folders whose path contains a space or an umlaut.** In a folder such as "Backup Übersicht", the files Mirror reported as deleted were left in place, and replacing a file there left a `.filefluss-part` file behind.
- **Names that differ only in capitals or accents no longer overwrite each other.** A cloud folder holding `README.md` and `Readme.md` (or two forms of "Café") kept only one of them on the Mac, and after a cut the other was gone from the cloud too. Now the second arrives as `Readme (2).md`. A Mirror between `readme.md` and `README.md` replaces the file in place instead of writing it and then deleting it.
- **A folder can't be copied or moved into itself.** Copying a folder into one of its own subfolders kept copying until the path was too long.
- **Replacing a folder from a cloud no longer deletes an unrelated file** such as `Report.pdf` next to it. Only an earlier download of the same Google document is replaced, and it goes to the Trash.
- **Tags survive a tags file that can't be read.** FileFluss now keeps a backup, falls back to it, and never writes over a file it couldn't read; before, all tags of that account were lost.
- **Files opened from a cloud get macOS's security check,** like any download: double-clicking, Open With, Quick Look and files taken out of a cloud archive.
- **Accounts shown in Finder are only reachable by Finder.** The local server behind them now requires a secret address and refuses requests from web pages. A copy to such a folder that is cut off no longer replaces the cloud file with the part that arrived.
- **Synology Drive and QNAP:** "Keep both" no longer overwrites the file you chose to keep; a failed download is reported instead of saving the NAS's error message as the file; a copy that is still running on the NAS isn't taken as finished; names containing `+` reach the right file. QNAP lists folders with more than 1,000 items completely.
- **Internxt replaces a file safely.** The old file is removed only once the new one is in place.
- **Koofr, Seafile and pCloud reach the right file when its name contains `&`, `=` or `+`.** Deleting `Q&A` could delete `Q`, and uploading `a&b.txt` to pCloud wrote a file called `a`. Seafile passwords containing these characters work too.
- **Dropbox keeps emoji in file names.** A name such as "🎉 Party.jpg" was uploaded under a garbled name and couldn't be downloaded.
- **Jottacloud** no longer signs you out when two requests renew the sign-in at once, and no longer hides a file while a newer upload of it is unfinished.
- **Mirror** replaces a file that stands where the source has a folder (it used to fail on every run), compares files with a resource fork by their content size, and works in folders under `/private`.
- **FileFluss stays responsive** while it indexes a drive, compares large folders, uploads a large folder, or when a network drive has gone offline. The Compare window builds its results in the background, and "Compare Date" no longer closes every open folder.

## Fixed on Windows

- **Thumbnails are the right way up.** List rows and gallery tiles showed every image upside down.
- **pCloud keeps umlauts and other non-ASCII characters in file names.** "Brückenstr.docx" was stored as `=?utf-8?B?…?=`.
- **Replacing a file on Internxt no longer fails now and then.** Internxt can take a moment to recognise a file it has just created; the replacement used to give up and stay under a temporary `.fileflusspart` name.
- **FTP keeps umlauts in file names on every server.** On servers that don't announce UTF-8, "Grüße.txt" was listed as "Gr??e.txt" and uploaded under that name.
- **Replacing a read-only file works.** It failed and left a `.fileflusspart` file next to it.
- **Sync keeps both files whose names are one name on the PC,** such as `README.md` and `Readme.md` from a cloud that tells them apart. One used to overwrite the other while Sync still reported success; now the second arrives as `Readme (2).md`, and later runs leave both alone.
- **Updates are installed only when they carry Rana GmbH's code signature,** in addition to matching the release's checksums, as Sparkle requires its signature on the Mac.
- **"Allow self-signed certificate" now trusts a NAS's certificate on first use, not every certificate.** If it changes later, FileFluss refuses to connect and, when you edit the account, shows the old and new fingerprints and asks before trusting the new one. New accounts turn the option on only for local addresses (such as `192.168.…` or `nas.local`); existing accounts keep their setting. This covers QNAP, Synology Drive, NAS WebDAV, Seafile and FTPS.
- **A network drive or share that has gone offline no longer freezes FileFluss.** Showing a mapped drive's name in the path bar or title, or typing a path to an offline server, could block the window for up to a minute.
- **A local path typed into a cloud panel's path bar opens on this PC.** `C:\…`, `\\server\share` and `%USERPROFILE%` used to be looked up inside the cloud account.
- **The Compare window stays responsive with thousands of selected items,** also after copying them, and when "Compare dates" is switched on or off.
- **Quick Look is smoother.** Large photos are prepared in the background, and arrowing past cloud files stops their downloads instead of fetching every one.
- **Opening the same cloud file twice** (a second double-click, or Enter after Quick Look) waits for the download already running instead of failing with "file in use".
- **Tags are safe when their file can't be read** at launch (for example while a virus scanner holds it). They used to be replaced with an empty set. A save that fails is now tried again instead of being dropped.
- **Google Drive:** uploading a file that was deleted in the Drive web app works again; it failed until FileFluss was restarted.
- **Synology Drive** reports a rename that fails after copying or moving, instead of reporting success.
- **Internxt:** when the old file can't be removed during a replace, FileFluss now says so and leaves the old file as it was.
- **A folder copy goes on past a subfolder it can't read.** Everything readable is copied, and what was skipped is listed with the reason; a move leaves the skipped parts where they were. One unreadable subfolder used to stop the whole copy before anything was written.
- **Opening a file never holds the window,** even from a slow network share. Programs and scripts opened from an account mapped as a drive now get Windows' security warning, like any download.
- **When a folder you're looking at is deleted elsewhere,** the panel moves up to the nearest folder that still exists, and the status bar says "Loading..." while a folder is listed instead of "0 files".
- **Mirror** replaces a file that stands where the source has a folder (it used to fail on every run), and **Compare** uses Sync's date tolerance, so files on SFTP and FTP no longer all show as different.
- **Rate limits** are retried once instead of over and over, and reported as rate limits rather than as timeouts. **QNAP** lists very large folders completely and waits properly for server-side copies; **S3** handles object keys containing `./` or `../`.
- **The support log** names each item a transfer couldn't copy, and why.
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
