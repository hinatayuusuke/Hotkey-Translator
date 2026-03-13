# Hook / Fullscreen Help Text Archive

`Hook / Fullscreen` 設定画面から常時表示の説明文を外す前の退避メモです。
後で tooltip、hint、help box へ戻すときの原文として使います。

## Page
- `Hook / Fullscreen`
  - Exclusive fullscreen support and virtual fullscreen workflows live here so game compatibility settings stay grouped together.

## Graphics Hook
- Section description
  - Use this path when overlay injection or direct rendering support is required for the target app.
- `Capture FPS limit`
  - Limits capture-side updates when the hook pipeline owns the target app.

## Launcher
- Section description
  - Use launcher integration when the target app must be started through the hook pipeline.
- Note
  - In launcher mode, lock/unlock fixed target hotkeys (`F7` / `Shift+F7`) are disabled.
- `Steam launch`
  - Format: `"<Hotkey-Translator.exe>" --hook-launch -- %command%`.
  - For non-Steam shortcuts, `%command%` may not expand as expected.

## Mirror Fullscreen
- Section description
  - Use Magpie-based virtual fullscreen for apps that are fixed-window or need fullscreen-like presentation.
- `Magpie profile index`
  - Magpie core path: `Tools\Magpie\Magpie.Core.exe` (app relative)
