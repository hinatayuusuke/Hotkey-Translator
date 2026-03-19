# Fixed ROI Overlay Custom Frame Implementation Plan

1. **概要（1–3行）**
`Fixed ROI overlay mode` の表示先を切り替え可能にし、従来どおり ROI を使う経路と、ユーザーが矩形描画で決めた表示枠を使う経路を両立する。
`ユーザ描画領域` は ROI と同じ `NormalizedRect` で保存し、WPF overlay と GraphicsHook overlay の両方で同じ表示位置を再現する。

2. **ゴール / 非ゴール**
- ゴール
  - `Fixed ROI overlay mode` の表示先を `ROI` と `ユーザ描画領域` で切り替えられるようにする。
  - `ユーザ描画領域` は ROI と同じ矩形選択 UI で設定できる。
  - `表示先 = ROI` では既存動作を維持する。
  - `表示先 = ユーザ描画領域` のときだけ、集約テキストを ROI ではなく表示枠へ描画する。
  - 表示枠の枠線を選択中と選択直後の短時間 preview で確認できる。
  - 表示枠を選択する専用 hotkey を追加する。
- 非ゴール
  - ROI preset との連動。
  - 複数の fixed overlay 枠保存。
  - 自由配置アンカーや複数配置ルールの追加。
  - ROI の OCR 対象範囲そのものの仕様変更。
  - 表示枠の常時枠線表示。

3. **前提・仮定**
- 現在の fixed overlay は ROI 矩形をそのまま集約表示先として使っている。
- ROI 選択 UI は `RoiSelectorWindow` と既存の矩形 preview 経路を再利用できる。
- 表示枠は capture bounds 基準で保存し、絶対座標は永続化しない。
- 既存ユーザー影響を抑えるため、fixed overlay の表示先デフォルトは現行互換の `ROI` とする。
- Hook fullscreen 利用時も表示位置を一致させる必要があるため、WPF 専用実装にはしない。

4. **現状整理**
- 現行挙動
  - `EnableFixedRoiOverlay` が ON のとき、`OverlayStage.BuildItems` は OCR 結果を 1 つの `OverlayItem` に集約し、その描画矩形に `roiScreen` を使う。
  - ROI 枠線は `ShowRoiPreview` による一時 preview 専用で、常時表示状態は持っていない。
  - Hook overlay でも ROI preview は描けるが、`Wrap=2` の予約用途は一時 preview 前提である。
- 現状の課題
  - OCR 対象 ROI と翻訳テキストの表示位置を分離できない。
  - ゲームの字幕位置と見やすい翻訳表示位置が異なるケースに対応できない。
  - ROI preview の仕組みは一時表示前提なので、固定表示枠も preview 用として扱う方が自然である。
  - 既存の fixed overlay 利用者向けには、従来どおり ROI を表示先に使う経路を残す必要がある。

5. **提案アーキテクチャ**
- コンポーネント構成
  - `Models/AppSettings.cs`
    - fixed overlay の表示先モードと表示枠設定値を保持する。
  - `MainWindow.xaml.cs`
    - 表示枠選択 UI と hotkey の入口を持つ。
  - `Services/Application/HotkeyCommandController.cs`
    - 表示枠選択 hotkey の実行入口を追加する。
  - `Services/Orchestration/Stages/OverlayStage.cs`
    - fixed overlay 時の表示矩形を、設定された表示先モードに応じて決定する。
  - `Services/OverlayPresenter.cs` / `UI/OverlayWindow.xaml.cs`
    - fixed overlay frame の一時 preview を描画する。
  - `Services/PipelineOrchestrator.cs`
    - Hook overlay 側へ preview frame を反映する。
- データフロー / シーケンス
  1. ユーザーが UI または hotkey で `Select Fixed Overlay Frame` を起動する。
  2. `RoiSelectorWindow` で capture bounds 上に矩形を描画する。
  3. 確定時、選択矩形を `NormalizedRect` に変換して settings に保存する。
  4. fixed overlay mode が有効な場合、`FixedOverlayPlacementMode` を見て表示先を決定する。
  5. `ROI` が選ばれている場合は従来どおり ROI 矩形へ描画する。
  6. `ユーザ描画領域` が選ばれていて表示枠が有効な場合は、その矩形へ描画する。
  7. `ユーザ描画領域` が選ばれているが未設定の場合は ROI へ fallback する。
  8. WPF / Hook は選択中と選択直後の短時間 preview だけ frame を描画する。
- 既存パターンへの整合
  - OCR 対象範囲は従来どおり `EnableRoi` / `NormalizedRoi` を使う。
  - 表示位置だけを独立設定へ切り出すため、OCR・scene change・ROI preset には影響を広げない。
  - 既存動作の互換は `FixedOverlayPlacementMode = Roi` で維持する。

6. **インターフェース設計**
- `AppSettings` 追加項目案
  - `FixedOverlayPlacementMode FixedOverlayPlacementMode = FixedOverlayPlacementMode.Roi`
  - `NormalizedRect? FixedOverlayNormalizedRect`
  - `string HotkeySelectFixedOverlayFrameKey`
  - `string HotkeySelectFixedOverlayFrameModifiers`
- `FixedOverlayPlacementMode` 案
  - `Roi`
  - `CustomFrame`
- 運用ルール
  - fixed overlay が ON かつ `FixedOverlayPlacementMode == Roi` の場合は、従来どおり ROI 矩形を表示先として使う。
  - fixed overlay が ON かつ `FixedOverlayPlacementMode == CustomFrame` で `FixedOverlayNormalizedRect` が有効な場合は、その矩形を表示先として使う。
  - `FixedOverlayPlacementMode == CustomFrame` でも表示枠が未設定なら、互換動作として ROI 矩形へ fallback する。
  - 表示枠の選択は ROI と同じく fail fast にし、capture bounds が取れない場合は保存しない。
  - 表示枠の枠線表示は選択中と選択直後の短時間 preview のみとする。
- UI 案
  - Overview に fixed overlay 関連の主設定を集約する。
  - `Fixed ROI overlay mode` の近くに `表示先` の `ComboBox` を追加する。
  - `ComboBox` の選択肢は `ROI` / `ユーザ描画領域` の 2 つに絞る。
  - `表示先 == ユーザ描画領域` のときだけ `ユーザ描画領域を選択` ボタンを有効化する。
  - `Clear` ボタンは追加しない。
  - fixed overlay ON 時に「表示先: ROI」「表示先: ユーザ描画領域」「未設定のため現在は ROI を使用」の状態が分かる補助表示を付ける。
  - 補助文で「OCR対象ROIではなく翻訳表示位置だけを変える」と明示する。
- hotkey 案
  - `Select Fixed Overlay Frame`
  - 初期値は `Disable` を推奨する。既存 `F6` 系 ROI 操作と混同しやすいため。

7. **実装手順（ステップ分割）**
- Step 1. settings / ViewModel 追加
  - `AppSettings` に表示先 enum、表示枠設定、hotkey 設定を追加する。
  - `SettingsViewModel` に対応プロパティを追加し、Load / Save / auto-save 経路へ接続する。
  - `HotkeyDefaults` に新しい hotkey default を追加する。
- Step 2. 表示枠選択フロー追加
  - `MainWindow` に `SelectFixedOverlayFrameAsync` を追加する。
  - `RoiSelectorWindow` を流用し、確定時は `FixedOverlayNormalizedRect` を保存する。
  - 保存後は WPF / Hook の preview を短時間だけ表示する。
- Step 3. hotkey 登録追加
  - `HotkeyCommandController` に handler を追加する。
  - `MainWindow` の hotkey config / registration / event handler に新規 binding を追加する。
  - `UI/HotkeysControl.xaml` に設定行を追加する。
- Step 3.5. Overview UI 追加
  - `Fixed ROI overlay mode` の近くに `表示先` `ComboBox` を追加する。
  - `ComboBox` が `ユーザ描画領域` のときだけ `ユーザ描画領域を選択` ボタンを有効化する。
  - `Clear` は置かず、未設定時は状態表示で ROI fallback を伝える。
- Step 4. WPF overlay 表示切替
  - `OverlayStage` で fixed overlay 時の描画矩形を `FixedOverlayPlacementMode` に応じて決定する。
  - `CustomFrame` が未設定のときは ROI fallback にする。
  - `OverlayWindow` / `OverlayPresenter` に fixed overlay frame preview API を追加する。
  - 通常運用時は枠線を出さず、選択時だけ preview を出す。
- Step 5. Hook overlay 表示切替
  - `PipelineOrchestrator` に fixed overlay frame preview の publish 経路を追加する。
  - Hook overlay の既存 preview 経路を流用するか、最小差分で fixed overlay preview を追加する。
  - DX11 / DX9 / Vulkan 側で preview frame を描画する。
- Step 6. 互換性と状態同期
  - 旧 settings は `FixedOverlayPlacementMode = Roi` と同等に扱う。
  - fixed overlay ON でも表示枠未設定時は ROI fallback を維持する。
  - settings 読み込み後に表示位置だけを復元し、枠線は通常表示しない。

8. **非機能要件チェック**
- 性能
  - 追加負荷は軽微で、主に設定保存と短時間 preview のみ。
  - Hook overlay も常時ではなく preview 時だけ frame block を出す。
- セキュリティ
  - ローカル settings のみを扱い、新規の外部 I/F は増やさない。
- 可観測性
  - ログ候補
    - `fixed_overlay_frame_selected`
    - `fixed_overlay_frame_preview_shown`
    - `fixed_overlay_frame_fallback_to_roi`
    - `fixed_overlay_placement_mode_changed`
- 互換性
  - 旧 settings では `FixedOverlayPlacementMode` を `Roi` 扱いにし、従来の表示を維持する。
  - `FixedOverlayNormalizedRect` が未設定でも ROI fallback により既存動作を壊さない。
  - 既存 `EnableFixedRoiOverlay` の意味は維持し、表示先解決だけを拡張する。

9. **リスクと緩和策**
- Risk: OCR ROI と表示枠が離れすぎると、どの字幕に対する翻訳か分かりづらくなる。
- Mitigation: 未設定時は ROI fallback を維持する。
- Risk: 表示先の切替 UI が曖昧だと、ユーザーが ROI と表示枠の違いを誤解する。
- Mitigation: `ComboBox` の選択肢を 2 つに限定し、補助文で「OCR対象ROIではなく翻訳表示位置だけを変える」と明示する。
- Risk: fixed overlay 用 preview を ROI preview と雑に共用すると、選択中の意味がログや state 上で曖昧になる。
- Mitigation: preview の呼び出し元と用途をログ上で区別し、必要なら API 名を分ける。
- Risk: Hook overlay が WPF と同じ frame 表示を持たないと、fullscreen 時だけ挙動差が出る。
- Mitigation: WPF 実装と同時に Hook の preview 経路も揃え、選択時の確認手段を一致させる。

10. **影響範囲**
- 変更ファイル候補
  - `Models/AppSettings.cs`
  - `Models/HotkeyDefaults.cs`
  - `ViewModels/SettingsViewModel.cs`
  - `MainWindow.xaml.cs`
  - `Services/Application/HotkeyCommandController.cs`
  - `Services/Orchestration/Stages/OverlayStage.cs`
  - `Services/OverlayPresenter.cs`
  - `UI/OverlayWindow.xaml`
  - `UI/OverlayWindow.xaml.cs`
  - `UI/HotkeysControl.xaml`
  - `UI/HotkeysControl.xaml.cs`
  - `UI/OverviewControl.xaml`
  - `Services/PipelineOrchestrator.cs`
  - `Native/HookAgentDx11/Dx11PresentHook.cpp`
  - `Native/HookAgentDx9/Dx9PresentHook.cpp`
  - `Native/HookAgentVulkan/VulkanPresentHook.cpp`
- 移行
  - 追加設定は未設定許容にし、旧 settings は `FixedOverlayPlacementMode = Roi` と同等に扱う。
  - 既存 fixed ROI overlay ユーザーは、デフォルトで従来どおり ROI へ描画される。
  - `CustomFrame` 利用者だけが表示枠設定を追加で行う。
- ドキュメント
  - 本ファイルを実装計画として追加する。

11. **Definition of Done**
- fixed overlay ON 時、`表示先 = ROI` なら従来どおり ROI に集約翻訳が表示される。
- fixed overlay ON 時、`表示先 = ユーザ描画領域` かつ表示枠が設定済みならその矩形に集約翻訳が表示される。
- OCR ROI を変えても、表示枠が独立して維持される。
- UI と hotkey の両方から表示枠を選択できる。
- WPF overlay と Hook overlay で preview 表示位置が一致する。
- 表示枠未設定時は ROI fallback が効き、既存動作を壊さない。
- `dotnet build Hotkey-Translator.sln -c Release` が成功する。
