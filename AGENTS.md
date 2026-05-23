# Headroom — Agent Instructions

## Build and Commit

After any task that modifies source files (`src/*.cs`), always:

1. **Verify build**: run `.\build.ps1`
   - If it fails, fix the errors before committing
2. **Commit**: once the build succeeds, commit with a concise message in `type: summary` format
   - Examples: `fix: resolve null reference in tray icon`, `feat: add hotkey support`

For non-source changes (docs, config), skip the build but still commit.

---

## Operation Mode

Choose the mode based on **collision risk**, not task size.

### Lightweight mode

Use when **all** of the following apply:

- Single agent working alone
- Changes touch 1–2 files
- No overlap with: UI, auth, settings persistence, API clients, or refresh policy

**Process:**

- No task file needed
- No separate worktree needed
- Run `git status` and `tests/run-tests.ps1` before and after

**Files typically safe for lightweight:**

```
README.md
README.ja.md
docs/*
tests/*
src/UsageParsers.cs   (minor fix only)
src/RefreshPolicy.cs  (minor fix only)
```

If `UsageParsers.cs` or `RefreshPolicy.cs` changes behavior, add or update tests — lightweight mode still applies.

---

### Standard mode

Use when **any** of the following apply:

- Changes touch 3 or more files
- Adding a settings field
- Changes span both UI and logic
- Touching any of: parser, credential store, refresh policy, settings store

**Process:**

- Create a task file under `docs/ai/tasks/`
- Work in a single worktree
- Specify `Owner`, `Scope`, and `Out of scope` in the task file
- The other agent reviews after the task is done

**Files that require standard mode or above:**

```
src/SettingsForm.cs
src/UsageForm.Drawing.cs
src/UsageForm.cs
src/Models.cs
src/SettingsStore.cs
src/UsageClients.cs
src/OAuthFlows.cs
src/CredentialStores.cs
```

---

### Separation mode

Use when **any** of the following apply:

- Claude and Codex are working at the same time
- Two agents are advancing different tasks concurrently
- Large UI changes
- Parallel work across multiple domains (auth / API / settings / drawing)

**Process:**

- Each agent gets its own worktree and branch
- Task file is required for each task
- A human or one agent reviews before merge

```powershell
git worktree add ..\Headroom-codex -b codex/task-name
git worktree add ..\Headroom-claude -b claude/task-name
```

---

## Minimum Rules

1. If two agents are working at the same time, always use separate worktrees.
2. Any task touching `src/SettingsForm.cs`, `src/UsageForm.Drawing.cs`, or `src/UsageForm.cs` requires a task file.
3. Any task that adds a settings field uses standard mode or above.
4. Any task touching auth / API / credentials / refresh uses standard mode or above.
5. At the end of every task, report the output of `git diff --stat` and `tests/run-tests.ps1`.
