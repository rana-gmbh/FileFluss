# FileFluss 1.0.1

A small follow-up to the 1.0 release that adds update awareness and a refreshed About panel.

## What's new

### Update notifications

FileFluss now checks for new versions on GitHub.

- A daily background check shows a one-time popup whenever a newer version is available, with *Later* and *Open Release Page* buttons. Subsequent days won't bother you again about the same version.
- Manual check via **Apple menu → About FileFluss → Check for Updates** or **Help → Check for Updates…**.
- The check uses the public GitHub Releases API — no telemetry, no account, no signup.
- Defaults to on. Disable with `defaults write com.rana-gmbh.FileFluss automaticUpdateChecksEnabled -bool false`.

### Refreshed About panel

The standard About panel has been replaced with a custom window that shows:

- The new Light/Dark app icon
- Version + a *Release Notes ↗* link to the matching tag on GitHub
- Direct link to the GitHub repository
- Credits for the app icon (@JohnnyFireOne) and the bundled file-type icons
- The *Check for Updates* button described above

This also fixes encoding glitches in the previous HTML-based About panel (`Â©`, `â€”`).

### Help menu

Two new entries under **Help**:

- **Check for Updates…** — opens the About panel ready to check.
- **GitHub Repository** — opens https://github.com/rana-gmbh/filefluss in your browser.

## Installation

### Homebrew

```bash
brew upgrade --cask filefluss
```

### Manual

Download `FileFluss-v1.0.1.dmg` below and drag FileFluss.app into your Applications folder.

## Requirements

- macOS 14.0 (Sonoma) or later
