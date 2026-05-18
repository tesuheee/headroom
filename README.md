[**English**] · [日本語](README.ja.md)

# AI Usage Monitor

A floating Windows desktop widget that shows your remaining Claude and Codex quotas at a glance.

## Features

- **Side-by-side monitoring** — Claude and Codex, both 5-hour and weekly quotas, in one always-on-top widget
- **Flexible display** — per-service remaining/used switch, horizontal or vertical layout, reset shown as countdown or clock time
- **Low-quota warnings** — progress bar turns yellow then red as remaining quota drops past configurable thresholds

## Getting Started

1. Download the latest `AiUsageWebView2.zip` from Releases and unzip anywhere.
2. Run `AiUsageWebView2.exe`.
3. On first launch, click **Login** on each card and sign in to Claude / Codex through the embedded browser. Sessions persist locally; subsequent launches fetch usage automatically.

> Requires the WebView2 Runtime (preinstalled on Windows 11 and recent Windows 10).

## Screens

### Both services, horizontal (default)

![Overview](docs/01-overview.png)

### Single service

![Single service](docs/02-single-service.png)

Disable a service from **Settings → General** to compact down to one card.

### Vertical layout

![Vertical layout](docs/03-layout-vertical.png)

Toggle from the side rail, or via **Settings → Layout**.

### Display modes

![Display modes](docs/04-display-modes.png)

Each service has its own **Remaining / Used** switch. Reset can be a countdown ("3h 53m left") or an absolute clock time ("5/25 0:59"), set independently for 5-hour and weekly. Different phrasings on Claude and Codex pages are normalized so the format stays consistent.

## Buttons

| Button | Action |
|--------|--------|
| ↻ | Refresh now |
| ⚡ | Boost — refresh every minute for 30 minutes |
| × | Close |
| 📌 | Pin / unpin (always on top) |
| ⇆ | Toggle horizontal / vertical |
| ⚙ | Open settings |

## Settings

Open with the ⚙ icon on the side rail.

- **General** — language, always on top, enable/disable each service
- **Layout** — arrangement, per-service remaining/used, per-quota reset format
- **Refresh** — normal interval (15 min default), Boost duration / interval (30 min / 1 min default)
- **Thresholds** — yellow at 50%, red at 30% (configurable)

## How it works

The app hosts two hidden WebView2 instances pointed at the Claude and Codex usage pages, parses the rendered text, and renders a custom dark UI. Login sessions live in the WebView2 user-data folder under `%LOCALAPPDATA%\AiUsageWebView2\`; credentials are not sent anywhere else.

## Build from source

```powershell
.\build.ps1
```

Windows + .NET Framework 4 required (csc.exe path is hard-coded in `build.ps1`). The WebView2 NuGet package is fetched on first build.
