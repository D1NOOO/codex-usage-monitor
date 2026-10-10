<p align="center">
  <img src="assets/logo.png" alt="Codex Rate Monitor logo" width="180">
</p>

# Codex Rate Monitor for Windows

[简体中文](README.zh-CN.md) · [繁體中文](README.zh-TW.md) · **English**

A small, native Windows tray utility that shows the current Codex 5-hour and
7-day usage windows next to ChatGPT desktop's Codex UI, plus available
rate-limit reset credits.

> [!IMPORTANT]
> Unofficial community project, not affiliated with or endorsed by OpenAI.
> The local `codex app-server` protocol may change between versions.

## Screenshots

The overlay supports 1-row and multi-row layouts. The reset-credit card in the
multi-row layout changes color as expiry approaches: normal 鈫?warning (鈮?
days) 鈫?danger tint (鈮? days), and hides itself when no credits are available.

![Multi-row overlay](docs/overlay-multirow-credits-zh-cn.png)

The 1-row layout lines up the 5-hour, 7-day, and reset-credit cards
horizontally, sized for a title bar.

![One-row overlay](docs/overlay-oneline-credits-zh-cn.png)

Hovering the tray icon shows a multi-line summary of usage and reset credits.

![Tray tooltip](docs/tray-tooltip-zh-cn.png)

Appearance settings offer a style preview: position, fonts, colors, opacity,
and the reset-credits (read-only) toggle.
The preview uses sample usage, reset times, and credit counts.
Usage panels offer Auto (recommended), Show all (5h + 7d), and Weekly usage only.
In Auto mode, a complete read confirming only weekly usage hides the
5-hour panel and reduces horizontal width or stacked height. Preview and tray summaries
follow the same available windows. Show all retains missing windows as "Not provided";
Weekly usage only always displays the 7-day panel.
Initial reads show a status message; refresh errors retain cached data and layout, and
incomplete notifications cannot collapse panels.
Selecting window attachment at the bottom-right defaults to multi-row. You can then
choose one row manually; saved row choices survive reopening settings and restarting.
The overlay follows Windows display scaling immediately; the appearance scale
is an additional multiplier. Preview renders at actual size when it fits and
shows the reduction percentage when space is limited.
The appearance scale defaults to the new 100%, equivalent to the former 85% size.
Adjust Appearance settings → Typography and size → Scale from 50% to 200%.
Fonts, spacing, and corners scale together; existing configurations retain their size.

![Appearance settings](docs/appearance-settings-reset-credits-zh-cn.png)

## Features

- Overlay with desktop-floating (topmost, draggable, click-through) and
  window-attach modes; 1-row / multi-row layouts; remaining or used display.
- Reset-credit badge: read-only display of available credits and earliest
  expiry 鈥?never redeems credits.
- Multi-line tray tooltip with usage and reset-credit summary.
- Periodic reads reuse a long-lived app-server; polling slows down while the
  desktop window is minimized (see "Traffic" below).
- Also: zh-CN / zh-TW / English UI, full appearance editor, start with
  Windows, background update checks with verified in-place updates.

## Requirements

- Windows 10/11, .NET Framework 4.8.
- A signed-in ChatGPT desktop app (legacy Codex App + Codex CLI still work).
  API-key login cannot show subscription usage windows.

## Install

1. Download `CodexRateMonitor-VERSION-windows-x64.zip` from
   [Releases](https://github.com/D1NOOO/codex-usage-monitor/releases) and
   optionally verify `SHA256SUMS.txt`.
2. Extract to a stable location and run `CodexRateMonitor.exe`. The app has no
   main window; look for its icon in the notification area (possibly under the
   `^` overflow). Right-click to configure, double-click for settings.

Release executables are unsigned; SmartScreen may show an unknown-publisher
warning. Download only from this repository's Releases page and verify.

## Traffic

The tool is designed to be gentle on your data plan (background:
[issue #5](https://github.com/D1NOOO/codex-usage-monitor/issues/5)):

| Scenario | Behavior |
|---|---|
| ChatGPT/Codex in the foreground | One lightweight read every `ForegroundRefreshSeconds` (default 30s) |
| Window visible in the background | One read every `RefreshSeconds` (default 60s) |
| Window minimized / tray-only | Cadence drops to `MinimizedRefreshSeconds` (default 300s) |
| Reset-credit query | One read-only request every `ResetCreditsSeconds` (default 30 min) |

The app-server process stays alive and is reused for the whole session;
periodic refreshes check the local account with `account/read` (`refreshToken: false`),
then send `account/rateLimits/read` and merge `account/rateLimits/updated` notifications
from the monitor's own connection. Returning to the foreground, restoring the
window, or waking the computer schedules one supplemental read after a one-second
debounce. Concurrent refreshes merge; reads time out after 30 seconds and failed
reads back off to at most five minutes. Identical quotas do not trigger restarts.
Tooltips show the last confirmed update time; account changes clear old data.
This reduces polling delay without guaranteeing synchronization with the desktop
GUI's separately cached data. The app-server may also make background requests, so total
process traffic can exceed the traffic from rate-limit reads alone.

If the app-server reports a revoked token, the monitor clears the stale usage
display immediately. Repeated failures allow one automatic app-server restart;
if sign-in remains invalid, the monitor stops automatic restarts and checks for
recovery no more often than every five minutes. After signing in through
ChatGPT/Codex, use the tray's "Refresh now" command to retry immediately.

## How it works

```mermaid
flowchart LR
    UI["Tray icon + click-through overlay"] --> Client["AppServerClient"]
    Client -->|"newline-delimited JSON over stdin/stdout"| Server["codex app-server"]
    Server --> Auth["ChatGPT/Codex-managed authentication"]
    Server --> API["OpenAI services"]
```

The monitor locates a Codex executable (desktop-bundled 鈫?npm install 鈫?PATH),
starts `codex.exe app-server`, sends `account/rateLimits/read`, renders
usage windows by duration (5 hours or 7 days), and merges
`account/rateLimits/updated` notifications. A weekly window in `primary` is still
identified as weekly usage.

OpenAI authentication is owned by ChatGPT/Codex. The one exception: when reset
credits are enabled, the monitor reads the access token from `~/.codex/auth.json`
locally and uses it only to query the read-only endpoint
`chatgpt.com/backend-api/wham/rate-limit-reset-credits` 鈥?the token lives in
memory for the duration of each request and is never redeemed, stored, or sent
anywhere else. Redacted diagnostic logs live under
`%LOCALAPPDATA%\CodexRateMonitor\logs` and never contain tokens or account
identifiers. Private vulnerabilities: [SECURITY.md](SECURITY.md).

Diagnostics are off by default. To investigate a mismatch, set `DiagnosticsEnabled`
to `true` in `settings.json` and restart the monitor. Logs retain seven days,
rotate at about 2 MiB per file, and share a 20 MiB disk budget. Expired logs are
cleaned even when diagnostics are disabled, while the monitor is running.
Refresh origins, elapsed times and notification dispositions are documented in
[usage refresh diagnostics](docs/usage-refresh.md).

## Configuration

Common `settings.json` fields (template in `config/settings.default.json`):

| Field | Description |
|---|---|
| `Language` | `auto` / `zh-CN` / `zh-TW` / `en` |
| `OverlayMode` | `desktop` (default, floating) / `attach` |
| `UsageDisplay` | `remaining` (default) / `used` |
| `UsagePanels` | `auto` (default, available windows) / `all` (show 5h + 7d) / `weekly` (weekly usage only) |
| `DisplayLines` | `1` / `2`; selecting bottom-right attachment defaults to `2`, with a manual override |
| `ForegroundRefreshSeconds` | 30 to `RefreshSeconds`, foreground interval (default 30) |
| `RefreshSeconds` | 30 to 900, interval while visible in the background (default 60) |
| `MinimizedRefreshSeconds` | 60鈥?600, cadence while minimized (default 300) |
| `ShowResetCredits` | Reset-credit badge toggle (default on) |
| `ResetCreditsSeconds` | 300鈥?6400, reset-credit query interval (default 1800) |
| `DiagnosticsEnabled` / `DiagnosticRetentionDays` | Diagnostics default off; retention defaults to 7 days (1–30), total budget 20 MiB |

Appearance fields (fonts, `#RRGGBB` colors, scale, opacity) are documented in
the default template and editable in the settings UI.

Overlay scale is 50–200%, with 100% using the compact size that was previously
85%. Existing unversioned settings are converted once in memory to preserve
their rendered size: old 85% becomes new 100%, and old 100% becomes about 117.65%.
The updater keeps existing `settings.json`; saving records `Style.ScaleBasisVersion: 2`
so subsequent loads do not convert again. Other preferences remain in place.

## Build and release

```powershell
git clone https://github.com/D1NOOO/codex-usage-monitor.git
cd codex-usage-monitor
.\scripts\build.ps1 -Package
```

Output goes to `artifacts/`. CI runs `scripts/verify.ps1` first
(localization keys, JSON/PowerShell syntax, privacy and credential scans).
To release, bump `version.txt`, push a matching tag, and
`.github/workflows/release.yml` builds and publishes with provenance.

## FAQ

### Codex executable not found / not signed in

Open or update ChatGPT desktop first. Standalone CLI users: check
`codex --version` and `codex app-server --help`, then run `codex doctor`.
If it reports API-key auth, sign in with the ChatGPT account flow 鈥?API keys
cannot return subscription usage windows.

### The overlay is missing

Bring the ChatGPT/Codex window to the foreground (attach mode only shows while
the window is visible), confirm the tray icon is running, then use
**Refresh now**.

### Overlay position drifted after a UI update

Placement is tailored to the current desktop layout. Open an issue with a
redacted screenshot if it needs adjusting.

## Known limitations

Windows only; depends on the experimental local app-server protocol; release
binaries are not code-signed.

## License

[MIT](LICENSE). Codex and OpenAI are trademarks of their respective owner.
This project is unofficial and uses no OpenAI branding assets.
