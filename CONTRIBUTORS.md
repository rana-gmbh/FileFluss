# Contributors

FileFluss is written by Robert Rudolph at Rana GmbH. These people made it better.

## Code

- **[@knoxville68](https://github.com/knoxville68)** — Synology Drive with DSM two-factor authentication ([#66](https://github.com/rana-gmbh/filefluss/issues/66), [#67](https://github.com/rana-gmbh/filefluss/pull/67)): found that the session from the OTP login was thrown away, requested and stored a DSM device token so later logins don't ask for a new code, and tested it against a real DS920+ on DSM 7.4.1.

## Reports

Bug reports that led to a fix are credited in the release notes of the version that fixed them. If you reported something and your name is missing anywhere you'd like it, say so in an issue.

## Contributing

Pull requests are welcome. A few things that make them easy to accept:

- Say what you tested and on what. FileFluss talks to twenty-odd services and there is no test rig for most of them, so "tested against DSM 7.4.1" is worth more than any amount of code review.
- Say which parts you couldn't test. An untested path that is marked as untested is fine; one that is presented as working is not.
- If an AI assistant wrote part of it, say so. It doesn't count against the contribution — it changes what gets looked at twice.
- Keep a change to one subject. A provider fix and a refactor in the same PR take three times as long to review.
