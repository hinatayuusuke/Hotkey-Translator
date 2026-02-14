# Bottom Drawer Pinned Thumbnail Tab Plan

1. **概要（1–3行）**
- 下部Drawerの左ペイン（現 OCR Preview）に Preview / Pinned タブを追加し、ログ右固定のまま表示密度を維持する。
- Pinned は「固定ウィンドウをロックした瞬間のサムネ1枚」を表示し、リアルタイム更新は行わない。
- 固定解除・取得失敗時は画像の代わりに状態メッセージを表示する。

2. **ゴール / 非ゴール**
### ゴール
- 既存の右側 Log ペインを維持しつつ、左側で Preview と Pinned を切り替え可能にする。
- 固定キャプチャ成功時にのみサムネを保持し、低コストで状況確認できるUIを提供する。
- 固定解除/取得失敗時にユーザーへ明確な状態表示を行う。

### 非ゴール
- 固定サムネの連続更新（fps更新）や履歴管理。
- サムネ画像の永続化（settings.json / ファイル保存）。
- 右側ログペインの位置・構造変更。

3. **前提・仮定**
- Drawerは現状 BottomPreviewPaneVisible（左）と BottomLogPaneVisible（右）で表示制御済み。
- 左ペインは MainWindow.xaml の GroupBox Header="OCR Preview" と OcrPreprocessPreviewImage を使用。
- 固定ウィンドウのロック/解除は既存コマンド（LockCaptureWindow / UnlockCaptureWindow）経由で実行される。

4. **現状整理**
- 左ペインはOCR前処理プレビュー専用で、固定対象の視覚確認用途を兼ねられない。
- 3分割化すると窮屈になるため、左ペイン内でのモード切替が必要。
- 固定サムネは「即時性より状態確認」が目的であり、1回取得で十分。

5. **提案アーキテクチャ**
### 5.1 UI構成
- 左 GroupBox 内を TabControl 化し、タブは以下2つ:
  - Preview: 既存の OcrPreprocessPreviewImage を表示。
  - Pinned: 固定時サムネ（Image）または状態テキストを表示。
- 右 Log は既存のまま維持。

### 5.2 データフロー
- 固定ロック成功時:
  - その時点で固定対象から1枚キャプチャして Pinned 用ImageSourceへ反映。
  - PinnedStatusMessage を空にして画像優先表示。
- 固定解除時:
  - PinnedImageSource = null。
  - PinnedStatusMessage = "固定対象なし"。
- 取得失敗時:
  - PinnedImageSource = null。
  - PinnedStatusMessage = "サムネ取得失敗"（必要なら理由を短く追記）。

### 5.3 既存パターン整合
- OCRプレビュー更新は PreviewFrameDispatcher 経路（最新のみ保持）を継続。
- 固定サムネは別経路で管理し、OCRプレビュー更新と干渉させない。

6. **インターフェース設計**
### 6.1 ViewModel拡張案（MainWindowViewModel）
- BottomLeftPreviewTabIndex : int（0=Preview, 1=Pinned）
- PinnedCaptureThumbnail : BitmapSource?
- PinnedCaptureStatusMessage : string
- HasPinnedCaptureThumbnail : bool（UIのVisibility制御用）

### 6.2 Viewイベント/メソッド案（MainWindow側）
- UpdatePinnedCaptureThumbnail(BitmapSource? source, string? message)
- ClearPinnedCaptureThumbnail(string message)
- WHY: ロック/解除/失敗の各分岐をUI更新関数へ集約し、条件漏れを防ぐため。

### 6.3 エラー/バリデーション
- サムネ取得失敗は例外を握りつぶさずログ出力し、UIには短い状態文のみ表示。
- 画像が null の場合は必ずメッセージ表示にフォールバックする。

7. **実装手順（ステップ分割）**
- Step 1: 左ペインUIをタブ化
  - MainWindow.xaml の OCR Preview GroupBox内部を TabControl 化。
  - Preview タブに既存プレビューUIを移植。
  - Pinned タブに Image + TextBlock（空状態表示）を追加。

- Step 2: ViewModelプロパティ追加
  - BottomLeftPreviewTabIndex / PinnedCaptureThumbnail / PinnedCaptureStatusMessage を追加。
  - 初期値は Preview タブ選択、PinnedStatus="固定対象なし"。

- Step 3: 固定ロック時のサムネ取得導線追加
  - 既存ロック成功処理の直後で1回キャプチャを実施。
  - 成功時は PinnedCaptureThumbnail 更新、失敗時は状態文設定。

- Step 4: 固定解除時のクリア導線追加
  - 既存解除処理で PinnedCaptureThumbnail をクリアし、固定対象なし を表示。

- Step 5: ログと回帰確認
  - 取得失敗時にログ出力されることを確認。
  - OCRプレビュー表示・ログ表示・Drawer開閉の既存挙動を確認。

8. **非機能要件チェック**
- 性能
  - サムネは固定時1回のみ取得で、常時更新負荷は発生しない。
- 可観測性
  - 失敗時はログに理由を残し、UIは簡潔表示に留める。
- 互換性
  - 既存の Preview/Log トグル仕様や右ログ配置は維持。
- 運用
  - 設定永続化なし（アプリ再起動時は空状態に戻る）。

9. **リスクと緩和策**
- Risk: ロック時サムネ取得が失敗して、ユーザーが不具合と誤認する。
- Mitigation: UIに明示メッセージを表示し、ログへ詳細を記録する。

- Risk: OCRプレビュー更新とPinned更新が競合して描画ちらつきが発生する。
- Mitigation: Pinnedは別Imageコントロール/別プロパティで分離し、相互更新しない。

- Risk: タブ追加で左ペインの見通しが悪化する。
- Mitigation: 既定タブを Preview にし、既存ユーザー導線を維持する。

10. **影響範囲（変更ファイル候補・移行・ドキュメント更新）**
- MainWindow.xaml — 左Drawerペインをタブ化（Preview/Pinned）。
- ViewModels/MainWindowViewModel.cs — Pinned表示状態プロパティ追加。
- MainWindow.Preview.cs または MainWindow.xaml.cs — ロック/解除時のサムネ更新導線を追加。
- （必要時）Services/Application/* — サムネ取得ヘルパーの切り出し。
- Doc/* — 実装完了後に仕様差分を追記（任意）。

11. **Definition of Done（完了条件のチェックリスト）**
- [ ] 左Drawerペインに Preview / Pinned タブが表示される。
- [ ] 右Drawerの Log は現状位置・挙動を維持する。
- [ ] 固定ロック成功時に Pinned タブでサムネが表示される（1回取得）。
- [ ] 固定解除時に Pinned タブへ 固定対象なし が表示される。
- [ ] 取得失敗時に サムネ取得失敗 が表示され、ログに失敗理由が出る。
- [ ] dotnet build Hotkey-Translator.csproj -p:UseAppHost=false が成功する。