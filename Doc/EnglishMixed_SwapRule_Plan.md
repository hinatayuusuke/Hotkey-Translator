# English Mixed Language Option + Swap Rule Plan

1. **概要（1–3行）**
- Source言語ドロップダウンに「英語混在」を追加し、Paddle OCRの認識モデル切替に利用する。
- Swapボタンは英語（通常/混在）が関与する場合、Target側を英語固定にする。
- WinRT側は混在設定を持たないため、`en-mixed` は `en` と同等扱いにする。

2. **ゴール / 非ゴール**
- ゴール: 英語混在（latin）/英語専用（en）をUIから明示指定できる。
- ゴール: Swap時に英語グループがTarget側に固定される挙動を実現する。
- ゴール: WinRT/翻訳は `en-mixed` を `en` として扱い互換性を保つ。
- 非ゴール: 自動言語判定の導入。

3. **前提・仮定**
- SourceLanguageはUIで選択されるタグ文字列。
- Paddle OCRのみ `text_recognition_model_name` を言語に応じて切替できる。

4. **現状整理**
- SourceLanguageには英語/日本語/中国語などがあるが「英語混在」は存在しない。
- Swapは単純に Source/Target を交換している。
- Paddle OCRの text_recognition_model_name は固定で言語反映されていない。

5. **提案アーキテクチャ**

   * コンポーネント構成
   - UI: SourceLanguageドロップダウンに「English (Mixed)」を追加
   - WPF: Swapロジックで英語グループ固定判定
   - Python OCR: language -> rec model のマッピング追加

   * データフロー / シーケンス
   - SourceLanguageが `en` → rec model = en_PP-OCRv5_mobile_rec
   - SourceLanguageが `en-mixed` → rec model = latin_PP-OCRv5_mobile_rec
   - SourceLanguageが `ru`（ロシア語中心）→ rec model = eslav_PP-OCRv5_mobile_rec
   - WinRT/翻訳は `en-mixed` を `en` として処理
   - Swap時: Source/Targetのどちらかが英語グループならTargetは英語に固定

   * 既存パターンへの整合
   - UIタグ追加のみで既存の言語選択設計は維持。

6. **インターフェース設計**

   * UI
   - SourceLangCombo に `English (Mixed)` (tag: `en-mixed`) を追加

   * OCRモデル切替
   - `ocr_engine.py` に language->rec model の対応を追加

   * WinRT/翻訳
   - `en-mixed` を `en` に正規化

7. **実装手順（ステップ分割）**

   * Step 1…
   - SourceLangComboの候補に `English (Mixed)` を追加

   * Step 2…
   - Swapロジックを更新し、英語グループがTarget側なら固定

   * Step 3…
   - PaddleOCRエンジン生成時に `text_recognition_model_name` を言語に応じて切替
   - WinRT/翻訳には正規化済みの `en` を渡す

8. **非機能要件チェック**

   * 性能: 影響なし
   * 互換性: 既存の英語/日本語設定は維持
   * 可観測性: 必要ならログ出力を追加

9. **リスクと緩和策**
- Risk: 英語混在が翻訳言語と混ざると誤翻訳が起こりうる
- Mitigation: OCRモデル切替に限定し、翻訳の言語は `en` を維持

10. **影響範囲**
- 変更ファイル候補
  - MainWindow.xaml / MainWindow.xaml.cs
  - OcrService/ocr_engine.py
  - (必要なら) Services/PaddleGrpcHost.cs

11. **Definition of Done**
- SourceLanguageに「英語混在」が表示される
- Swap時に英語がTarget側に固定される
- Paddle OCRが英語/混在/ロシア語中心で認識モデルを切替できる
- WinRT/翻訳は `en-mixed` を `en` として扱う
