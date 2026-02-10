# OCR縦書き対応（書字方向判定 + 2系統結合）実装案

1. **概要（1–3行）**
- OCR結果の近傍ベクトルから書字方向（Horizontal / Vertical / Unknown）を先に判定し、結合パイプラインを分岐する。
- 横書きは既存の改善済み2段階（同一行→行間）を継続し、縦書きは同一列→列間の2段階へ切り替える。
- 不確実な判定は現行の横書きパスへフォールバックし、誤判定時の破壊的影響を抑える。

2. **ゴール / 非ゴール**
- ゴール: 横書き/縦書きの混在ケースで、最低限の読み順破綻を減らす。
- ゴール: 縦書き時の結合規則（同一列判定、Y隣接、強制ブレーク）を実装可能な形で定義する。
- ゴール: 初期実装は「方向判定 + パス分岐 + 縦書き結合判定 + 読み順明示（1+2+3+4）」に限定する。
- 非ゴール: OCRエンジンそのものの変更や再学習。
- 非ゴール: 句読点整形や翻訳品質チューニングの最適化。

3. **前提・仮定**
- 現行の結合実装は `Services/OcrLineGrouper.cs` に集約され、横書き前提の結合が中心。
- 現行パイプラインは `OcrLineGrouper.MergeLines(...)` の出力順/結合結果を翻訳入力に利用する。
- 不確実判定時の安全側動作として、既存横書き結合へフォールバック可能である。
- 縦書き自動判定は当面 `ja` / `zh` 系言語のみを対象とし、それ以外は横書き固定とする。

4. **現状整理**
- 現在は横書きを主対象に `Y->X` で整列し、同一行/行間の結合を行う。
- 縦書きに対する専用判定（同一列判定、列順規則、縦方向ブレーク）が未定義。
- そのため、縦書き資料では読み順の崩れや不自然な結合が起きやすい。

5. **提案アーキテクチャ**
- コンポーネント構成:
  - Step A: 書字方向判定（Horizontal / Vertical / Unknown）
  - Step B-H: 横書き結合（既存: 同一行→行間）
  - Step B-V: 縦書き結合（新規: 同一列→列間）
- データフロー / シーケンス:
  1. OCR結果 `lines` を入力。
  2. 言語ゲート判定（`ja` / `zh*` のみ自動判定対象、その他は横書き固定）。
  3. 対象言語のみ、近傍関係からテキストクラスタを作成。
  4. クラスタごとに近傍ベクトル（右接続優勢か、下接続優勢か）を集計して方向判定。
  5. `Horizontal` は既存横書きパス、`Vertical` は縦書きパスへ分岐。
  6. `Unknown`（小クラスタ含む）は横書きパスへフォールバック。
  7. 結果を統一フォーマット `OcrLine` で返却。
- 既存パターンへの整合:
  - `MergeLines(...)` の入口/出口インターフェースは維持。
  - 横書きの既存判定・既存閾値は原則維持し、縦書き分岐を追加する。

6. **インターフェース設計**
- 既存メソッド:
  - `MergeLines(IReadOnlyList<OcrLine> lines, AppSettings settings)` を維持。
- 新規内部メソッド（案）:
  - `ShouldEnableVerticalDetectionForLanguage(...)`（`ja` / `zh*` ゲート）
  - `ResolveWritingMode(...)`（方向推定）
  - `ResolveWritingModeForCluster(...)`（クラスタ単位方向推定）
  - `MergeHorizontal(...)`（既存横書き分岐）
  - `MergeVertical(...)`（同一列→列間）
  - `IsSameColumnCandidate(...)`（X中心差 + 幅比）
  - `ShouldMergeVerticalAdjacent(...)`（Yギャップ + 強制ブレーク）
  - `OrderVerticalColumns(...)`（列順規則）
- 設定追加（最小候補）:
  - `EnableVerticalMerge: bool`
  - `VerticalModeAutoDetect: bool`
  - `VerticalModeOverride: Auto | Horizontal | Vertical`（任意の手動上書き）
  - `VerticalColumnOrder: RightToLeft | LeftToRight`
  - `VerticalGapRatio: double`
- 方向判定の内部定数（実装固定値として管理）:
  - `minSampleCount`（判定に必要な最小サンプル数）
  - `dominanceRatio`（Horizontal/Vertical 決定に必要な優勢比）
  - `hysteresisMargin`（前回モードからの切替余白）

7. **実装手順（ステップ分割）**
- Step 1: `OcrLineGrouper` に書字方向判定器 `ResolveWritingMode(...)` を追加。
  - 近傍ベクトル集計で `Horizontal / Vertical / Unknown` を返す。
  - 判定はクラスタ単位を基本とし、`minSampleCount` 未満は `Unknown` とする。
- Step 1-1: 言語ゲートを追加。
  - `sourceLanguage` が `ja` / `zh*` の場合のみ自動方向判定を実施。
  - それ以外は横書き固定（`Horizontal`）で処理する。
- Step 2: `MergeLines(...)` を分岐化。
  - `Horizontal` -> 既存横書きパス
  - `Vertical` -> 新規縦書きパス
  - `Unknown` -> 既存横書きへフォールバック
- Step 2-1: 判定安定化を実装。
  - `dominanceRatio` を満たさない場合は `Unknown` とする。
  - 直前判定からの切替は `hysteresisMargin` を満たす場合のみ許可する。
- Step 3: 縦書きパス実装（同一列→列間）。
  - 同一列判定: X中心差 + 幅比
  - 結合判定: `Yギャップ <= minWidth * K`
  - 強制ブレーク: `Yギャップ > minWidth * K2` なら非結合
- Step 4: 縦書き読み順を明示実装。
  - 日本語縦書きの既定: 列は右→左、列内は上→下
  - 不確実時は横書きフォールバックで安全側に倒す
- Step 5（後続フェーズ）: 連結ルール分離と設定追加（5+6）を段階導入。
  - 横書き: スペース連結
  - 縦書き: 原則スペースなし（必要時のみ句読点整形）

8. **非機能要件チェック**
- 性能: 方向判定は近傍探索ベースで既存オーダー（概ね O(N * neighbor)）を維持。
- セキュリティ: OCR結果の後処理のみで新規外部I/Oなし。
- 可観測性: ログに `writingMode`, `inputCount`, `mergedCount` を出せるようにする。
- 互換性: `Unknown` の横書きフォールバックで既存動作からの乖離を抑える。
- 設定優先順位（MUST）:
  - `EnableLineMerge=false` -> すべての結合処理を無効化（最優先）。
  - `EnableLineMerge=true && EnableTwoStageLineMerge=false` -> 旧横書き結合のみ。
  - `EnableLineMerge=true && EnableTwoStageLineMerge=true && EnableVerticalMerge=false` -> 新横書き結合のみ。
  - `EnableLineMerge=true && EnableTwoStageLineMerge=true && EnableVerticalMerge=true && VerticalModeOverride=Horizontal` -> 横書き固定。
  - `EnableLineMerge=true && EnableTwoStageLineMerge=true && EnableVerticalMerge=true && VerticalModeOverride=Vertical` -> 縦書き固定。
  - `EnableLineMerge=true && EnableTwoStageLineMerge=true && EnableVerticalMerge=true && VerticalModeOverride=Auto` -> `ja` / `zh*` のみ自動方向判定 + 横/縦分岐を有効化（他言語は横書き固定）。

9. **リスクと緩和策**
- Risk: 方向判定誤りで読み順が悪化する。
- Mitigation: 信頼度が低いケースは `Unknown` 扱いで横書きへフォールバック。
- Risk: 縦書き判定が強すぎると、本来別列を誤結合する。
- Mitigation: 強制ブレーク（K2）を設け、長距離結合を禁止する。
- Risk: 設定を増やしすぎると調整コストが上がる。
- Mitigation: 初期実装は 1+2+3+4 に限定し、設定公開は最小4項目に絞る。

10. **影響範囲（変更ファイル候補）**
- `Services/OcrLineGrouper.cs` — 方向判定、縦書き分岐、縦書き結合ロジック追加
- `Models/AppSettings.cs` — 縦書き最小設定の追加
- `MainWindow.xaml.cs`（任意） — 方向判定/結合件数のログ補強

11. **Definition of Done（完了条件）**
- [ ] 横書きケースで既存品質を維持（回帰なし）
- [ ] 縦書きケースで読み順が「列:右→左、列内:上→下」に従う
- [ ] 方向判定が不確実なケースで横書きフォールバックする
- [ ] `ja` / `zh*` 以外の言語では横書き固定で処理される
- [ ] `minSampleCount` 未満/`dominanceRatio` 未満のケースで `Unknown` 判定に落ちる
- [ ] `hysteresisMargin` によりモードのフレーム間揺れが抑制される
- [ ] 縦書きの強制ブレークが機能し、別列誤結合が抑制される
- [ ] `dotnet build Hotkey-Translator.sln` が成功する
