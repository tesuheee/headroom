# Headroom — Claude Code Instructions

## ビルドとコミット

ソースコード（`src/` 以下の `.cs` ファイル）を変更したタスクの最後に、必ず以下を実行してください。

1. **ビルド確認**: `.\build.ps1` を実行する
   - 失敗した場合はコミットせず、エラーを修正してから再ビルドする
2. **コミット**: ビルド成功後、変更内容を説明する簡潔なメッセージでコミットする
   - コミットメッセージは英語で `type: summary` 形式（例: `fix: resolve null reference in tray icon`）

ソースコード以外（ドキュメント、設定ファイル等）のみの変更はビルド不要ですが、コミットは行ってください。

---

## 運用モード

タスクの「大きさ」ではなく「**衝突リスク**」でモードを選んでください。

### 軽量モード

以下を**すべて**満たす場合に使用：

- エージェント1つで単独作業
- 変更が1〜2ファイル
- UI・認証・設定永続化・APIクライアント・refresh policy にまたがらない

**運用：**

- タスクファイル不要
- 別worktree不要
- 作業前後に `git status` と `tests/run-tests.ps1` を実行

**軽量モードで十分なファイル（目安）：**

```
README.md
README.ja.md
docs/*
tests/*
src/UsageParsers.cs   （小修正のみ）
src/RefreshPolicy.cs  （小修正のみ）
```

`UsageParsers.cs` や `RefreshPolicy.cs` で挙動変更を行う場合はテスト追加が必須。軽量モードのままで可。

---

### 標準モード

以下の**いずれか**に該当する場合に使用：

- 変更が3ファイル以上
- 設定項目の追加
- UIとロジックの両方に触る
- parser・credential store・refresh policy・settings store のいずれかを触る

**運用：**

- `docs/ai/tasks/` にタスクファイルを作成
- 1つのworktreeで作業
- タスクファイルに `Owner`・`Scope`・`Out of scope` を明記
- 作業完了後にもう片方のエージェントがレビュー

**標準モード以上が必要なファイル：**

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

### 分離モード

以下の**いずれか**に該当する場合に使用：

- ClaudeとCodexが同時に作業する
- 同じ時間帯に別タスクを並行で進める
- 大きなUI変更
- 認証・API・設定・描画など複数領域を並行で触る

**運用：**

- エージェントごとに別worktreeと別ブランチを用意
- 各タスクにタスクファイルが必須
- merge前に人間またはもう片方のエージェントがレビュー

```powershell
git worktree add ..\Headroom-codex -b codex/task-name
git worktree add ..\Headroom-claude -b claude/task-name
```

---

## 最小ルール

1. 2つのエージェントが同時に作業するなら必ず別worktreeを使う。
2. `src/SettingsForm.cs`・`src/UsageForm.Drawing.cs`・`src/UsageForm.cs` を触る作業はタスクファイル必須。
3. 設定項目を追加する作業は標準モード以上。
4. 認証・API・credential・refreshを触る作業は標準モード以上。
5. 作業終了時は `git diff --stat` と `tests/run-tests.ps1` の結果を報告する。
