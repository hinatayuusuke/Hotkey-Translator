# CTranslate2 + NLLB200 600M 翻訳サーバ（gRPC/UV・分離構成）実装案

1. **概要（1–3行）**
- CTranslate2 + NLLB200 600M を Python gRPC サーバとして実装し、WPF から翻訳プロバイダとして利用する。
- OCR サーバとは分離し、翻訳専用プロセスとして起動・監視する。
- CPU(int8)/GPU(fp16) 両対応、初回DL、既存フォールバックと共存。

2. **ゴール / 非ゴール**
- ゴール: オフライン翻訳（NLLB200）を UI で ON/OFF でき、既存翻訳フォールバックに組み込む。
- ゴール: CPU/GPU 両対応、CPUは int8 を優先利用。
- ゴール: Target 言語優先で利用できる言語を決定。
- 非ゴール: OCR の仕様変更、翻訳結果の後処理ロジック刷新。
- 非ゴール: 既存翻訳プロバイダの削除。

3. **前提・仮定**
- Python 実行は uv を使用。
- gRPC はローカルホスト（127.0.0.1）でのみ公開。
- CTranslate2 のランタイムが利用可能（CPU/GPU）である。
 - NOTE: モデルのライセンスは CC-BY-NC-4.0（商用利用不可）。

3.1 **参考情報（モデルカード要点）**
- 対象モデル: `entai2965/nllb-200-distilled-600M-ctranslate2`
- 目的: 研究用途向けの機械翻訳（製品/商用利用は想定外）。
- 入力長: 512 token 超で品質低下の可能性。
- 言語コード: Flores-200 の言語タグ（例: `eng_Latn`, `jpn_Jpan`）。
- 実行例: `ctranslate2.Translator` + `AutoTokenizer` で `target_prefix` にターゲット言語タグを付与。
- メモリ目安: 600M モデルで約 3GB。

4. **現状整理**
- 翻訳は `TranslationFallbackService` + `ITranslationProvider` 構成。
- PaddleOCR gRPC サーバは既に起動・監視されている。

5. **提案アーキテクチャ**
## 5.1 翻訳 gRPC サーバ（Python）
- `TranslationService/` を新設（OCRサーバとは別）
- `server.py`：gRPC サーバ起動
- `translator_engine.py`：CTranslate2 + NLLB200 のロードと推論
- `requirements.txt` or `pyproject.toml`：依存定義

## 5.2 WPF 側
- `CTranslate2TranslationProvider` を追加
- `TranslationFallbackService` に統合
- UI で ON/OFF と優先順位を設定

6. **インターフェース設計**
## 6.1 gRPC 定義（案）
- `Translate(TranslateRequest) -> TranslateResponse`
  - request: `texts[]`, `source_lang`, `target_lang`, `device`, `precision`
  - response: `translations[]`, `model`, `latency_ms`

## 6.2 設定項目（WPF）
- `EnableCTranslate2` (bool)
- `CTranslate2GrpcHost` / `Port`
- `CTranslate2ModelName` (固定: NLLB200-600M)
- `CTranslate2Device` (cpu/gpu)
- `CTranslate2Precision` (int8 / fp16)
- `CTranslate2ModelDir` (optional)
- `CTranslate2AutoDownload` (bool)

7. **実装手順（ステップ分割）**
### Step 1: Python サーバ作成
- gRPC サービスの骨組み
- NLLB200 600M をロード
- CPU int8 / GPU fp16 切替

### Step 2: WPF 側プロバイダ追加
- `ITranslationProvider` 実装
- gRPC クライアント追加

### Step 3: UI 設定追加
- ON/OFF, device, precision, auto-download
- Translation priority への追加

### Step 4: 起動/監視/再起動
- PaddleOCR gRPC と同様のホスト管理ロジック

8. **非機能要件チェック**
- 性能: CPU int8 で最低限の速度、GPUで高速化。
- 可観測性: サーバ起動/翻訳時間をログ出力。
- 互換性: 既存翻訳は保持し、失敗時にフォールバック。

9. **リスクと緩和策**
- Risk: 初回DLが失敗すると翻訳不可
  - Mitigation: 再試行・ログ案内・フォールバック
- Risk: GPU利用不可で遅い
  - Mitigation: CPU int8 fallback

10. **影響範囲**
- `TranslationService/` (新規)
- `Services/` (翻訳プロバイダ追加)
- `MainWindow.xaml` / `MainWindow.xaml.cs`
- `Models/AppSettings.cs`

11. **Definition of Done**
- CTranslate2 翻訳を ON/OFF できる
- 既存翻訳フォールバックと共存
- CPU/GPU切替が動作する
- 初回DLが完了した後に翻訳できる
