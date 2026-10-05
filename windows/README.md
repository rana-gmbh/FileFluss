# FileFluss for Windows

Native Windows port of FileFluss, tracking the macOS app's feature set. C# on .NET 10 with
WPF; the macOS Swift code is the specification, and the macOS `.strings` catalogues are the
single source of truth for every shared string.

| Area | Windows |
|---|---|
| Panels | Two independent panels (or one), local folders and cloud accounts side by side; copy, move, rename, delete to the Recycle Bin, drag & drop |
| Cloud accounts | Box, Dropbox, Filen, FTP/FTPS, GMX Cloud, GoPro, Google Drive, iCloud Drive, Internxt, Jottacloud, kDrive, Koofr, MEGA, Nextcloud, OneDrive, pCloud, S3 and S3-compatible, Seafile, SFTP, Synology C2 / Drive, TeraBox, WebDAV, WordPress — credentials encrypted with DPAPI |
| Tools | Transfers with details, search and offline index, compare folders, sync, storage analysis, share links |
| Languages | English, German, Simplified and Traditional Chinese |
| Updates | Installed copies update in place from GitHub releases, checksum-verified |

## Install

Download from the [releases](https://github.com/rana-gmbh/FileFluss/releases) tagged
`win-v…`: **FileFluss-Setup-X.Y.Z-x64.exe** (or `-arm64`) installs per user, without
administrator rights, into `%LOCALAPPDATA%\Programs\FileFluss`, with a Start menu shortcut
and an uninstaller (Settings → Apps). The `-portable.zip` runs from any folder and is updated
by hand. Windows 10 2004 (19041) or later; the builds are self-contained, so no .NET runtime
is needed. `SHA256SUMS.txt` beside them lists every file's SHA-256.

Settings, accounts, the search index and logs live in `%LOCALAPPDATA%\FileFluss` — not in
the install folder — so neither an update nor uninstalling touches them.

FileFluss checks for a newer Windows release once a day (Settings → "Check for updates
automatically", the same switch as on macOS) and on Help → Check for Updates…. An installed
copy downloads the new setup, verifies it against the release's `SHA256SUMS.txt` and
relaunches updated; a portable copy is pointed at the release page.

## Build

```
dotnet build windows/FileFluss.sln
dotnet test windows/src/FileFluss.Core.Tests
dotnet run --project windows/src/FileFluss.App
```

Requires the .NET 10 SDK. Warnings are errors (`windows/Directory.Build.props`). There is no
Visual Studio requirement — `FileFluss.sln` opens in Visual Studio, but the CLI is
sufficient.

After changing a macOS `Localizable.strings` file (`FileFluss/Resources` or
`Packages/FileFlussCore/Sources/FileFlussCore/Resources`), regenerate the `.resx` files:

```
dotnet run --project windows/tools/StringsToResx
```

CI runs the same tool with `--check` and fails when the checked-in `.resx` files are stale.
`-- --missing` lists the keys the Windows sources look up that no catalogue has yet.

## Test

```
dotnet test windows/src/FileFluss.Core.Tests
```

xUnit, against `FileFluss.Core` only — models, every provider (over a scripted
`FakeHttpHandler`, never the network), the transfer engine, sync, compare, search, settings
and the update lookup. `.github/workflows/windows-ci.yml` builds and tests on
`windows-latest` for every change under `windows/` or to either `Localizable.strings`
location.

## Release

```
powershell -File windows/Packaging/build-release.ps1 -Version 1.0.0
```

builds both architectures (win-x64, win-arm64) into `windows/artifacts/release/`: the
installer (needs [Inno Setup 6](https://jrsoftware.org/isinfo.php); without it the script
warns and builds the rest), a portable zip per architecture, and `SHA256SUMS.txt`, which the
in-app updater requires — it installs nothing it cannot match against that file. The script
checks each publish for the parts that are easy to lose: `Assets\`, the de / zh-Hans / zh-Hant
satellite assemblies, SQLite's native `e_sqlite3.dll` and the stamped version. Set
`FILEFLUSS_SIGN_PFX` and `FILEFLUSS_SIGN_PASSWORD` to Authenticode-sign the executable and
installers; unsigned builds work, but SmartScreen warns on first run.

Releases are cut by tag; `.github/workflows/windows-release.yml` does the rest (tests, Inno
Setup, optional signing from the `WINDOWS_SIGNING_PFX_BASE64` / `WINDOWS_SIGNING_PASSWORD`
secrets, upload):

```
git tag win-v1.0.0 && git push origin win-v1.0.0
```

or create the release first:

```
gh release create win-v1.0.0 --title "FileFluss for Windows 1.0.0" --latest=false --notes "…"
```

**`--latest=false` is not optional.** The macOS app's Sparkle feed is
`releases/latest/download/appcast.xml`, so a Windows release marked "latest" would cut every
Mac off from updates. The workflow creates its releases with `--latest=false` and moves
"latest" back to the newest macOS release if it finds it on a Windows one. Windows tags are
always `win-vX.Y.Z`: a plain `v*` tag starts the macOS release instead, and the in-app
updater only ever looks at `win-v*` releases.

## Projects

| Project | Target | Role |
|---|---|---|
| `FileFluss.Core` | `net10.0` | Models, the cloud-provider contract and every provider, transfers, sync, compare, the search index, localization, settings, update lookup. Platform-neutral **on purpose** so it builds and unit-tests off Windows. |
| `FileFluss.Native` | `net10.0-windows` | Win32 and shell interop: icons and thumbnails, the Recycle Bin, DPAPI credential storage, drives, terminals. |
| `FileFluss.App` | `net10.0-windows10.0.19041.0` | WPF shell (assembly `FileFluss.exe`) — panels, sidebar, dialogs, settings, feature windows, in-app updates. |
| `FileFluss.Core.Tests` | `net10.0` | xUnit for Core. |
| `tools/StringsToResx` | | Generates the `.resx` files from the macOS string catalogues. |
| `Packaging` | | `build-release.ps1` and the Inno Setup script `FileFluss.iss`. |

Porting a provider: [docs/PROVIDER-PORTING.md](docs/PROVIDER-PORTING.md).
