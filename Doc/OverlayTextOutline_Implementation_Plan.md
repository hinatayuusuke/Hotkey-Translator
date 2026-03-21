# Overlay Text Outline Implementation Plan

1. **概要（1–3行）**
オーバーレイ文字の可読性を上げるため、WPF overlay と GraphicsHook overlay の両方に黒フチを追加する。
初期実装は固定色・固定太さで導入し、通常 overlay と hook/fullscreen overlay の見た目差を増やさないことを優先する。

2. **ゴール / 非ゴール**
- ゴール
  - WPF overlay と GraphicsHook overlay の両方で、翻訳テキストに同等の縁取りを付ける。
  - 明るい背景や高コントラストなゲーム UI 上でも、白文字の視認性を改善する。
  - 初期実装は固定値で導入し、設定 UI 追加なしで完結させる。
  - 既存の背景付き overlay デザインを維持したまま、文字の視認性だけを強化する。
- 非ゴール
  - 文字ごとの複雑なベクター輪郭描画。
  - WPF と ImGui の完全一致な見た目。
  - outline 色・太さの設定 UI 追加。
  - text shadow / glow / gradient など別系統の装飾追加。

3. **前提・仮定**
- 現在の WPF overlay は `UI/OverlayWindow.xaml.cs` で `TextBlock` を生成して描画している。
- GraphicsHook overlay は `OverlayItem` から `OverlayTextBlockV2` を組み立て、native 側で `ImDrawList::AddText(...)` している。
- 背景ボックスはすでに存在するため、outline は背景代替ではなく文字の視認性強化として扱う。
- 初期は固定の黒フチで十分であり、色や太さの runtime 設定は必要ない。

4. **現状整理**
- WPF overlay
  - `UI/OverlayWindow.xaml.cs` の `UpdateItems(...)` が `TextBlock` を 1 つ生成し、`Border` の子として `OverlayCanvas` に積んでいる。
  - フォントサイズは `OverlayFontFitter` で決めているが、outline 分の余白はまだ考慮していない。
- GraphicsHook overlay
  - `Services/PipelineOrchestrator.cs` が `GraphicsHookOverlayV2CommandWriter.TextBlockV2` を生成し、`fgArgb / bgArgb / fontPx / paddingPx / roundingPx` を詰めている。
  - `Native/HookCommon/HookIpcProtocol.h` の `OverlayTextBlockV2` には outline 用の色や太さがない。
  - `Native/HookAgentDx11/Dx11PresentHook.cpp` と `Native/HookAgentVulkan/VulkanPresentHook.cpp` は本体文字を 1 回だけ `AddText(...)` している。
- 課題
  - 白文字 + 半透明背景だけでは、明るい UI や細かいテクスチャの上で文字が弱い。
  - WPF overlay と hook overlay の描画実装が別なので、片方だけに縁取りを入れると表示差が出る。

5. **提案アーキテクチャ**
- コンポーネント構成
  - `UI/OverlayWindow.xaml.cs`
    - WPF overlay の outline 描画を持つ。
  - `Services/PipelineOrchestrator.cs`
    - hook overlay 用 `TextBlockV2` に outline 情報を詰める。
  - `Services/Hook/GraphicsHookOverlayV2CommandWriter.cs`
    - outline 付き `TextBlockV2` を writer へ流す。
  - `Native/HookCommon/HookIpcProtocol.h`
    - overlay block の shared-memory 構造体に outline 情報を追加する。
  - `Native/HookAgentDx11/Dx11PresentHook.cpp`
  - `Native/HookAgentVulkan/VulkanPresentHook.cpp`
    - native 側で outline を描いてから本体文字を重ねる。
- データフロー / シーケンス
  1. C# 側で `OverlayItem` ごとの font size と描画矩形を決める。
  2. WPF overlay は outline 用テキストを先に描き、本体テキストを最後に重ねる。
  3. hook overlay は `outlineArgb / outlinePx` を `TextBlockV2` に載せて shared memory へ書く。
  4. native renderer は 8 方向へオフセットした `AddText(...)` で outline を描いた後、本体文字を 1 回描く。
- 既存パターンへの整合
  - 背景矩形の構造は維持し、文字描画の前景部分だけを拡張する。
  - WPF では既存の `OverlayFontFitter` をそのまま使い、必要最小限の描画変更に留める。
  - hook overlay では既存の `TextBlockV2` パイプラインを拡張し、別系統の overlay プロトコルは増やさない。

6. **インターフェース設計**
- WPF 側
  - `OverlayWindow.UpdateItems(...)`
    - `TextBlock` 単体ではなく、outline 用レイヤを含む `Grid` か `Canvas` を `Border.Child` に入れる。
  - outline 用固定値
    - `OutlineBrush = Black`
    - `OutlineThicknessDip = 1.5` または `2.0`
- Hook IPC 側
  - `GraphicsHookOverlayV2CommandWriter.TextBlockV2` と `OverlayTextBlockV2` に以下を追加
    - `float OutlinePx`
    - `uint OutlineArgb`
  - 既存の `fgArgb / bgArgb / fontPx` と同列で扱う。
- Native renderer 側
  - `OutlinePx <= 0` または alpha 0 の場合は outline 描画を省略する。
  - outline が有効なら、上下左右 + 斜めの 8 方向へ `AddText(...)` を描いてから本体文字を描く。
- バリデーション
  - outline 太さは `0.0 <= outlinePx <= 4.0` 程度で clamp する。
  - outline 色は初期実装では固定値にして、runtime 設定は持たない。

7. **実装手順（ステップ分割）**
- Step 1
  - WPF overlay に outline を追加する。
  - `TextBlock` 多重描画で、中央の本体文字に対して 8 方向オフセットの outline text を重ねる。
  - WHY: WPF 側だけでも通常運用の可読性改善を先に確認できる。
- Step 2
  - `OverlayTextBlockV2` / `TextBlockV2` に outline 情報を追加する。
  - writer と reader の struct サイズを更新し、shared memory の整合を取る。
- Step 3
  - DX11 / Vulkan hook renderer に outline 描画を追加する。
  - 既存の背景矩形描画と本体文字描画の間に outline 描画を挟む。
- Step 4
  - 必要なら `OverlayWindow` と hook 用 `paddingPx` を少し増やし、outline 分で文字が窮屈になりすぎないよう調整する。
  - `OverlayFontFitter` 自体の変更は初期実装では入れず、はみ出しが出た場合にだけ余白計算を追加する。

8. **非機能要件チェック**
- 性能
  - WPF 側は text block が増えるため描画コストが上がるが、overlay item 数は通常少ないため初期実装では許容範囲とみなす。
  - hook 側は `AddText(...)` が 8 回増えるためコスト増はあるが、短文中心なのでまずは実測で判断する。
- セキュリティ
  - 新しい外部入力や権限制御は追加しない。
- 可観測性
  - 初期実装では専用ログ追加は不要。
  - 表示崩れが出た場合に `hook_v2_map` 系既存ログで block 数・fontPx を追える構成を維持する。
- 互換性
  - IPC 構造体変更があるため、hook renderer と C# writer は同時更新前提。
  - COMPAT: 旧 native binary との混在は前提にしない。
- 運用
  - outline 追加後にキャプチャ動画、通常 overlay、hook overlay の 3 パターンで見え方を確認する。

9. **リスクと緩和策**
- Risk: outline 分で文字が大きく見え、狭い box で折り返しやクリップが増える可能性がある。
- Mitigation: 初期は outline 太さを控えめにし、必要なら `paddingPx` を先に増やして吸収する。
- Risk: WPF と hook で outline の見え方が少しズレる可能性がある。
- Mitigation: 色・太さを固定し、8方向オフセットという同じルールで揃える。
- Risk: hook overlay の描画コストが上がる可能性がある。
- Mitigation: outline 太さを固定値に絞り、まずは全 block 一律で軽い 8方向描画に留める。

10. **影響範囲**
- 変更ファイル候補
  - `UI/OverlayWindow.xaml.cs`
  - `Services/PipelineOrchestrator.cs`
  - `Services/Hook/GraphicsHookOverlayV2CommandWriter.cs`
  - `Native/HookCommon/HookIpcProtocol.h`
  - `Native/HookAgentDx11/Dx11PresentHook.cpp`
  - `Native/HookAgentVulkan/VulkanPresentHook.cpp`
- ドキュメント更新
  - 本ファイルを実装案として追加する。
  - 必要なら `GraphicsHook_ImGui_TranslationOverlay_Spec.md` に outline フィールドを追記する。
- 移行
  - native / managed の同時ビルドが必要。

11. **Definition of Done**
- WPF overlay で文字に黒フチが表示される。
- hook/fullscreen overlay でも同等の黒フチが表示される。
- 明るい背景の UI で、縁取りなし時より可読性が改善している。
- `dotnet build .\Hotkey-Translator.sln -c Release` と native ビルドが通る。
- 表示崩れが強い box がないことを通常 overlay と hook overlay の両方で確認できる。
