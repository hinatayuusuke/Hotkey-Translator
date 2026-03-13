**概要**  
大きなレイアウト変更を避けるなら、`WPF UI` は「構造はそのまま、見た目だけ今風にする」方向でかなり相性が良いです。今の2カラム構成と各 `UserControl` は維持しつつ、テーマ辞書、ウィンドウ外観、カード化、入力コントロール、配色トークンだけを段階的に置き換える案が現実的です。

**ゴール / 非ゴール**
- ゴール: 既存の画面分割とバインディングを崩さず、Windows 11/Fluent寄りの見た目に寄せる
- ゴール: ハードコード色や素のWPFコントロール感を減らす
- 非ゴール: `NavigationView` ベースへの全面再設計
- 非ゴール: Page遷移化、MVVM再編、オーバーレイ描画ロジックの変更

**提案**
1. テーマ基盤を先に入れる  
[App.xaml](g:/Local App/Hotkey-Translator/App.xaml#L1) に `WPF UI` の `ThemesDictionary` と `ControlsDictionary` を追加します。ここで全体のLight/Dark、アクセント、既定コントロール見た目を一括適用します。  
あわせて、ハードコード色は `DynamicResource` に寄せます。`WPF UI` はテーマ変更反映に `DynamicResource` を推奨しています。

2. シェルだけ `FluentWindow` にする  
[MainWindow.xaml](g:/Local App/Hotkey-Translator/MainWindow.xaml#L1) の `Window` を `ui:FluentWindow` に置き換え、今の `Grid` レイアウトはそのまま残します。  
変えるのは主に以下です。
- ウィンドウ背景
- 角丸
- タイトルバー質感
- Mica/Acrylic 系のバックドロップ
- 左サイドバー、下部ステータス、Busy オーバーレイの余白と境界線

3. 既存 `Border` を `Card` に寄せる  
[UI/OverviewControl.xaml](g:/Local App/Hotkey-Translator/UI/OverviewControl.xaml#L8) の `OverviewBlockBorderStyle` で作っているカード群は、見た目の近代化効果が大きいので優先度が高いです。  
方針は2通りです。
- 最小変更: `Border` のまま `WPF UI` のブラシに差し替える
- 少し進める: `ui:Card` に置き換える  
後者の方が見た目は一気に良くなりますが、まずは前者でも十分効果があります。

4. 入力系だけ薄く差し替える  
今は `TextBox` と `ComboBox` が大量にあります。見た目優先なら全部を変えず、数値入力だけ `NumberBox` に絞るのが良いです。  
対象候補:
- [UI/OcrSettingsControl.xaml](g:/Local App/Hotkey-Translator/UI/OcrSettingsControl.xaml)
- [UI/OcrEnginesControl.xaml](g:/Local App/Hotkey-Translator/UI/OcrEnginesControl.xaml)
- [UI/OverlayBehaviorControl.xaml](g:/Local App/Hotkey-Translator/UI/OverlayBehaviorControl.xaml)  
しきい値、opacity、font size、ms などの数値入力は `NumberBox` に置き換えると、見た目と入力品質が同時に上がります。

5. ボタンとトグルの見た目を統一する  
`Run test`、`Select ROI`、`Preview`、`Log`、`Cancel` などの主要アクションは `ui:Button` に寄せます。  
特に [MainWindow.xaml](g:/Local App/Hotkey-Translator/MainWindow.xaml#L165) 周辺の下部ボタンと、[UI/OverviewControl.xaml](g:/Local App/Hotkey-Translator/UI/OverviewControl.xaml#L317) 周辺の主要操作ボタンは、アイコン付きにするとWPF標準感がかなり薄れます。  
`CheckBox` は全置換しなくてよく、ON/OFF意味が強い箇所だけ `ToggleSwitch` 系に寄せる程度で十分です。

6. ログ領域は `GroupBox` をやめる  
[UI/RuntimeLogsControl.xaml](g:/Local App/Hotkey-Translator/UI/RuntimeLogsControl.xaml#L30) と [UI/RuntimeLogsControl.xaml](g:/Local App/Hotkey-Translator/UI/RuntimeLogsControl.xaml#L88) の `GroupBox` は古く見えやすいので、ここは優先的に `Card + Header` へ変えるのが良いです。  
レイアウト自体は維持して、見た目だけ以下に変えます。
- フラットなヘッダー
- 背景の階層差
- 区切り線の細化
- TabControl の見た目調整

7. オーバーレイ画面は基本触らない  
[UI/OverlayWindow.xaml](g:/Local App/Hotkey-Translator/UI/OverlayWindow.xaml#L1) は透明・最前面・ヒットテスト無効の性質が強いので、`WPF UI` 化の対象から外すのが安全です。  
ここはトースト色やバッジ色だけテーマに合わせる程度で止めるべきです。

**実装ステップ**
1. `WPF-UI` パッケージ追加
2. [App.xaml](g:/Local App/Hotkey-Translator/App.xaml#L1) にテーマ辞書追加
3. [MainWindow.xaml](g:/Local App/Hotkey-Translator/MainWindow.xaml#L1) を `ui:FluentWindow` 化
4. [MainWindow.xaml.cs](g:/Local App/Hotkey-Translator/MainWindow.xaml.cs) で `SystemThemeWatcher.Watch(this)` を有効化
5. 共通ブラシ・角丸・余白を `ResourceDictionary` に集約
6. `OverviewControl` と `RuntimeLogsControl` を先行で見た目調整
7. 数値入力だけ `NumberBox` 化
8. 主要ボタンにアイコン追加

**この案で触るファイル候補**
- [Hotkey-Translator.csproj](g:/Local App/Hotkey-Translator/Hotkey-Translator.csproj#L1)
- [App.xaml](g:/Local App/Hotkey-Translator/App.xaml#L1)
- [MainWindow.xaml](g:/Local App/Hotkey-Translator/MainWindow.xaml#L1)
- [MainWindow.xaml.cs](g:/Local App/Hotkey-Translator/MainWindow.xaml.cs)
- [UI/OverviewControl.xaml](g:/Local App/Hotkey-Translator/UI/OverviewControl.xaml#L1)
- [UI/RuntimeLogsControl.xaml](g:/Local App/Hotkey-Translator/UI/RuntimeLogsControl.xaml#L1)
- [UI/OcrSettingsControl.xaml](g:/Local App/Hotkey-Translator/UI/OcrSettingsControl.xaml#L1)
- [UI/OcrEnginesControl.xaml](g:/Local App/Hotkey-Translator/UI/OcrEnginesControl.xaml#L1)
- [UI/OverlayBehaviorControl.xaml](g:/Local App/Hotkey-Translator/UI/OverlayBehaviorControl.xaml#L1)

**おすすめの進め方**
最初の1回は `Overview` と下部ログだけ触るのが良いです。ここで見た目の方向性が決まり、その後に各設定画面へ横展開できます。全部を一気に変えるより、共通トークンを先に作って面で揃える方が事故が少ないです。

参考:
- WPF UI docs: https://wpfui.lepo.co/documentation
- Themes: https://wpfui.lepo.co/documentation/themes.html
- SystemThemeWatcher: https://wpfui.lepo.co/documentation/system-theme-watcher.html
- Gallery: https://wpfui.lepo.co/documentation/gallery.html

必要なら次に、`このプロジェクト用の最小差分移行案` として、実際にどのXAMLをどう置き換えるかをファイル単位で出します。