# AI Usage WebView2 Portable

Small Windows widget for Claude and Codex usage.

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
