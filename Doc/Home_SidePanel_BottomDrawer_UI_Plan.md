# Home統合 + サイドパネル + 下部ドロワーUI 実装案

1. **概要（1–3行）**
- `Main` / `Settings` タブ構成を廃止し、`Home` に統合した上で設定群をサイドパネル化する。
- `OCRプレビュー` と `ログ` は下部ドロワー内で同時表示できる2ペイン構成（左右分割 + Splitter可変）とする。
- 段階移行で回帰リスクを抑え、既存MVVM配線とホットキー導線を維持する。

2. **ゴール / 非ゴール**
### ゴール
- 画面トップレベルの情報設計を「Home + サイドカテゴリ + 下部ドロワー」に再構成する。
- `OCRプレビュー` と `ログ` を全体共通UIにして、同時表示/個別折りたたみの両方を可能にする。
- 現在の設定保存・ホスト再起動・Run導線を壊さず移行する。

### 非ゴール
- OCR/翻訳アルゴリズムの仕様変更。
- ViewModelの全面刷新（既存MVVMを前提に最小差分で再レイアウト）。
- 初回での大規模デザイン刷新（色/テーマ大改造）。

3. **前提・仮定**
- `MainWindow.xaml` は既に MVVM バインディング中心で、`SettingsViewModel` と `RuntimeStatusViewModel` が利用可能。
- `OnOcrPreprocessPreviewReady` などプレビュー更新イベントの配線は既存で稼働している。
- ログは `UiLogController` 経由で `LogBox` へ流れているため、描画先コンテナ変更で再利用できる。
- サイドパネルは左側固定とする（右側移動は対象外）。
- プレビューは最新のみ保持し、ドロワー表示負荷に応じてフレーム間引きを許容する。
- 下部ドロワー状態（開閉・比率）は `settings.json` に保存しない。
- アプリ起動直後ログの100%表示は非要件（欠落許容）。

4. **現状整理**
- 現在は `TabControl(Main, Settings)` で、操作と設定が分断されている。
- `LogBox` は右カラム固定で、UI密度が高い場面で視線移動が大きい。
- OCRプレビューは専用導線が弱く、調整作業時に開閉性が不足。

5. **提案アーキテクチャ**
### 5.1 コンポーネント構成
- ルート: `Home` 単一画面
- 左: サイドパネル（カテゴリ切替で設定群表示）
- 中央/右: 操作コンテンツ（Capture/OCR/Translation 主要操作）
- 下: ボトムドロワー（`OCR Preview` 左ペイン + `Log` 右ペインの同時表示）

### 5.2 データフロー / シーケンス
1. ユーザー操作（Run / 設定変更）。
2. 設定は既存 `SettingsViewModel` に即時反映、debounce保存。
3. OCRプレビュー更新イベントで下部ドロワーのプレビュー領域を更新。
4. ログは既存 `UiLogController` が下部ドロワーのログ領域へ出力。
5. ユーザーは `GridSplitter` で `Preview/Log` の横幅比を調整し、必要に応じて片側のみ折りたためる。

### 5.3 既存パターンへの整合
- `SelectedSettingsCategoryIndex` など既存カテゴリ切替バインディングを流用。
- ログ更新ロジックは維持し、表示先レイアウトのみ変更。
- ホスト制御ボタン（再起動/停止）は現行配置意図を維持しつつHome内へ再配置。

6. **インターフェース設計**
### 6.1 UI状態（追加候補）
- `IsBottomPanelOpen`（bool）
- `BottomPanelHeight`（可変、Splitter対応）
- `BottomPreviewPaneVisible`（bool）
- `BottomLogPaneVisible`（bool）
- `BottomPreviewPaneWidthRatio`（double, 0.0-1.0）

不変条件（MUST）
- `IsBottomPanelOpen=false` のときは両ペイン非表示扱い（描画更新を抑止）。
- `IsBottomPanelOpen=true` かつ `BottomPreviewPaneVisible=false` かつ `BottomLogPaneVisible=false` は禁止。
  - 片側表示へ自動補正（既定: `Log=true`）。
- `BottomPreviewPaneWidthRatio` は `0.2-0.8` にクランプし、範囲外復元時は既定 `0.45` を適用。
- 上記状態はセッション内のみ保持し、`settings.json` へ永続化しない。

### 6.2 コマンド（追加候補）
- `ToggleBottomPanelCommand`
- `TogglePreviewPaneCommand`
- `ToggleLogPaneCommand`

### 6.3 表示仕様
- 下部ドロワーは常にWindow下辺に固定。
- `Preview` と `Log` は同時表示を既定とし、中央 `GridSplitter` で比率可変。
- 既定比率は `Preview 45% / Log 55%`（目安）とする。
- 各ペインは個別に折りたたみ可能（片側のみ表示可）。
- プレビューは最新フレームのみ保持し、可視時もUI負荷に応じて中間フレームを破棄できる。
- ドロワー非表示時は更新を軽量化（必要最小限のバッファ更新のみ）。
- ログは欠落許容。起動初期のバーストログで全件表示を保証しない。

7. **実装手順（ステップ分割）**
- Step 1: レイアウト骨格の再配置
  - `TabControl(Main/Settings)` を `Home` 単体構造へ置換。
  - 既存SettingsカテゴリUIをサイドパネルへ移動。

- Step 2: ボトムドロワー追加
  - `Grid.RowDefinitions` を増設し、下部にドロワー領域を追加。
  - `OCR Preview` / `Log` の2ペイン同時表示 + `GridSplitter` を実装。

- Step 3: 既存配線の接続し直し
  - `LogBox` 出力先を新ドロワー内コンテナへ移行。
  - ログ欠落許容を前提に、移行中は既存バッファ上限ポリシー（`MaxLogLines`）を維持する。
  - プレビュー更新イベントの描画先を新ドロワーへ接続。
  - プレビューは「最新のみ保持」で、連続更新時は古い更新要求を上書きする。

- Step 4: 操作性調整
  - ドロワー開閉ショートカット（任意）追加。
  - Preview/Log の個別折りたたみを追加。
  - 「両ペイン同時に折りたたみ」は禁止し、片側へ自動補正する。
  - Splitterや最小高さ制約を調整して可読性を担保。

- Step 5: 回帰確認
  - Run/ForceRun/OCR-only/ホスト再起動/停止/設定保存/ホットキーの一連動作を確認。

8. **非機能要件チェック**
- 性能
  - ドロワー非表示時の不要再描画を抑える。
- 可観測性
  - ログ表示は常時アクセス可能とし、調査導線を短縮。
- 互換性
  - 設定キー・保存形式（`settings.json`）は変更しない。
- 運用
  - 「どこに何があるか」を単純化し、設定探索コストを下げる。

9. **リスクと緩和策**
- Risk: 大規模XAML変更でレイアウト崩れが発生。
- Mitigation: Step 1/2 を分割し、各段階でビルド+手動確認を実施。

- Risk: ログ/プレビューの更新配線切替で表示欠落。
- Mitigation: 既存イベントハンドラを維持し、描画先コントロール名のみ段階置換する。ログ欠落は許容範囲として運用定義する。

- Risk: ドロワー常設で画面高さ不足（小画面）。
- Mitigation: 折りたたみ状態を既定にし、最小高さ+Splitterで調整可能にする。

- Risk: 同時表示でレンダリング/ログ更新負荷が増える可能性。
- Mitigation: プレビューは最新のみ保持し、過去フレームを破棄。非表示ペインは更新を間引き、可視ペインを優先更新する。

10. **影響範囲**
- `MainWindow.xaml` — ルートレイアウト再構成（Home統合/サイドパネル/下部ドロワー）。
- `ViewModels/MainWindowViewModel.cs` — ドロワー状態/コマンド追加（必要時）。
- `MainWindow.xaml.cs` — ログ/プレビュー描画先の再接続（最小限）。
- `Services/UIログ関連` — 参照先がコントロール名に依存していれば調整。
- `Doc/` — 画面操作ガイドの更新（必要時）。

11. **Definition of Done**
- [ ] `Main` / `Settings` タブが廃止され、`Home` 統合UIになっている。
- [ ] 設定カテゴリは左サイドパネルで切替可能。
- [ ] 下部ドロワーで `OCR Preview` / `Log` が同時表示できる。
- [ ] `GridSplitter` で `Preview/Log` の表示比率を変更できる。
- [ ] `Preview` と `Log` を個別に折りたたみできる。
- [ ] 両ペイン同時折りたたみは禁止され、片側表示へ自動補正される。
- [ ] どの操作状態でも下部ドロワーへアクセスできる。
- [ ] プレビューが「最新のみ保持」で更新される（中間フレーム破棄を許容）。
- [ ] 下部ドロワーの開閉・比率が `settings.json` に保存されない。
- [ ] 起動初期ログの全件表示は保証しない仕様が明文化されている。
- [ ] `dotnet build Hotkey-Translator.csproj -p:UseAppHost=false` が成功する。
- [ ] 主要導線（Run/設定保存/ホスト制御/ホットキー）に回帰がない。
