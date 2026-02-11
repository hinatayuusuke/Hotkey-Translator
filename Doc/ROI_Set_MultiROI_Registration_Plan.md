# ROIセット登録・複数ROI描画・セット切替 実装案

1. **概要（1–3行）**
- 固定ROI機能は維持しつつ、ゲーム向けに「複数ROIを1セットとして登録・切替」できる運用を追加する。
- ROI選択は複数矩形の手動描画を前提とし、描画中は枠を可視化して登録確定する。
- 重なり防止は描画時に適用し、既存ROI近傍までで引けるように制約する。
- OCR実行は複数回に分割せず、ROI集合の外接矩形で切り抜いた1枚画像を従来パイプラインへ渡す。

2. **ゴール / 非ゴール**
- ゴール:
- ユーザーが複数ROIを1セットとして保存できる。
- セット切替で対象領域を即時変更できる。
- ROI作成時の重なり・近接ミスを抑制できる。
- 非ゴール:
- 複数セット同時OCR（初期実装では行わない）。
- ROIごとの複数OCR実行（初期実装では行わない）。
- OCR検出精度改善そのもの。
- 複数ウィンドウ同時追従の高度制御。

3. **前提・仮定**
- 既存は単一ROI中心で、固定ROI Overlayモードが存在する。
- 固定ROIとROIセットは用途が異なるため、設定・UI・実行経路を分離する。
- 非固定Overlayの描画・翻訳送信順は、ROIセット内の登録順で扱う。

4. **現状整理**
- ROIは `AppSettings.Roi / NormalizedRoi` を主に使用している。
- ROI選択UIは単発矩形選択を前提。
- 固定ROI Overlayモードがあり、全テキスト集約表示経路が存在する。

5. **提案アーキテクチャ**
- コンポーネント構成:
- `AppSettings` に ROIセット構造（複数セット + アクティブID）を追加。
- `RoiSelectorWindow` を複数矩形描画モードに拡張。
- `PipelineOrchestrator` はアクティブROIセットからマスク画像を1枚生成し、OCRは1回のみ実行する。
- データフロー / シーケンス:
1. ユーザーが「ROIセット編集」を開始
2. ROIを複数描画（描画中は可視枠表示）
3. `登録完了` で1セット保存
4. アクティブセット切替で次回OCRから適用
5. `RoiMode=Set` かつ ROI数2以上のとき、ROI集合の外接矩形で切り抜き
6. 切り抜き画像のうちROI外を黒塗りしてOCRへ投入
7. OCR結果はROI所属を判定し、行結合は同一ROI内だけ許可
- 既存パターン整合:
- 単一ROIは「1要素セット」として互換移行。
- 固定ROI経路は維持し、ROIセット経路とは排他で切替運用する。

6. **インターフェース設計**
- 新規設定（案）:
- `RoiSets: List<RoiSetConfig>`
- `ActiveRoiSetId: string?`
- `RoiSetConfig`:
- `Id: string`
- `Name: string`
- `Rects: List<NormalizedRect>`
- `CreatedAtUtc: string` / `UpdatedAtUtc: string`
- 運用モード（案）:
- `RoiMode: Single | Set`
- `RoiSet` は `RoiMode=Set` のときのみ有効
- 互換移行:
- 既存 `NormalizedRoi` がある場合は、起動時に `Default` セットへ自動移行する。
- 旧単一ROI設定は互換のため保持しつつ、実行時は `RoiMode` に従って適用対象を決める。
- `EnableFixedRoiOverlay` は既存値を維持する（自動無効化しない）。
- モード前提（MUST）:
- `EnableRoi=false` の場合は単一ROI/ROIセットともに無効（ROI未使用）。
- `RoiMode=Set` は `EnableRoi=true` を前提とする。
- `EnableFixedRoiOverlay` は表示モード設定として維持し、ROI有効/無効判定は `EnableRoi` に従う。
- ROIセットOCRポリシー（MUST）:
- `RoiMode=Set` かつ ROI数が2以上のときは、まずROI集合の外接矩形を切り抜く。
- 切り抜き後画像の ROI外を黒塗りする（余白拡張なし、ROI境界そのまま）。
- `RoiMode=Set` かつ ROI数が1のときは黒塗りを行わず、従来どおり単一ROI切り抜きでOCRへ渡す。
- OCR投入画像は1枚のみ（複数ROIであっても OCR 呼び出しは1回）。
- OCR後の座標は外接矩形オフセットを加算して元スクリーン座標へ戻す。
- OCR行は `OcrLine.OwnerRoiIndex`（中心点ベース）を付与し、行結合は同一 `OwnerRoiIndex` 内のみ許可する。
- ROI描画制約（MUST）:
- 新規ROIは既存ROIと高重複（例: IoU >= 0.15）なら追加拒否。
- 近接しすぎる場合は `minGapPx`（例: 8px）を保つ位置までクランプ。
- 既存ROIの内側に完全包含されるROIは拒否。
- 操作補助（SHOULD）:
- `Undo Last` / `Clear All` / `登録完了` / `キャンセル`。
- `Shift` 押下時のみ近接クランプ解除（重複禁止は維持）。

7. **実装手順（ステップ分割）**
- Step 1: `AppSettings` に ROIセット構造を追加し、旧ROIからの移行処理を実装。
- Step 2: 固定ROIとROIセットのモード切替を追加（相互に独立設定として扱う）。
- Step 3: ROI選択UIを複数描画対応へ拡張（描画中枠表示、Undo/Clear/完了）。
- Step 4: 描画時の重複/近接ガード（IoU + minGap）を実装。
- Step 5: セット保存・セット切替UI（最低限: コンボ + 保存/更新）を実装。
- Step 6: Pipelineで `EnableRoi` / `RoiMode` / ROI数に応じて入力経路を分岐（ROI数1は従来切り抜き、ROI数2以上は外接矩形切り抜き + ROI外黒塗り1枚OCR）。
- Step 6.5: ROI数2以上経路で、OCR結果座標へ外接矩形オフセットを戻す。
- Step 7: `OcrLine` に `OwnerRoiIndex` を追加し、OCR結果へ所属情報を付与。
- Step 8: 行結合を同一 `OwnerRoiIndex` 内に制限（ROI跨ぎ結合禁止）。
- Step 9: 手動検証（代表ゲーム画面）とビルド確認。

8. **非機能要件チェック**
- 性能:
- OCR呼び出しは1回維持のため、ROI数増加時の増分は主に外接矩形生成とマスク生成コスト。
- セキュリティ:
- 外部I/Oなし（設定保存のみ）。
- 可観測性:
- セットID、ROI数、入力経路（切り抜き/黒塗り）、重複拒否回数、ROI跨ぎ結合ブロック件数をログ化。
- 互換性:
- 旧単一ROIは自動移行し、既存利用者を壊さない。

9. **リスクと緩和策**
- Risk: ROI境界ぎりぎりの文字が黒塗りで欠ける可能性。
- Mitigation: 初期は余白なし方針を採用し、問題が出た場合のみエンジン別に余白設定を追加検討する。
- Risk: 外接矩形切り抜き後の座標戻しミスでOverlay位置がずれる可能性。
- Mitigation: オフセット加算を共通化し、DoDで位置一致を確認する。
- Risk: 近接制約が強すぎて必要ROIが作れない。
- Mitigation: `minGapPx` を保守的初期値にし、`Shift` で一時解除を許可。
- Risk: ROIが近い場合に行結合で誤連結する可能性。
- Mitigation: `OwnerRoiIndex` を導入し、ROI跨ぎ結合を禁止する。
- Risk: ROI数1でも黒塗り経路を通すと既存挙動との差分が増える。
- Mitigation: ROI数1は従来切り抜き経路を維持し、既存チューニングを活かす。
- Risk: 旧設定との整合不備。
- Mitigation: 起動時マイグレーション + ログ + 失敗時フォールバック（単一ROI）。

10. **影響範囲（変更ファイル候補）**
- `Models/AppSettings.cs` — ROIセット設定とROIモード設定の追加。
- `Models/OcrLine.cs` — `OwnerRoiIndex` 追加。
- `Services/SettingsService.cs` — 旧設定からROIセットへの移行処理。
- `UI/RoiSelectorWindow.xaml(.cs)` — 複数ROI描画・枠表示・登録完了操作。
- `MainWindow.xaml(.cs)` — ROIモード選択、ROIセット編集/切替UI、保存処理。
- `Services/PipelineOrchestrator.cs` — 外接矩形切り抜き + ROI外黒塗り画像生成 + 座標オフセット復元経路。
- `Services/OcrLineGrouper.cs` — ROI跨ぎ結合禁止（OwnerRoiIndexゲート）対応。

11. **Definition of Done**
- [ ] 固定ROI機能は従来どおり動作する。
- [ ] 複数ROIを1セットとして登録・保存できる。
- [ ] セット切替が反映され、OCR対象領域が切り替わる。
- [ ] 描画時に重複/近接ガードが動作する。
- [ ] `EnableRoi=false` のとき ROIは適用されない。
- [ ] `RoiMode=Set` かつ ROI数1のときは従来切り抜き経路でOCRが実行される。
- [ ] `RoiMode=Set` かつ ROI数2以上のとき、外接矩形切り抜き + ROI外黒塗り（余白なし）の1枚画像でOCRが実行される。
- [ ] ROI数2以上経路で、OCR結果の座標が元スクリーン座標へ正しく復元される。
- [ ] ROI跨ぎ行結合が発生しない。
- [ ] 旧単一ROI設定が起動時に `Default` セットへ自動移行される。
- [ ] `dotnet build Hotkey-Translator.sln` が成功する。
