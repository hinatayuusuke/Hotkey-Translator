# BetterUIPlan: 設定UI整理案

## Goal
- 設定項目の増加に対応し、迷いなく操作できるUIに整理する
- 主要操作は1画面で完結しつつ、詳細設定は隠せるようにする

## 方針
- セクション分割 + 折りたたみ (Expander)
- 基本/詳細の二段構成
- 重要な状態は “ステータス表示” で可視化

## セクション案

### 1) Capture
- 基本
  - CaptureMode
  - ROI設定 (Select ROI / Enable ROI / 状態表示)
- 詳細 (Expander)
  - Capture provider / cooldown / black-frame 設定

### 2) OCR
- 基本
  - OCR エンジン選択 (WinRT / Paddle)
  - 言語設定
- 詳細
  - Paddle 設定 (Project / uv / device / model)

### 3) Translation
- 基本
  - 翻訳有効化 (Gemini / DeepL)
  - 翻訳優先度リスト
- 詳細
  - APIキー入力
  - エンドポイント入力

### 4) Overlay
- 基本
  - Overlay フォント / 前景 / 背景
- 詳細
  - 自動フィット係数 (将来)

## UIコンポーネント改善
- ステータス表示
  - 例: "Gemini: enabled", "DeepL: key missing"
- DeepL endpoint は ComboBox (Free/Pro) + Custom
- 翻訳優先度は Drag&Drop リスト (可能なら)
- 既定値へ戻すボタン (セクションごと)

## 実装ステップ
1) XAML をセクション分割 + Expander へ移行
2) Basic/Advanced の切替トグル追加
3) Translation のステータス表示とエンドポイント選択UI追加
4) 優先度UIをドラッグ移動対応 (簡易なら上下ボタン維持)

## 注意点
- 既存設定の保存ロジックは維持
- UI変更は必ず `SettingsService` のキーと一致させる
- 変更後は UI の操作導線を簡潔に保つ