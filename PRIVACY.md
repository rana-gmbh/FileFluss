# FileFluss Privacy Policy

**Applies to:** the FileFluss application for macOS, all versions.
**Last updated:** 6 October 2026.

The website www.filefluss.de has its own, separate privacy policy. This one covers the app.

## In short

FileFluss has no servers. Rana GmbH receives no data from the app: no account, no sign-up, no analytics, no telemetry, no crash reports. Your files stay on your Mac and on the services you choose to connect, and nowhere else.

## Your files

FileFluss reads and writes the files you point it at — folders on your Mac, drives you attach, and the cloud accounts or servers you connect.

File contents travel only between your Mac and the service that file belongs to. They are never sent to us, and never to a third party.

Copying between two cloud accounts stages the file through a cache folder on your Mac and uploads it from there. It does not pass through any server of ours — we have none.

## What FileFluss stores on your Mac

| What | Where | Contents |
|------|-------|----------|
| Credentials and OAuth tokens | macOS Keychain, service `com.rana-gmbh.FileFluss` | Passwords, keys and tokens for the accounts you connect. Never written to disk in plain text, and never sent anywhere except the service they belong to. |
| Search index | `~/Library/Application Support/FileFluss/search_index.db` | File and folder **names, paths, sizes and dates**, plus a provider-supplied checksum for cloud entries. **No file contents.** |
| Cached downloads | The system temporary folder, or the folder you choose in Settings → Storage | Copies of files you opened or transferred, so reopening is instant. |
| Preferences | `com.rana-gmbh.FileFluss` user defaults | Favourites, panel and window state, and each account's display name, provider type and root path. No passwords. |
| Support log | In memory; written to a file only where you save it | File and cloud operations — paths, names, error messages. |

All of it stays on your Mac. You can delete the search index from Settings → Index Status, clear the cache from Settings → Storage, and remove an account's credentials by removing the account.

The support log is **off by default**. It records only while you start it from File → Support Log, and nothing is transmitted — if you send one to us for a bug report, that is you choosing to, and you can read the file first.

## What leaves your Mac

1. **The services you connect.** Requests go to that provider and no one else: file contents, names, folder listings, and storage quota, authenticated with your credentials. The same applies to a NAS, an SFTP or WebDAV server, an S3 endpoint or a camera you point FileFluss at — the address is the one you entered.
2. **Share links.** When you create one, FileFluss asks that provider's API to make the file shareable. It then fetches the resulting link once, without credentials, to check that a stranger can actually open it. That request goes to the provider.
3. **Update checks.** FileFluss checks `github.com` for an update feed, and downloads the update from there when you accept one. GitHub sees your IP address and the request's user agent. No other service is contacted, and nothing about your files or accounts is included. Switch it off in Settings → General → "Check for updates automatically".
4. **Links you click.** The homepage, the GitHub repository, a provider's sign-in page: these open in your browser, and what happens there is ordinary web browsing.

## What FileFluss never does

- No user account, and no registration.
- No analytics, usage statistics, telemetry or crash reporting.
- No advertising identifiers, and no profiling.
- No third-party SDKs other than [Sparkle](https://sparkle-project.org), which is used only for the update check described above.

## Mounting an account in Finder

Mounting a cloud account runs a small WebDAV server on your Mac so Finder can see it. It is bound to `127.0.0.1` and reachable only from your own Mac; nothing is exposed to your network or the internet.

## Permissions FileFluss asks for

- **Files and folders**, including Full Disk Access if you want to browse everything — so the app can do its job. This is between you and macOS; the access is not reported anywhere.
- **Keychain**, to store the credentials of the accounts you connect.
- **Network**, to reach the services you connect and to check for updates.

## Children

FileFluss is a file manager for general use and is not directed at children. It collects nothing about anyone.

## Changes to this policy

The current version lives at <https://github.com/rana-gmbh/filefluss/blob/main/PRIVACY.md> and carries the date above. Material changes will be noted in the release notes of the version that introduces them.

## Contact

Rana GmbH — see the imprint at www.filefluss.de for postal address and contact details. Questions about this policy, or about what the app does with a particular piece of data, are welcome as a [GitHub issue](https://github.com/rana-gmbh/filefluss/issues).
