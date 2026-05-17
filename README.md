# AI Usage WebView2 Portable

Small Windows widget for Claude and Codex usage.

![AI Usage WebView2 Portable screenshot](docs/screenshot.png)

## What it shows

- Codex and Claude usage in one compact always-on-top window.
- `5時間` shows the short rolling usage window.
- `週` shows the weekly usage window.
- The percentage can be shown as remaining or used from settings.
- Reset text uses relative time for the short window and date/time for weekly resets.
- Yellow/red bars indicate configurable low-remaining thresholds.
- The right rail contains close, pin, settings, and resize controls.

## Start

Run `Start.bat`, or run `bin\AiUsageWebView2.exe` directly.

## Files to copy to another PC

Copy this whole folder:

- `Start.bat`
- `bin\AiUsageWebView2.exe`
- `bin\*.dll`
- `bin\settings.json`

The login profile is stored on each PC under `%LOCALAPPDATA%\AiUsageWebView2\WebView2Profile`.

## Build

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

The build script downloads the WebView2 NuGet package into `packages\` when needed.
