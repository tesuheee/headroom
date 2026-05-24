# Task: Fix Rendering Quality

Owner: Claude
Mode: Standard
Status: In Progress

## Goal

1. アンチエイリアスの問題（ギザギザ）: TransparencyKey + GDI+ アンチエイリアスの組み合わせを UpdateLayeredWindow に切り替えてピクセル単位の透明度を実現する
2. 設定画面のちらつき: SettingsForm に WS_EX_COMPOSITED を適用して二重バッファリングを有効にする

## Scope

- `src/UsageForm.Drawing.cs`: OnPaint を PaintContent(Graphics g) に分離、Color.Black → Color.Transparent
- `src/UsageForm.cs`: TransparencyKey 削除、WS_EX_LAYERED + UpdateLayeredWindow 追加、WndProc 追加
- `src/SettingsForm.cs`: WS_EX_COMPOSITED を CreateParams に追加

## Out of scope

- UI デザインの変更
- 機能変更

## Test plan

- [ ] ウィジェットを白いデスクトップ背景の前に置いてカードの角がスムーズか確認
- [ ] サイドバーアイコンがギザギザでないか確認
- [ ] 設定画面を開いてちらつきがないか確認
- [ ] ドラッグ移動が正常に動作するか確認
- [ ] リサイズが正常に動作するか確認

## Handoff

TBD
