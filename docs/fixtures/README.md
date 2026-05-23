# Headroom Fixtures

These fixtures are used with `Headroom.fixture.exe --fixture <folder>` to verify parser and UI states without calling the live APIs.

Each scenario contains:

- `claude.json`: response shaped like Claude usage API output.
- `codex.json`: response shaped like Codex usage API output.

Scenarios:

- `01-ok`: both services have normal usage values.
- `02-five-hour-exhausted`: the 5-hour window is exhausted.
- `03-weekly-exhausted`: the weekly window is exhausted.
- `04-login-required`: both services show login required.
- `05-no-data`: responses are intentionally missing usage values.
