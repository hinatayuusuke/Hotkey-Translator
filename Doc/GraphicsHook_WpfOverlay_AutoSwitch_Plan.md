# GraphicsHook 実行時の WPF Overlay 自動抑止 実装案

1. **概要（1–3行）**
Hook パイプラインが実際に有効動作している間だけ、通常の WPF オーバーレイ描画を自動で抑止する。
Hook 未接続時やフォールバック時は WPF オーバーレイへ自動復帰させ、表示消失を防ぐ。

2. **ゴール / 非ゴール**
- ゴール
  - Hook V2 描画と WPF 描画の二重表示を防ぐ。
  - Hook 稼働状態に応じて WPF オーバーレイを自動 ON/OFF する。
  - ユーザーの手動 Overlay トグル（F9）との整合を保つ。
- 非ゴール
  - 今回は Hook V1 IPC 削除や HookHost プロトコル改修は行わない。
  - SceneChange ロジック自体の仕様変更は行わない。

3. **前提・仮定**
- テキスト/半透明背景の本番描画は Hook V2 で満たせる。
- Hook が未接続・失敗・フォールバック中は WPF 側表示を維持する必要がある。
- 判定は「設定ON」ではなく「実行状態（Attached + GraphicsHook 稼働）」を優先する。

4. **現状整理**
- Services/PipelineOrchestrator.cs
  - WPF 更新（OverlayStage.Update）と Hook 更新（TryUpdateDx11HookOverlayV2）を同一フローで実行している。
- Services/Application/MainWindowRunCoordinator.cs
  - 実行開始時に EnableOverlay() を呼び、WPF overlay を有効化する。
- MainWindow.xaml.cs
  - _overlayEnabled が手動トグル状態を保持し、Hook runtime config publish とも連動している。

5. **提案アーキテクチャ**
- コンポーネント構成
  - PipelineOrchestrator に「WPF描画を抑止すべきか」の判定関数を追加する。
  - 判定結果は Run 単位で算出し、WPF更新をスキップ、Hook V2 更新は継続する。
- データフロー / シーケンス
  1. Frame capture 後に frame.ProviderKind == GraphicsHook か判定。
  2. EnableDx11HookPipeline && Dx11HookOverlayEnabled を満たし、かつ provider が GraphicsHook の場合は WPF 描画を抑止。
  3. 抑止時は WPF overlay を ClearOverlay()（または showLast しない）で非表示維持。
  4. provider が GraphicsHook 以外へ切り替わったら WPF 描画を自動復帰。
- 既存パターンへの整合
  - 手動トグル _overlayEnabled は「ユーザー意図の上位状態」として維持。
  - 自動抑止は runtime の追加条件として合成する。

6. **インターフェース設計**
- 変更対象（候補）
  - Services/PipelineOrchestrator.cs
    - ShouldSuppressWpfOverlay(CaptureFrame frame, AppSettings settings) を追加。
    - overlay 更新箇所で WPF 更新を条件分岐。
  - Services/Application/MainWindowRunCoordinator.cs
    - 既存 EnableOverlay() は維持（ユーザー状態復元の責務）。
- 入出力/エラー/バリデーション
  - 入力: CaptureFrame.ProviderKind, AppSettings.EnableDx11HookPipeline, AppSettings.Dx11HookOverlayEnabled, 手動 overlay state。
  - 出力: WPF overlay を更新するか否か（bool）。
  - 異常時: 判定不能/例外時は安全側として WPF 描画を許可（表示消失回避）。

7. **実装手順（ステップ分割）**
- Step 1: PipelineOrchestrator に WPF 抑止判定メソッドを追加。
- Step 2: 通常 Run と RunWithReadingUnitsAsync の両方で WPF 更新を条件分岐。
- Step 3: 抑止時に WPF overlay を明示クリアし、Hook V2 だけ更新する。
- Step 4: ログ追加（例: stage=overlay_route mode=hook_only|wpf_only）で経路可視化。
- Step 5: 手動トグル（F9）・固定窓 lock/unlock・Hook attach/detach の回帰確認。

8. **非機能要件チェック**
- 性能
  - Hook 稼働時に WPF 描画を止めるため、UI 側オーバーヘッドを削減。
- セキュリティ
  - 新規外部I/Fなし。既存 Hook IPC のみ利用。
- 可観測性
  - overlay 経路ログを追加し、二重描画や表示消失の切り分けを容易化。
- 互換性
  - Hook 未接続/失敗時は従来どおり WPF を使うため機能後退を抑制。

9. **リスクと緩和策**
- Risk: 抑止条件が広すぎると Hook 未描画時に表示が消える。
- Mitigation: 判定を provider=GraphicsHook に限定し、例外時は WPF 許可へフォールバック。
- Risk: 手動トグルとの競合で状態が分かりにくくなる。
- Mitigation: UI/ログで「手動OFF」と「Hook自動抑止」を区別して出す。

10. **影響範囲**
- 変更ファイル候補
  - Services/PipelineOrchestrator.cs
  - （必要なら）Services/Application/MainWindowRunCoordinator.cs
  - （必要なら）MainWindow.xaml.cs（状態表示ログのみ）
- 移行
  - なし（設定項目追加なしを想定）。
- ドキュメント
  - 本ファイルを実装計画として追加。

11. **Definition of Done**
- Hook 稼働中（GraphicsHook provider）に WPF overlay が表示されない。
- Hook 非稼働/フォールバック時に WPF overlay が自動復帰する。
- F9 手動トグルと競合せず、期待どおり ON/OFF できる。
- dotnet build Hotkey-Translator.sln -c Release が成功する。
- 主要操作（Run/F8/F10/F11、lock/unlock、scene auto）で表示回帰がない。
