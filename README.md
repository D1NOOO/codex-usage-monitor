<p align="center">
  <img src="assets/logo.png" alt="Codex Rate Monitor logo" width="180">
</p>

# Codex Rate Monitor for Windows

[简体中文](README.zh-CN.md) · [繁體中文](README.zh-TW.md) · **English**

A small, native Windows tray utility that displays the current Codex
5-hour and 7-day usage windows next to the Codex app.

> [!IMPORTANT]
> This is an unofficial community project. It is not affiliated with,
> endorsed by, or supported by OpenAI. The local `codex app-server` protocol
> may change between Codex versions.

![Appearance settings](docs/appearance-settings-zh-cn.png)

## Features

- Shows remaining percentage by default, or used percentage when selected.
- Progress length and warning/danger colors follow the selected percentage mode.
- Two overlay positions:
  - bottom-left, beside the profile area;
  - centered in the top title bar.
- Lives in the Windows notification area without a console window.
- Click-through overlay: it does not steal focus or block Codex controls.
- Appearance editor with live preview:
  - font family and sizes;
  - scale, opacity, and corner radius;
  - background, text, progress, warning, and danger colors;
  - light and dark presets.
- Simplified Chinese, Traditional Chinese, and English.
  Language can follow Windows automatically or be selected manually.
- Optional start-with-Windows toggle.
- No direct access to Codex credential files.

## Requirements

- Windows 10 or Windows 11.
- .NET Framework 4.8.
- Codex App and a Codex CLI version that supports:

  ```powershell
  codex app-server
  ```

- A signed-in Codex account with rate-limit data available.

## Install

1. Open the repository's **Releases** page.
2. Download `CodexRateMonitor-VERSION-windows-x64.zip`.
3. Verify `SHA256SUMS.txt` if desired.
4. Extract the complete folder to a stable location.
5. Double-click `CodexRateMonitor.exe`.

The application has no main window. Look for its information icon in the
Windows notification area; Windows may initially place it under the `^`
overflow menu.

Release executables are currently unsigned. Windows SmartScreen may show an
unknown-publisher warning. Always download from this repository's Releases
page and verify the checksum or GitHub artifact attestation.

## Use

Right-click the notification-area icon:

| Menu item | Behavior |
|---|---|
| Refresh now | Reads the latest rate-limit snapshot. |
| Reload style | Reloads `settings.json` from disk. |
| Appearance settings | Opens the visual editor and live preview. |
| Top title bar | Uses the compact horizontal overlay. |
| Bottom-left | Uses the larger two-row overlay beside the profile area. |
| Start with Windows | Adds/removes a current-user startup registry value. |
| Exit | Stops the monitor and the app-server process it launched. |

Double-click the tray icon to open Appearance settings.

### Language

Open **Appearance settings → Language**:

- Auto (system)
- 简体中文
- 繁體中文
- English

After saving, the tray menu, settings window, overlay labels, status messages,
and date format use the selected language. Reopen Appearance settings to see
the newly selected language throughout that window.

## How it works

```mermaid
flowchart LR
    UI["Tray icon + click-through overlay"] --> Client["AppServerClient"]
    Client -->|"newline-delimited JSON over stdin/stdout"| Server["codex app-server"]
    Server --> Auth["Codex-managed authentication"]
    Server --> API["OpenAI services"]
```

The monitor locates the native Codex CLI executable and starts:

```text
codex.exe app-server --stdio
```

It sends an initialization request, the initialized notification, then:

```json
{"method":"account/rateLimits/read","id":11}
```

The response contains rate-limit windows with fields such as:

- `usedPercent`
- `windowDurationMins`
- `resetsAt`

The monitor renders `primary` as the 5-hour window and `secondary` as the
7-day window. It also accepts sparse
`account/rateLimits/updated` notifications and merges them into the last
snapshot.

The monitor never implements OpenAI authentication. Codex itself owns login,
token refresh, and network communication.

## Privacy and security

### The application does

- communicate only with a child `codex app-server` over redirected standard
  input/output;
- retain current usage values in memory for display;
- store visual preferences in `settings.json`;
- optionally write:

  ```text
  HKCU\Software\Microsoft\Windows\CurrentVersion\Run
  ```

### The application does not

- open, parse, copy, upload, or print `auth.json`;
- store access tokens, account identifiers, or usage history;
- write application logs;
- include telemetry or analytics;
- require an OpenAI API key;
- send usage data to a developer-controlled server.

Do not attach `auth.json`, tokens, private account information, or unredacted
desktop screenshots to issues.

See [SECURITY.md](SECURITY.md) for private vulnerability reporting.
Maintainers should also review [PUBLISHING.md](PUBLISHING.md) before making
the repository public.

## Configuration

`settings.json` is created from `config/settings.default.json` in release
packages. It stores display preferences only.

Important fields:

| Field | Values |
|---|---|
| `Language` | `auto`, `zh-CN`, `zh-TW`, `en` |
| `Position` | `bottom-left`, `top` |
| `UsageDisplay` | `remaining` (default), `used` |
| `RefreshSeconds` | 30–900 |
| `Style.Scale` | 0.75–1.50 |
| `Style.Opacity` | 0.50–1.00 |
| `Style.FontSize` | 10–22 |
| `Style.ResetFontSize` | 9–18 |

Colors use `#RRGGBB` or `#RRGGBBAA`.

Runtime `settings.json` is ignored by Git. Only the privacy-safe default
template is committed.

## Build from source

Open PowerShell on Windows:

```powershell
git clone https://github.com/D1NOOO/codex-usage-monitor.git
cd codex-usage-monitor
.\scripts\build.ps1 -Package
```

Output:

```text
artifacts/
├── CodexRateMonitor/
│   ├── CodexRateMonitor.exe
│   ├── settings.json
│   ├── style-examples/
│   └── SHA256SUMS.txt
├── CodexRateMonitor-VERSION-windows-x64.zip
└── SHA256SUMS.txt
```

The build uses the .NET Framework 4.8 compiler included with Windows/Visual
Studio. It does not download NuGet dependencies.

Before compiling, CI runs `scripts/verify.ps1` to check localization
completeness, JSON/PowerShell syntax, accidental runtime settings, personal
paths, and common credential formats.

## Automated Releases

GitHub Actions performs both CI builds and tagged Releases.

1. Update `version.txt`.
2. Commit the change.
3. Create and push a matching semantic-version tag:

   ```powershell
   git tag v2.2.0
   git push origin main
   git push origin v2.2.0
   ```

4. `.github/workflows/release.yml`:
   - verifies the tag matches `version.txt`;
   - builds on `windows-latest`;
   - creates the ZIP and `SHA256SUMS.txt`;
   - generates a GitHub artifact provenance attestation;
   - creates the GitHub Release with generated notes.

The workflow uses the repository-scoped `GITHUB_TOKEN`; no personal access
token is required. The release job receives only `contents: write`,
`id-token: write`, and `attestations: write`.

Official actions are pinned to full commit SHAs. Dependabot checks for action
updates weekly.

To verify provenance after publishing:

```powershell
gh attestation verify CodexRateMonitor-VERSION-windows-x64.zip `
  --repo D1NOOO/codex-usage-monitor
```

## Project layout

```text
src/                    WinForms application and localization tables
assets/                 Application icon
config/                 Privacy-safe default settings
style-examples/         Optional appearance presets
scripts/build.ps1       Reproducible local/CI build
.github/workflows/      CI and Release automation
SECURITY.md             Private vulnerability reporting policy
CONTRIBUTING.md         Contribution guide
```

## Troubleshooting

### `Native Codex CLI not found`

Open a new PowerShell window and check:

```powershell
codex --version
codex app-server --help
```

Update/install the Codex CLI if `app-server` is unavailable.

### `Not signed in`

Open Codex App or Codex CLI and sign in normally. The monitor does not handle
credentials itself.

### The overlay is missing

- Bring the Codex window to the foreground.
- Check the tray icon is running.
- Select a position again from the tray menu.
- Use **Refresh now**.

### Codex UI changed

Overlay placement depends on the Codex window geometry. A future Codex UI
update may require adjusted offsets. Open a privacy-safe issue with a tightly
cropped screenshot that contains no personal content.

## Known limitations

- Windows only.
- Depends on an experimental/local Codex app-server protocol.
- Overlay positioning is tailored to the current Codex desktop layout.
- Release binaries are not code-signed.
- Rate-limit availability and semantics are controlled by Codex/OpenAI.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Keep every visible string in all three
localization tables and never commit credentials, personal paths, runtime
settings, or private screenshots.

## License

[MIT](LICENSE)

Codex and OpenAI are trademarks of their respective owner. This project is
unofficial and uses no OpenAI branding assets.
