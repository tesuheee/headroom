# Headroom 改善計画

作成日: 2026-05-21

## 目的

Headroom を「動いている小さな WinForms ツール」から、外部 API 変更・OAuth 状態・レート制限・UI 状態を安全に扱える保守可能なアプリにする。

現状は機能の方向性は明確で、Claude Code と Codex の使用量を軽量に見られる価値もある。一方で、外部サービスの非公開または変更されやすい API、CLI 互換の認証ファイル、手描き UI、タイマー更新が同じ `UsageForm` 周辺に密集している。そのため、壊れたときに原因が見えにくく、修正が別の挙動を壊しやすい。

この計画では、まず安全網を作り、挙動の不整合を直し、その後に構造を分ける。

## 現状整理

### アプリの役割

- Windows 専用の WinForms ウィジェット。
- `%USERPROFILE%\.claude\.credentials.json` と `%USERPROFILE%\.codex\auth.json` を読む。
- Claude / Codex の使用量 API を直接呼び出す。
- 5時間枠、週間枠、残量/使用量、リセット時刻、ログイン状態、429 待機状態を表示する。
- 設定は `%LOCALAPPDATA%\Headroom\settings.json` に保存する。

### 主要ファイル

- `src/Program.cs`: 起動、fixture 引数の解釈。
- `src/UsageForm.cs`: メインフォーム、タイマー、更新制御、OAuth 共通処理、ファイル監視、ログ出力。
- `src/UsageForm.Claude.cs`: Claude 認証ファイル、Claude API、Claude OAuth。
- `src/UsageForm.Codex.cs`: Codex 認証ファイル、Codex API、Codex OAuth。
- `src/UsageForm.Drawing.cs`: ウィジェット描画。
- `src/UsageForm.Input.cs`: マウス/キー入力と設定ダイアログ起動。
- `src/SettingsForm.cs`: 設定 UI。
- `src/Models.cs`: `ServiceState`, `UsageData`, `WidgetSettings`。
- `build.ps1`: .NET Framework 4 の `csc.exe` を直接呼ぶビルド。

### 現在確認できている状態

- `.\build.ps1` は成功する。
- ビルド警告が 8 件ある。
- `docs/fixtures` が存在しないが README は存在する前提で案内している。
- `src/UsageForm.cs` と `src/UsageForm.Claude.cs` に未コミット変更がある。

## 問題一覧

### P0: 429 バックオフを迂回する更新経路がある

`RefreshServiceAsync` には `RateLimitedUntil` を見て更新を止める処理がある。しかし `RefreshAllAsync` は `RefreshCodexViaApiAsync` / `RefreshClaudeViaApiAsync` を直接呼んでいる。

影響:

- 起動時更新、F5、または一括更新が 429 待機を無視する可能性がある。
- README の「429 ではバックオフする」という説明と実装が一致しない。
- 外部 API に対して不要な再試行を行う可能性がある。

あるべき姿:

- すべての更新入口が同じポリシーを通る。
- 手動更新で待機を無視するかどうかを明示的に設計する。
- 初期表示、F5、個別更新、認証ファイル変更、fixture 更新で挙動が説明可能である。

推奨方針:

- `RefreshAllAsync` も `RefreshServiceAsync` 経由にする。
- 「ユーザーが明示的に更新した場合に 429 待機を解除する」仕様にするなら、引数名を `force` などに分け、UI 表示も合わせる。
- まずは安全側として、手動更新でも `RateLimitedUntil` 中は更新しない。

受け入れ条件:

- `RateLimitedUntil` 中のサービスは、起動時/F5/一括更新/ファイル監視更新でも API を呼ばない。
- `RateLimitedUntil` が過ぎると通常更新に戻る。
- `rate_limited` 表示が維持される。

### P0: テストとフィクスチャがない

外部 API の形状に依存しているにもかかわらず、パーサや状態遷移のテストがない。README にある fixture 導線も実体がない。

影響:

- API の JSON 形状変更に気づきにくい。
- 429、ログイン切れ、リセット直前などの状態を手動でしか確認できない。
- UI 変更と API パース変更の影響範囲が見えない。

あるべき姿:

- API レスポンス fixture をリポジトリに持つ。
- パーサは fixture ベースで検証できる。
- scheduler / rate limit / settings の非 UI ロジックを自動テストできる。
- UI の最低限の手動確認手順が docs にある。

推奨方針:

- `docs/fixtures/` を追加する。
- 最初は以下の fixture を用意する。
  - `01-ok`: Claude / Codex とも通常値。
  - `02-login-required`: 認証なし。
  - `03-five-hour-exhausted`: 5時間枠が 0%。
  - `04-weekly-exhausted`: 週間枠が 0%。
  - `05-no-data`: API 形状不明または値なし。
  - `06-near-reset`: リセット直前。
- README の fixture 例を実在するパスに合わせる。
- parser テストから始め、UI screenshot テストは後続に回す。

受け入れ条件:

- README に書かれた fixture コマンドが実行可能。
- fixture mode で各状態が再現できる。
- parser の期待値がテストで固定されている。

### P1: JSON を正規表現で読んでいる

認証ファイル、設定ファイル、API レスポンスを正規表現で読んでいる。単純な形なら動くが、ネスト、エスケープ、順序変更、同名キーの追加に弱い。

影響:

- API の一部がネストしただけで `no_data` になる可能性がある。
- JSON 文字列にエスケープが入ると正しく読めない可能性がある。
- 設定破損時の原因が見えにくい。

あるべき姿:

- JSON は JSON パーサで読む。
- パース失敗時は「どのファイル/どのキーが読めなかったか」をログに残す。
- API パーサはサービスごとに独立し、fixture で検証される。

推奨方針:

- .NET Framework 4 互換を維持するなら `System.Web.Script.Serialization.JavaScriptSerializer` を検討する。
- 参照追加を避けたい場合は `DataContractJsonSerializer` を検討する。
- 外部 NuGet 依存を入れるなら、ビルド方式を `.csproj` に寄せるタイミングで Newtonsoft.Json も候補にする。
- まずは API パーサだけ JSON 化し、その後に設定/認証ファイルへ広げる。

受け入れ条件:

- Claude / Codex の parser が正規表現に依存しない。
- fixture のキー順序を変えてもテストが通る。
- 予期しない JSON でも例外で落ちず、`no_data` または `fetch_error` として扱う。

### P1: 認証ファイルを丸ごと再生成している

Browser OAuth や token refresh 後に、CLI 互換の認証ファイルを独自の JSON 文字列で上書きしている。

影響:

- CLI 側が追加した未知の項目を消す可能性がある。
- ファイル形式が変わったときに互換性を壊しやすい。
- refresh と CLI の同時書き込み時に競合しやすい。

あるべき姿:

- 既存 JSON を読み込み、必要な token 項目だけ更新する。
- 未知の項目は保持する。
- 書き込みは可能なら atomic に行う。
- 書き込み直後の FileSystemWatcher 通知で不要な再更新ループが起きない。

推奨方針:

- `CredentialStore` をサービス別に切る。
- 読み取り/書き込み/refresh 更新を `UsageForm` から分離する。
- 書き込みは一時ファイルへ書いて replace する方式を検討する。
- まずは既存項目保持のテストを作る。

受け入れ条件:

- 未知の top-level / nested key が refresh 後も残る。
- token 値と期限だけが更新される。
- 書き込み失敗時に既存ファイルを壊さない。

### P1: CI がリリース時のみ

現在の GitHub Actions はタグ push 時のリリース用 workflow のみ。

影響:

- 通常 push / PR でビルド破壊に気づけない。
- fixture や parser テストを追加しても自動検証されない。

あるべき姿:

- push / PR で build と test が走る。
- tag push では従来通り release zip を作る。
- release workflow と CI workflow が同じビルド手順を使う。

推奨方針:

- `.github/workflows/ci.yml` を追加する。
- 最初は `.\build.ps1` を実行するだけでもよい。
- テスト基盤追加後に test step を追加する。

受け入れ条件:

- push / PR で build が通る。
- テスト追加後は CI でテストが実行される。
- release workflow は壊さない。

### P2: `UsageForm` に責務が集中している

メインフォームが、描画以外にもスケジューリング、API 呼び出し、OAuth、ファイル監視、デバッグログ、状態管理を持っている。

影響:

- 変更の影響範囲が読みづらい。
- テストしづらい。
- UI イベントとネットワーク処理が絡み、状態遷移のバグが入りやすい。

あるべき姿:

- `UsageForm` は UI とイベント接続に寄せる。
- API、認証、パース、更新判断、ログは小さなクラスへ分ける。
- 非 UI ロジックは WinForms なしでテストできる。

推奨分割:

- `Services/IUsageClient.cs`: 使用量取得の抽象。
- `Services/ClaudeUsageClient.cs`: Claude API 呼び出し。
- `Services/CodexUsageClient.cs`: Codex API 呼び出し。
- `Parsing/ClaudeUsageParser.cs`: Claude JSON パース。
- `Parsing/CodexUsageParser.cs`: Codex JSON パース。
- `Auth/ClaudeCredentialStore.cs`: Claude 認証ファイル読み書き。
- `Auth/CodexCredentialStore.cs`: Codex 認証ファイル読み書き。
- `Refresh/RefreshPolicy.cs`: 通常/boost/near reset/429 の更新判定。
- `Diagnostics/DebugLog.cs`: ログ出力。

受け入れ条件:

- `UsageForm` から API レスポンス JSON の詳細が消えている。
- parser / refresh policy / credential store が単体テスト可能。
- UI の見た目と操作は維持される。

### P2: ビルド警告と未使用フィールド

現状のビルドでは未使用または未代入の警告が出ている。

影響:

- 本当に危険な警告が埋もれる。
- 未完了機能と古い残骸の区別がつかない。

あるべき姿:

- 通常ビルドで警告が 0 件。
- まだ使う予定のフィールドは TODO ではなく具体的な issue/コメントで管理する。

推奨方針:

- `UsageData` の未代入フィールドを、今後使うなら parser で埋める。不要なら削除する。
- `UsageForm` の resize 系未使用フィールドを削除する。
- warning-as-error は、CI が安定してから検討する。

受け入れ条件:

- `.\build.ps1` の warning が 0 件。
- 削除したフィールドに依存する UI 状態がない。

### P2: 設定の失敗が見えない

`WidgetSettings.Load` / `Save` が例外を握りつぶしている。

影響:

- 設定ファイル破損、権限、書き込み失敗の原因がわからない。
- ユーザーから見ると設定が保存されないだけに見える。

あるべき姿:

- 設定読み込み失敗はデフォルトにフォールバックしつつログに残す。
- 設定保存失敗はログに残す。
- 設定ファイルが破損している場合、破損ファイルを退避するか、少なくとも原因を表示/ログ化する。

受け入れ条件:

- 設定 JSON が壊れていても起動する。
- 失敗理由が `%USERPROFILE%\.headroom` 配下のログで追える。

### P3: ビルド方式が古く拡張しにくい

`build.ps1` が `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` を直接呼んでいる。

影響:

- テストプロジェクトや JSON ライブラリ導入が面倒。
- IDE/CI の標準的な扱いから外れる。
- 依存関係が増えたときに破綻しやすい。

あるべき姿:

- `.csproj` でビルドできる。
- 既存 `build.ps1` は互換入口として残す。
- release zip 生成は従来と同じ成果物名を維持する。

推奨方針:

- まずは現行 `build.ps1` を壊さず CI を追加する。
- 次に `Headroom.csproj` を追加する。
- 必要なら `Headroom.Core` と `Headroom.Tests` に分ける。

受け入れ条件:

- `.\build.ps1` と `dotnet/msbuild` のどちらでもビルドできる。
- release workflow の成果物名が変わらない。

## 実装フェーズ

### Phase 1: 挙動の安全化と検証導線

目的:

- 既存 UI を大きく変えず、レート制限と fixture の不整合を直す。

作業:

- `RefreshAllAsync` を `RefreshServiceAsync` 経由に修正する。
- `RateLimitedUntil` 中の手動更新ポリシーを明文化する。
- `docs/fixtures` を追加する。
- README / README.ja の fixture コマンドを実在パスに合わせる。
- `.\build.ps1 -DebugFixture` で fixture mode を確認する。

完了条件:

- ビルド成功。
- README の fixture 起動例が実行可能。
- 429 待機中に一括更新が API を呼ばない。

### Phase 2: Parser と CredentialStore の安全化

目的:

- 外部 API と認証ファイル形式の変化に耐える。

作業:

- JSON parser 方針を決める。
- Claude / Codex parser を正規表現から JSON パースへ置き換える。
- fixture ベースの parser テストを追加する。
- 認証ファイル読み書きを CredentialStore へ分離する。
- 既存未知フィールドを保持する書き込み方式にする。

完了条件:

- API JSON のキー順序や不要フィールド追加で parser が壊れない。
- 認証ファイルの未知フィールドが refresh 後も残る。
- parser / credential store のテストが通る。

### Phase 3: CI と警告整理

目的:

- 変更を継続的に検証できる状態にする。

作業:

- `.github/workflows/ci.yml` を追加する。
- push / PR で build を走らせる。
- テスト基盤が入ったら CI に test を追加する。
- ビルド警告を整理する。

完了条件:

- CI で build/test が実行される。
- 通常ビルドの警告が 0 件。
- release workflow は従来通り zip を作れる。

### Phase 4: 責務分離

目的:

- 今後の機能追加や API 変更対応を小さな差分で行えるようにする。

作業:

- `RefreshPolicy` を分離する。
- `UsageClient` を Claude / Codex ごとに分離する。
- `CredentialStore` を UI から切り離す。
- `DebugLog` を共通化する。
- `UsageForm` は状態表示とイベント接続に寄せる。

完了条件:

- 非 UI ロジックの主要部分がテスト可能。
- `UsageForm` に API endpoint や JSON key の詳細が残らない。
- UI 挙動が変わっていない。

### Phase 5: ビルド構成の近代化

目的:

- テスト、依存管理、IDE 体験を改善する。

作業:

- `.csproj` を追加する。
- `build.ps1` を `.csproj` ベースに寄せるか、互換 wrapper として残す。
- テストプロジェクトを正式化する。
- JSON ライブラリ導入が必要ならこのタイミングで判断する。

完了条件:

- 既存の release zip 生成が維持される。
- CI とローカルで同じ手順を使える。
- 新規開発者が標準的な .NET ツールで開ける。

## 判断が必要な点

### 手動更新は 429 待機を無視できるべきか

候補:

- 安全側: 手動更新でも 429 待機中は API を呼ばない。
- 操作優先: 手動更新だけは「強制更新」として待機を解除できる。

推奨:

- まず安全側にする。外部 API の利用制限を尊重し、不要な連打を避ける。
- 将来必要なら、UI に「待機中。あと X 分」の表示を出し、強制更新は隠し操作または確認付きにする。

### JSON ライブラリを入れるか

候補:

- 標準ライブラリのみ。
- Newtonsoft.Json を導入。
- .NET バージョンを上げて System.Text.Json を使う。

推奨:

- 短期は標準ライブラリで進める。
- `.csproj` 化後に Newtonsoft.Json または target framework 更新を検討する。

### `.csproj` 化を先にやるか

候補:

- 先に `.csproj` 化してテスト基盤を整える。
- 現行 `build.ps1` を維持して最小修正から入る。

推奨:

- 先に最小修正と fixture を入れる。
- `.csproj` 化は Phase 5 で行う。先にやると差分が大きくなり、挙動修正のレビューが難しくなる。

## 最初の実装単位

最初の PR またはコミットでやる範囲:

1. `RefreshAllAsync` の更新経路統一。
2. `docs/fixtures` の最小セット追加。
3. README / README.ja の fixture パス修正。
4. `.\build.ps1` と `.\build.ps1 -DebugFixture` の確認。

この単位は UI 構造や parser の大改修を含めない。理由は、最初にレート制限と検証導線だけを確実に安定させるため。

## 以後の進め方

- 各 Phase は小さなコミットに分ける。
- 外部 API の挙動に関わる変更では fixture を必ず追加または更新する。
- UI 見た目の変更と API/認証処理の変更は同じコミットに混ぜない。
- 認証ファイルや設定ファイルの書き込み変更では、既存ファイルを破壊しないテストを先に置く。
- docs の説明と実体がずれたら、コード変更と同時に修正する。

## 完了の定義

- `.\build.ps1` が警告なしで成功する。
- fixture mode が README 通りに使える。
- parser / credential / refresh policy のテストが CI で通る。
- 429 バックオフがすべての更新入口で一貫する。
- 認証ファイル更新で未知フィールドを保持する。
- `UsageForm` から API JSON の詳細と credential 書き込み詳細が分離されている。
- 通常 push / PR で build/test が走る。
