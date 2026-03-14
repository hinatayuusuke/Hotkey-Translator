# Overview 下部ステータスバー簡素化 実装案

## 1. 概要
- `Overview` で `Current Setup` が常設の要約を担っているため、ウィンドウ下部バーから常時表示のステータステキストを外す。
- 下部バーは `Preview` と `Log` のユーティリティ操作を中心にした軽量なアクション領域へ整理する。
- 例外として、通常時は非表示にしたランタイム状態を、実行中やエラー時のみ短く出せる余地を残す。

## 2. ゴール / 非ゴール
### ゴール
- 下部バーの常時ノイズを減らし、`Overview` の情報重複を解消する。
- `Preview` と `Log` へのアクセスは維持する。
- 将来的に Busy / Error の一時メッセージを追加しやすい構造へ整理する。

### 非ゴール
- `Current Setup` 自体の項目追加や文言刷新。
- `RuntimeLogsControl` の構造変更。
- 新しいステータス通知コンポーネントの導入。

## 3. 前提・仮定
- 現在の下部バーは [MainWindow.xaml](../MainWindow.xaml) の `Grid.Row="2"` にあり、`TranslationStatusMessage` と `RoiStatusMessage` を常時表示している。
- `Overview` の [UI/OverviewControl.xaml](../UI/OverviewControl.xaml) には `Current Setup` セクションがあり、翻訳経路・ROI 状態・Hook 状態などを既に要約している。
- 現行要件では、通常時に常時表示すべきランタイムステータスは必須ではない。

## 4. 現状整理
- 下部バーは左に `TranslationStatusMessage`、右に `Preview`、`Log`、`RoiStatusMessage` を置く構成になっている。
- `TranslationStatusMessage` は `Llama: disabled | Gemini: disabled | ...` のように、設定状態の一覧を出すことが多い。
- `Current Setup` は `TranslationRouteSummary` と `RuntimeStatus.RoiStatusMessage` を表示しており、下部バーと情報の重複がある。
- その結果、画面下端が「常設情報表示」と「操作導線」の混在領域になっている。

## 5. 提案アーキテクチャ
### コンポーネント構成
- `MainWindow.xaml`
  下部バーのレイアウトを `Preview` / `Log` 中心のアクションバーへ変更する。
- `MainWindowViewModel` または `RuntimeStatus`
  将来 Busy / Error 表示を足す場合のため、短い一時メッセージ用の表示面を維持できるようにする。

### データフロー / シーケンス
- 通常時
  下部バーは `Preview` と `Log` のみを表示する。
- 実行中またはエラー時
  必要になった時だけ短いメッセージを表示する。
  `Current Setup` はあくまで現在構成の要約を担い、実行中メッセージの代替にはしない。

### 既存パターンへの整合
- 既存の `Button Command="{Binding TogglePreviewPaneCommand}"` と `Button Command="{Binding ToggleLogPaneCommand}"` はそのまま使う。
- ステータスメッセージは「常設表示」から「条件表示」へ責務を縮小するだけで、データ生成側ロジックをすぐには消さない。

## 6. インターフェース設計
### UI 変更
- [MainWindow.xaml](../MainWindow.xaml)
  `TextBlock Text="{Binding RuntimeStatus.TranslationStatusMessage}"` を削除する。
- [MainWindow.xaml](../MainWindow.xaml)
  `TextBlock Text="{Binding RuntimeStatus.RoiStatusMessage}"` を削除する。
- [MainWindow.xaml](../MainWindow.xaml)
  `Preview` と `Log` ボタンだけを残し、配置を中央寄せまたは右寄せの小さなアクション領域に整理する。

### 条件表示の余地
- 将来対応として、`RuntimeStatus.HasTransientStatus` と `RuntimeStatus.TransientStatusMessage` のような最小プロパティを追加すれば、Busy / Error 時だけ表示する拡張が可能。
- WHY: 常設表示を先に消しても、異常時だけ知らせる導線を後から薄い差分で戻せるため。

## 7. 実装手順
1. `MainWindow.xaml` の下部バーから `TranslationStatusMessage` と `RoiStatusMessage` の `TextBlock` を削除する。
2. `Preview` と `Log` ボタンだけが自然に見えるよう、`DockPanel` または `StackPanel` の余白を調整する。
3. 余白が過剰なら、下部バー自体の `Padding` と `Margin` を少し詰める。
4. `dotnet build` で XAML 整合性を確認する。
5. 必要なら次の段階で、一時状態表示用の最小プロパティを ViewModel 側へ足す。

## 8. 非機能要件チェック
### 性能
- UI ツリーが減るだけなので影響は軽微。

### セキュリティ
- 影響なし。

### 可観測性
- 常時表示の視認性は下がるが、`Current Setup` と `Log` で通常確認は可能。
- Busy / Error の一時表示が必要なら、別タスクで条件表示を追加する。

### 互換性
- 画面構成のみの変更であり、設定ファイルやコマンドに影響はない。

### 運用
- 利用者は通常状態の詳細確認を `Current Setup` または `Log` へ寄せることになる。

## 9. リスクと緩和策
- Risk: 常時ステータスを見ていた利用者が、状態確認先の移動に一時的に戸惑う可能性がある。
- Mitigation: `Current Setup` が既に翻訳・ROI の要約を持っている前提で進め、必要なら `Current Setup` の文言だけ後続で調整する。
- Risk: 実行中や異常時の気付きが弱くなる可能性がある。
- Mitigation: 本タスクでは通常時の常設表示だけを削り、必要が確認できたら条件付きの一時表示を追加する。

## 10. 影響範囲
- `MainWindow.xaml`
- 必要時のみ `MainWindowViewModel.cs` または `RuntimeStatus` 関連クラス
- `.agent/changes.md`

## 11. Definition of Done
- [ ] 下部バーに `Preview` と `Log` だけが表示される。
- [ ] `Overview` の `Current Setup` で翻訳経路と ROI 状態を引き続き確認できる。
- [ ] `dotnet build .\Hotkey-Translator.csproj -v minimal /m:1` が成功する。
- [ ] 変更内容が `.agent/changes.md` に記録される。
