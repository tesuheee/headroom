# AI Usage WebView2 Portable

Claude と Codex の使用量を、小さい常時表示ウィンドウで見るための Windows アプリです。
Microsoft Edge WebView2 で各サービスの使用量ページを読み取り、コンパクトな表示にまとめます。

![AI Usage WebView2 Portable screenshot](docs/screenshot.png)

インタラクティブなアーキテクチャ図: [docs/architecture.html](docs/architecture.html) をブラウザで開くと全体像を確認できます。

## 表示内容

- `Codex` と `Claude` の使用量を横並びで表示
- `5時間`（短期利用枠）と `週`（週次利用枠）の2行表示
- `残量表示` / `使用量表示` を設定で切り替え可能
- 5時間枠: `リセットまで 4時間25分`（残り時間）
- 週次枠: `リセット 5/24 9:49`（日付と時刻）
- 残量に応じてバーと数値の色が自動変化（白→黄→赤）
- 右側のサイドレールで閉じる・最前面固定・設定・リサイズ

## 起動

`Start.bat` を実行してください。直接 `bin\AiUsageWebView2.exe` でも動きます。

初回はログインが必要です。使用量が取得できない場合は、アプリ内の `ログイン` ボタンから Claude / Codex にログインしてください。

## 仕組み

**「Edge（WebView2）を非表示ブラウザとして使い、使用量ページをスクレイピングして、Windows Forms の小さなウィンドウに GDI+ でリッチ描画する」** アプリです。

### システム構成

```
Program.cs
├── UsageForm      : メインウィンドウ（スクレイピング・描画・スケジュール）
├── SettingsForm   : 設定ダイアログ（フォント・色・間隔など）
├── ServiceState   : サービス別ランタイム状態（Claude / Codex）
├── UsageData      : 使用量データ + Used⇔Remaining 相互変換
└── WidgetSettings : JSON 設定の読み書き
```

### データ取得フロー

1. **非表示ブラウザ起動** — WebView2 インスタンスを Claude 用・Codex 用に1つずつ作成（1×1px、画面外配置）。ログイン Cookie は `%LOCALAPPDATA%\AiUsageWebView2\WebView2Profile` に保存され、一度ログインすれば以降は自動認証
2. **ナビゲート** — キャッシュ回避のためランダムパラメータを付加して使用量ページに遷移。`NavigationCompleted` イベントで読み込み完了を検知（最大30秒待機）
3. **安定化ポーリング** — SPA の動的レンダリングに対応するため、1.2秒 × 最大10回ポーリング。「100文字以上 & 前回と一致」で完了判定
4. **DOM テキスト取得** — `document.body.innerText` を JavaScript 経由で実行し、レンダリング後のテキストを取得
5. **正規表現パース** — テキストを行分割 → セクション見出しを特定 → 範囲内から % とリセット時刻を抽出

対象 URL:
- Claude: `https://claude.ai/settings/usage`
- Codex: `https://chatgpt.com/codex/cloud/settings/analytics#usage`

**パースの違い**: Claude は「使用 %」を返し、Codex は「残り %」を返す。`UsageData` の計算プロパティが両方向変換し、表示モードに合わせて統一的に描画します。

## 更新スケジュール

3段階の自動更新で、状況に応じて更新頻度が変わります。

```
[通常: 5分] ──────────────────── [ファイナル: 1分] ── [Boost: 1分]
                                    ↑ リセットまで15分切ったら自動発動    ↑ ユーザーが手動ON
```

| モード | 間隔 | 発動条件 |
|--------|------|---------|
| 通常 | 5分 | 常時 |
| ファイナルウィンドウ | 1分 | 5h リセットまで15分以内（自動） |
| ブーストモード | 1分 | ユーザーが手動 ON（30分間） |

ブーストはカード右上のトグルスイッチで ON/OFF。残り時間はトグル横に表示されます。10秒ごとの `schedulerTimer` が更新タイミングを判定します。

## UI デザイン

### カード

- グラデーション背景（上→下）+ 半透明ハイライトボーダー + 角丸12px
- サービス名の左にアクセントカラーのドットインジケーター（Claude: 青、Codex: 緑）
- 状態に応じて背景色が変化:

| 状態 | 背景 | 備考 |
|------|------|------|
| Normal | ダークグレー | 上端に薄い白ハイライト |
| Stale（古いデータ） | 暗い黄系 | "古い" バッジ表示 |
| Exhausted（上限到達） | 暗い赤系 | アクセントが赤に変化・"上限" バッジ |

### プログレスバー（4層描画）

1. **トラック背景** — 暗い溝 + インセットボーダー
2. **グロー** — アクセントカラー 12% 透過で浮遊感
3. **グラデーションフィル** — アクセントの明るい版 → 本来色
4. **トップハイライト** — 上半分に白 18% を重ねて艶を表現

### その他

- 数値は残量に応じて白→黄→赤に自動変色
- トグルスイッチは ON 時グラデーション + ノブに影
- 更新中は Unicode 四分円文字 `◜◝◞◟` を 250ms 回転
- リサイズグリップはドットパターン
- フォントは全体を Segoe UI に統一

## 設定

歯車アイコンから設定画面を開けます。2カラム横長レイアウト（820×420）で、変更はライブプレビューされます。キャンセル時は元に戻ります。

| 左カラム（更新設定・一般） | 右カラム（表示設定・しきい値） |
|---|---|
| 通常更新間隔 | Codex / Claude の残量/使用量表示 |
| ブースト更新の時間と間隔 | ラベル・パーセント・リセットの文字サイズ |
| リセット直前の自動高頻度更新 | 黄色・赤に変わる残量しきい値と色 |
| 表示言語（日本語 / English） | |
| 最前面固定 | |

### デフォルト設定値

| 設定 | キー | デフォルト |
|------|------|-----------|
| 通常更新間隔 | `normalIntervalMinutes` | 5分 |
| ブースト時間 | `boostDurationMinutes` | 30分 |
| ブースト更新間隔 | `boostIntervalMinutes` | 1分 |
| 直前更新開始 | `finalRefreshWindowMinutes` | リセット15分前 |
| 直前更新間隔 | `finalRefreshIntervalMinutes` | 1分 |
| 黄色しきい値 | `warningRemainingPercent` | 残量50%以下 |
| 赤しきい値 | `criticalRemainingPercent` | 残量30%以下 |
| 表示モード | `claudeShowUsed` / `codexShowUsed` | 残量表示 |

## 別PCへ移す

このフォルダごとコピーしてください。レジストリは使っていません。最低限必要なのは以下です。

- `Start.bat`
- `bin\AiUsageWebView2.exe`
- `bin\*.dll`
- `bin\settings.json`

コピー先PCにも Microsoft Edge WebView2 Runtime が必要です（Windows 10以降はほぼプリインストール済み）。

## ビルド

Visual Studio は不要です。Windows 付属の `csc.exe`（.NET Framework 4.x）で直接コンパイルします。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

初回は NuGet から WebView2 パッケージを `packages\` にダウンロードします。

## デバッグ

取得テキストとエラーログは以下に保存されます。ページの HTML 構造が変わって解析できなくなった場合はこれらを確認してください。

| ファイル | 内容 |
|---------|------|
| `~\.ai-usage-widget\claude-webview2.txt` | Claude から取得した生テキスト |
| `~\.ai-usage-widget\codex-webview2.txt` | Codex から取得した生テキスト |
| `~\.ai-usage-widget\*-error.txt` | エラー発生時の詳細 |

## 技術スタック

| 項目 | 内容 |
|------|------|
| 言語 | C# (.NET Framework 4.6.2+) |
| UI | Windows Forms + GDI+ カスタム描画 |
| ブラウザエンジン | Microsoft Edge WebView2 |
| ビルド | PowerShell + csc.exe |
| 設定 | JSON（正規表現パース） |
| 依存関係 | WebView2 NuGet パッケージのみ |
