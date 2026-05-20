[**English**] · [日本語](README.ja.md)

# Headroom *— Claude & Codex quota monitor —*


[![Platform](https://img.shields.io/badge/Platform-Windows-0078D4)](https://github.com/tesuheee/headroom)
[![Language](https://img.shields.io/badge/Language-C%23-239120)](https://learn.microsoft.com/dotnet/csharp/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A compact Windows desktop widget that shows how much Claude and Codex quota headroom you have left.

## Features

- **Side-by-side monitoring** — Claude and Codex, both 5-hour and weekly quotas, in one floating widget
- **Flexible display** — per-service Remaining / Used switch, wide or tall layout, reset shown as countdown or clock time
- **Low-quota warnings** — each quota row turns yellow or red at configurable thresholds
- **Account controls** — log in or log out of Claude / Codex from the Settings dialog

## Getting Started

1. Download the latest versioned `Headroom-vX.Y.Z.zip` from [Releases](https://github.com/tesuheee/headroom/releases) and unzip anywhere.
2. Run `Headroom.exe`.
3. On first launch, click **Login** on each card.
   - **If Claude / Codex CLI is installed**, a terminal window opens for the CLI's login flow.
     - **Claude**: type `/login` and follow the browser sign-in flow.
     - **Codex**: the `codex login` browser flow starts automatically.
   - **If no CLI is installed**, your default browser opens the OAuth login page directly. After signing in, the tab shows "Login complete" and Headroom picks up the new credentials automatically.
   You can also manage sessions from **Settings → Account**.

## Screens

### Both services, horizontal (default)

![Overview](docs/images/01-overview.png)

### Single service

![Single service](docs/images/02-single-service.png)

Disable a service from **Settings → General** to compact down to one card.

### Vertical layout

![Vertical layout](docs/images/03-layout-vertical.png)

Switch between wide and tall layouts from **Settings → Layout**.

### Display modes

![Display modes](docs/images/04-display-modes.png)

Each service has its own **Remaining / Used** switch. Reset can be a countdown ("3h 53m left") or an absolute clock time ("5/25 0:59"), set independently for 5-hour and weekly. Different phrasings on Claude and Codex pages are normalized so the format stays consistent.

### Color thresholds

![Color thresholds](docs/images/05-color-thresholds.png)

Each quota row is colored independently: normal rows use the service color, warning rows turn yellow, and critical rows turn red. If a quota is exhausted, the affected card also shows a `Limit` badge.

## Buttons

![Side rail controls](docs/images/06-sidebar-guide.png)

| Side rail control | Action |
|-------------------|--------|
| × | Close |
| Pin | Toggle always on top |
| R / U | Toggle Rem / Used for visible services |
| 5h | Toggle 5-hour reset between countdown and clock time |
| Wk | Toggle weekly reset between countdown and clock time |
| ⚙ | Open settings |

Per-service buttons:

| Button | Action |
|--------|--------|
| ↻ | Refresh one service now |
| ⚡ | Boost one service — refresh every minute for 30 minutes |

## Settings

Open with the ⚙ icon on the side rail.

- **General** — language, always on top, enable/disable each service
- **Account** — login/logout controls for Claude and Codex
- **Layout** — arrangement, per-service remaining/used, per-quota reset format
- **Refresh** — normal interval (15 min default), Boost duration / interval (30 min / 1 min default)
- **Thresholds** — yellow at 50%, red at 30% (configurable)

## How it works

The app reads OAuth tokens from `%USERPROFILE%\.claude\.credentials.json` (Claude) and
`%USERPROFILE%\.codex\auth.json` (Codex), calls the respective usage APIs directly, and
renders a custom dark UI. When a CLI is installed, Headroom defers credential writes to it.
When no CLI is present, Headroom runs its own PKCE OAuth flow (system browser + localhost
callback) and writes credentials in a CLI-compatible format. Refresh tokens are used to keep
the access token alive without re-login. Settings are stored in
`%LOCALAPPDATA%\Headroom\settings.json`.

## Fixture mode

For UI verification without spending quota, start Headroom with a fixture folder:

```powershell
.\build.ps1 -DebugFixture
.\debug\Headroom.fixture.exe --fixture .\docs\fixtures\03-weekly-exhausted
```

The folder must contain `claude.json` and `codex.json` in the same shape as the live API
responses. Headroom watches those files and refreshes automatically when they change.

## Build from source

```powershell
.\build.ps1
```

Windows + .NET Framework 4 required (csc.exe path is hard-coded in `build.ps1`).

To create a release archive:

```powershell
.\build.ps1 -Version 2.0.0
```

The archive is written to `releases/Headroom-vX.Y.Z.zip`.
