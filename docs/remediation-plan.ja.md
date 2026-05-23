# Headroom 改善計画

作成日: 2026-05-21
更新日: 2026-05-22

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

- `.\build.ps1` は警告なしで成功する。
- `.\build.ps1 -DebugFixture` は警告なしで成功する。
- `.\tests\run-tests.ps1` は成功する。
- `docs/fixtures` は追加済み。
- push / pull request 用 CI は追加済み。
- 作業ツリーは clean。
- `UsageParsers` と `DebugLog` は `UsageForm` から分離済み。
- 認証ファイルの読み取り/書き込みは JSON パーサを使うよう変更済み。ただし `CredentialStore` クラスとしてはまだ分離していない。

### 実施済みコミット

- `933574c docs: add remediation plan`
- `1ad75e6 fix: unify refresh backoff paths`
- `d127205 refactor: parse usage responses as json`
- `5100dd1 fix: preserve credential json fields`
- `4c3ae63 ci: build on push and pull requests`
- `f324963 chore: clean build warnings`
- `9c9a30a test: add fixture parser coverage`
- `1d36d0b refactor: read credentials as json`
- `ad2c46d chore: log settings failures`

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

## 残作業の詳細実行計画

ここから先は、以下の順番で進める。各項目は原則として 1 コミットにする。大きくなった場合は「テスト追加」「実装」「配線」「ドキュメント」を別コミットに分ける。

### R1: CredentialStore を分離する

目的:

- Claude / Codex の認証ファイル読み書き、token refresh 後の保存、既存フィールド保持を `UsageForm.*.cs` から外す。
- 認証ファイルの互換性をテストで固定する。

作業単位:

1. `CredentialSnapshot` 相当の小さなデータ型を追加する。
   - Claude: `AccessToken`, `RefreshToken`, `ExpiresAtMs`
   - Codex: `AccessToken`, `RefreshToken`, `IdToken`, `AccountId`, `ExpiresAtMs`
2. `ClaudeCredentialStore` を追加する。
   - `Read()`
   - `Write(snapshot, scopes)`
   - 既存の `claudeAiOauth` 以外の top-level key を保持する。
   - `claudeAiOauth` 内の未知 key も保持する。
3. `CodexCredentialStore` を追加する。
   - `Read()`
   - `ReadIdToken()`
   - `Write(snapshot)`
   - `auth_mode`, `tokens`, `last_refresh` 以外の top-level key を保持する。
   - `tokens` 内の未知 key を保持する。
4. `tests/CredentialStoreTests.cs` を追加する。
   - 既存未知フィールド保持。
   - token 更新。
   - missing file。
   - invalid JSON。
   - refresh token が空の場合の扱い。
5. `UsageForm.Claude.cs` / `UsageForm.Codex.cs` の `Read*Credentials` / `Write*Credentials` を store 呼び出しに置き換える。
6. `tests/run-tests.ps1` と CI に credential tests を含める。

想定コミット:

- `test: add credential store coverage`
- `refactor: extract credential stores`
- `refactor: wire credential stores into refresh flows`

完了条件:

- 認証ファイルの未知フィールド保持がテストで保証される。
- `UsageForm.Claude.cs` / `UsageForm.Codex.cs` に credential JSON のキー詳細が残らない。
- `.\build.ps1`, `.\build.ps1 -DebugFixture`, `.\tests\run-tests.ps1` が通る。

注意点:

- いきなり atomic write を混ぜない。まず既存挙動を保持した分離を行う。
- atomic write は R1 完了後の独立コミットで行う。

### R2: 認証ファイル書き込みを atomic にする

目的:

- token refresh 中や CLI との競合時に、壊れた JSON を残しにくくする。

作業単位:

1. `JsonFileStore` または `FileWrite` ヘルパーを追加する。
2. 同じディレクトリに一時ファイルを書き、可能なら replace、難しければ move で置き換える。
3. 書き込み失敗時に既存ファイルを残す。
4. 失敗時は `DebugLog` に出す。
5. credential tests に「書き込み結果が JSON として読める」ことを追加する。

想定コミット:

- `fix: write credentials atomically`

完了条件:

- refresh 失敗時に既存 credential を壊さない設計になっている。
- credential tests が通る。

### R3: RefreshPolicy を分離する

目的:

- 通常更新、boost、near reset、rate limit、手動更新の判断を `UsageForm` から切り出す。
- 429 待機の一貫性をテストで固定する。

作業単位:

1. `RefreshPolicy` を追加する。
2. `RefreshDecision` を追加する。
   - `ShouldRefresh`
   - `Due`
   - `Reason`
   - `NextTimerInterval`
3. `IsNearOrRecentReset`, `IsVeryNearReset`, `RefreshIntervalMinutes`, `RateLimitUntil` のうち、UI 依存しないものを移す。
4. `tests/RefreshPolicyTests.cs` を追加する。
   - 初回更新。
   - 通常 15 分。
   - boost 1 分。
   - reset 付近 15 秒。
   - reset ±1分 5 秒。
   - 429 待機中は更新しない。
   - 429 待機期限切れ後は更新する。
5. `UsageForm.MaybeRefreshAsync` と `RunScheduledRefreshAsync` を `RefreshPolicy` 経由にする。

想定コミット:

- `test: add refresh policy coverage`
- `refactor: extract refresh policy`

完了条件:

- 更新判断が単体テスト可能。
- `UsageForm.cs` の scheduler 周りが薄くなる。
- 429 バックオフの入口統一がテストで固定される。

### R4: UsageClient を分離する

目的:

- HTTP 呼び出し、token refresh、API エラー処理を `UsageForm` から外す。
- UI は「取得を依頼して結果を状態へ反映する」だけに寄せる。

作業単位:

1. `UsageFetchResult` を追加する。
   - `UsageData Data`
   - `string Status`
   - `DateTime? RateLimitedUntil`
   - `bool LoginRequired`
   - `string DebugLogName`
   - `string DebugLogText`
2. `IUsageClient` を追加する。
   - `Task<UsageFetchResult> FetchAsync(bool manual)`
3. `ClaudeUsageClient` を追加する。
   - credential store を使う。
   - parser を使う。
   - token refresh を含める。
   - 401/403/429/fetch_error を result で返す。
4. `CodexUsageClient` を追加する。
   - account id header を credential store から取得する。
   - token refresh を含める。
5. `UsageForm.RefreshClaudeViaApiAsync` / `RefreshCodexViaApiAsync` を薄くする。
6. `UsageForm` から API endpoint、OAuth token endpoint 以外の JSON 詳細を消す。

想定コミット:

- `refactor: introduce usage fetch result`
- `refactor: extract claude usage client`
- `refactor: extract codex usage client`
- `refactor: wire usage clients into form`

完了条件:

- `UsageForm.Claude.cs` / `UsageForm.Codex.cs` の refresh メソッドが状態反映中心になる。
- API レスポンス本文のパースは client/parser 側に閉じる。
- 既存 fixture / parser / refresh tests が通る。

注意点:

- OAuth browser login はここで無理に分離しない。まず API fetch と token refresh を切り出す。

### R5: OAuth flow を分離する

目的:

- PKCE、localhost listener、token exchange、CLI 起動を `UsageForm` から外す。

作業単位:

1. `OAuthPkce` ヘルパーを追加する。
   - verifier/challenge/state 生成。
2. `LocalOAuthCallbackListener` を追加する。
   - port listen。
   - callback wait。
   - state 検証。
   - HTML response。
3. `ClaudeLoginFlow` / `CodexLoginFlow` を追加する。
4. CLI 起動処理を `CliLoginLauncher` に分離する。
5. `OpenLoginAsync` を flow 呼び出しに寄せる。

想定コミット:

- `refactor: extract oauth callback listener`
- `refactor: extract browser login flows`
- `refactor: extract cli login launcher`

完了条件:

- `UsageForm.cs` に PKCE verifier/challenge 生成や HttpListener 詳細が残らない。
- ログイン成功後の状態反映は現状維持。

注意点:

- OAuth は実サービス依存が強いため、自動テストは helper の純粋関数と listener の小範囲に限定する。

### R6: SettingsStore を分離し、設定 JSON も JSON パーサ化する

目的:

- `WidgetSettings` が自分でファイル IO と Regex JSON パースを持つ状態を解消する。

作業単位:

1. `SettingsStore` を追加する。
2. `WidgetSettings.Load/Save` を store 経由にする、または static load/save を store へ移す。
3. 設定 JSON の読み取りを Regex から `Json` helper に置き換える。
4. `tests/SettingsStoreTests.cs` を追加する。
   - missing file は default 作成。
   - legacy settings migration。
   - invalid JSON は default + log。
   - unknown key は保持するか、保持しない方針を明記する。
5. 設定保存失敗ログを継続する。

想定コミット:

- `test: add settings store coverage`
- `refactor: extract settings store`

完了条件:

- 設定読み書きがテスト可能。
- 設定 JSON 読み取りに Regex が残らない。

### R7: `.csproj` 化と正式なテスト構成

目的:

- 現行 `csc.exe` 直呼びから、標準的な .NET/MSBuild 構成へ移行する。
- ただし `build.ps1` の入口と release zip 名は維持する。

作業単位:

1. `Headroom.csproj` を追加する。
   - WinForms executable。
   - .NET Framework を維持する場合は `net48` など現実的な target を検討する。
2. `Headroom.Tests.csproj` または既存 lightweight tests の正式化を行う。
3. `build.ps1` を `msbuild` 呼び出しへ寄せる。
4. GitHub Actions の CI を新構成に合わせる。
5. release workflow が従来通り `releases/Headroom-vX.Y.Z.zip` を作ることを確認する。

想定コミット:

- `build: add project file`
- `build: run builds through msbuild`
- `test: formalize test project`

完了条件:

- `.\build.ps1`
- `.\build.ps1 -DebugFixture`
- test command
- CI
- release workflow

上記がすべて新構成で通る。

注意点:

- `.csproj` 化は差分が大きくなるので、R1〜R6 の責務分離が済んでから行う。
- 先にやると「ビルド構成変更」と「挙動変更」が混ざるため避ける。

### R8: UI fixture の視覚確認導線を作る

目的:

- fixture mode があるだけでなく、主要状態を視覚的に確認できる運用にする。

作業単位:

1. `docs/fixtures/06-near-reset` を追加する。
2. `docs/fixtures/07-rate-limited` を追加できるか検討する。
   - 現状 fixture は JSON ファイルだけなので、rate limit は API response ではなく状態注入が必要。
   - 必要なら fixture 用 metadata を追加する。
3. `docs/fixture-verification.ja.md` を追加する。
   - 各 fixture の期待表示。
   - 起動コマンド。
   - スクリーンショット更新手順。
4. 可能なら Playwright 等ではなく、まず手動確認手順に留める。

想定コミット:

- `docs: add fixture verification guide`
- `test: add near-reset fixture`

完了条件:

- API を叩かずに主要 UI 状態を確認できる。
- docs と fixture のパスが一致する。

### R9: ドキュメントとアーキテクチャの最終同期

目的:

- 実装変更後の構成を README / architecture docs に反映する。

作業単位:

1. README / README.ja の「仕組み」を更新する。
2. `docs/architecture.html` の責務説明を更新する。
3. `docs/remediation-plan.ja.md` の完了状況を更新する。
4. 必要なら `docs/architecture-archive.html` は触らない。archive として残す。

想定コミット:

- `docs: sync architecture after refactor`

完了条件:

- README とコード構成が矛盾しない。
- 完了済み/未完了が plan 上で明確。

## 実行時のルール

- 1 コミットは 1 目的にする。
- 各コミット前に最低限 `.\build.ps1` を通す。
- parser / credential / refresh / settings を触ったコミットでは `.\tests\run-tests.ps1` も通す。
- fixture binary に影響する変更では `.\build.ps1 -DebugFixture` も通す。
- UI 見た目の変更と認証/API/refresh ロジック変更を混ぜない。
- `.csproj` 化は R1〜R6 が終わるまで保留する。
- 途中で計画と実装がずれた場合、先にこの計画を更新してから実装する。

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
