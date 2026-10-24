# FileFluss 1.5.2

A maintenance release, and most of it came from reports. Thank you for every one of them — several of these were bugs I had put there myself and would not have found alone.

If you transfer large files, or edit files on a cloud account with another app, this release matters to you.

## Large transfers run to the end

A customer's 1.5 GB upload to Nextcloud stopped at about half, every time, while the same file through Finder was fine.

Every provider in the app was setting a **wall-clock cap on the whole request** — five to thirty minutes, depending on the service — in the belief that it was an inactivity timeout. It isn't. A transfer too big for your connection to carry inside that window died in the middle no matter how well it was going, and seventeen providers had one. Uploads and downloads alike, on almost every service.

That cap is gone. What remains is the guard that is actually worth having: a transfer goes on as long as bytes are moving, and fails only when it has genuinely gone silent.

Two more things on the same path:

- **A 3.7 GB upload that reached 100% and then failed** was waiting for the server. Once the last byte is sent, the far end still has to finish with several gigabytes, and thirty seconds was not enough. It now waits five minutes for that answer.
- **A large WebDAV upload no longer loads the file into memory first.** Nextcloud and generic WebDAV hand the file to the system and let it read as it sends, so the upload costs the same memory whatever the file's size.

Worth saying plainly: a single request of several gigabytes is still a lot to ask of a PHP server. This buys real headroom, not immunity. Chunked uploads would be the robust answer and are not in this release.

**The support log no longer stops after sixty seconds.** The same customer could not record his failing upload at all, because the recording ended minutes before the failure did. It now runs until you stop it, with an hour's backstop in case it's forgotten.

## Editing a cloud file in another app

A user reported Pages losing its changes to a file on a WebDAV account, while TextEdit kept them. Two separate faults, both ours.

**A mounted account now opens the file where it really is.** His account was mounted in Finder the whole time, but a double-click in a FileFluss panel never looked at the mount — it downloaded a copy, opened that, and his saves went into a file on this Mac that nothing uploads. A mounted account now opens through the volume, so the save goes back to the server, from Pages or anything else.

**And FileFluss no longer deletes a copy that holds your edits.** This is the worse half. The next open compared the local copy against the server's and, on any mismatch, deleted it and downloaded again — which for someone who had edited the file was FileFluss deleting the only copy of their work. An edited copy is now set aside with the time it was written, and you're told where it went and offered *Show in Finder*.

Being straightforward about the limit: for an account that is **not** mounted, FileFluss still does not upload changes you make in another app. Mounting the account is the way to edit in place today. Preserving the file instead of destroying it is the fix here; uploading it back is the next stage.

## How much room a transfer needs on this Mac

Asked for by a reader, who pointed out that a big cloud-to-cloud transfer is an easy place to forget about a nearly full Mac SSD. He was right that nothing said so — every byte of a cloud-to-cloud copy crosses a local disk on its way, and the pre-flight only ever looked at the destination account.

The transfer preview and the sync planner now show both figures, and they're calculated per transfer shape: cloud to cloud needs the whole transfer at once, cloud to local needs only the largest single item — nothing at all when it lands on the same volume it staged on — and local to cloud needs nothing. When space is short, the warning says *which* disk, because freeing space on your Mac and freeing space in an account are not the same errand.

A cloud-to-cloud transfer that cannot fit now stops before it starts, naming the disk and the setting, instead of failing half way with a disk-full error. Two things turned up while tracing this: such transfers were staging into the system temp folder rather than the folder you chose in Settings → Storage — which is exactly what moving the cache to an external drive is for — and the pre-flight is off by default, so the reported case would have warned nobody. Both fixed.

## Fixes

- **SFTP moves and copies now happen on the server.** Dragging a file to another folder on the same server downloaded it and uploaded it again — the cost of a full round trip to achieve a rename. FileFluss now asks the server to do it. Copying needs OpenSSH 9.0 or later; a server without it is asked once and remembered, so a folder of a thousand files doesn't pay a failed round trip a thousand times. Thanks to @paull27 for the report and the analysis. (#62)
- **WebDAV no longer hides one of your folders.** A Storage Box with two folders showed one. A listing returns the folder itself alongside its children, and FileFluss removed it by position rather than by name — so on a server that doesn't name its own entry, it removed a real folder instead. It is now matched by path, and if nothing matches, nothing is removed. Nextcloud had the same line, one server-side change away from the same bug. Thanks to @mrflory. (#64)
- **Synology Drive accounts with 2FA survive a restart and an edit.** The one-time code was sent only with the first login, so every later one was refused. Found, diagnosed, patched and tested against a real DS920+ by @knoxville68 — see the credit below. Editing such an account dropped the device token and asked for a code again; it's now carried through when the server and username are unchanged. Codes typed or pasted with a space ("123 456") are accepted in both sheets.
- **A failed search index says so instead of reporting success.** An SMB share indexed, 179,000 files appeared in the sidebar, and the database was empty — no error anywhere, and the index gone after a relaunch. Index writes were discarding every database result, including the commit, so a write that never happened looked identical to one that did. Writes now fail loudly and roll back, and the count shown is read back from the index rather than tallied while walking: the sidebar says what is actually searchable.
- **Indexing a large share no longer holds it all in memory.** A 179,000-file walk kept every entry for the eight minutes it took, then wrote them in one transaction. Rows are now written as they're found, into a staging area that search and browsing can't see, and swapped in at the end. Until that succeeds your previous index is intact — and if it fails, it still is.
- **A Trash refusal now explains itself.** macOS refused to let FileFluss move three files to the Trash for want of permission, and the app said only "Failed to trash". It now recognises that refusal, names the setting that fixes it, offers to open System Settings, and says a restart is needed afterwards.
- **A sidebar drop that moves files refreshes both panels.** The destination refreshed; the panel the files came from went on listing files that were no longer there.

## Privacy policy

Raised publicly, and fairly: the policy on the website says nothing about user files or application data, which is what an app needs one for. There is now one written from what the code does — every claim in it is checkable in the repository — and **Help → Privacy Policy** opens it.

The short version: no analytics or telemetry of any kind, the only request the app makes on its own is checking for updates, credentials live in the Keychain, the search index holds names and sizes but no file contents, the support log is off by default and never transmitted, and the Finder mount listens on 127.0.0.1 only.

## German and Chinese are complete again

123 strings were missing from the translation files, so German, Simplified and Traditional Chinese users saw English in the transfer preview, the space warnings, the storage analysis and the GoPro import. All four languages now carry the same 810 strings, and a test keeps them that way.

## Thank you

**@knoxville68** found the Synology 2FA bug, worked out that the session established by the one-time-code login was being thrown away, wrote the patch and tested it against real hardware — the part nobody here could have done. Contributors are now credited in `CONTRIBUTORS.md`, which also says what makes a pull request easy to accept.

## Install

```bash
brew trust rana-gmbh/filefluss && brew install --cask rana-gmbh/filefluss/filefluss
```

Already have FileFluss? `brew upgrade --cask filefluss`, or let the app update itself.
