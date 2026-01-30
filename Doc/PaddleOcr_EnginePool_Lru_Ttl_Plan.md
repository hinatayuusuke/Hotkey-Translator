# PaddleOCR EnginePool LRU/TTL + Init Guard Plan

1. **概要（1–3行）**
- PaddleOCR gRPCサーバのエンジンキャッシュにLRU(最大2)とTTL(長め)を導入し、言語/検出モデル切替時のメモリ肥大化を抑える。
- 初期化中の連打を防ぐため、UI側に簡易ローディング表示とガードを入れる。

2. **ゴール / 非ゴール**
- ゴール: エンジン数を2に制限し、一定時間未使用なら破棄する。
- ゴール: 初期化中の連打を抑止し、ユーザに進行状況を伝える。
- ゴール: 既存の言語/検出モデル切替の動作は維持する。
- 非ゴール: PaddleOCR自体の性能最適化や推論結果の改良。
- 非ゴール: GPU/CPUの自動切替や複数GPU制御。

3. **前提・仮定**
- gRPCサーバは常駐し、言語/検出モデルの切替はリクエスト単位で行う。
- EnginePoolは(言語, detモデル)キーでPaddleOcrEngineをキャッシュする。
- TTLは「長め」に設定する（例: 20〜30分）。
- UI側のローディングは初回・切替時にのみ表示される想定。

4. **現状整理**
- OcrService/server.py の EnginePool は無制限キャッシュ。
- リクエストで language と text_detection_model_name を受け取ると、未作成なら新規生成。
- 破棄や上限管理、初期化中ガードは未実装。

5. **提案アーキテクチャ**

   * コンポーネント構成
   - OcrService/server.py: EnginePool にLRU+TTL制御を追加。
   - WPF側: OCR実行中/初期化中のガード + 簡易ロード表示。

   * データフロー / シーケンス（文章で可）
   - Recognize() -> EnginePool.get(language, det_model)
   - EnginePool内でTTL切れ/容量超過をチェック
   - TTL切れ or LRU超過なら古いエンジンを破棄
   - 必要なら新規エンジンを生成して返却
   - UI側は初期化フラグ中は入力を無効化し、完了後に解除

   * 既存パターンへの整合
   - 既存の request.language / request.text_detection_model_name の設計は維持。
   - 既存のデフォルト言語/モデル引数も維持。

6. **インターフェース設計**

   * API / 関数 / イベント / Queue / DB スキーマ変更
   - EnginePool.__init__(max_engines=2, ttl_seconds=1800) の追加
   - EnginePool.get() 内部でLRU/TTL制御
   - UI側: 初期化ガードフラグと簡易ロード表示

   * 入出力、エラー、バリデーション
   - language / det_model が空の場合は既存のデフォルトにフォールバック
   - TTLやmax_enginesが無効値なら固定値に丸める
   - 初期化中は新規OCRリクエストを拒否/キュー抑制

7. **実装手順（ステップ分割）**

   * Step 1…
   - EnginePoolにLRU用の順序管理(OrderedDict or list)を追加
   - 각エンジンの最終利用時刻を保持

   * Step 2…
   - get() で TTLチェック → 期限切れを破棄
   - 最大2超過なら最古の未使用を破棄

   * Step 3…
   - 初期化ログに「キャッシュ制限/TTL」を出力
   - 破棄時にログを出して挙動確認しやすくする

   * Step 4…
   - WPF側でOCR実行中フラグを追加
   - 初期化中はボタン/ホットキー入力を無効化
   - 画面に簡易ローディング表示を追加

8. **非機能要件チェック**

   * 性能: LRU/TTLチェックは低頻度・軽量にする（毎リクエストでもO(1)〜O(n)小）
   * セキュリティ: 外部入力の language/model は既存のフォールバック処理維持
   * 可観測性: 破棄ログ・初期化ログを追加し、チューニング可能にする
   * 互換性: API変更なし（gRPCの入出力は現行のまま）
   * 運用: TTL/最大数は固定値（必要なら起動引数化）

9. **リスクと緩和策**
- Risk: 破棄直後に同一組み合わせが再要求されると初期化コストが発生。
- Mitigation: TTLを長めに設定し、頻繁な破棄を避ける。
- Risk: UI側ガードが強すぎると操作が詰まる。
- Mitigation: 表示は軽量にし、完了時に必ず解除する。

10. **影響範囲**
- 変更ファイル候補
  - OcrService/server.py
  - WPF側のUI/イベント処理（MainWindow.xaml / MainWindow.xaml.cs）
- 移行: なし
- ドキュメント更新: 本ファイル

11. **Definition of Done**
- EnginePoolが最大2エンジンまで保持すること
- TTL経過後にエンジンが破棄されること（ログで確認）
- 初期化中に連打しても重複OCRが走らないこと
- ロード表示が完了後に必ず消えること
- 既存のgRPCリクエストで言語/検出モデル切替が引き続き動作すること
