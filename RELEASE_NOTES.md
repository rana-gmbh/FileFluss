# FileFluss 0.10

## New: GMX Cloud integration

Connect your **GMX MediaCenter** account directly with your GMX email and password — FileFluss handles the WebDAV endpoint underneath so there's nothing to configure. Browse, upload, download, and sync GMX Cloud alongside every other connected provider.

## pCloud authentication — fixed

pCloud recently stopped issuing auth tokens to third-party clients via password login and simultaneously **disabled new OAuth app registrations** (the "My Apps" page reports *"Temporarily unavailable, please contact support team"* — a known regression discussed on Reddit for several months). Existing FileFluss users who signed out could no longer sign back in.

FileFluss 0.10 works around this:

- Email + password login is still attempted first, for the accounts where it still works.
- For everyone else, a new **Access Token** field accepts the `pcauth` browser cookie. The sign-in sheet now walks you through locating it in Chrome, Edge, Firefox, and Safari DevTools step by step.
- Once pasted, the token is stored in the macOS Keychain just like any other credential.

## Installation experience

Double-clicking the release DMG now opens a styled install window with the FileFluss icon on the left, an Applications shortcut on the right, and clear instructions across the top — drag and drop, no Finder navigation required.

## Other bug fixes

- Upload regressions in the generic transfer path have been corrected.

## Full Changelog

See the [commit history](https://github.com/rana-gmbh/filefluss/commits/main) for the complete list of changes.
