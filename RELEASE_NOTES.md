# FileFluss 1.3.1 — SFTP diagnostic build

**This is a special diagnostic pre-release for the "SFTP folder appears empty" issue ([#31](https://github.com/rana-gmbh/FileFluss/issues/31)).** It is identical to 1.3 except that it records detailed SFTP diagnostics into the Support Log so we can see exactly what your server returns. It does not change any behaviour and is safe to use day-to-day.

If your SFTP folders show up empty in FileFluss, please help us pin down the cause:

## How to capture the log (about 1 minute)

1. Install this build (drag FileFluss into Applications, replacing your current copy). Your accounts and settings carry over.
2. In FileFluss, open the menu **Help → Support Log** and click **Support Log** to start a 60-second recording (a banner appears at the top).
3. While it records, click your **SFTP account** in the sidebar and open the folder that shows up empty. Try clicking into a sub-folder too.
4. When the 60 seconds are up, a **Save** dialog appears — save the `.log` file.
5. Attach that file to a comment on issue **#31** (or email it). It contains the raw directory listing your server sends; it does **not** contain your password or SSH key.

That listing is the missing piece — it tells us the exact format your (newer) SFTP server uses so we can parse it correctly.

## Requirements

- macOS 14.0 (Sonoma) or later

---

*Regular users: stay on **1.3** (the latest stable release). This build is only for diagnosing the SFTP issue.*
