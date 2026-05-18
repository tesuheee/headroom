[**English**] · [日本語](README.ja.md)

# AI Usage Monitor

A floating Windows desktop widget that reads the Claude and Codex web usage pages and keeps your remaining quota visible at a glance.

## Features

- Watch Claude and Codex usage side by side, in one always-on-top widget
- See both the 5-hour rolling quota and the weekly quota for each service
- Independently toggle between **Remaining** and **Used** views per service
- Show only the services you care about (Codex-only, Claude-only, or both)
- Switch between horizontal and vertical layouts with a single click
- Display reset as a **countdown** ("3h 53m left") or as an **absolute time** ("5/25 0:59"), independently for 5-hour and weekly
- Yellow/red threshold colors warn when remaining quota gets low
- One-click **Boost** for high-frequency refresh when you are close to a reset

## Getting Started

1. Download the latest `AiUsageWebView2.zip` from Releases, unzip anywhere.
2. Double-click `AiUsageWebView2.exe`.
3. On first run, click the **Login** button on each card and sign in to Claude / Codex through the embedded browser. After that, usage updates automatically.

> Requires the WebView2 Runtime (preinstalled on Windows 11 and recent Windows 10).

## Screens

### Both services, horizontal (default)

![Overview](docs/01-overview.png)

Claude and Codex each show their 5-hour and weekly quotas with a percentage, a progress bar, and a reset hint. The side rail on the right hosts the global controls.

### Single service

![Single service](docs/02-single-service.png)

If you only use one of the two, disable the other from Settings → General. The widget shrinks to a compact one-card layout.

### Vertical layout

![Vertical layout](docs/03-layout-vertical.png)

Stack the two cards vertically — handy for narrow side regions on the desktop. Use the layout toggle button on the side rail, or switch from Settings → Layout.

### Display modes & reset formats

![Display modes](docs/04-display-modes.png)

Each service has its own **Remaining / Used** switch. Reset time can be a relative countdown or an absolute clock time, configured separately for the 5-hour and weekly quotas. Original strings on Claude/Codex pages are parsed into a datetime, so the formatting is consistent regardless of how each provider phrases it.

## Buttons

| Button | Action |
|--------|--------|
| ⚡ | Boost — high-frequency refresh (1-minute interval for 30 minutes) |
| ↻ | Refresh now |
| × | Close |
| 📌 | Pin / unpin (always on top) |
| ⇆ | Toggle horizontal / vertical layout |
| ⚙ | Open settings |

Hover any button for a tooltip.

## Settings

Click the ⚙ icon on the right side rail.

### General
| Item | Default | Notes |
|------|---------|-------|
| Language | 日本語 | 日本語 / English |
| Always on top | Disabled | |
| Codex | Enabled | Show or hide this service |
| Claude | Enabled | Show or hide this service |

### Layout
| Item | Default | Notes |
|------|---------|-------|
| Arrangement | Wide | Wide (horizontal) / Tall (vertical) |
| Codex value | Remaining | Remaining % or Used % |
| Claude value | Remaining | Remaining % or Used % |
| 5h reset display | Time left | Time left / Clock time |
| Weekly reset display | Clock time | Time left / Clock time |

### Refresh
| Item | Default |
|------|---------|
| Normal interval (min) | 15 |
| Boost duration (min) | 30 |
| Boost interval (min) | 1 |

### Thresholds
| Item | Default | Notes |
|------|---------|-------|
| Yellow threshold (%) | 50 | Progress bar turns yellow when remaining ≤ this |
| Red threshold (%) | 30 | Progress bar turns red when remaining ≤ this |

## How it works

The app hosts two hidden WebView2 instances pointed at the Claude and Codex usage pages, scrapes the rendered text, parses out the percentages and reset times, and renders them with a custom dark-mode UI. Your login session lives inside the WebView2 user-data folder under `%LOCALAPPDATA%\AiUsageWebView2\`; the app does not transmit any credentials anywhere else.

## Build from source

```powershell
.\build.ps1
```

Requires Windows + .NET Framework 4 (csc.exe path is hard-coded in `build.ps1`). The script fetches the WebView2 NuGet package on first run.
