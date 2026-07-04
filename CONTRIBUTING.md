# Contributing

Contributions are welcome.

1. Fork the repository and create a focused branch.
2. Keep credentials, personal paths, logs, screenshots with private content,
   and runtime `settings.json` out of commits.
3. Run `./scripts/build.ps1 -Package` on Windows.
4. Verify Simplified Chinese, Traditional Chinese, and English when changing
   visible text.
5. Open a pull request describing the behavior change and verification.

## Localization

All user-facing strings belong in `src/Localization.cs`. Every key must exist
in all three tables: `zh-CN`, `zh-TW`, and `en`.

## Pull request scope

Prefer small changes. Avoid unrelated formatting or generated binaries in
source pull requests. Release binaries are produced by GitHub Actions.
