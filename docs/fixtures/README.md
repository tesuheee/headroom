# Headroom fixtures

Fixture mode lets Headroom render quota states from local JSON instead of calling the live APIs.

Run from the repository root:

```powershell
.\build.ps1 -DebugFixture
.\debug\Headroom.fixture.exe --fixture .\docs\fixtures\03-weekly-exhausted
```

While fixture mode is active, editing `claude.json` or `codex.json` in the selected folder refreshes the UI automatically.

## Scenarios

| Folder | Purpose |
|--------|---------|
| `01-normal` | 5h and weekly quotas both have room left |
| `02-5h-exhausted` | 5h quota is exhausted but weekly quota remains |
| `03-weekly-exhausted` | weekly quota is exhausted; the 5h row should be muted/locked |
| `04-both-exhausted` | both quotas are exhausted |
| `05-warning-threshold` | remaining quota is below the warning threshold |
| `06-critical-threshold` | remaining quota is below the critical threshold |
| `07-login-required` | login-required state without live credentials |
