# ROIセット登録・複数ROI描画・セット切替 実装案

1. **概要（1–3行）**
- 旧 `EnableFixedRoiOverlay` は段階廃止し、ゲーム向けに「複数ROIを1セットとして登録・切替」できる運用へ統合する。
- ROI選択は `RoiMode=Single` では1矩形のみ、`RoiMode=Set` では複数矩形の手動描画を許可し、描画中は枠を可視化して登録確定する。
- 重なり防止は描画時に適用し、既存ROI近傍までで引けるように制約する。
- OCR実行は複数回に分割せず、`RoiMode=Single` は既存単一ROI経路、`RoiMode=Set` はROI集合の外接矩形で切り抜いた1枚画像を従来パイプラインへ渡す。

2. **ゴール / 非ゴール**
- ゴール:
- ユーザーが複数ROIを1セットとして保存できる。
- セット切替で対象領域を即時変更できる。
- ROI作成時の重なり・近接ミスを抑制できる。
- 旧固定ROI表示フラグを `OverlayLayoutMode` へ統合し、表示分岐を簡素化できる。
- 非ゴール:
- 複数セット同時OCR（初期実装では行わない）。
- ROIごとの複数OCR実行（初期実装では行わない）。
- OCR検出精度改善そのもの。
- 複数ウィンドウ同時追従の高度制御。

3. **前提・仮定**
- 既存は単一ROI中心で、旧 `EnableFixedRoiOverlay` 分岐が残っている。
- ROI入力元（Single/Set）と表示モード（PerBox/PerRoiAggregate）を分離し、表示制御を `OverlayLayoutMode` に統合する。
- `RoiMode=Single` は既存単一ROIパイプラインを維持し、複数ROI入力を受け付けない。
- `RoiMode=Single` の非固定Overlay描画・翻訳送信順は、既存の読順ロジック（書字方向判定後の行順）に従う。
- `RoiMode=Set` の `OverlayLayoutMode=PerBox` 時の描画・翻訳送信順は、ROI間はセット内の登録順、各ROI内は既存の読順ロジックに従う。
- `RoiMode=Set` の `OverlayLayoutMode=PerRoiAggregate` 時は ROIごとに1ブロック集約表示し、ROI間は登録順に従う。
- 設定の source of truth は `RoiSets + ActiveRoiSetId + RoiMode + OverlayLayoutMode` とし、旧 `Roi/NormalizedRoi/EnableFixedRoiOverlay` は互換読込専用とする。

4. **現状整理**
- ROIは `AppSettings.Roi / NormalizedRoi` を主に使用している。
- ROI選択UIは単発矩形選択を前提。
- 旧 `EnableFixedRoiOverlay` があり、表示ロジックに分岐が残っている。

5. **提案アーキテクチャ**
- コンポーネント構成:
- `AppSettings` に ROIセット構造（複数セット + アクティブID）を追加。
- `RoiSelectorWindow` を `RoiMode` に応じた描画モード（Single: 単一矩形 / Set: 複数矩形）に拡張。
- `PipelineOrchestrator` は `RoiMode=Single` では既存単一ROI経路を維持し、`RoiMode=Set` ではアクティブROIセットからマスク画像を1枚生成してOCRを1回だけ実行する。
- Overlay表示は `OverlayLayoutMode` で制御し、`PerBox`（従来）と `PerRoiAggregate`（ROI単位集約）を切り替える。
- データフロー / シーケンス:
1. ユーザーが「ROIセット編集」を開始
2. `RoiMode=Set` の場合はROIを複数描画（描画中は可視枠表示）
3. `登録完了` で1セット保存
4. アクティブセット切替で次回OCRから適用
5. `RoiMode=Single` のときは既存単一ROI切り抜き経路をそのまま実行
6. `RoiMode=Set` かつ ROI数2以上のとき、ROI集合の外接矩形で切り抜き
7. 切り抜き画像のうちROI外を黒塗りしてOCRへ投入
8. OCR結果はROI所属を判定し、行結合は同一ROI内だけ許可
- 既存パターン整合:
- 単一ROIは「1要素セット」として互換移行。
- 旧 `EnableFixedRoiOverlay` は互換読込のみ残し、起動時に `true -> PerRoiAggregate / false -> PerBox` へマップする。

6. **インターフェース設計**
- 新規設定（案）:
- `RoiSets: List<RoiSetConfig>`
- `ActiveRoiSetId: string?`
- `OverlayLayoutMode: PerBox | PerRoiAggregate`
- `RoiSetConfig`:
- `Id: string`
- `Name: string`
- `Rects: List<NormalizedRect>`
- `CreatedAtUtc: string` / `UpdatedAtUtc: string`
- 運用モード（案）:
- `RoiMode: Single | Set`
- `RoiSet` は `RoiMode=Set` のときのみ有効
- `OverlayLayoutMode` は描画モード設定（OCR入力モードとは独立）
- 互換移行:
- 既存 `NormalizedRoi` がある場合は、起動時に `Default` セットへ自動移行する。
- 旧単一ROI設定は互換読込のみ行い、保存時は新形式（`RoiSets + ActiveRoiSetId + RoiMode + OverlayLayoutMode`）へ正規化して書き戻す。
- 旧 `EnableFixedRoiOverlay=true` は、初回起動時に `OverlayLayoutMode=PerRoiAggregate` へ移行して保存する。
- 旧 `EnableFixedRoiOverlay=false` は、`OverlayLayoutMode=PerBox` へ移行して保存する。
- モード前提（MUST）:
- `EnableRoi=false` の場合は単一ROI/ROIセットともに無効（ROI未使用）。
- `RoiMode=Set` は `EnableRoi=true` を前提とする。
- `RoiMode=Single` では ROI選択UIは1矩形のみ許可し、追加描画は受け付けない。
- `RoiMode=Single` では OCR入力経路は既存単一ROI切り抜き（黒塗りなし）をそのまま使う。
- ROI有効/無効判定は `EnableRoi` に従い、表示レイアウトは `OverlayLayoutMode` のみで制御する。
- ROIセットOCRポリシー（MUST）:
- `RoiMode=Set` かつ ROI数が2以上のときは、まずROI集合の外接矩形を切り抜く。
- 切り抜き後画像の ROI外を黒塗りする（余白拡張なし、ROI境界そのまま）。
- `RoiMode=Set` かつ ROI数が1のときは黒塗りを行わず、従来どおり単一ROI切り抜きでOCRへ渡す。
- OCR投入画像は1枚のみ（複数ROIであっても OCR 呼び出しは1回）。
- OCR後の座標は外接矩形オフセットを加算して元スクリーン座標へ戻す。
- OCR行は `OcrLine.OwnerRoiIndex` を付与し、行結合は同一 `OwnerRoiIndex` 内のみ許可する。
- `OwnerRoiIndex` 判定ルール:
- 第1候補: 行矩形中心点が含まれるROI。
- 第2候補: 含まれない場合は IoU 最大のROI。
- 同点時: ROI登録順の早い方を採用。
- 最小IoU閾値未満は `OwnerRoiIndex=-1` とし、行結合対象から除外する（表示/翻訳対象からも除外）。
- 並び順ルール（MUST）:
- 最終表示順・翻訳送信順は `OwnerRoiIndex`（ROI登録順）→ ROI内読順 で確定する。
- 1枚OCRの生順序は採用せず、最終出力直前に上記順へ再整列する。
- ROI描画制約（MUST）:
- 新規ROIは既存ROIと高重複（例: IoU >= 0.15）なら追加拒否。
- 近接しすぎる場合は `minGapPx`（例: 8px）を保つ位置までクランプ。
- 既存ROIの内側に完全包含されるROIは拒否。
- `RoiMode=Single` では重複/近接判定は不要（複数描画自体を禁止）。
- Scene-change watcher 適用（MUST）:
- `RoiMode=Single` および `RoiMode=Set` かつ ROI数1 は従来ROI切り抜きで判定する。
- `RoiMode=Set` かつ ROI数2以上は、OCR入力と同じ「外接矩形 + ROI外黒塗り」の1枚画像で判定する。
- 操作補助（SHOULD）:
- `Undo Last` / `Clear All` / `登録完了` / `キャンセル`。
- `Shift` 押下時のみ近接クランプ解除（重複禁止は維持）。
- 表示モード切替（`PerBox` / `PerRoiAggregate`）を設定UIに提供する。

7. **実装手順（ステップ分割）**
- Step 1: `AppSettings` に ROIセット構造を追加し、旧ROIからの移行処理を実装。
- Step 2: ROI入力元のモード切替（`RoiMode=Single|Set`）を追加する。
- Step 2.5: `OverlayLayoutMode` を導入し、旧 `EnableFixedRoiOverlay` を起動時に新モードへ移行する（互換読込のみ残す）。
- Step 3: ROI選択UIを `RoiMode` 対応へ拡張（Singleは単一描画のみ、Setは複数描画 + 枠表示 + Undo/Clear/完了）。
- Step 4: `RoiMode=Set` 時の描画で重複/近接ガード（IoU + minGap）を実装。
- Step 5: セット保存・セット切替UI（最低限: コンボ + 保存/更新）を実装。
- Step 6: Pipelineで `EnableRoi` / `RoiMode` / ROI数に応じて入力経路を分岐（ROI数1は従来切り抜き、ROI数2以上は外接矩形切り抜き + ROI外黒塗り1枚OCR）。
- Step 6.5: ROI数2以上経路で、OCR結果座標へ外接矩形オフセットを戻す。
- Step 7: `OcrLine` に `OwnerRoiIndex` を追加し、OCR結果へ所属情報を付与。
- Step 8: 行結合を同一 `OwnerRoiIndex` 内に制限（ROI跨ぎ結合禁止）。
- Step 8.5: Overlay構築を `OverlayLayoutMode` 分岐へ置換（`PerBox` は従来、`PerRoiAggregate` はROI単位集約）。
- Step 8.6: `EnableFixedRoiOverlay` のUI項目を撤去し、表示モードUIへ置換。
- Step 8.7: 最終表示順・翻訳送信順を `OwnerRoiIndex`（登録順）→ ROI内読順 へ再整列する。
- Step 8.8: scene-change watcher のROI判定入力を `RoiMode`/ROI数に応じて分岐する（Set複数は外接矩形+黒塗り）。
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
- 旧 `EnableFixedRoiOverlay` は起動時マッピングで新 `OverlayLayoutMode` へ移行し、設定破壊を避ける。

9. **リスクと緩和策**
- Risk: ROI境界ぎりぎりの文字が黒塗りで欠ける可能性。
- Mitigation: 初期は余白なし方針を採用し、問題が出た場合のみエンジン別に余白設定を追加検討する。
- Risk: 外接矩形切り抜き後の座標戻しミスでOverlay位置がずれる可能性。
- Mitigation: オフセット加算を共通化し、DoDで位置一致を確認する。
- Risk: 近接制約が強すぎて必要ROIが作れない。
- Mitigation: `minGapPx` を保守的初期値にし、`Shift` で一時解除を許可。
- Risk: ROIが近い場合に行結合で誤連結する可能性。
- Mitigation: `OwnerRoiIndex` を導入し、ROI跨ぎ結合を禁止する。
- Risk: `RoiMode=Single` なのに複数ROI相当のデータが流入して経路がぶれる可能性。
- Mitigation: UIで複数描画を禁止し、保存時バリデーションでSingleは1ROIのみ許可する。
- Risk: ROI数1でも黒塗り経路を通すと既存挙動との差分が増える。
- Mitigation: ROI数1は従来切り抜き経路を維持し、既存チューニングを活かす。
- Risk: 旧設定との整合不備。
- Mitigation: 起動時マイグレーション + ログ + 失敗時フォールバック（単一ROI）。
- Risk: 固定ROI廃止により既存ユーザーが表示仕様変更を混乱する可能性。
- Mitigation: 互換移行時に `EnableFixedRoiOverlay` 値を `OverlayLayoutMode` へ写像し、初回だけ移行ログを表示する。

10. **影響範囲（変更ファイル候補）**
- `Models/AppSettings.cs` — ROIセット設定とROIモード設定の追加。
- `Models/OcrLine.cs` — `OwnerRoiIndex` 追加。
- `Services/SettingsService.cs` — 旧設定からROIセットへの移行処理。
- `UI/RoiSelectorWindow.xaml(.cs)` — 複数ROI描画・枠表示・登録完了操作。
- `MainWindow.xaml(.cs)` — ROIモード選択、ROIセット編集/切替UI、`OverlayLayoutMode` 選択、保存処理。
- `Services/PipelineOrchestrator.cs` — 外接矩形切り抜き + ROI外黒塗り画像生成 + 座標オフセット復元 + Overlay構築分岐更新。
- `UI/OverlayWindow.xaml(.cs)` — `PerRoiAggregate` 表示モードでの描画整形。
- `Services/OcrLineGrouper.cs` — ROI跨ぎ結合禁止（OwnerRoiIndexゲート）対応。

11. **Definition of Done**
- [ ] 複数ROIを1セットとして登録・保存できる。
- [ ] セット切替が反映され、OCR対象領域が切り替わる。
- [ ] 描画時に重複/近接ガードが動作する。
- [ ] `EnableRoi=false` のとき ROIは適用されない。
- [ ] `RoiMode=Single` のとき、ROI選択UIで複数描画ができない。
- [ ] `RoiMode=Single` のとき、OCRは既存単一ROI切り抜き経路（黒塗りなし）で実行される。
- [ ] `RoiMode=Set` かつ ROI数1のときは従来切り抜き経路でOCRが実行される。
- [ ] `RoiMode=Set` かつ ROI数2以上のとき、外接矩形切り抜き + ROI外黒塗り（余白なし）の1枚画像でOCRが実行される。
- [ ] ROI数2以上経路で、OCR結果の座標が元スクリーン座標へ正しく復元される。
- [ ] ROI跨ぎ行結合が発生しない。
- [ ] `OwnerRoiIndex` の割当が仕様どおり（中心点優先→IoU最大→登録順タイブレーク、閾値未満は -1 除外）に動作する。
- [ ] `OverlayLayoutMode=PerBox` / `PerRoiAggregate` の両方で、表示順が `ROI登録順 -> ROI内読順` を満たす。
- [ ] 翻訳送信順が表示順と一致し、`ROI登録順 -> ROI内読順` を満たす。
- [ ] 旧単一ROI設定が起動時に `Default` セットへ自動移行される。
- [ ] 旧 `EnableFixedRoiOverlay` が初回起動時に `OverlayLayoutMode` へ移行され、次回以降は新モードのみで動作する。
- [ ] `OverlayLayoutMode=PerBox` と `OverlayLayoutMode=PerRoiAggregate` の表示差分が意図どおり確認できる。
- [ ] scene-change watcher が `RoiMode`/ROI数に応じて正しい入力経路（単一切り抜き / 外接矩形+黒塗り）を使う。
- [ ] `dotnet build Hotkey-Translator.sln` が成功する。
