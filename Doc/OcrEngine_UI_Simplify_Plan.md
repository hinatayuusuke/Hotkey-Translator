# OCRエンジンUI整理 実装案（WinRT + PaddleOCRv5 のみ）

1. **概要（1–3行）**
   OCRエンジンの選択肢を WinRT と PaddleOCRv5 のみに絞り、UIと設定項目を整理する。
   既存のPaddleOCRv5 gRPC統合を前提に、不要なエンジン/設定を削除する。

2. **ゴール / 非ゴール**
   **ゴール**

   * OCRエンジンのUIを簡素化し、設定ミスを減らす
   * 保守対象を WinRT + PaddleOCRv5 に限定する
   * 既存ユーザ設定の移行負荷を最小化する

   **非ゴール**

   * Florence/PaddleVLLM など他エンジンの互換維持
   * OCR精度改善のための新アルゴリズム導入

3. **前提・仮定**（不確実性の扱いを明確化）

   * PaddleOCRv5 は gRPC サーバー常駐で動作する
   * WinRT OCR はフォールバック用途として残す

4. **現状整理**（現行挙動、関連モジュール、既存制約）

   * `OcrEngineKind` に複数のエンジンが定義されている
   * UIにエンジン選択が複数存在
   * OcrEngine が Florence/PaddleVllm も選択肢として持つ

5. **提案アーキテクチャ**

   ### コンポーネント構成

   * OCRエンジンは `WinRT` と `PaddleOCRv5` のみ
   * UIのエンジン選択を2択に限定

   ### データフロー / シーケンス

   1. UIで WinRT / PaddleOCRv5 を選択
   2. PaddleOCRv5 選択時は gRPCサーバー起動済みを前提
   3. gRPC失敗時は WinRT にフォールバック

   ### 既存パターンへの整合

   * 既存の `OcrEngine` フォールバック構造は維持

6. **インターフェース設計**

   * `OcrEngineKind` の列挙を WinRT / Paddle に限定
   * UIのエンジン選択ComboBox を2択に変更

7. **実装手順（ステップ分割）**

   ### Step 1: モデル定義整理
   * `OcrEngineKind` から Florence/PaddleVllm を削除

   ### Step 2: UI整理
   * OCRエンジン選択UIを2択に変更
   * 不要な設定項目を非表示/削除

   ### Step 3: エンジン実装整理
   * OcrEngine の Florence/PaddleVllm 分岐を削除
   * PaddleOCRv5 と WinRT のみ残す

8. **非機能要件チェック**

   * **性能**：不要なエンジンロードを削除
   * **互換性**：旧設定値は WinRT へフォールバック

9. **リスクと緩和策**

   * **旧設定が無効になる**
     * 緩和: 起動時に WinRT へ自動補正

10. **影響範囲**（変更ファイル候補・移行・ドキュメント更新）

* 変更候補
  * `Models/OcrEngineKind.cs`
  * `Services/OcrEngine.cs`
  * `MainWindow.xaml` / `MainWindow.xaml.cs`
  * 設定ロード処理

11. **Definition of Done**（完了条件のチェックリスト）

* [ ] UIに WinRT / PaddleOCRv5 のみ表示
* [ ] 旧エンジン選択はWinRTに補正
* [ ] OcrEngine の分岐が2択のみ
