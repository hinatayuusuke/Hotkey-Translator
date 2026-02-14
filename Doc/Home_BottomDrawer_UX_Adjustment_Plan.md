# Home Bottom Drawer UX Adjustment Plan

1. **概要（1–3行）**
- 本計画は、下部Drawerの操作モデルを `Drawer/Preview/Log` 3ボタン方式から `Preview/Log` 2ボタン方式へ整理する。
- `Preview` または `Log` を表示する時は Drawer を自動表示し、両方非表示時は Drawer を自動で閉じる。
- Drawer表示時に上部設定画面を圧縮しすぎないため、Window高さを安全に自動拡張する。

2. **ゴール / 非ゴール**
### ゴール
- ユーザーが「見たいペインを選ぶだけ」で動作する直感的操作へ変更する。
- Drawer表示時に上側コンテンツへ被さる/押し上げる圧迫感を減らす。
- 既存の Preview 最新保持・ログ欠落許容・settings非永続方針を維持する。

### 非ゴール
- OCR/翻訳パイプラインの挙動変更。
- Drawer比率の永続化追加。
- UIテーマ/配色の刷新。

3. **前提・仮定**
- 現状は `IsBottomPanelOpen`, `BottomPreviewPaneVisible`, `BottomLogPaneVisible` が導入済み。
- 現状ステータスバーに `Drawer/Preview/Log` の3ボタンが存在する。
- Drawerの表示切替は ViewModel の状態遷移で制御可能。

4. **現状整理**
- `Drawer` ボタンと `Preview/Log` ボタンが分離しており、ユーザーがどれを押せばよいか迷いやすい。
- Drawerを開いた時、上部が圧縮されるため設定操作時の視認性が下がる。
- 複数モニタや小さい画面で、単純な固定高さ確保はレイアウト破綻リスクがある。

5. **提案アーキテクチャ**
### 5.1 操作モデル（UX）
- ボタンは `Preview` / `Log` の2つだけにする。
- `Preview` ON: `BottomPreviewPaneVisible=true` かつ `IsBottomPanelOpen=true`。
- `Log` ON: `BottomLogPaneVisible=true` かつ `IsBottomPanelOpen=true`。
- `Preview=false` かつ `Log=false` になったら `IsBottomPanelOpen=false`。
- `Drawer` 単独ON/OFF概念は廃止（派生状態にする）。

### 5.2 Window高さ自動拡張
- Drawer closed -> open の遷移時のみ、Window高さを増加。
- 増加量は「Drawer実表示高さ + 下マージン」の実測値を基準にする。
- 画面外にはみ出さないよう `WorkingArea` で上限クランプ。
- Drawer open -> closed では、直前に自動拡張した分のみ戻す（ユーザー手動リサイズは尊重）。

6. **インターフェース設計**
### 6.1 ViewModel状態ルール
- 既存 `IsBottomPanelOpen` は保持するが、原則 `Preview/Log` から導出する。
- 不変条件:
  - `BottomPreviewPaneVisible || BottomLogPaneVisible` の時は `IsBottomPanelOpen=true`。
  - `!BottomPreviewPaneVisible && !BottomLogPaneVisible` の時は `IsBottomPanelOpen=false`。
- `ToggleBottomPanelCommand` は削除（または非公開化）。

### 6.2 Window自動拡張状態（MainWindow側）
- 追加候補フィールド:
  - `_autoDrawerHeightDelta`（最後に自動加算した高さ）
  - `_drawerAutoExpanded`（自動拡張中フラグ）
  - `_suppressAutoResize`（再入防止）
- WHY: 自動調整とユーザー手動リサイズを区別し、閉じる時の過剰縮小を防ぐため。

7. **実装手順（ステップ分割）**
- Step 1: ボタン整理
  - `MainWindow.xaml` の `Drawer` ボタンを削除。
  - `Preview` / `Log` ボタンのみ残す。

- Step 2: ViewModel状態遷移整理
  - `ToggleBottomPanelCommand` を削除。
  - `Preview/Log` 切替時に `IsBottomPanelOpen` が自動で導出されるよう変更。
  - 両方OFF時は Drawer を閉じる。

- Step 3: Window高さ自動拡張
  - `MainWindow.xaml.cs` で `IsBottomPanelOpen` 変化を監視。
  - open遷移時: `Height` を安全に増加（WorkingAreaクランプ）。
  - close遷移時: 自動で増やした分のみ戻す。

- Step 4: 境界条件対応
  - 最大化状態では自動拡張を無効化。
  - 画面下端固定時に位置補正（必要なら `Top` 調整）を実装。
  - 手動リサイズ後に閉じても異常縮小しないことを確認。

- Step 5: 回帰確認
  - Run/ForceRun中のログ追従。
  - Preview更新（最新のみ保持）。
  - サイドパネル切替中のDrawer挙動。

8. **非機能要件チェック**
- UX
  - 操作ボタンを2つに統一し、状態理解コストを下げる。
- 互換性
  - settings.json への新規永続化は追加しない。
- 性能
  - Preview最新保持の方針を維持し、描画過負荷を増やさない。
- 可観測性
  - open/close時の自動拡張量を debug ログ出力可能にする（任意）。

9. **リスクと緩和策**
- Risk: 自動拡張と手動リサイズが競合し、Windowサイズが意図せず変化する。
- Mitigation: 自動変更分を追跡し、戻し処理で「自動加算分のみ」を対象にする。

- Risk: 複数モニタ/DPIで WorkingArea 計算ミスが発生する。
- Mitigation: 現在Windowが所属する画面の作業領域を使用し、クランプ後サイズで適用する。

- Risk: 最大化時に高さ変更ロジックが不要に走る。
- Mitigation: `WindowState == Maximized` では処理をスキップする。

10. **影響範囲**
- `MainWindow.xaml` — Drawerボタン削除、Preview/Logボタンのみの導線へ変更。
- `ViewModels/MainWindowViewModel.cs` — Drawer導出ロジックに統一、不要コマンド削除。
- `MainWindow.xaml.cs` — Drawer開閉時のWindow高さ自動拡張/復元ロジック追加。
- `Doc/Home_SidePanel_BottomDrawer_UI_Plan.md` — 必要に応じて最終仕様を統合更新。

11. **Definition of Done**
- [ ] ステータスバーに `Preview` / `Log` の2ボタンのみ存在する。
- [ ] `Preview` または `Log` をONにすると Drawer が自動表示される。
- [ ] `Preview` と `Log` を両方OFFにすると Drawer が自動非表示になる。
- [ ] Drawer表示時、上部設定領域が過度に圧縮されず、Window高さが安全に拡張される。
- [ ] 最大化時に不自然なサイズ変更が発生しない。
- [ ] `dotnet build Hotkey-Translator.csproj -p:UseAppHost=false` が成功する。
