# OCRエンジン別 LineMergeプリセット自動適用 実装案

1. **概要（1–3行）**
- `OcrEngine` が `WinRt` / `Paddle` で切り替わるたびに、9つの枠結合パラメータをエンジン別プリセットから自動反映する。
- 既存の実行時参照先（`AppSettings` の各 Merge パラメータ）は維持し、適用タイミングだけを追加する。
- `Doc/Paddle-settings.json` と `Doc/WinRT-settings.json` は初期値ソースとして扱い、運用時は `settings.json` 内のプリセットを正とする。

2. **ゴール / 非ゴール**
- ゴール: `WinRt` と `Paddle` で最適値を自動切替し、手動再設定の手間をなくす。
- ゴール: 既存の `OcrLineGrouper` ロジックを変更せず、設定適用レイヤで完結させる。
- ゴール: 既存ユーザー設定との互換性を維持する。
- 非ゴール: 新しい結合アルゴリズムの導入。
- 非ゴール: UIで9パラメータを直接編集する新規画面の追加（今回は必須にしない）。

3. **前提・仮定**
- 現在の結合ロジックは `AppSettings` の9パラメータを直接参照している。
- 既存 `settings.json` は単一セットの値しか持たないため、エンジン切替で最適値が失われる。
- `Doc/Paddle-settings.json` / `Doc/WinRT-settings.json` に各エンジン向けの基準値が定義済み。

4. **現状整理**
- 参照側:
  - `Services/OcrLineGrouper.cs` が以下9項目を使用。
  - `MergeOverlapRatioThreshold`
  - `MergeVerticalWeight`
  - `MergeThresholdRatio`
  - `MergeNeighborCount`
  - `RowMergeYCenterToleranceRatio`
  - `RowMergeHeightRatioMin`
  - `RowMergeMaxGapRatio`
  - `RowMergeHardBreakRatio`
  - `RowMergeNeighborCount`
- 設定読込:
  - `Services/SettingsService.cs` が `%AppData%/Hotkey-Translator/settings.json` をロードし、`AppSettings` に反映。
- 課題:
  - `OcrEngine` を切り替えても、9項目は前回値のままでエンジン特性と不一致になりやすい。

5. **提案アーキテクチャ**
- コンポーネント構成:
  - `LineMergePreset`（9項目を束ねるDTO）を追加。
  - `AppSettings` に `WinRtLineMergePreset` / `PaddleLineMergePreset` / `EnablePerEngineLineMergePreset` を追加。
  - `LineMergePresetService`（または `AppSettings` 拡張メソッド）で「エンジン→実行値」適用を集中管理。
- データフロー / シーケンス:
  1. アプリ起動時に `settings.json` をロード
  2. プリセット未定義なら既定値で初期化（WinRT/Paddle）
  3. `EnablePerEngineLineMergePreset=true` の場合、現在の `OcrEngine` に対応するプリセットを9項目へ反映
  4. ユーザーが `OcrEngine` を変更したとき、同様に自動反映して保存
- 既存パターンへの整合:
  - `OcrLineGrouper` は変更不要（参照先プロパティは現状維持）。
  - 実行値は引き続き `AppSettings` の既存9項目を参照するため影響範囲が小さい。

6. **インターフェース設計**
- 新規モデル（案）:
  - `Models/LineMergePreset.cs`
    - `double MergeOverlapRatioThreshold`
    - `double MergeVerticalWeight`
    - `double MergeThresholdRatio`
    - `int MergeNeighborCount`
    - `double RowMergeYCenterToleranceRatio`
    - `double RowMergeHeightRatioMin`
    - `double RowMergeMaxGapRatio`
    - `double RowMergeHardBreakRatio`
    - `int RowMergeNeighborCount`
- `AppSettings` 追加項目（案）:
  - `bool EnablePerEngineLineMergePreset = true`
  - `LineMergePreset WinRtLineMergePreset`
  - `LineMergePreset PaddleLineMergePreset`
- 適用API（案）:
  - `void ApplyLineMergePresetForCurrentEngine(AppSettings settings)`
  - `void ApplyLineMergePreset(AppSettings settings, OcrEngineKind engine)`

- バリデーション:
  - `MergeNeighborCount` / `RowMergeNeighborCount` は `>=1`
  - 比率値は `>0` を下限にクリップ
  - 不正値は既定値へフォールバック

7. **実装手順（ステップ分割）**
- Step 1: `LineMergePreset` 型を追加し、WinRT/Paddleの既定値ファクトリを実装。
- Step 2: `AppSettings` にエンジン別プリセット項目と `EnablePerEngineLineMergePreset` を追加。
- Step 3: `SettingsService.LoadAsync()` 後の正規化処理で、プリセット未定義時の補完と値検証を実施。
- Step 4: 起動時に `OcrEngine` に応じたプリセットを実行値9項目へ適用。
- Step 5: UIのOCRエンジン変更ハンドラで、`OcrEngine` 変更直後にプリセットを再適用して保存。


8. **非機能要件チェック**
- 性能: 設定コピーのみのため実行コストは無視可能。
- セキュリティ: 追加の秘密情報はなし。
- 可観測性: 設定適用ログを1行追加（`engine`, `preset`, 9項目要約）。
- 互換性: 既存9項目を残すため、旧 `settings.json` でも起動可能。
- 運用: `EnablePerEngineLineMergePreset=false` で従来運用へ戻せる。

9. **リスクと緩和策**
- Risk: エンジン切替時にユーザーが一時調整した値が上書きされる。
- Mitigation: `EnablePerEngineLineMergePreset` のON/OFFを用意し、必要時は自動反映を停止可能にする。
- Risk: 既存ユーザーの単一設定が初回移行で意図せず変わる。
- Mitigation: 初回は「現在値をWinRTプリセットへコピー + Paddleは既定値」の保守的移行を採用する。
- Risk: `Doc/*.json` 依存で実行環境差が出る。
- Mitigation: `Doc/*.json` は初期値生成時のみ参照し、ランタイムは `settings.json` のみ参照。

10. **影響範囲（変更ファイル候補・移行・ドキュメント更新）**
- `Models/LineMergePreset.cs` — 新規追加（9項目DTO）
- `Models/AppSettings.cs` — プリセット関連プロパティ追加
- `Services/SettingsService.cs` — ロード後の正規化/適用処理追加
- `MainWindow.xaml.cs`（または設定変更ハンドラ） — OCRエンジン切替時のプリセット再適用
- `Doc/Paddle-settings.json`, `Doc/WinRT-settings.json` — 既定値ソース（任意で移行時参照）

11. **Definition of Done（完了条件のチェックリスト）**
- [ ] `OcrEngine=WinRt` に切り替えると WinRTプリセットの9項目が適用される
- [ ] `OcrEngine=Paddle` に切り替えると Paddleプリセットの9項目が適用される
- [ ] アプリ再起動後もエンジン別プリセットが保持・再適用される
- [ ] `EnablePerEngineLineMergePreset=false` で自動切替が停止し、従来挙動に戻る
- [ ] `dotnet build Hotkey-Translator.sln` が成功する
