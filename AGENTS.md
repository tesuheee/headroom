# Headroom — Agent Instructions

## Build and Commit

After any task that modifies source files (`src/*.cs`), always:

1. **Verify build**: run `.\build.ps1`
   - If it fails, fix the errors before committing
2. **Commit**: once the build succeeds, commit with a concise message in `type: summary` format
   - Examples: `fix: resolve null reference in tray icon`, `feat: add hotkey support`

For non-source changes (docs, config), skip the build but still commit.

---

## Task Start Protocol

Before writing any code, do the following:

1. **List the files you expect to touch** based on the task description.
2. **Select the mode** using the rules below:
   - Any file matches the standard-mode list -> standard mode
   - Two agents will work at the same time -> separation mode
   - Otherwise -> lightweight mode
3. **Announce your decision** before doing anything else.
   - Example: `Mode: standard - touches src/SettingsForm.cs`
4. **Set up for the mode**:
   - Lightweight: proceed directly
   - Standard: create `docs/ai/tasks/<task-name>.md` with `Owner`, `Scope`, `Out of scope` filled in
   - Separation: create or switch to the correct worktree when possible; if the environment prevents that, output the exact worktree commands and wait for the user to run them

Do not begin implementation until the setup for your chosen mode is complete.

If the user does not specify a mode, classify the task automatically. Do not ask the user to choose a mode unless the classification is ambiguous and choosing the wrong mode would create meaningful risk.

### Task Request Resolution

When the user says a task should be processed, handled, reviewed, or continued without naming a specific file:

1. Look for task files under `docs/ai/tasks/`.
2. If exactly one task is actionable, use it.
3. If multiple tasks are actionable, list the candidates and ask the user which one to use.
4. If no task file exists but the request includes enough detail to proceed, create a new task file and continue in the appropriate mode.
5. If the request does not include enough detail to identify the task safely, ask one concise clarification question.

Actionable task files are those not marked as `Done`, `Closed`, or `Cancelled`.

### Task File Minimum Fields

Use this shape when creating a task file:

```markdown
# Task Title

Owner:
Mode: Auto
Status: Ready

## Goal

## Scope

## Out of scope

## Test plan

## Handoff
```

At the end of Standard or Separation mode work, include a short handoff block for the other agent. The handoff must reference the task file and PR/branch instead of repeating the full plan.

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
