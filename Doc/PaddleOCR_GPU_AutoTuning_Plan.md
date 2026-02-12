# PaddleOCR GPU自動最適化 実装案

1. **概要（1–3行）**
- PaddleOCR 初期化時に `enable_hpi` を常時 `true` に固定し、`use_tensorrt` と `precision` は GPU/環境可用性に応じて自動決定する。
- TensorRT が使える場合は `use_tensorrt=true` かつ `precision=fp16`、使えない場合は `use_tensorrt=false` かつ `precision=fp32` とする。
- 失敗時は安全側へ即フォールバックし、起動失敗による OCR 全停止を避ける。

2. **ゴール / 非ゴール**
- ゴール:
- GPU環境での PaddleOCR 推論設定を自動最適化し、設定ミスによる性能低下を減らす。
- 環境差（TRT有無）で起動失敗しても、確実に `fp32` 経路へ戻せるようにする。
- 非ゴール:
- Settings.json へ新規項目を追加して手動制御を増やすこと。
- CPU経路の最適化（現状方針は GPU 前提維持）。
- PaddleOCR 本体パラメーター全体の再設計。

3. **前提・仮定**
- 現行 `OcrService/ocr_engine.py` は `PaddleOCR(**kwargs)` を1回構築し、失敗時の再試行経路は持たない。
- `PaddleDevice=cpu` はホスト側で `gpu:0` へ補正される運用であり、実質 GPU 前提。
- `enable_mkldnn` / `mkldnn_cache_capacity` は今回「デフォルト任せ」を維持する。

4. **現状整理**
- 現状 `enable_hpi` / `use_tensorrt` / `precision` は `kwargs` に未指定で、PaddleOCRデフォルト依存。
- `text_det_*` と `text_rec_score_thresh` は `ocr_engine.py` 側で固定値を指定済み。
- GPU有効性は `paddle.device.is_compiled_with_cuda()` と `cuda.device_count()` で事前チェックしている。

5. **提案アーキテクチャ**
- コンポーネント構成:
- `OcrService/ocr_engine.py` の `PaddleOcrEngine.__init__` で推論設定を決定する小さな関数を追加する。
- データフロー / シーケンス:
1. GPU可用性を確認（既存チェック継続）。
2. `enable_hpi=true` を常時設定。
3. TRT利用可否を判定（利用可能なら `use_tensorrt=true`）。
4. `use_tensorrt=true` のとき `precision=fp16`、それ以外は `precision=fp32`。
5. PaddleOCR 初期化失敗時は `use_tensorrt=false` + `precision=fp32` で1回再試行。
6. 最終決定値をログへ出力。
- 既存パターン整合:
- 既存の `kwargs` 構築方式に追記するため、呼び出し側（gRPC/Provider）の変更は不要。

6. **インターフェース設計**
- API / 関数変更:
- `OcrService/ocr_engine.py` に内部ヘルパー（例: `_resolve_runtime_inference_options()`）を追加。
- 入出力:
- 入力: `device`, GPU可用情報
- 出力: `enable_hpi`, `use_tensorrt`, `precision`
- エラー/フォールバック:
- 初回 `PaddleOCR(**kwargs)` 失敗時に、TRT無効化の安全設定で再初期化。
- 再試行失敗時のみ例外送出。

7. **実装手順（ステップ分割）**
- Step 1: `ocr_engine.py` に推論設定解決ロジックを追加（`enable_hpi=true` 固定、TRT条件判定、precision決定）。
- Step 2: `PaddleOCR(**kwargs)` 初期化にフォールバック再試行を実装。
- Step 3: 起動時ログに最終採用値（`enable_hpi/use_tensorrt/precision`）を追加。
- Step 4: 既存GPU環境で起動確認し、TRT有/無の両ケースをログで検証。

8. **非機能要件チェック**
- 性能:
- TRT有効環境では `fp16` により推論高速化が期待できる。
- TRT不可環境では `fp32` へ自動退避し、起動安定性を優先。
- セキュリティ:
- 外部入出力変更なし。
- 可観測性:
- 自動決定値とフォールバック発生有無を必ずログ出力。
- 互換性:
- 呼び出しインターフェース不変（既存設定との互換性維持）。

9. **リスクと緩和策**
- Risk: TRT可用判定が不完全で初回起動に失敗する可能性。
- Mitigation: 初期化失敗時の `use_tensorrt=false` 再試行を必須化する。
- Risk: `fp16` で精度差や挙動差が出る可能性。
- Mitigation: 問題時は自動で `fp32` フォールバックし、ログで判別可能にする。
- Risk: 将来の PaddleOCR 更新でオプション名が変わる可能性。
- Mitigation: 例外時にパラメーター名を含むエラーログを残し、追従修正しやすくする。

10. **影響範囲**（変更ファイル候補・移行・ドキュメント更新）
- `OcrService/ocr_engine.py` — 推論設定の自動決定とフォールバック再初期化を追加。
- `Doc/PaddleOCRv5_Settings_Operation_Guide.md` — 実装後に「デフォルト依存」記述を更新。
- `Doc/PaddleOCR_Core_Parameters_Reference.md` — プロジェクト実装方針（自動決定値）を追記。

11. **Definition of Done**
- [ ] `enable_hpi` が常時 `true` で初期化される。
- [ ] TRT可用時に `use_tensorrt=true` かつ `precision=fp16` が採用される。
- [ ] TRT不可/失敗時に `use_tensorrt=false` かつ `precision=fp32` へフォールバックできる。
- [ ] 起動ログで最終採用値が確認できる。
- [ ] 既存の PaddleOCR gRPC 経路で回帰がない（起動・OCR実行が成功する）。

