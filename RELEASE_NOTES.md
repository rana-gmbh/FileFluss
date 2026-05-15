# FileFluss 1.1.1

Fixes a critical credential-persistence bug in 1.1.

## What was broken in 1.1

After adding a cloud account in 1.1, closing the app, and reopening it, every account showed **"Cloud account not connected."** Re-adding worked for the duration of the session but didn't survive a relaunch either.

The cause: 1.1's `KeychainService` switched to the macOS data-protection keychain via `kSecUseDataProtectionKeychain: true`. On Developer ID-signed builds, `SecItemAdd` returned success but items did not actually persist across process launches. Save → close → reopen → load returned nothing.

## Fix in 1.1.1

`KeychainService` now uses the legacy login keychain — the same store 1.0.1 used and that has reliably persisted FileFluss credentials for the entire 1.0.x series. No code-signing or entitlement change required.

After upgrading from 1.1 you will need to **add your cloud accounts again, once** — any 1.1 saves are gone with the data-protection keychain. From there on, accounts stick across launches as they did in 1.0.1.

## Installation

### Homebrew

```bash
brew upgrade --cask filefluss
```

### Manual

Download `FileFluss-v1.1.1.dmg` below and drag FileFluss.app into your Applications folder.

## Requirements

- macOS 14.0 (Sonoma) or later
