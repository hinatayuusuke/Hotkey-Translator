# Plan7: PaddleOCR オンデマンド導入 (UIトリガー)

## Goal
- ユーザが UI で PaddleOCR を選択した時に、必要な Python/依存を自動セットアップする
- 既存の WinRT OCR を壊さず、失敗時は安全にフォールバックする
- 導入フローの進捗と失敗理由を UI に可視化する

## 前提
- 実行環境は Windows
- uv を使って Python 環境と依存を管理する
- 既存の OCR パイプラインは `OcrEngine` 経由で選択される

## 方針 (推奨)
- PaddleOCR を初回選択したタイミングでセットアップを起動
- セットアップが完了するまで OCR は WinRT を維持する
- セットアップ状態を `settings.json` に永続化して再実行を避ける

## 変更点 (設計)

### 1) Settings 拡張
`Models/AppSettings.cs` に導入状態を保持する。

- 例:
  - `public bool PaddleOnDemandEnabled { get; set; } = true;`
  - `public bool PaddleInstalled { get; set; } = false;`
  - `public string? PaddleInstallVersion { get; set; }`
  - `public string? PaddleLastInstallError { get; set; }`

### 2) UI フロー
- OCR エンジンを Paddle に変更した際に導入確認を表示
  - "PaddleOCR をセットアップします (約XXX MB)。続行しますか？"
- 進捗 UI
  - "uv 検出 -> python 準備 -> pip/uv sync -> モデル取得"
- 完了後に PaddleOCR を有効化

### 3) セットアップマネージャ
- `PaddleSetupManager` を追加
  ```csharp
  Task<PaddleSetupResult> EnsureReadyAsync(AppSettings settings, IProgress<string> progress, CancellationToken ct);
  ```
- 役割:
  - uv の存在確認 (無い場合は自動導入 or 失敗通知)
  - `Tools/PaddleOcr` の project 生成 (pyproject.toml + bridge script)
  - `uv sync` 実行
  - モデルディレクトリ取得 (任意)
  - 成功/失敗の永続化

### 4) 自動導入フロー (概略)
1. UI で PaddleOCR を選択
2. `EnsureReadyAsync` を起動
3. 成功 -> `settings.OcrEngine = Paddle`, `PaddleInstalled = true`
4. 失敗 -> `PaddleInstalled = false`, `PaddleLastInstallError` を保存し WinRT 継続

### 5) uv/インストール戦略
- uv が無い場合は次のいずれか:
  - A) ガイド表示のみ (ダウンロードURL表示)
  - B) 自動導入 (インストーラ実行)
- ProjectDir に `pyproject.toml` を生成
- `uv sync` に失敗したらエラーをログ/画面に表示

### 6) 互換性
- Paddle が未導入のままでも WinRT OCR で動作を継続
- 既存ユーザは勝手に導入されない (UI 選択時のみ)

## リスクと対策
- リスク: ダウンロード容量が大きい
  - 対策: 事前に容量表示と確認ダイアログ
- リスク: 企業環境でインストールが失敗
  - 対策: 失敗理由を保存し、手動手順へ誘導
- リスク: セットアップ中に OCR が使えない
  - 対策: セットアップ完了まで WinRT を使い続ける

## テスト観点
- PaddleOCR 未導入で UI を切り替えた時に導入フローが走る
- 導入失敗時に WinRT に戻る
- 導入成功後に PaddleOCR が選択される
- 2 回目以降は導入フローがスキップされる

## 実装順 (推奨)
1) Settings 追加
2) UI イベントで Paddle 選択を検知
3) PaddleSetupManager 実装
4) UI 進捗表示とエラーハンドリング
5) ログ整理と運用ドキュメント更新