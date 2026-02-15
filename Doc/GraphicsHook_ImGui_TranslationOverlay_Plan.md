# GraphicsHook: ImGui Translation Overlay Implementation Plan (DX11 first)

補助仕様書（実装詳細）: Doc/GraphicsHook_ImGui_TranslationOverlay_Spec.md

## 1. 概要
DX11 Present フック（HookAgentDx11）内で Dear ImGui を使い、ゲーム画面へ半透明の翻訳レイヤ（背景パネル + 動的テキスト）を合成表示する。
WPF Topmost 競合を回避しつつ、将来の Vulkan/OpenGL 追加でも「コマンドI/Fとロジックは共通、描画バックエンドのみ差し替え」になりやすい設計を優先する。

## 2. ゴール / 非ゴール
### ゴール
- DX11 タイトルで「半透明パネル + 翻訳テキスト」のゲーム内合成表示が安定する。
- overlay 更新が高頻度でも、処理が破綻しない（最新コマンド上書き、間引き、落ちてもゲームを落とさない）。
- 将来 Vulkan/OpenGL を追加する前提で、IPC/コマンド契約を versioning できる。

### 非ゴール（当面）
- DTP 品質の文字組（禁則、縦書き、複雑合字、双方向、Harfbuzz shaping 完備）。
- クリック/ドラッグ/コピー等のインタラクティブ UI（まずは表示のみ）。
- DX12 対応。

## 3. 前提・仮定
- いまの v1（Rect-only overlayUpdate）は動作している前提。
- HookAgentDx11 は swapchain/device/context を取得できる。
- まずは「表示のみ」。入力（WndProc hook）は後回し。

## 4. 現状整理（関連）
- C# → 共有メモリ Local\\HT_HOOK_CMD_<api>_<pid> に Rect コマンドを最新上書き。
- HookAgentDx11 は Present/Present1 内で Rect を描画（ClearView fallback + shader path）。
- HookAgentDx11 は state backup/restore を持ち、最近 UAV も含めるように拡張済み。

## 5. 提案アーキテクチャ
### 5.1 コンポーネント
- C#（Hotkey-Translator）
  - OverlayCommandComposer: 表示したい内容（翻訳テキスト、背景、枠など）を「描画コマンド」に変換
  - OverlayCommandWriter: 共有メモリへ最新コマンドを書き込み
- HookHost
  - 当面変更最小：attach/detach と注入担当。描画コマンドは共有メモリで渡す（pipe JSON 依存を減らす）
- HookAgentDx11
  - OverlayCommandReader: 共有メモリから最新コマンドを読む
  - ImGuiOverlayRenderer: Dear ImGui を初期化し、Present 内で描画する（描画前後で state を保全）

### 5.2 データフロー
1. C# が OCR/翻訳結果を得る
2. C# が overlay 表示用の payload を生成し、HT_HOOK_CMD（または新 mapping）へ最新上書き
3. HookAgentDx11 が Present 内で payload を読み、ImGui で半透明パネルとテキストを描画

### 5.3 重要な設計方針
- 「最新のみ」
  - 共有メモリは常に上書き、HookAgent は updatedQpc を見て反映。古い内容は捨てる。
- 安全第一
  - Hook 側は例外/失敗があってもゲームプロセスを落とさない（既存と同様に SEH + フラグで無効化）。
- バックエンド差分を隔離
  - コマンド契約は DX11 専用にしない（api/pid/version を header に持つ）。

## 6. インターフェース設計（IPC / コマンド）
Rect-only の v1 に加え、翻訳レイヤを扱える v2 を追加する。

### 6.1 Mapping
- 既存：Local\\HT_HOOK_CMD_<api>_<pid>（Rect v1）
- 追加案（推奨）：Local\\HT_HOOK_OVL_<api>_<pid>（Overlay v2: Rect + Text + Panel）
  - WHY: 既存 v1 を壊さず段階導入できる。v2 が不安定でも v1 に戻せる。

### 6.2 Header（例）
- magic / version=2 / api / targetPid / updatedSeq(or updatedQpc) / rectCount / textBlockCount / textBytes / flags
- サイズは固定（例：64KB）にして、内部に arrays + text blob を格納。

### 6.3 コマンド（例）
- RectCommand（既存と同等）
- TextBlockCommand
  - anchor/rect（x,y,w,h）: 表示位置や最大幅の基準
  - fontPx: フォントサイズ（px）
  - fgArgb: 文字色
  - bgArgb: 背景色（必要なら per-block で）
  - paddingPx, roundingPx
  - wrap: 折り返し有無
  - textOffset/textLen（UTF-8）

NOTE: 最初は textBlockCount=1（翻訳1ブロック）だけに制限し、安定後に拡張する。

## 7. DX11 実装方針（HookAgentDx11）
### 7.1 ImGui 統合
- 依存の追加
  - imgui 本体 + imgui_impl_dx11 を HookAgentDx11 に組み込む
  - imgui_impl_win32 は「入力が必要になるまで」最小限（HWND は swapchain desc から得られるが、WndProc hook は後回し）

- 初期化タイミング
  - device/context/backbuffer RTV が確定した後に 1 回だけ init
  - ResizeBuffers 時は InvalidateDeviceObjects → 次の Present で CreateDeviceObjects

- 描画の前後で state を保全
  - 既存の state backup/restore を活用するか、ImGui backend の保全に寄せる（タイトル破壊を避ける）
  - UAV を使うタイトルへの配慮は維持（最近の修正方針を踏襲）

### 7.2 描画内容
- 背景
  - ImDrawList::AddRectFilled で半透明の背景パネル
  - 角丸/影は最初は角丸のみ（影は後回し）
- テキスト
  - ImDrawList::AddText + 折り返し（必要なら ImGui::PushTextWrapPos）
  - まずは「表示できる」優先。品質/縁取り/影は後続。

### 7.3 フォント
- 初期は ImGui default font で動作確認
- 次段階
  - 日本語表示が必要なら、TTF を同梱して AddFontFromFileTTF を使用
  - HookAgent 内からファイルパスを解決するため、GetModuleFileNameW で DLL のディレクトリを基準にする

## 8. C# 実装方針
- Dx11HookOverlayCommandWriter に v2 writer を追加（HT_HOOK_OVL）
- Pipeline で「翻訳テキスト確定後」に overlay を更新
  - 連打や自動翻訳でも最終状態を反映できるよう、最新上書きのみ
- UI 設定（将来）
  - フック方式 overlay の on/off
  - 文字サイズ、最大幅、背景透明度、表示位置（画面下など）

## 9. 段階実装手順（ステップ分割）
### Step 1: IPC v2 の追加（描画はまだ Rect のまま）
- HT_HOOK_OVL mapping と header/version を追加
- HookAgentDx11 は v2 があれば読むが、まだ描画はしない（ログ/ステータス反映だけ）

### Step 2: ImGui の「背景パネルだけ」を表示
- ImGui init + 1 drawcall で半透明パネル（固定位置）を描画
- state 退避/復帰でタイトル破壊が起きないか確認

### Step 3: TextBlock（翻訳1ブロック）を表示
- v2 から UTF-8 の text blob を読み、折り返し付きで描画
- 更新頻度を上げても破綻しないことを確認（最新上書き）

### Step 4: 表示チューニング
- 位置（画面下/中央/左下）と最大幅、padding、角丸を調整
- 文字の可読性（必要なら簡易 shadow/outline を DrawList で多重描画）

### Step 5: 将来の Vulkan/OpenGL に向けた分離
- HookAgent 側 renderer を OverlayRenderer に分離し、DX11 は ImGui backend 実装に寄せる
- API 別で共通の「overlay command contract」を維持

## 10. 非機能要件チェック
- 安定性: 失敗時は overlay を無効化して capture を継続（ゲームを落とさない）
- 性能: overlay 更新は 5-15Hz 程度へ間引き可能。描画は 1-数回の DrawList で収める
- 可観測性: HT_HOOK_STAT に overlay v2 の updatedQpc, textBytes 等の診断情報を追加可能
- 互換性: v1（Rect-only）を残し、v2 を段階導入

## 11. リスクと緩和策
- Risk: タイトルによって ImGui の state 保存/復元が不十分で描画が壊れる
  - Mitigation: 既存の state backup/restore を強化（UAV/viewport/RTV/PSO 周り）し、壊れるタイトルは feature flag で v2 を disable
- Risk: フォントファイルの配布/パス解決
  - Mitigation: 最初は default font。日本語必須なら同梱して DLL 相対パスで読む
- Risk: 多言語 shaping が必要になる
  - Mitigation: まず非対応。必要なら Harfbuzz で glyph 生成するテキストスタックを別途設計

## 12. 影響範囲（変更ファイル候補）
- Native
  - Native/HookCommon/HookIpcProtocol.h（HT_HOOK_OVL naming / structs）
  - Native/HookCommon/SharedOverlayV2.*（writer/reader）
  - Native/HookAgentDx11/*（ImGui 統合、renderer 実装、ResizeBuffers 対応）
- C#
  - Services/Hook/*（v2 writer）
  - Services/PipelineOrchestrator.cs（翻訳完了時に v2 update）
  - Settings（UI expose は後回しでも可）

## 13. Definition of Done
- DX11 タイトル（ウィンドウ/フルスクリーン/排他）で、半透明背景 + 翻訳テキストがゲーム内に表示される
- 自動翻訳/手動連打でも UI が破綻しない（最新上書き、クラッシュなし）
- 既存 v1 の Rect overlay は壊れない（v2 を無効化しても動く）






