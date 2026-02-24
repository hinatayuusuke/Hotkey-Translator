**2026-01-19 11:16 (Asia/Taipei) ? Implement phased capture/OCR/overlay pipeline**

### Summary
- Implemented Phase 1-7 pipeline scaffolding with capture, OCR, ROI/pHash, caching, and Gemini integration.

### Context / Goal
- Follow Doc/plan.md to implement functionality in phase order.
- Deliver a minimal working WPF pipeline with hotkey-triggered capture and overlay.

### Changes
- Added capture/OCR pipeline, overlay presenter, ROI selector, and pHash/normalization/cache services.
- Wired settings persistence (DPAPI protected API key) and hotkey-triggered orchestration.

### Files Touched
- `Hotkey-Translator.csproj` ? switch target framework to net8.0-windows10.0.19041.0 and add package references.
- `MainWindow.xaml` ? add capture/settings controls and logging UI.
- `MainWindow.xaml.cs` ? wire settings load/save, hotkey, and pipeline execution.
- `Services/CaptureManager.cs` ? capture via active window/screen using GDI and bounds helpers.
- `Services/OcrEngine.cs` ? Windows Media OCR integration and line extraction.
- `Services/PipelineOrchestrator.cs` ? phase-ordered orchestration (pHash, OCR diff, cache, Gemini, overlay).
- `Services/CacheRepository.cs` ? SQLite persistence for translation cache.
- `Services/SettingsService.cs` ? JSON settings + DPAPI key protection.
- `UI/OverlayWindow.xaml` ? transparent overlay window.
- `UI/RoiSelectorWindow.xaml` ? ROI selection overlay.
- `Models/*.cs` ? data models for OCR, overlay, settings, and cache keys.

### Behavioral Impact
- F8 triggers a single capture/OCR run with overlay output; ROI selection gates OCR and pHash skips unchanged frames.
- Translations cache to a local SQLite file, and Gemini calls only run when enabled with an API key.
- Target framework changed to net8.0-windows10.0.19041.0 for build compatibility.

### Risk & Mitigation
- Risk: WinRT runtime assembly conflict warning during build and OCR runtime availability.
- Mitigation: Build succeeds with a warning; log output and overlay fallback keep prior results when OCR or capture fails.

### Tests / Verification
- `dotnet build` (warning: WinRT.Runtime version conflict remains).
**2026-01-19 11:28 (Asia/Taipei) ? Add Mermaid diagrams**

### Summary
- Documented architecture, pipeline, and phase roadmap with Mermaid diagrams.

### Context / Goal
- Provide Mermaid code in Doc/mermaid.md based on Doc/plan.md.
- Make the phase flow and runtime pipeline easy to visualize.

### Changes
- Added Mermaid flowcharts for architecture, hotkey pipeline, and phase roadmap.

### Files Touched
- `Doc/mermaid.md` ? new Mermaid diagrams for architecture, pipeline, and phases.

### Behavioral Impact
- No runtime behavior changes; documentation only.

### Risk & Mitigation
- Risk: Diagram semantics may drift from implementation over time.
- Mitigation: Update diagrams alongside pipeline changes.

### Tests / Verification
- �����{�i�h�L�������g�ǉ��̂݁j
**2026-01-19 11:59 (Asia/Taipei) ? Disable Gemini safety blocking**

### Summary
- Added explicit Gemini safety settings to avoid model-side blocking.

### Context / Goal
- Ensure Gemini requests are not blocked by safety filters.
- Keep structured output mapping stable for OCR text.

### Changes
- Added safety_settings with BLOCK_NONE thresholds to Gemini request payload.

### Files Touched
- `Services/GeminiClient.cs` ? include safety_settings in request JSON.

### Behavioral Impact
- Gemini responses will no longer be blocked by safety settings, increasing output coverage for sensitive text.

### Risk & Mitigation
- Risk: Potentially returns unsafe content if OCR captures it.
- Mitigation: Downstream UI/usage must handle display policy as needed.

### Tests / Verification
- �����{�i�ݒ�ǉ��̂݁j
**2026-01-19 14:10 (Asia/Taipei) ? Expand capture rationale comment**

### Summary
- Expanded the WHY comment explaining the GDI capture choice and transition plan.

### Context / Goal
- Clarify the rationale behind using GDI capture in Phase 1.
- Make the dependency and reliability tradeoffs explicit in code comments.

### Changes
- Reworded the WHY comment in CaptureManager to document WinRT/GraphicsCapture risks.

### Files Touched
- `Services/CaptureManager.cs` ? expanded the WHY comment about GDI vs WinRT capture.

### Behavioral Impact
- No runtime behavior change; comment-only update.

### Risk & Mitigation
- Risk: None (documentation change only).
- Mitigation: N/A.

### Tests / Verification
- �����{�i�R�����g�ύX�̂݁j
**2026-01-19 15:40 (Asia/Taipei) ? Implement Plan3 capture phases scaffolding**

### Summary
- Refactored capture pipeline for provider abstraction, black-frame gating, and normalized ROI persistence.

### Context / Goal
- Follow Doc/Plan3.md to introduce capture provider selection and ROI normalization without breaking existing pipeline.
- Prepare for GPU-first capture phases with safe fallbacks and logging.

### Changes
- Added capture provider abstraction with GDI implementation, WGC/DXGI scaffolding, and provider cooldown tracking.
- Implemented black-frame detection gate and normalized ROI storage with auto-migration.
- Updated pipeline/UI to use new CaptureManager API and normalized ROI selection.
- Adjusted csproj to use Microsoft.Windows.SDK.NET (preview) for WinRT metadata.

### Files Touched
- `Hotkey-Translator.csproj` ? replace Windows SDK contracts with Microsoft.Windows.SDK.NET preview package.
- `Models/AppSettings.cs` ? add normalized ROI and capture provider/gate settings.
- `Models/CaptureFrame.cs` ? include provider kind, timestamp, and black-frame flag.
- `Models/CaptureProviderKind.cs` ? new provider enum.
- `Models/NormalizedRect.cs` ? normalized ROI conversion helpers.
- `Services/ICaptureProvider.cs` ? provider abstraction.
- `Services/GdiCaptureProvider.cs` ? migrated GDI capture logic.
- `Services/WgcCaptureProvider.cs` ? WGC provider stub with bounds helpers.
- `Services/DxgiDuplicationProvider.cs` ? DXGI provider stub.
- `Services/CaptureManager.cs` ? provider selection, cooldowns, and black-frame handling.
- `Services/FrameGate.cs` ? black-frame detection (mean/variance sampling).
- `Services/BitmapHelper.cs` ? software bitmap to bitmap conversion helper.
- `Services/PipelineOrchestrator.cs` ? black-frame skip, preferred provider tracking, normalized ROI resolution.
- `UI/RoiSelectorWindow.xaml.cs` ? save normalized ROI based on capture bounds.
- `MainWindow.xaml.cs` ? new capture manager wiring and normalized ROI status.

### Behavioral Impact
- Capture now runs through provider selection with black-frame gating and cooldowns; preferred provider updates persist.
- ROI is saved in normalized coordinates for DPI/monitor-safe reuse; legacy absolute ROI migrates on first capture.
- WGC/DXGI providers are scaffolded but currently return fallback errors; GDI remains active.

### Risk & Mitigation
- Risk: WGC/DXGI providers are stubs, so enabling them yields fallback behavior only.
- Mitigation: GDI remains the last provider and cooldown logic prevents capture stalls; logs note provider failures.
- Risk: WinRT runtime version conflict warning during build persists.
- Mitigation: Build succeeds with warning; monitor runtime OCR/WGC behavior per environment.

### Tests / Verification
- `dotnet build` (warning: WinRT.Runtime version conflict remains).
**2026-01-19 16:09 (Asia/Taipei) ? Implement WGC capture provider**

### Summary
- Implemented WGC capture via Direct3D11CaptureFramePool with D3D11 device interop.

### Context / Goal
- Provide a working WGC provider for the capture pipeline per Plan3 Phase 1.
- Avoid WinRT helper dependencies by using explicit COM/interop creation.****

### Changes
- Added D3D11 device creation via P/Invoke, DXGI QueryInterface, and IDirect3DDevice conversion.
- Wired Direct3D11CaptureFramePool to grab a single frame and convert it to Bitmap.
- Implemented activation factory lookup with RoGetActivationFactory + HSTRING for GraphicsCaptureItem creation.

### Files Touched
- `Services/WgcCaptureProvider.cs` ? full WGC capture implementation with interop helpers and device setup.

### Behavioral Impact
- WGC provider can now capture one frame via Direct3D11CaptureFramePool when enabled and supported.

### Risk & Mitigation
- Risk: WGC failures on unsupported OS or restricted windows.
- Mitigation: CaptureManager falls back to other providers and logs WGC errors.

### Tests / Verification
- `dotnet build` (warning: WinRT.Runtime version conflict remains).
**2026-01-20 09:23 (Asia/Taipei) ? Implement DXGI duplication provider**

### Summary
- Implemented DXGI Desktop Duplication with active-window monitor cropping.

### Context / Goal
- Add a working DXGI Desktop Duplication capture path per Plan3.
- Support ActiveWindow mode by capturing the containing monitor and cropping to window bounds.

### Changes
- Added D3D11 device/context creation via P/Invoke and COM wrapping.
- Implemented DXGI output selection, duplication, frame acquisition, staging copy, and bitmap extraction.
- Added active window bounds detection with DWM extended frame and monitor cropping logic.

### Files Touched
- `Services/DxgiDuplicationProvider.cs` ? DXGI duplication implementation and ActiveWindow monitor crop handling.

### Behavioral Impact
- DXGI provider can now capture a frame; ActiveWindow mode crops the duplicated monitor image to the foreground window bounds.

### Risk & Mitigation
- Risk: DXGI duplication fails on unsupported hardware or protected content.
- Mitigation: CaptureManager logs failures and falls back to other providers.

### Tests / Verification
- `dotnet build` (warning: WinRT.Runtime version conflict remains).
**2026-01-20 16:33 (Asia/Taipei) — Code review doc**

### Summary
- Created a code review report.

### Context / Goal
- Document review findings for the WPF OCR/translation overlay app.
- Provide actionable risks and gaps.

### Changes
- Authored a review report in Doc/Review.md.

### Files Touched
- `Doc/Review.md` — added code review findings and questions.

### Behavioral Impact
- No runtime behavior changes; documentation only.

### Risk & Mitigation
- Risk: None (documentation-only).
- Mitigation: N/A.

### Tests / Verification
- Not run (documentation-only change).
**2026-01-20 17:10 (Asia/Taipei) — Use Vortice D3D11 device creation**

### Summary
- Switched DXGI device creation to Vortice APIs to avoid COM lifetime issues.

### Context / Goal
- Fix DXGI duplication crash (E_NOINTERFACE) on Win11 with RTX GPUs.
- Avoid manual COM pointer lifetime management in device creation.

### Changes
- Replaced P/Invoke D3D11CreateDevice with Vortice D3D11CreateDevice.
- Removed manual COM Release and unused D3D11 interop enums/constants.

### Files Touched
- Services/DxgiDuplicationProvider.cs — switched device/context creation to Vortice and removed P/Invoke path.

### Behavioral Impact
- DXGI capture initialization should succeed without the previous crash path.

### Risk & Mitigation
- Risk: Vortice signature mismatch could fail to compile in this repo.
- Mitigation: Rebuild to confirm; revert to fallback provider if needed.

### Tests / Verification
- Not run (not requested).**2026-01-20 17:33 (Asia/Taipei) — Bind OCR language to UI source**

### Summary
- Wired OCR language selection to the UI Source Language setting with safe fallbacks.

### Context / Goal
- Allow OCR to respect the user-selected source language.
- Keep OCR functional when a language tag is invalid or not installed.

### Changes
- Added per-language OCR engine resolution with fallback behavior.
- Passed settings.SourceLanguage into OCR recognition.

### Files Touched
- Services/OcrEngine.cs — accept language tag, resolve engines, and fall back when unsupported.
- Services/PipelineOrchestrator.cs — pass Source Language into OCR.

### Behavioral Impact
- OCR now attempts to use the language specified in the UI Source Language field.

### Risk & Mitigation
- Risk: Unsupported language tags could still cause unexpected OCR results.
- Mitigation: Fallback to user profile languages or English if unsupported.

### Tests / Verification
- Not run (not requested).
- 
- **2026-01-20 18:14 (Asia/Taipei) — Add OCR language diagnostics**

### Summary
- Logged resolved OCR language and fallbacks to investigate language selection.

### Context / Goal
- Diagnose why OCR keeps using Japanese even with English pack installed.
- Surface which language tag and engine are actually used.

### Changes
- Added OCR language resolution logs with fallback notices.
- Injected AppLogger into OcrEngine.

### Files Touched
- Services/OcrEngine.cs — log requested/resolved OCR language and fallback cases.
- MainWindow.xaml.cs — pass logger into OcrEngine.

### Behavioral Impact
- Log output now includes OCR language selection details.

### Risk & Mitigation
- Risk: Additional log noise during repeated OCR runs.
- Mitigation: Logs only on language resolution and fallback paths.

### Tests / Verification
- Not run (not requested).**2026-01-21 11:32 (Asia/Taipei) — Draft PaddleOCR integration plan**

### Summary
- Added an implementation plan for PaddleOCR integration and selection in settings.

### Context / Goal
- Provide a concrete design for adding PaddleOCR alongside the existing WinRT OCR.
- Outline settings, UI, and process integration changes.

### Changes
- Authored Doc/Plan4.md with architecture, settings, and rollout steps.

### Files Touched
- Doc/Plan4.md — documented PaddleOCR integration plan and risks.

### Behavioral Impact
- No runtime changes; documentation only.

### Risk & Mitigation
- Risk: None (documentation-only).
- Mitigation: N/A.

### Tests / Verification
- Not run (documentation-only change).**2026-01-21 11:58 (Asia/Taipei) — Add overlay toggle hotkey**

### Summary
- Added a hotkey to toggle overlay visibility without being overridden by the pipeline.

### Context / Goal
- Allow users to show/hide the translation overlay on demand.
- Keep overlay state consistent across capture runs.

### Changes
- Added overlay enable/disable handling in the presenter to suppress updates when hidden.
- Registered F9 hotkey to toggle overlay visibility with logging.

### Files Touched
- Services/OverlayPresenter.cs — added enable state handling and toggle method.
- MainWindow.xaml.cs — registered F9 hotkey and wired toggle handler.

### Behavioral Impact
- Overlay can now be toggled with F9 and remains hidden during pipeline runs until re-enabled.

### Risk & Mitigation
- Risk: Users may hide overlay and forget it is disabled.
- Mitigation: Log messages on toggle indicate current state.

### Tests / Verification
- Not run (not requested).**2026-01-21 12:01 (Asia/Taipei) — Show overlay on run hotkey**

### Summary
- Ensure the overlay is re-enabled when the translation hotkey is used.

### Context / Goal
- Users expect the overlay to appear when running translation.
- Keep manual hide/show toggle but override it on capture.

### Changes
- Added a helper to re-enable overlay and called it before running the pipeline.

### Files Touched
- MainWindow.xaml.cs — enable overlay when running OCR/translation.

### Behavioral Impact
- Pressing F8 (or Run Once) now forces the overlay visible.

### Risk & Mitigation
- Risk: Users who intentionally hid the overlay may see it reappear on capture.
- Mitigation: This is the requested behavior; manual toggle remains for subsequent use.

### Tests / Verification
- Not run (not requested).**2026-01-21 13:42 (Asia/Taipei) — Clarify line-merge gate rules**

### Summary
- Clarified P_align as a hard gate and specified device-pixel coordinates in the algorithm doc.

### Context / Goal
- Align the algorithm document with the agreed implementation assumptions.
- Avoid ambiguity between gate checks and cost calculation.

### Changes
- Updated P_align description to be gate-only and excluded from cost.
- Stated that Rect coordinates are handled in device pixels.

### Files Touched
- Doc/Algorithm.md — clarified gate behavior and coordinate basis.

### Behavioral Impact
- No runtime changes; documentation only.

### Risk & Mitigation
- Risk: None (documentation-only).
- Mitigation: N/A.

### Tests / Verification
- Not run (documentation-only change).**2026-01-21 13:51 (Asia/Taipei) — Merge OCR lines into chunks**

### Summary
- Implemented line grouping per Algorithm B and wired it into the OCR pipeline.

### Context / Goal
- Combine nearby OCR lines into paragraph-like chunks using the documented clustering rules.
- Ensure translations and overlay operate on merged chunks.

### Changes
- Added an OCR line grouper with alignment gate and cost-based unioning.
- Routed pipeline OCR results through the line grouper before diff/translation.
- Introduced merge-related settings with defaults.

### Files Touched
- Services/OcrLineGrouper.cs — new clustering implementation for merging OCR lines.
- Services/PipelineOrchestrator.cs — apply grouping before diff/translation/overlay.
- MainWindow.xaml.cs — create and pass the line grouper into the pipeline.
- Models/AppSettings.cs — added line-merge settings defaults.

### Behavioral Impact
- OCR output is now merged into chunks before translation and overlay rendering.

### Risk & Mitigation
- Risk: Incorrect grouping could merge unrelated lines.
- Mitigation: Gate on horizontal overlap and tune thresholds via settings defaults.

### Tests / Verification
- Not run (not requested).**2026-01-21 15:01 (Asia/Taipei) — Add Gemini smoke test console**

### Summary
- Added a console smoke test project that reads Key.txt and calls Gemini.

### Context / Goal
- Allow testing Gemini translation output without running the WPF app.
- Read the API key from a local Key.txt file for quick validation.

### Changes
- Added a new console project and program to invoke GeminiClient.
- Read API key from Key.txt and print translations to stdout.

### Files Touched
- Tools/GeminiSmokeTest/GeminiSmokeTest.csproj — new console project referencing the main app.
- Tools/GeminiSmokeTest/Program.cs — smoke test entrypoint reading Key.txt and calling Gemini.

### Behavioral Impact
- No runtime changes to the WPF app; adds a separate test harness.

### Risk & Mitigation
- Risk: Accidental commit of Key.txt.
- Mitigation: Keep Key.txt out of version control.

### Tests / Verification
- Not run (not requested).**2026-01-21 15:14 (Asia/Taipei) — Fix duplicate assembly attributes**

### Summary
- Disabled SDK-generated assembly attributes to resolve duplicate attribute build errors.

### Context / Goal
- Resolve build failure when running the Gemini smoke test console.
- Prevent duplicate assembly attribute generation in the WPF project.

### Changes
- Turned off SDK assembly info and target framework attribute generation.

### Files Touched
- `Hotkey-Translator.csproj` — disabled auto assembly info and target framework attributes.

### Behavioral Impact
- Build now succeeds; assembly metadata is no longer auto-generated.

### Risk & Mitigation
- Risk: Missing assembly metadata (company/product/version) in the built app.
- Mitigation: Add explicit attributes later if needed.

### Tests / Verification
- `dotnet run --project Tools/GeminiSmokeTest`**2026-01-21 15:29 (Asia/Taipei) — Exclude doc code from build**

### Summary
- Excluded doc sample .cs files from the WPF project to remove CS7022 warnings.

### Context / Goal
- Remove the duplicate entry point warning caused by top-level statements in Doc sources.
- Keep the WPF project clean when building or running tests.

### Changes
- Excluded `Doc\**\*.cs` from compilation in the main project.

### Files Touched
- `Hotkey-Translator.csproj` — removed Doc .cs files from compile items.

### Behavioral Impact
- No runtime changes; build warnings are removed.

### Risk & Mitigation
- Risk: Doc sample code will no longer compile as part of the app.
- Mitigation: Keep doc samples in the Tools project instead.

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj`**2026-01-21 15:31 (Asia/Taipei) — Restore Gemini smoke test project**

### Summary
- Recreated the Gemini smoke test console project.

### Context / Goal
- Provide a standalone console runner for Gemini translation checks.
- Restore the Tools project after it went missing.

### Changes
- Added the console project file targeting net8.0-windows.
- Added the smoke test program that reads Key.txt and prints translations.

### Files Touched
- `Tools/GeminiSmokeTest/GeminiSmokeTest.csproj` — console project definition.
- `Tools/GeminiSmokeTest/Program.cs` — Gemini smoke test runner.

### Behavioral Impact
- No runtime changes to the WPF app; adds a separate test harness.

### Risk & Mitigation
- Risk: Accidental commit of Key.txt.
- Mitigation: Keep Key.txt out of version control.

### Tests / Verification
- Not run (not requested).**2026-01-21 16:18 (Asia/Taipei) — Add OCR/Gemini debug logs**

### Summary
- Added timing and status logs around OCR grouping and Gemini requests.

### Context / Goal
- Capture diagnostic logs from OCR output through Gemini response timing.
- Aid investigation when Gemini appears unresponsive.

### Changes
- Logged OCR recognition duration, grouped line counts, and diff counts.
- Logged Gemini pending counts plus request/response timing and parse results.

### Files Touched
- `Services/PipelineOrchestrator.cs` — log OCR timing, grouping counts, diff stats, and pending translation info.
- `Services/GeminiClient.cs` — log skip reasons, request size, HTTP timing/status, and parse results.
- `MainWindow.xaml.cs` — pass AppLogger into GeminiClient for UI log output.

### Behavioral Impact
- Log output now includes OCR/Gemini diagnostics; no change to translation behavior.

### Risk & Mitigation
- Risk: Increased log volume during frequent captures.
- Mitigation: Logs avoid OCR text content and only emit counts/timings.

### Tests / Verification
- Not run (logging-only change).**2026-01-21 16:33 (Asia/Taipei) — Cap Gemini output and fix schema casing**

### Summary
- Capped Gemini output tokens, corrected response schema casing, and logged response lengths.

### Context / Goal
- Prevent verbose Gemini outputs and enforce structured JSON responses.
- Capture JSON and translation length diagnostics for debugging.

### Changes
- Switched to camelCase response schema keys and set maxOutputTokens to 5000.
- Logged JSON text length and per-item translation lengths without content.

### Files Touched
- `Services/GeminiClient.cs` — adjust generationConfig casing, cap output tokens, and add length logs.

### Behavioral Impact
- Gemini responses are bounded to 5000 tokens and more reliably follow the JSON schema.

### Risk & Mitigation
- Risk: Some longer translations may be truncated due to token cap.
- Mitigation: Increase the cap later if needed after observing logs.

### Tests / Verification
- Not run (config/logging change only).**2026-01-21 16:51 (Asia/Taipei) — Apply Plan5 Gemini response fixes**

### Summary
- Adjusted Gemini request/response handling to avoid thought parts and enforce correct API keys.

### Context / Goal
- Prevent oversized responses and parsing failures caused by thought parts or ignored safety settings.
- Align request payload keys with Gemini API expectations.

### Changes
- Switched `safetySettings` to camelCase and added `thinkingConfig` to suppress thought parts.
- Updated JSON extraction to prefer non-thought parts in multi-part responses.

### Files Touched
- `Services/GeminiClient.cs` — corrected request key casing, added thinkingConfig, and improved JSON text extraction.

### Behavioral Impact
- Gemini responses should be smaller and parsed reliably even when multi-part outputs are returned.

### Risk & Mitigation
- Risk: Some models might ignore thinkingConfig fields.
- Mitigation: ExtractJsonText now skips thought parts when present.

### Tests / Verification
- Not run (not requested).**2026-01-21 17:33 (Asia/Taipei) — Scale overlay font by OCR height**

### Summary
- Derived overlay font size from OCR rectangle height to better match large text.

### Context / Goal
- Large OCR boxes were rendered with the default small font, causing mismatched overlay sizes.
- Use OCR geometry to scale font size per item.

### Changes
- Calculated per-item font size from rect height and line count with padding awareness.
- Introduced clamp limits and reused padding constants for consistent layout.

### Files Touched
- `UI/OverlayWindow.xaml.cs` — compute font size based on OCR rect height and line count.

### Behavioral Impact
- Overlay text size now scales to the OCR bounding box height instead of a fixed font size.

### Risk & Mitigation
- Risk: Some very tall boxes may yield oversized fonts or truncation.
- Mitigation: Font size is clamped to a reasonable range and falls back to the default on invalid sizes.

### Tests / Verification
- Not run (UI change only).**2026-01-21 18:01 (Asia/Taipei) — Fit overlay font by measured text size**

### Summary
- Switched overlay font sizing to use measured text fit within OCR rectangles.

### Context / Goal
- Normalize perceived font size across languages by measuring rendered text size.
- Avoid script-specific font metric differences that skew manual scaling.

### Changes
- Calculated per-item font size with FormattedText and binary search fit.
- Added padding-aware measurement bounds with min/max font clamps.

### Files Touched
- `UI/OverlayWindow.xaml.cs` — measure text size and fit font to OCR bounds.

### Behavioral Impact
- Overlay text now scales to fit each OCR rectangle based on actual rendered size.

### Risk & Mitigation
- Risk: Extra per-item measurement could impact frame latency.
- Mitigation: Fixed small iteration count and early fallbacks for invalid bounds.

### Tests / Verification
- Not run (UI change only).**2026-01-21 18:09 (Asia/Taipei) — Anchor font size to OCR line height**

### Summary
- Adjusted overlay font sizing to use OCR line height as the base and only nudge with measured text size.

### Context / Goal
- Prevent oversized fonts by grounding scaling to OCR-derived line height.
- Keep language differences in check with small measurement-based adjustments.

### Changes
- Added line count to OverlayItem and propagated it through pipeline/presenter.
- Updated overlay font sizing to base on OCR line height with capped adjustment.

### Files Touched
- `Models/OverlayItem.cs` — include OCR line count for overlay sizing.
- `Services/PipelineOrchestrator.cs` — compute OCR line count when building overlay items.
- `Services/OverlayPresenter.cs` — preserve line count when converting to DIP.
- `UI/OverlayWindow.xaml.cs` — base font size on OCR line height with measured nudge.

### Behavioral Impact
- Overlay font size now follows OCR line height instead of filling the entire bounding box.

### Risk & Mitigation
- Risk: Some translations may still overflow if text expands significantly.
- Mitigation: Font size is capped and adjusted conservatively; can tune ratios if needed.

### Tests / Verification
- Not run (UI change only).**2026-01-21 18:27 (Asia/Taipei) — Add ROI enable toggle**

### Summary
- Added UI and settings support to enable/disable ROI usage.

### Context / Goal
- Let users temporarily bypass ROI without clearing the saved rectangle.
- Apply ROI only when explicitly enabled in settings.

### Changes
- Added an Enable ROI checkbox in the settings UI.
- Persisted EnableRoi in settings and honored it in ROI bounds calculation.
- Updated ROI status display to reflect disabled state.

### Files Touched
- `Models/AppSettings.cs` — added EnableRoi setting.
- `MainWindow.xaml` — added Enable ROI checkbox control.
- `MainWindow.xaml.cs` — load/save EnableRoi and update ROI status on save.
- `Services/PipelineOrchestrator.cs` — skip ROI bounds when disabled.

### Behavioral Impact
- ROI is ignored when disabled, so OCR uses the full capture frame until re-enabled.

### Risk & Mitigation
- Risk: Users may think ROI is active when disabled.
- Mitigation: UI status now shows "ROI: disabled" after saving settings.

### Tests / Verification
- Not run (UI/settings change only).**2026-01-21 18:47 (Asia/Taipei) — Plan6 overlay auto-fit design**

### Summary
- Documented the recommended design for auto-fitting overlay frames and font sizes.

### Context / Goal
- Provide a concrete implementation plan to fix small overlay text on large OCR boxes.
- Capture a low-risk auto-fit strategy based on OCR line metrics.

### Changes
- Added Plan6 design covering model changes, pipeline wiring, and overlay measurement logic.

### Files Touched
- Doc/Plan6.md — added the implementation plan for overlay auto-fit sizing.

### Behavioral Impact
- No runtime behavior changes (documentation only).

### Risk & Mitigation
- Risk: None (documentation-only change).
- Mitigation: N/A.

### Tests / Verification
- 未実施（設計ドキュメント追記のみ）**2026-01-21 19:05 (Asia/Taipei) — Auto-fit overlay sizing from OCR lines**

### Summary
- Implemented overlay auto-fit sizing based on OCR line height and line count.

### Context / Goal
- Make overlay text and boxes scale to large OCR regions instead of staying small.
- Use OCR-derived metrics and only shrink when text would overflow.

### Changes
- Added line count/line height metadata through OCR grouping to overlay rendering.
- Implemented font-size resolution and measurement-based shrink to fit bounds.
- Applied DPI-aware scaling for line height data.

### Files Touched
- Models/OcrLine.cs — extended OCR line metadata to include line count and height.
- Models/OverlayItem.cs — added line count/height for overlay sizing.
- Services/OcrEngine.cs — populate per-line height from OCR rectangles.
- Services/OcrLineGrouper.cs — compute group line count and median line height.
- Services/PipelineOrchestrator.cs — pass OCR line metrics into overlay items.
- Services/OverlayPresenter.cs — scale line height when converting to DIP.
- UI/OverlayWindow.xaml.cs — compute font size from OCR metrics and fit within bounds.

### Behavioral Impact
- Overlay font size and box sizing now scale with OCR line height and group size.

### Risk & Mitigation
- Risk: Extra text measurement could add UI overhead on dense overlays.
- Mitigation: Limit shrink fitting to a fixed number of iterations and skip when bounds are invalid.

### Tests / Verification
- 未実施（UI調整のため）**2026-01-22 10:13 (Asia/Taipei) — Update Plan4 for uv-based PaddleOCR**

### Summary
- Rewrote Plan4 to align with current code and uv-based Python execution.

### Context / Goal
- Reflect the current WPF pipeline and settings model.
- Define a uv-centered PaddleOCR integration plan with safe fallback.

### Changes
- Updated Plan4 design, settings, UI, provider abstraction, and uv setup steps.

### Files Touched
- Doc/Plan4.md — revised PaddleOCR integration plan with uv workflow.

### Behavioral Impact
- No runtime behavior changes (documentation only).

### Risk & Mitigation
- Risk: None (documentation-only change).
- Mitigation: N/A.

### Tests / Verification
- 未実施（設計ドキュメント更新のみ）**2026-01-22 10:23 (Asia/Taipei) — Add PaddleOCR init guide**

### Summary
- Added a uv-based PaddleOCR setup guide.

### Context / Goal
- Provide concrete installation steps aligned with Plan4.
- Document uv project layout, dependencies, and app settings.

### Changes
- Authored PaddleOCR initialization instructions.

### Files Touched
- Doc/PaddleOCR-init.md — added uv setup and configuration steps.

### Behavioral Impact
- No runtime behavior changes (documentation only).

### Risk & Mitigation
- Risk: None (documentation-only change).
- Mitigation: N/A.

### Tests / Verification
- 未実施（ドキュメント追加のみ）**2026-01-22 10:33 (Asia/Taipei) — Implement PaddleOCR provider integration**

### Summary
- Added PaddleOCR provider wiring with uv execution and settings/UI support.

### Context / Goal
- Implement Plan4 and PaddleOCR-init to allow selecting PaddleOCR with fallback to WinRT.
- Keep the existing OCR pipeline intact while adding provider abstraction.

### Changes
- Added OCR engine selection and Paddle settings in models and UI.
- Refactored OCR into provider-based architecture with WinRT and Paddle providers.
- Implemented uv-based PaddleOCR process runner and JSON parsing.

### Files Touched
- Models/AppSettings.cs — added OCR engine and Paddle settings fields.
- Models/OcrEngineKind.cs — new OCR engine enum.
- Services/IOcrProvider.cs — introduced provider interface.
- Services/OcrEngine.cs — orchestrates provider selection and fallback.
- Services/WinRtOcrProvider.cs — extracted WinRT OCR implementation.
- Services/PaddleOcrProvider.cs — added uv-based PaddleOCR runner and parser.
- Services/PipelineOrchestrator.cs — pass settings into OCR engine.
- MainWindow.xaml — added OCR engine and Paddle settings inputs.
- MainWindow.xaml.cs — load/save Paddle settings and engine selection.

### Behavioral Impact
- OCR can now be switched to PaddleOCR via settings; failures fall back to WinRT.

### Risk & Mitigation
- Risk: Paddle environment misconfiguration or missing uv/script causes OCR failures.
- Mitigation: Errors are logged and WinRT fallback is used automatically.

### Tests / Verification
- 未実施（環境依存のため）**2026-01-22 10:52 (Asia/Taipei) — Add Plan7 on-demand PaddleOCR setup**

### Summary
- Added an implementation plan for on-demand PaddleOCR setup triggered by UI selection.

### Context / Goal
- Outline automatic uv/Python setup when users choose PaddleOCR.
- Keep WinRT OCR as a safe fallback during setup failures.

### Changes
- Authored Plan7 covering settings, UI flow, setup manager, and risks.

### Files Touched
- Doc/Plan7.md — new plan for on-demand PaddleOCR install flow.

### Behavioral Impact
- No runtime behavior changes (documentation only).

### Risk & Mitigation
- Risk: None (documentation-only change).
- Mitigation: N/A.

### Tests / Verification
- 未実施（ドキュメント追加のみ）**2026-01-22 14:02 (Asia/Taipei) — Add PaddleOCR-VL vLLM integration plan**

### Summary
- Added a vLLM-based PaddleOCR-VL integration plan.

### Context / Goal
- Document how to connect PaddleOCR-VL via a remote vLLM server.
- Define settings/UI and provider changes for OpenAI-compatible OCR.

### Changes
- Authored a new plan for PaddleOCR-VL provider integration.

### Files Touched
- Doc/PaddleOCR-VL-Plan.md — added vLLM integration plan.

### Behavioral Impact
- No runtime behavior changes (documentation only).

### Risk & Mitigation
- Risk: None (documentation-only change).
- Mitigation: N/A.

### Tests / Verification
- 未実施（ドキュメント追加のみ）**2026-01-22 14:05 (Asia/Taipei) — Tweak overlay auto-fit sizing**

### Summary
- Tuned overlay font sizing constants to reduce text overflow.

### Context / Goal
- Make overlay text fit within OCR boxes more reliably.
- Prefer smaller base font size and tighter max bounds.

### Changes
- Reduced line-height scale and max font size, increased fit iterations.

### Files Touched
- UI/OverlayWindow.xaml.cs — adjusted auto-fit sizing constants.

### Behavioral Impact
- Overlay text shrinks slightly more and should overflow less often.

### Risk & Mitigation
- Risk: Text may become slightly smaller than desired in some cases.
- Mitigation: Constants are centralized for quick retuning.

### Tests / Verification
- 未実施（UI調整のため）**2026-01-22 14:29 (Asia/Taipei) — Scale overlay font by screen occupancy**

### Summary
- Adjusted base font sizing to scale with OCR box height relative to screen height.

### Context / Goal
- Allow larger text for large OCR regions while shrinking text for small boxes.
- Reduce overflow by making sizing more conservative for low-occupancy boxes.

### Changes
- Replaced fixed line-height scale with occupancy-based scaling in overlay font sizing.

### Files Touched
- UI/OverlayWindow.xaml.cs — added occupancy-based scale calculation for base font size.

### Behavioral Impact
- Overlay text size now varies based on how tall the OCR box is relative to the screen.

### Risk & Mitigation
- Risk: Some layouts may appear smaller than before for short boxes.
- Mitigation: Min/max scales are centralized for quick retuning.

### Tests / Verification
- 未実施（UI調整のため）**2026-01-22 14:40 (Asia/Taipei) — Normalize overlay line breaks**

### Summary
- Normalized overlay text line breaks to respect OCR line counts.

### Context / Goal
- Prevent overflow when translation inserts extra line breaks beyond the OCR box.
- Keep overlay text within the original grouped line count.

### Changes
- Added overlay text normalization that collapses extra lines into the final line.
- Applied normalization when building overlay items.

### Files Touched
- Services/PipelineOrchestrator.cs — normalize overlay text based on line count.

### Behavioral Impact
- Translated text with extra line breaks is collapsed to fit the OCR line count.

### Risk & Mitigation
- Risk: Some translations may appear less readable due to removed line breaks.
- Mitigation: Only line breaks beyond the OCR line count are collapsed.

### Tests / Verification
- 未実施（UI調整のため）**2026-01-22 14:52 (Asia/Taipei) — Refresh Plan2 for translation priority UI**

### Summary
- Updated Plan2 to match current code and allow user-controlled translation priority.

### Context / Goal
- Align the translation plan with the current Gemini-only pipeline.
- Define a user-editable priority order for translation services.

### Changes
- Rewrote Plan2 to include provider abstraction, fallback service, and priority UI/setting.

### Files Touched
- Doc/Plan2.md — updated implementation plan and priority selection design.

### Behavioral Impact
- No runtime behavior changes (documentation only).

### Risk & Mitigation
- Risk: None (documentation-only change).
- Mitigation: N/A.

### Tests / Verification
- 未実施（ドキュメント更新のみ）**2026-01-22 14:54 (Asia/Taipei) — Add DeepL/GoogleWeb details to Plan2**

### Summary
- Appended concrete implementation notes for DeepL and GoogleWeb translation providers.

### Context / Goal
- Expand Plan2 with service-specific implementation details.
- Document error handling and fallback considerations.

### Changes
- Added DeepL and GoogleWeb provider detail section to Plan2.

### Files Touched
- Doc/Plan2.md — appended DeepL/GoogleWeb provider implementation details.

### Behavioral Impact
- No runtime behavior changes (documentation only).

### Risk & Mitigation
- Risk: None (documentation-only change).
- Mitigation: N/A.

### Tests / Verification
- 未実施（ドキュメント更新のみ）**2026-01-22 14:57 (Asia/Taipei) — Add DeepL/GoogleWeb implementation doc**

### Summary
- Created a separate implementation note for DeepL and GoogleWeb providers.

### Context / Goal
- Keep Plan2 clean while documenting service-specific details elsewhere.
- Provide concrete failure handling and priority guidance.

### Changes
- Added DeepL/GoogleWeb implementation guidance document.

### Files Touched
- Doc/DeepL-GoogleWeb.md — new implementation plan for DeepL and GoogleWeb.

### Behavioral Impact
- No runtime behavior changes (documentation only).

### Risk & Mitigation
- Risk: None (documentation-only change).
- Mitigation: N/A.

### Tests / Verification
- 未実施（ドキュメント追加のみ）**2026-01-22 15:08 (Asia/Taipei) — Add CTranslate2+MarianMT plan**

### Summary
- Added a plan for integrating CTranslate2 with MarianMT.

### Context / Goal
- Document a local translation provider using uv + Python bridge.
- Align CT/Marian with the translation fallback architecture.

### Changes
- Authored CTranslate2+MarianMT implementation plan.

### Files Touched
- Doc/CTMTPlan.md — new integration plan for CT/Marian.

### Behavioral Impact
- No runtime behavior changes (documentation only).

### Risk & Mitigation
- Risk: None (documentation-only change).
- Mitigation: N/A.

### Tests / Verification
- 未実施（ドキュメント追加のみ）**2026-01-22 15:20 (Asia/Taipei) — Implement translation priority and fallback**

### Summary
- Added translation provider abstraction with user-controlled priority order.

### Context / Goal
- Implement Plan2 to allow translation service fallback in configurable priority order.
- Keep the existing Gemini translation while preparing for additional providers.

### Changes
- Added translation provider interface, Gemini provider wrapper, and fallback service.
- Added translation priority list setting and UI for reordering.
- Routed pipeline translation through the fallback service.

### Files Touched
- Models/AppSettings.cs — added translation priority list setting.
- Models/TranslationProviderNames.cs — defined provider name constants and defaults.
- Services/ITranslationProvider.cs — new translation provider interface.
- Services/GeminiTranslationProvider.cs — wraps GeminiClient as a provider.
- Services/TranslationFallbackService.cs — provider ordering and fallback logic.
- Services/PipelineOrchestrator.cs — use fallback service for translations.
- MainWindow.xaml — added translation priority UI.
- MainWindow.xaml.cs — load/save and reorder translation priority list.

### Behavioral Impact
- Users can reorder translation service priority; translation attempts follow the chosen order.

### Risk & Mitigation
- Risk: Priority list could be empty or contain unknown providers.
- Mitigation: Defaults are appended and unknown providers are skipped with logs.

### Tests / Verification
- 未実施（UI/ロジック更新のため）**' + $stamp + ' (Asia/Taipei) — Verify build after Plan2 implementation**

### Summary
- Ran a build to verify the translation priority implementation compiles.

### Context / Goal
- Confirm the new translation priority and fallback changes compile cleanly.
- Update verification status after implementation.

### Changes
- No code changes; build verification only.

### Files Touched
- None.

### Behavioral Impact
- No runtime behavior changes.

### Risk & Mitigation
- Risk: None.
- Mitigation: N/A.

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj`**2026-01-22 15:27 (Asia/Taipei) — Implement DeepL translation provider**

### Summary
- Added DeepL translation provider with settings and UI wiring.

### Context / Goal
- Implement the DeepL portion of Doc/DeepL-GoogleWeb.md.
- Enable DeepL usage within the translation fallback order.

### Changes
- Added DeepL settings (API key, endpoint, enable flag) with DPAPI protection.
- Implemented DeepLTranslationProvider and registered it in the fallback chain.
- Added UI inputs for DeepL configuration and persistence.

### Files Touched
- `Models/AppSettings.cs` — add DeepL settings and protected key fields.
- `Services/SettingsService.cs` — protect/unprotect DeepL API key.
- `Services/DeepLTranslationProvider.cs` — new DeepL API implementation.
- `MainWindow.xaml` — add DeepL controls.
- `MainWindow.xaml.cs` — load/save DeepL settings and register provider.

### Behavioral Impact
- Users can enable DeepL and use it in the translation priority order.

### Risk & Mitigation
- Risk: DeepL endpoint/key misconfiguration leads to empty translations.
- Mitigation: Errors are logged and fallback continues to the next provider.

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj`**' + $stamp + ' (Asia/Taipei) — Add BetterUIPlan**

### Summary
- Documented a structured plan to simplify the settings UI.

### Context / Goal
- Reduce UI complexity as settings grow.
- Group and hide advanced options without losing functionality.

### Changes
- Added a plan for sectioned UI layout, basic/advanced split, and status indicators.

### Files Touched
- `Doc/BetterUIPlan.md` — added UI improvement plan.

### Behavioral Impact
- No runtime behavior changes (documentation only).

### Risk & Mitigation
- Risk: None (documentation-only change).
- Mitigation: N/A.

### Tests / Verification
- 未実施（ドキュメント追加のみ）**' + $stamp + ' (Asia/Taipei) — Rework settings UI layout**

### Summary
- Reorganized the settings UI into collapsible sections with a translation status line.

### Context / Goal
- Reduce UI clutter as settings grew and follow BetterUIPlan layout goals.
- Keep advanced options accessible without overwhelming the main view.

### Changes
- Replaced the left panel with sectioned expanders and a scrollable layout.
- Grouped OCR/Paddle settings and translation advanced inputs under nested expanders.
- Added translation status text to reflect Gemini/DeepL enablement and key presence.

### Files Touched
- `MainWindow.xaml` — reorganized UI into expanders and added status/advanced sections.
- `MainWindow.xaml.cs` — added translation status update hook.

### Behavioral Impact
- Settings UI is now grouped and collapsible; translation status is shown inline.

### Risk & Mitigation
- Risk: Users may miss advanced options if expanders are collapsed.
- Mitigation: Sections are labeled clearly and remain expandable.

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj`**' + $stamp + ' (Asia/Taipei) — Split UI into Main/Settings tabs**

### Summary
- Moved frequent controls to a Main tab and grouped detailed options under a Settings tab.

### Context / Goal
- Reduce main screen complexity and keep advanced options in categorized settings.
- Remove the Run Once button per request.

### Changes
- Replaced the left panel with a TabControl (Main/Settings).
- Moved advanced OCR/Paddle/translation settings into collapsible sections.
- Removed the Run Once button from the UI.

### Files Touched
- `MainWindow.xaml` — reorganized layout into tabs and moved controls.

### Behavioral Impact
- Main tab now shows only frequent controls; detailed settings are under Settings tab.

### Risk & Mitigation
- Risk: Users may not find moved options.
- Mitigation: Settings tab names sections clearly and keeps advanced items expandable.

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj` (failed: apphost.exe locked by running app)**' + $stamp + ' (Asia/Taipei) — Add Settings side panel**

### Summary
- Implemented a side-panel category selector for Settings.

### Context / Goal
- Replace nested expanders with a left-hand category list and right-hand details.
- Keep detailed settings grouped but easier to navigate.

### Changes
- Added Settings category list with panel visibility switching.
- Moved OCR/Paddle/Translation advanced controls into category panels.

### Files Touched
- `MainWindow.xaml` — added side-panel layout and category list.
- `MainWindow.xaml.cs` — handle category selection and panel visibility.

### Behavioral Impact
- Settings tab now uses a side-panel layout for advanced sections.

### Risk & Mitigation
- Risk: Default category not selected on first load.
- Mitigation: Auto-select the first category on load.

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj`**' + $stamp + ' (Asia/Taipei) — Fix Settings category init crash**

### Summary
- Guarded Settings panel selection handling to avoid null refs during XAML initialization.

### Context / Goal
- App crashed on startup due to SelectionChanged firing before panel fields were assigned.
- Ensure category switching only runs once controls are initialized.

### Changes
- Added null checks in Settings category panel update method.

### Files Touched
- `MainWindow.xaml.cs` — guard against early SelectionChanged during InitializeComponent.

### Behavioral Impact
- App no longer crashes during startup when Settings tab loads.

### Risk & Mitigation
- Risk: Category panels may not update if called before initialization.
- Mitigation: Update runs again after load via existing selection initialization.

### Tests / Verification
- `dotnet run --project Hotkey-Translator.csproj` (app ran; command timed out while app stayed open)**' + $stamp + ' (Asia/Taipei) — Add log splitter and min widths**

### Summary
- Added a column splitter and minimum widths to prevent the log pane from clipping settings.

### Context / Goal
- Avoid layout clipping by allowing the log pane to be resized.
- Ensure both panes keep a usable minimum width.

### Changes
- Inserted a GridSplitter between the settings tabs and log pane.
- Set minimum widths for the left panel and log panel.

### Files Touched
- `MainWindow.xaml` — added a GridSplitter and column min widths.

### Behavioral Impact
- Users can resize the log pane; settings stay readable.

### Risk & Mitigation
- Risk: Splitter may feel narrow for some users.
- Mitigation: Width is a single constant and easy to adjust.

### Tests / Verification
- 未実施（UI調整のため）**' + $stamp + ' (Asia/Taipei) — Auto-save settings on change**

### Summary
- Removed the Save Settings button and save changes on interaction.

### Context / Goal
- Persist settings automatically when users change values.
- Save text fields on focus loss and toggles/selectors immediately.

### Changes
- Removed Save Settings UI and handler.
- Added auto-save handlers for selection/click/blur events.
- Centralized UI-to-settings updates and guarded against init-time saves.

### Files Touched
- `MainWindow.xaml` — removed Save Settings button, added auto-save events.
- `MainWindow.xaml.cs` — added auto-save logic and removed manual save handler.

### Behavioral Impact
- Settings are persisted when controls change or text boxes lose focus.

### Risk & Mitigation
- Risk: Extra saves when users adjust settings frequently.
- Mitigation: Text fields only save on focus loss; init-time changes are suppressed.

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj`**' + $stamp + ' (Asia/Taipei) — Add language dropdowns with swap**

### Summary
- Replaced manual language entry with dropdowns, custom input, and a swap action.

### Context / Goal
- Offer quick language selection for common pairs while retaining custom entry.
- Make source/target swapping a single action.

### Changes
- Added source/target language dropdowns with a Custom option and swap button.
- Wired selection changes to auto-save and toggled custom input visibility.
- Updated settings load/save to use dropdown selection or custom text.

### Files Touched
- `MainWindow.xaml` — added language dropdowns, custom input, and swap button.
- `MainWindow.xaml.cs` — handle language selection, swap logic, and saving.

### Behavioral Impact
- Users can choose preset languages or enter custom codes and swap source/target quickly.

### Risk & Mitigation
- Risk: Custom language codes may be invalid.
- Mitigation: Existing OCR/translation fallbacks handle unsupported tags.

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj`**' + $stamp + ' (Asia/Taipei) — Widen window and settings panel**

### Summary
- Increased the default window width and widened the settings panel.

### Context / Goal
- Avoid settings tab clipping at the default window size.
- Keep the Select ROI button width consistent.

### Changes
- Increased default window width and left panel width.
- Widened settings category list column.
- Set a fixed width for the Select ROI button.

### Files Touched
- `MainWindow.xaml` — adjusted window width, column sizes, and button width.

### Behavioral Impact
- Settings tab has more horizontal space; Select ROI button is fixed width.

### Risk & Mitigation
- Risk: Wider default window may not fit smaller screens.
- Mitigation: Min widths still allow resizing if needed.

### Tests / Verification
- 未実施（UI調整のため）**' + $stamp + ' (Asia/Taipei) — Left-align Select ROI button**

### Summary
- Anchored the Select ROI button to the left edge.

### Context / Goal
- Keep the Select ROI button consistently left-aligned in the main panel.

### Changes
- Set `HorizontalAlignment` to `Left` for the Select ROI button.

### Files Touched
- `MainWindow.xaml` — left-aligned the Select ROI button.

### Behavioral Impact
- Select ROI button stays fixed to the left in the main panel.

### Risk & Mitigation
- Risk: None.
- Mitigation: N/A.

### Tests / Verification
- 未実施（UI調整のため）**' + $stamp + ' (Asia/Taipei) — Fix capture dropdown alignment**

### Summary
- Set a fixed width and left alignment for the capture mode dropdown.

### Context / Goal
- Keep the capture dropdown visually aligned with left-edge controls.

### Changes
- Applied width and left alignment to `CaptureModeBox`.

### Files Touched
- `MainWindow.xaml` — adjusted capture dropdown layout.

### Behavioral Impact
- Capture mode dropdown is now fixed width and left-aligned.

### Risk & Mitigation
- Risk: None.
- Mitigation: N/A.

### Tests / Verification
- 未実施（UI調整のため）**' + $stamp + ' (Asia/Taipei) — Align Main tab layout**

### Summary
- Realigned Main tab controls into consistent label/value grids for readability.

### Context / Goal
- Make the Main tab layout visually consistent and easier to scan.
- Align labels, inputs, and action buttons across sections.

### Changes
- Replaced stacked rows with grid-based layouts for Capture/OCR/Translation.
- Aligned labels and inputs to fixed columns and unified button sizing.
- Corrected language label text rendering for preset language items.

### Files Touched
- `MainWindow.xaml` — restructured Main tab layout into aligned grids.

### Behavioral Impact
- Main tab controls now align consistently; swap and enable toggles are grouped in-row.

### Risk & Mitigation
- Risk: Layout may feel tighter on narrow windows.
- Mitigation: Controls keep widths and panels remain resizable via splitter.

### Tests / Verification
- 未実施（UI調整のため）**' + $stamp + ' (Asia/Taipei) — Fix ROI/Swap alignment**

### Summary
- Fixed the ROI enable checkbox and swap button positions by using fixed column widths.

### Context / Goal
- Prevent ROI enable and Swap controls from shifting when the panel width changes.
- Keep Main tab layout visually stable.

### Changes
- Set fixed column widths and left alignment for Capture/OCR/Translation grids.
- Fixed widths and left alignment for the ROI checkbox and Swap button.

### Files Touched
- `MainWindow.xaml` — adjusted grid column widths and control alignment.

### Behavioral Impact
- ROI enable checkbox and Swap button stay in fixed positions regardless of panel width.

### Risk & Mitigation
- Risk: Fixed column widths may reduce flexibility on narrow windows.
- Mitigation: Panels are still resizable and columns are consistent across sections.

### Tests / Verification
- 未実施（UI調整のため）**' + $stamp + ' (Asia/Taipei) — Add DeepL auth fix note**

### Summary
- Documented the fix for DeepL 403 legacy auth errors.

### Context / Goal
- Capture the required switch from form auth to header auth.
- Provide clear implementation steps and test guidance.

### Changes
- Added DeepL fix document with header-based auth guidance.

### Files Touched
- `Doc/DeeplFix.md` — new DeepL auth fix plan.

### Behavioral Impact
- No runtime changes (documentation only).

### Risk & Mitigation
- Risk: None (documentation-only change).
- Mitigation: N/A.

### Tests / Verification
- 未実施（ドキュメント追加のみ）**' + $stamp + ' (Asia/Taipei) — Update DeepL auth to header**

### Summary
- Switched DeepL authentication to header-based auth.

### Context / Goal
- Fix 403 errors caused by deprecated form-body auth.
- Align with DeepL’s header auth requirement.

### Changes
- Send `Authorization: DeepL-Auth-Key` header and remove form auth_key.
- Added a targeted 403 log hint for legacy auth errors.

### Files Touched
- `Services/DeepLTranslationProvider.cs` — moved auth_key to header auth.

### Behavioral Impact
- DeepL requests now authenticate via headers and should succeed with valid keys.

### Risk & Mitigation
- Risk: Misconfigured API key still fails.
- Mitigation: Errors are logged and fallback continues.

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj`**' + $stamp + ' (Asia/Taipei) — Use swap icon for language button**

### Summary
- Replaced the Swap button text with a switch-style glyph icon.

### Context / Goal
- Use a compact visual icon for the language swap action.
- Keep the layout aligned with the new grid-based main panel.

### Changes
- Updated the Swap button to show a Segoe MDL2 Assets glyph and tooltip.

### Files Touched
- `MainWindow.xaml` — replaced Swap button content with an icon.

### Behavioral Impact
- Swap button now displays an icon instead of text.

### Risk & Mitigation
- Risk: Glyph may render as a missing character on non-Windows fonts.
- Mitigation: Uses Windows default MDL2 font; can fall back to text if needed.

### Tests / Verification
- 未実施（UI調整のため）**' + $stamp + ' (Asia/Taipei) — Reposition swap button**

### Summary
- Centered the swap button between the source/target language dropdowns.

### Context / Goal
- Keep the swap control visually centered between the two language selectors.

### Changes
- Adjusted swap button row/rowspan and alignment in the translation grid.

### Files Touched
- `MainWindow.xaml` — moved swap button to the middle of the dropdown pair.

### Behavioral Impact
- Swap button now sits between the two language menus instead of aligning with only the target row.

### Risk & Mitigation
- Risk: None.
- Mitigation: N/A.

### Tests / Verification
- 未実施（UI調整のため）**' + $stamp + ' (Asia/Taipei) — Pull swap button closer**

### Summary
- Reduced the spacing between the language dropdowns and the swap button.

### Context / Goal
- Bring the swap icon closer to the language selectors for tighter alignment.

### Changes
- Narrowed the swap column width and reduced its left margin.

### Files Touched
- `MainWindow.xaml` — adjusted swap button column width and margin.

### Behavioral Impact
- Swap icon sits closer to the dropdowns.

### Risk & Mitigation
- Risk: None.
- Mitigation: N/A.

### Tests / Verification
- 未実施（UI調整のため）**' + $stamp + ' (Asia/Taipei) — Center swap button between language selects**

### Summary
- Positioned the Swap button between the Source and Target dropdowns.

### Context / Goal
- Align the swap control vertically between the two language selectors.

### Changes
- Moved the swap button into a dedicated column with row span.
- Rewrote the translation section to use a fixed grid layout.

### Files Touched
- `MainWindow.xaml` — updated translation section layout for swap placement.

### Behavioral Impact
- Swap button now sits between the Source and Target language dropdowns.

### Risk & Mitigation
- Risk: None.
- Mitigation: N/A.

### Tests / Verification
- 未実施（UI調整のため）**' + $stamp + ' (Asia/Taipei) — Refine Paddle OCR settings layout**

### Summary
- Moved the Paddle Device input to its own row and tightened spacing.

### Context / Goal
- Improve readability by giving the Device field its own line.
- Reduce vertical gaps between Paddle OCR labels and inputs.

### Changes
- Separated Device into a dedicated row.
- Reduced vertical margins within the Paddle OCR section.

### Files Touched
- `MainWindow.xaml` — adjusted Paddle OCR layout spacing and rows.

### Behavioral Impact
- Paddle OCR settings appear more compact and evenly spaced.

### Risk & Mitigation
- Risk: None.
- Mitigation: N/A.

### Tests / Verification
- 未実施（UI調整のため）**' + $stamp + ' (Asia/Taipei) — Tighten Paddle label spacing**

### Summary
- Reduced Paddle OCR label widths to bring inputs closer.

### Context / Goal
- Decrease horizontal spacing between labels and input fields in the Paddle OCR section.

### Changes
- Reduced label widths for Project/Language/Device/uv path/Model dir.

### Files Touched
- `MainWindow.xaml` — adjusted Paddle OCR label widths.

### Behavioral Impact
- Paddle OCR labels sit closer to their input fields.

### Risk & Mitigation
- Risk: Long labels may wrap if window is narrow.
- Mitigation: Labels are still short and section width is larger by default.

### Tests / Verification
- 未実施（UI調整のため）**' + $stamp + ' (Asia/Taipei) — Review OCR preprocessing doc**

### Summary
- Reviewed OCR-Image-Preprocessing doc and documented findings.

### Context / Goal
- Provide a review with performance considerations for OCR preprocessing guidance.

### Changes
- Added a review document noting the source doc is empty and listing suggested perf topics.

### Files Touched
- `Doc/OCR-Image-Preprocessing-Review.md` — new review output.

### Behavioral Impact
- No runtime behavior changes (documentation only).

### Risk & Mitigation
- Risk: None (documentation-only change).
- Mitigation: N/A.

### Tests / Verification
- 未実施（ドキュメント追加のみ）**' + $stamp + ' (Asia/Taipei) — Review OCR preprocessing doc (updated)**

### Summary
- Reviewed OCR-Image-Preprocessing with performance considerations and noted encoding issues.

### Context / Goal
- Provide a review that highlights risks and performance constraints.

### Changes
- Added updated review findings and recommendations.

### Files Touched
- `Doc/OCR-Image-Preprocessing-Review.md` — updated review output.

### Behavioral Impact
- No runtime behavior changes (documentation only).

### Risk & Mitigation
- Risk: None (documentation-only change).
- Mitigation: N/A.

### Tests / Verification
- 未実施（ドキュメント更新のみ）**' + $stamp + ' (Asia/Taipei) — Review OCR preprocessing doc (revised)**

### Summary
- Re-reviewed OCR-Image-Preprocessing with detailed findings and perf notes.

### Context / Goal
- Provide an updated review after the document was filled in.

### Changes
- Updated review findings and recommendations.

### Files Touched
- `Doc/OCR-Image-Preprocessing-Review.md` — refreshed review output.

### Behavioral Impact
- No runtime behavior changes (documentation only).

### Risk & Mitigation
- Risk: None (documentation-only change).
- Mitigation: N/A.

### Tests / Verification
- 未実施（ドキュメント更新のみ）**' + $stamp + ' (Asia/Taipei) — Review OCR preprocessing EN doc**

### Summary
- Reviewed the English OCR preprocessing document with performance-focused findings.

### Context / Goal
- Provide an updated review based on the English version.

### Changes
- Rewrote the review to reference the EN document and its risks.

### Files Touched
- `Doc/OCR-Image-Preprocessing-Review.md` — updated review output.

### Behavioral Impact
- No runtime behavior changes (documentation only).

### Risk & Mitigation
- Risk: None (documentation-only change).
- Mitigation: N/A.

### Tests / Verification
- 未実施（ドキュメント更新のみ）**' + $stamp + ' (Asia/Taipei) — Rewrite OCR preprocessing EN**

### Summary
- Rewrote the English preprocessing plan with adaptive thresholding and OCR skip rules.

### Context / Goal
- Align the EN doc with the chosen strategy: no auto retry, adaptive thresholding, and pHash-based skips.

### Changes
- Updated OCR preprocessing steps, guardrails, and summary recommendations.

### Files Touched
- `Doc/OCR-Image-PreprocessingEN.md` — rewritten per updated strategy.

### Behavioral Impact
- No runtime behavior changes (documentation only).

### Risk & Mitigation
- Risk: None (documentation-only change).
- Mitigation: N/A.

### Tests / Verification
- 未実施（ドキュメント更新のみ）

**2026-01-23 14:45 (Asia/Taipei) — Wire OCR preprocessing**

### Summary
- Run OCR on preprocessed ROI images to apply scaling and adaptive thresholding.

### Context / Goal
- Implement the OCR preprocessing flow described in Doc/OCR-Image-PreprocessingEN.md.
- Improve OCR stability for mixed backgrounds and small glyphs.

### Changes
- Added an OCR preprocessor dependency to the pipeline.
- Preprocess ROI bitmaps before calling OCR.

### Files Touched
- Services/PipelineOrchestrator.cs — preprocess ROI before OCR and inject preprocessor dependency.
- MainWindow.xaml.cs — instantiate and pass the OCR preprocessor into the pipeline.

### Behavioral Impact
- OCR now runs on binarized, scaled images; recognition results may differ from prior raw input.

### Risk & Mitigation
- Risk: Preprocessing could reduce accuracy for some scenes or providers.
- Mitigation: Adjust preprocessing defaults or bypass the preprocessor if regressions appear.

### Tests / Verification
- Not run (not requested).

**2026-01-23 14:57 (Asia/Taipei) — Save preprocessed OCR images**

### Summary
- Save each preprocessed OCR image to Ocr-image under the working directory.

### Context / Goal
- Inspect OCR preprocessing output for debugging and tuning.
- Persist the preprocessed bitmap on every OCR run.

### Changes
- Added image dump helper in the pipeline.
- Save preprocessed ROI bitmaps as PNG files with timestamps.

### Files Touched
- Services/PipelineOrchestrator.cs — dump preprocessed images to disk before OCR.

### Behavioral Impact
- Each OCR run writes a PNG file to Ocr-image in the working directory.

### Risk & Mitigation
- Risk: Increased disk usage and IO overhead on frequent OCR runs.
- Mitigation: Manually clear the folder or disable the dump when no longer needed.

### Tests / Verification
- Not run (not requested).

**2026-01-23 15:00 (Asia/Taipei) — Gate OCR image dump behind setting**

### Summary
- Added a debug toggle to save preprocessed OCR images only when enabled.

### Context / Goal
- Allow inspecting preprocessing output without always writing images to disk.
- Keep default behavior quiet unless the debug switch is enabled.

### Changes
- Added a UI setting to toggle preprocessed image dumping.
- Persisted the new setting and guarded the save call in the pipeline.

### Files Touched
- Models/AppSettings.cs — added EnablePreprocessedImageDump setting.
- MainWindow.xaml — added the debug checkbox in OCR advanced settings.
- MainWindow.xaml.cs — wired the checkbox to settings load/save.
- Services/PipelineOrchestrator.cs — save preprocessed images only when enabled.

### Behavioral Impact
- Preprocessed images are saved only when the debug checkbox is enabled.

### Risk & Mitigation
- Risk: Users might not notice the toggle and expect images to appear.
- Mitigation: The checkbox is placed in OCR advanced settings for clarity.

### Tests / Verification
- Not run (not requested).**2026-01-23 16:26 (Asia/Taipei) — Update PaddleOCR-VL vLLM plan**

### Summary
- Update the vLLM integration plan to match the current OCR pipeline and settings patterns.

### Context / Goal
- Align the plan with the existing Capture/OCR/Overlay flow.
- Clarify settings, provider behavior, and fallback expectations.

### Changes
- Expanded prerequisites to reflect the actual pipeline flow and overlay handling.
- Adjusted settings/UI/provider details, including DPAPI handling for vLLM API keys.
- Clarified coordinate expectations, base64 image transfer, and fallback/testing points.

### Files Touched
- Doc/PaddleOCR-VL-Plan.md — refreshed plan content to reflect current architecture and implementation steps.

### Behavioral Impact
- Documentation-only change; no runtime behavior changes.

### Risk & Mitigation
- Risk: Plan expectations could still diverge if code changes later.
- Mitigation: Re-validate against implementation when starting the vLLM work.

### Tests / Verification
- 未実施（ドキュメント更新のみ）
**2026-01-23 16:46 (Asia/Taipei) — Implement PaddleOCR-VL vLLM OCR**

### Summary
- Add PaddleOCR-VL (vLLM) OCR engine support with settings, UI, and provider integration.

### Context / Goal
- Implement vLLM-backed OCR per PaddleOCR-VL usage guidance.
- Keep existing capture/OCR/overlay flow and WinRT fallback behavior.

### Changes
- Added vLLM settings (base URL, model name, API key) with DPAPI protection.
- Introduced a vLLM OCR provider and wired it into OcrEngine and the UI.
- Added UI controls and category panel for vLLM configuration.

### Files Touched
- Models/OcrEngineKind.cs — added PaddleVllm engine kind.
- Models/AppSettings.cs — added vLLM configuration fields and protected API key storage.
- Services/SettingsService.cs — load/save DPAPI protection for vLLM API key.
- Services/PaddleVllmOcrProvider.cs — new OpenAI-compatible vLLM OCR provider.
- Services/OcrEngine.cs — integrated vLLM provider with WinRT fallback.
- MainWindow.xaml — added vLLM engine option and settings panel.
- MainWindow.xaml.cs — wired vLLM UI fields and engine selection.

### Behavioral Impact
- New OCR engine option uses vLLM when selected; failures fall back to WinRT.

### Risk & Mitigation
- Risk: vLLM responses may include non-JSON content or schema mismatches.
- Mitigation: JSON extraction guard and fallback to WinRT on errors.

### Tests / Verification
- 未実施（UI/通信の手動確認は未実行）
**2026-01-23 16:53 (Asia/Taipei) — Draft PaddleOCR-VL on-demand plan**

### Summary
- Document an on-demand installation strategy for PaddleOCR-VL using uv.

### Context / Goal
- Provide a Japanese guide for on-demand vLLM setup and runtime flow.
- Keep alignment with the existing vLLM-based OCR integration approach.

### Changes
- Added a new doc outlining triggers, install steps, startup, and fallback behavior.

### Files Touched
- Doc/PaddleOCR-VL-Ondemand.md — new on-demand installation and operation plan.

### Behavioral Impact
- Documentation-only change; no runtime behavior changes.

### Risk & Mitigation
- Risk: The plan may diverge from future implementation details.
- Mitigation: Re-validate the flow during implementation.

### Tests / Verification
- 未実施（ドキュメント追加のみ）
**2026-01-23 17:52 (Asia/Taipei) — Draft Florence-2 OCR plan**

### Summary
- Documented an implementation plan to add Microsoft Florence-2 OCR (Large/Base).

### Context / Goal
- Provide a Japanese implementation outline aligned with the current OCR pipeline.
- Cover settings, UI, provider behavior, and fallback expectations.

### Changes
- Added a new plan document describing Florence-2 integration steps.

### Files Touched
- Doc/Microsoft-Florence-2Plan.md — new plan for Florence-2 OCR integration.

### Behavioral Impact
- Documentation-only change; no runtime behavior changes.

### Risk & Mitigation
- Risk: Plan may diverge from model/API specifics.
- Mitigation: Re-validate with target Florence-2 runtime before implementation.

### Tests / Verification
- 未実施（ドキュメント追加のみ）
**2026-01-23 17:56 (Asia/Taipei) — Draft Florence-2 on-demand plan**

### Summary
- Added an on-demand installation plan for Florence-2 using Python/uv.

### Context / Goal
- Provide a Japanese on-demand setup flow aligned with OCR integration plans.
- Focus on venv isolation, dependency install, and fallback behavior.

### Changes
- Added a new doc covering triggers, install steps, and operational guidance.

### Files Touched
- Doc/Microsoft-Florence-2-ondemand.md — new on-demand installation plan for Florence-2.

### Behavioral Impact
- Documentation-only change; no runtime behavior changes.

### Risk & Mitigation
- Risk: Dependency/torch variants differ by environment.
- Mitigation: Documented GPU/CPU variance and suggested explicit install URLs.

### Tests / Verification
- 未実施（ドキュメント追加のみ）
**2026-01-23 18:25 (Asia/Taipei) — Revise Florence-2 plan for local inference**

### Summary
- Rewrote the Florence-2 OCR plan to assume local Python/uv inference.

### Context / Goal
- Align the plan with a local inference workflow instead of OpenAI-compatible APIs.
- Clarify settings, provider behavior, and data flow for the Python bridge.

### Changes
- Replaced API-based assumptions with Python bridge and temp-file workflow.
- Updated settings/UI guidance to local inference fields.
- Adjusted risks and test points for local environment variance.

### Files Touched
- Doc/Microsoft-Florence-2Plan.md — switched plan to local inference assumptions.

### Behavioral Impact
- Documentation-only change; no runtime behavior changes.

### Risk & Mitigation
- Risk: Plan may diverge from future bridge implementation.
- Mitigation: Re-validate during implementation and update accordingly.

### Tests / Verification
- 未実施（ドキュメント更新のみ）
**2026-01-26 13:49 (Asia/Taipei) — Add auto-hide timing logs**

### Summary
- Added perf logs around scene change evaluation and auto-hide watcher timing.

### Context / Goal
- Diagnose the extra latency when auto-hide is enabled.
- Surface which path adds time per OCR run.

### Changes
- Logged SceneChangeEvaluator execution time when perf logging is enabled.
- Logged auto-hide baseline reset and watcher tick durations.

### Files Touched
- Services/PipelineOrchestrator.cs — timed scene change evaluation and logged when above threshold.
- MainWindow.xaml.cs — timed auto-hide baseline reset and watcher tick work.

### Behavioral Impact
- No functional change; additional perf logs appear only when perf logging is enabled and threshold is met.

### Risk & Mitigation
- Risk: Increased log volume during perf logging.
- Mitigation: Existing perf threshold still gates output.

### Tests / Verification
- 未実施（ログ追加のみ）
**2026-01-26 16:09 (Asia/Taipei) — Switch auto-hide to watcher-only**

### Summary
- Removed scene-change evaluation from the OCR path and rely on the watcher for auto-hide.

### Context / Goal
- Eliminate the large OCR latency caused by SceneChangeEvaluator.
- Keep auto-hide behavior via the existing watcher timer.

### Changes
- Deleted SceneChangeEvaluator usage and OverlayAutoHidden event from the pipeline.
- Removed the scene-change auto-hide handler hookup in MainWindow.

### Files Touched
- Services/PipelineOrchestrator.cs — removed scene-change evaluation and event wiring.
- MainWindow.xaml.cs — removed OverlayAutoHidden subscription and handler.

### Behavioral Impact
- Auto-hide now triggers only via the watcher timer; OCR runs no longer include scene-change evaluation.

### Risk & Mitigation
- Risk: Auto-hide response may be delayed by the watcher interval.
- Mitigation: Adjust SceneChangeWatchIntervalMs to a smaller value if needed.

### Tests / Verification
- 未実施（ロジック削除のみ）
**2026-01-26 16:15 (Asia/Taipei) — Align default settings with current JSON**

### Summary
- Updated AppSettings defaults to match the current settings.json values.

### Context / Goal
- Use the current Json.Settings as the startup defaults when no settings file exists.
- Preserve existing behavior for persisted settings.json users.

### Changes
- Updated AppSettings default values for ROI, capture provider, OCR perf/log flags, scene-change settings, merge weights, languages, and overlay appearance.
- Kept API key fields unset to avoid embedding secrets in defaults.

### Files Touched
- Models/AppSettings.cs — synced default property initializers to current settings.json values (excluding secrets).

### Behavioral Impact
- Fresh installs (or missing settings.json) now start with the current settings.json defaults.

### Risk & Mitigation
- Risk: Defaults include a specific ROI that may not match other displays.
- Mitigation: ROI is disabled by default; users can enable and adjust as needed.

### Tests / Verification
- 未実施（デフォルト値更新のみ）
**2026-01-27 17:45 (Asia/Taipei) — Add DXGI capture smoke test**

### Summary
- Added a console smoke test that captures DXGI output and saves a PNG for debugging black frames.

### Context / Goal
- DxgiDuplicationProvider output appears black; need a minimal tool to inspect capture output.
- Save a captured frame to disk for visual verification.

### Changes
- Added a new Tools console project to invoke DxgiDuplicationProvider directly.
- Implemented CLI parsing and file output for captured frames.

### Files Touched
- Tools/DxgiCaptureSmokeTest/DxgiCaptureSmokeTest.csproj — new console project referencing the main app.
- Tools/DxgiCaptureSmokeTest/Program.cs — capture invocation and PNG output.

### Behavioral Impact
- No impact on the main application; adds an optional debugging tool.

### Risk & Mitigation
- Risk: None for production; tool is manual and isolated.
- Mitigation: N/A.

### Tests / Verification
- 未実施（ローカル実行前のツール追加のみ）
**2026-01-28 10:19 (Asia/Taipei) — DXGI resident session for hotkey capture**

### Summary
- Kept DXGI device/duplication resident when preferred and added warm-up handling for black frames.

### Context / Goal
- Reduce DXGI re-initialization cost and avoid initial black frames during hotkey capture.
- Keep DXGI resident only when it is the selected capture provider.

### Changes
- Added resident DXGI session lifecycle with staging reuse, warm-up skipping, and access-lost recovery.
- CaptureManager now toggles DXGI resident mode based on settings.

### Files Touched
- Services/DxgiDuplicationProvider.cs — resident session, warm-up, and error recovery logic.
- Services/CaptureManager.cs — set DXGI resident enablement from settings.

### Behavioral Impact
- When DXGI is preferred, initialization is reused across hotkey captures; initial warm-up frames may be skipped.
- When DXGI is not preferred, any resident session is released and DXGI falls back to transient capture.

### Risk & Mitigation
- Risk: Session reuse could fail on access-lost or monitor changes.
- Mitigation: Reset and recreate session on recoverable DXGI errors; fallback to other providers via CaptureManager.

### Tests / Verification
- 未実施（手動実行前の実装のみ）
**2026-01-28 10:49 (Asia/Taipei) — Extend DXGI smoke test for continuous capture**

### Summary
- Updated the DXGI smoke test tool to support timed, repeated capture output with interval control.

### Context / Goal
- Align the test tool with resident DXGI capture changes and enable continuous output for diagnosis.
- Allow users to specify duration, interval, and output directory.

### Changes
- Added duration/interval/output directory arguments plus single-frame mode detection.
- Enabled DXGI resident session during the test loop and released it on exit.

### Files Touched
- Tools/DxgiCaptureSmokeTest/Program.cs — added looping capture, argument parsing, and output sequence naming.

### Behavioral Impact
- The smoke test can now output a series of PNGs over a timed run; single-file capture remains supported via .png argument.

### Risk & Mitigation
- Risk: More captures can increase GPU/IO load during long runs.
- Mitigation: Defaults are short; interval and duration are clamped to safe ranges.

### Tests / Verification
- 未実施（ツール更新のみ）
**2026-01-28 11:19 (Asia/Taipei) — Exclude overlay from capture**

### Summary
- Applied capture exclusion to the overlay window to reduce DXGI/WGC overlay artifacts.

### Context / Goal
- DXGI capture was including the overlay window, affecting OCR and scene-change detection.
- Prefer excluding the overlay window at the OS level without flicker.

### Changes
- Set window display affinity to exclude the overlay from capture during source initialization.

### Files Touched
- UI/OverlayWindow.xaml.cs — added SetWindowDisplayAffinity call and constant.

### Behavioral Impact
- On supported Windows versions, the overlay should no longer appear in captured frames.

### Risk & Mitigation
- Risk: Some OS/builds may ignore the affinity setting.
- Mitigation: Behavior remains unchanged on unsupported systems; no crash path added.

### Tests / Verification
- 未実施（手動確認前の実装のみ）
**2026-01-28 16:48 (Asia/Taipei) — Draft Paddle OCR gRPC resident plan**

### Summary
- Added a design document for Paddle OCR gRPC hosting with restart, monitoring, and fallback.

### Context / Goal
- Plan a resident Python gRPC OCR service with robust lifecycle management.
- Ensure WPF can fall back to WinRT OCR on failure.

### Changes
- Documented architecture, steps, and risks for gRPC server + client integration.

### Files Touched
- Doc/PaddleOcr_Grpc_Resident_Plan.md — new design plan covering restart/monitoring/fallback.

### Behavioral Impact
- None (documentation only).

### Risk & Mitigation
- Risk: None (no runtime changes).
- Mitigation: N/A.

### Tests / Verification
- 未実施（ドキュメント追加のみ）
**2026-01-29 13:08 (Asia/Taipei) — Add Paddle gRPC OCR host and client**

### Summary
- Implemented resident Paddle OCR gRPC hosting and client integration with restart/health checks and WinRT fallback.

### Context / Goal
- Replace per-call Paddle OCR process with a resident gRPC server for lower latency.
- Add monitoring/restart logic and keep fallback to WinRT OCR on failure.

### Changes
- Added gRPC proto, client provider, and host lifecycle management for Paddle OCR.
- Wired gRPC host startup/shutdown and new settings for endpoint/process control.
- Added Python gRPC server scaffolding for Paddle OCR.

### Files Touched
- Services/PaddleGrpcHost.cs — new process host with health checks and restart logic.
- Services/PaddleGrpcOcrProvider.cs — new gRPC OCR provider for Paddle engine.
- Services/OcrEngine.cs — switched Paddle engine to gRPC provider.
- MainWindow.xaml.cs — start/stop gRPC host during app lifecycle.
- Models/AppSettings.cs — added Paddle gRPC configuration defaults.
- Hotkey-Translator.csproj — added gRPC/protobuf package references and proto build item.
- Protos/OcrGrpc.proto — gRPC contract for OCR + health.
- OcrService/ocr.proto — Python-side proto copy.
- OcrService/ocr_engine.py — PaddleOCR initialization and JSON mapping.
- OcrService/server.py — gRPC server entrypoint with health/recognize endpoints.
- OcrService/requirements.txt — Python dependencies for gRPC OCR.

### Behavioral Impact
- When Paddle OCR is selected, OCR requests go through the local gRPC service; failures fall back to WinRT.
- App startup now attempts to start the Paddle gRPC server if enabled.

### Risk & Mitigation
- Risk: gRPC server may fail to start or become ready in time.
- Mitigation: Startup timeout handling and automatic restart attempts; fallback to WinRT on OCR failure.

### Tests / Verification
- 未実施（実装のみ）
**2026-01-29 16:01 (Asia/Taipei) — Plan fixed Paddle gRPC server contract**

### Summary
- Added a fixed-server gRPC plan that forces GPU on host start and removes per-request device/lang/model.

### Context / Goal
- Align WPF and Python OCR flows with GPU-only PaddleOCR 3.x and a simplified request contract.
- Avoid runtime parameter mismatch between client and server.

### Changes
- Documented GPU override at host startup and request field removal.

### Files Touched
- Doc/PaddleOcr_Grpc_FixedServer_Plan.md — new implementation plan.

### Behavioral Impact
- None (documentation only).

### Risk & Mitigation
- Risk: None (no runtime changes).
- Mitigation: N/A.

### Tests / Verification
- 未実施（ドキュメント追加のみ）
**2026-01-30 14:29 (Asia/Taipei) — Update Paddle gRPC fixed server plan**

### Summary
- Updated the Paddle gRPC plan to include language + detection model selection from UI.

### Context / Goal
- Align gRPC request contract with UI-driven language and detection model selection.

### Changes
- Revised fixed-server plan to send language and 	ext_detection_model_name only.

### Files Touched
- Doc/PaddleOcr_Grpc_FixedServer_Plan.md — updated request contract and steps.

### Behavioral Impact
- None (documentation only).

### Risk & Mitigation
- Risk: None (no runtime changes).
- Mitigation: N/A.

### Tests / Verification
- 未実施（ドキュメント更新のみ）
**2026-01-30 14:30 (Asia/Taipei) — Add ROI pHash/translation gating plan**

### Summary
- Added a plan for multi-ROI pHash monitoring with OCR/translation gating and cache reuse.

### Context / Goal
- Reduce OCR/translation load by only processing changed ROIs and skipping low-value text.

### Changes
- Documented ROI-based pHash monitoring, OCR gating thresholds, and cache reuse flow.

### Files Touched
- Doc/Roi_Phash_Ocr_Translation_Plan.md — new implementation plan.

### Behavioral Impact
- None (documentation only).

### Risk & Mitigation
- Risk: None (no runtime changes).
- Mitigation: N/A.

### Tests / Verification
- 未実施（ドキュメント追加のみ）
**2026-01-30 14:38 (Asia/Taipei) — Add ROI UI plan**

### Summary
- Expanded the ROI pHash/OCR plan with user UI design details.

### Context / Goal
- Provide a UI concept for managing multiple ROIs and translation gating settings.

### Changes
- Added ROI list/editor, gating controls, and UX notes to the plan.

### Files Touched
- Doc/Roi_Phash_Ocr_Translation_Plan.md — appended user UI section.

### Behavioral Impact
- None (documentation only).

### Risk & Mitigation
- Risk: None (no runtime changes).
- Mitigation: N/A.

### Tests / Verification
- 未実施（ドキュメント更新のみ）
**2026-01-30 14:41 (Asia/Taipei) — Implement Paddle gRPC fixed-server contract**

### Summary
- Implemented language + detection model request contract with GPU enforcement for Paddle gRPC OCR.

### Context / Goal
- Align WPF and Python with fixed server settings while allowing UI-driven language and detection model selection.
- Remove per-request device/model_dir and enforce GPU for PaddleOCR v5.

### Changes
- Updated gRPC protos to send only language and text_detection_model_name.
- Added Paddle text detection model setting and passed it to the gRPC server.
- Implemented engine pool on the Python side for language/model selection and updated OCR engine init signature.
- Adjusted Paddle gRPC client request payload to match the new contract.

### Files Touched
- Protos/OcrGrpc.proto — removed device/model_dir fields, added text_detection_model_name.
- OcrService/ocr.proto — same contract update for Python.
- Models/AppSettings.cs — added PaddleTextDetectionModelName.
- Services/PaddleGrpcHost.cs — enforce GPU and pass det-model.
- Services/PaddleGrpcOcrProvider.cs — send language + text_detection_model_name only.
- OcrService/ocr_engine.py — accept text_detection_model_name parameter.
- OcrService/server.py — engine pool keyed by language/detection model and request handling.

### Behavioral Impact
- Paddle OCR requests now vary language and detection model only; device/model_dir are fixed server-side.
- Host startup forces GPU when CPU is configured.

### Risk & Mitigation
- Risk: Protocol change requires WPF/Python to be updated together.
- Mitigation: Both client and server protos updated in this change set.

### Tests / Verification
- 未実施（実装のみ）
**2026-01-30 14:54 (Asia/Taipei) — Add OCR engine UI simplification plan**

### Summary
- Added plan to keep only WinRT and PaddleOCRv5 in OCR engine UI.

### Context / Goal
- Simplify engine selection and reduce maintenance overhead.

### Changes
- Documented steps to trim OcrEngineKind, UI options, and branching logic.

### Files Touched
- Doc/OcrEngine_UI_Simplify_Plan.md — new implementation plan.

### Behavioral Impact
- None (documentation only).

### Risk & Mitigation
- Risk: None (no runtime changes).
- Mitigation: N/A.

### Tests / Verification
- 未実施（ドキュメント追加のみ）
**2026-01-30 14:59 (Asia/Taipei) — Simplify OCR engine UI to WinRT/Paddle**

### Summary
- Removed Florence and PaddleVLLM OCR options from UI and runtime selection logic.

### Context / Goal
- Keep only WinRT and PaddleOCRv5 as selectable engines per the UI simplification plan.
- Reduce settings surface and avoid unused engine branches.

### Changes
- Trimmed OCR engine ComboBox to two options and removed Florence/VLLM settings panels.
- Updated settings mapping and engine selection logic to ignore removed engines.
- Removed Florence/VLLM branches from OcrEngine.

### Files Touched
- MainWindow.xaml — removed Florence/VLLM items and panels.
- MainWindow.xaml.cs — simplified engine selection and settings panel routing.
- Services/OcrEngine.cs — removed Florence/VLLM provider branches.

### Behavioral Impact
- OCR engine selection now only allows WinRT or PaddleOCRv5; legacy selections fall back to WinRT.

### Risk & Mitigation
- Risk: Users with Florence/VLLM in settings lose access to those engines.
- Mitigation: Default mapping falls back to WinRT without crashing.

### Tests / Verification
- 未実施（UI整理のみ）
**2026-01-30 15:00 (Asia/Taipei) — Remove Florence/VLLM settings bindings**

### Summary
- Removed Florence/VLLM UI bindings from settings save path after UI cleanup.

### Context / Goal
- Fix build errors caused by deleted UI controls while keeping OCR engine options trimmed.
- Ensure settings save path only references existing UI elements.

### Changes
- Deleted Florence/VLLM settings assignments in SaveSettingsAsync.

### Files Touched
- MainWindow.xaml.cs — removed Florence/VLLM control references in settings save.

### Behavioral Impact
- Florence/VLLM settings are no longer persisted via UI (controls removed already).

### Risk & Mitigation
- Risk: Users with old settings may lose those values when saving.
- Mitigation: Values are no longer used and engine options are removed.

### Tests / Verification
- 未実施（ビルド修正のみ）
**2026-01-30 15:11 (Asia/Taipei) — Simplify Paddle OCR settings**

### Summary
- Simplified Paddle OCR settings to only select detection model and bind language to Main settings.

### Context / Goal
- Remove deprecated Paddle UI fields and keep only Mobile/Server detection model selection.
- Ensure Paddle gRPC uses Main language settings consistently.

### Changes
- Mapped Paddle detection model selection to `PaddleTextDetectionModelName` in settings UI.
- Removed Paddle UI field bindings for project/device/lang/model values.
- Updated Paddle gRPC language resolution to always follow Main language settings.

### Files Touched
- `MainWindow.xaml.cs` — bind Paddle detection model ComboBox and drop removed UI field assignments.
- `Services/PaddleGrpcHost.cs` — resolve Paddle language from Main settings only.
- `Services/PaddleGrpcOcrProvider.cs` — resolve Paddle language from Main settings only.

### Behavioral Impact
- Paddle OCR language now always follows Main language settings; Paddle-specific language overrides are ignored.
- UI only persists the detection model choice (mobile/server).

### Risk & Mitigation
- Risk: Users relying on legacy PaddleLanguage values may see language change to Main settings.
- Mitigation: Main language already drives OCR flow; fallback remains `en` for non-Japanese.

### Tests / Verification
- 未実施（UIとgRPCの手動確認が必要）
**2026-01-30 15:25 (Asia/Taipei) — Add Paddle OCR cache limits and busy overlay**

### Summary
- Added LRU/TTL limits to PaddleOCR engine cache and a UI busy overlay to prevent OCR spam during initialization.

### Context / Goal
- Keep PaddleOCR server memory bounded when switching language/model.
- Improve UX by blocking repeated OCR triggers while initialization is in progress.

### Changes
- Implemented EnginePool LRU (max 2) and TTL eviction with logging in gRPC server.
- Added busy overlay UI and run guard to prevent concurrent OCR runs.

### Files Touched
- `OcrService/server.py` — added LRU/TTL cache eviction and logging.
- `MainWindow.xaml` — added busy overlay panel.
- `MainWindow.xaml.cs` — added run guard and overlay toggle during OCR.

### Behavioral Impact
- PaddleOCR engine cache is limited to 2 entries with long TTL; old entries are evicted.
- OCR hotkeys/buttons are ignored while a run is already in progress, with a visible loading overlay.

### Risk & Mitigation
- Risk: Evicted engines may reinitialize, causing a temporary delay.
- Mitigation: TTL is long and cache size is small but sufficient for typical use.

### Tests / Verification
- 未実施（gRPCサーバの起動とOCR実行の手動確認が必要）
**2026-01-30 16:01 (Asia/Taipei) — Disable overlay expansion for Paddle OCR**

### Summary
- Disabled overlay box expansion when using Paddle OCR to avoid oversized boxes.

### Context / Goal
- Paddle OCR produces larger detection boxes than WinRT.
- Keep WinRT expansion behavior while rendering Paddle OCR boxes as-is.

### Changes
- Added an engine-aware flag to skip overlay expansion for Paddle OCR.

### Files Touched
- `UI/OverlayWindow.xaml.cs` — gate overlay expansion by OCR engine.

### Behavioral Impact
- Paddle OCR overlays now use raw detection boxes; WinRT remains expanded.

### Risk & Mitigation
- Risk: Some Paddle boxes might feel tight for small text.
- Mitigation: Expansion can be reintroduced later with a Paddle-specific ratio if needed.

### Tests / Verification
- 未実施（Paddle/WinRT切替の描画確認が必要）
**2026-01-30 16:51 (Asia/Taipei) — Parameterize Paddle OCR test script**

### Summary
- Added CLI args to choose language and detection model when testing Paddle OCR engine.

### Context / Goal
- Allow quick validation of `ocr_engine.py` with different language/model combinations.
- Keep GPU-only behavior while making test usage flexible.

### Changes
- Replaced manual argv handling with argparse and added `--lang`/`--det-model`/`--device` options.
- Passed selected arguments into `PaddleOcrEngine` initialization.

### Files Touched
- `OcrService/test_ocr_gpu_one.py` — added argparse and parameterized engine init.

### Behavioral Impact
- Test script now accepts optional flags for language/model/device; defaults unchanged.

### Risk & Mitigation
- Risk: None; test script only.
- Mitigation: Defaults preserve prior behavior.

### Tests / Verification
- 未実施（ローカル環境での実行が必要）
**2026-01-30 18:23 (Asia/Taipei) — Add overlay short-line shrink toggle**

### Summary
- Added a UI setting to enable/disable overlay shrink for 1–2 line OCR results.

### Context / Goal
- Allow users to turn off conservative shrink behavior for short line counts.
- Keep default behavior unchanged unless the new toggle is disabled.

### Changes
- Added `EnableOverlayShortLineShrink` to settings and wired it to the UI.
- Applied the toggle in overlay rendering to skip short-line shrink when disabled.

### Files Touched
- `Models/AppSettings.cs` — added `EnableOverlayShortLineShrink` setting.
- `MainWindow.xaml` — added checkbox in Overlay Layout section.
- `MainWindow.xaml.cs` — save/apply the new toggle.
- `UI/OverlayWindow.xaml.cs` — gate short-line shrink by setting.

### Behavioral Impact
- Users can now disable the 1–2 line shrink behavior globally.

### Risk & Mitigation
- Risk: Disabling shrink may cause text overflow in small boxes.
- Mitigation: Default remains enabled; users can re-enable if needed.

### Tests / Verification
- `dotnet build`
**2026-01-30 19:10 (Asia/Taipei) — Simplify F8 hotkey behavior**

### Summary
- Removed overlay hide toggle from F8 so it always triggers a run.

### Context / Goal
- Avoid confusing dual behavior on the F8 hotkey.
- Keep overlay toggling only on F9.

### Changes
- Simplified F8 handler to always run OCR and updated the startup hint text.

### Files Touched
- `MainWindow.xaml.cs` — removed F8 overlay-hide branch and adjusted log message.

### Behavioral Impact
- Pressing F8 no longer hides the overlay; it always runs OCR once.

### Risk & Mitigation
- Risk: Users relying on F8 to hide overlay lose that shortcut.
- Mitigation: F9 still toggles overlay, and logs were updated.

### Tests / Verification
- 未実施（ホットキー動作の手動確認が必要）
**2026-01-30 19:28 (Asia/Taipei) — Remove dimming background for OCR busy overlay**

### Summary
- Removed the semi-transparent dark overlay behind the OCR loading indicator.

### Context / Goal
- Keep OCR loading UI lightweight without obscuring the underlying content.

### Changes
- Set busy overlay background to transparent so only the centered indicator remains.

### Files Touched
- `MainWindow.xaml` — made BusyOverlay background transparent.

### Behavioral Impact
- OCR loading now shows only the centered indicator; no full-screen dimming.

### Risk & Mitigation
- Risk: Indicator could be less noticeable on bright backgrounds.
- Mitigation: The indicator box remains visible with a light background.

### Tests / Verification
- 未実施（UIの見た目確認が必要）
**2026-01-30 19:36 (Asia/Taipei) — Add translation loading state to shared overlay**

### Summary
- Reused the central busy overlay to show a "Translating..." state during translation.

### Context / Goal
- Provide feedback when translation takes longer than OCR.
- Reuse the existing loading UI instead of adding a new widget.

### Changes
- Added translation start/end events in the pipeline.
- Updated the busy overlay message while translation is running.

### Files Touched
- `Services/PipelineOrchestrator.cs` — emit translation start/end events.
- `MainWindow.xaml.cs` — subscribe to events and update busy overlay text.

### Behavioral Impact
- The busy overlay now switches to "Translating..." while translation is in progress.

### Risk & Mitigation
- Risk: Message may briefly flip if translation is fast.
- Mitigation: Only shows when there are pending translation items.

### Tests / Verification
- `dotnet build`
**2026-01-30 19:39 (Asia/Taipei) — Delay translation loading overlay**

### Summary
- Added a short delay so the translating indicator only appears for non-instant translations.

### Context / Goal
- Avoid flicker when translation completes quickly.
- Reuse the existing busy overlay message without extra UI.

### Changes
- Added a delayed translation overlay timer with cancellation on completion.
- Canceled pending translation overlay when OCR run finishes or app closes.

### Files Touched
- `MainWindow.xaml.cs` — delayed "Translating..." display and cancellation logic.

### Behavioral Impact
- The translating overlay appears only if translation takes longer than ~200ms.

### Risk & Mitigation
- Risk: If translation completes just after delay, brief message swap may occur.
- Mitigation: Delay is short and overlay is already visible during OCR.

### Tests / Verification
- `dotnet build`
**2026-02-02 15:02 (Asia/Taipei) — Add Paddle OCR recognition model setting**

### Summary
- Added a Paddle OCR recognition model selection in settings and wired it to the gRPC server.

### Context / Goal
- Let users choose `text_recognition_model_name` via Paddle OCR settings.
- Keep changes scoped to Paddle OCR without affecting WinRT/translation.

### Changes
- Added `PaddleTextRecognitionModelName` to settings and UI binding.
- Passed recognition model to Paddle gRPC host and server.
- Used the selected recognition model when initializing PaddleOCR.

### Files Touched
- `Models/AppSettings.cs` — added Paddle recognition model setting.
- `MainWindow.xaml` — added recognition model dropdown in Paddle OCR settings.
- `MainWindow.xaml.cs` — save/apply recognition model selection.
- `Services/PaddleGrpcHost.cs` — pass `--rec-model` to gRPC server.
- `OcrService/server.py` — accept `--rec-model` and include it in engine cache.
- `OcrService/ocr_engine.py` — use provided recognition model name.

### Behavioral Impact
- Paddle OCR uses the selected recognition model; defaults remain server_rec.

### Risk & Mitigation
- Risk: Invalid model names could break PaddleOCR init.
- Mitigation: UI uses a fixed dropdown list of valid model names.

### Tests / Verification
- `dotnet build`
**2026-02-02 15:12 (Asia/Taipei) — Send Paddle OCR recognition model per request**

### Summary
- Added `text_recognition_model_name` to gRPC requests so recognition model changes apply without server restart.

### Context / Goal
- Match the per-request behavior of `text_detection_model_name`.
- Avoid restarting the Paddle gRPC server when changing recognition model in UI.

### Changes
- Extended OcrRequest in protos with `text_recognition_model_name`.
- Sent recognition model from the Paddle gRPC client and consumed it on the server.
- Updated server proto regeneration to refresh when `ocr.proto` changes.

### Files Touched
- `Protos/OcrGrpc.proto` — added `text_recognition_model_name` field.
- `OcrService/ocr.proto` — added `text_recognition_model_name` field.
- `Services/PaddleGrpcOcrProvider.cs` — send recognition model in requests.
- `OcrService/server.py` — use request recognition model and auto-regenerate proto on change.

### Behavioral Impact
- Recognition model selection now applies immediately per OCR request; no gRPC restart required.

### Risk & Mitigation
- Risk: Older generated Python stubs may miss the new field.
- Mitigation: Server now regenerates stubs when `ocr.proto` changes.

### Tests / Verification
- `dotnet build`
**2026-02-02 15:20 (Asia/Taipei) — Add auto recognition model selection**

### Summary
- Added an Auto option for Paddle OCR recognition model that maps from Source language.

### Context / Goal
- Let recognition model follow Source language when set to Auto, otherwise keep the selected model.
- Keep per-request behavior without requiring gRPC restarts.

### Changes
- Added Auto entry to recognition model dropdown.
- Resolved Auto to en/eslav/server_rec based on Source language in gRPC client and host.

### Files Touched
- `MainWindow.xaml` — added Auto entry in recognition model dropdown.
- `Services/PaddleGrpcOcrProvider.cs` — resolve Auto to recognition model by Source language.
- `Services/PaddleGrpcHost.cs` — resolve Auto for server startup defaults.

### Behavioral Impact
- Selecting Auto maps Source=en to en_PP-OCRv5_mobile_rec, Source=ru to eslav_PP-OCRv5_mobile_rec; others use PP-OCRv5_server_rec.

### Risk & Mitigation
- Risk: Users may expect different mappings for other languages.
- Mitigation: Auto is optional; manual selection remains available.

### Tests / Verification
- `dotnet build`
**2026-02-02 15:28 (Asia/Taipei) — Add rec model flag to Paddle OCR test**

### Summary
- Extended the Paddle OCR test script to accept a recognition model argument.

### Context / Goal
- Keep test coverage aligned with new recognition model selection.

### Changes
- Added `--rec-model` CLI flag and passed it into `PaddleOcrEngine`.

### Files Touched
- `OcrService/test_ocr_gpu_one.py` — added recognition model argument.

### Behavioral Impact
- Test script can now validate specific recognition models.

### Risk & Mitigation
- Risk: None; test script only.
- Mitigation: Defaults preserve previous behavior.

### Tests / Verification
- 未実施（ローカル実行が必要）
**2026-02-02 15:54 (Asia/Taipei) — Add padding + clamp for Paddle OCR preprocessing**

### Summary
- Added padding with background color estimation and safe coordinate clamp for PaddleOCR preprocessing.

### Context / Goal
- Reduce edge clipping for text near image borders.
- Ensure corrected boxes never go negative after padding compensation.

### Changes
- Added padding expansion using estimated border color before OCR.
- Subtracted padding from result boxes and clamped x/y to non-negative.
- Added helper to estimate padding color from image edges.

### Files Touched
- `OcrService/ocr_engine.py` — added padding, coordinate correction, and color estimation.

### Behavioral Impact
- OCR runs on padded images; reported boxes are shifted back and clamped to original coordinates.

### Risk & Mitigation
- Risk: Padding could slightly change detection near edges.
- Mitigation: Padding color is estimated from image borders to minimize artifacts.

### Tests / Verification
- 未実施（OCRの結果確認が必要）
**2026-02-03 12:11 (Asia/Taipei) — Fit-only overlay font sizing**

### Summary
- Simplified overlay font sizing to fit-only with quantization and hysteresis.

### Context / Goal
- Align overlay font sizing with fit-only plan and reduce jitter.
- Remove multi-stage scaling and ensure text always fits.

### Changes
- Replaced font sizing logic with fit-only binary search and fallback shrinking.
- Added quantized inputs and hysteresis cache for stable font sizes.

### Files Touched
- `UI/OverlayWindow.xaml.cs` — replaced overlay font sizing with fit-only sizing, cache, and helpers.

### Behavioral Impact
- Overlay text now uses the largest size that fits the box, shrinking further only when needed; small fluctuations are smoothed.

### Risk & Mitigation
- Risk: Font sizes may differ from prior scaling rules.
- Mitigation: Fit-only sizing prevents clipping; hysteresis reduces jitter.

### Tests / Verification
- `dotnet build`
**2026-02-03 13:35 (Asia/Taipei) — Fix overlay fit measurement**

### Summary
- Fixed font fit measurement to detect vertical overflow correctly.

### Context / Goal
- Overlay font sizing was not shrinking because height was always clamped in measurement.
- Ensure fit-only sizing can detect clipping and shrink accordingly.

### Changes
- Removed MaxTextHeight from FormattedText measurement to allow real height comparison.

### Files Touched
- `UI/OverlayWindow.xaml.cs` — adjusted Fit measurement to avoid height clamping.

### Behavioral Impact
- Overlay text now shrinks when the measured height exceeds the available box height.

### Risk & Mitigation
- Risk: Slightly different line breaking vs prior measurement.
- Mitigation: Still respects MaxTextWidth; only height clamp removed for accurate fit.

### Tests / Verification
- `dotnet build`
**2026-02-03 13:52 (Asia/Taipei) — Remove overlay box expansion**

### Summary
- Removed post-process overlay rectangle expansion so OCR boxes render as-is.

### Context / Goal
- Keep overlay bounds aligned with OCR results across engines.
- Avoid expansion-driven misalignment now that fit-only sizing is in place.

### Changes
- Removed expansion constants, logic, and method from overlay rendering.
- Render overlay directly using OCR rects without enlargement.

### Files Touched
- `UI/OverlayWindow.xaml.cs` — removed overlay rect expansion logic and usage.

### Behavioral Impact
- Overlay boxes match OCR-detected bounds; no extra padding/expansion is applied.

### Risk & Mitigation
- Risk: Small OCR boxes may look tighter than before.
- Mitigation: Fit-only sizing still shrinks text to fit within the box.

### Tests / Verification
- `dotnet build`
**2026-02-03 15:13 (Asia/Taipei) — Paddle confidence line filter**

### Summary
- Added Paddle-only confidence filtering to drop low-score OCR lines.

### Context / Goal
- Remove false-positive OCR lines without affecting WinRT output.

### Changes
- Added Paddle confidence filter settings (enable + threshold) and UI controls.
- Applied line filtering in the OCR pipeline before grouping/translation.

### Files Touched
- `Models/AppSettings.cs` — added confidence filter settings.
- `MainWindow.xaml` — added Paddle confidence filter UI.
- `MainWindow.xaml.cs` — wired UI settings and threshold value display.
- `Services/PipelineOrchestrator.cs` — filtered low-confidence Paddle lines.

### Behavioral Impact
- When enabled and using Paddle OCR, low-confidence lines are skipped before translation and overlay.

### Risk & Mitigation
- Risk: Threshold set too high may drop valid lines.
- Mitigation: Default is modest (0.60) and adjustable in UI.

### Tests / Verification
- `dotnet build`
**2026-02-03 16:51 (Asia/Taipei) — Add CTranslate2 gRPC translation service**

### Summary
- Added a separate CTranslate2/NLLB200 gRPC translation service and integrated it as a translation provider.

### Context / Goal
- Provide offline translation via CTranslate2 with CPU/GPU support and integrate with existing fallback chain.
- Keep translation host separate from OCR and allow UI configuration.

### Changes
- Added translation gRPC proto, host, and provider wired into translation fallback.
- Added TranslationService Python server with NLLB200 engine and uv dependencies.
- Added UI + settings for enabling CTranslate2 and configuring device/precision/model download.

### Files Touched
- `Protos/TranslationGrpc.proto` — new translation gRPC contract.
- `Hotkey-Translator.csproj` — added translation proto for client generation.
- `TranslationService/translation.proto` — server-side proto.
- `TranslationService/translator_engine.py` — NLLB200 engine wrapper.
- `TranslationService/server.py` — gRPC server entrypoint.
- `TranslationService/pyproject.toml` — uv dependencies for translation server.
- `Services/CTranslate2GrpcHost.cs` — host/monitor for translation server.
- `Services/CTranslate2GrpcTranslationProvider.cs` — gRPC translation provider.
- `Models/AppSettings.cs` — added CTranslate2 settings.
- `Models/TranslationProviderNames.cs` — added CTranslate2 provider name.
- `MainWindow.xaml` — added CTranslate2 UI controls.
- `MainWindow.xaml.cs` — wired settings, host startup, provider list, status text.

### Behavioral Impact
- When enabled, CTranslate2 translations are attempted (with fallback to other providers on failure).
- Translation server can auto-download the NLLB200 model and run on CPU/GPU.

### Risk & Mitigation
- Risk: Model download or gRPC startup latency can delay translation availability.
- Mitigation: Host startup failures are logged; fallback providers remain active.

### Tests / Verification
- `dotnet build`
**2026-02-03 17:18 (Asia/Taipei) — Simplify CTranslate2 UI and restart behavior**

### Summary
- Simplified CTranslate2 UI to enable + device only and added automatic restart logic.

### Context / Goal
- Hide advanced translation settings and enforce fixed precision rules.
- Restart the translation gRPC host when device or related settings change.

### Changes
- Removed CTranslate2 precision/model/autodownload controls from the UI.
- Enforced CPU=int8, GPU=fp16 and always-on auto-download in settings normalization.
- Added host config tracking and restart logic when settings change.
- Added startup logs for CTranslate2 host.

### Files Touched
- `MainWindow.xaml` — removed advanced CTranslate2 controls.
- `MainWindow.xaml.cs` — normalized CTranslate2 settings, restart logic, and updated bindings.
- `Services/CTranslate2GrpcHost.cs` — added startup logging for device/precision and auto-download.

### Behavioral Impact
- CTranslate2 runs with fixed precision per device and restarts automatically when device changes.
- Auto-download is always enabled and no longer exposed in the UI.

### Risk & Mitigation
- Risk: Users cannot tweak model/endpoint via UI.
- Mitigation: Settings remain in config for manual editing if needed.

### Tests / Verification
- `dotnet build`
**2026-02-04 11:17 (Asia/Taipei) — Translation tokenizer warning fixes**

### Summary
- Reduced Hugging Face warning noise and improved tokenizer loading robustness.

### Context / Goal
- Address Mistral regex, symlink caching, and unauthenticated download warnings.
- Keep translation downloads authenticated when a token is provided.

### Changes
- Added HF token resolution and pass-through to downloads/tokenizer loading.
- Suppressed symlink warning on Windows via environment default.
- Hardened tokenizer loading with regex fix application and warning filtering.

### Files Touched
- `TranslationService/translator_engine.py` — updated HF hub handling and tokenizer load logic.

### Behavioral Impact
- Tokenizer loads with a safer regex patch path and fewer warnings.
- Authenticated downloads are used automatically when `HF_TOKEN` is set.

### Risk & Mitigation
- Risk: Some warnings are suppressed, potentially hiding environment issues.
- Mitigation: Behavior is limited to known noisy warnings; failures still surface as errors.

### Tests / Verification
- 未実施（警告対応のみのため）
**2026-02-04 11:26 (Asia/Taipei) — Reduce HF access in translator engine**

### Summary
- Avoided unnecessary Hugging Face access when cached and removed lang_code_to_id fallback.

### Context / Goal
- Stop extra HF requests after first download.
- Remove redundant tokenizer fallback now that target_prefix is used.

### Changes
- Skip snapshot_download when the cached model directory exists.
- Always use the local tokenizer without lang_code_to_id checks.

### Files Touched
- `TranslationService/translator_engine.py` — cache-aware model path resolution and tokenizer load simplification.

### Behavioral Impact
- Cached models no longer trigger HF metadata checks.
- Tokenizer loading no longer falls back to base NLLB.

### Risk & Mitigation
- Risk: Corrupted local cache could go unnoticed.
- Mitigation: If translation fails, user can delete the cache to force re-download.

### Tests / Verification
- 未実施（ロジック変更のみのため）
**2026-02-04 11:32 (Asia/Taipei) — Use slow tokenizer for NLLB**

### Summary
- Switched NLLB tokenizer to the slow (sentencepiece) implementation to avoid regex warnings and quality regressions.

### Context / Goal
- Restore translation quality while eliminating the incorrect regex pattern warning.

### Changes
- Forced `use_fast=False` in NLLB tokenizer loading.
- Simplified tokenizer load path now that regex patching is unnecessary for slow tokenizers.

### Files Touched
- `TranslationService/translator_engine.py` — use slow tokenizer path for NLLB.

### Behavioral Impact
- Tokenizer loading is slightly slower but translation output is more stable.

### Risk & Mitigation
- Risk: Slightly slower initialization due to slow tokenizer.
- Mitigation: Initialization happens once per engine; runtime translation unaffected.

### Tests / Verification
- 未実施（ロジック変更のみのため）
**2026-02-04 11:43 (Asia/Taipei) — Robust tokenizer fallback and regex fix**

### Summary
- Reintroduced local-only NLLB tokenizer fallback and applied proper regex patching for fast tokenizers.

### Context / Goal
- Recover translation quality while keeping HF offline after initial download.

### Changes
- Attempted NLLB slow tokenizer load first; fall back to fast tokenizer with corrected regex patch.
- Restored lang_code_to_id check and local-only base tokenizer fallback.

### Files Touched
- `TranslationService/translator_engine.py` — tokenizer loading strategy and regex patching.

### Behavioral Impact
- Local tokenizer loading is more stable and avoids broken regex tokenization.
- If base tokenizer is not cached, no HF access is triggered.

### Risk & Mitigation
- Risk: If base tokenizer is missing locally, fallback stays on model tokenizer.
- Mitigation: User can pre-cache the base tokenizer or clear cache to re-download.

### Tests / Verification
- 未実施（ロジック変更のみのため）
**2026-02-04 11:53 (Asia/Taipei) — GPU precision auto-fallback**

### Summary
- Added GPU FP16/FP32 selection based on compute capability with runtime fallback.

### Context / Goal
- Avoid GPU translation failures when FP16 is unsupported.
- Prefer FP16 on tensor-core GPUs and FP32 otherwise.

### Changes
- Added compute capability probe via `nvidia-smi`.
- Selected FP16 for capable GPUs, FP32 otherwise.
- Added FP16->FP32 fallback on Translator initialization errors.

### Files Touched
- `TranslationService/translator_engine.py` — GPU precision selection and safe fallback.

### Behavioral Impact
- GPU runs on FP16 when supported; otherwise it automatically uses FP32.
- GPU initialization no longer fails due to unsupported FP16.

### Risk & Mitigation
- Risk: `nvidia-smi` missing or slow could delay initialization.
- Mitigation: Failure to query falls back to existing preference and runtime fallback remains.

### Tests / Verification
- 未実施（ロジック変更のみのため）
**2026-02-04 15:48 (Asia/Taipei) — Token-budgeted chunking for NLLB**

### Summary
- Added token-length based chunking in the translation engine to avoid 512-token overflow.

### Context / Goal
- Prevent translation degradation when inputs exceed the NLLB token limit.

### Changes
- Split inputs by delimiters and token budget before translation.
- Added fallback splitting for long segments and hard character splits when needed.

### Files Touched
- `TranslationService/translator_engine.py` — token-budgeted chunking logic.

### Behavioral Impact
- Long inputs are split into safe chunks and reassembled after translation.

### Risk & Mitigation
- Risk: Over-splitting can make translations slightly less natural.
- Mitigation: Prefer sentence/line delimiters and only hard split when required.

### Tests / Verification
- 未実施（ロジック変更のみのため）
**2026-02-04 16:01 (Asia/Taipei) — Expand delimiter set for chunking**

### Summary
- Added English and Chinese punctuation to the token-budgeted split delimiters.

### Context / Goal
- Improve chunking accuracy for English/Chinese inputs.

### Changes
- Expanded delimiter regex to include English and Chinese punctuation marks.

### Files Touched
- `TranslationService/translator_engine.py` — updated delimiter regex in chunking.

### Behavioral Impact
- Long inputs split more naturally on English/Chinese punctuation.

### Risk & Mitigation
- Risk: Additional punctuation may increase splitting frequency.
- Mitigation: Token budget guard still controls chunk size.

### Tests / Verification
- 未実施（ロジック変更のみのため）
**2026-02-04 16:03 (Asia/Taipei) — Conditional English period splitting**

### Summary
- Implemented conditional handling for English periods to reduce false splits.

### Context / Goal
- Avoid over-splitting on '.' while still honoring sentence boundaries in English.

### Changes
- Replaced regex-based delimiter split with a scanner.
- Added conditional split for '.' based on next token (uppercase/quote/end) and decimal checks.

### Files Touched
- `TranslationService/translator_engine.py` — delimiter splitting logic.

### Behavioral Impact
- English period splitting is more conservative and should reduce false positives.

### Risk & Mitigation
- Risk: Some abbreviations may still be split.
- Mitigation: Token-budget guard prevents overflow; heuristic favors fewer splits.

### Tests / Verification
- 未実施（ロジック変更のみのため）
**2026-02-04 16:15 (Asia/Taipei) — Split on punctuation followed by space**

### Summary
- Adjusted sentence splitting to cut after punctuation when followed by whitespace.

### Context / Goal
- Improve English sentence splitting by using a simpler whitespace-based rule.

### Changes
- Split on punctuation (including '.') only when the next character is whitespace.

### Files Touched
- `TranslationService/translator_engine.py` — delimiter handling update.

### Behavioral Impact
- English sentences are split more aggressively when punctuation is followed by spaces.

### Risk & Mitigation
- Risk: URLs or abbreviations followed by space may be split.
- Mitigation: Token-budget guard remains; splitting is acceptable for chunking.

### Tests / Verification
- 未実施（ロジック変更のみのため）
**2026-02-04 16:19 (Asia/Taipei) — Add debug logging for chunking**

### Summary
- Added DEBUG logs to show how input is split into segments and chunks.

### Context / Goal
- Make token-budget splitting decisions visible for tuning.

### Changes
- Logged segment list and final chunk list in chunking pipeline.

### Files Touched
- `TranslationService/translator_engine.py` — debug logs for chunking.

### Behavioral Impact
- When DEBUG logging is enabled, chunking details are emitted.

### Risk & Mitigation
- Risk: Large logs for very long inputs.
- Mitigation: Only emitted at DEBUG level.

### Tests / Verification
- 未実施（ログ追加のみのため）
**2026-02-04 16:29 (Asia/Taipei) — Add logging config to translation entrypoints**

### Summary
- Added configurable logging setup to translation server and test harness.

### Context / Goal
- Enable DEBUG chunking logs via environment without code changes.

### Changes
- Read `LOGLEVEL` in `server.py` and `test_translation_engine.py`.
- Standardized log format for easier debugging.

### Files Touched
- `TranslationService/server.py` — logging.basicConfig with LOGLEVEL.
- `TranslationService/test_translation_engine.py` — logging.basicConfig with LOGLEVEL.

### Behavioral Impact
- DEBUG logs can be enabled by setting `LOGLEVEL=DEBUG`.

### Risk & Mitigation
- Risk: Verbose logs if LOGLEVEL is set too high.
- Mitigation: Default remains INFO.

### Tests / Verification
- 未実施（ログ設定のみのため）
**2026-02-04 16:35 (Asia/Taipei) — Remove comma delimiters from chunking**

### Summary
- Removed comma-based delimiters to reduce over-splitting.

### Context / Goal
- Avoid unnatural splits caused by commas in long text.

### Changes
- Dropped Chinese/Japanese/English commas from the delimiter set.

### Files Touched
- `TranslationService/translator_engine.py` — delimiter set update.

### Behavioral Impact
- Chunking is less aggressive on comma-separated clauses.

### Risk & Mitigation
- Risk: Larger chunks before token-budget split.
- Mitigation: Token-budget guard still enforces the max length.

### Tests / Verification
- 未実施（ロジック変更のみのため）
**2026-02-04 16:41 (Asia/Taipei) — Remove unused quote candidates**

### Summary
- Removed the unused quote candidate set in delimiter splitting.

### Context / Goal
- Clean up leftover variables after simplifying English period rules.

### Changes
- Deleted unused `quote_candidates` variable.

### Files Touched
- `TranslationService/translator_engine.py` — removed unused variable.

### Behavioral Impact
- No runtime behavior changes.

### Risk & Mitigation
- Risk: None (unused variable).
- Mitigation: Not applicable.

### Tests / Verification
- 未実施（リファクタのみのため）
**2026-02-04 17:19 (Asia/Taipei) — Force local tokenizer loading**

### Summary
- Forced NLLB tokenizer loading to use local files only.

### Context / Goal
- Avoid remote access after pre-caching the base tokenizer.

### Changes
- Set the primary tokenizer load path to `local_files_only=True`.

### Files Touched
- `TranslationService/translator_engine.py` — local-only tokenizer load.

### Behavioral Impact
- Tokenizer loading will fail if local files are missing.

### Risk & Mitigation
- Risk: Missing cache causes load errors.
- Mitigation: Run the pre-cache command before starting.

### Tests / Verification
- 未実施（ロジック変更のみのため）
**2026-02-04 17:23 (Asia/Taipei) — Force base NLLB tokenizer**

### Summary
- Always load the base NLLB tokenizer locally to avoid regex warnings and quality regressions.

### Context / Goal
- Stabilize translation quality regardless of the model-bundled tokenizer.

### Changes
- Prefer `facebook/nllb-200-distilled-600M` tokenizer with local-only loading.
- Fall back to the model tokenizer only if the base tokenizer is missing.

### Files Touched
- `TranslationService/translator_engine.py` — tokenizer selection logic.

### Behavioral Impact
- Tokenization should be consistent with the base NLLB model.

### Risk & Mitigation
- Risk: Missing base tokenizer cache causes fallback.
- Mitigation: Pre-cache once using the provided command.

### Tests / Verification
- 未実施（ロジック変更のみのため）
**2026-02-04 18:37 (Asia/Taipei) — Set beam size for translation**

### Summary
- Set beam search size to 4 for NLLB translations.

### Context / Goal
- Improve translation quality for verification.

### Changes
- Added `beam_size=4` to CTranslate2 translate_batch calls.

### Files Touched
- `TranslationService/translator_engine.py` — translate_batch parameters.

### Behavioral Impact
- Translation quality may improve at the cost of slightly slower decoding.

### Risk & Mitigation
- Risk: Increased latency for large batches.
- Mitigation: Beam size can be tuned down if needed.

### Tests / Verification
- 未実施（パラメータ変更のみのため）
**2026-02-04 19:39 (Asia/Taipei) — Re-download model if binary missing**

### Summary
- Ensured the model auto-downloads if model.bin is missing.

### Context / Goal
- Fix startup failures when the model directory exists but is incomplete.

### Changes
- Added a model.bin existence check before skipping downloads.
- Log a warning and re-download if the directory is partial.

### Files Touched
- `TranslationService/translator_engine.py` — model path resolution guard.

### Behavioral Impact
- Partial model directories trigger a fresh download instead of failing.

### Risk & Mitigation
- Risk: Extra download if model.bin was manually removed.
- Mitigation: Only triggers when the file is missing.

### Tests / Verification
- 未実施（ロジック変更のみのため）
**2026-02-05 10:57 (Asia/Taipei) — On-demand OCR/CT2 loading**

### Summary
- Load PaddleOCR/CTranslate2 only when selected/enabled, with a busy overlay and failure rollback.

### Context / Goal
- Reduce startup overhead while keeping heavy OCR/translation resources resident once loaded.
- Provide clear UX for load failures and memory release timing.

### Changes
- Added on-demand host loading with busy overlay, failure dialogs, and settings rollback.
- Added settings UI notice about memory release requiring restart.

### Files Touched
- `MainWindow.xaml.cs` — on-demand host load flow, failure handling, and rollback.
- `MainWindow.xaml` — added resource-release notice text near OCR/CT2 settings.

### Behavioral Impact
- PaddleOCR/CTranslate2 hosts load only when selected/enabled; disabling does not unload until restart.
- Loading shows a busy overlay; failures disable the setting and show a dialog.

### Risk & Mitigation
- Risk: Users may expect memory to free immediately after turning OFF.
- Mitigation: Added explicit restart-required notice in settings and failure dialogs with log guidance.

### Tests / Verification
- 未実施（UI/起動時フローの目視が必要なため）
**2026-02-05 21:57 (Asia/Taipei) — Show overlay on toggle enable**

### Summary
- Ensure F9 re-enables the overlay window by calling Show before ShowLast.

### Context / Goal
- F9 toggling ON was not showing the overlay after it had been hidden.
- Keep the existing behavior that ShowLast is a no-op when no items exist.

### Changes
- Call `Show()` when enabling the overlay so the window becomes visible before `ShowLast()`.

### Files Touched
- `Services/OverlayPresenter.cs` — show window on enable before replaying last items.

### Behavioral Impact
- Toggling overlay ON now displays the overlay window even after a prior Hide; content still depends on last items.

### Risk & Mitigation
- Risk: Overlay window may appear empty if no items exist yet.
- Mitigation: Window is transparent and ShowLast still guards empty item updates.

### Tests / Verification
- 未実施（手動でF9トグル確認が必要なため）
**2026-02-05 22:44 (Asia/Taipei) — Remove overlay hide/show during OCR run**

### Summary
- Stop hiding the overlay window during OCR runs to eliminate flicker.

### Context / Goal
- Overlay flickers (A disappears and reappears before B) during OCR runs.
- Preserve continuous overlay visibility while new results are computed.

### Changes
- Removed per-run `Hide()`/`Show()` calls around the capture/OCR pipeline.

### Files Touched
- `Services/PipelineOrchestrator.cs` — removed overlay hide/show in `RunOnceAsync`.

### Behavioral Impact
- The previous overlay remains visible until the new overlay items are updated; no hide/show flicker.

### Risk & Mitigation
- Risk: If capture exclusion fails on some environments, the overlay could appear in OCR input because it stays visible.
- Mitigation: None in this change; can reintroduce conditional hide if needed.

### Tests / Verification
- 未実施（手動でOCR実行時のちらつき確認が必要なため）
**2026-02-05 23:59 (Asia/Taipei) — Toggle overlay via canvas opacity**

### Summary
- Keep the overlay window resident and toggle only the canvas visibility to avoid Hide/Show flashes.

### Context / Goal
- F9 hide/show caused flashes due to window Hide/Show behavior.
- Switch to transparency toggling while keeping the window shown.

### Changes
- Added `SetOverlayVisibility` to control overlay canvas opacity.
- `OverlayPresenter.Hide()` now toggles canvas visibility instead of hiding the window.
- `OverlayPresenter.Show()` ensures the window is shown and the canvas is visible.

### Files Touched
- `UI/OverlayWindow.xaml.cs` — added canvas visibility helper.
- `Services/OverlayPresenter.cs` — hide/show now toggle overlay visibility without window hide.

### Behavioral Impact
- F9 now hides the overlay by making it transparent while keeping the window shown; flashing from Hide/Show should be eliminated.

### Risk & Mitigation
- Risk: Minor compositing overhead from keeping the window visible.
- Mitigation: Canvas is fully transparent when hidden; overhead should be negligible.

### Tests / Verification
- 未実施（F9の表示/非表示とOCR実行の目視確認が必要なため）
**2026-02-06 00:18 (Asia/Taipei) — Suppress stale overlay on run start**

### Summary
- Prevent showing the previous overlay when auto-enabling at run start.

### Context / Goal
- After hiding via F9, starting OCR still flashed the previous overlay before new results.
- Ensure the overlay stays blank until the new update arrives.

### Changes
- Added optional `showLast` flag to `OverlayPresenter.SetEnabled`.
- Auto-enable during `RunOnce` now uses `showLast: false`.
- Hide now clears overlay visuals while keeping cached items for ShowLast.

### Files Touched
- `Services/OverlayPresenter.cs` — add `showLast` flag and clear visuals on hide.
- `MainWindow.xaml.cs` — suppress ShowLast when auto-enabling for a run.

### Behavioral Impact
- Starting OCR after F9 hide no longer shows the previous overlay before new items arrive.

### Risk & Mitigation
- Risk: Overlay remains blank until the next successful update when auto-enabled.
- Mitigation: Update flow still renders new results as usual; F9 toggle uses ShowLast.

### Tests / Verification
- 未実施（F9→OCRの目視確認が必要なため）
**2026-02-06 00:32 (Asia/Taipei) — Implement F11 overlay text toggle**

### Summary
- Switched F11 from OCR-only run to toggling overlay text between translated and source.

### Context / Goal
- Allow quick source/translated overlay switching without re-running OCR/translation.
- Ignore toggles while OCR/translation is running.

### Changes
- Added `OverlayTextMode` and stored last OCR/translation data in the pipeline.
- Made pipeline rebuild overlay items for the selected text mode on F11.
- Updated hotkey handler/logs and UI label to reflect the new F11 behavior.

### Files Touched
- `Models/OverlayTextMode.cs` — added overlay text mode enum.
- `Services/PipelineOrchestrator.cs` — store last OCR/translation data and support mode toggling.
- `MainWindow.xaml.cs` — F11 toggles overlay text mode with busy/no-data guard.
- `MainWindow.xaml` — hotkey label updated to “Overlay text”.

### Behavioral Impact
- F11 now toggles overlay between translated and source text without running OCR.
- Toggle is ignored during active OCR/translation or when no overlay data exists.

### Risk & Mitigation
- Risk: Users expecting OCR-only on F11 lose that behavior.
- Mitigation: Log message and UI label now reflect the new purpose.

### Tests / Verification
- 未実施（F11トグルとF8/F10の目視確認が必要なため）
**2026-02-06 00:45 (Asia/Taipei) — Show toast on OCR no-text**

### Summary
- Show a small “No text detected” toast at the focused window’s bottom-right when OCR finds zero lines, and clear the overlay.

### Context / Goal
- Users should see explicit feedback when OCR detects no text.
- Avoid showing stale overlay results on zero-line OCR runs.

### Changes
- Added toast UI elements to the overlay window and a timed show/hide method with a simple rate limit.
- Added `OverlayPresenter` APIs to clear overlays and show toasts.
- On zero-line OCR results, clear overlay and show the toast instead of showing the last overlay.

### Files Touched
- `UI/OverlayWindow.xaml` — added toast container UI.
- `UI/OverlayWindow.xaml.cs` — toast positioning, timing, and rate limiting.
- `Services/OverlayPresenter.cs` — added `ClearOverlay` and `ShowToast` wrappers.
- `Services/PipelineOrchestrator.cs` — zero-line OCR now clears overlay and shows toast.

### Behavioral Impact
- When OCR detects no text, the overlay is cleared and a brief “No text detected” toast appears.

### Risk & Mitigation
- Risk: Toast could spam on repeated failures.
- Mitigation: Added a minimum interval between toast displays.

### Tests / Verification
- 未実施（OCR 0行時の表示確認が必要なため）
**2026-02-06 00:48 (Asia/Taipei) — Fix missing DpiHelper using**

### Summary
- Added the missing namespace import for `DpiHelper` in the overlay window.

### Context / Goal
- Build failed because `DpiHelper` was referenced without its namespace.

### Changes
- Added `using Hotkey_Translator.Services;` to `OverlayWindow.xaml.cs`.

### Files Touched
- `UI/OverlayWindow.xaml.cs` — added missing using directive.

### Behavioral Impact
- No runtime behavior change; build error resolved.

### Risk & Mitigation
- Risk: None.
- Mitigation: N/A.

### Tests / Verification
- 未実施（ビルド再実行が必要）

**2026-02-06 11:14 (Asia/Taipei) — Add CUDA DLL search paths for CTranslate2**

### Summary
- Added venv CUDA DLL directories to the Windows loader search path before importing CTranslate2.

### Context / Goal
- Fix runtime failures where `cublas64_12.dll` exists in site-packages but cannot be loaded.

### Changes
- Added a Windows-only helper to register CUDA-related DLL folders from the `nvidia` package.
- Ensured the DLL paths are added before importing `ctranslate2`.

### Files Touched
- `TranslationService/translator_engine.py` — add CUDA DLL directories via `os.add_dll_directory` fallback.

### Behavioral Impact
- CTranslate2 GPU initialization can find CUDA DLLs shipped inside the venv without relying on global PATH.

### Risk & Mitigation
- Risk: Missing or unexpected `nvidia` package layout may skip path injection.
- Mitigation: Fallback preserves existing behavior; no changes on non-Windows platforms.

### Tests / Verification
- 未実施（手元での再現確認のみ）。

**2026-02-06 11:17 (Asia/Taipei) — Force CUDA DLL dirs into PATH**

### Summary
- Ensured CUDA DLL directories inside the venv are added to PATH even when add_dll_directory succeeds.

### Context / Goal
- `cublas64_12.dll` was still not found; some loaders rely on PATH instead of AddDllDirectory.

### Changes
- Always prepend detected CUDA DLL folders to PATH after registering them.

### Files Touched
- `TranslationService/translator_engine.py` — add PATH injection alongside DLL directory registration.

### Behavioral Impact
- DLL loader can resolve CUDA dependencies through PATH when `os.add_dll_directory` is insufficient.

### Risk & Mitigation
- Risk: PATH grows with repeated imports.
- Mitigation: The helper runs once per process and uses a small fixed set of directories.

### Tests / Verification
- 未実施（手元での再現確認のみ）。

**2026-02-06 11:27 (Asia/Taipei) — Add CUDA DLL paths for PaddleOCR**

### Summary
- Ensured PaddleOCR can find CUDA DLLs shipped inside the venv on Windows.

### Context / Goal
- Avoid CUDA load failures when DLLs live under `site-packages\nvidia\*\bin`.

### Changes
- Added Windows-only CUDA DLL path registration before importing `paddle`.

### Files Touched
- `OcrService/ocr_engine.py` — register CUDA DLL directories via `os.add_dll_directory` and PATH.

### Behavioral Impact
- PaddleOCR GPU initialization can resolve CUDA DLLs without relying on global PATH.

### Risk & Mitigation
- Risk: Missing or unexpected `nvidia` package layout may skip path injection.
- Mitigation: Fallback preserves existing behavior; no changes on non-Windows platforms.

### Tests / Verification
- 未実施（手元での再現確認のみ）。

**2026-02-06 12:14 (Asia/Taipei) — Draft Llama.cpp translation engine plan**

### Summary
- Documented an implementation plan for adding Llama.cpp (HY-MT1.5-1.8B) translation via gRPC-managed HTTP server.

### Context / Goal
- Provide a concrete plan for integrating llama-server as a new translation engine without fallback.

### Changes
- Added a new Doc file describing architecture, settings, steps, and risks.

### Files Touched
- `Doc/LlamaCpp_HY_MT1.5_Translation_Plan.md` — new implementation plan.

### Behavioral Impact
- No runtime impact (documentation only).

### Risk & Mitigation
- Risk: Plan assumptions may require adjustment after prototype.
- Mitigation: Validate with a small POC before full integration.

### Tests / Verification
- 未実施（ドキュメントのみ）。

**2026-02-06 13:09 (Asia/Taipei) — Note Llama/CT2 env separation**

### Summary
- Added a requirement to keep Llama and CTranslate2 in separate Python environments.

### Context / Goal
- Avoid dependency conflicts between Llama.cpp runtime and CTranslate2.

### Changes
- Documented environment separation in the Llama.cpp translation plan.

### Files Touched
- `Doc/LlamaCpp_HY_MT1.5_Translation_Plan.md` — add env separation assumption.

### Behavioral Impact
- No runtime impact (documentation only).

### Risk & Mitigation
- Risk: Increased setup overhead.
- Mitigation: Keeps CUDA/Dependency issues isolated per engine.

### Tests / Verification
- 未実施（ドキュメントのみ）。

**2026-02-06 13:11 (Asia/Taipei) — Expand Llama.cpp translation plan details**

### Summary
- Expanded the Llama.cpp translation plan with concrete settings, directory layout, and API mapping details.

### Context / Goal
- Provide implementation-ready guidance for the upcoming Llama translation engine work.

### Changes
- Added OpenAI-compatible HTTP request mapping details.
- Added configuration items and a separated environment directory plan.
- Refined step-by-step implementation outline for Llama integration.

### Files Touched
- `Doc/LlamaCpp_HY_MT1.5_Translation_Plan.md` — appended detailed integration guidance.

### Behavioral Impact
- No runtime impact (documentation only).

### Risk & Mitigation
- Risk: Plan may need adjustment once llama-server command options are finalized.
- Mitigation: Keep settings centralized so parameters can be tuned without code churn.

### Tests / Verification
- 未実施（ドキュメントのみ）。

**2026-02-06 13:21 (Asia/Taipei) — Clarify Llama service ownership and request policy**

### Summary
- Clarified that Llama runs as a separate TranslationServiceLlama and pinned HTTP API/concurrency decisions.

### Context / Goal
- Resolve open questions from the doc review for implementation readiness.

### Changes
- Defined `TranslationServiceLlama` as the host service and fixed `/v1/chat/completions` usage with non-streaming responses.
- Specified model path resolution and single-flight busy handling.

### Files Touched
- `Doc/LlamaCpp_HY_MT1.5_Translation_Plan.md` — added explicit service ownership and request policy details.

### Behavioral Impact
- No runtime impact (documentation only).

### Risk & Mitigation
- Risk: Busy policy may drop rapid updates.
- Mitigation: OCR pipeline favors fresh frames; latest request can retry on next tick.

### Tests / Verification
- 未実施（ドキュメントのみ）。

**2026-02-06 13:38 (Asia/Taipei) — Implement Llama.cpp translation engine integration**

### Summary
- Added a new Llama.cpp gRPC translation service and wired it into the WPF app with settings and exclusive selection.

### Context / Goal
- Implement the Llama.cpp (HY-MT1.5-1.8B) translation plan with a separate service, UI controls, and no fallback behavior.

### Changes
- Added `TranslationServiceLlama` (pyproject, proto, server, llama engine) that starts/monitors `llama-server` and exposes gRPC.
- Added Llama translation provider and gRPC host in WPF, plus settings/UI fields for model/server/params.
- Enforced Llama-only translation when enabled and prevented CTranslate2 from running concurrently.

### Files Touched
- `TranslationServiceLlama/pyproject.toml` — new Llama gRPC service environment.
- `TranslationServiceLlama/translation.proto` — gRPC contract copy.
- `TranslationServiceLlama/server.py` — gRPC server + llama-server startup/health.
- `TranslationServiceLlama/llama_engine.py` — HTTP client + single-flight translation logic.
- `Services/LlamaGrpcHost.cs` — WPF host for the Llama gRPC service.
- `Services/LlamaGrpcTranslationProvider.cs` — Llama translation provider implementation.
- `Services/TranslationFallbackService.cs` — Llama-enabled path bypasses fallback.
- `Models/AppSettings.cs` — Llama settings added.
- `Models/TranslationProviderNames.cs` — Llama provider name + defaults.
- `MainWindow.xaml` — Llama translation UI controls.
- `MainWindow.xaml.cs` — settings handling, host startup, exclusivity, status updates.

### Behavioral Impact
- When Llama translation is enabled, the app uses only Llama and does not fall back to other providers.
- CTranslate2 is prevented from running alongside Llama to avoid VRAM contention.

### Risk & Mitigation
- Risk: Incorrect llama-server path/model path causes startup failure.
- Mitigation: Startup failures disable Llama and show a load-failure dialog; status text indicates missing model.

### Tests / Verification
- 未実施（実装のみ）。

**2026-02-06 13:58 (Asia/Taipei) — Fix Llama system prompt to target language**

### Summary
- Removed editable system prompt UI and fixed the Llama prompt template to follow the target language.

### Context / Goal
- Ensure prompt alignment with target language without user-editable drift.

### Changes
- Removed Llama system prompt UI fields and settings persistence.
- Hard-coded the Llama system prompt template and linked it to the target language.
- Dropped the system prompt argument from the Llama gRPC host launch path.

### Files Touched
- `MainWindow.xaml` — removed Llama system prompt UI.
- `MainWindow.xaml.cs` — removed prompt bindings and config field.
- `Models/AppSettings.cs` — removed stored Llama prompt.
- `TranslationServiceLlama/llama_engine.py` — fixed prompt template by target language.
- `TranslationServiceLlama/server.py` — removed prompt CLI argument.
- `Services/LlamaGrpcHost.cs` — removed prompt argument passthrough.

### Behavioral Impact
- Llama translations always use the fixed template tied to the current target language.

### Risk & Mitigation
- Risk: Users lose the ability to customize the prompt.
- Mitigation: Template is stable and language-aware; customization can be reintroduced later if needed.

### Tests / Verification
- 未実施（実装のみ）。

**2026-02-06 16:08 (Asia/Taipei) — Create Llama binary directory**

### Summary
- Created a dedicated directory for llama-server binaries used by LlamaServerPath.

### Context / Goal
- Prepare a stable location for Llama.cpp runtime binaries.
- Make LlamaServerPath configuration predictable.

### Changes
- Added Tools/LlamaCpp/bin directory.

### Files Touched
- Tools/LlamaCpp/bin — new directory for placing llama-server.exe and dependent DLLs.

### Behavioral Impact
- No runtime behavior change until binaries are placed and LlamaServerPath is configured.

### Risk & Mitigation
- Risk: Path may still be misconfigured if pointing to a folder instead of executable.
- Mitigation: Set LlamaServerPath to Tools\\LlamaCpp\\bin\\llama-server.exe.

### Tests / Verification
- Verified directory creation via Get-ChildItem .\\Tools\\LlamaCpp.

**2026-02-06 16:14 (Asia/Taipei) — Align Llama binary folder with service-local layout**

### Summary
- Created a service-local Llama binary directory and updated default LlamaServerPath to match it.

### Context / Goal
- Use TranslationServiceLlama-relative binary placement for stable path resolution.
- Match runtime defaults to the agreed folder structure.

### Changes
- Added TranslationServiceLlama/LlamaCpp directory.
- Updated default/fallback LlamaServerPath to LlamaCpp\\llama-server.exe.

### Files Touched
- TranslationServiceLlama/LlamaCpp — new directory for llama-server.exe and DLLs.
- Models/AppSettings.cs — default LlamaServerPath changed to service-local relative path.
- MainWindow.xaml.cs — normalization fallback for empty LlamaServerPath updated.

### Behavioral Impact
- New setups default to TranslationServiceLlama\\LlamaCpp\\llama-server.exe without manual path rewrites.

### Risk & Mitigation
- Risk: Existing settings may still point to old paths.
- Mitigation: Existing explicit paths are preserved; only default/fallback changed.

### Tests / Verification
- Verified directory creation via Get-ChildItem .\\TranslationServiceLlama.
- Code-level path defaults verified by search and patch.

**2026-02-06 16:26 (Asia/Taipei) — Add plan for TranslationServiceLlama CUDA DLL auto-download**

### Summary
- Added an implementation plan document for auto-downloading CUDA12 DLL dependencies via pyproject.toml and boot-time PATH injection.

### Context / Goal
- Clarify whether current TranslationServiceLlama layout can auto-fetch CUDA DLLs.
- Provide a concrete implementation path that avoids global Windows PATH manual setup.

### Changes
- Added a new design document covering dependency strategy, startup flow, validation, and risks.

### Files Touched
- Doc/TranslationServiceLlama_CUDA_DLL_AutoDownload_Plan.md — New implementation plan for CUDA DLL auto-download and llama-server runtime resolution.

### Behavioral Impact
- No runtime behavior change yet (documentation-only task).

### Risk & Mitigation
- Risk: Plan may diverge from future runtime code structure.
- Mitigation: The document explicitly defines candidate files and DoD for implementation-time validation.

### Tests / Verification
- Verified file creation and UTF-8 output at Doc/TranslationServiceLlama_CUDA_DLL_AutoDownload_Plan.md.

**2026-02-06 18:53 (Asia/Taipei) — Fix sentence boundary loss in NLLB chunk merge**

### Summary
- Fixed missing translation tail caused by dropped sentence boundaries during chunk merge.

### Context / Goal
- Translation output sometimes omitted the latter sentence after delimiter-based splitting.
- Preserve boundaries so split-and-merge input stays semantically equivalent to original text.

### Changes
- Replaced raw string concatenation in chunk assembly with boundary-aware merging.
- Added _merge_with_boundary(left, right) to restore a single separator when delimiters removed whitespace.
- Applied the same boundary-aware merge when recombining translated chunk outputs.

### Files Touched
- TranslationService/translator_engine.py — Added boundary-aware merge helper and wired it into pre-translation chunk merge and post-translation output merge.

### Behavioral Impact
- Inputs split into multiple segments/chunks keep sentence boundaries (". Without" no longer becomes ".Without").
- Reduces cases where NLLB drops or degrades translation of later sentences.

### Risk & Mitigation
- Risk: Boundary insertion could add spaces in some punctuation edge cases.
- Mitigation: Merge logic avoids insertion when whitespace/newline already exists or right side starts with closing punctuation.

### Tests / Verification
- Ran: uv run test_translation_engine.py --device gpu --auto-download --source-lang eng_Latn --target-lang jpn_Jpan --input .TransTest\test4.txt
- Verified debug log changed from fused chunk (Switch.Without) to proper boundary (Switch. Without).
- Verified output now contains translation for the latter sentence (Without further ado...).

**2026-02-06 19:01 (Asia/Taipei) — Prioritize no-split path under token budget**

### Summary
- Added a fast path to skip sentence splitting when input is already within the token budget.

### Context / Goal
- Current translation flow always performs delimiter splitting even for short inputs.
- Reduce unnecessary preprocessing and avoid boundary-related quality regressions when text is <= 512 tokens.

### Changes
- Added token-count precheck in split_text_by_token_budget.
- If 	oken_count <= max_tokens, return the original text as a single chunk and skip delimiter splitting.
- Added debug log to make split/no-split decision observable.

### Files Touched
- TranslationService/translator_engine.py — Added no-split fast path before delimiter-based segmentation.

### Behavioral Impact
- Inputs within token budget now translate as a single chunk.
- Oversized inputs still use existing split logic.

### Risk & Mitigation
- Risk: Token counting on full text can still be relatively expensive.
- Mitigation: This replaces broader split processing for in-budget inputs and keeps existing overflow logic unchanged.

### Tests / Verification
- Ran: uv run test_translation_engine.py --device gpu --auto-download --source-lang eng_Latn --target-lang jpn_Jpan --input .TransTest\\test4.txt
- Verified debug log: Skip splitting: token_count=78 <= max_tokens=512.
- Ran a long-text check with small budget (max_tokens=64) and confirmed multi-chunk split still occurs.

**2026-02-06 19:17 (Asia/Taipei) — Use soft split budget for long NLLB inputs**

### Summary
- Changed chunking policy so long texts are actively split with a soft budget (256) instead of being repacked into one 512-token chunk.

### Context / Goal
- After enabling no-split mode, long inputs (e.g., ~330 tokens) were still merged into a single chunk and often lost the latter part in translation.
- Keep short inputs unsplit while forcing stable chunking for longer inputs.

### Changes
- Introduced soft_no_split_tokens = min(max_tokens, 256) in split_text_by_token_budget.
- Added explicit split mode when 
aw_tokens > soft_no_split_tokens.
- Applied split_budget (256) to segment normalization and final chunk packing, preventing re-merge into one 512-token chunk.
- Added debug log for split activation and active budgets.

### Files Touched
- TranslationService/translator_engine.py — Updated split policy and budget usage for long inputs.

### Behavioral Impact
- Inputs up to 256 tokens remain unsplit.
- Inputs over 256 tokens are split into multiple chunks (each <= 256 tokens) before translation, reducing tail-drop cases.

### Risk & Mitigation
- Risk: More chunks can increase latency slightly on long paragraphs.
- Mitigation: Keep no-split fast path for short inputs and use deterministic split logging for tuning.

### Tests / Verification
- Ran: uv run test_translation_engine.py --device gpu --auto-download --source-lang eng_Latn --target-lang jpn_Jpan --input .TransTest\\test3.txt with PYTHONUTF8=1.
- Verified debug log changed to split mode: Enable splitting: token_count=330 > soft_no_split_tokens=256.
- Verified Final chunks became 2 chunks (not 1), and output includes later-part content.

**2026-02-06 23:06 (Asia/Taipei) — Force translated overlay on F8/F10 runs**

### Summary
- F8/F10 実行時にオーバーレイ表示モードを Translated へ自動で戻すようにした。

### Context / Goal
- F11 で Source 表示へ切り替えた後に F8/F10 を押すと、翻訳実行でも原文表示のままになる。
- F8/F10 は翻訳実行操作として常に翻訳文表示で始まるようにする。

### Changes
- F8 (OnHotkeyPressed) と F10 (OnForceRunHotkeyPressed) の先頭で、翻訳表示モードへ復帰させる処理を追加。
- PipelineOrchestrator.TrySetOverlayTextMode に allowModeUpdateWithoutData を追加し、オーバーレイ未生成時でもモードのみ更新できるようにした（F11 の既存動作は維持）。

### Files Touched
- MainWindow.xaml.cs — F8/F10 実行前に EnsureTranslatedOverlayForRunHotkeys() を呼び出す処理を追加。
- Services/PipelineOrchestrator.cs — モード更新APIに任意フラグを追加し、データ未生成時のモード更新を許可。

### Behavioral Impact
- F8/F10 実行時は常に翻訳文表示モードでオーバーレイが更新される。
- F11 のトグル仕様とエラーメッセージ動作は従来どおり。

### Risk & Mitigation
- Risk: 実行直前に表示モードが意図せず戻ることで、F11 での一時確認結果が保持されない。
- Mitigation: 変更対象を F8/F10 のみへ限定し、通常の F11 トグル経路は変更しない。

### Tests / Verification
- 実施: dotnet build Hotkey-Translator.csproj -v minimal
- 結果: 成功（警告 0 / エラー 0）

**2026-02-06 23:54 (Asia/Taipei) — Draft fixed OCR window lock implementation plan**

### Summary
- OCR対象ウィンドウを固定する機能の実装案を Doc/ に追加した。

### Context / Goal
- 現状は ActiveWindow 前提で、フォーカス移動時にOCR対象が変わる。
- OCR対象を固定し、フォーカス非依存で同一ウィンドウを継続認識できる設計案を整理する。

### Changes
- 現行の CaptureManager / Provider / AppSettings の制約を踏まえた段階導入プランを作成。
- AppSettings 追加項目、ICaptureProvider I/F拡張、フォールバック方針、DoD を明文化。

### Files Touched
- Doc/FixedCaptureWindow_Plan.md — 固定ウィンドウOCR機能の実装提案を新規作成。

### Behavioral Impact
- ドキュメント追加のみで、アプリ実行時の挙動変更はない。

### Risk & Mitigation
- Risk: 実装前提の認識違いが残る可能性。
- Mitigation: 現行コード構造に基づいた影響範囲・非ゴール・段階導入を先に合意できる内容にした。

### Tests / Verification
- 未実施（設計ドキュメント作成のみ）。

**2026-02-06 23:59 (Asia/Taipei) — Revise fixed-window OCR plan to hotkey-only flow**

### Summary
- 固定ウィンドウOCR計画をUI前提からホットキー固定/解除前提へ更新した。

### Context / Goal
- 固定対象の指定はUIよりホットキー運用の方が操作負荷が低く、実装も小さく抑えられる。
- Doc/FixedCaptureWindow_Plan.md を実運用に合わせて再設計する。

### Changes
- UI導線（固定ボタン/表示）の記述を削除し、ホットキー方式（固定/解除）へ置換。
- 実装手順、影響範囲、DoD をホットキー中心に再定義。
- フォールバック方針とログ運用を「UIなしで状態追跡可能」な方針に更新。

### Files Touched
- Doc/FixedCaptureWindow_Plan.md — ホットキー専用案へ全面更新。

### Behavioral Impact
- ドキュメント更新のみ。アプリ実行挙動は変更なし。

### Risk & Mitigation
- Risk: UIなしのため固定状態が見えづらい。
- Mitigation: ログで固定/解除/再解決/フォールバックを明示する方針を計画へ反映。

### Tests / Verification
- 未実施（設計ドキュメント更新のみ）。

**2026-02-07 00:04 (Asia/Taipei) — Add user-configurable hotkey spec to fixed-window plan**

### Summary
- 固定ウィンドウ計画に「他ホットキーと同様にユーザー変更可能」な仕様を追記した。

### Context / Goal
- 固定/解除ホットキーも既存 F8/F9/F10/F11 と同等に、ユーザーが設定画面で変更できる必要がある。
- Hotkey-only 方針を維持しつつ、設定可能性を計画へ明示する。

### Changes
- ゴールに「固定/解除ホットキーのユーザー変更」を追加。
- 非ゴールを「固定対象専用UI」に限定し、Hotkeys設定統合は対象内に明確化。
- 実装手順 Step 3 を「既存 Hotkeys 設定へ統合」に更新。
- 影響範囲に MainWindow.xaml の Hotkeys 設定項目追加を反映。
- DoD に「固定/解除ホットキーの変更・保存」を追加。

### Files Touched
- Doc/FixedCaptureWindow_Plan.md — ホットキー変更可能要件を反映して更新。

### Behavioral Impact
- ドキュメント更新のみ。実行挙動の変更はない。

### Risk & Mitigation
- Risk: 専用UIを作らない方針と設定変更要件の境界が曖昧になる。
- Mitigation: 専用固定UIは非ゴール、既存Hotkeys設定統合はゴールとして分離して明記。

### Tests / Verification
- 未実施（設計ドキュメント更新のみ）。

**2026-02-07 15:33 (Asia/Taipei) — Add model auto-download paths to Llama CUDA plan**

### Summary
- Llama CUDA 自動セットアップ計画に、モデル自動ダウンロードと保存先パスを追加した。

### Context / Goal
- 初回起動時の手動モデル配置をなくし、Llama 起動失敗を減らしたい。
- 指定パス TranslationServiceLlama\\LlamaCpp / TranslationServiceLlama\\LlamaCpp\\Models を計画へ明示する。

### Changes
- ドキュメント題名を CUDA DLL + Model Auto-Download に拡張。
- モデル自動取得（不足時ダウンロード、.tmp 受信後 rename）をフローへ追加。
- 保存先を TranslationServiceLlama\\LlamaCpp\\Models に固定して明記。
- API案に EnsureLlamaModelAsync() を追加。
- リスクに「モデルDL中断時の不完全ファイル」を追加し、緩和策を明記。
- DoD に「初回起動でモデル自動配置」を追加。

### Files Touched
- Doc/TranslationServiceLlama_CUDA_DLL_AutoDownload_Plan.md — モデル自動ダウンロード仕様と保存先パスを反映。

### Behavioral Impact
- ドキュメント更新のみ。アプリ実行挙動の変更はない。

### Risk & Mitigation
- Risk: モデル取得仕様が実装と先行して差異が出る可能性。
- Mitigation: パス・手順・検証条件をDoDまで具体化し、実装時の受け入れ基準を固定した。

### Tests / Verification
- 未実施（設計ドキュメント更新のみ）。

**2026-02-07 15:35 (Asia/Taipei) — Pin Llama auto-download model to qwen3-1_7b-instruct-q4_k_m.gguf**

### Summary
- Llama 自動ダウンロード計画の対象モデルを qwen3-1_7b-instruct-q4_k_m.gguf に固定した。

### Context / Goal
- 利用予定モデルが確定したため、汎用 <model>.gguf 記述ではなく実際のモデル名で計画を具体化する。
- 実装時の保存先/検証条件の解釈ブレを防ぐ。

### Changes
- 前提・シーケンス・API説明・バリデーション・実装手順・DoD の各節で対象モデル名を qwen3-1_7b-instruct-q4_k_m.gguf に統一。
- モデル存在確認・不足時DL・初回配置確認の記述を具体名ベースに更新。

### Files Touched
- Doc/TranslationServiceLlama_CUDA_DLL_AutoDownload_Plan.md — モデル名を qwen3-1_7b-instruct-q4_k_m.gguf に固定して更新。

### Behavioral Impact
- ドキュメント更新のみ。アプリ実行挙動の変更はない。

### Risk & Mitigation
- Risk: 今後モデルを変更する際にドキュメント更新漏れが起きる。
- Mitigation: モデル変更時は本計画の対象モデル名とDoDを同時更新する運用を明記して管理する。

### Tests / Verification
- 未実施（設計ドキュメント更新のみ）。

**2026-02-07 15:41 (Asia/Taipei) — Incorporate self-review recommendations into Llama auto-download plan**

### Summary
- Llama 自動セットアップ計画へ、自己レビュー指摘（供給源固定・モデルパス上書き・DoD強化）を反映した。

### Context / Goal
- モデル取得元未定義、LlamaModelPath 不整合、検証不足のリスクを解消したい。
- 運用時の再現性と安全性を高めるため、仕様を具体化する。

### Changes
- model_manifest.json を新設してモデルURL/revision/SHA256 を固定管理する方針を追加。
- LlamaModelPath を管理パスへ上書きする運用を明記（ユーザー指定より管理値優先）。
- uv sync の実行条件を「初回または lock/manifest 変更時のみ」に限定。
- モデルDL時の .tmp + rename + SHA256 検証を必須化。
- DoD を強化し、/v1/models の 200 応答と翻訳API最小1ケース成功を追加。

### Files Touched
- Doc/TranslationServiceLlama_CUDA_DLL_AutoDownload_Plan.md — 自己レビュー推奨事項を反映して更新。

### Behavioral Impact
- ドキュメント更新のみ。アプリ実行挙動の変更はない。

### Risk & Mitigation
- Risk: 実装時にマニフェスト管理と設定上書き仕様の整合が崩れる可能性。
- Mitigation: API案・実装手順・DoDを同じ前提（管理パス優先）に統一して明記した。

### Tests / Verification
- 未実施（設計ドキュメント更新のみ）。

**2026-02-07 15:51 (Asia/Taipei) — Fix Llama paths as non-editable and remove UI path controls in plan**

### Summary
- Llama の LlamaServerPath / LlamaModelPath を固定運用にし、UIから削除する方針を計画へ反映した。

### Context / Goal
- モデル/サーバーパスをユーザー編集可能にすると、自動取得モデルと実行実体がずれて不具合が起きやすい。
- 固定パス運用に統一し、配布・サポート・再現性を高める。

### Changes
- 概要・ゴールに「固定パス運用」「ユーザー編集不可」を明記。
- 非ゴールに「LlamaServerPath / LlamaModelPath の編集機能維持」を追加。
- 前提に固定パス（TranslationServiceLlama\\LlamaCpp\\llama-server.exe / ...\\Models\\qwen3-1_7b-instruct-q4_k_m.gguf）を明記。
- シーケンス/内部API/実装手順を「上書き」から「固定パス使用・設定読み取り廃止」へ更新。
- 影響範囲に MainWindow.xaml / MainWindow.xaml.cs のパス入力UI削除を追加。
- 既存設定無視による混乱リスクと緩和策（告知・UI削除）を追記。

### Files Touched
- Doc/TranslationServiceLlama_CUDA_DLL_AutoDownload_Plan.md — パス固定化とUI削除方針を反映して更新。

### Behavioral Impact
- ドキュメント更新のみ。アプリ実行挙動の変更はない。

### Risk & Mitigation
- Risk: 既存ユーザーが設定値が反映されないことに戸惑う。
- Mitigation: リリースノートとUIの明示削除で「固定パス運用」へ誘導する。

### Tests / Verification
- 未実施（設計ドキュメント更新のみ）。
**2026-02-07 16:02 (Asia/Taipei) — Implement fixed Llama paths and model auto-download**

### Summary
- Llama の実行パスを固定化し、モデル自動ダウンロードと CUDA 依存検証を実装しました。

### Context / Goal
- `Doc/TranslationServiceLlama_CUDA_DLL_AutoDownload_Plan.md` の内容をコードへ反映する。
- `LlamaServerPath` / `LlamaModelPath` の可変設定を廃止し、起動失敗要因を減らす。

### Changes
- `LlamaGrpcHost` に起動前処理を追加（`uv sync` ガード、固定パス解決、ネイティブ必須ファイル検証、モデルDL+SHA256検証、CUDA DLL PATH 注入）。
- `TranslationServiceLlama/model_manifest.json` を新規追加し、`qwen3-1_7b-instruct-q4_k_m.gguf` の取得元とハッシュを固定。
- `TranslationServiceLlama/pyproject.toml` に CUDA 関連依存を追加。
- UI から `LlamaServerPath` / `LlamaModelPath` 入力を削除し、`AppSettings` から当該設定を削除。

### Files Touched
- `Services/LlamaGrpcHost.cs` — 固定パス運用、`uv sync` 実行条件管理、モデル自動DL、SHA256検証、CUDA DLL 検証、PATH 前置を実装。
- `TranslationServiceLlama/model_manifest.json` — モデルファイル名/URL/SHA256/サイズを定義（新規）。
- `TranslationServiceLlama/pyproject.toml` — CUDA 12 系 Python 依存を追加。
- `Models/AppSettings.cs` — `LlamaServerPath` / `LlamaModelPath` プロパティを削除。
- `MainWindow.xaml` — Llama の path 入力UIを削除し固定運用の説明文へ変更。
- `MainWindow.xaml.cs` — path 入出力と正規化処理を削除、Llama 設定比較レコードを更新。

### Behavioral Impact
- Llama 起動時は常に `TranslationServiceLlama\LlamaCpp\llama-server.exe` と `TranslationServiceLlama\LlamaCpp\Models\qwen3-1_7b-instruct-q4_k_m.gguf` を使用します。
- 初回またはランタイム定義変更時のみ `uv sync` を実行し、モデル未配置時は自動ダウンロードします。

### Risk & Mitigation
- Risk: モデル配布元変更やネットワーク障害で自動DLが失敗する可能性。
- Mitigation: `model_manifest.json` 固定 + SHA256 検証 + 失敗時例外で原因を明示。

### Tests / Verification
- `dotnet build Hotkey-Translator.sln` を実行し成功（0 errors, 0 warnings）。
- 実環境でのモデルDL/llama-server 起動の結合確認は未実施（ネットワーク・GPU依存のため）。
**2026-02-07 16:16 (Asia/Taipei) — Switch model manifest to public Instruct URL**

### Summary
- Llama モデルの取得先を、匿名ダウンロード可能な公開URLへ切り替えました。

### Context / Goal
- 既存のモデルURLで `401 Unauthorized` が発生し、起動時の自動DLが失敗していた。
- トークン不要で取得できる配布元へ変更し、初期化失敗を解消する。

### Changes
- `TranslationServiceLlama/model_manifest.json` の `download_url` を `leuconoe/Qwen3-1.7B-Instruct-GGUF` の commit 固定URLへ変更。
- 同ファイルの `sha256` と `size_bytes` を公開APIの値に更新。

### Files Touched
- `TranslationServiceLlama/model_manifest.json` — 公開URL・ハッシュ・サイズを更新。

### Behavioral Impact
- 起動時のモデル自動ダウンロードが、認証不要のURLで実行されるようになります。

### Risk & Mitigation
- Risk: 将来URLが無効化される可能性。
- Mitigation: revision固定URLを採用し、失敗時は `model_manifest.json` の差し替えで復旧可能にする。

### Tests / Verification
- `curl -L -I` で更新URLが `HTTP 200` を返すことを確認。
- アプリ実行でのエンドツーエンド確認は未実施。
**2026-02-07 16:25 (Asia/Taipei) — Fix llama-server log decoding to avoid cp932 crash**

### Summary
- `llama_engine.py` の subprocess ログ読み取りで UTF-8 + replace を指定し、`UnicodeDecodeError` を回避しました。

### Context / Goal
- `llama-server` の出力を cp932 として読んだ際に `UnicodeDecodeError` が発生し、pump スレッドが落ちていた。
- 即効対応として、ログ読取のデコード失敗で処理全体が止まらないようにする。

### Changes
- `subprocess.Popen(..., text=True)` に `encoding="utf-8"` と `errors="replace"` を追加。
- 不正バイトは置換文字で扱い、行読み取りループが継続するように変更。

### Files Touched
- `TranslationServiceLlama/llama_engine.py` — Popen のデコード設定を追加。

### Behavioral Impact
- `cp932` 由来のデコード例外でログスレッドが停止しにくくなり、起動待機中の安定性が向上します。
- 一部ログ文字は `�` に置換される可能性があります。

### Risk & Mitigation
- Risk: UTF-8 以外の出力で文字化けが残る可能性。
- Mitigation: `errors="replace"` によりクラッシュは回避し、必要なら次段でバイナリ読み＋多段デコードへ拡張可能。

### Tests / Verification
- `python -m py_compile TranslationServiceLlama/llama_engine.py` を実行し成功。
**2026-02-07 16:37 (Asia/Taipei) — Add Llama gRPC block-based smoke test script**

### Summary
- `TranslationServiceLlama` 向けに、空行でリクエストを分割する翻訳テストスクリプトを追加しました。

### Context / Goal
- 本番に近い形で、複数テキストを1リクエストにまとめつつ、空行で別リクエストとして Llama 翻訳を検証したい。
- 既存の CTranslate2 テストスクリプトと同等の使い勝手で、Llama 専用検証を可能にする。

### Changes
- `TranslationServiceLlama/test_translation_engine.py` を新規追加。
- `--input` 指定時は UTF-8 ファイルを読み込み、空行でリクエスト分割・非空行を `texts[]` 要素として送信。
- `Health` 確認後に各リクエストを順次 `Translate` し、入出力・レイテンシ・空結果件数を表示。
- `--continue-on-error` を追加し、エラー時継続テストを可能化。

### Files Touched
- `TranslationServiceLlama/test_translation_engine.py` — Llama gRPC 用のブロック分割テストCLIを追加。

### Behavioral Impact
- LlamaCpp 翻訳だけを、ファイル入力ベースで本番に近い単位（空行区切り）で検証できるようになりました。

### Risk & Mitigation
- Risk: 実運用の OCR 差分・キャッシュ挙動までは再現しない。
- Mitigation: 本スクリプトは gRPC 翻訳レイヤの切り分け目的に限定し、必要時はアプリ統合テストと併用する。

### Tests / Verification
- `python -m py_compile TranslationServiceLlama/test_translation_engine.py` 実行成功。
- `uv run test_translation_engine.py --help` でCLI起動確認。
**2026-02-07 16:43 (Asia/Taipei) — Add device/batch/max_tokens args to Llama test script**

### Summary
- Llama テストスクリプトに `--device` / `--batch-size` / `--max-tokens` 引数を追加し、指定値を起動へ反映できるようにしました。

### Context / Goal
- `TranslationServiceLlama/test_translation_engine.py` で、LlamaCpp 翻訳テスト時に実行条件（CPU/GPU、batch、max_tokens）を切り替えたい。
- 既存の接続テスト用途を維持しつつ、指定時にはローカルサーバ起動で設定値を実適用したい。

### Changes
- 新規引数を追加: `--device {cpu,gpu}`, `--batch-size`, `--max-tokens`, `--gpu-layers`。
- `--auto-start-server` とサーバ起動関連引数（`--llama-server`, `--model`, `--llama-host`, `--llama-port`, `--startup-timeout-sec`）を追加。
- 上記引数が指定された場合、`server.py` をローカル起動して設定を反映した状態でテスト実行するよう実装。
- 起動ログのポンプ、health 待機、終了時のプロセス停止処理を追加。

### Files Touched
- `TranslationServiceLlama/test_translation_engine.py` — 引数拡張とローカルサーバ自動起動ロジックを追加。

### Behavioral Impact
- 既存サーバ接続テストに加え、CLI引数で Llama 実行設定を切り替えた再現テストが可能になりました。

### Risk & Mitigation
- Risk: 指定ポート使用中などでローカル起動に失敗する可能性。
- Mitigation: health タイムアウトと例外メッセージで失敗理由を明示し、既存サーバ利用モードも継続可能にする。

### Tests / Verification
- `python -m py_compile TranslationServiceLlama/test_translation_engine.py` 実行成功。
- `uv run test_translation_engine.py --help` で追加引数表示を確認。
**2026-02-07 16:53 (Asia/Taipei) — Inject CUDA DLL PATH into Llama test auto-start flow**

### Summary
- `test_translation_engine.py` のローカル起動経路に CUDA DLL PATH 注入を追加し、`llama-server` の DLL 解決失敗を回避するよう修正しました。

### Context / Goal
- テストスクリプトの `--device` 指定時に自動起動した `llama-server` が `0xC0000135` で即終了していた。
- アプリ本体 (`LlamaGrpcHost`) と同様に `.venv` 内 NVIDIA DLL を PATH 前置して、同等の起動条件に合わせる。

### Changes
- `.venv\Lib\site-packages\nvidia\{cuda_runtime,cublas,nvjitlink,cudnn}\bin` を収集する処理を追加。
- 収集したディレクトリを `subprocess.Popen(..., env=...)` の `PATH` に前置する処理を追加。
- GPU指定時に `cudart64_12.dll / cublas64_12.dll / cublasLt64_12.dll` の存在を事前検証し、不足時は明示エラーで停止する処理を追加。

### Files Touched
- `TranslationServiceLlama/test_translation_engine.py` — CUDA DLL 収集・検証・PATH 注入ロジックを追加。

### Behavioral Impact
- `--device gpu` を含むローカル自動起動テストで、DLL不足による即終了の再現を減らし、原因を事前に明示できるようになります。

### Risk & Mitigation
- Risk: CUDA DLL が未インストール環境では GPU 指定時に即エラー終了する。
- Mitigation: 失敗理由を不足DLL名つきで表示し、環境修復後に再実行しやすくする。

### Tests / Verification
- `python -m py_compile TranslationServiceLlama/test_translation_engine.py` 実行成功。
- `uv run test_translation_engine.py --help` 実行成功。
**2026-02-07 16:58 (Asia/Taipei) — Prevent test script from spawning/retaining extra llama-server processes**

### Summary
- Llama テスト実行時に追加プロセスが残留してVRAMを占有し続ける問題を防ぐため、起動条件と終了処理を見直しました。

### Context / Goal
- `test_translation_engine.py` が `--device` 指定だけで自動起動し、毎回 server/llama-server が増える運用になっていた。
- 中断時や終了時に子プロセス (`llama-server.exe`) が残留しないようにしたい。

### Changes
- ローカル自動起動は `--auto-start-server` 指定時のみ有効化。
- `--device/--batch-size/--max-tokens/--gpu-layers` を `--auto-start-server` なしで指定した場合は、明示エラーで終了。
- Windows では停止時に `taskkill /PID <pid> /T /F` でプロセスツリー全体を終了する処理へ変更。

### Files Touched
- `TranslationServiceLlama/test_translation_engine.py` — 自動起動条件の明確化とプロセスツリー終了処理を追加。

### Behavioral Impact
- 意図しない追加起動が減り、テスト終了後に `llama-server.exe` が残ってVRAMを占有する事象を抑制します。

### Risk & Mitigation
- Risk: 既存の手順で `--device` のみ指定していた場合、エラー終了になる。
- Mitigation: `--auto-start-server` を併用する明示手順へ統一し、既存サーバ利用時は起動オプションを外す。

### Tests / Verification
- `python -m py_compile TranslationServiceLlama/test_translation_engine.py` 実行成功。
- `uv run test_translation_engine.py --help` 実行成功。
**2026-02-07 17:09 (Asia/Taipei) — Add optional keep-server mode for Llama test auto-start**

### Summary
- `--auto-start-server` 利用時に、終了後の自動停止を無効化できる `--keep-server` を追加しました。

### Context / Goal
- 毎回 `--auto-start-server` で起動して都度終了する運用だと、連続試験時の再起動コストが高い。
- ただしデフォルト安全性（自動停止）も維持したい。

### Changes
- 新規引数 `--keep-server` を追加。
- `--keep-server` は `--auto-start-server` と併用必須にし、単独指定時は明示エラーで終了。
- 後始末処理を条件分岐し、`--keep-server` 指定時はローカル起動サーバを停止しない。

### Files Touched
- `TranslationServiceLlama/test_translation_engine.py` — keep-server オプションと終了処理分岐を追加。

### Behavioral Impact
- 既定動作は従来通り「自動起動 + 自動停止」を維持。
- 明示的に `--keep-server` を付けた場合のみ、テスト終了後もサーバ常駐を継続できます。

### Risk & Mitigation
- Risk: `--keep-server` 利用後に常駐サーバが残り、VRAM占有やポート競合が発生する可能性。
- Mitigation: オプトイン化し、必要時のみ利用する。終了時は手動で `taskkill /IM llama-server.exe /F` 等を実行。

### Tests / Verification
- `python -m py_compile TranslationServiceLlama/test_translation_engine.py` 実行成功。
- `uv run test_translation_engine.py --help` で `--keep-server` 表示を確認。
**2026-02-07 17:12 (Asia/Taipei) — Roll back keep-server option from Llama test script**

### Summary
- 直前に追加した `--keep-server` 機能をロールバックし、従来の自動停止動作へ戻しました。

### Context / Goal
- ユーザー依頼により、最新変更（keep-server 追加）を取り消す必要があった。
- 既存の安全動作（auto-start 時は終了時に停止）を維持する。

### Changes
- `--keep-server` 引数定義を削除。
- `--keep-server` の入力バリデーションを削除。
- finally 節の停止条件を元に戻し、auto-start したローカルサーバは常に停止する動作へ復帰。

### Files Touched
- `TranslationServiceLlama/test_translation_engine.py` — keep-server 関連コードを削除して前状態へ復帰。

### Behavioral Impact
- `--auto-start-server` 使用時はテスト終了時にローカルサーバを自動停止します。

### Risk & Mitigation
- Risk: 連続試験時に毎回起動コストが発生する。
- Mitigation: 常駐運用が必要な場合は既存サーバ手動起動モード（`--auto-start-server` なし）を利用する。

### Tests / Verification
- `python -m py_compile TranslationServiceLlama/test_translation_engine.py` 実行成功。
- `uv run test_translation_engine.py --help` で `--keep-server` 非表示を確認。
**2026-02-07 18:30 (Asia/Taipei) — Add Gemini-like JSON batch send mode to Llama test script**

### Summary
- `test_translation_engine.py` に、Gemini送信に近い「JSON配列を1回で送る」検証モードを追加しました。

### Context / Goal
- 現行の gRPC 分割送信と比較し、LlamaCpp でも 1リクエスト JSON バッチ方式を試験できるようにする。
- 本番送信方式の比較検証を、テストスクリプトだけで完結させる。

### Changes
- `--send-mode` 引数を追加し、`grpc`（既存）/`llama-json-batch`（新規）を切替可能にした。
- `llama-json-batch` 用に `/v1/models` ヘルス待機、モデル名解決、`/v1/chat/completions` 送信、JSON抽出/復元処理を追加した。
- HTTP タイムアウト調整用に `--http-timeout-sec` を追加した。
- 後始末を補強し、HTTP クライアントと gRPC チャネルを明示クローズするようにした。

### Files Touched
- `TranslationServiceLlama/test_translation_engine.py` — JSONバッチ送信モード、CLI引数、応答パース、クライアント終了処理を追加。

### Behavioral Impact
- 既定の `grpc` モード動作は維持。
- `--send-mode llama-json-batch` 指定時のみ、Llama HTTP API に JSON配列を1回送信する経路で翻訳を試験可能。

### Risk & Mitigation
- Risk: モデルが厳密JSONで返さない場合、一部出力が空になる可能性。
- Mitigation: JSONコードフェンス除去とオブジェクト抽出を実装し、配列長不一致時は `source_text` マッピングで復元する。

### Tests / Verification
- `python -m py_compile TranslationServiceLlama/test_translation_engine.py` 実行成功。
- `uv run test_translation_engine.py --help` で `--send-mode` / `--http-timeout-sec` 表示を確認。
**2026-02-07 18:55 (Asia/Taipei) — Add plan for downstream single-HTTP batching in Llama engine**

### Summary
- `llama_engine.py` の下流HTTPを原則1回化するための実装案を `Doc/` に新規作成しました。

### Context / Goal
- 現在は gRPC 1回でも下流HTTPが item 数だけ発生するため、往復回数を減らす設計方針を明確化する必要があった。
- UI制限超過時のみ分割する条件付きバッチ戦略を文書化する。

### Changes
- 新規ドキュメント `Doc/LlamaCpp_DownstreamHttp_Batch_Request_Plan.md` を追加。
- 実装案テンプレートに沿って、ゴール/非ゴール、設計、手順、リスク、DoD を整理。
- `index` ベース復元、JSON崩れ時フォールバック、上限超過時分割方針を明記。

### Files Touched
- `Doc/LlamaCpp_DownstreamHttp_Batch_Request_Plan.md` — 下流HTTP一括送信化の実装計画書を新規作成。

### Behavioral Impact
- 実コード変更は未実施。挙動変更はありません。

### Risk & Mitigation
- Risk: 計画書のみで実装未着手のため、実運用改善は未反映。
- Mitigation: 次ステップで計画に基づき `TranslationServiceLlama/llama_engine.py` を段階実装する。

### Tests / Verification
- ドキュメント内容を UTF-8 で書き出し、`Get-Content -Encoding UTF8` で確認。
**2026-02-07 19:07 (Asia/Taipei) — Implement downstream single-HTTP batch translation in llama_engine**

### Summary
- `TranslationServiceLlama/llama_engine.py` を実装し、複数入力を原則1回の下流HTTPで送信し、制約超過時のみ分割送信するように変更しました。

### Context / Goal
- gRPC では1リクエストでも、下流 `chat/completions` が入力件数分発生していた。
- 下流HTTP往復回数を削減しつつ、UI設定上限を超えるケースのみ安全に分割する必要があった。

### Changes
- `LlamaTranslator.translate()` を一括送信ベースの `adaptive split` フローへ置換。
- 一括送信用のプロンプト生成（index付き入力JSON）と JSON パース処理を追加。
- `index` ベースで出力復元し、順序・件数の整合を維持するようにした。
- 推定トークン数と `context_size/max_tokens/batch_size` に基づく分割判定を追加。
- 一括失敗時は自動2分割で再試行し、単一要素失敗時は空文字で位置整合を維持するフォールバックを追加。
- 実行ログに `items/http_calls/splits` を追加し、下流呼び出し回数を追跡可能にした。

### Files Touched
- `TranslationServiceLlama/llama_engine.py` — 下流HTTP一括送信、制約ベース分割、JSON復元、ログ強化を実装。

### Behavioral Impact
- 複数テキスト入力時、通常は下流 `POST /v1/chat/completions` が1回になります。
- 制約超過見込みまたは応答不正時のみ分割して複数回送信します。
- 応答パース失敗時の耐障害性が向上し、要素順序・件数の不整合を起こしにくくなります。

### Risk & Mitigation
- Risk: 文字数ベースのトークン推定が実トークンと乖離する場合、分割判定が過不足になる可能性。
- Mitigation: 下流失敗時に自動分割再試行するフォールバックを実装し、推定誤差を吸収。

### Tests / Verification
- `python -m py_compile TranslationServiceLlama/llama_engine.py` 実行成功。
- `python -m py_compile TranslationServiceLlama/server.py` 実行成功。
- `uv run test_translation_engine.py --help` 実行成功（回帰なし確認）。
- `uv run -` で `parse_batch_translation_content` の簡易実行確認（`['A', 'B']` を確認）。
**2026-02-07 20:07 (Asia/Taipei) — Fix llama-server orphan process on app shutdown**

### Summary
- WPF終了後に llama-server.exe が残る問題を、起動経路と終了処理の両面で修正しました。

### Context / Goal
- Llama経路で、親プロセス終了後に llama-server.exe が残留するケースがあった。
- アプリ終了時に Llama 関連プロセスを確実に停止させることが目的。

### Changes
- Services/LlamaGrpcHost.cs の起動方式を uv run ... から .venv の python 直接起動へ変更。
- Services/LlamaGrpcHost.cs に .venv Python 解決処理 (ResolvePythonExecutable) を追加。
- TranslationServiceLlama/server.py に SIGINT/SIGTERM ハンドラを追加し、終了時に gRPC 停止を要求。
- `TranslationServiceLlama/server.py` の `finally` で `host.stop()` を必ず実行するよう変更。
- Doc/LlamaCpp_llama_server_shutdown_fix.md を新規作成し、原因・修正内容・検証結果を記録。

### Files Touched
- Services/LlamaGrpcHost.cs — Llama gRPC ホストの実行プロセスを .venv Python 直接起動へ変更。
- TranslationServiceLlama/server.py — gRPC 終了経路で llama-server を必ず停止する処理を追加。
- Doc/LlamaCpp_llama_server_shutdown_fix.md — 調査と修正内容のドキュメントを追加。

### Behavioral Impact
- WPF アプリ終了時、Llama gRPC ホスト配下の llama-server.exe が残留しにくくなります。
- 実行時依存同期 (uv sync) は維持され、起動時のみ .venv Python を使用します。

### Risk & Mitigation
- Risk: .venv の Python 実行ファイルが壊れている/欠損している場合、Llama ホスト起動に失敗する。
- Mitigation: 起動前に実行ファイル存在チェックを行い、明示的なエラーで失敗させる。

### Tests / Verification
- dotnet build -nologo 実行成功（0 error / 0 warning）。
- python -m py_compile TranslationServiceLlama/server.py TranslationServiceLlama/llama_engine.py 実行成功。
**2026-02-07 20:16 (Asia/Taipei) — Add fallback reaper for lingering llama-server.exe**

### Summary
- 先行修正で残った llama-server.exe 残留ケースに対し、PID追跡と実行ファイル一致チェック付きの回収フォールバックを追加しました。

### Context / Goal
- `.venv` Python 直起動と `finally: host.stop()` 導入後も、環境により llama-server.exe が残るケースがあった。
- 親プロセス状態に依存しない停止経路を追加して、残留を実用上解消することが目的。

### Changes
- TranslationServiceLlama/llama_engine.py に llama-server pid=<pid> ログを追加。
- Services/LlamaGrpcHost.cs で llama-server pid ログを抽出し、子 PID を追跡する処理を追加。
- Services/LlamaGrpcHost.cs の Stop() に、通常 kill 後のフォールバック回収処理を追加。
- フォールバックでは PID 直接 kill を試行し、失敗時は固定 llama-server.exe パス一致プロセスを列挙して回収するようにした。
- 誤kill防止のため、MainModule.FileName と固定サーバーパスの一致検証を追加。
- Doc/LlamaCpp_llama_server_shutdown_fix_followup.md を新規作成し、追加対策を記録。

### Files Touched
- Services/LlamaGrpcHost.cs — PID追跡、停止時フォールバック回収、実行ファイル一致検証を追加。
- TranslationServiceLlama/llama_engine.py — llama-server 起動 PID のログ出力を追加。
- Doc/LlamaCpp_llama_server_shutdown_fix_followup.md — フォローアップ修正ドキュメントを追加。

### Behavioral Impact
- WPF終了時に親プロセスが既に終了しているケースでも、llama-server.exe を回収できる可能性が上がる。
- 回収対象は固定サーバーパス一致に限定されるため、誤killリスクを抑制。

### Risk & Mitigation
- Risk: 一部環境で MainModule 参照が失敗すると PID回収がスキップされる可能性。
- Mitigation: PID回収に失敗した場合でも、同一実行ファイルパス列挙で二段階フォールバックを実施。

### Tests / Verification
- dotnet build -nologo 実行成功（0 error / 0 warning）。
- python -m py_compile TranslationServiceLlama/server.py TranslationServiceLlama/llama_engine.py 実行成功。



**2026-02-07 21:07 (Asia/Taipei) — Draft UI model selection plan for LlamaCpp models folder**

### Summary
- TranslationServiceLlama\\LlamaCpp\\Models 配下のモデルをUIで選択可能にする実装案を作成しました。

### Context / Goal
- ユーザーが配置済みの .gguf モデルをUIから選択したい要望がある。
- 現在は固定モデル運用のため、設定保存・起動時反映・フォールバックを含む設計を明確化する必要がある。

### Changes
- Doc/LlamaCpp_Model_Selector_UI_Plan.md を新規作成。
- 実装案テンプレートに沿って、ゴール/非ゴール、設計、手順、リスク、DoD を整理。
- 既存manifest運用との整合（既定モデルのみ自動DL管理、ユーザー選択モデルは存在確認）方針を明記。

### Files Touched
- Doc/LlamaCpp_Model_Selector_UI_Plan.md — LlamaCpp モデル選択UIの実装案を新規作成。

### Behavioral Impact
- 実コード変更は未実施。アプリ挙動の変更はありません。

### Risk & Mitigation
- Risk: 計画のみで実装未着手のため、現状の固定モデル仕様は継続する。
- Mitigation: 次ステップで計画に沿って AppSettings / MainWindow / LlamaGrpcHost を段階実装する。

### Tests / Verification
- Get-Content -Encoding UTF8 Doc/LlamaCpp_Model_Selector_UI_Plan.md で内容確認。

**2026-02-07 21:16 (Asia/Taipei) — Implement LlamaCpp model selector from Models folder**

### Summary
- TranslationServiceLlama\\LlamaCpp\\Models 配下の .gguf をUIで選択し、Llama起動モデルへ反映できるよう実装しました。

### Context / Goal
- ユーザー配置モデルをUIから選択して利用したい要望がある。
- 固定モデル依存を解消しつつ、既定モデルのmanifest自動ダウンロード運用は維持したい。

### Changes
- AppSettings に LlamaSelectedModelFileName を追加し、選択モデルを保存可能にした。
- LlamaModelCatalog を新規追加し、Models 配下 .gguf 列挙とファイル名正規化を実装した。
- MainWindow.xaml に Llamaモデル選択 ComboBox と Reload ボタンを追加した。
- MainWindow.xaml.cs にモデル候補再読込・UI反映・保存処理を実装した。
- LlamaHostConfig 比較項目にモデル名を追加し、設定差分検知対象に含めた。
- LlamaGrpcHost を選択モデル起動へ変更し、既定モデル選択時のみmanifest自動DL/検証を適用するようにした。

### Files Touched
- Models/AppSettings.cs — LlamaSelectedModelFileName 設定項目を追加。
- Services/LlamaModelCatalog.cs — .gguf 列挙・モデル名正規化・固定配下解決ロジックを新規実装。
- MainWindow.xaml — Llamaモデル選択UI（ComboBox/Reload）を追加。
- MainWindow.xaml.cs — モデル候補読込、設定保存、正規化、ホスト設定比較を更新。
- Services/LlamaGrpcHost.cs — 選択モデル解決と起動引数反映、manifest適用条件分岐を実装。

### Behavioral Impact
- Llama翻訳有効時に、Models 配下の任意 .gguf をUIで選択して起動できるようになった。
- 選択モデルが既定以外の場合は自動ダウンロードせず、ローカル存在チェックのみ行う。
- 選択モデルが欠損している場合は起動時に明示エラーとなる。

### Risk & Mitigation
- Risk: ユーザーが巨大モデルを選ぶとVRAM不足で起動失敗する可能性。
- Mitigation: 起動時に失敗を明示ログ化し、モデル選択を変更して再起動できる運用を維持。

### Tests / Verification
- dotnet build -nologo 実行成功（0 error / 0 warning）。
- python -m py_compile TranslationServiceLlama/server.py TranslationServiceLlama/llama_engine.py 実行成功。

**2026-02-07 21:22 (Asia/Taipei) — Add runtime controls for Llama restart and stop in UI**

### Summary
- WPF UI に Restart Llama.cpp と Stop llama-server を追加し、実行中ホストを即時操作できるようにしました。

### Context / Goal
- Llamaパラメータ/モデルの反映が再起動前提だったため、手動で再起動・停止するUI操作が必要だった。
- 停止操作時に設定が再起動を誘発しないよう、UI状態と保存設定を同期する必要があった。

### Changes
- MainWindow.xaml のLlamaセクションに Restart Llama.cpp ボタンと Stop llama-server ボタンを追加。
- MainWindow.xaml.cs に OnRestartLlamaCpp を追加し、実行中ホスト停止後にLlama有効状態で保存フローを実行して再起動するよう実装。
- MainWindow.xaml.cs に OnStopLlamaServer を追加し、ホスト停止後にLlama無効状態を保存して再起動を抑止するよう実装。
- 明示操作と設定保存の整合について WHY コメントを追加。

### Files Touched
- MainWindow.xaml — Llamaランタイム操作ボタン（再起動・停止）を追加。
- MainWindow.xaml.cs — 再起動/停止ハンドラを追加し、ホスト停止と設定保存の同期を実装。

### Behavioral Impact
- ユーザーはWPF再起動なしで Llama ホストの再起動と停止を実行できる。
- Stop llama-server 実行時は Llama 設定も OFF で保存されるため、他設定保存時に意図せず再起動しにくくなる。

### Risk & Mitigation
- Risk: 再起動時に Llama 起動失敗するとユーザーが状態を見失う可能性。
- Mitigation: 失敗時はログ記録とエラーダイアログ表示を行い、原因追跡を容易化。

### Tests / Verification
- dotnet build -nologo 実行成功（0 error / 0 warning）。
- g -n "OnRestartLlamaCpp|OnStopLlamaServer|Restart Llama.cpp|Stop llama-server" MainWindow.xaml MainWindow.xaml.cs でイベント接続を確認。

**2026-02-07 22:04 (Asia/Taipei) — Tighten Llama batch prompts for JSON compliance**

### Summary
- llama_engine.py のバッチ system/user プロンプトを、JSON厳格出力を優先する内容に更新しました。

### Context / Goal
- 小型モデルでのJSON崩れを減らし、	ranslations[index] 形式の安定出力率を上げたい。
- 入力構造を明示して、出力の配列順序・件数維持をより強く誘導したい。

### Changes
- uild_batch_system_prompt を厳格JSONルール中心の文面に変更。
- uild_batch_user_prompt で入力を {"items":[...]} 形式に変更し、キー非翻訳を明示。
- 入力JSONを separators=(",", ":") で圧縮し、無駄な空白を減らした。

### Files Touched
- TranslationServiceLlama/llama_engine.py — バッチ翻訳向け system/user プロンプトを更新。

### Behavioral Impact
- Llama下流応答で JSON schema 遵守率が向上することを期待。
- 翻訳品質よりフォーマット順守を優先する方向に寄る。

### Risk & Mitigation
- Risk: 制約強化により、まれに訳文自然さが低下する可能性。
- Mitigation: 必要に応じて system 側制約を緩和し、user 側入力提示を維持して調整可能にした。

### Tests / Verification
- python -m py_compile TranslationServiceLlama/llama_engine.py TranslationServiceLlama/server.py 実行成功。

**2026-02-07 23:17 (Asia/Taipei) — Add JSON schema response_format for multi-item Llama batches**

### Summary
- llama_engine.py の複数件バッチ送信に esponse_format(json_schema) を追加しました。

### Context / Goal
- 小型モデルでJSON崩れが発生しやすいため、下流生成をスキーマ制約で安定化したい。
- 既存の分割/フォールバック挙動は変更せず、最小差分で導入したい。

### Changes
- _translate_batch_once で len(texts) > 1 の場合のみ payload["response_format"] を追加。
- uild_batch_json_schema_response_format() を新規追加し、	ranslations[index, translated_text] の厳格スキーマを定義。
- WHY コメントを追加し、複数件時のみ制約を掛ける意図を明示。

### Files Touched
- TranslationServiceLlama/llama_engine.py — 複数件バッチの esponse_format(json_schema) 追加とスキーマ定義関数を実装。

### Behavioral Impact
- 複数件翻訳リクエストで、llama-server の構造化出力制約が有効になる。
- 単一件翻訳では従来どおり esponse_format を付与せず挙動維持。
- 既存のJSONパース失敗時フォールバック（分割/空文字）は維持。

### Risk & Mitigation
- Risk: 一部 llama-server 実装差異で esponse_format 未対応の場合、HTTPエラーとなる可能性。
- Mitigation: 既存の例外処理と分割フォールバック経路を維持し、失敗時は段階的に縮小して処理継続。

### Tests / Verification
- python -m py_compile TranslationServiceLlama/llama_engine.py TranslationServiceLlama/server.py 実行成功。

**2026-02-07 23:31 (Asia/Taipei) — Draft robustness plan for grammar fallback and parser rescue**

### Summary
- grammar フォールバック、短縮スキーマ、パーサー救済の実装案を Doc/ に作成しました。

### Context / Goal
- 複数件翻訳でも JSON 崩れが残るため、モデル限界を前提に実装側で回復率を上げたい。
- 最小差分で段階導入できる計画を明文化したい。

### Changes
- Doc/LlamaCpp_Json_Robustness_Plan.md を新規作成。
- json_schema -> grammar の2段階送信、短縮キー 	/i/x、救済パーサー強化案を定義。
- 影響範囲、リスク、DoD、段階導入方針を整理。

### Files Touched
- Doc/LlamaCpp_Json_Robustness_Plan.md — JSON堅牢化の実装案ドキュメントを新規作成。

### Behavioral Impact
- 実コード変更は未実施。アプリ挙動の変更はありません。

### Risk & Mitigation
- Risk: 計画のみで実装未着手のため、現状のJSON崩れ挙動は継続する。
- Mitigation: 次ステップで計画に沿って llama_engine.py を段階実装し、ログで効果検証する。

### Tests / Verification
- Get-Content -Encoding UTF8 Doc/LlamaCpp_Json_Robustness_Plan.md で内容確認。


**2026-02-07 23:50 (Asia/Taipei) — 単件プレーンテキスト方針の明確化**

### Summary
- JSON堅牢化計画に単件時プレーンテキストの適用条件を明確化しました。

### Context / Goal
- 単件時はJSON制約を外す方針を、実装時に解釈ブレが出ない粒度まで具体化したい。
- DoDで検証可能な条件を追加し、実装・確認の着地点を揃えたい。

### Changes
- インターフェース設計に、単件時は esponse_format / grammar を送らない方針を追記。
- 実装手順に、単件レスポンスはJSONパーサーを通さない手順を追記。
- Definition of Doneに、単件送信ログで制約未付与を確認する条件を追加。

### Files Touched
- Doc/LlamaCpp_Json_Robustness_Plan.md — 単件プレーンテキスト方針の適用条件と検証条件を具体化。

### Behavioral Impact
- 実コード変更は未実施。動作への直接影響はありません。

### Risk & Mitigation
- Risk: 計画更新のみで実装未反映のため、現状挙動は変わらない。
- Mitigation: 次ステップで計画に沿って llama_engine.py を実装し、段階別ログで検証する。

### Tests / Verification
- Get-Content -Encoding UTF8 Doc/LlamaCpp_Json_Robustness_Plan.md で追記内容を確認。

**2026-02-08 00:15 (Asia/Taipei) — JSON堅牢化計画の実装（schema->grammar / 単件プレーン / 救済パース）**

### Summary
- Llamaバッチ翻訳に、単件プレーン化・短縮スキーマ・grammarフォールバック・救済パースを実装しました。

### Context / Goal
- 複数件翻訳でJSON崩れが残るため、追加遅延を失敗時のみに限定して回復率を上げる必要がある。
- 単件翻訳ではJSON制約を外し、構文出力の露出を抑えたい。

### Changes
- _translate_batch_once を分岐化し、len(texts)==1 は esponse_format/grammar を付けない単件プレーン翻訳へ変更。
- 複数件は 1回目 esponse_format(json_schema)、失敗時のみ 2回目 grammar で再送する段階実行へ変更。
- 出力スキーマを短縮キー 	/i/x に変更し、旧キー 	ranslations/index/translated_text も互換受理。
- パーサーを救済強化（コードフェンス除去、最外JSON抽出、エスケープ改行正規化、余剰 } 切り詰め、JSON文字列の再デコード）。
- 段階別ログを追加（plain_single_success, schema_success, grammar_fallback_success, parser_rescue_success, split_fallback）。

### Files Touched
- TranslationServiceLlama/llama_engine.py — 送信経路・制約指定・スキーマ定義・パーサー救済・ログを実装。

### Behavioral Impact
- 単件翻訳はプレーンテキスト処理に固定され、JSON文字列露出リスクが低下。
- 複数件翻訳は通常1回送信維持、schema失敗時のみgrammar再送で回復を試行。
- 既存の分割フォールバックは維持され、最終失敗時は従来どおり分割再試行。

### Risk & Mitigation
- Risk: grammar 指定が環境差分で未対応だと再送が失敗する可能性。
- Mitigation: grammar失敗時は既存 split フォールバックに移行し、処理継続する。
- Risk: 救済パースが過剰に通す可能性。
- Mitigation: 型・件数・index範囲チェックを維持し、不整合は失敗扱いにする。

### Tests / Verification
- python -m py_compile TranslationServiceLlama/llama_engine.py TranslationServiceLlama/server.py 実行成功。
- 実行時テストは未実施（このシェル環境に httpx が未導入のためローカル実行確認は未実施）。

**2026-02-08 15:54 (Asia/Taipei) — Fixed capture window hotkey lock/unlock 実装**

### Summary
- ActiveWindow をホットキーで固定/解除できる固定キャプチャ機能を実装しました。

### Context / Goal
- OCR対象がフォーカス移動で変わるため、任意ウィンドウを固定して継続キャプチャしたい。
- 固定対象が無効化された場合でも処理を止めず、通常 ActiveWindow へ安全に戻したい。

### Changes
- 固定対象メタデータ（HWND/PID/Process/Class/Title）と固定/解除ホットキー設定を AppSettings に追加。
- WindowBindingService を新規追加し、フォアグラウンド固定・再探索・解除を実装。
- ICaptureProvider を CaptureRequest ベースへ拡張し、固定 HWND を各 Provider（WGC/DXGI/GDI）へ伝搬。
- CaptureManager に固定対象解決と無効時フォールバック（ActiveWindow）ログを追加。
- Hotkeys 設定UIに Lock/Unlock を追加し、MainWindow のホットキー登録導線へ統合。
- 既存ホットキー競合の懸念に対して、登録前の重複検証を追加。

### Files Touched
- Models/AppSettings.cs — 固定対象と固定/解除ホットキーの設定項目を追加。
- Models/CaptureRequest.cs — Provider へ渡すキャプチャ要求モデルを新規追加。
- Models/FixedCaptureWindowSpec.cs — 固定対象ウィンドウ情報モデルを新規追加。
- Services/WindowBindingService.cs — 固定対象の取得・再探索・解除ロジックを新規実装。
- Services/ICaptureProvider.cs — TryGetBounds/TryCapture を CaptureRequest 受け取りへ変更。
- Services/CaptureManager.cs — 固定対象解決、無効時フォールバック、状態ログを追加。
- Services/WgcCaptureProvider.cs — CaptureRequest.TargetWindowHandle 対応を追加。
- Services/DxgiDuplicationProvider.cs — CaptureRequest.TargetWindowHandle 対応を追加。
- Services/GdiCaptureProvider.cs — CaptureRequest.TargetWindowHandle 対応を追加。
- MainWindow.xaml — Hotkeys 設定に Lock/Unlock 行を追加。
- MainWindow.xaml.cs — Lock/Unlock ホットキー配線、設定同期、競合検証、イベント処理を追加。

### Behavioral Impact
- CaptureMode=ActiveWindow かつ固定有効時、F12（既定）で現在フォーカス中ウィンドウを固定可能。
- Shift+F12（既定）で固定解除し、通常 ActiveWindow キャプチャへ復帰。
- 固定対象が無効な場合は処理を継続したまま ActiveWindow へ自動フォールバック。

### Risk & Mitigation
- Risk: PID/Class/Title が重複する環境で再探索誤一致の可能性。
- Mitigation: PID優先で厳密一致し、識別情報が空の場合は再探索せずフォールバックする。
- Risk: ホットキー重複で登録失敗する可能性。
- Mitigation: 登録前に重複を検出し、既存ホットキーを維持したままエラーログを出す。

### Tests / Verification
- dotnet build Hotkey-Translator.sln 実行成功（0 errors / 0 warnings）。

**2026-02-08 16:09 (Asia/Taipei) — ホットキー登録失敗の波及防止とF7既定化**

### Summary
- 固定/解除ホットキー既定を F7 系へ変更し、1件登録失敗が他ホットキーへ波及しないよう修正しました。

### Context / Goal
- F12 系が外部アプリと競合し、RegisterHotKey 失敗時に全ホットキー無効化が発生していた。
- 失敗時は該当ホットキーのみ失敗扱いとし、他ホットキーを継続利用可能にしたい。

### Changes
- 固定/解除ホットキーの既定を F7 / Shift+F7 に変更。
- 旧既定（F12 / Shift+F12）を起動時に F7 系へ自動移行する正規化処理を追加。
- ホットキー登録を個別適用方式へ変更し、失敗時はそのホットキーのみロールバックする実装に変更。
- 登録失敗ログに失敗理由（例外メッセージ）を出力するよう改善。
- 初期化時の全体フォールバック再登録を廃止し、二重失敗ログと全体無効化を回避。

### Files Touched
- Models/AppSettings.cs — Lock/Unlock の既定値を F7 / Shift+F7 に変更。
- Services/HotkeyManager.cs — 現在バインド情報（Key/Modifiers）の参照プロパティを追加。
- MainWindow.xaml.cs — Hotkey正規化、個別登録・失敗時ロールバック、失敗ログ改善、起動メッセージ更新を実装。

### Behavioral Impact
- あるホットキーの登録失敗時でも、他の登録済みホットキーは有効なまま維持される。
- 旧 F12 既定設定で保存済みでも、起動時に F7 系へ移行される。

### Risk & Mitigation
- Risk: 設定上で重複キーを指定した場合、一部ホットキーのみ無効になる可能性。
- Mitigation: 重複検知ログを出し、失敗ホットキーのみロールバックして他ホットキーを維持する。

### Tests / Verification
- dotnet build Hotkey-Translator.sln 実行成功（0 errors / 0 warnings）。

**2026-02-08 16:21 (Asia/Taipei) — ROI選択ホットキー追加（F6既定）**

### Summary
- ROI選択をホットキーから起動できるようにし、既存ホットキー設定UIへ統合しました。

### Context / Goal
- ROI再設定をマウス移動なしで素早く呼び出せるようにしたい。
- 既存のホットキー登録方式（失敗しても他キーへ波及しない）に合わせて追加したい。

### Changes
- AppSettings に HotkeySelectRoiKey / HotkeySelectRoiModifiers を追加（既定 F6 / None）。
- Hotkeys設定UIに Select ROI 行を追加し、他ホットキーと同じ保存導線へ統合。
- MainWindow に ROIホットキーイベントを追加し、既存 ROI選択処理を SelectRoiAsync() として共通化。
- ホットキー登録処理に SelectRoi バインディング（id=5）を追加し、既存個別登録/ロールバック方式で運用。
- 起動時ログに ROIホットキー案内（F6）を追記。

### Files Touched
- Models/AppSettings.cs — ROI選択ホットキー設定を追加。
- MainWindow.xaml — Hotkeysセクションに Select ROI の Key/Modifiers 入力を追加。
- MainWindow.xaml.cs — 設定反映、登録配線、実行ハンドラ、正規化を追加。

### Behavioral Impact
- F6（既定）で ROI選択ウィンドウを直接開ける。
- 他ホットキー同様、UIでキー/修飾キーの変更と保存が可能。
- ROIホットキーの登録失敗時も、他ホットキーは継続利用できる。

### Risk & Mitigation
- Risk: 他アプリと F6 が競合すると ROIホットキーだけ無効になる可能性。
- Mitigation: 失敗時ログを出しつつ、他ホットキーを維持する個別登録方式を維持。

### Tests / Verification
- dotnet build Hotkey-Translator.sln 実行成功（0 errors / 0 warnings）。
**2026-02-08 17:00 (Asia/Taipei) — キャプチャ右下ローディングスピナー実装**

### Summary
- OCR実行中にキャプチャ（またはROI）右下へ追従するスピナーを追加し、完了/失敗時に必ず消えるようにしました。

### Context / Goal
- 実行開始からオーバーレイ更新完了までの処理中状態を、キャプチャ領域の近傍で即時に視認したい。
- 既存の `OverlayCanvas.Opacity` 制御を壊さず、スピナーだけ独立レイヤで制御したい。

### Changes
- `OverlayWindow` にスピナー表示/非表示APIと回転アニメーション制御を実装。
- `OverlayPresenter` に `ShowLoadingSpinner` / `HideLoadingSpinner` を追加し、Screen座標からDIP変換を統一。
- `RunOnceAsync` の開始時にスピナー表示、`finally` で確実に非表示化する連動を追加。
- ROIが無効・不正な場合はキャプチャ矩形をアンカーにフォールバックするようにした。
- Overlay無効化経路（`Hide`）でもスピナーを強制非表示にするガードを追加。

### Files Touched
- `UI/OverlayWindow.xaml` — スピナー描画レイヤー（`SpinnerCanvas`）と回転用要素を追加。
- `UI/OverlayWindow.xaml.cs` — スピナー表示/非表示、位置計算、回転アニメーション開始/停止を追加。
- `Services/OverlayPresenter.cs` — スピナー表示APIを追加し、非表示時の残留防止を実装。
- `MainWindow.xaml.cs` — 実行開始/終了時のスピナー連動、アンカー矩形解決と例外時フォールバックを追加。

### Behavioral Impact
- 実行開始直後からキャプチャ対象右下にスピナーが表示され、処理終了時に自動で消えます。
- ROI有効時はROI右下、ROI無効または不正時はキャプチャ領域右下に表示されます。
- オーバーレイ本文のOpacity制御とは独立してスピナーが動作し、Overlay無効化時は同時に消えます。

### Risk & Mitigation
- Risk: 座標変換失敗や境界取得失敗でスピナー処理が本処理に影響する可能性。
- Mitigation: スピナー表示/非表示を例外ガードし、失敗してもOCR実行フローを継続。

### Tests / Verification
- `dotnet build Hotkey-Translator.sln` 実行成功（0 errors / 0 warnings）。
**2026-02-09 11:43 (Asia/Taipei) — Llamaデフォルト自動ダウンロードモデルをHY-MT1.5-1.8B-Q8_0へ変更**

### Summary
- Llama.cpp のデフォルト自動ダウンロード対象を `HY-MT1.5-1.8B-Q8_0.gguf` に切り替えた。

### Context / Goal
- 既定モデルを `qwen3-1_7b-instruct-q4_k_m.gguf` から、指定された `HY-MT1.5-1.8B-Q8_0.gguf` に置き換えたい。
- 「設定変更のみ」では自動DL対象にならないため、デフォルト名と manifest をコード上で一致させる必要がある。

### Changes
- `model_manifest.json` の `filename` / `download_url` / `sha256` / `size_bytes` を HY-MT1.5-1.8B-Q8_0 に更新。
- C# 側のデフォルトモデル名定数を新モデル名へ更新。
- `AppSettings` の既定 `LlamaSelectedModelFileName` を新モデル名へ更新。
- Python テストの既定モデルパスを新モデル名へ更新。

### Files Touched
- `TranslationServiceLlama/model_manifest.json` — 自動DL先URLと検証値（SHA256/サイズ）を新モデルに更新。
- `Services/LlamaGrpcHost.cs` — `DefaultLlamaModelFileName` を `HY-MT1.5-1.8B-Q8_0.gguf` に更新。
- `MainWindow.xaml.cs` — UI側フォールバック既定モデル名を新モデルに更新。
- `Models/AppSettings.cs` — `LlamaSelectedModelFileName` の既定値を新モデルに更新。
- `TranslationServiceLlama/test_translation_engine.py` — テスト既定モデルパスを新モデル名へ更新。

### Behavioral Impact
- Llama有効時、既定選択モデルが `HY-MT1.5-1.8B-Q8_0.gguf` になり、未配置時は manifest URL から自動DLされる。
- 既存の自動検証（SHA256/サイズ）フローは維持される。

### Risk & Mitigation
- Risk: `resolve/main` URL は upstream 更新の影響を受けうる。
- Mitigation: SHA256/サイズ検証で改変を検知し、不一致時は起動時に明示エラーで停止する。

### Tests / Verification
- `dotnet build Hotkey-Translator.sln` 実行成功（0 errors / 0 warnings）。
- `uv run --project TranslationServiceLlama pytest TranslationServiceLlama/test_translation_engine.py` は `pytest` 未導入のため未実施。
**2026-02-09 13:39 (Asia/Taipei) — TranslationPriority準拠化とCTranslate2/GoogleWeb整理の実装**

### Summary
- Llama固定分岐を廃止して優先度順選択へ統一し、CTranslate2/GoogleWeb を優先度UIと実行経路から整理した。

### Context / Goal
- `TranslationPriority` に従って翻訳エンジンを選択するようにし、Llama有効時の固定実行を解消したい。
- CTranslate2/GoogleWeb を段階廃止する前提で、既定優先度とUI表示を `LlamaCpp, Gemini, DeepL` に揃えたい。

### Changes
- `TranslationFallbackService` の Llama直行分岐を削除し、優先度ループへ一本化。
- 優先度正規化で `TranslationProviderNames.Defaults` 以外（CTranslate2/GoogleWeb含む旧値）を除外。
- 既定優先度を `LlamaCpp, Gemini, DeepL` に変更。
- MainWindow の翻訳プロバイダ登録から CTranslate2 を除外。
- CTranslate2 は設定ロード/保存時に常にOFFへ寄せ、ホスト起動判定を常時 false 化。
- Translation status から CTranslate2 表示を除外。
- Translation設定UIの CTranslate2 ブロックを `Visibility="Collapsed"` として非表示化。

### Files Touched
- `Services/TranslationFallbackService.cs` — Llama固定分岐削除、優先度順ログ強化、許可プロバイダのみ正規化。
- `Models/TranslationProviderNames.cs` — 既定優先度を `LlamaCpp, Gemini, DeepL` へ更新。
- `MainWindow.xaml.cs` — CTranslate2プロバイダ除外、CTranslate2無効化、優先度正規化フィルタ、状態表示更新。
- `MainWindow.xaml` — CTranslate2 設定UIを非表示化。

### Behavioral Impact
- Llama有効でも翻訳選択は常に `TranslationPriority` 順で行われる。
- 既定優先度は `LlamaCpp > Gemini > DeepL` となる。
- UI上で CTranslate2 は操作不可（非表示）となり、翻訳実行経路でも使用されない。
- 旧設定に残る CTranslate2/GoogleWeb は優先度正規化で除外される。

### Risk & Mitigation
- Risk: 既存の「Llama有効なら必ずLlama」期待と挙動差が出る。
- Mitigation: 優先度順を明示するログを追加し、選択順が追跡可能な状態にした。
- Risk: 旧設定に CTranslate2 が残ると意図しない挙動が起こる可能性。
- Mitigation: 読み込み時に `EnableCTranslate2=false` へ補正し、起動判定も常時無効化した。

### Tests / Verification
- `dotnet build Hotkey-Translator.sln` 実行成功（0 errors / 0 warnings）。
**2026-02-09 13:51 (Asia/Taipei) — Gemini Strict計画書の現状仕様整合修正**

### Summary
- Doc/Gemini_Force_Strict_UI_Hotkey_Plan.md の失敗時挙動記述を現状仕様（原文フォールバック）に揃えた。

### Context / Goal
- 直前反映後に残っていた文言差分（概要・DoD）を、現状実装方針と一致させる。
- Strict時の「プロバイダフォールバックなし」と「最終表示の原文フォールバック」を同時に明確化する。

### Changes
- 概要の失敗時説明を、空返却後に原文表示へフォールバックする記述へ修正。
- DoDのStrict失敗条件を、追加プロバイダ不使用 + 原文フォールバック明記へ修正。

### Files Touched
- Doc/Gemini_Force_Strict_UI_Hotkey_Plan.md — 失敗時挙動に関する文言を現状仕様に合わせて更新。

### Behavioral Impact
- 実装計画の受け取り方が統一され、Strict失敗時の期待挙動（原文表示）が明確になった。

### Risk & Mitigation
- Risk: 計画書と実装の解釈ズレにより、Strict失敗時挙動の認識差が残る。
- Mitigation: 概要とDoDの両方に同一方針を明記し、レビュー時の判断基準を固定化した。

### Tests / Verification
- 未実施（ドキュメント修正のみのため）。
**2026-02-09 13:55 (Asia/Taipei) — Gemini Strict計画書のHotkey専用化**

### Summary
- Gemini Force Strict 計画を UI併用案から Hotkey専用案へ変更した。

### Context / Goal
- 運用方針を「UI側を消してホットキーのみ」に一本化する。
- 計画書の構成要素（概要/設計/DoD）を新方針に整合させる。

### Changes
- タイトルと概要を「Hotkey専用」に更新。
- UI常時トグル前提（設定項目・フロー・DoD）を削除。
- 実行判定を effectiveForce = options.ForceGeminiStrict に更新。
- 影響範囲とリスク緩和文言をHotkey専用運用へ調整。

### Files Touched
- Doc/Gemini_Force_Strict_UI_Hotkey_Plan.md — UI併用記述を削除し、Hotkey専用設計へ全面更新。

### Behavioral Impact
- 計画上、Gemini Strictは Shift+F10 ワンショット実行時のみ有効化される前提になった。

### Risk & Mitigation
- Risk: UI常時強制を期待する読み手との認識差が生じる。
- Mitigation: 非ゴールに「UIトグル常時固定は提供しない」を明記し、DoDもHotkey前提に統一した。

### Tests / Verification
- 未実施（ドキュメント修正のみのため）。
**2026-02-09 14:10 (Asia/Taipei) — Gemini Strict Hotkey専用実装**

### Summary
- Gemini Strict を Shift+F10 のワンショットHotkeyとして実装し、Strict時はGemini単独実行で追加フォールバックしない経路を追加した。

### Context / Goal
- Doc/Gemini_Force_Strict_UI_Hotkey_Plan.md の Hotkey専用方針に沿って、UI常時トグルなしで Gemini 固定実行を可能にする。
- 既存の通常実行（優先度フォールバック）には影響を与えない形で、ワンショット強制実行のみを追加する。

### Changes
- Hotkeys UIに Force Gemini (strict) 行を追加し、設定の読み書き/登録/更新ログまで接続。
- AppSettings に HotkeyForceGeminiStrictKey/Modifiers を追加（既定 F10 + Shift）。
- MainWindow に OnForceGeminiStrictHotkeyPressed を追加し、ForceRunOptions へ ForceGeminiStrict=true を付与して実行。
- ForceRunOptions に ForceGeminiStrict フラグを追加し、Pipeline から翻訳サービスへ伝播。
- TranslationFallbackService に Strict分岐を追加し、Gemini単独実行・失敗時空返却（追加プロバイダへフォールバックしない）を実装。

### Files Touched
- MainWindow.xaml — Hotkeysパネルへ Force Gemini (strict) のKey/Modifier入力行を追加。
- MainWindow.xaml.cs — 新Hotkeyのイベント/登録/設定反映/既定補完/起動ログ/Dispose処理を追加。
- Models/AppSettings.cs — Gemini Strict専用Hotkey設定プロパティを追加。
- Services/PipelineOrchestrator.cs — ForceRunOptions 拡張と翻訳実行オプション伝播を実装。
- Services/TranslationFallbackService.cs — ForceGeminiStrict 時のGemini単独実行分岐を追加。

### Behavioral Impact
- Shift+F10 実行時のみ Gemini Strict が有効になり、Gemini失敗時は追加フォールバックせず空返却となる。
- 非Strict実行（通常F8/F10）は従来どおり TranslationPriority 順のフォールバック挙動を維持する。

### Risk & Mitigation
- Risk: Shift+F10 が他ホットキー設定と衝突すると登録に失敗する可能性。
- Mitigation: 既存の重複検知と個別ロールバック機構 (TryApplyHotkeyBinding) を利用し、他ホットキーへの波及を防止。
- Risk: Strict時にGeminiが無効/障害の場合、翻訳結果が空になりやすい。
- Mitigation: Strict専用ログ（skipped/active/empty/failed）を追加し、原因追跡を容易化。

### Tests / Verification
- dotnet build Hotkey-Translator.sln 実行成功（0 errors / 0 warnings）。
**2026-02-09 14:57 (Asia/Taipei) — Overlay安定化設定の名称・文言整理**

### Summary
- OCR設定の旧名称 `Shrink overlay for 1–2 lines` を、実際の挙動に合わせて `Stabilize overlay font size` へ更新した。

### Context / Goal
- 現在の実装は「1-2行限定の縮小」ではなく、フォントサイズの安定化（量子化/ヒステリシス）を制御している。
- UI文言・設定名・参照コードを実態と一致させ、誤解を減らす。

### Changes
- 設定モデルの主キー名を `EnableOverlayFontStabilization` に変更。
- 旧キー `EnableOverlayShortLineShrink` は読み込み互換のために受け口を残した。
- Settings UIのチェックボックス名と表示文言を `Stabilize overlay font size` に変更。
- MainWindow と OverlayWindow の参照を新設定名へ更新。

### Files Touched
- `Models/AppSettings.cs` — `EnableOverlayFontStabilization` 追加、旧キーの互換デシリアライズ受け口を追加。
- `MainWindow.xaml` — OCR設定チェックボックス文言/名前を新名称へ変更。
- `MainWindow.xaml.cs` — 設定読込/保存で新設定名を参照するよう更新。
- `UI/OverlayWindow.xaml.cs` — スタイル適用時に新設定名を参照するよう更新。

### Behavioral Impact
- 機能挙動は維持され、表示文言と設定名だけが実態に一致する形へ整理された。
- 既存の `settings.json` にある旧キー値は読み込み時に新設定へ移行される。

### Risk & Mitigation
- Risk: 設定名変更により既存設定の読み込み互換が崩れる可能性。
- Mitigation: 旧キー専用の互換プロパティを用意し、既存値を新キーへマッピングした。

### Tests / Verification
- `dotnet build Hotkey-Translator.sln` は実行中プロセスによるファイルロックで失敗（`Hotkey-Translator.exe/.dll` が使用中）。
- `dotnet build Hotkey-Translator.sln -p:OutDir="g:\Local App\Hotkey-Translator\obj\verify-build\"` は成功（0 errors / 0 warnings）。
**2026-02-09 15:12 (Asia/Taipei) — SceneChange AutoTranslate計画の排他・間隔共用方針反映**

### Summary
- `Doc/SceneChange_AutoTranslate_Plan.md` に「Auto-hide/Auto-translate排他」と「`SceneChangeWatchIntervalMs` のクールダウン共用」方針を反映した。

### Context / Goal
- AutoTranslateの追加にあたり、設定項目を増やさず既存監視設定を流用したい。
- Auto-hide と Auto-translate の競合を避け、同時ON不可の明確仕様にしたい。

### Changes
- `SceneChangeAutoTranslateCooldownMs` の案を削除し、`SceneChangeWatchIntervalMs` 共用へ変更。
- Auto-hide/Auto-translate の排他（同時ON不可）と自動補正ルールを追加。
- 実装手順・リスク・DoDを排他仕様と間隔共用仕様に合わせて更新。

### Files Touched
- `Doc/SceneChange_AutoTranslate_Plan.md` — 設定設計、判定ルール、手順、リスク、DoDを方針に合わせて更新。

### Behavioral Impact
- 計画上、シーン変化モードは Auto-hide か Auto-translate のどちらか一方のみ有効となる。
- 再発火抑止は新規設定ではなく `SceneChangeWatchIntervalMs` を使用する前提になった。

### Risk & Mitigation
- Risk: 既存の同時ON想定と計画仕様が不一致になる可能性。
- Mitigation: 排他補正（保存時/起動時）の明記とDoD追加でレビュー観点を固定化した。

### Tests / Verification
- 未実施（ドキュメント更新のみのため）。
**2026-02-09 18:15 (Asia/Taipei) — SceneChange AutoTranslate計画にOverlay非依存監視方針を反映**

### Summary
- `Doc/SceneChange_AutoTranslate_Plan.md` に「Auto-translate は Overlay 表示状態に依存しない（非表示時も監視継続）」方針を反映した。

### Context / Goal
- Auto-translate の有効範囲を明確化し、実装時の解釈ぶれを防ぐ。
- 既存 watcher 流用時に `overlayVisible` 条件へ引きずられない仕様をドキュメントで固定する。

### Changes
- 前提・仮定に「Auto-translate は Overlay 非表示時も監視継続」を追加。
- 実行判定ルールに「Auto-translate 有効時は `overlayVisible` で監視停止しない」を追加。
- 実装手順に watcher 起動条件の拡張（Auto-translate 時は `overlayVisible` 非依存）を追記。
- リスク/DoDに Overlay 非表示時の監視継続に関する項目を追加。

### Files Touched
- `Doc/SceneChange_AutoTranslate_Plan.md` — 有効範囲・判定ルール・実装手順・リスク・DoDをOverlay非依存仕様へ更新。

### Behavioral Impact
- 計画上、Auto-translate は Overlay の表示状態に関係なくシーン変化監視と起動判定を行う前提になった。

### Risk & Mitigation
- Risk: Overlay 非表示中にも自動翻訳が走る挙動を利用者が想定しない可能性。
- Mitigation: UI説明とログで「Overlay 表示状態に依存しない」ことを明示する。

### Tests / Verification
- 未実施（ドキュメント更新のみのため）。
**2026-02-09 18:23 (Asia/Taipei) — SceneChange AutoTranslate実装（既存watcher流用）**

### Summary
- `Enable auto-hide on scene change` の既存 watcher を流用し、排他モードの `Enable auto-translate on scene change` を実装した。

### Context / Goal
- シーン変化検知パイプラインを再利用し、検知後アクションを Auto-hide / Auto-translate で切り替える。
- Auto-hide と Auto-translate は同時ON不可、旧設定で両ONなら Auto-hide 優先に統一する。
- Auto-translate は Overlay 表示状態に依存せず、非表示時も監視継続する。

### Changes
- `AppSettings` に `EnableSceneChangeAutoTranslate` を追加。
- Scene Change UIを `Scene Change Automation` へ更新し、`Enable auto-translate on scene change` を追加。
- `OnSettingChanged` / `SaveSettingsAsync` に排他制御を追加（相互ON時は片方を自動OFF）。
- 起動時の旧設定補正として `NormalizeSceneChangeModeSettings` を追加（両ON時は Auto-hide 優先）。
- watcher 起動条件を `AutoTranslate || (AutoHide && overlayVisible)` に拡張。
- `OnAutoHideTick` で検知時アクションを分岐:
  - Auto-hide: 従来どおり Overlay 非表示
  - Auto-translate: UIスレッドへ起動要求をキュー投入
- Auto-translate 起動キュー処理を追加:
  - 実行中 (`_runInProgress`) はログして破棄
  - `SceneChangeWatchIntervalMs` 共用クールダウンで連打抑止
  - 実行は `RunOnceAsync(ForceRunOptions.None)` 経路に統一

### Files Touched
- `Models/AppSettings.cs` — `EnableSceneChangeAutoTranslate` 設定を追加。
- `MainWindow.xaml` — Scene Change設定UIに Auto-translate トグルと説明文を追加。
- `MainWindow.xaml.cs` — 排他補正、watcher起動条件拡張、Auto-translate 起動キュー、監視分岐を実装。

### Behavioral Impact
- Scene Change モードは Auto-hide / Auto-translate の排他運用になる。
- Auto-translate 有効時は Overlay 非表示でも監視と自動翻訳判定が継続される。
- 検知時の自動翻訳起動は UI スレッドで実行され、実行中は安全に破棄される。

### Risk & Mitigation
- Risk: Auto-translate 非表示監視により、意図しないタイミングで自動翻訳が走る可能性。
- Mitigation: UI説明文と詳細ログ（triggered / cooldown skip / running skip）で挙動を明示。
- Risk: 旧設定で両ONのまま残っている場合に挙動が不定になる可能性。
- Mitigation: 起動時に自動正規化し、Auto-hide 優先で固定化した。

### Tests / Verification
- `dotnet build Hotkey-Translator.sln -p:OutDir="g:\Local App\Hotkey-Translator\obj\verify-build-autotranslate\"` 実行成功（0 errors / 0 warnings）。
**2026-02-09 18:43 (Asia/Taipei) — 自動翻訳トグルホットキー（F5）を追加**

### Summary
- シーン変化Auto-translateをホットキーでON/OFFできるようにし、デフォルト割り当てを `F5` に追加した。

### Context / Goal
- UI操作なしで、運用中に自動翻訳モードを即時切替できるようにする。
- 既存のホットキー設定・保存・再登録フローに統合し、設定永続化まで一貫して動作させる。

### Changes
- `AppSettings` に `HotkeyToggleSceneAutoTranslateKey/Modifiers` を追加し、デフォルトを `F5` / `None` に設定。
- Hotkeys設定UIに `Scene auto-translate` のキー/修飾キー行を追加。
- `MainWindow` に `OnToggleSceneAutoTranslateHotkeyPressed` を追加し、押下ごとに `EnableSceneChangeAutoTranslate` をトグル。
- Auto-translate をONにする際は排他仕様に従って Auto-hide をOFF化し、UI表示・watcher状態・設定保存を即時反映。
- ホットキー正規化、UI反映、保存、再登録、ログ出力、`HotkeyConfig` の定義に新ホットキーを組み込み。

### Files Touched
- `Models/AppSettings.cs` — Scene auto-translateトグル用ホットキー設定項目を追加。
- `MainWindow.xaml` — Hotkeysセクションへ `Scene auto-translate` の入力行を追加。
- `MainWindow.xaml.cs` — ホットキー管理フィールド/ハンドラ/設定反映/登録処理/既定値/ログ文言を更新。

### Behavioral Impact
- デフォルト `F5` でシーン変化Auto-translateをON/OFF可能。
- `F5` でONにした場合、排他制御により Auto-hide は自動でOFFになる。
- 切替結果は即時に監視パイプラインへ反映され、設定ファイルへ永続化される。

### Risk & Mitigation
- Risk: ユーザーが `F5` を別用途に割り当てると重複登録で片方が有効化されない可能性。
- Mitigation: 既存の重複検知・ロールバック処理を流用し、失敗時はログで可視化。

### Tests / Verification
- `dotnet build Hotkey-Translator.sln -p:OutDir="g:\Local App\Hotkey-Translator\obj\verify-build-hotkey-f5\"` を実行。
- 結果: 成功（0 errors / 0 warnings）。
**2026-02-10 10:38 (Asia/Taipei) — ROI選択時の自動有効化を実装**

### Summary
- `Select ROI` 確定時に `EnableRoi` を自動でONにし、UIチェック状態も同期するようにした。

### Context / Goal
- ROIを選択したのに有効化漏れで反映されない、という運用ミスを減らす。
- ユーザー操作（F6/ボタン）後に即時有効な状態へそろえる。

### Changes
- `SelectRoiAsync` 内で ROI確定時に `settings.EnableRoi = true` を適用。
- `EnableRoiCheck` をプログラム側でONへ同期。
- 既存ログに加えて、無効→有効へ変わったときのみ `ROI enabled automatically.` を出力。

### Files Touched
- `MainWindow.xaml.cs` — ROI確定時の設定反映処理を更新し、自動有効化・UI同期・補助ログを追加。

### Behavioral Impact
- ROI選択を確定すると、ROIは自動的に有効化される。
- 以前のように ROIだけ更新されて `EnableRoi=false` のまま残る状態は発生しにくくなる。

### Risk & Mitigation
- Risk: ROIを保存だけして無効のまま保持したい運用には合わない。
- Mitigation: 必要なら従来どおり `Enable ROI` を手動でOFFに戻せる。

### Tests / Verification
- `dotnet build Hotkey-Translator.sln -p:OutDir="g:\Local App\Hotkey-Translator\obj\verify-build-roi-auto-enable\"` を実行。
- 結果: 成功（0 errors / 0 warnings）。
**2026-02-10 11:23 (Asia/Taipei) — OCR二段階LineMerge計画を方針更新**

### Summary
- `Doc/Ocr_LineMerge_TwoStage_Plan.md` を、指定の3方針（Stage Aスペース結合・新方式優先・隣接判定+強制ブレーク）に合わせて更新した。

### Context / Goal
- 同一行結合の目的と後段改行ロジックの整合を明確化する。
- 別カラム誤結合を抑えるため、Stage A の判定戦略を具体化する。

### Changes
- Stage A の結合を「同一行クラスタ + X隣接のみ」に修正。
- Stage A 出力仕様として「テキストはスペース連結」「`lineCount=1`」を明記。
- `gapX > minHeight * K` の強制ブレーク条件を判定式へ追加。
- 互換性方針を「基本は新方式優先、旧方式は明示OFF時のみ」に更新。
- DoD に `7. 本文` 形式（`7.\n本文` 回避）の確認項目を追加。

### Files Touched
- `Doc/Ocr_LineMerge_TwoStage_Plan.md` — Stage A/B仕様、判定式、実装手順、互換性方針、DoDを更新。

### Behavioral Impact
- ドキュメント上の実装方針が、現行パイプラインとの整合性を保った内容に明確化された。

### Risk & Mitigation
- Risk: Stage A 閾値の調整幅が増え、初期チューニングが必要。
- Mitigation: 強制ブレークと隣接限定を先に固定し、閾値は再現ケースで段階調整する。

### Tests / Verification
- 未実施（ドキュメント更新のみのため）。
**2026-02-10 11:44 (Asia/Taipei) — OCR LineMerge二段階化を実装**

### Summary
- `Doc/Ocr_LineMerge_TwoStage_Plan.md` に沿って、LineMerge を Stage A（同一行）→ Stage B（縦結合）の2段階方式に実装した。

### Context / Goal
- `7.` のような同一行トークン分離を改善し、後段ロジックとの整合（同一行は1行扱い）を取る。
- 誤結合を抑えるため、Stage A は全結合ではなく同一行クラスタ内の隣接判定に限定する。

### Changes
- `AppSettings` に 2段階LineMerge用設定を追加。
  - `EnableTwoStageLineMerge`（既定: true）
  - `RowMergeYCenterToleranceRatio` / `RowMergeHeightRatioMin` / `RowMergeMaxGapRatio` / `RowMergeHardBreakRatio` / `RowMergeNeighborCount`
- `OcrLineGrouper` を段階分離:
  - Stage A: `MergeSameRowTokens`（同一行クラスタ化 + X隣接のみ結合）
  - Stage B: `MergeVerticalLines`（既存の `PassesAlignmentGate` + `IsMergeableByCost` を継続）
- Stage A の出力仕様を実装:
  - テキスト連結はスペース (`" "`)。
  - `lineCount=1` 固定。
- 列分離ガードを実装:
  - `gapX > minHeight * RowMergeHardBreakRatio` なら必ず非結合。
- 互換フォールバックを実装:
  - `EnableTwoStageLineMerge=false` なら旧方式（縦結合のみ）を使用。

### Files Touched
- `Models/AppSettings.cs` — 2段階LineMerge関連の設定項目を追加。
- `Services/OcrLineGrouper.cs` — Stage A/Stage B 分離、隣接判定、強制ブレーク、フォールバックを実装。

### Behavioral Impact
- 既定では新方式（2段階）が有効となり、同一行トークンの結合精度が改善される。
- Stage Aで結合された行は1行扱いになるため、`7.\n本文` ではなく `7. 本文` になりやすい。
- 旧方式は `EnableTwoStageLineMerge=false` で再現可能。

### Risk & Mitigation
- Risk: Stage A 閾値次第で結合漏れ/誤結合が起こる可能性。
- Mitigation: 同一行候補判定に加えて隣接限定 + hard-break を導入し、過結合を抑制。

### Tests / Verification
- `dotnet build Hotkey-Translator.sln -p:OutDir="g:\Local App\Hotkey-Translator\obj\verify-build-line-merge-twostage\"` を実行。
- 結果: 成功（0 errors / 0 warnings）。
**2026-02-10 13:26 (Asia/Taipei) — PaddleOCRパディング座標戻しの最小修正**

### Summary
- `OcrService/ocr_engine.py` のパディング戻しを `x,y` 補正のみから、`left/top/right/bottom` 再計算 + 画像境界クリップへ変更した。

### Context / Goal
- パディング付き推論時に、検出ボックスが表示上大きくはみ出すケースを抑える。
- まずは最小実装として、座標復元の幾何補正だけを強化する。

### Changes
- `recognize()` で元画像サイズ（`original_width`, `original_height`）を保持。
- パディング戻し処理を `_restore_boxes_after_padding(...)` へ分離。
- 各ボックスについて `left/top/right/bottom` をパディング分だけ戻し、元画像サイズへクリップ。
- クリップ後に `width/height` を再計算し、無効ボックス（0以下）は除外。

### Files Touched
- `OcrService/ocr_engine.py` — パディング復元ロジックを再計算方式へ変更し、座標補正ヘルパーを追加。

### Behavioral Impact
- 画像端での検出ボックス過大化が起きにくくなり、オーバレイはみ出しが軽減される。
- パディングを使った見切れ救済自体は維持される。

### Risk & Mitigation
- Risk: 端境界で極小ボックスが除外され、まれに1件欠落する可能性。
- Mitigation: 無効ボックス除外は `width/height<=0` のみとし、通常ケースへの影響を最小化。

### Tests / Verification
- `python -m py_compile OcrService/ocr_engine.py` を実行。
- 結果: 成功（文法エラーなし）。
**2026-02-10 13:38 (Asia/Taipei) — PaddleOCRの検出しきい値パラメータを追加**

### Summary
- PaddleOCR v3系推奨の `text_det_thresh` と `text_det_box_thresh` をOCRエンジン初期化引数に追加した。

### Context / Goal
- 検出ボックスの低信頼ノイズを抑え、オーバレイの不要な「釣り」や過大ボックスを減らす。
- 旧 `det_db_*` ではなく v3系の推奨キー名で反映する。

### Changes
- `OcrService/ocr_engine.py` の `PaddleOCR(**kwargs)` に以下を追加:
  - `text_det_thresh = 0.35`
  - `text_det_box_thresh = 0.70`
- `OcrService/cpu_ocr_engine.py` にも同設定を追加し、GPU/CPU挙動を揃えた。

### Files Touched
- `OcrService/ocr_engine.py` — PaddleOCR初期化引数へ検出しきい値2項目を追加。
- `OcrService/cpu_ocr_engine.py` — 同様のしきい値2項目を追加。

### Behavioral Impact
- 検出の採択がやや厳しくなり、低信頼ボックスが減る方向に動作する。
- その反面、淡い/小さい文字が一部欠落する可能性がある。

### Risk & Mitigation
- Risk: しきい値引き上げにより取りこぼしが増える可能性。
- Mitigation: 値は過度に上げず中程度（0.35/0.70）で開始し、必要に応じて再調整する。

### Tests / Verification
- `python -m py_compile OcrService/ocr_engine.py OcrService/cpu_ocr_engine.py` を実行。
- 結果: 成功（文法エラーなし）。
**2026-02-10 13:56 (Asia/Taipei) — PaddleOCR座標補正を検証用に一時OFF**

### Summary
- `OcrService/ocr_engine.py` のパディング戻し補正を検証用に一時的に無効化した。

### Context / Goal
- 全体が左上へずれる症状が、`-padding` 補正の過適用かを切り分ける。
- 補正ロジックを維持したまま、ON/OFF比較を素早く行える状態にする。

### Changes
- `recognize()` 内の `_restore_boxes_after_padding(...)` 呼び出し条件を一時的に無効化。
- 変更箇所に検証目的である旨の `NOTE` コメントを追加。

### Files Touched
- `OcrService/ocr_engine.py` — unpadding適用行を一時OFF化（検証用）。

### Behavioral Impact
- PaddleOCR返却座標に対して `-padding` 補正を行わない挙動になる。
- 返却座標が既に元画像基準なら、左上ずれが改善する可能性がある。

### Risk & Mitigation
- Risk: 返却座標がpad後基準の環境では、逆に右下へずれる可能性。
- Mitigation: 検証目的の一時変更として扱い、結果確認後に恒久策（auto判定等）へ移行する。

### Tests / Verification
- `python -m py_compile OcrService/ocr_engine.py` を実行。
- 結果: 成功（文法エラーなし）。
**2026-02-10 15:38 (Asia/Taipei) — OCR縦書き対応の実装案ドキュメントを追加**

### Summary
- 書字方向判定と縦書き結合分岐を中心にした実装案を `Doc/` に新規追加した。

### Context / Goal
- 縦書きでの読み順崩れ・誤結合を減らすため、結合パイプラインを横書き/縦書きで分岐する方針を整理する。
- 初期実装は 1+2+3+4（方向判定、2系統結合、縦書き判定式、読み順明示）に限定する。

### Changes
- 新規ドキュメント `Doc/Ocr_Vertical_Writing_Merge_Plan.md` を作成。
- 以下を明記:
  - 書字方向判定（Horizontal / Vertical / Unknown + fallback）
  - 横書き/縦書き2系統結合
  - 縦書き判定式（同一列判定、Yギャップ、強制ブレーク）
  - 縦書き読み順（右→左、上→下）
  - 最小設定候補と段階導入方針

### Files Touched
- `Doc/Ocr_Vertical_Writing_Merge_Plan.md` — OCR縦書き対応の実装案を追加。

### Behavioral Impact
- 実装前の計画文書が追加され、今後の実装範囲と段階導入方針が明確化された。

### Risk & Mitigation
- Risk: 方向判定と縦書き結合の閾値調整が難航する可能性。
- Mitigation: 不確実時は横書きフォールバックを維持し、初期スコープを 1+2+3+4 に限定する。

### Tests / Verification
- 未実施（ドキュメント追加のみのため）。
**2026-02-10 15:46 (Asia/Taipei) — 縦書き実装案に判定安定化と優先順位を追記**

### Summary
- `Doc/Ocr_Vertical_Writing_Merge_Plan.md` に、推奨1〜3（クラスタ単位判定・安定化ルール・設定優先順位）を反映した。

### Context / Goal
- 混在ページでの破綻を防ぐため、全体一律判定ではなくクラスタ単位判定を明確化する。
- 実装時のぶれを防ぐため、方向判定しきい値と設定優先順位を文書で固定する。

### Changes
- データフローを「クラスタ作成 -> クラスタ単位方向判定」に更新。
- 新規内部メソッド案に `ResolveWritingModeForCluster(...)` を追加。
- 方向判定の内部定数として `minSampleCount` / `dominanceRatio` / `hysteresisMargin` を追記。
- 実装手順に判定安定化（優勢比・ヒステリシス）ステップを追加。
- 互換性セクションに設定優先順位（`EnableLineMerge` / `EnableTwoStageLineMerge` / `EnableVerticalMerge`）を明記。
- DoDに「Unknown落ち」「揺れ抑制」の確認項目を追加。

### Files Touched
- `Doc/Ocr_Vertical_Writing_Merge_Plan.md` — 方向判定戦略、安定化条件、設定優先順位、DoDを更新。

### Behavioral Impact
- ドキュメント上で実装判断基準が具体化され、混在ケースとモード揺れへの対処方針が明確化された。

### Risk & Mitigation
- Risk: 判定閾値の初期値次第で Unknown 判定が増え、縦書き分岐が効きにくくなる可能性。
- Mitigation: `Unknown -> 横書きフォールバック` を維持し、検証ケースで閾値を段階調整する。

### Tests / Verification
- 未実施（ドキュメント更新のみのため）。
**2026-02-10 15:50 (Asia/Taipei) — 縦書き計画に言語ゲート（ja/zh限定）を反映**

### Summary
- `Doc/Ocr_Vertical_Writing_Merge_Plan.md` に、縦書き自動判定を `ja` / `zh*` のみに限定する方針を追記した。

### Context / Goal
- 実運用での誤判定を減らすため、縦書き判定対象言語を絞る。
- 例外ケース対応のため、手動上書きモードを設計へ含める。

### Changes
- 前提・仮定に「`ja` / `zh*` 以外は横書き固定」を追加。
- データフローに言語ゲート手順を追加。
- 新規内部メソッド案に `ShouldEnableVerticalDetectionForLanguage(...)` を追加。
- 設定候補に `VerticalModeOverride: Auto | Horizontal | Vertical` を追加。
- 実装手順に言語ゲート実装ステップを追加。
- 設定優先順位へ `VerticalModeOverride` と `ja/zh限定Auto` の分岐を明記。
- DoDに「`ja` / `zh*` 以外は横書き固定」確認項目を追加。

### Files Touched
- `Doc/Ocr_Vertical_Writing_Merge_Plan.md` — 言語ゲート方針、優先順位、DoDを更新。

### Behavioral Impact
- ドキュメント上で縦書き判定適用範囲が明確化され、英語等での誤判定リスクを抑える方針になった。

### Risk & Mitigation
- Risk: 日本語・中国語以外で縦書きが必要なケースを取りこぼす可能性。
- Mitigation: `VerticalModeOverride` による手動上書きを設計へ含めた。

### Tests / Verification
- 未実施（ドキュメント更新のみのため）。
**2026-02-10 16:39 (Asia/Taipei) — 縦書き判定込みOCR実装案を更新**

### Summary
- Doc/Ocr_Vertical_Writing_Merge_Plan.md を、ロールバック後の現状を前提にした縦書き判定込みの実装案へ更新。

### Context / Goal
- 縦書き時にオーバレイと翻訳順が左列先頭になる問題の原因を、現行コード前提で再定義する。
- 書字方向判定（Horizontal/Vertical/Unknown）を含む実装手順をDocとして明文化する。

### Changes
- ロールバック後の現状整理（Y->X 固定整列、groupedLines の共通利用）を追記。
- 縦書き判定の導入方針（言語ゲート、Unknownフォールバック、縦書き2-stage）を具体化。
- 設定追加案、実装ステップ、リスク/緩和策、DoDを現状仕様に合わせて再構成。

### Files Touched
- Doc/Ocr_Vertical_Writing_Merge_Plan.md — 縦書き判定込みの実装案に全面更新。

### Behavioral Impact
- コード挙動の変更はなし。実装着手時の判断基準と変更範囲が明確化された。

### Risk & Mitigation
- Risk: 実装前のため、閾値の妥当性は未検証。
- Mitigation: DoDにフォールバック条件と回帰確認を明記し、実装時に検証可能な形へ整理した。

### Tests / Verification
- 未実施（ドキュメント更新のみのため）。
**2026-02-10 16:40 (Asia/Taipei) — 縦書き判定案を新規Docとして作成**

### Summary
- 縦書き判定込みOCR実装案を既存ファイル更新ではなく新規Docとして作成し、既存Docは元状態へ戻した。

### Context / Goal
- 既存計画書の上書きではなく、新規ファイルで実装案を管理したい。
- 既存ドキュメント資産を保持したまま、改訂案を別紙化する。

### Changes
- Doc/Ocr_Vertical_Writing_Merge_Plan_v2.md を新規作成（縦書き判定込みの実装案内容）。
- Doc/Ocr_Vertical_Writing_Merge_Plan.md はHEAD時点の内容へ復元。

### Files Touched
- Doc/Ocr_Vertical_Writing_Merge_Plan_v2.md — 新規作成（改訂版実装案）。
- Doc/Ocr_Vertical_Writing_Merge_Plan.md — 既存版を復元。

### Behavioral Impact
- 実装コードへの影響なし。Doc運用が「既存保持 + 新規案追加」に変更された。

### Risk & Mitigation
- Risk: どちらが最新版か混同する可能性。
- Mitigation: ファイル名に 2 を付与し、レビュー対象を明示する。

### Tests / Verification
- git status --short と git diff -- Doc/Ocr_Vertical_Writing_Merge_Plan.md Doc/Ocr_Vertical_Writing_Merge_Plan_v2.md で差分を確認。
**2026-02-10 16:47 (Asia/Taipei) — Ocr縦書き判定と縦書き2段結合を実装**

### Summary
- Doc/Ocr_Vertical_Writing_Merge_Plan_v2.md の方針に沿って、縦書き判定と縦書き用結合/並び順を OcrLineGrouper に実装した。

### Context / Goal
- WinRT縦書きOCRで、非固定ROI時の表示順と翻訳順が左列先頭になる課題があった。
- groupedLines の生成順を縦書き基準（右列→左列、列内は上→下）へ分岐可能にする。

### Changes
- AppSettings に縦書き制御用の最小設定を追加（Enable/AutoDetect/Override/ColumnOrder/GapRatio）。
- OcrLineGrouper に書字方向判定（Horizontal/Vertical/Unknown）を追加し、ja/zh* 言語ゲートで自動判定を有効化。
- 判定結果に応じて、横書き既存パスと縦書き2段結合パス（同一列トークン結合 + 列順整列）を分岐。
- 縦書き時の結合規則として、X中心差+幅比の同一列判定、Yギャップ閾値と強制ブレークを追加。

### Files Touched
- Models/AppSettings.cs — VerticalModeOverride / VerticalColumnOrder enum と縦書き関連設定を追加。
- Services/OcrLineGrouper.cs — 書字方向判定、言語ゲート、縦書き2段結合、縦書き順序整列を実装。

### Behavioral Impact
- SourceLanguage が ja / zh* で縦書き優勢と判定された場合、groupedLines が縦書き順序で出力される。
- 非固定ROIオーバレイ表示順と翻訳投入順が同じ groupedLines を使うため、縦書き順序が両方に反映される。
- 判定が不確実（Unknown）または対象外言語の場合は横書きパスへフォールバックする。

### Risk & Mitigation
- Risk: 自動判定が混在レイアウトで誤る可能性。
- Mitigation: 優勢比判定で Unknown に落とし、横書きへフォールバック。さらに VerticalModeOverride で手動固定可能。

### Tests / Verification
- dotnet build Hotkey-Translator.sln は実行中EXEロックで失敗（Hotkey-Translator.exe 使用中）。
- dotnet build Hotkey-Translator.sln -p:OutDir="g:\Local App\Hotkey-Translator\obj\verify-build-vertical-v2\" でビルド成功（0 warning / 0 error）。
**2026-02-10 17:27 (Asia/Taipei) — 固定ROI表示順をVertical固定に対応**

### Summary
- 固定ROIオーバレイの結合表示順を、VerticalModeOverride=Vertical 時に縦書き順へ切り替える処理を追加。

### Context / Goal
- 検証目的として、まず固定ROIモードで表示順のみ正しくなるかを確認したい。
- 非固定ROIや結合ロジック本体は触らず、固定ROI経路だけを最小変更する。

### Changes
- BuildOverlayItems(...) の固定ROI経路に分岐を追加。
- VerticalModeOverride=Vertical の場合、VerticalColumnOrder に従って X優先（右→左または左→右）+ Y昇順 で並べる。
- それ以外は従来どおり Y->X ソートを維持。

### Files Touched
- Services/PipelineOrchestrator.cs — 固定ROI時の表示順ソートを縦書き固定対応に変更。

### Behavioral Impact
- EnableFixedRoiOverlay=true かつ VerticalModeOverride=Vertical のとき、固定ROIの1ボックス結合表示が縦書き列順で連結される。
- 非固定ROI表示と他モードの挙動は変更なし。

### Risk & Mitigation
- Risk: VerticalModeOverride=Auto の縦書きケースでは固定ROI経路は従来順のまま。
- Mitigation: 今回は検証目的の最小変更として限定し、必要なら次段でAuto時も書字方向を反映する。

### Tests / Verification
- dotnet build Hotkey-Translator.sln -p:OutDir="g:\Local App\Hotkey-Translator\obj\verify-build-fixedroi-order\" を実行し、0 warning / 0 error を確認。
**2026-02-10 17:57 (Asia/Taipei) — ReadingUnit導入の縦書き実装案を新規作成**

### Summary
- 非固定ROI表示順修正と縦書き翻訳送信改善を目的に、ReadingUnit導入案を新規Docとして追加した。

### Context / Goal
- 固定ROIは改善したが、非固定ROIと翻訳送信単位は縦書きで課題が残っている。
- Llama単件plain経路を維持しつつ、表示順と翻訳送信を同一単位で整える設計を明文化する。

### Changes
- Doc/Ocr_Vertical_ReadingUnit_Plan.md を新規作成。
- ReadingUnitモデル、Builder、Pipeline反映、UnitIdマッピング、Llama単件/複数件方針を定義。

### Files Touched
- Doc/Ocr_Vertical_ReadingUnit_Plan.md — ReadingUnit導入を軸にした実装計画を新規追加。

### Behavioral Impact
- コード挙動の変更はなし。次実装フェーズの設計判断基準が追加された。

### Risk & Mitigation
- Risk: 計画のみで閾値の妥当性は未検証。
- Mitigation: DoDに検証観点（順序、送信粒度、Llama単件/複数件経路）を明記した。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。
**2026-02-10 18:00 (Asia/Taipei) — ReadingUnit計画に固定ROI統一方針を追記**

### Summary
- Doc/Ocr_Vertical_ReadingUnit_Plan.md に、固定ROIも内部的にReadingUnitを使う方針を追記した。

### Context / Goal
- 固定ROIだけ別経路にすると順序不一致が再発しやすいため、表示/翻訳単位を統一したい。
- 見た目の1ボックス表示は維持しつつ、内部処理はReadingUnitへ一本化する。

### Changes
- 概要に「固定ROIもReadingUnitを利用、描画のみ1ボックス集約」を追加。
- データフローに固定ROIの連結描画ステップを追加。
- 実装手順に固定ROI経路のReadingUnit統一ステップを追加。
- DoDに固定ROI順序の検証項目を追加。

### Files Touched
- Doc/Ocr_Vertical_ReadingUnit_Plan.md — 固定ROIのReadingUnit統一方針を追記。

### Behavioral Impact
- コード挙動の変更はなし。実装方針として固定/非固定ROIの整合性要件が明確化された。

### Risk & Mitigation
- Risk: 計画上の統一により実装影響範囲が拡大する可能性。
- Mitigation: Step分割（まず表示、次に翻訳）を維持し、段階的に導入する。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。
**2026-02-10 18:03 (Asia/Taipei) — ReadingUnit計画に横書き統一方針を追記**

### Summary
- Doc/Ocr_Vertical_ReadingUnit_Plan.md に、横書きもReadingUnitを共通利用する方針を反映した。

### Context / Goal
- 縦書きだけをReadingUnit化すると、横書きとの経路差分で順序不一致の再発リスクが残る。
- 固定/非固定・横/縦で同一単位を使う設計方針を明確化する。

### Changes
- 概要に「横書き/縦書きのReadingUnit共通利用」を追記。
- Goalに「横書きの表示/翻訳単位統一」を追加。
- データフローを横/縦共通のReadingUnit表示順へ修正。
- インターフェース設計に横書き構築ポリシー（1行=1単位基本）を追加。
- 実装手順に横書きReadingUnit構築ステップを追加。
- 非機能要件とDoDに横書き統一検証項目を追加。

### Files Touched
- Doc/Ocr_Vertical_ReadingUnit_Plan.md — 横書きReadingUnit統一方針を追記。

### Behavioral Impact
- コード挙動の変更はなし。実装時の単位統一ルールが明確化された。

### Risk & Mitigation
- Risk: 横書き側まで対象拡張することで実装範囲が増える。
- Mitigation: 横書きは1行=1単位を基本とし、既存見た目を維持する段階導入とする。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。
**2026-02-10 18:12 (Asia/Taipei) — ReadingUnit計画に同一行/同一列の判定式を追記**

### Summary
- Doc/Ocr_Vertical_ReadingUnit_Plan.md に、同じ行/同じ列の判定条件と連結条件を具体式で追記した。

### Context / Goal
- 「どうやって同じ行だと判定して連結するか」を実装前に明確化したい。
- 横書き/縦書きの判定基準と強制ブレーク条件をDoc上で参照可能にする。

### Changes
- インターフェース設計セクションに横書き/縦書きの判定・連結ルールを追加。
- 横書き: centerY 差、高さ比、gapX 判定、ハードブレーク条件を明記。
- 縦書き: centerX 差、幅比、オーバーラップガード、gapY 判定、ハードブレーク条件を明記。
- 実装手順に「隣接のみ判定」「連結時の連結文字規則」を追記。

### Files Touched
- Doc/Ocr_Vertical_ReadingUnit_Plan.md — 判定式と連結条件の明文化を追加。

### Behavioral Impact
- コード挙動の変更はなし。実装時の判定基準が明確化された。

### Risk & Mitigation
- Risk: しきい値の初期値が実データに合わない可能性。
- Mitigation: Docにハードブレーク条件を明記し、検証時に段階調整しやすくした。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。
**2026-02-10 18:19 (Asia/Taipei) — ReadingUnit計画へGrouper正本方針を反映**

### Summary
- Doc/Ocr_Vertical_ReadingUnit_Plan.md を、順序/結合の責務を OcrLineGrouper に一本化する方針へ更新した。

### Context / Goal
- ReadingUnit側で再判定/再連結すると、現行 OcrLineGrouper と重複・衝突のリスクがある。
- 縦書き表示順修正を安全に進めるため、責務分離を明確化する。

### Changes
- アーキテクチャに「OcrLineGrouper を順序/結合の正本」と明記。
- データフローを「groupedLines確定 -> ReadingUnitは順序保持写像」に変更。
- ReadingUnitBuilder のMUST制約として再クラスタ/再連結/再ソート禁止を追記。
- 実装手順を再構成し、閾値調整は OcrLineGrouper 側で実施する方針に変更。
- リスク/DoDに順序不一致防止（再ソート禁止）の検証項目を追加。

### Files Touched
- Doc/Ocr_Vertical_ReadingUnit_Plan.md — Grouper正本方針、責務分離、順序保持要件を追記。

### Behavioral Impact
- コード挙動の変更はなし。実装時の重複・衝突回避方針が明確化された。

### Risk & Mitigation
- Risk: 後段で再ソートや再連結を入れると順序不一致が再発する。
- Mitigation: Docに禁止事項を明記し、DoDで順序一致を必須確認にした。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。
**2026-02-10 18:30 (Asia/Taipei) — ReadingUnit計画の自己レビュー指摘を反映**

### Summary
- Doc/Ocr_Vertical_ReadingUnit_Plan.md に、送信件数比較基準と残り3項目（しきい値公開方針/固定ROI互換/性能記述）を反映した。

### Context / Goal
- 自己レビューで挙がった曖昧点を解消し、実装時の解釈ブレを減らす。
- とくに「件数比較の基準」を翻訳送信直前の実データ基準で明確化する。

### Changes
- DoDに「比較対象は最終的に翻訳へ送信する内容（送信直前件数）」を明記。
- DoDに After <= Before と、縦書き代表ケースで After < Before の確認条件を追加。
- VerticalGapRatio は設定公開、他の縦書きしきい値は内部定数維持の方針を追記。
- 固定ROI横書き互換（OcrLineGrouper の Y->X 前提維持）を実装手順へ追記。
- 性能記述を「ReadingUnitBuilderは写像中心で O(n)」へ更新し、近傍判定はGrouper側責務に整理。

### Files Touched
- Doc/Ocr_Vertical_ReadingUnit_Plan.md — 自己レビュー指摘反映（DoD/設定方針/互換要件/性能記述）。

### Behavioral Impact
- コード挙動の変更はなし。実装時の評価基準と責務分離が明確になった。

### Risk & Mitigation
- Risk: 内部定数のままでは調整に再ビルドが必要。
- Mitigation: 検証結果を見て必要時に設定公開へ段階移行する方針を明記。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。
**2026-02-10 18:42 (Asia/Taipei) — ReadingUnit導入による縦書き表示順/翻訳投入の統一実装**

### Summary
- `Doc/Ocr_Vertical_ReadingUnit_Plan.md` 方針に合わせて、表示と翻訳の単位を `ReadingUnit` 基準へ移行した。

### Context / Goal
- 非固定ROIで縦書き時の表示順と翻訳送信順がズレる問題を解消したい。
- `groupedLines` 直結依存を外し、同一データ単位で表示/翻訳を扱えるようにする。

### Changes
- `ReadingUnit` モデルと `ReadingUnitBuilder` を新規追加し、`groupedLines` から順序保持で `ReadingUnit` を生成。
- `PipelineOrchestrator` を `ReadingUnit` 基準に変更し、翻訳結果保持を `Dictionary<int, string>`（`UnitId` キー）へ移行。
- OCR差分結果を `ReadingUnit` 単位へマッピングする `ResolveChangedUnitIds` を追加。
- オーバーレイ生成（固定ROI/非固定ROI）を `ReadingUnit` 入力へ統一し、後段の再ソートを行わない構成に変更。
- 縦書き同一列候補の誤結合を抑えるため、`OcrLineGrouper` に最小水平オーバーラップ判定を追加。
- `VerticalModeOverride` 既定値を `Auto` に戻し、通常運用の自動判別前提へ整合。

### Files Touched
- `Models/ReadingUnit.cs` — `ReadingUnit` レコードを新規追加。
- `Services/ReadingUnitBuilder.cs` — `groupedLines` を順序保持で `ReadingUnit` 化するビルダーを新規追加。
- `Services/PipelineOrchestrator.cs` — 表示/翻訳パイプラインを `ReadingUnit` 基準へ置換、翻訳辞書キーを `UnitId` 化。
- `Services/OcrLineGrouper.cs` — 縦書き同一列候補に最小水平オーバーラップのガードを追加。
- `Models/AppSettings.cs` — `VerticalModeOverride` 既定値を `Auto` に変更。

### Behavioral Impact
- 非固定ROI/固定ROIとも、オーバーレイ表示と翻訳反映が同じ `ReadingUnit` 順序に揃う。
- 同一原文が複数ユニットで出ても、`UnitId` ベースで表示反映されるためマッピング崩れが起きにくくなる。
- `VerticalModeOverride` の既定動作は `Auto`（自動判別）になる。

### Risk & Mitigation
- Risk: `ReadingUnitBuilder` は現状1:1写像のため、細切れOCRを完全に減らす効果は `OcrLineGrouper` 側品質に依存する。
- Mitigation: 判定・結合ロジック責務を `OcrLineGrouper` に集約し、必要なしきい値調整を同箇所で実施できる構成を維持。

### Tests / Verification
- `dotnet build Hotkey-Translator.sln -p:OutDir="g:\Local App\Hotkey-Translator\obj\verify-build-readingunit-2\"` を実行し、0 warning / 0 error を確認。

**2026-02-10 19:07 (Asia/Taipei) — 翻訳送信ペイロード可視化ログの追加**

### Summary
- 縦書き時の翻訳送信テキスト検証のため、翻訳実行直前の送信内容プレビューをログ出力するようにした。

### Context / Goal
- 送信テキストに不要なスペースが混入しているかを実データで確認したい。
- 目視しにくい空白・改行を可視化して原因切り分けを進める。

### Changes
- `PipelineOrchestrator.ResolveTranslationsAsync` の翻訳呼び出し直前に、送信予定 `pending` の詳細ログを追加。
- ログには `UnitId`、文字長、先頭/末尾スペース数、連続スペース最大長を出力。
- プレビュー文字列は `\r` `\n` `\t` をエスケープし、半角スペースを `<sp>` へ置換して可視化。
- 過大ログを避けるため、最大件数（10件）・最大文字数（180文字）で打ち切り。

### Files Touched
- `Services/PipelineOrchestrator.cs` — 翻訳送信ペイロード可視化ログと補助メソッドを追加。

### Behavioral Impact
- 翻訳結果自体のロジックは不変で、ログ出力のみ増える。
- ログ有効時に翻訳送信テキストの空白/改行状態を追跡できる。

### Risk & Mitigation
- Risk: OCR原文がログに多く出力され、ログ量が増える。
- Mitigation: プレビュー件数と文字数を上限で制限し、詳細は必要最小限に抑える。

### Tests / Verification
- `dotnet build Hotkey-Translator.sln -p:OutDir="g:\Local App\Hotkey-Translator\obj\verify-build-translation-payload-log\"` を実行し、0 warning / 0 error を確認。

**2026-02-10 19:59 (Asia/Taipei) — 書字方向の双方向試行スコア選択プランを新規作成**

### Summary
- `Auto + ja/zh` 限定で Horizontal/Vertical の両結果を採点して選択する実装案を `Doc/` に新規作成した。

### Context / Goal
- 現状課題の「縦書き/横書き判定精度」を改善するため、単一判定ではなく結果品質ベースで選択する方針を整理する。
- 性能影響を抑えるため、対象条件を `VerticalModeOverride=Auto` かつ `ja/zh` に限定する。

### Changes
- 双方向試行（Horizontal/Vertical両実行）とスコア選択の設計を記載。
- 採点要素として 1文字ユニット比率 / 不自然空白率 / 矩形ばらつき を明記。
- 実装手順、ログ要件、リスクと緩和策、DoD を明記。

### Files Touched
- `Doc/Ocr_WritingMode_BidirectionalScoring_Plan.md` — 双方向スコア選択方式の新規実装案を追加。

### Behavioral Impact
- コード挙動の変更はなし（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: しきい値・重み設計が不適切だと誤判定が残る。
- Mitigation: 判定ログ（scoreH/scoreV/selected）を必須化し、段階調整可能な設計にした。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。

**2026-02-10 20:01 (Asia/Taipei) — 双方向判定プランから旧単純判定を除外**

### Summary
- `Doc/Ocr_WritingMode_BidirectionalScoring_Plan.md` を更新し、近傍 dx/dy の旧単純判定を主判定・補助判定の両方から除外する方針へ修正した。

### Context / Goal
- 旧単純判定は精度面でノイズになりやすく、双方向スコア選択の一貫性を崩す。
- 判定根拠を「最終生成物の品質スコア」に一本化する。

### Changes
- 概要で「旧単純判定を補助用途へ降格」記述を削除し、「完全除外」へ変更。
- データフローのタイブレーク記述を旧判定依存から、決定的ルール（言語優先・ヒステリシス）へ変更。
- 実装手順の Step 4 を「旧判定を使わないタイブレーク」へ変更。
- リスク緩和の同点対策を、旧判定参照からヒステリシス方式へ変更。
- DoD に「旧単純判定を主/補助とも使用しない」チェック項目を追加。

### Files Touched
- `Doc/Ocr_WritingMode_BidirectionalScoring_Plan.md` — 旧単純判定除外方針とタイブレーク方針を更新。

### Behavioral Impact
- コード挙動の変更はなし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: 同点ケースのタイブレークが新ルール設計に依存する。
- Mitigation: ヒステリシスとスコア差分閾値を明記し、判定ログで調整可能にする。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。

**2026-02-10 20:06 (Asia/Taipei) — 双方向判定プランに文字ブロック縦横比スコアを追加**

### Summary
- `Doc/Ocr_WritingMode_BidirectionalScoring_Plan.md` に、OCR文字ブロックの縦横比を使う `BlockAspectScore` を採点要素として追加した。

### Context / Goal
- 書字方向判定の安定性を上げるため、幾何特徴（矩形の縦長/横長）をスコアに取り込みたい。
- 単一指標依存を避けつつ、最終品質スコアの識別力を高める。

### Changes
- 採点要素に `BlockAspectScore`（`Rect.Height/Rect.Width`）を追加。
- 縦仮説は縦長比率、横仮説は横長比率で加点する方針を追記。
- 小面積ノイズ枠への対策（重み減衰/除外）を追記。
- 総合スコア式を 3指標から 4指標へ更新し、初期重み例を更新。
- 実装手順に `BlockAspectScore` 実装ステップを追加。
- リスク/緩和策とDoDを4指標前提へ更新。

### Files Touched
- `Doc/Ocr_WritingMode_BidirectionalScoring_Plan.md` — `BlockAspectScore` と関連方針を追記。

### Behavioral Impact
- コード挙動の変更はなし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: 記号・ルビ・小サイズ文字が縦横比指標を汚染する。
- Mitigation: 面積下限フィルタや重み減衰の適用を計画に明記した。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。

**2026-02-10 20:15 (Asia/Taipei) — 双方向判定プランを旧判定完全撤去方針へ更新**

### Summary
- `Doc/Ocr_WritingMode_BidirectionalScoring_Plan.md` を更新し、旧単純判定の完全撤去と言語優先タイブレーク廃止を反映した。

### Context / Goal
- 旧単純判定を残すとノイズとして誤判定原因になるため、主/補助ともに除外したい。
- タイブレークでの言語優先を外し、再現性の高い決定ロジックに統一したい。

### Changes
- 旧単純判定（近傍 dx/dy）を主判定・補助判定の両方から除外する方針へ明確化。
- `Auto + 非ja/zh` の挙動を Horizontal 固定へ明文化（旧判定不使用）。
- タイブレークを言語優先からヒステリシスのみ（差分閾値 + 前回モード維持）へ変更。
- 4指標スコアの実装定義を追加（0..1正規化、欠損時0.5中立、重み再配分なし）。
- `BlockAspectScore` の面積フィルタ初期値（`rect.Area < max(16, medianArea*0.15)` 除外）を追加。
- DoDに精度検証条件（縦20/横20ケース）と性能条件（`groupMs` 増分中央値 +10ms以内）を追加。

### Files Touched
- `Doc/Ocr_WritingMode_BidirectionalScoring_Plan.md` — 旧判定完全撤去・タイブレーク・受け入れ基準を更新。

### Behavioral Impact
- コード挙動の変更はなし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: Auto+非ja/zh を Horizontal 固定にすると一部レイアウトで縦書き検出機会が減る。
- Mitigation: 対象範囲を明確化し、必要時は別タスクで非ja/zh向け双方向化を段階導入する。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。

**2026-02-10 20:18 (Asia/Taipei) — 書字方向モードのUI選択方針をプランへ反映**

### Summary
- `Doc/Ocr_WritingMode_BidirectionalScoring_Plan.md` に `Auto / Vertical / Horizontal` のUI選択方針を追加した。

### Context / Goal
- 書字方向モードをユーザーが明示的に切り替えられるようにし、運用時の調整容易性を上げたい。
- 設定値の永続化不整合を防ぐため、UI値とenum値の対応を明確化したい。

### Changes
- ゴールに「UIで Auto / Vertical / Horizontal を明示選択可能」を追加。
- インターフェース設計に `VerticalModeOverride` のUI公開方針を追加。
- enum値との 1:1 対応（`Auto=0`, `Horizontal=1`, `Vertical=2`）を明記。
- 不正値読み込み時の `Auto` フォールバックと警告ログ方針を追加。
- 実装手順に Settings UI 追加（Step 0）を追加。
- リスクに「UI表示名と永続値不整合」を追加し、緩和策を明記。
- 影響範囲に Settings の View/ViewModel 変更候補を追記。
- DoDに「UIで選択・保存・再読込できる」確認項目を追加。

### Files Touched
- `Doc/Ocr_WritingMode_BidirectionalScoring_Plan.md` — 書字方向モードのUI設計・DoDを追記。

### Behavioral Impact
- コード挙動の変更はなし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: UI値と永続値の不整合で意図しないモードが適用される。
- Mitigation: enum対応を固定化し、未知値は `Auto` フォールバック + 警告ログで吸収する。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。

**2026-02-10 22:16 (Asia/Taipei) — Geminiターゲット言語名マッピング実装（主要5言語）**

### Summary
- Geminiプロンプトのターゲット言語を主要5言語で人間可読な言語名へ変換する実装を追加しました。

### Context / Goal
- `ja` / `zh-TW` のような言語ID指定より、Geminiが解釈しやすい言語名指定へ寄せたい。
- 対象は指定の主要言語（英語、日本語、繁体中国語、簡体中国語、ロシア語）に限定したい。

### Changes
- `Services/GeminiClient.cs` の `BuildPrompt` で `settings.TargetLanguage` を直接使う方式をやめ、言語名解決メソッド経由に変更。
- 主要5言語向けのマッピングを追加（`en`/`ja`/`ru`/`zh-Hant系`/`zh-Hans系`）。
- 対象外のコードは既存値フォールバックとし、未知言語で挙動を壊さないようにした。

### Files Touched
- `Services/GeminiClient.cs` — Geminiプロンプトのターゲット言語名解決ロジックを追加。

### Behavioral Impact
- Geminiへの指示文で、ターゲット言語が言語IDではなく言語名（例: `Japanese`, `Traditional Chinese`）として送信される。
- 指定の主要5言語は安定した表記に正規化される。

### Risk & Mitigation
- Risk: 主要5言語以外の言語コードはそのまま文字列出力され、期待どおりの表記でない可能性。
- Mitigation: 対象外はフォールバックで互換維持し、必要時にマッピング対象を追加拡張できる構造にした。

### Tests / Verification
- `dotnet build Hotkey-Translator.sln` 実行成功（0 errors / 0 warnings）。
**2026-02-11 01:24 (Asia/Taipei) — Geminiレスポンス全文のTXTダンプ追加**

### Summary
- Gemini APIの返却内容（raw body）をデバッグ用に全文 `.txt` 保存する処理を追加しました。

### Context / Goal
- Geminiの返却形式崩れや空応答を追跡できるように、実際に返ってきた本文を後から確認したい。
- 成功/失敗レスポンスを含めて、通信結果を再現可能にしたい。

### Changes
- `Services/GeminiClient.cs` にレスポンス本文の保存処理 `WriteGeminiRawResponseAsync` を追加。
- Gemini HTTP応答受信後、ステータス判定前に raw body を UTF-8 で保存するフローを追加。
- 保存先を `%AppData%\Hotkey-Translator\debug\gemini` とし、時刻ベースファイル名で衝突を回避。
- 保存失敗時は翻訳処理を止めないよう、例外を握りつぶしてログのみ出すガードを追加。

### Files Touched
- `Services/GeminiClient.cs` — raw response のTXT保存機能を追加。

### Behavioral Impact
- Gemini呼び出しごとにデバッグTXTが生成され、返却本文を全文確認できるようになる。
- 応答が失敗ステータスでも本文が保存されるため、障害解析がしやすくなる。

### Risk & Mitigation
- Risk: デバッグファイルが増加し、ディスク使用量が増える。
- Mitigation: 保存先を専用ディレクトリに限定し、運用で定期削除しやすい構造にした。

### Tests / Verification
- `dotnet build Hotkey-Translator.sln` 実行成功（0 errors / 0 warnings）。

**2026-02-11 02:20 (Asia/Taipei) — Gemini配列順マッピング実装**

### Summary
- Gemini翻訳を配列順ベースに切り替え、GeminiClient 内で 	exts[i] -> translations[i] 再マップして辞書契約を維持しました。

### Context / Goal
- source_text 一致依存を外し、Gemini翻訳成功時の取りこぼしを減らしたい。
- 既存の IReadOnlyDictionary<string,string> 契約を維持して他プロバイダへの影響を避けたい。

### Changes
- Services/GeminiClient.cs の esponseSchema を {"translations":["..."]} へ最小化。
- パース処理を配列ベースへ変更し、TryParseTranslations で順序保持リストを取得。
- RemapByIndex を追加し、min(texts.Count, translations.Count) の先頭N部分適用で辞書に再マップ。
- 空要素は辞書へ入れず原文フォールバックに倒す仕様を実装。
- raw dump を失敗時/短件数時のみ保存する条件付きに変更し、保存理由（eason）をメタ情報へ追加。
- Geminiログを status, latency, count(in/out) 中心の最小ログへ整理。

### Files Touched
- Services/GeminiClient.cs — スキーマ短文化、配列パース、index再マップ、条件付きraw dump、ログ整理を実装。

### Behavioral Impact
- Gemini応答が source_text を返さなくても、配列順で翻訳結果を適用できる。
- 件数不足時は先頭Nだけ反映し、残りは原文維持となる。
- 通常成功時のraw dump生成が減り、失敗調査時のみ詳細本文を保存する。

### Risk & Mitigation
- Risk: 同一原文が複数ある場合、辞書契約上は後勝ちで上書きされる。
- Mitigation: RemapByIndex に WHYコメントを追加し、仕様として明示した。
- Risk: 件数不足時は後半が未翻訳のまま残る。
- Mitigation: 先頭N部分適用を明示し、count(in/out) ログと count_mismatch_short dump で追跡できるようにした。

### Tests / Verification
- dotnet build Hotkey-Translator.sln 実行成功（0 errors）。
- 実行中プロセスが Hotkey-Translator.exe をロックしていたため、apphostコピーでMSB3026警告は発生（既知の実行中ビルド警告）。

**2026-02-11 03:43 (Asia/Taipei) — GeminiプロンプトJSONの非ASCIIエスケープ無効化**

### Summary
- BuildPromptの入力JSON生成を、非ASCII文字を`\uXXXX`へエスケープしない設定に変更しました。

### Context / Goal
- Gemini向けプロンプトでUnicodeエスケープを減らし、多言語文字列をそのまま渡したい。
- 変更範囲を最小化し、既存フローやスキーマ制約は維持したい。

### Changes
- `Services/GeminiClient.cs` に `PromptJsonOptions` を追加し、`JavaScriptEncoder.UnsafeRelaxedJsonEscaping` を設定。
- `BuildPrompt` の `JsonSerializer.Serialize(texts)` を `JsonSerializer.Serialize(texts, PromptJsonOptions)` へ変更。
- 既存の翻訳処理、レスポンスパース、フォールバックロジックは未変更。

### Files Touched
- `Services/GeminiClient.cs` — BuildPrompt用JSONシリアライズ設定を非ASCII非エスケープに変更。

### Behavioral Impact
- Geminiプロンプト中の入力配列で、日本語・繁体字などがUnicodeエスケープではなくUTF-8文字として渡される。
- それ以外の通信仕様・戻り値契約には影響しない。

### Risk & Mitigation
- Risk: `UnsafeRelaxedJsonEscaping` により、プロンプトJSONの可読文字が増える。
- Mitigation: 対象はプロンプト本文のみで、外部APIのJSONボディ構造・スキーマは既存どおり維持する。

### Tests / Verification
- `dotnet build Hotkey-Translator.sln` 実行成功（0 errors / 0 warnings）。

**2026-02-11 04:18 (Asia/Taipei) — PaddleOCR向け枠結合パラメータ最適化**

### Summary
- WinRT寄りだった行結合設定を、PaddleOCRの座標揺れ・大きめ矩形前提へ調整しました。

### Context / Goal
- 現行の `settings.json` はWinRT向けに寄っており、PaddleOCRで枠分断が起きやすい。
- 過剰結合を抑えつつ、同一行/近接行の取りこぼしを減らす設定へ寄せたい。

### Changes
- `%AppData%\Hotkey-Translator\settings.json` の `OcrEngine` を `1`（Paddle）へ変更。
- 2段階結合関連の閾値をPaddle向けに中庸調整:
  - `MergeOverlapRatioThreshold: 0.16`
  - `MergeVerticalWeight: 0.4`
  - `MergeThresholdRatio: 1.05`
  - `MergeNeighborCount: 14`
  - `RowMergeYCenterToleranceRatio: 0.55`
  - `RowMergeHeightRatioMin: 0.45`
  - `RowMergeMaxGapRatio: 1.9`
  - `RowMergeHardBreakRatio: 2.4`
  - `RowMergeNeighborCount: 28`

### Files Touched
- `%AppData%\Hotkey-Translator\settings.json` — PaddleOCR前提の行結合閾値へ更新。
- `.agent/changes.md` — 本作業ログを追記。

### Behavioral Impact
- PaddleOCRで行内トークン結合と段落結合が通りやすくなり、分断されるケースを減らす方向の挙動になる。
- 一方で近接テキストが多い画面では、誤結合リスクがわずかに増える可能性がある。

### Risk & Mitigation
- Risk: 許容幅の拡大により、UI密集箇所で別文が結合される場合がある。
- Mitigation: まず上記中庸値で運用し、誤結合が目立つ場合は `RowMergeMaxGapRatio` と `RowMergeYCenterToleranceRatio` を小さく戻して微調整する。

### Tests / Verification
- `%AppData%\Hotkey-Translator\settings.json` の該当キー更新を再読込で確認。
- アプリ実画面での目視検証は未実施（設定反映のみ実施）。
**2026-02-11 11:04 (Asia/Taipei) — 書字方向スコア選択実装とUIモード追加**

### Summary
- `Doc/Ocr_WritingMode_BidirectionalScoring_Plan.md` 方針を実装し、書字方向の旧単純判定を廃止して双方向スコア選択へ移行、あわせて UI に `Auto/Vertical/Horizontal` モード選択を追加した。

### Context / Goal
- 縦書き/横書きの誤判定を減らし、判定根拠を幾何スコアベースへ統一したい。
- 旧設定との互換を保ったまま、UI から明示的にモード制御できるようにしたい。

### Changes
- `OcrLineGrouper` を改修し、`VerticalModeOverride=Auto` かつ `ja/zh` のみで Horizontal/Vertical 両経路を実行してスコア比較する方式を実装。
- 旧近傍 `dx/dy` 判定ロジック（Auto 判定）を除去し、同点/僅差はヒステリシス（前回モード維持、初回 Horizontal）で処理。
- スコア要素として `SingleChar` / `AbnormalSpace` / `RectVariance` / `BlockAspect` の4指標を追加し、判定内訳ログを出力。
- Settings UI の OCR セクションへ `Writing mode` コンボを追加（`Auto` / `Vertical (forced)` / `Horizontal (forced)`）。
- `MainWindow` の設定反映・保存処理に `VerticalModeOverride` の読み書きを追加。
- 旧設定互換として、未知 enum 値は `Auto` にフォールバックし、旧トグル `EnableVerticalMerge`/`VerticalModeAutoDetect` は常時有効へ正規化。

### Files Touched
- `Services/OcrLineGrouper.cs` — 双方向スコア選択、4指標採点、ヒステリシス選択、判定ログを追加。
- `MainWindow.xaml` — OCR 詳細設定に `Writing mode` コンボ（Auto/Vertical/Horizontal）を追加。
- `MainWindow.xaml.cs` — UI⇔設定のバインド、書字方向設定の互換正規化、`OcrLineGrouper` への logger 注入を追加。

### Behavioral Impact
- Auto モード時は `ja/zh` のみ双方向スコア選択が動作し、その他言語の Auto は Horizontal 固定になる。
- Vertical/Horizontal 強制モードは UI から明示的に指定でき、Auto 判定を経由しない。
- 旧 Auto 判定（近傍 `dx/dy`）には戻らないため、判定挙動は従来より決定論的になる。

### Risk & Mitigation
- Risk: 非 `ja/zh` の Auto が Horizontal 固定になるため、将来的な縦書き言語拡張時には不足する可能性がある。
- Mitigation: 判定ロジックを `ShouldEnableVerticalDetectionForLanguage` に集約しており、対象言語拡張はこの関数更新で対応可能。

### Tests / Verification
- `dotnet build Hotkey-Translator.sln -p:OutDir="g:\Local App\Hotkey-Translator\obj\verify-build-writingmode-impl\"` を実行し、0 warning / 0 error を確認。

**2026-02-11 12:19 (Asia/Taipei) — WinRT CJKスペース補正の実装案をDocへ追加**

### Summary
- WinRT OCR の CJK文字間スペース問題に対する段階導入型の実装案を `Doc/` に新規作成した。

### Context / Goal
- `line.Text` の CJK 文字間スペース混入により、翻訳送信とオーバーレイ可読性が低下している。
- まずは低リスクな最小修正から入り、必要時のみ拡張する実装方針を整理する。

### Changes
- `Doc/Ocr_WinRt_Cjk_Spacing_Fix_Plan.md` を新規作成。
- 推奨順として「第1段階: CJK間スペースのみ除去」→「第2段階: CJK率ゲート」→「第3段階: Words再構成」を記載。
- WinRT限定適用、Settingsトグル追加、DoD/リスク緩和を明記。

### Files Touched
- `Doc/Ocr_WinRt_Cjk_Spacing_Fix_Plan.md` — WinRT CJKスペース補正の実装案を追加。

### Behavioral Impact
- コード挙動の変更はなし（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: 実装時に英数字スペースまで削除すると副作用が出る。
- Mitigation: Doc で初期適用範囲を「CJK間空白のみ」に限定し、段階導入前提を明記した。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。

**2026-02-11 13:50 (Asia/Taipei) — WinRT CJKスペース補正計画の自己レビュー反映**

### Summary
- `Doc/Ocr_WinRt_Cjk_Spacing_Fix_Plan.md` を更新し、「line.Words未実装」前提に合わせて UI なし・line.Text補正中心の計画へ整理した。

### Context / Goal
- 直前の自己レビュー指摘（UI前提の残存、補正ルール曖昧、言語タグ依存）を解消したい。
- 現実装方針に沿った実装手順と受け入れ基準へ修正する。

### Changes
- 概要・構成から UI トグル前提を除外し、WinRT内部補正のみのスコープに修正。
- `line.Text` 補正ルールを具体化（半角/全角空白統一、CJK+数字、句読点前後、trim）。
- 適用条件に `SourceLanguage` だけでなく CJK率フォールバック（例: 0.6）を追加。
- 実装手順を現方針に合わせて再整理し、チューニング手順を明確化。
- DoDを定量化し、翻訳送信ログ（`<sp>`）で前後比較できる検証条件を追加。

### Files Touched
- `Doc/Ocr_WinRt_Cjk_Spacing_Fix_Plan.md` — 自己レビュー指摘反映（UIなし前提、適用条件、補正ルール、DoD）を更新。

### Behavioral Impact
- コード挙動の変更はなし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: UIトグルなしのため、実装後の即時切り戻し導線が弱い。
- Mitigation: 補正呼び出しを1箇所へ集約し、コード側で無効化可能な構造を計画に明記した。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。

**2026-02-11 14:00 (Asia/Taipei) — WinRT CJKスペース補正をline.Text経路で実装**

### Summary
- `WinRtOcrProvider` に line.Text ベースの CJKスペース補正を実装し、翻訳送信前の不自然空白を減らす処理を追加した。

### Context / Goal
- WinRT OCR で CJK文字が `文 字 列` のように分割され、翻訳品質と表示可読性を落としていた。
- UI追加なしで、WinRT経路だけに限定した低リスク補正を先に導入したい。

### Changes
- `RecognizeAsync` で各行テキストに対して補正判定・補正適用を追加。
- 適用判定は `SourceLanguage` (`ja/zh*`) または CJK率しきい値（0.60）で実施。
- 補正ルールを追加:
  - 半角/全角空白を統一
  - CJK-CJK 間空白を削除
  - CJK-数字間空白を削除
  - 句読点直前空白を削除
  - 英数字単語間空白は維持
  - trim で前後空白を除去
- 補正が発生したフレームのみ、件数とサンプル（`<sp>` 可視化）をログ出力するようにした。

### Files Touched
- `Services/WinRtOcrProvider.cs` — CJKスペース補正ロジック、適用条件判定、可視化ログを追加。

### Behavioral Impact
- WinRT + CJK主体行で不自然な文字間空白が減る。
- 英語単語間空白は維持される設計。
- PaddleOCR を含む他OCR経路の挙動は変更しない。

### Risk & Mitigation
- Risk: 混在文で必要な空白を誤削除する可能性。
- Mitigation: CJK関連ケースに限定して空白削除し、英数字間は維持。適用判定に言語タグとCJK率の二重条件を使う。

### Tests / Verification
- `dotnet build Hotkey-Translator.sln -p:OutDir="g:\Local App\Hotkey-Translator\obj\verify-build-winrt-cjk-fix\"` を実行し、0 warning / 0 error を確認。

**2026-02-11 14:31 (Asia/Taipei) — 縦書き列間横結合の推奨実装案をDoc化**

### Summary
- 縦書きで列と列を左右結合するための推奨案を、現行 `ReadingUnit`/翻訳仕様との整合を含めて `Doc/` に新規出力した。

### Context / Goal
- 現行は縦書きで列内結合のみで、列間が分断されるケースが残る。
- 横書きB-stageの考え方を転置して、安全に列間結合を導入する実装方針を整理する。

### Changes
- `Doc/Ocr_Vertical_ColumnMerge_Plan.md` を新規作成。
- Stage-A（列内）+ Stage-B（列間横結合）構成、判定式、ハードブレーク、設定項目案を定義。
- `OcrLineGrouper` 内完結により `ReadingUnitBuilder`/`PipelineOrchestrator` 契約を維持する方針を明記。
- DoDに `afterUnitCount <= beforeUnitCount` などの検証条件を追加。

### Files Touched
- `Doc/Ocr_Vertical_ColumnMerge_Plan.md` — 縦書き列間横結合の実装案を追加。

### Behavioral Impact
- コード挙動の変更はなし（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: 列間の誤結合で別セリフが混ざる可能性。
- Mitigation: オーバーラップゲート + コスト閾値 + ハードブレークの3段ガード、初期OFF運用を計画に明記。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。

**2026-02-11 14:41 (Asia/Taipei) — 縦書き列間横結合プランに運用方針を追記**

### Summary
- `Doc/Ocr_Vertical_ColumnMerge_Plan.md` を更新し、推奨方針（1～3）と UI 非露出方針を反映した。

### Context / Goal
- 自己レビュー指摘のうち、設定優先順位・列ユニット定義・Auto判定タイミングを明確化したい。
- 実装ブレを防ぐため、UIを触らない運用前提を計画へ固定する。

### Changes
- 適用優先順位を明記（`EnableVerticalColumnMerge=true` かつ最終Vertical時のみ適用）。
- `ColumnUnit` を「Stage-A後 `OcrLine`」で統一定義し、再生成ルールを具体化。
- Auto判定で `scoreV` を Stage-B適用後Vertical結果で算出する方針を明記。
- 実装手順を更新（UI露出なし、Auto判定反映手順を追加）。
- 非機能要件に「settings.jsonのみ管理（UI非露出）」を追記。
- DoDに ColumnUnit統一・Auto判定算出条件を追加。

### Files Touched
- `Doc/Ocr_Vertical_ColumnMerge_Plan.md` — 優先順位・列ユニット定義・Auto判定タイミング・UI非露出方針を追記。

### Behavioral Impact
- コード挙動の変更はなし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: UIがないため調整導線が弱く、設定ミス時の発見が遅れる。
- Mitigation: 初期OFF運用とログ観測（before/after列数、結合件数）を必須化して段階導入する。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。


**2026-02-11 14:49 (Asia/Taipei) — 縦書き列間横結合の実装**

### Summary
- 縦書き Stage-A 後に列間横結合（Stage-B）を追加し、EnableVerticalColumnMerge 設定で有効化できるようにした。

### Context / Goal
- 縦書きで列分断されたテキストを、過結合を抑えながら同一読み単位として統合したい。
- ReadingUnitBuilder / 翻訳送信の 1:1 契約を崩さず、OcrLineGrouper 内で完結させる。

### Changes
- AppSettings に縦書き列間横結合の設定項目を追加（初期値は安全側で OFF）。
- OcrLineGrouper.MergeVerticalLinesTwoStage に Stage-B を追加し、縦方向重なりゲート + コスト閾値 + ハードブレークで列間結合を実装。
- 結合後テキスト順を VerticalColumnOrder（右→左 / 左→右）に合わせるため、マージ時ソートに降順オプションを追加。
- ログに efore/after 件数、mergedPairs、hardBreakSkips を出力。

### Files Touched
- Models/AppSettings.cs — EnableVerticalColumnMerge と関連閾値設定を追加。
- Services/OcrLineGrouper.cs — 縦書き Stage-B 列間横結合ロジックと補助判定関数、ログを追加。

### Behavioral Impact
- EnableVerticalColumnMerge=false のとき既存挙動を維持。
- EnableVerticalColumnMerge=true かつ最終モードが縦書きのとき、Stage-A 後の列ユニットが追加統合される。

### Risk & Mitigation
- Risk: 近接した別列を誤って結合する可能性。
- Mitigation: 縦重なりゲート、ハードブレーク、コスト閾値を併用し、既定値を OFF にして段階適用可能にした。

### Tests / Verification
- dotnet build Hotkey-Translator.sln を実行し、成功（0 warnings / 0 errors）。

**2026-02-11 15:17 (Asia/Taipei) — Overlay小枠可読性ブースト実装案を追加**

### Summary
- 小枠の可読性改善（枠拡大 + フォント追従）の実装案を Doc/ に新規追加した。

### Context / Goal
- 面積だけでは判定しづらい横長/縦長1行枠でも、表示文字が小さすぎる問題を改善したい。
- OCR/翻訳パイプラインを変えず、Overlay描画のみで対処する案を明文化する。

### Changes
- effectiveTextPx（短辺/行高ベース）を主指標にした小枠判定案を定義。
- 細長い1行枠向けの動的閾値補正（slender boost）を追加。
- UIスライダー案（閾値・最大倍率・フォント追従重み・追加余白）を定義。
- 初回適用範囲を非固定ROIに限定する方針を明記。

### Files Touched
- Doc/Overlay_SmallBox_Readability_Boost_Plan.md — 実装案を新規作成。

### Behavioral Impact
- コード挙動の変更なし（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: 補正倍率が強すぎると枠重なりが増える。
- Mitigation: SmallBoxMaxScale と SmallBoxFontScaleWeight を保守的初期値で開始する。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。

**2026-02-11 15:34 (Asia/Taipei) — Overlay小枠可読性UIの公開項目を簡潔化**

### Summary
- Overlay_SmallBox_Readability_Boost_Plan の公開UIを「ON/OFF + 感度スライダー」の2項目に整理した。

### Context / Goal
- 小枠可読性機能のUI露出を最小化し、運用時の調整負荷を下げたい。
- 内部パラメータは固定運用にして、誤調整リスクを抑えたい。

### Changes
- 公開設定を EnableSmallBoxReadabilityBoost / SmallTextThresholdPx のみへ変更。
- SmallBoxMaxScale / SmallBoxFontScaleWeight / slender補正係数を内部固定・非UIに再定義。
- 実装手順のUIステップを2項目公開前提へ更新。

### Files Touched
- Doc/Overlay_SmallBox_Readability_Boost_Plan.md — 公開/非公開設定方針と手順を更新。

### Behavioral Impact
- コード挙動の変更なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: 公開項目が少ないため細かな調整要望に即応しにくい。
- Mitigation: まずは内部固定で安定運用し、必要時に内部定数のみ段階調整する。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。

**2026-02-11 15:36 (Asia/Taipei) — Overlay小枠可読性プランにsettings.json調整方針を追記**

### Summary
- UI非公開パラメータを settings.json で調整可能にする方針を計画へ反映した。

### Context / Goal
- UIは簡潔化を維持しつつ、上級者向けの調整余地を残したい。
- 不正値で挙動が崩れないよう、設定ロード時バリデーション方針を明確化したい。

### Changes
- SmallBoxMaxScale など4項目を「UI非公開だが settings.json で調整可能」に変更。
- 読み込み時クランプ（範囲検証）要件を追加。
- 実装手順に settings.json 読込・検証ステップ（Step 2.5）を追加。
- リスク欄に「不正値入力」と「クランプ + 異常値ログ」対策を追加。

### Files Touched
- Doc/Overlay_SmallBox_Readability_Boost_Plan.md — settings.json調整方針とバリデーション要件を追記。

### Behavioral Impact
- コード挙動の変更なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: settings.json 手動編集で極端値が入る。
- Mitigation: 読み込み時クランプと異常値ログを標準動作にする。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。

**2026-02-11 15:43 (Asia/Taipei) — Overlay小枠可読性ブーストを実装**

### Summary
- 小枠可読性ブースト（UI公開2項目 + settings.json調整4項目）を実装し、Overlay描画時に小さいOCR枠を拡大表示できるようにした。

### Context / Goal
- 小さいOCR枠で文字が読みにくい問題を、OCR/翻訳パイプラインに手を入れずOverlay層のみで改善したい。
- UIは簡潔に保ちつつ、上級者が settings.json で詳細調整できる構成にしたい。

### Changes
- AppSettings に小枠可読性設定を追加（公開2 + 非公開4）。
- Settings > OCR に EnableSmallBoxReadabilityBoost と SmallTextThresholdPx スライダーを追加。
- MainWindow で新設定の読込/保存を追加し、settings.json 調整値のクランプ（範囲検証）を実装。
- OverlayWindow に小枠判定（effectiveTextPx）と枠拡大/フォント追従ロジックを追加。
- 小枠補正は非固定ROI時のみ適用し、固定ROIモードは既存挙動を維持。

### Files Touched
- Models/AppSettings.cs — 小枠可読性ブースト設定を追加。
- MainWindow.xaml — 公開UI（ON/OFF + 感度スライダー）を追加。
- MainWindow.xaml.cs — 設定読込/保存、クランプ、表示更新、イベント処理を追加。
- UI/OverlayWindow.xaml.cs — 小枠補正アルゴリズムと描画時適用を追加。

### Behavioral Impact
- EnableSmallBoxReadabilityBoost=false では既存表示と同等。
- EnableSmallBoxReadabilityBoost=true では小さいOCR枠のみ表示枠と基準フォントが拡大され、読みやすさが向上する。
- UI非公開パラメータは settings.json で調整可能だが、範囲外値はロード時にクランプされる。

### Risk & Mitigation
- Risk: 拡大で隣接枠の重なりが増える可能性。
- Mitigation: SmallBoxMaxScale に上限を設け、既定値を保守的に設定した。
- Risk: settings.json の異常値で表示が不安定になる可能性。
- Mitigation: 設定ロード時に範囲検証とクランプを実施し、ログに記録する。

### Tests / Verification
- dotnet build Hotkey-Translator.sln を実行し、成功（0 warnings / 0 errors）。

**2026-02-11 16:10 (Asia/Taipei) — 小枠拡大の縦書き判定と拡大アンカーを修正**

### Summary
- 小枠可読性ブーストが縦書きでも効くように判定式を修正し、拡大方向を横書き/縦書きで分岐した。

### Context / Goal
- 現行実装では LineHeight 優先のため縦書きで補正が発火しにくく、枠拡大が効かない問題があった。
- 併せて、拡大方向を横書きは右下、縦書きは列順に応じて左下/右下へ揃えたい。

### Changes
- OverlayWindow に書字モード判定（強制Horizontal/Vertical優先、Auto時は矩形アスペクト推定）を追加。
- effectiveTextPx を縦横で分離計算に変更（横=高さ基準、縦=幅基準、LineHeight は短辺側にクランプ）。
- 枠拡大を中心拡大からアンカー拡大へ変更。
- アンカー規則を導入（横書き: 右+下、縦書きRTL: 左+下、縦書きLTR: 右+下）。
- ApplyStyle で VerticalModeOverride / VerticalColumnOrder を保持して拡大方向に反映。

### Files Touched
- UI/OverlayWindow.xaml.cs — 書字モード判定、effectiveTextPx算出、アンカー拡大ロジックを修正。

### Behavioral Impact
- 小枠補正ON時、縦書きでも小さい幅の枠に対して拡大が発火しやすくなる。
- 拡大方向が読み方向に揃うため、表示起点の視覚ズレが減る。

### Risk & Mitigation
- Risk: Auto判定で矩形形状が曖昧なケースは誤判定する可能性。
- Mitigation: VerticalModeOverride が強制時はそれを優先し、Autoしきい値は定数で調整可能にした。

### Tests / Verification
- dotnet build Hotkey-Translator.sln を実行し、成功（0 warnings / 0 errors）。

**2026-02-11 16:17 (Asia/Taipei) — 小枠拡大を縦横で軸別倍率に調整**

### Summary
- 小枠拡大を等倍率から軸別倍率へ変更し、横書きは高さ寄り、縦書きは幅寄りに拡大するようにした。

### Context / Goal
- 現行は等倍率拡大のため、必要な軸への拡大効率が低く、重なりが増えやすかった。
- 横書き/縦書きで読みやすさに効く軸へ優先的に拡大をかけたい。

### Changes
- OverlayWindow に軸別倍率定数を追加（dominant/secondary）。
- ExpandRectWithAnchor で書字モード別に scaleX / scaleY を分離計算。
- 横書き: Y優先拡大、縦書き: X優先拡大へ変更。
- 既存アンカー規則（横:右下、縦RTL:左下、縦LTR:右下）は維持。

### Files Touched
- UI/OverlayWindow.xaml.cs — 軸別倍率拡大ロジックを実装。

### Behavioral Impact
- 小枠補正ON時、横書きでは高さ方向の余裕が増え、縦書きでは幅方向の余裕が増える。
- 逆軸の不要な拡大量が減るため、重なりリスクを抑えやすくなる。

### Risk & Mitigation
- Risk: 軸比率が強すぎると一部レイアウトで見切れや重なりが残る可能性。
- Mitigation: 比率定数をコード定義に集約し、必要時に一括調整しやすくした。

### Tests / Verification
- dotnet build Hotkey-Translator.sln を実行し、成功（0 warnings / 0 errors）。

**2026-02-11 17:22 (Asia/Taipei) — 複数ROIセット登録・切替の実装案を追加**

### Summary
- 固定ROI無効化を前提に、複数ROIの手動登録とセット切替機能の実装案を Doc/ に新規作成した。

### Context / Goal
- ゲーム用途で、複数領域をまとめて管理し、セット単位で切り替えたい。
- ROI描画時の重なり防止（近接制約）を設計へ含めたい。

### Changes
- ROIセット構造（RoiSets / ActiveRoiSetId）の提案を追加。
- 複数ROI描画フロー（枠表示、Undo/Clear、登録完了）を定義。
- 重複/近接ガード（IoU + minGap + 包含拒否）を定義。
- 固定ROI Overlay廃止、旧単一ROIからの互換移行方針を明記。

### Files Touched
- Doc/ROI_Set_MultiROI_Registration_Plan.md — 新規作成。

### Behavioral Impact
- コード挙動の変更なし（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: ROI数増加によるOCR処理時間の増大。
- Mitigation: 初期はROI数上限を設け、順次処理で運用する方針を明記。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。

**2026-02-11 17:25 (Asia/Taipei) — ROIセット実装案を固定ROI併存方針へ修正**

### Summary
- ROI_Set_MultiROI_Registration_Plan を「固定ROI維持 + ROIセット分離」方針に更新した。

### Context / Goal
- 先行案が固定ROI廃止前提になっていたため、意図（固定ROIは残す）に合わせて設計を修正したい。
- ROIセットは固定ROIと分離し、独立モードとして運用したい。

### Changes
- 概要/前提/アーキテクチャから固定ROI廃止記述を削除し、併存・分離運用へ変更。
- 設定設計に RoiMode: Single | Set を追加。
- 互換移行を「固定ROIを保持しつつ、必要ならDefaultセットへ任意コピー」に修正。
- 実装手順・影響範囲・DoDを固定ROI維持前提へ更新。

### Files Touched
- Doc/ROI_Set_MultiROI_Registration_Plan.md — 固定ROI併存方針へ全面修正。

### Behavioral Impact
- コード挙動の変更なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: Single/Set のモード分離が曖昧だとUI/実行経路で混線する。
- Mitigation: RoiMode を明示し、Pipelineでモード単位に処理経路を分離する方針を明記。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。

**2026-02-11 17:41 (Asia/Taipei) — ROIセット案に黒塗り単一OCRとROI跨ぎ結合禁止を反映**

### Summary
- ROI_Set_MultiROI_Registration_Plan に、ROI外黒塗り（余白なし）の単一OCR方式とROI跨ぎ結合禁止を反映した。

### Context / Goal
- 複数ROIでもOCR呼び出しを増やさず、既存パイプライン互換を維持したい。
- ゲーム画面でのノイズ抑制と近接ROI間の誤結合防止を設計へ明示したい。

### Changes
- 複数OCR前提を撤回し、RoiMode=Set での「ROI外黒塗り1枚OCR」方式へ変更。
- 余白拡張なし（ROI境界そのまま）をポリシーとして明記。
- OCR行に OwnerRoiIndex を付与し、行結合は同一ROI内のみ許可する方針を追加。
- 実装手順・非機能要件・リスク・DoDを上記方針に合わせて更新。

### Files Touched
- Doc/ROI_Set_MultiROI_Registration_Plan.md — 単一OCR黒塗り方式とROI跨ぎ結合禁止を追記。

### Behavioral Impact
- コード挙動の変更なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: 余白なし黒塗りでROI境界の文字が欠ける可能性。
- Mitigation: 初期は余白なしを維持し、問題時のみエンジン別余白設定を検討する方針を明記。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。

**2026-02-11 18:02 (Asia/Taipei) — ROIセット実装案に4方針を反映**

### Summary
- ROI_Set_MultiROI_Registration_Plan に、指定された4方針（自動移行、OwnerRoiIndex、ROI跨ぎ結合禁止、ROI数1は従来切り抜き）を反映した。

### Context / Goal
- 仕様の最終方針を設計書へ固定し、実装時のブレを防ぎたい。
- ROIセット導入後も既存挙動との差分を必要最小限に抑えたい。

### Changes
- 互換移行を「Defaultセットへ自動移行」に統一。
- OcrLine.OwnerRoiIndex を明示し、結合は同一ROI内のみ許可へ更新。
- EnableRoi / RoiMode / EnableFixedRoiOverlay の前提関係を明文化。
- RoiMode=Set の入力経路を ROI数で分岐（1:従来切り抜き、2以上:ROI外黒塗り1枚OCR）へ更新。
- 実装手順・リスク・DoD・影響ファイルを上記方針に整合させた。

### Files Touched
- Doc/ROI_Set_MultiROI_Registration_Plan.md — 指定4方針を反映して更新。

### Behavioral Impact
- コード挙動の変更なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: RoiMode と既存設定の優先順位解釈が実装でずれる可能性。
- Mitigation: 前提条件とDoDに条件を明記し、実装時に検証可能な形へ固定した。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。

**2026-02-11 18:08 (Asia/Taipei) — ROIセット案に外接矩形切り抜き経路を反映**

### Summary
- ROI_Set_MultiROI_Registration_Plan を更新し、複数ROI時のOCR入力を「外接矩形切り抜き + ROI外黒塗り」へ変更した。

### Context / Goal
- ROI集合全体を覆う矩形で切り抜いて入力を小さくし、既存1回OCRパイプラインを維持したい。
- 余計なノイズを抑えつつ、座標復元の要件を明確化したい。

### Changes
- 概要/データフロー/OCRポリシーを外接矩形切り抜き前提に更新。
- ROI数2以上経路に座標オフセット復元要件を追加。
- 実装手順に経路分岐とオフセット復元ステップを追加。
- リスク/DoDに座標復元検証を追加。
- 影響範囲の PipelineOrchestrator 記述を重複整理。

### Files Touched
- Doc/ROI_Set_MultiROI_Registration_Plan.md — 外接矩形切り抜き + ROI外黒塗り方針へ更新。

### Behavioral Impact
- コード挙動の変更なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: 切り抜き後の座標復元ミスでOverlay位置がずれる。
- Mitigation: オフセット加算の共通化とDoDでの位置一致確認を明記。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。

**2026-02-11 18:13 (Asia/Taipei) — ROI計画書の固定ROI非排他化表現を修正**

### Summary
- ROIセット計画書内の「固定ROIが排他に見える記述」を非排他仕様へ統一した。

### Context / Goal
- 固定ROI機能は残したまま、ROI入力元のみ Single/Set 切替とする意図を文書上で明確化したい。
- 実装時の誤解（固定ROI無効化や排他化）を防ぎたい。

### Changes
- 前提・仮定の文言を、固定ROI維持 + ROI入力元選択の表現へ更新。
- 提案アーキテクチャの「排他で切替運用」を削除し、固定ROI機能は無効化しないことを明記。
- 実装手順 Step 2 を ROI入力元切替 + 固定ROI併存の表現へ修正。

### Files Touched
- Doc/ROI_Set_MultiROI_Registration_Plan.md — 固定ROI非排他の仕様意図に沿う表現へ修正。

### Behavioral Impact
- コード挙動の変更なし（ドキュメント表現の整合修正のみ）。

### Risk & Mitigation
- Risk: 表現変更のみのため、実装時に旧認識が残る可能性。
- Mitigation: 主要3箇所（前提/アーキテクチャ/実装手順）を同時修正して解釈を固定。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。

**2026-02-11 18:25 (Asia/Taipei) — ROI Singleモード仕様の明確化（複数選択禁止）**

### Summary
- ROI Set計画書に RoiMode=Single の複数ROI選択禁止と既存単一ROI経路維持を反映し、全体整合を修正した。

### Context / Goal
- RoiMode=Single では複数ROIを選べない仕様にしたい。
- Single時は既存パイプラインをそのまま利用する方針を文書上で明確化したい。

### Changes
- 概要/前提/アーキテクチャ/インターフェースに、Single時の1ROI制約と既存経路維持を追記。
- データフローをSingle/Setで読めるように補正。
- 実装手順のUI・ガード実装範囲を RoiMode=Set 前提に整理。
- DoDとリスクにSingle時の複数入力防止を追加。

### Files Touched
- Doc/ROI_Set_MultiROI_Registration_Plan.md — Single/Setの責務分離とSingle時の既存経路維持を反映。

### Behavioral Impact
- コード挙動の変更なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: 実装時にSingleでもSet向け処理（多重ROI前提）が混入する可能性。
- Mitigation: Singleの制約（UI禁止・保存時1ROI）とDoDを明示し、レビュー観点を固定。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。

**2026-02-11 18:30 (Asia/Taipei) — ROI順序ルールの矛盾解消（Single/Set分離）**

### Summary
- ROI計画書の「非固定Overlayの順序ルール」を Single/Set で分離し、矛盾しない仕様に修正した。

### Context / Goal
- 「ROIセット登録順で扱う」という文言が Single モードと衝突する曖昧さを解消したい。
- 実装時に ROI間順序と ROI内読順の責務を明確化したい。

### Changes
- RoiMode=Single は既存の読順ロジックに従うと明記。
- RoiMode=Set は ROI間を登録順、ROI内を既存読順ロジックとする規則に分離。

### Files Touched
- Doc/ROI_Set_MultiROI_Registration_Plan.md — 非固定Overlayの描画・翻訳送信順ルールを Single/Set で明確化。

### Behavioral Impact
- コード挙動の変更なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: 順序ルールの適用層（ROI間/ROI内）が実装で混同される可能性。
- Mitigation: 文言を2層に分離し、レビュー時の確認点を明示。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。

**2026-02-11 19:39 (Asia/Taipei) — Small OCR Box拡大を中心アンカーへ変更**

### Summary
- Small-box readability boost の矩形拡大を「開始端固定」から「中心拡大」に変更しました。

### Context / Goal
- 小さいOCR枠の可読性ブースト時、片方向拡大だと表示位置が偏って見える。
- 枠ゆれ時の見切れ/片寄りを減らすため、拡大の基準点を中心へ統一したい。

### Changes
- `UI/OverlayWindow.xaml.cs` の `ExpandRectWithAnchor` で、`x/y` を中心基準で再計算する方式へ変更。
- 旧実装の `VerticalColumnOrder` 依存アンカー分岐（右下/左下方向固定）を削除。
- コメントを WHY 観点で更新（中心拡大で揺れ時のクリップ偏りを抑える意図を明示）。

### Files Touched
- `UI/OverlayWindow.xaml.cs` — small-box拡大のアンカー計算を中心拡大へ変更。

### Behavioral Impact
- readability boost発動時、枠が左右上下に均等に拡張される。
- 縦書きRTL/LTRでの拡大方向差はなくなり、同一ルールで表示される。

### Risk & Mitigation
- Risk: 画面端付近では中心拡大によりはみ出しやすくなる。
- Mitigation: 既存の `ClipRectToOverlayBounds` によりオーバーレイ境界内へクリップされる。

### Tests / Verification
- `dotnet build Hotkey-Translator.sln` 実行成功（0 errors / 0 warnings）。

**2026-02-12 14:32 (Asia/Taipei) — OCR縦結合/横結合 Settings運用ガイドを新規作成**

### Summary
- Settings.json の縦結合・横結合関連項目を整理した運用ガイドを Doc に追加した。

### Context / Goal
- 枠の縦結合/横結合に関わる設定項目が多く、どれをどう調整すべきか分かりにくい。
- 実装ロジックに沿って、優先順・調整方向・症状別の対処を一枚で参照できる資料を用意したい。

### Changes
- OcrLineGrouper の分岐順（Override優先、Auto判定条件、2段結合の有無）を整理して記載。
- 横書き/縦書きの Stage A・Stage B ごとに、関連キーと調整方向を整理。
- よくある症状別に、どのキーをどう動かすかの運用指針を追加。
- Settings.json の最小サンプルを追記。

### Files Touched
- Doc/Ocr_LineMerge_Settings_Guide.md — 縦結合/横結合の設定運用ガイドを新規作成。

### Behavioral Impact
- コード挙動の変更なし（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: 将来ロジック変更でガイド記載が陳腐化する可能性。
- Mitigation: 分岐条件や閾値はコード実装に対応する形で明示し、変更時の更新点を限定できる構成にした。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。

**2026-02-12 14:48 (Asia/Taipei) — ActiveWindow境界でsmall-box可読性ブーストをクランプ**

### Summary
- small OCR box readability boost の拡大クランプ境界を、ActiveWindowキャプチャ時はモニター全体ではなくアクティブウィンドウ境界に合わせるよう変更した。

### Context / Goal
- 一部ゲームでsmall-box拡大時に表示枠が画面全体基準で広がり、意図より外側に寄ることがあった。
- ActiveWindow運用時はゲームウィンドウ内で拡大・クランプしたい。

### Changes
- OverlayWindow に small-box 用クランプ矩形（DIP）を受け取る SetSmallBoxClipBounds を追加。
- ClipRectToOverlayBounds を更新し、指定クランプ矩形がある場合はそれを優先してクリップ。
- OverlayPresenter.Update に任意のクランプ矩形（画面座標）引数を追加し、DIPへ変換して OverlayWindow へ適用。
- ShowLast でも直近クランプ矩形を再適用するように変更。
- PipelineOrchestrator で CaptureMode=ActiveWindow のとき rame.Bounds をクランプ矩形として渡すように変更。
- TrySetOverlayTextMode の再描画でも直近クランプ矩形を維持して適用。

### Files Touched
- UI/OverlayWindow.xaml.cs — small-box拡大のクランプ境界を外部指定できるよう拡張。
- Services/OverlayPresenter.cs — クランプ境界の保持・DIP変換・OverlayWindow適用を追加。
- Services/PipelineOrchestrator.cs — ActiveWindow時のクランプ境界解決と更新経路への引き渡しを追加。

### Behavioral Impact
- CaptureMode=ActiveWindow のとき、small-box readability boost の拡大枠はアクティブウィンドウ領域内でクランプされる。
- CaptureMode=Screen のときは従来どおり仮想スクリーン境界クランプの挙動を維持する。

### Risk & Mitigation
- Risk: 画面座標→DIP変換の誤差で端部クリップが厳しく見える可能性。
- Mitigation: 既存の DpiHelper.ScreenRectToWindowDip を使用し、無効矩形時は従来のウィンドウ全体境界へフォールバックする。

### Tests / Verification
- dotnet build Hotkey-Translator.sln 実行成功（0 errors / 0 warnings）。

**2026-02-12 15:27 (Asia/Taipei) — PaddleOCR v5 パラメーター運用ガイドを新規作成**

### Summary
- PaddleOCR v5 の関連パラメーターを整理し、Settings.json と OcrService/ocr_engine.py の責務を分けた運用ドキュメントを追加した。

### Context / Goal
- PaddleOCR v5 の調整点が複数ファイルに散在しており、どこを運用で触れるべきか判断しづらい。
- 実装経路に沿って、設定可能項目とコード固定項目を分離した実務向けガイドを用意したい。

### Changes
- PaddleOCR 実行経路（OcrEngine -> gRPC Provider -> gRPC Host -> OcrService）を整理して記載。
- Settings.json で調整可能な項目（接続・モデル・後段信頼度フィルタ・再起動制御）を一覧化。
- ocr_engine.py 側の固定閾値（text_det_* / text_rec_score_thresh / padding_px）を明記。
- gRPC経路では PaddleLanguage 非使用、PaddleDevice=cpu 強制補正などの注意点を追記。
- 症状別チューニング指針と最小設定サンプルを追加。

### Files Touched
- Doc/PaddleOCRv5_Settings_Operation_Guide.md — PaddleOCR v5 パラメーター運用ガイドを新規作成。

### Behavioral Impact
- コード挙動の変更なし（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: 将来の実装変更でガイド記載と実コードが乖離する可能性。
- Mitigation: 項目を「Settings連動 / コード固定」に分離し、変更時の更新対象を追いやすい構成にした。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。

**2026-02-12 15:44 (Asia/Taipei) — PaddleOCR本体パラメーター情報を運用ガイドへ反映**

### Summary
- Doc/PaddleOCRv5_Settings_Operation_Guide.md に PaddleOCR 本体が提供する主要パラメーター群と旧名対応を追記し、全体整合を再レビューして修正した。

### Context / Goal
- ocr_engine.py 固定値だけでなく、PaddleOCR 自体の提供パラメーターを運用視点で把握できるようにしたい。
- 旧パラメーター名と新パラメーター名の混在による設定ミスを防ぎたい。

### Changes
- PaddleOCR(...) 初期化で使える主要パラメーター（実行/モデル/前処理/閾値/性能）を追加。
- predict(...) での上書き優先ルール（None でない値が初期化値を優先）を追記。
- 旧名 -> 新名（det_db_* / use_angle_cls）の対応表を追加。
- 公式参照リンクを追記。
- 自己レビューで見つけた章番号順の不整合（6/7）を修正。

### Files Touched
- Doc/PaddleOCRv5_Settings_Operation_Guide.md — PaddleOCR本体パラメーター説明と旧名対応、参考リンクを追加し整合修正。

### Behavioral Impact
- コード挙動の変更なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: PaddleOCR の将来更新でパラメーター体系が変わる可能性。
- Mitigation: 公式ドキュメント参照セクションを明記し、追従更新しやすい形にした。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。

**2026-02-12 15:51 (Asia/Taipei) — PaddleOCR主要パラメーター解説ドキュメントを新規作成**

### Summary
- PaddleOCR 3.x の主要パラメーターと predict(...) 上書きルールを独立ドキュメントとして追加した。

### Context / Goal
- PaddleOCR 本体が提供する項目を、運用判断しやすい形で単独参照できる資料が必要。
- 初期化パラメーターと推論時上書きの使い分けを明確化したい。

### Changes
- PaddleOCR(...) 初期化時の主要パラメーターをカテゴリ別（実行/検出/認識/前処理/しきい値/性能）に整理。
- predict(...) の上書き優先ルール（None でない値が初期化値より優先）を明記。
- 運用時の基本方針（固定基準→局所上書き）を追記。

### Files Touched
- Doc/PaddleOCR_Core_Parameters_Reference.md — PaddleOCR 主要パラメーター解説を新規作成。

### Behavioral Impact
- コード挙動の変更なし（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: PaddleOCR本体の仕様更新で項目名や挙動が変わる可能性。
- Mitigation: 公式参照リンクを併記し、追従更新しやすい構成にした。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。

**2026-02-12 16:00 (Asia/Taipei) — PaddleOCR GPU自動最適化の実装案ドキュメントを新規作成**

### Summary
- enable_hpi 固定 + TRT条件判定 + precision 自動切替 + 失敗時フォールバックを含む実装案を Doc に新規作成した。

### Context / Goal
- PaddleOCR の推論パラメーターを GPU環境に応じて自動決定したい。
- TensorRT 未対応環境でも OCR 初期化が止まらない安全設計にしたい。

### Changes
- enable_hpi=true 固定方針を明記。
- use_tensorrt 条件付きON、precision を p16/fp32 で自動切替する設計を明記。
- PaddleOCR 初期化失敗時の安全フォールバック再試行を設計に追加。
- 実装手順、リスク、DoD をテンプレート形式で整理。

### Files Touched
- Doc/PaddleOCR_GPU_AutoTuning_Plan.md — PaddleOCR GPU自動最適化の実装案を新規作成。

### Behavioral Impact
- コード挙動の変更なし（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: 実装時にTRT判定が環境差で不安定になる可能性。
- Mitigation: 初期化失敗時に use_tensorrt=false + precision=fp32 へ再試行する設計を先に規定。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。

**2026-02-12 16:04 (Asia/Taipei) — PaddleOCR GPU自動最適化を実装（HPI固定/TRT条件判定）**

### Summary
- OcrService/ocr_engine.py に enable_hpi 固定、TensorRT条件判定、precision 自動切替、初期化失敗時フォールバックを実装した。

### Context / Goal
- GPU環境で enable_hpi=true を常用しつつ、TensorRT 可用性に応じて use_tensorrt / precision を安全に自動決定したい。
- TRT非対応環境で初期化失敗して OCR が停止する事態を避けたい。

### Changes
- PaddleOCR kwargs に enable_hpi=true を常時設定。
- TensorRT可用性プローブ（	ensorrt import + Paddle TRT compile version 確認）を追加。
- use_tensorrt はプローブ結果で条件付きON、precision は TRT有効時 p16 / 無効時 p32 を採用。
- TRT有効初期化が失敗した場合、use_tensorrt=false + precision=fp32 で再初期化するフォールバックを追加。
- 最終採用のランタイム設定をログ出力するよう追加。

### Files Touched
- OcrService/ocr_engine.py — GPU自動最適化ロジックとフォールバック初期化を実装。

### Behavioral Impact
- GPU環境で TRT利用可能時は enable_hpi=true, use_tensorrt=true, precision=fp16 が優先される。
- TRT利用不可/初期化失敗時は自動的に use_tensorrt=false, precision=fp32 へ退避し、OCR初期化継続を優先する。

### Risk & Mitigation
- Risk: 環境依存で TRT 判定が過剰に保守的になり、TRT を使えない場合がある。
- Mitigation: 判定理由（probe結果）をログに残し、必要時に判定条件を調整できるようにした。

### Tests / Verification
- python -m py_compile OcrService/ocr_engine.py 実行成功。

**2026-02-12 16:05 (Asia/Taipei) — PaddleOCR GPU自動最適化を実装（記録訂正版）**

### Summary
- `OcrService/ocr_engine.py` に `enable_hpi` 固定、TensorRT条件判定、`precision` 自動切替、初期化失敗時フォールバックを実装した。

### Context / Goal
- GPU環境で `enable_hpi=true` を常用しつつ、TensorRT 可用性に応じて `use_tensorrt` / `precision` を安全に自動決定したい。
- TRT非対応環境で初期化失敗して OCR が停止する事態を避けたい。

### Changes
- PaddleOCR `kwargs` に `enable_hpi=true` を常時設定。
- TensorRT可用性プローブ（`tensorrt` import + Paddle TRT compile version 確認）を追加。
- `use_tensorrt` はプローブ結果で条件付きON、`precision` は TRT有効時 `fp16` / 無効時 `fp32` を採用。
- TRT有効初期化が失敗した場合、`use_tensorrt=false` + `precision=fp32` で再初期化するフォールバックを追加。
- 最終採用のランタイム設定をログ出力するよう追加。

### Files Touched
- `OcrService/ocr_engine.py` — GPU自動最適化ロジックとフォールバック初期化を実装。

### Behavioral Impact
- GPU環境で TRT利用可能時は `enable_hpi=true`, `use_tensorrt=true`, `precision=fp16` が優先される。
- TRT利用不可/初期化失敗時は自動的に `use_tensorrt=false`, `precision=fp32` へ退避し、OCR初期化継続を優先する。

### Risk & Mitigation
- Risk: 環境依存で TRT 判定が過剰に保守的になり、TRT を使えない場合がある。
- Mitigation: 判定理由（probe結果）をログに残し、必要時に判定条件を調整できるようにした。

### Tests / Verification
- `python -m py_compile OcrService/ocr_engine.py` 実行成功。

**2026-02-12 16:16 (Asia/Taipei) — OCR枠結合のエンジン別補正係数 実装案を新規作成**

### Summary
- WinRT/Paddle の枠特性差分を「共通ベース + エンジン別係数」で吸収する実装案を Doc に追加した。

### Context / Goal
- Doc/WinRT-settings.json と Doc/Paddle-settings.json の最適値差を、二重設定化せず運用可能な形に落とし込みたい。
- 既存互換を維持しつつ、エンジン別に実効閾値を分けたい。

### Changes
- WinRTをベース値、Paddleを係数（Paddle/WinRT）で導出する方針を明記。
- 実効値プロファイル（effective = base * scale）と clamp 方針を定義。
- 係数対象を横結合優先、縦結合は初期 1.0 固定とする段階導入案を追加。
- 実装手順、影響範囲、DoD をテンプレート形式で整理。

### Files Touched
- Doc/Ocr_LineMerge_EngineScale_Profile_Plan.md — エンジン別補正係数の実装案を新規作成。

### Behavioral Impact
- コード挙動の変更なし（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: 係数が過大/過小で誤結合または未結合が増える可能性。
- Mitigation: 機能フラグ初期OFF + clamp + 実効値ログで段階検証可能にした。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。

**2026-02-12 16:25 (Asia/Taipei) — OcrLineGrouper エンジン別スケール実装**

### Summary
- Doc/Ocr_LineMerge_EngineScale_Profile_Plan.md に沿って、Paddle時のみ有効になるエンジン別補正係数を実装した。

### Context / Goal
- WinRT と Paddle の OCR 枠特性差を、共通ベース値 + エンジン別係数で吸収したい。
- 既存挙動との互換性を維持しつつ、必要時だけ実効閾値を切り替えたい。

### Changes
- AppSettings に EnableEngineScaledLineMergeProfile と Paddle向けスケール係数群を追加。
- OcrLineGrouper に実効閾値の解決ロジック（base * scale + clamp）を追加。
- EnableEngineScaledLineMergeProfile=true かつ OcrEngine=Paddle のときだけスケール適用する分岐を実装。
- 横結合/縦結合の判定で raw settings ではなく実効閾値を参照するよう差し替え。
- 実効プロファイルのログを同一値重複出力しないガード付きで追加。

### Files Touched
- Models/AppSettings.cs — エンジン別スケール機能の設定項目と既定値を追加。
- Services/OcrLineGrouper.cs — 実効閾値解決、適用、ログ出力を実装。

### Behavioral Impact
- 既定値（EnableEngineScaledLineMergeProfile=false）では従来挙動のまま。
- 機能ON時、Paddleのみスケール済み閾値で行/列結合判定を行う。

### Risk & Mitigation
- Risk: 係数が環境に合わない場合、誤結合または未結合が増える可能性。
- Mitigation: 機能フラグ初期OFF・係数を設定で調整可能・実効値ログで検証可能にした。

### Tests / Verification
- dotnet build Hotkey-Translator.sln 実行成功（0 warnings / 0 errors）。

**2026-02-12 17:44 (Asia/Taipei) — ROI Set Plan 推奨修正反映**

### Summary
- Doc/ROI_Set_MultiROI_Registration_Plan.md に、実装前レビューでの推奨事項（順序保証・移行一本化・OwnerRoiIndex規則・Watcher連携）を反映した。

### Context / Goal
- 実装着手前に、仕様解釈の揺れで不具合化しやすい箇所を先に文書で固定したい。
- 表示順と翻訳送信順の不整合、旧設定との二重管理、監視系の取りこぼしを防ぎたい。

### Changes
- source of truth を RoiSets + ActiveRoiSetId + RoiMode + OverlayLayoutMode に一本化し、旧設定は互換読込専用と明記。
- OwnerRoiIndex の判定手順（中心点優先→IoU最大→登録順タイブレーク→閾値未満は -1 除外）を追加。
- 最終表示順/翻訳送信順を ROI登録順 -> ROI内読順 に再整列する MUST を追加。
- scene-change watcher の ROI 適用経路（Single/Set1は従来、Set複数は外接矩形+黒塗り）を追加。
- DoD に順序保証・OwnerRoiIndex割当・watcher経路検証のチェック項目を追加。

### Files Touched
- Doc/ROI_Set_MultiROI_Registration_Plan.md — 実装前の仕様固定項目を追記・明確化。

### Behavioral Impact
- コード挙動の変更なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: 文書だけ更新して実装が追随しない可能性。
- Mitigation: 追加したDoD項目を実装時の受け入れ基準として使用する。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。

**2026-02-12 18:18 (Asia/Taipei) — OCR前処理プレビュー即時反映 実装案作成**

### Summary
- 設定変更時にOCR本実行を待たず前処理プレビューを更新する実装案を Doc に新規作成した。

### Context / Goal
- 現状は Run OCR 実行時のみプレビュー更新され、設定調整の即時フィードバックがない。
- 前処理設定の調整体験を改善しつつ、本番OCRパイプラインへの影響を最小化したい。

### Changes
- プレビュー専用更新API（PipelineOrchestrator）と UI側 debounce/cancel 制御（MainWindow）の構成案を定義。
- 対象設定の判定方針、データフロー、段階実装ステップ、リスク緩和策を整理。
- DoD に即時反映・過剰更新抑制・ROI変更反映・ビルド成功を明記。

### Files Touched
- Doc/Ocr_Preprocess_Preview_Immediate_Refresh_Plan.md — OCR前処理プレビュー即時反映の実装案を新規追加。

### Behavioral Impact
- コード挙動の変更なし（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: 実装時に対象設定判定漏れが起き、期待どおり再描画されない可能性。
- Mitigation: 対象設定を明示リスト化し、DoD で確認手順を固定する。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。

**2026-02-13 00:59 (Asia/Taipei) — PaddleOCR-VL gRPC排他運用 実装案作成**

### Summary
- PaddleOCR と PaddleOCR-VL を相互排他にし、切替時に非選択側リソースを解放する方針を含む全体実装案を Doc に新規作成した。

### Context / Goal
- OcrServiceVL を PaddleOCR と同様の gRPC サーバ方式へ統一したい。
- Paddle/PaddleVllm の同時常駐を防ぎ、切替時に VRAM/プロセス/チャネルを解放する運用方針を明文化したい。

### Changes
- OcrServiceVL gRPC 化の全体構成（Python server/engine、C# host/provider、UI配線）を整理。
- 相互排他ルール（選択側のみ起動、非選択側停止）と解放対象（プロセス/チャネル/監視タスク）を明記。
- 実装ステップ、AppSettings追加候補、DoD、リスク緩和策を定義。

### Files Touched
- Doc/PaddleOCR_VL_Grpc_MutualExclusion_Plan.md — PaddleOCR-VL gRPC化と相互排他・リソース解放方針を含む実装案を新規追加。

### Behavioral Impact
- コード挙動の変更なし（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: ドキュメントのみ先行し、実装時に排他停止処理の漏れが出る可能性。
- Mitigation: DoD に「非選択側停止・解放」を明示し、実装時の受け入れ条件として固定。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。

**2026-02-13 02:58 (Asia/Taipei) — SceneChange自動翻訳 Pending追いかけ実行案 作成**

### Summary
- 自動翻訳スキップ時に未処理フラグを保持し、実行可能時に1回だけ追いかけ実行する実装案を Doc に追加した。

### Context / Goal
- 現状はシーン変化検知が実行中/クールダウンで破棄され、タイミングによって未発火になる。
- 破棄せず 1 件集約で後追い実行し、未発火を減らしたい。

### Changes
- Pending 状態フィールド、Mark/Drain/Clear ヘルパー、既存メソッド改修ポイントを定義。
- QueueSceneChangeAutoTranslate・RunOnceAsync finally・OnAutoHideTick への反映手順を具体化。
- リスク、緩和策、DoD、Open Questions を整理。

### Files Touched
- Doc/SceneChange_AutoTranslate_PendingDrain_Plan.md — 自動翻訳未発火対策（Pending 1回追いかけ実行）の実装案を新規追加。

### Behavioral Impact
- コード挙動の変更なし（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: 実装時に pending クリア漏れやログノイズ増加が起きる可能性。
- Mitigation: clear 導線を明示し、DoD に残留なし・1回実行を含めて検証基準化。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。

**2026-02-13 03:03 (Asia/Taipei) — 自動翻訳UI静音化（Spinner/No text）実装案作成**

### Summary
- 自動翻訳有効時に右下SpinnerとOCR No text通知を抑制する実装案を Doc に追加した。

### Context / Goal
- シーン変化監視では短周期実行が起きやすく、SpinnerとNo text表示がノイズ化しやすい。
- 自動実行のみUIフィードバックを静音化し、手動実行の可視性は維持したい。

### Changes
- 実行トリガー識別フラグ（auto/manual）を ForceRunOptions へ追加する設計案を定義。
- MainWindow の Spinner制御と PipelineOrchestrator の No textトースト抑制条件を整理。
- 実装手順、リスク、DoD、Open Questions を明記。

### Files Touched
- Doc/SceneChange_AutoTranslate_QuietUi_Plan.md — 自動翻訳時のSpinner/No text非表示に関する実装案を新規追加。

### Behavioral Impact
- コード挙動の変更なし（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: 自動実行が見えにくくなり、停止と誤認される可能性。
- Mitigation: ログに auto 実行開始/終了および suppressed 通知を残す方針を併記。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。

**2026-02-13 09:25 (Asia/Taipei) — SceneChange PendingDrain Plan 明確化修正**

### Summary
- Doc/SceneChange_AutoTranslate_PendingDrain_Plan.md に、PendingSince削除と実装曖昧点の解消（drain時クールダウン更新・Tick挙動固定・clear導線固定）を反映した。

### Context / Goal
- レビューで指摘した仕様の曖昧さを実装前に解消し、実装者間の解釈差を防ぎたい。
- 特に drain 発火条件とモードOFF時の stale pending 防止を明文化したい。

### Changes
- _sceneChangeAutoTranslatePendingSinceUtc を削除し、未使用フィールドを整理。
- drain 成功時に _lastSceneChangeAutoTranslateRequestUtc を更新する仕様を追加。
- OnAutoHideTick で drain 起動した Tick は即 return する挙動を明記。
- pending clear の呼び出し箇所（Stop watcher / F5 OFF / SaveSettings正規化後OFF）を固定。
- pending set ログ抑制を「同一 reason 2 秒窓」に具体化。

### Files Touched
- Doc/SceneChange_AutoTranslate_PendingDrain_Plan.md — PendingDrain 実装案の仕様明確化を追記・修正。

### Behavioral Impact
- コード挙動の変更なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: 具体化した挙動が既存実装制約に合わず再調整が必要になる可能性。
- Mitigation: 実装時に MainWindow.xaml.cs の既存 run/watcher 制御へ沿って最小差分で適用する。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。

**2026-02-13 09:36 (Asia/Taipei) — SceneChange auto-translate PendingDrain 実装**

### Summary
- Scene-change 自動翻訳のスキップ時イベントを pending として保持し、実行可能時に1回だけ後追い実行する処理を実装した。

### Context / Goal
- クールダウン中や OCR 実行中に検知イベントが破棄されると、未発火のまま終わるケースがあった。
- 既存の run ガードを維持したまま、pending を集約回収して未発火を減らしたい。

### Changes
- MainWindow.xaml.cs に pending 状態フィールドとログ抑制定数を追加。
- MarkSceneChangeAutoTranslatePending / TryDrainPendingSceneChangeAutoTranslate / ClearSceneChangeAutoTranslatePending を実装。
- QueueSceneChangeAutoTranslate を、スキップ時は pending 化、即時実行時は pending クリアへ変更。
- RunOnceAsync finally で _runInProgress 解放後に pending drain を試行するよう追加。
- OnAutoHideTick 冒頭で drain を試行し、drain 起動時は同 Tick の検知処理を打ち切るよう変更。
- StopAutoHideWatcher、F5 auto-translate OFF、SaveSettingsAsync の mode 正規化後OFFで pending クリアを追加。

### Files Touched
- MainWindow.xaml.cs — PendingDrain ロジックと既存 watcher/run 経路への接続を実装。

### Behavioral Impact
- シーン変化検知がクールダウン中または実行中でも、イベントは pending として保持される。
- 実行可能になったタイミングで pending が1回だけ実行される。
- auto-translate OFF または watcher 停止時に stale pending は破棄される。

### Risk & Mitigation
- Risk: pending lifecycle ログが増える可能性。
- Mitigation: 同一 reason の pending set ログは 2 秒窓で抑制した。

### Tests / Verification
- dotnet build Hotkey-Translator.sln 実行成功（0 warnings / 0 errors）。

**2026-02-13 09:56 (Asia/Taipei) — DXGI timeout のクールダウン抑制**

### Summary
- DXGI の wait timeout をクールダウン対象から外し、DXGI固定時の長い取得停止を緩和した。

### Context / Goal
- DXGI固定 + 自動翻訳監視で DXGI timed out waiting for a new frame. が発生すると、プロバイダがクールダウン入りして数秒間 OCR が進まない。
- transient timeout を恒久障害扱いしないようにして、再取得の機会を維持したい。

### Changes
- CaptureManager.Capture の失敗処理で、CaptureProviderKind.Dxgi かつ DXGI timed out waiting for a new frame. の場合は StartCooldown を呼ばない分岐を追加。
- DXGI wait timeout 判定用の IsDxgiWaitTimeout(string? error) ヘルパーを追加。
- WHY コメントを追加し、設計意図（transient timeout を cooldown しない）を明記。

### Files Touched
- Services/CaptureManager.cs — DXGI wait timeout のクールダウン抑制ロジックを実装。

### Behavioral Impact
- DXGI wait timeout 発生時、DXGI がクールダウンに入らなくなる。
- DXGI固定時でも次回 tick で即再試行でき、数秒単位の空白期間が減る。

### Risk & Mitigation
- Risk: timeout 発生頻度が高い環境では再試行回数が増える可能性。
- Mitigation: 変更は wait timeout のみに限定し、他のDXGIエラーは従来どおりクールダウンを維持する。

### Tests / Verification
- dotnet build Hotkey-Translator.sln 実行成功（0 warnings / 0 errors）。

**2026-02-13 10:11 (Asia/Taipei) — DXGI timeout 時の前回フレーム維持（最小実装）**

### Summary
- DXGI fixed で AcquireNextFrame が timeout した場合に、失敗ではなく直近成功フレームを返す最小実装を追加した。

### Context / Goal
- DXGI timeout は一時的な「新規フレームなし」であり、固定運用時に OCR 実行の空振りが増えやすい。
- timeout 時でも直前フレームを維持して返し、実行パイプラインを継続させたい。

### Changes
- DxgiDuplicationProvider に直近成功フレームのキャッシュ（bitmap/bounds/mode/monitor）を追加。
- 成功キャプチャ時に RememberLastSuccessfulFrame(...) でキャッシュを更新。
- AcquireNextFrame timeout 時に TryCreateFrameFromLastSuccess(...) を優先し、取得できれば成功として返却。
- キャッシュ不一致（mode/monitor違い）または未保持時は従来どおり timeout エラーを返す。

### Files Touched
- Services/DxgiDuplicationProvider.cs — timeout 時の前回フレーム返却ロジックを実装。

### Behavioral Impact
- DXGI fixed 運用で timeout 発生時でも、条件一致時は前回フレームで OCR が継続する。
- monitor/mode が変わった場合は誤用を避けるため前回フレームを使わず、従来挙動のまま失敗扱い。

### Risk & Mitigation
- Risk: 画面が更新されていない間は同一フレームを繰り返し返し、差分判定が変化しにくくなる可能性。
- Mitigation: fallback は timeout 時のみ発動し、通常の新規フレーム取得時は常に最新キャプチャで上書きされる。

### Tests / Verification
- dotnet build Hotkey-Translator.sln は実行中プロセスによる DLL/EXE ロックで失敗（コードエラーではない）。
- 代替として dotnet build Hotkey-Translator.sln /p:OutputPath=bin\\TempVerify\\ でビルド成功（0 warnings / 0 errors）。

**2026-02-13 10:23 (Asia/Taipei) — Quiet UI計画の現状仕様整合リライト**

### Summary
- `SceneChange_AutoTranslate_QuietUi_Plan.md` を現行実装に合わせて再構成し、pending drain経路漏れ・ログ定義不足を解消した。

### Context / Goal
- 自動翻訳時の一時UI抑制案について、実装レビューで見つかった仕様漏れをドキュメント側で是正する。
- BusyOverlay は維持する方針を明文化し、実装時の解釈ブレを防ぐ。

### Changes
- 自動翻訳の2経路（即時実行/保留drain実行）を明記し、両方で同一optionsを使う要件に変更。
- `ForceRunOptions` の役割分担を明確化し、`RunTrigger`（観測用）と `SuppressTransientUiFeedback`（表示制御用）を定義。
- `No text detected` 抑制時の必須ログ（suppressed toast）と、run開始時の `trigger` ログ要件を追加。
- 非ゴールへ「中央 BusyOverlay は現行維持」を明記し、DoDにも反映。

### Files Touched
- `Doc/SceneChange_AutoTranslate_QuietUi_Plan.md` — 仕様漏れ修正と方針固定（BusyOverlay維持、pending drain統一、ログ要件明確化）。

### Behavioral Impact
- なし（ドキュメント更新のみ）。
- ただし本計画どおり実装すれば、自動翻訳時の右下Spinner/No text toastの抑制が即時/Drain両経路で一貫する。

### Risk & Mitigation
- Risk: 実装側が旧計画のまま進むと、Drain経路でUI抑制漏れが再発する。
- Mitigation: 影響範囲とDoDで「即時/Drainの両経路」を明示して固定した。

### Tests / Verification
- `Doc/SceneChange_AutoTranslate_QuietUi_Plan.md` をUTF-8で再読し、更新内容を行単位で確認。
- コード変更は未実施のためビルド/実行テストは未実施。

**2026-02-13 10:28 (Asia/Taipei) — Scene-change auto-translate Quiet UI実装**

### Summary
- 自動シーン変化実行時のみ右下SpinnerとNo textトーストを抑制し、手動実行時の挙動は維持した。

### Context / Goal
- `Doc/SceneChange_AutoTranslate_QuietUi_Plan.md` の方針をコードへ反映する。
- 特に pending drain 経路を含む自動実行全経路で Quiet UI を一貫適用する。

### Changes
- `RunTrigger` enum を追加し、`ForceRunOptions` に `Trigger` と `SuppressTransientUiFeedback` を追加。
- `PipelineOrchestrator.RunOnceAsync` に run context ログ（trigger / suppress flag）を追加。
- `PipelineOrchestrator` の `No text detected` 2箇所で、`SuppressTransientUiFeedback=true` 時はトーストを抑制し suppression ログを出力する分岐を追加。
- `MainWindow` に auto-scene 用 options を共通定義し、即時実行と pending drain 実行の双方で同じ options を使用。
- `MainWindow.RunOnceAsync` の Spinner 表示/非表示を `SuppressTransientUiFeedback` 連動に変更。
- `WHY` コメントを追加し、auto options 共通化の意図を明示。

### Files Touched
- `Services/PipelineOrchestrator.cs` — `RunTrigger`/`ForceRunOptions` 拡張、run context ログ追加、No textトースト抑制分岐を実装。
- `MainWindow.xaml.cs` — auto-scene 共通 options 追加、Spinner抑制分岐、scene-change即時/Drain経路の呼び出し更新。

### Behavioral Impact
- 自動シーン変化実行（即時/Drainの両経路）では右下Spinnerが表示されない。
- 自動シーン変化実行（即時/Drainの両経路）では `No text detected` トーストが表示されない。
- 手動実行は従来どおりSpinner/No textトースト表示を維持。
- 中央 BusyOverlay は従来どおり表示される。

### Risk & Mitigation
- Risk: UI抑制で実行有無が見えにくくなる可能性。
- Mitigation: run context ログと `No text detected (toast suppressed).` ログを追加し可観測性を維持。

### Tests / Verification
- `dotnet build Hotkey-Translator.sln /p:OutputPath=bin\\TempVerify\\` 実行成功（0 warnings / 0 errors）。

**2026-02-13 10:41 (Asia/Taipei) — PaddleOCR-VL gRPC排他計画の方針反映（Vllm廃止/WinRt非停止）**

### Summary
- `Doc/PaddleOCR_VL_Grpc_MutualExclusion_Plan.md` を現方針へ更新し、Vllm設定廃止・WinRt時非停止・実装責務の曖昧さを解消した。

### Context / Goal
- 現行コードレビュー結果を踏まえ、計画書を実装可能な粒度に修正する。
- ユーザー方針（既存 Vllm* 廃止、WinRt時は両停止しない）を優先して文書へ固定する。

### Changes
- ゴールに「`Vllm*`（HTTP直叩き）設定廃止、gRPC一本化」を追加。
- 非ゴールに「WinRt選択時のPaddle系自動停止は要件外」を明記。
- 現状整理へ、既存 `PaddleVllmOcrProvider` と `Vllm*` 設定残存を追記。
- 提案構成で `PaddleVllm` の参照を gRPC化し、HTTP provider 参照撤去を明記。
- 排他ルールを「Paddle と PaddleVllm の相互切替時のみ」に修正。
- `IDisposable` の責務を明確化し、`OcrEngine : IDisposable` + `MainWindow.OnClosed` での明示解放に固定。
- proto運用を `Protos/OcrGrpc.proto` 単一ソースへ統一する方針を追加。
- AppSettings へ `Vllm*` 廃止項目と互換移行（起動時無視・保存時収束）を追記。
- 実装手順、スモークテスト、DoD を新方針に合わせて更新。

### Files Touched
- `Doc/PaddleOCR_VL_Grpc_MutualExclusion_Plan.md` — 方針更新、責務明確化、移行方針追加、DoD/テスト条件の再定義。

### Behavioral Impact
- なし（ドキュメント更新のみ）。
- ただし本計画どおり実装すれば、Paddle/PaddleVllm は排他運用、WinRtは非停止運用、Vllm*は廃止運用で一貫する。

### Risk & Mitigation
- Risk: 旧 `Vllm*` 設定残存による移行時混乱。
- Mitigation: 計画で「起動時無視・保存時削除/空化」を明記し、収束ルールを固定。

### Tests / Verification
- `Doc/PaddleOCR_VL_Grpc_MutualExclusion_Plan.md` を UTF-8 で再読し、排他定義・非ゴール・DoD の整合を確認。
- コード変更は未実施のためビルド/実行テストは未実施。

**2026-02-13 11:04 (Asia/Taipei) — PaddleOCR-VL計画へUI公開パラメーター方針を反映**

### Summary
- `OcrServiceVL/main.py` 引数を考慮した UI公開/非公開パラメーター方針を `Doc/PaddleOCR_VL_Grpc_MutualExclusion_Plan.md` に追記した。

### Context / Goal
- PaddleOCR Core と PaddleOCR-VL のパラメーターのうち、運用で安全にUI露出できる範囲を明確化する。
- 過剰露出による設定事故を避けつつ、実運用で必要なしきい値調整は可能にする。

### Changes
- `7.3 UI露出パラメーター方針（Core + VL）` セクションを追加。
- 公開UI: Coreの4しきい値（`text_det_thresh`, `text_det_box_thresh`, `text_det_unclip_ratio`, `text_rec_score_thresh`）とVLの3項目（`pipeline_version`, `max_pixels`, `layout_threshold`）を定義。
- 非公開設定（Settings.json）: VL挙動系（`merge_layout_blocks`, `use_ocr_for_image_block`, `use_layout_detection`, `max_new_tokens`）と性能系（`enable_hpi`, `use_tensorrt`, `precision`）を定義。
- UI非対象: `input_image`, `save_dir`（CLI専用）と `device`（Auto既定）を明記。
- 競合回避: 既存 `PaddleConfidenceThreshold` 系と `text_rec_score_thresh` の二重調整回避ルールを追記。

### Files Touched
- `Doc/PaddleOCR_VL_Grpc_MutualExclusion_Plan.md` — UI公開/非公開パラメーター方針を追加。

### Behavioral Impact
- なし（ドキュメント更新のみ）。
- ただし本方針どおり実装すると、UIは最小公開で運用しやすく、詳細チューニングはSettings.json側に分離される。

### Risk & Mitigation
- Risk: 閾値系を複数経路で露出すると調整意図が衝突する可能性。
- Mitigation: 文書に「一系統へ統一（推奨: `text_rec_score_thresh`）」を明記。

### Tests / Verification
- `Doc/PaddleOCR_VL_Grpc_MutualExclusion_Plan.md` を UTF-8 で再読し、追加セクションの位置・内容整合を確認。
- コード変更は未実施のためビルド/実行テストは未実施。

**2026-02-13 11:09 (Asia/Taipei) — PaddleOCR-VL計画へ停止/再開（設定反映）運用を追記**

### Summary
- `Doc/PaddleOCR_VL_Grpc_MutualExclusion_Plan.md` に OCRホストの `停止` と `設定を反映して再起動` 導線を追加した。

### Context / Goal
- Llama同様に、設定変更後に明示操作でホストへ反映できる運用を計画へ反映する。
- 排他制御と競合防止を維持したまま、運用者の再適用手順を明確化する。

### Changes
- C#側提案にホスト制御UI（主操作: `設定を反映して再起動`、副操作: `停止`）を追加。
- 実装ポイントへ、保存先行・`_runInProgress` 中ボタン無効化・`resourceLoadGate` 直列化を追記。
- 実装手順に「ホスト制御UI（停止/再起動）追加」ステップを追加。
- スモークテストへ、再起動反映確認と停止操作の影響範囲確認を追加。
- リスクへ「未保存再起動による想定外構成起動」を追加し、保存必須化で緩和する方針を追記。
- DoDへ「再起動で最新設定反映」「停止で対象ホスト停止」を追加。

### Files Touched
- `Doc/PaddleOCR_VL_Grpc_MutualExclusion_Plan.md` — 停止/再起動UI運用、実装条件、検証条件を追記。

### Behavioral Impact
- なし（ドキュメント更新のみ）。
- ただし本計画どおり実装すると、設定反映タイミングをユーザーが明示的に制御できる。

### Risk & Mitigation
- Risk: 実行中操作や未保存状態で再起動すると状態競合や設定不一致が起きる可能性。
- Mitigation: 実行中ボタン無効化、保存先行、`resourceLoadGate` 直列化を必須化。

### Tests / Verification
- `Doc/PaddleOCR_VL_Grpc_MutualExclusion_Plan.md` を UTF-8 で再読し、追加したUI運用・DoD・リスク項目の整合を確認。
- コード変更は未実施のためビルド/実行テストは未実施。

**2026-02-13 11:13 (Asia/Taipei) — PaddleOCR-VLタブ集約とMaxTokensレンジ要件を計画へ反映**

### Summary
- `Doc/PaddleOCR_VL_Grpc_MutualExclusion_Plan.md` に「PaddleOCR-VLタブへUI集約」と `max_new_tokens(512～4096)` 露出要件を追記した。

### Context / Goal
- UI露出先を `Settings > PaddleOCR-VL` タブに統一し、設定導線を分散させない。
- `max_new_tokens` を運用可能な範囲でUI公開し、過大/過小値を防ぐ。

### Changes
- C#側提案に「PaddleOCR-VLタブ新設」「UI露出項目の集約」を追加。
- AppSettings候補へ `PaddleVlMaxNewTokens` と UIレンジ制限（512～4096）を追加。
- UI方針セクションをタブ集約前提へ更新し、`max_new_tokens` を公開UIへ移動。
- `max_new_tokens` は UI入力時と Python引数生成時の二重クランプを明記。
- 実装手順にタブ追加とレンジ制約実装を追加。
- スモークテストに `max_new_tokens` クランプ確認を追加。
- DoD に「タブ集約完了」「`max_new_tokens` 範囲運用」を追加。

### Files Touched
- `Doc/PaddleOCR_VL_Grpc_MutualExclusion_Plan.md` — UI集約方針とMaxTokens制約を追記。

### Behavioral Impact
- なし（ドキュメント更新のみ）。
- ただし本計画どおり実装すると、PaddleOCR-VL関連UIは単一タブに集約され、`max_new_tokens` は安全レンジで運用される。

### Risk & Mitigation
- Risk: UI制限だけで満足すると、非UI経路（設定直編集）で範囲外値が混入する可能性。
- Mitigation: Python引数生成時にも二重クランプする要件を明記。

### Tests / Verification
- `Doc/PaddleOCR_VL_Grpc_MutualExclusion_Plan.md` を UTF-8 で再読し、タブ集約・レンジ制約・DoD の整合を確認。
- コード変更は未実施のためビルド/実行テストは未実施。

**2026-02-13 11:18 (Asia/Taipei) — PaddleOCR-VL計画にAUTO/停止no-op仕様を反映**

### Summary
- `max_new_tokens` の未指定時挙動を `AUTO(None)` に固定し、停止ボタンの no-op 条件を計画へ反映した。

### Context / Goal
- 最終確認で残った曖昧点（未指定時挙動、停止ボタン対象）を仕様として確定する。
- 実装時の解釈ブレをなくす。

### Changes
- ホスト制御UIの副操作を「`PaddleOCR-VL` ホスト生存時のみ停止、それ以外は no-op」に更新。
- AppSettings候補の `PaddleVlMaxNewTokens` に「未指定は `AUTO(None)`」を追記。
- UI方針へ「未指定時は AUTO 扱いで Python に `None` を渡す」ルールを追加。
- 実装手順の停止ボタン仕様を no-op 条件付きに更新。
- スモークテストに「AUTO(None)動作確認」「非稼働時停止no-op確認」を追加。
- DoD に「AUTO(None)運用」「停止no-op仕様」を追加。

### Files Touched
- `Doc/PaddleOCR_VL_Grpc_MutualExclusion_Plan.md` — AUTO/停止no-op仕様を明文化。

### Behavioral Impact
- なし（ドキュメント更新のみ）。
- ただし本計画どおり実装すると、未指定 `max_new_tokens` は内部既定利用となり、停止ボタンは無害な no-op 挙動になる。

### Risk & Mitigation
- Risk: UI未指定値や非稼働停止の扱いが実装者ごとに変わる可能性。
- Mitigation: 文書に明示仕様（AUTO(None) / no-op）を固定し、テスト項目にも追加。

### Tests / Verification
- `Doc/PaddleOCR_VL_Grpc_MutualExclusion_Plan.md` を UTF-8 で再読し、追加仕様が実装手順・DoD と一致することを確認。
- コード変更は未実施のためビルド/実行テストは未実施。

**2026-02-13 11:39 (Asia/Taipei) — PaddleOCR-VL gRPC mutual exclusion implementation**

### Summary
- PaddleOCR-VL をローカル gRPC 経路で実装し、Paddle/PaddleOCR-VL の相互排他起動と UI/設定反映導線を追加した。

### Context / Goal
- Doc/PaddleOCR_VL_Grpc_MutualExclusion_Plan.md の実装。
- Paddle と PaddleVllm の切替時に非選択側ホストを停止し、OCR実行経路を gRPC に一本化する。

### Changes
- OcrServiceVL に gRPC サーバ (server.py) を追加し、ocr_vl_engine.py を実運用用エンジンとして接続。
- Services/PaddleVlGrpcHost.cs と Services/PaddleVlGrpcOcrProvider.cs を追加し、OcrEngineKind.PaddleVllm の実行経路を有効化。
- Services/OcrEngine.cs を IDisposable 化し、Paddle/PaddleOCR-VL gRPC channel 解放を実装。
- MainWindow.xaml(.cs) に OCR engine 選択肢 (PaddleOCR-VL) と PaddleOCR-VL 設定パネル、再起動/停止ボタンを追加。
- EnsureResourceHostsAsync を拡張し、Paddle 選択時は PaddleVl 停止、PaddleVllm 選択時は Paddle 停止を強制。
- SettingsService/AppSettings から旧 Vllm* 設定を撤去し、PaddleVlGrpc* と VL推論設定を追加。
- Core Paddle の 	ext_det_* / 	ext_rec_score_thresh を settings 経由で渡せるよう OcrService/server.py と OcrService/ocr_engine.py を拡張。
- 旧 HTTP provider Services/PaddleVllmOcrProvider.cs を削除。

### Files Touched
- MainWindow.xaml — OCR engine選択肢追加、Settingsカテゴリ名変更、PaddleOCR-VL 設定UI/再起動・停止ボタン追加。
- MainWindow.xaml.cs — Paddle/PaddleVL host 相互排他制御、PaddleVL host 起動失敗時フォールバック、再起動/停止ハンドラ、UI保存反映。
- Models/AppSettings.cs — PaddleVlGrpc*/PaddleVl* 設定と Core threshold 設定追加、旧 Vllm* 設定削除。
- Services/SettingsService.cs — VllmApiKey* 保護/復号処理を削除。
- Services/OcrEngine.cs — PaddleVllm 分岐追加、provider解放のため IDisposable 実装。
- Services/PaddleGrpcHost.cs — Core threshold 引数を Python サーバへ送信。
- Services/PaddleGrpcOcrProvider.cs — gRPC channel 解放 (IDisposable) 追加。
- Services/PaddleVlGrpcHost.cs — 新規、PaddleOCR-VL gRPC host 起動/監視/再起動管理。
- Services/PaddleVlGrpcOcrProvider.cs — 新規、PaddleOCR-VL gRPC OCR provider。
- Services/PaddleVllmOcrProvider.cs — 削除（旧HTTP経路撤去）。
- OcrService/server.py — Core threshold CLI引数対応と EnginePool 注入。
- OcrService/ocr_engine.py — threshold をコンストラクタ引数化。
- OcrServiceVL/server.py — 新規、PaddleOCR-VL gRPC サーバ実装。
- OcrServiceVL/ocr_vl_engine.py — CUDA DLL bootstrap と close を追加。
- OcrServiceVL/pyproject.toml — grpcio/grpcio-tools/pillow 依存追加。
- OcrServiceVL/ocr.proto — gRPC 契約ファイルを配置。

### Behavioral Impact
- OCR engine を PaddleOCR-VL (gRPC) に切替えると、ローカル gRPC サーバ経由で OCR を実行する。
- Paddle と PaddleOCR-VL は同時常駐せず、切替時に非選択側ホストを停止する。
- PaddleOCR-VL 停止ボタンはホスト非稼働時は no-op ログのみ。
- max_new_tokens は UI で空欄なら AUTO(None)、数値入力時は 512〜4096 にクランプされる。
- WinRT 選択時は Paddle系ホストを自動停止しない（既存方針維持）。

### Risk & Mitigation
- Risk: Paddle/VL 片側だけ設定変更して再起動せずに稼働継続すると、古いプロセス設定で動作する。
- Mitigation: 設定パネルに「設定を反映して再起動」導線を追加し、保存後の再起動を明示。
- Risk: 旧 settings.json に残る Vllm* 項目との整合。
- Mitigation: AppSettings から該当プロパティを削除して読み込み時に自然無視し、以後保存で収束。

### Tests / Verification
- dotnet build 実行: 成功（警告0/エラー0）。
- python -m py_compile OcrServiceVL/server.py OcrServiceVL/ocr_vl_engine.py OcrService/server.py OcrService/ocr_engine.py 実行: 成功。

**2026-02-13 13:04 (Asia/Taipei) — Overlay adaptive background color plan追加**

### Summary
- オーバレイ背景を画面色へ追従させる実装案を PerFrame → PerBox の段階導入で新規作成した。

### Context / Goal
- 半透明背景を画面背景に近づける設定を追加したい。
- 既存 OverlayBackgroundOpacity を維持しつつ、実装順序を明確化したい。

### Changes
- Doc/Overlay_Adaptive_Background_Color_Plan.md を新規作成。
- PerFrame先行・PerBox後行の段階実装、UI追加、フォールバック、DoDを定義。
- 透明度は既存スライダー値を最終適用する方針を明記。

### Files Touched
- Doc/Overlay_Adaptive_Background_Color_Plan.md — 新規実装案ドキュメントを追加。

### Behavioral Impact
- なし（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: PerBox導入時にちらつき/負荷増が発生する可能性。
- Mitigation: PerFrame先行で段階導入し、PerBoxはサンプル上限・EMA平滑化・フォールバックを前提化。

### Tests / Verification
- Doc/Overlay_Adaptive_Background_Color_Plan.md を UTF-8 出力で作成。
- 内容が「opacity反映」「PerFrame先行」「PerBox後行」を満たすことを目視確認。

**2026-02-13 13:12 (Asia/Taipei) — Overlay背景追従計画に白黒自動文字色を反映**

### Summary
- Overlay_Adaptive_Background_Color_Plan.md に白黒自動文字色切替（補色非採用）方針を追加した。

### Context / Goal
- 背景追従色に対し、可読性確保のため文字色を白黒で自動切替する案を計画へ反映する。
- 補色案は採用せず、まず安定実装を優先する。

### Changes
- 概要/ゴール/非ゴールに「白黒自動切替」「補色非採用」を追記。
- アーキテクチャに OverlayItem.ForegroundColor 追加案を反映。
- データフローに「背景輝度から白黒決定」を追加。
- 実装手順（PerFrame/PerBox）へ白黒判定・フォールバックを追記。
- リスクに「閾値境界での白黒反転」を追加し、ヒステリシス導入を明記。
- DoDに白黒自動切替確認項目を追加。

### Files Touched
- Doc/Overlay_Adaptive_Background_Color_Plan.md — 白黒自動文字色切替方針を反映。

### Behavioral Impact
- なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: 白黒切替閾値付近で反転頻度が高くなる可能性。
- Mitigation: 閾値ヒステリシスを仕様に含め、実装時の反転抑制を前提化。

### Tests / Verification
- Doc/Overlay_Adaptive_Background_Color_Plan.md を UTF-8 で再読し、白黒自動切替・補色非採用が各セクションで一貫していることを確認。

**2026-02-13 13:24 (Asia/Taipei) — Overlay背景追従計画へ座標変換/UI配置/白黒閾値を反映**

### Summary
- Overlay_Adaptive_Background_Color_Plan.md に、座標変換の必須化・MainタブUI配置・白黒閾値初期値を追記した。

### Context / Goal
- レビュー指摘3点（座標系、UI配置、白黒判定仕様）を具体化し、実装時の解釈ブレを防ぐ。

### Changes
- screen -> roi local 座標変換と clamp を仕様へ明記。
- UI配置を Mainタブ > Overlay に固定。
- 白黒判定の初期ヒステリシス閾値を L<=0.45:white / L>=0.55:black として明記（初期はUI非露出）。
- リスク/DoD/影響範囲へ上記内容を反映。

### Files Touched
- Doc/Overlay_Adaptive_Background_Color_Plan.md — 指定3点を反映。

### Behavioral Impact
- なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: 実装時に座標系の取り扱いを誤り、背景推定が不安定になる可能性。
- Mitigation: 変換手順と clamp 条件を実装手順・DoDへ明記して固定。

### Tests / Verification
- Doc/Overlay_Adaptive_Background_Color_Plan.md を UTF-8 で再読し、3点（推奨反映）が全セクションで整合していることを確認。

**2026-02-13 13:34 (Asia/Taipei) — Overlay背景追従計画をPerBox専用方針へ更新**

### Summary
- Overlay_Adaptive_Background_Color_Plan.md を PerFrame 非採用・PerBox専用に再構成した。

### Context / Goal
- ゲーム画面では背景色が混在しやすく、PerFrame代表色が可読性を悪化させる懸念がある。
- デフォルト半透明黒を維持しつつ、必要時のみ PerBox 自動推定を使う方針へ統一する。

### Changes
- 文書全体を「PerBoxのみ実装」に更新し、PerFrame関連の設計/手順/DoDを削除。
- ゴールを ON/OFF のみへ簡素化し、UIのモード選択を廃止。
- 非ゴールに PerFrame 非採用を明記。
- フォールバック方針を「推定失敗時は既存背景（半透明黒）」へ統一。
- 白黒文字色自動切替（0.45/0.55ヒステリシス）方針を維持。

### Files Touched
- Doc/Overlay_Adaptive_Background_Color_Plan.md — PerBox専用方針へ更新。

### Behavioral Impact
- なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: PerBoxサンプリングは枠数比例で負荷が増える。
- Mitigation: サンプル数上限・早期打切り・失敗時フォールバックを前提化。

### Tests / Verification
- Doc/Overlay_Adaptive_Background_Color_Plan.md を UTF-8 で再読し、PerFrame記述が排除されていることを確認。

**2026-02-13 13:44 (Asia/Taipei) — SceneChange文字/ブロック変化トリガー実装案を追加**

### Summary
- 自動翻訳/自動非表示を「文字・ブロック変化時のみ発火」にする二段ゲート実装案を新規作成した。

### Context / Goal
- pHash差分のみだと背景変化で誤発火しやすいため、OCRスナップショット差分を追加して意味的変化で発火制御したい。
- Auto-hide / Auto-translate の両方に共通適用できる設計を定義する。

### Changes
- Doc/SceneChange_TextBlockChanged_Trigger_Plan.md を新規作成。
- Stage A(pHash) + Stage B(OCR semantic diff) の二段ゲート方針を定義。
- block対応付け（IoU）・本文変化・追加/削除をトリガー条件として明記。
- settings.json向けの最小パラメータ（UI非露出）を追加提案。
- 既存 pending-drain / 排他仕様との整合を明記。

### Files Touched
- Doc/SceneChange_TextBlockChanged_Trigger_Plan.md — 新規実装案ドキュメントを追加。

### Behavioral Impact
- なし（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: Stage B追加で候補時OCRコストが増える。
- Mitigation: Stage A通過時のみStage Bを実行し、閾値とConfirmTicksで制御する。

### Tests / Verification
- Doc/SceneChange_TextBlockChanged_Trigger_Plan.md を UTF-8 で作成。
- 内容が「Auto-hide/Auto-translate両方に適用」「文字/ブロック変化時のみ発火」を満たすことを目視確認。

**2026-02-13 13:56 (Asia/Taipei) — SceneChange文字ブロック変化計画へStageB OCR再利用方針を反映**

### Summary
- SceneChange_TextBlockChanged_Trigger_Plan.md に、Stage B OCR結果を翻訳へ再利用する低遅延方針を追記した。

### Context / Goal
- 自動翻訳確定時の遅延を減らすため、Stage Bで取得したOCR payloadを再OCRせず翻訳入力へ流用したい。
- 再利用時の安全性を担保するフォールバック条件も文書化する。

### Changes
- ゴールへ「Stage B OCR再利用による遅延低減」を追加。
- SceneTextSnapshot に ReadingUnits / CapturedAtUtc / SnapshotSignature を追加提案。
- Auto-translate整合章に「再利用優先 + 不整合時RunOnceフォールバック」を追加。
- 判定フロー/実装手順/可観測性/リスク/DoDに再利用経路を反映。
- 判定フローの番号不整合（6重複）を修正。

### Files Touched
- Doc/SceneChange_TextBlockChanged_Trigger_Plan.md — Stage B OCR再利用方針を反映。

### Behavioral Impact
- なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: 古いpayloadを誤再利用して画面とずれた翻訳になる可能性。
- Mitigation: TTL（初期500ms）と設定署名一致チェックで再利用可否を厳格化し、不一致時は従来RunOnceへフォールバック。

### Tests / Verification
- Doc/SceneChange_TextBlockChanged_Trigger_Plan.md を UTF-8 で再読し、再利用条件・フォールバック条件・DoDが整合していることを確認。

**2026-02-13 14:19 (Asia/Taipei) — SceneChange文字ブロック計画の推奨4点反映を補正**

### Summary
- 指定方針（同品質OCR採用 + 推奨3点）を反映し、文書内の発火条件/手順番号の整合を補正した。

### Context / Goal
- 自己レビューで挙がった不足（ConfirmTicks適用点、Signature範囲、前提文言）を解消し、実装時の解釈ブレを防ぐ。
- Stage B OCR再利用方針を品質劣化なく成立させる。

### Changes
- 前提文言を pending-drain 現行仕様（実行中は破棄でなく集約）へ更新。
- Stage B の OCR取得を「本番と同一品質経路」で固定。
- SnapshotSignature 対象を preprocess / writing-mode / line-merge まで拡張。
- OnAutoHideTick 判定フローへ SceneSemanticRequireConfirmTicks を明示適用。
- 最終発火条件に streak >= SceneSemanticRequireConfirmTicks を明記。
- 実装手順の Step 番号重複を修正（Step 1..7）。

### Files Touched
- Doc/SceneChange_TextBlockChanged_Trigger_Plan.md — 推奨4点と整合補正を反映。

### Behavioral Impact
- なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: 文書上の発火条件と手順の不整合で実装が分岐する可能性。
- Mitigation: 最終発火条件と手順番号を補正し、判定フローを一意化。

### Tests / Verification
- Doc/SceneChange_TextBlockChanged_Trigger_Plan.md を UTF-8 で再読し、4点反映と番号整合を確認。
**2026-02-13 14:53 (Asia/Taipei) — SceneChange TextBlockChanged trigger 実装**

### Summary
- Scene-change watcher に semantic gate（二段判定）を実装し、Stage B OCR payload の auto-translate 再利用経路を追加した。

### Context / Goal
- 画面差分だけで誤発火するケースを抑え、文字/ブロック変化時のみ Auto-hide / Auto-translate を発火させる。
- Stage B で取得した OCR 結果を翻訳に再利用して、確定後の再OCR遅延を削減する。

### Changes
- Scene semantic 用モデルを追加（`SceneTextBlock` / `SceneTextSnapshot`）。
- `SceneTextSnapshotService` を新規追加し、監視用 OCR snapshot 取得・比較（IoUマッチ + text差分）を実装。
- `MainWindow.xaml.cs` に Stage A(pHash)→Stage B(semantic) の判定フローを追加。
- `SceneSemanticRequireConfirmTicks` による streak 判定を追加。
- auto-translate pending に semantic payload を保持し、drain 時に payload を引き継ぐよう変更。
- `RunOnceAsync` に payload 再利用判定（TTL/署名一致）を追加し、有効時は precomputed reading units 経路を使用。
- `PipelineOrchestrator` に `RunWithReadingUnitsAsync(...)` を追加し、OCRスキップ時の翻訳/overlay 更新を実装。
- `AppSettings` に semantic gate 設定を追加（Enable/IoU/MinChars/ConfirmTicks）し、`MainWindow` で normalize を追加。

### Files Touched
- `MainWindow.xaml.cs` — semantic gate 判定、payload再利用、pending-drain連携、設定normalizeを追加。
- `Models/AppSettings.cs` — scene semantic gate 設定項目を追加。
- `Services/PipelineOrchestrator.cs` — precomputed reading units 実行経路を追加。
- `Services/SceneTextSnapshotService.cs` — snapshot 取得/比較ロジックを新規実装。
- `Models/SceneTextBlock.cs` — semantic block モデルを新規追加。
- `Models/SceneTextSnapshot.cs` — snapshot モデルを新規追加。

### Behavioral Impact
- `EnableSceneChangeSemanticGate=true` 時、Auto-hide / Auto-translate は Stage A + Stage B 通過時のみ発火する。
- Stage B payload が新鮮かつ設定署名一致の場合、auto-translate 実行で再OCRを省略する。
- payload が古い/不一致/空の場合は従来の `RunOnceAsync` へ自動フォールバックする。

### Risk & Mitigation
- Risk: semantic snapshot 比較の閾値が厳しすぎる/緩すぎると未発火または誤発火が起こる。
- Mitigation: `SceneSemanticBlockIouThreshold` / `SceneSemanticMinChars` / `SceneSemanticRequireConfirmTicks` を settings で調整可能にした。
- Risk: payload 再利用で設定変更直後の不整合が起こる。
- Mitigation: 500ms TTL + `SnapshotSignature` 一致条件で再利用を制限し、失敗時は full OCR にフォールバック。

### Tests / Verification
- `dotnet build` を実行し、0 error / 0 warning でビルド成功を確認。
- コンパイル確認で新規追加型・新規メソッド参照が解決することを確認。
**2026-02-14 00:20 (Asia/Taipei) — Layer1 Application: Run/Hotkey/Log Controller 分離**

### Summary
- MainWindow から Run/Hotkey/Logging の制御責務を専用 Controller へ分離し、UI 側を委譲中心へ整理した。

### Context / Goal
- Layer1 計画に沿って MainWindow.xaml.cs の責務集中を段階的に解消する。
- 既存のホットキー動作、Run 実行、ログ表示の挙動互換を維持したまま境界を導入する。

### Changes
- IMainWindowViewBridge を新設し、Controller から UI 更新する最小橋渡し API を定義。
- MainWindowRunCoordinator を新設し、Run 排他・busy 表示・semantic payload 再利用判定・pending drain 呼び出しを移管。
- HotkeyController を新設し、hotkey 登録/更新/重複検知/ロールバックを MainWindow から分離。
- UiLogController を新設し、ログキューと flush タイマ制御を MainWindow から分離。
- MainWindow.xaml.cs を更新し、Run/Hotkey/Log の既存処理を各 Controller へ委譲する構成に変更。

### Files Touched
- MainWindow.xaml.cs — Bridge 実装、Controller 注入、Run/Hotkey/Log の委譲化、旧 private state/処理の削減。
- Services/Application/IMainWindowViewBridge.cs — UI ブリッジの最小インターフェースを新規追加。
- Services/Application/MainWindowRunCoordinator.cs — Run 実行制御を新規追加。
- Services/Application/HotkeyController.cs — Hotkey 制御を新規追加。
- Services/Application/UiLogController.cs — UI ログバッファ制御を新規追加。

### Behavioral Impact
- ユーザー操作上の仕様変更はなし（既存 hotkey / run / ログ表示挙動を維持）。
- 内部的に Run 実行状態参照が MainWindow フィールドから MainWindowRunCoordinator 経由へ移行した。

### Risk & Mitigation
- Risk: Controller 化に伴う状態移管漏れで Run/Hotkey/Log のタイミングがずれる可能性。
- Mitigation: 既存メソッドの実行順を維持し、dotnet build で参照整合を確認。Hotkey 登録失敗時ロールバック（WHY コメント）を維持。

### Tests / Verification
- dotnet build Hotkey-Translator.sln を実行し、0 warning / 0 error を確認。
**2026-02-14 00:49 (Asia/Taipei) — Layer1 Application: Scene/Settings Controller 分離完了**

### Summary
- Layer1 残タスクとして Scene-change watcher と Settings 調停を MainWindow から専用 Controller へ分離した。

### Context / Goal
- Doc/Refactoring_Layer1_Application_Plan.md の未完了項目（Scene watcher 制御、Settings 正規化/保存調停）を実装し、MainWindow を委譲中心へ寄せる。
- 既存の auto-hide / auto-translate / 設定保存 / hotkey 動作互換を維持する。

### Changes
- SceneChangeController を新規追加し、watcher lifecycle・Stage A/B 判定・pending/drain・auto-hide/auto-translate 発火を移管。
- SettingsUiController を新規追加し、Normalize* 群と保存フロー調停（UI入力反映→正規化→反映→保存→hotkey/watcher更新）を移管。
- MainWindow は ISettingsUiBridge を実装し、settings 保存は SaveSettingsAsync -> SettingsUiController.SaveFromUiAsync に委譲。
- MainWindow から watcher 状態フィールドと Normalize* 実装群を削除し、Scene/Settings の責務を縮小。

### Files Touched
- MainWindow.xaml.cs — Scene/Settings の委譲配線、bridge 実装、旧 watcher/normalize 実装削除、保存処理の分解。
- Services/Application/SceneChangeController.cs — watcher/semantic gate/pending-drain の専用制御を新規実装。
- Services/Application/SettingsUiController.cs — settings 正規化と保存調停を新規実装（ISettingsUiBridge 含む）。

### Behavioral Impact
- 既存ユーザー動作（F5/F8/F9/F10/F11/F6/F7、設定保存、scene auto-translate/auto-hide）は互換を維持。
- 内部実装は MainWindow 直持ち状態から Controller 内部状態へ移管され、責務境界が明確化された。

### Risk & Mitigation
- Risk: watcher/state 移管で pending run や semantic streak のタイミングが変わる可能性。
- Mitigation: 既存ロジックをそのまま移植し、Run 排他判定と pending-drain 経路を維持。
- Risk: settings 保存中に UI イベントが再入して二重保存になる可能性。
- Mitigation: SettingsUiController.SaveFromUiAsync で IsApplyingSettings を明示制御し再入を抑制。

### Tests / Verification
- dotnet build Hotkey-Translator.sln を実行し、0 warning / 0 error を確認。
- 行数確認: MainWindow.xaml.cs は 3049 -> 2616 行へ減少。
**2026-02-14 00:58 (Asia/Taipei) — dotnet run 起動時 NullReference 修正**

### Summary
- InitializeComponent 中の UI イベント先行発火で SaveSettingsAsync が落ちる問題を、SettingsUiController 初期化順の修正で解消した。

### Context / Goal
- dotnet run 実行時にエラーログが見えないまま起動失敗するとの報告があり、実際は起動直後の NullReferenceException が原因だった。
- 既存挙動を変えずに起動時クラッシュを止めることを目的とする。

### Changes
- MainWindow コンストラクタで _settingsUiController の生成を InitializeComponent() より前に移動。
- これにより、XAML 初期化中に設定系イベントが先行発火しても SaveSettingsAsync が null 参照しないようにした。

### Files Touched
- MainWindow.xaml.cs — _settingsUiController の初期化順を変更（起動時 NRE 回避）。

### Behavioral Impact
- 起動時の NullReferenceException が解消され、dotnet run でアプリ起動が継続可能になった。
- それ以外のユーザー機能挙動には変更なし。

### Risk & Mitigation
- Risk: 初期化順変更で他依存との順序問題が発生する可能性。
- Mitigation: 依存を持つ初期化は従来順を維持し、SettingsUiController のみ前倒しして最小差分で対応。

### Tests / Verification
- dotnet run --project Hotkey-Translator.csproj を実行し、例外なしで終了コード 0 を確認。
- dotnet build Hotkey-Translator.sln を実行し、0 warning / 0 error を確認。
**2026-02-14 01:11 (Asia/Taipei) — MVVM 実装計画ドキュメント追加**

### Summary
- MVVM 移行の実装計画（段階移行・責務境界・DoD）を新規ドキュメントとして追加した。

### Context / Goal
- Doc/MVVMPlan.md を補強し、実装順序・リスク制御・完了条件を具体化した別計画を用意する。
- 既存 Controller 分離済み構成を前提に、無理のない段階移行計画を明文化する。

### Changes
- Doc/MVVM_Implementation_Plan.md を新規作成。
- ViewModel 分割、保存 debounce、Global Hotkey 境界、段階移行 Step、DoD、Migration/Open Questions を明記。

### Files Touched
- Doc/MVVM_Implementation_Plan.md — MVVM 移行の具体実装計画を新規追加。

### Behavioral Impact
- なし（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: 計画が現実実装と乖離する可能性。
- Mitigation: 既存コードベース（Controller 分離済み）を前提に、段階移行と検証条件を具体化。

### Tests / Verification
- Doc/MVVM_Implementation_Plan.md を UTF-8 で再読し、構成・内容を確認。
**2026-02-14 01:32 (Asia/Taipei) — MVVM 実装計画の段階実装（Command化 + 保存debounce）**

### Summary
- `Doc/MVVM_Implementation_Plan.md` に沿って、主要操作の `ICommand` 化と設定保存の `debounce` 実装を本体へ反映した。

### Context / Goal
- `MainWindow.xaml.cs` のイベント駆動依存を段階的に減らし、MVVM 移行を進める。
- 設定変更時の即時連続保存を抑制し、明示操作時のみ即時保存する保存戦略へ揃える。

### Changes
- `CommunityToolkit.Mvvm` を導入し、`MainWindowViewModel` / `SettingsViewModel` / `RuntimeStatusViewModel` を追加。
- `MainWindow` で `DataContext` を ViewModel に配線し、`Select ROI` / `Swap languages` / `Translation priority Up/Down` を `Command` バインドへ移行。
- `SettingsChangeScheduler` を `MainWindow` で実運用化し、設定変更イベントの保存を `RequestSave()`（debounce）へ切替。
- 明示的な反映操作（Llama Reload/Restart、OCR Host 再起動など）は `SaveSettingsImmediatelyAsync()` を使うように分離。
- `RunOnceAsync` 前に `FlushPendingSettingsSaveAsync()` を実行し、未反映 UI 設定を先に反映するよう調整。

### Files Touched
- `Hotkey-Translator.csproj` — `CommunityToolkit.Mvvm` パッケージ参照を追加。
- `MainWindow.xaml` — 主要ボタンの `Click` を `Command` バインドへ変更。
- `MainWindow.xaml.cs` — ViewModel 配線、保存 `debounce` 戦略、即時保存経路、Run 前 flush を実装。
- `ViewModels/MainWindowViewModel.cs` — 画面コマンド公開用 ViewModel を追加。
- `ViewModels/SettingsViewModel.cs` — 設定保存スケジューラ操作の窓口を追加。
- `ViewModels/RuntimeStatusViewModel.cs` — ランタイム状態の基礎 ViewModel を追加。

### Behavioral Impact
- スライダー/チェックボックス等の連続変更で、保存が短時間に集約される。
- Llama/OCR 再起動操作は従来どおり即時反映される。
- ROI選択・言語入替・優先度上下は `Command` 経由で動作する。

### Risk & Mitigation
- Risk: `DataContext` 導入で既存 UI 連携が壊れる可能性。
- Mitigation: 段階移行として対象を主要4操作に限定し、既存 Controller と UI 名称参照は維持した。
- Risk: debounce による保存タイミング差で操作直後の反映が遅れる可能性。
- Mitigation: 明示操作は即時保存、Run 実行前は flush を強制して整合性を確保した。

### Tests / Verification
- `dotnet build` を実行し、`0 warning / 0 error` を確認。
- `dotnet run` は未実施（GUI 常駐アプリのため本セッションではビルド検証まで）。
**2026-02-14 02:09 (Asia/Taipei) — dotnet run 起動時 NRE の初期化順修正**

### Summary
- `dotnet run` で無ログに見えて落ちる問題を、`SettingsChangeScheduler` の初期化順を修正して解消した。

### Context / Goal
- 起動直後に未処理例外でアプリが終了し、アプリ内ログが出ないように見える問題が発生していた。
- MVVM段階移行で追加した設定保存 debounce を維持しつつ、起動時クラッシュを防ぐことを目的とした。

### Changes
- `MainWindow` コンストラクタで `_settingsChangeScheduler` の生成を `InitializeComponent()` より前へ移動。
- 初期化順の意図を示す `WHY` コメントを追加し、XAML初期化中イベント先行発火への対策を明示。

### Files Touched
- `MainWindow.xaml.cs` — `SettingsChangeScheduler` 初期化順を前倒しし、起動時 NRE を回避。

### Behavioral Impact
- `dotnet run` 実行時に起動直後の `NullReferenceException` で落ちる挙動が解消。
- 設定保存 debounce の挙動自体は維持。

### Risk & Mitigation
- Risk: 初期化順変更で副作用が出る可能性。
- Mitigation: 移動対象を scheduler 生成のみに限定し、既存の依存配線・ロジックは変更しない最小差分で対応。

### Tests / Verification
- `dotnet run --project Hotkey-Translator.csproj` をプロセス監視で5秒実行し、即時クラッシュしないことを確認（`RUNNING_OK_NO_EARLY_CRASH`）。
- `dotnet build` を単独で再実行し、`0 warning / 0 error` を確認。
**2026-02-14 02:17 (Asia/Taipei) — MVVM残タスク実装（Runtime Command化 + 値表示Binding化）**

### Summary
- `Doc/MVVM_Implementation_Plan.md` の残実装として、Runtime 操作の Command 化とスライダー値表示ロジックの XAML Binding 移行を実施した。

### Context / Goal
- 主要機能の Command 化を拡張し、`MainWindow.xaml.cs` のイベント依存をさらに削減する。
- `Update...Value` 系の手動UI同期を廃止し、表示更新を XAML バインディングへ寄せる。

### Changes
- `MainWindowViewModel` に Runtime 操作用コマンド（Llama reload/restart/stop、Paddle host restart/stop）を追加。
- `MainWindow.xaml` の Runtime 操作ボタンを `Click` から `Command` バインディングへ移行。
- スライダー横の値表示（OCR/Overlay/Scene/Paddle）を `ElementName` + `StringFormat` バインディングへ変更。
- `MainWindow.xaml.cs` から不要になった `Update...Value` / `UpdateSceneChangeWatchValues` を削除し、関連呼び出しも除去。
- `OnRunOnce` など不要イベントハンドラを削除し、Runtime ハンドラをコマンド向け `Task`/`Action` メソッドへ置換。
- View 境界責務を明示するため `Doc/MainWindow_View_Boundary.md` を追加し、`MainWindow` クラスに `NOTE` コメントを追記。

### Files Touched
- `ViewModels/MainWindowViewModel.cs` — Runtime 操作用 `ICommand` 群を追加。
- `MainWindow.xaml` — Runtime ボタンの `Command` 化と値表示 `Binding` 化を実施。
- `MainWindow.xaml.cs` — Runtime メソッドのコマンド化対応、不要UI同期メソッド削除、View責務コメント追加。
- `Doc/MainWindow_View_Boundary.md` — View に残す責務境界を文書化。

### Behavioral Impact
- Runtime 操作（Llama/Paddle）の UI 操作が ViewModel コマンド経由で実行される。
- スライダー値表示はコードビハインド更新ではなくバインディングで常時同期される。
- 設定保存 debounce / 即時保存の既存戦略は維持される。

### Risk & Mitigation
- Risk: Command 化で実行経路が変わり、既存ボタン操作が効かなくなる可能性。
- Mitigation: 既存ロジック本体は再利用し、入口のみ `Click` から `Command` へ移行。ビルドと起動確認を実施。
- Risk: XAML バインディング式の誤りで表示欠落が起こる可能性。
- Mitigation: 既存 `x:Name` を維持した `ElementName` バインディングを採用し、`dotnet run` 起動時クラッシュがないことを確認。

### Tests / Verification
- `dotnet build` 実行: `0 warning / 0 error`。
- `dotnet run --project Hotkey-Translator.csproj` を5秒監視で実行し、`RUNNING_OK_NO_EARLY_CRASH` を確認。
**2026-02-14 02:31 (Asia/Taipei) — MVVM残タスク実装（Settings TwoWay化の拡張）**

### Summary
- 言語設定と主要スライダー設定を `SettingsViewModel` の TwoWay Binding に移し、`MainWindow.xaml.cs` の UIイベント依存をさらに削減した。

### Context / Goal
- `Doc/MVVM_Implementation_Plan.md` の残項目である Settings 双方向同期の適用範囲を拡張し、`ApplyUiInputToSettings` の直接UI読み取り依存を段階的に解体する。
- `On...ValueChanged` / `OnLanguageSelectionChanged` 依存を減らし、ViewModel 経由の保存トリガへ統一する。

### Changes
- `SettingsViewModel` を `ObservableObject` 化し、主要設定値（OCR/Overlay/Scene/Paddle/言語）のプロパティを追加。
- `SettingsViewModel.LoadFrom(AppSettings)` / `ApplyTo(AppSettings)` を追加し、読み込み反映と保存反映を ViewModel 経由に変更。
- `SettingsViewModel` のプロパティ変更時に `debounce save` を自動要求する仕組みを追加（`_suspendAutoSave` でロード時保存を抑制）。
- `MainWindow.xaml` の Source/Target 言語UIを `SelectedValue` / `Text` の TwoWay Binding 化し、custom入力欄の表示制御を `DataTrigger` 化。
- 主要スライダー（OCR/Overlay/Scene/Paddle）の `Value` を `Settings.*` へ TwoWay Binding 化し、`ValueChanged` ハンドラをXAMLから除去。
- `MainWindow.xaml.cs` で言語選択とスライダー更新のイベントハンドラ・補助メソッドを削除し、`SwapLanguages` を ViewModel プロパティ交換ベースへ変更。
- `ApplySettingsToUi` は `Settings.LoadFrom(settings)`、`ApplyUiInputToSettings` は `Settings.ApplyTo(settings)` を使うように更新。

### Files Touched
- `ViewModels/SettingsViewModel.cs` — 設定プロパティ、Load/Apply、自動保存トリガ、言語解決ロジックを追加。
- `MainWindow.xaml` — 言語UIと主要スライダーを TwoWay Binding 化、Visibility DataTrigger 化。
- `MainWindow.xaml.cs` — 言語/スライダー関連の旧イベント・補助メソッドを削除し、ViewModel連携に置換。

### Behavioral Impact
- 言語設定（標準タグ/Custom）と主要スライダー設定が ViewModel を単一の更新経路として扱うようになった。
- スライダー変更時の保存はイベントハンドラではなく ViewModel プロパティ変更から `debounce` で発火する。
- 言語Custom欄の表示はコードビハインドではなく XAML `DataTrigger` で制御される。

### Risk & Mitigation
- Risk: Binding 導入で初期値反映時に不要保存が走る可能性。
- Mitigation: `SettingsViewModel` に `_suspendAutoSave` を導入し、`LoadFrom` 中の保存要求を抑制。
- Risk: 旧イベント削除で反映漏れが発生する可能性。
- Mitigation: `ApplyTo` に保存対象を明示し、`dotnet build` と起動スモークで回帰確認。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj` 実行: `0 warning / 0 error`。
- `dotnet run --project Hotkey-Translator.csproj` を5秒監視で実行し、`RUNNING_OK_NO_EARLY_CRASH` を確認。
**2026-02-14 02:45 (Asia/Taipei) — MVVM次段階実装（主要チェック/コンボのViewModel移行）**

### Summary
- Capture/OCR/Scene/Translation の主要チェックボックス・コンボを `SettingsViewModel` の TwoWay Binding に移し、`ApplyUiInputToSettings` の直接UI読取りをさらに削減した。

### Context / Goal
- `Doc/MVVM_Implementation_Plan.md` の Step 3/7 を継続し、Settings 双方向移行を拡張する。
- 既存の `OnSettingChanged` に依存していた設定群を ViewModel 主導の保存トリガへ寄せ、コードビハインド責務を縮小する。

### Changes
- `SettingsViewModel` に主要設定プロパティを追加（CaptureMode/Provider、ROI、OCR engine、各種フラグ、Paddle model/pipeline、Scene mode、DeepL/Gemini、Llama enable など）。
- `SettingsViewModel.LoadFrom` / `ApplyTo` を拡張し、上記設定を `AppSettings` と相互マッピング。
- SceneChange の auto-hide / auto-translate 相互排他を ViewModel 側へ移植（`_suspendSceneModeSync` 付き）。
- `MainWindow.xaml` の主要チェックボックス・コンボを `Click/SelectionChanged` から `IsChecked/SelectedValue` の TwoWay Binding へ移行。
- `MainWindow.xaml.cs` の `ApplySettingsToUi` / `ApplyUiInputToSettings` から移行済み項目の直接操作を削除し、`Settings.LoadFrom/ApplyTo` 中心に整理。
- 不要になった `GetCaptureMode` / `GetCaptureProviderKind` / `GetOcrEngineKind` / `GetVerticalModeOverride` などの補助メソッドを削除。
- `OnSettingChanged` は未移行コントロール（Hotkey/CTranslate/LlamaModel）向けの汎用保存トリガとして簡素化。

### Files Touched
- `ViewModels/SettingsViewModel.cs` — 主要設定プロパティ追加、Load/Apply拡張、相互排他ロジック追加。
- `MainWindow.xaml` — 主要チェック/コンボを TwoWay Binding 化。
- `MainWindow.xaml.cs` — 直接UI読取りの削減、不要変換メソッド削除、保存経路整理。

### Behavioral Impact
- 主要設定変更時の保存は ViewModel のプロパティ変更経由で `debounce` 実行される。
- SceneChange の auto-hide / auto-translate は UI イベントではなく ViewModel で排他制御される。
- Hotkey 設定や一部テキスト入力は従来どおり段階移行対象外として維持。

### Risk & Mitigation
- Risk: Binding 置換により初期化時・保存時の同期ズレが起きる可能性。
- Mitigation: `LoadFrom` の `_suspendAutoSave` と `ApplyTo` の単一反映点を維持し、build/runスモークで起動回帰を確認。
- Risk: 相互排他ロジックの再入で意図しない再保存が発生する可能性。
- Mitigation: `_suspendSceneModeSync` で再入を抑制し、最終的に `RequestSaveOnValueChange` を1経路に集約。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj` 実行: `0 warning / 0 error`。
- `dotnet run --project Hotkey-Translator.csproj` を5秒監視で実行し、`RUNNING_OK_NO_EARLY_CRASH` を確認。
**2026-02-14 02:58 (Asia/Taipei) — MVVM次段階実装（設定入力の単一路化とLlamaモデル選択Binding化）**

### Summary
- ApplyUiInputToSettings の重複パースを削減し、SettingsViewModel.ApplyTo を設定入力の主経路に統一した。

### Context / Goal
- Doc/MVVM_Implementation_Plan.md の Step 3/7 を進め、MainWindow.xaml.cs の設定同期責務をさらに縮小する。
- 保存正規化後の UI 反映を ViewModel 再読込で一貫化し、手動 TextBox 再代入を減らす。

### Changes
- MainWindow.xaml.cs の ApplyUiInputToSettings から、MVVM移行済み項目（Paddle/Llama/閾値群/DeepL endpoint）の直接読み取り処理を削除。
- ApplyRuntimeStateAfterSave で Settings.LoadFrom(settings) を実行し、正規化値を Binding 経由で UI に再反映する構成へ変更。
- SettingsViewModel に LlamaSelectedModelFileName を追加し、LoadFrom / ApplyTo / 保存トリガに組み込み。
- MainWindow.xaml の LlamaModelBox を SelectedValue TwoWay Binding 化し、SelectionChanged="OnSettingChanged" 依存を削減。
- ReloadLlamaModelOptions で、保存前の ViewModel 選択値も考慮してモデル再読込時の選択維持を強化。

### Files Touched
- MainWindow.xaml.cs — 設定入力経路の整理、保存後同期の ViewModel 化、Llamaモデル再読込選択ロジック調整。
- MainWindow.xaml — LlamaModelBox を Settings.LlamaSelectedModelFileName にバインド。
- ViewModels/SettingsViewModel.cs — Llamaモデル選択プロパティと AppSettings 双方向マッピングを追加。

### Behavioral Impact
- 設定保存時の反映元がより明確になり、保存正規化後は ViewModel 再読込で UI 表示が自動整合する。
- Llama モデル選択は ViewModel 経由で保存トリガされ、コードビハインドのイベント依存が減少する。

### Risk & Mitigation
- Risk: 保存後に ViewModel を再読込することで未保存編集中の表示が上書きされる可能性。
- Mitigation: 保存処理直後のみ再読込し、LoadFrom の _suspendAutoSave で再保存ループを抑制。
- Risk: Llama モデル再読込時に選択が意図せず切り替わる可能性。
- Mitigation: ViewModel の現在選択値を優先し、候補欠落時は既存の fallback/missing 表示ロジックを維持。

### Tests / Verification
- dotnet build Hotkey-Translator.csproj 実行:   warning / 0 error。
- dotnet run --project Hotkey-Translator.csproj --no-build を6秒監視で実行し、RUNNING_OK_NO_EARLY_CRASH を確認。
**2026-02-14 03:08 (Asia/Taipei) — MVVM次段階実装（Hotkey/Password のイベント依存撤去）**

### Summary
- Hotkey と API Key 入力を SettingsViewModel の TwoWay Binding に移し、MainWindow.xaml.cs のイベント駆動同期を削減した。

### Context / Goal
- Doc/MVVM_Implementation_Plan.md の Step 3/4/7 を前進させ、OnSettingLostFocus / OnHotkeySelectionChanged 依存を解消する。
- 設定反映経路を SettingsViewModel.LoadFrom/ApplyTo に集約し、View の責務を軽くする。

### Changes
- MainWindow.xaml の PasswordBox 2箇所を BoundPassword TwoWay Binding 化し、LostFocus イベントを削除。
- Hotkey 9系統（キー + Ctrl/Alt/Shift）をすべて Settings.* バインドへ移行し、SelectionChanged/Click イベントを削除。
- UI/PasswordBoxAssistant.cs を追加し、PasswordBox の双方向バインドを可能化。
- SettingsViewModel に API Key/Hotkey 用プロパティ群を追加し、LoadFrom/ApplyTo へ取り込み。
- SettingsViewModel に Hotkey 修飾子の parse/build ロジックを追加し、AppSettings との文字列表現変換を集約。
- MainWindow.xaml.cs から Hotkey/Password の手動同期メソッドを削除（ApplyHotkeySettingsToUi, ApplyHotkeySettingsFromUi, OnSettingLostFocus, OnHotkeySelectionChanged など）。

### Files Touched
- MainWindow.xaml — PasswordBox/Hotkey UI を ViewModel バインドへ変更。
- ViewModels/SettingsViewModel.cs — API Key/Hotkey プロパティ、Load/Apply マッピング、修飾子変換を追加。
- MainWindow.xaml.cs — 削除済みイベントハンドラ・手動同期ロジックを整理。
- UI/PasswordBoxAssistant.cs — PasswordBox 向け添付プロパティを新規追加。

### Behavioral Impact
- Hotkey 設定変更は ViewModel 経由で debounce 保存され、保存後の hotkey 再登録フローにそのまま連結される。
- DeepL/Gemini API key は PasswordBox のバインディング経由で AppSettings に反映される。
- OnSettingChanged は CTranslate2 の未移行 UI 向けにのみ残る。

### Risk & Mitigation
- Risk: Password を文字列バインドすることでメモリ上の平文保持時間が増える。
- Mitigation: PasswordBoxAssistant に SECURITY コメントを明示し、同期用途に限定して短いライフサイクルで扱う。
- Risk: Hotkey 修飾子の文字列表現差異で既存値が崩れる可能性。
- Mitigation: Ctrl/Control/Alt/Shift を許容する parser と、未指定時 None へ戻す builder を実装。

### Tests / Verification
- dotnet build Hotkey-Translator.csproj 実行:   warning / 0 error。
- dotnet run --project Hotkey-Translator.csproj --no-build を6秒監視で実行し、RUNNING_OK_NO_EARLY_CRASH を確認。
**2026-02-14 11:23 (Asia/Taipei) — MVVM次段階実装（カテゴリ表示Binding化とScene/SmallBox UI状態のXAML移行）**

### Summary
- Settingsカテゴリ切替と一部UI有効/無効制御をコードビハインドからXAML Bindingへ移し、MainWindow.xaml.cs の表示制御責務を削減した。

### Context / Goal
- Doc/MVVM_Implementation_Plan.md の Step 5/7 を継続し、OnSettingsCategoryChanged 系のイベント駆動表示更新を解消する。
- UpdateSmallBoxReadabilityControls / UpdateSceneChangeControls を廃止し、XAMLトリガで状態制御する。

### Changes
- MainWindowViewModel に SelectedSettingsCategoryIndex を追加。
- MainWindow.xaml の SettingsCategoryList を SelectedIndex TwoWay Binding 化し、カテゴリ選択イベント依存を削除。
- 各 Settings パネル（OCR/Paddle/Translation/Hotkeys）の Visibility を IntToVisibilityConverter で Binding 制御に変更。
- SmallTextThresholdSlider の IsEnabled と値表示色を XAML Binding/DataTrigger で制御。
- Scene Change の各スライダー IsEnabled を BooleanOrMultiConverter（auto-hide OR auto-translate）で制御し、値表示色も DataTrigger 化。
- MainWindow.xaml.cs から EnsureSettingsCategorySelection / OnSettingsCategoryChanged / UpdateSettingsCategoryPanels を削除。
- MainWindow.xaml.cs から UpdateSmallBoxReadabilityControls / UpdateSceneChangeControls を削除し、関連呼び出しも整理。

### Files Touched
- ViewModels/MainWindowViewModel.cs — SelectedSettingsCategoryIndex を追加。
- MainWindow.xaml — Settingsカテゴリ表示と Scene/SmallBox UI状態制御を Binding 化。
- MainWindow.xaml.cs — 旧カテゴリ表示更新メソッドと SmallBox/Scene 制御メソッドを削除。
- Converters/IntToVisibilityConverter.cs — カテゴリIndex→Visibility変換を追加。
- Converters/BooleanOrMultiConverter.cs — Scene watcher有効判定（OR）用のMultiValueConverterを追加。

### Behavioral Impact
- Settingsタブのカテゴリ切替は ViewModel の SelectedSettingsCategoryIndex で状態管理される。
- Scene Change と SmallBox の有効/無効表示が保存時の手動再計算なしで即時UI反映される。
- MainWindow.xaml.cs の UI 表示更新ロジックが縮小され、View責務が明確化される。

### Risk & Mitigation
- Risk: 変換ロジック誤りでパネル表示が崩れる可能性。
- Mitigation: IntToVisibilityConverter を単純な一致判定に限定し、Binding先を SelectedSettingsCategoryIndex に統一。
- Risk: Scene watcher有効判定の差異でスライダー有効状態が変わる可能性。
- Mitigation: 既存ロジック（auto-hide OR auto-translate）を BooleanOrMultiConverter でそのまま再現。

### Tests / Verification
- dotnet build Hotkey-Translator.csproj 実行:   warning / 0 error。
- dotnet run --project Hotkey-Translator.csproj --no-build を6秒監視で実行し、RUNNING_OK_NO_EARLY_CRASH を確認。
**2026-02-14 11:31 (Asia/Taipei) — MVVM次段階実装（CTranslate2設定Binding化とOCR Preprocess状態のXAML移行）**

### Summary
- CTranslate2 設定と OCR Preprocess の有効/無効制御を Binding 化し、MainWindow.xaml.cs のイベント・表示同期ロジックをさらに削減した。

### Context / Goal
- Doc/MVVM_Implementation_Plan.md の Step 4/5/7 を継続し、OnSettingChanged と UpdateOcrPreprocessControls 依存を解消する。
- ホットキー（F5）経由の設定変更も ViewModel を経由する状態更新に寄せる。

### Changes
- SettingsViewModel に EnableCTranslate2 / CTranslate2DeviceTag を追加し、LoadFrom/ApplyTo と保存トリガへ組み込み。
- MainWindow.xaml の CTranslate2 チェック/デバイス選択を TwoWay Binding 化し、Click/SelectionChanged イベントを削除。
- MainWindow.xaml の OCR Preprocess 項目に IsEnabled / Foreground の Binding・Trigger を追加。
- BooleanAndMultiConverter を新規追加し、複数条件AND（例: Logging && PerfLog、Binarization && TwoPass）をXAML側で表現。
- MainWindow.xaml.cs から OnSettingChanged と UpdateOcrPreprocessControls を削除し、関連呼び出しを整理。
- OnToggleSceneAutoTranslateHotkeyPressed を、チェックボックス直接操作から SettingsViewModel.LoadFrom(settings) による同期へ変更。
- ApplySettingsToUi / ApplyUiInputToSettings から CTranslate2 の手動UI同期・強制OFF処理を削減。

### Files Touched
- ViewModels/SettingsViewModel.cs — CTranslate2設定プロパティと AppSettings 反映ロジックを追加。
- MainWindow.xaml — CTranslate2 と OCR Preprocess の状態制御を Binding/Trigger 化。
- MainWindow.xaml.cs — OnSettingChanged と UpdateOcrPreprocessControls を削除、F5処理の同期経路を変更。
- Converters/BooleanAndMultiConverter.cs — 複数 bool の AND 判定コンバータを追加。

### Behavioral Impact
- CTranslate2 設定UIはイベントハンドラなしで保存キューへ反映される。
- OCR Preprocess の有効状態とラベル色は保存後の手動更新なしで即時反映される。
- F5 トグル時の Scene setting 表示が ViewModel 同期経路に統一される。

### Risk & Mitigation
- Risk: XAML条件式の移行ミスで一部コントロールの有効状態が変わる可能性。
- Mitigation: 旧 UpdateOcrPreprocessControls の条件式を同値になるよう BooleanAndMultiConverter / MultiDataTrigger で再現。
- Risk: F5処理で設定同期タイミングが変わる可能性。
- Mitigation: SettingsViewModel.LoadFrom(settings) により UI 表示を同一ソースで再同期し、既存の保存フローは維持。

### Tests / Verification
- dotnet build Hotkey-Translator.csproj 実行:   warning / 0 error。
- dotnet run --project Hotkey-Translator.csproj --no-build を6秒監視で実行し、RUNNING_OK_NO_EARLY_CRASH を確認。
**2026-02-14 11:40 (Asia/Taipei) — MVVM次段階実装（翻訳優先度のViewModel移管完了）**

### Summary
- 翻訳優先度リストの状態管理と並び替え操作を MainWindow.xaml.cs から MainWindowViewModel へ移し、UI を完全に Binding 駆動へ統一した。

### Context / Goal
- Doc/MVVM_Implementation_Plan.md の未完了項目として残っていた「翻訳優先度の責務分離」を完了させる。
- `_translationPriority` 参照欠落によるビルドエラーを解消し、保存フローを ViewModel 中心に整える。

### Changes
- MainWindowViewModel に翻訳優先度用の `TranslationPriority` / `SelectedTranslationPriorityIndex` / 並び替えロジックを実装済み構成として確定。
- MainWindow.xaml の `TranslationPriorityList` を `ItemsSource` / `SelectedIndex` の TwoWay Binding に変更。
- MainWindow.xaml.cs の `_translationPriority` フィールド・上下移動メソッド・ItemsSource 手動設定を削除。
- MainWindow.xaml.cs の `ApplyTranslationPriority` と `GetTranslationPriority` を ViewModel API 経由へ置換。

### Files Touched
- `ViewModels/MainWindowViewModel.cs` — 翻訳優先度コレクション・選択状態・移動コマンド処理・保存要求連携を実装。
- `MainWindow.xaml` — 翻訳優先度 ListBox を ViewModel プロパティに Binding。
- `MainWindow.xaml.cs` — 旧コレクション依存を除去し、翻訳優先度の読取/反映を ViewModel API に置換。

### Behavioral Impact
- 翻訳優先度の表示・選択・上下移動は ViewModel の単一ソースで管理される。
- 設定保存時の `TranslationPriority` は ViewModel コレクションから取得され、従来と同じ既定値フォールバックを維持する。

### Risk & Mitigation
- Risk: ListBox の選択Index同期不整合により並び替え操作が無効化される可能性。
- Mitigation: `SelectedTranslationPriorityIndex` を TwoWay Binding し、移動後に ViewModel 側で選択Indexを再設定。
- Risk: 既存起動中プロセスによるビルド出力ロックで検証が不安定になる可能性。
- Mitigation: 出力先を分離したビルドでコンパイル確認後、通常ビルド成功も再確認。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj -p:UseAppHost=false -p:OutDir=bin\\Debug\\mvvmcheck\\` 実行: 0 warning / 0 error。
- `dotnet build Hotkey-Translator.csproj` 実行: 0 warning / 0 error。
- `dotnet run --project Hotkey-Translator.csproj --no-build` を6秒監視し、`RUNNING_OK_NO_EARLY_CRASH` を確認。
**2026-02-14 11:43 (Asia/Taipei) — MVVM次段階実装（設定正規化時のUI直接操作削減）**

### Summary
- 設定正規化後のUI同期で残っていたチェックボックス直接操作を削減し、`SettingsViewModel.LoadFrom` 経由へ統一した。

### Context / Goal
- Doc/MVVM_Implementation_Plan.md の Step 6/7 を進めるため、MainWindow の View 依存責務をさらに縮小する。
- `SettingsUiController` の `ApplySceneChangeModeToUi` 依存を除去し、保存後再同期経路を一本化する。

### Changes
- `ISettingsUiBridge` から `ApplySceneChangeModeToUi` を削除し、`SettingsUiController.SaveFromUiAsync` での呼び出しを廃止。
- Scene mode 正規化は `NormalizeSceneChangeModeSettings(settings)` のみ実行し、UI反映は既存 `ApplyRuntimeStateAfterSave -> Settings.LoadFrom(settings)` に集約。
- `MainWindow.xaml.cs` の以下をコントロール直接代入から ViewModel再同期へ置換。
- `DisableCTranslate2` / `DisableLlamaTranslation`。
- `SelectRoiAsync` の ROI有効化反映。
- `RestartLlamaCppAsync` / `StopLlamaServerAsync` の Llama有効フラグ更新。

### Files Touched
- `Services/Application/SettingsUiController.cs` — Bridge API を簡素化し、scene mode 正規化後のUI直接反映処理を削除。
- `MainWindow.xaml.cs` — チェックボックス直接代入を削除し、`SettingsViewModel.LoadFrom(settings)` 経由の同期へ変更。

### Behavioral Impact
- scene mode 競合（auto-hide/auto-translate同時ON）正規化時のUI同期は保存後再同期経路に一本化される。
- Llama/CTranslate2/ROI の内部状態変更時も、View の個別コントロール操作なしで設定UIが追従する。

### Risk & Mitigation
- Risk: 正規化直後のUI表示反映タイミングが遅れる可能性。
- Mitigation: 保存フロー内の `ApplyRuntimeStateAfterSave` が同一トランザクション内で `LoadFrom` を実施し、表示差分を吸収。
- Risk: Bridge インターフェース変更による実装漏れ。
- Mitigation: `ApplySceneChangeModeToUi` 参照を全検索し未参照を確認、ビルドで型整合性を検証。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj` 実行: 0 warning / 0 error。
- `dotnet run --project Hotkey-Translator.csproj --no-build` を6秒監視し、`RUNNING_OK_NO_EARLY_CRASH` を確認。
**2026-02-14 11:45 (Asia/Taipei) — MVVM次段階実装（RuntimeStatusの表示Binding化）**

### Summary
- ROI/翻訳ステータス表示と Busy Overlay 表示を `RuntimeStatusViewModel` バインディングへ移し、`MainWindow.xaml.cs` の UI 直接更新を削減した。

### Context / Goal
- Doc/MVVM_Implementation_Plan.md の Step 5/6/7 を継続し、MainWindow を View + Composition Root に近づける。
- 表示更新責務を ViewModel 側へ集約し、MainWindow の TextBlock/Visibility 直接操作を減らす。

### Changes
- `RuntimeStatusViewModel` に `RoiStatusMessage` / `TranslationStatusMessage` を追加。
- `MainWindow.xaml` で以下を Binding 化。
- `RoiStatusText.Text -> RuntimeStatus.RoiStatusMessage`
- `TranslationStatusText.Text -> RuntimeStatus.TranslationStatusMessage`
- `BusyOverlay.Visibility -> RuntimeStatus.IsBusy`（BooleanToVisibilityConverter 経由）
- `BusyOverlayText.Text -> RuntimeStatus.BusyMessage`
- `MainWindow.xaml.cs` の `SetBusyOverlay` は View コントロール直接更新をやめ、`RuntimeStatus` プロパティ更新へ変更。
- `UpdateRoiStatus` / `UpdateTranslationStatus` も `RuntimeStatus` 更新へ変更。

### Files Touched
- `ViewModels/RuntimeStatusViewModel.cs` — ROI/翻訳ステータスメッセージの公開プロパティを追加。
- `MainWindow.xaml` — ステータス表示と Busy Overlay の可視状態を RuntimeStatus バインディングへ変更。
- `MainWindow.xaml.cs` — Busy/ROI/翻訳ステータスの反映先を ViewModel に置換。

### Behavioral Impact
- ステータス表示更新は ViewModel 経由の単一経路になり、UIコントロール直接更新依存が減る。
- Busy Overlay の表示制御がバインディング駆動になり、View側責務が明確化される。

### Risk & Mitigation
- Risk: BusyMessage が未設定のまま表示される可能性。
- Mitigation: 既存の `SetBusyOverlay` でメッセージ指定時のみ更新する挙動を維持し、既存呼び出しとの互換を保つ。
- Risk: バインディング変更で初期表示が変わる可能性。
- Mitigation: ViewModel の初期値を従来表示文言（ROI: not set / Translation status: unknown）に合わせる。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj` 実行: 0 warning / 0 error。
- `dotnet run --project Hotkey-Translator.csproj --no-build` を6秒監視し、`RUNNING_OK_NO_EARLY_CRASH` を確認。
**2026-02-14 11:46 (Asia/Taipei) — MVVM次段階実装（Status表示Binding化の仕上げ）**

### Summary
- RuntimeStatus バインディング移行後の不要な `x:Name` を整理し、View 側のコードビハインド依存をさらに削減した。

### Context / Goal
- 直前に実施した RuntimeStatus バインディング移行を仕上げ、MainWindow の UI 要素参照を最小化する。
- View の責務を「見た目定義 + Binding」に寄せる。

### Changes
- `MainWindow.xaml` から以下の未使用 `x:Name` を削除。
- `RoiStatusText`
- `TranslationStatusText`
- `BusyOverlay`
- `BusyOverlayText`
- 既存バインディング（`RuntimeStatus.*`）は維持し、表示挙動は変更しない。

### Files Touched
- `MainWindow.xaml` — 未使用 name フィールドを削除して View の参照面を縮小。

### Behavioral Impact
- 画面表示の機能差分はない。
- 自動生成されるコードビハインドフィールドが減り、View の直接参照余地が減る。

### Risk & Mitigation
- Risk: XAML name 削除で将来コード参照が追加された場合にコンパイルエラー化する可能性。
- Mitigation: 現在参照が存在しないことを全検索で確認済み。必要時は Binding優先で追加する方針を維持。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj` 実行: 2 warnings / 0 error（起動中プロセスによる exe lock 警告）。
- `dotnet run --project Hotkey-Translator.csproj --no-build` を6秒監視し、`RUNNING_OK_NO_EARLY_CRASH` を確認。
**2026-02-14 11:50 (Asia/Taipei) — MVVM次段階実装（ApplyUiInputToSettings依存の解体）**

### Summary
- `SettingsUiController` の保存処理から `ISettingsUiBridge.ApplyUiInputToSettings` 依存を外し、ViewModel反映アクション注入へ置換した。

### Context / Goal
- Doc/MVVM_Implementation_Plan.md の DoD にある `ApplyUiInputToSettings` 依存解消に向けて、Bridge責務を縮小する。
- 設定保存時のデータ反映を「UI Bridge呼び出し」から「明示的なSettings反映アクション」へ切り替える。

### Changes
- `ISettingsUiBridge` から `ApplyUiInputToSettings` を削除。
- `SettingsUiController` コンストラクタに `Action<AppSettings> applySettingsInput` を追加し、`SaveFromUiAsync` で使用。
- `MainWindow` から `SettingsUiController` 生成時に `ApplyViewModelInputToSettings` を注入。
- `MainWindow.xaml.cs` の明示インターフェース実装 `ISettingsUiBridge.ApplyUiInputToSettings` を削除。
- `ApplyUiInputToSettings` を `ApplyViewModelInputToSettings` にリネームし、翻訳優先度反映を ViewModel API 直呼びに統一。

### Files Touched
- `Services/Application/SettingsUiController.cs` — Bridge API を縮小し、設定反映をアクション注入へ変更。
- `MainWindow.xaml.cs` — Controller初期化と反映メソッド名を更新し、Bridge実装を削除。

### Behavioral Impact
- 設定保存時の挙動は維持されるが、`SettingsUiController` が UI Bridge の特定メソッドに依存しなくなる。
- TranslationPriority を含む設定反映は ViewModel起点で一貫化される。

### Risk & Mitigation
- Risk: 注入アクションの差し替え漏れで保存時反映が実行されない可能性。
- Mitigation: コンストラクタ引数を必須化し、ビルドでコンパイル時検知可能にした。
- Risk: 変更直後の保存フロー回帰。
- Mitigation: build/run スモークで起動直後クラッシュがないことを確認。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj` 実行: 2 warnings / 0 error（起動中プロセスによる exe lock 警告）。
- `dotnet run --project Hotkey-Translator.csproj --no-build` を6秒監視し、`RUNNING_OK_NO_EARLY_CRASH` を確認。
**2026-02-14 11:52 (Asia/Taipei) — MVVM次段階実装（Llamaモデル選択UIのBinding駆動化）**

### Summary
- Llamaモデル選択UIを `ComboBoxItem` 手動生成/選択から、ViewModelコレクションの Binding 駆動に移行した。

### Context / Goal
- MainWindow の UI部品直接操作を減らし、MVVM の責務分離を進める。
- 既存の「選択モデルが欠損している場合に `(missing)` 表示で残す」挙動は維持する。

### Changes
- `MainWindowViewModel` に `LlamaModelOption` と `ObservableCollection<LlamaModelOption> LlamaModelOptions` を追加。
- `ResetLlamaModelOptions(...)` を追加し、モデル候補リスト更新を ViewModel 経由に統一。
- `MainWindow.xaml` の `LlamaModelBox` を `ItemsSource` / `DisplayMemberPath` / `SelectedValuePath` Binding 化。
- `MainWindow.xaml.cs` の `ReloadLlamaModelOptions` を書き換え、`LlamaModelBox.Items` の直接編集を削除。
- `GetSelectedTag` / `SetComboBoxByTag` ヘルパーを削除。
- 併せて `DisablePaddleOcr` / `DisablePaddleVlOcr` の `OcrEngineBox` 直接操作を削除し、`SettingsViewModel.LoadFrom(settings)` 同期へ統一。

### Files Touched
- `ViewModels/MainWindowViewModel.cs` — Llama モデル選択肢コレクションと更新メソッドを追加。
- `MainWindow.xaml` — Llama モデル選択 ComboBox の Binding 定義へ変更。
- `MainWindow.xaml.cs` — Llama モデルリスト反映処理を ViewModel 更新へ変更し、不要ヘルパーを削除。Paddle失敗時のUI同期も ViewModel 経由へ変更。

### Behavioral Impact
- Llamaモデル候補は ViewModel の単一状態として管理される。
- 欠損モデルは引き続き `(missing)` 表示で選択状態を維持できる。
- Paddle host 起動失敗時の OCR engine 切替反映も、コントロール直接更新ではなく Binding 同期で反映される。

### Risk & Mitigation
- Risk: モデル候補更新時に選択値が失われる可能性。
- Mitigation: `ReloadLlamaModelOptions` 内で正規化済み選択値を `Settings.LlamaSelectedModelFileName` に再代入し、候補外の場合は先頭へフォールバック。
- Risk: Binding 切替で初期表示が崩れる可能性。
- Mitigation: build/run スモークで起動時クラッシュなしを確認。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj` 実行: 2 warnings / 0 error（起動中プロセスによる exe lock 警告）。
- `dotnet run --project Hotkey-Translator.csproj --no-build` を6秒監視し、`RUNNING_OK_NO_EARLY_CRASH` を確認。
**2026-02-14 11:59 (Asia/Taipei) — MVVM次段階実装（ResourceHostFacade抽出でMainWindow責務を縮小）**

### Summary
- gRPCホストのロード/再起動/停止制御を `ResourceHostFacade` に集約し、`MainWindow.xaml.cs` から `EnsureResourceHostsAsync` 一式を削除した。

### Context / Goal
- Doc/MVVM_Implementation_Plan.md の Step 6/7 に沿って、MainWindow を View + Composition Root に近づける。
- ホスト制御（Paddle/PaddleVL/CTranslate2/Llama）に関する大きな手続きロジックを専用サービスへ移す。

### Changes
- `Services/Application/ResourceHostFacade.cs` を新規追加。
- `EnsureResourceHostsAsync` / `ShouldLoad*` / `TryStart*` / host stop / failure時設定巻き戻しをFacadeへ移設。
- `MainWindow.xaml.cs` から以下を削除または置換。
- 旧 `EnsureResourceHostsAsync` 一式（ShouldLoad/TryStart/Disable群）を削除。
- 旧 `BuildCTranslate2HostConfig` / `BuildLlamaHostConfig` を削除。
- 旧 host config record (`CTranslate2HostConfig` / `LlamaHostConfig`) を削除。
- `MainWindow` に `ResourceHostFacade` フィールドを追加し、constructorで注入初期化。
- `OnClosed` は個別 host stop から `ResourceHostFacade.Dispose()` に置換。
- `RestartLlamaCppAsync` / `StopLlamaServerAsync` / `RestartPaddleOcrHostsAsync` / `StopPaddleVlHost` をFacade API呼び出しへ置換。
- `ISettingsUiBridge.EnsureResourceHostsAsync` はFacade委譲に変更。
- host起動失敗時UI同期用に `SyncSettingsAfterHostFailure(AppSettings,bool)` を追加。

### Files Touched
- `Services/Application/ResourceHostFacade.cs` — 新規。リソースホスト制御の集約。
- `MainWindow.xaml.cs` — host制御ロジックを削減しFacade委譲へ変更。

### Behavioral Impact
- ホスト起動/停止と失敗時の設定巻き戻し挙動は維持しつつ、実装責務がFacadeに集約される。
- `MainWindow` はホスト制御の詳細から解放され、イベント配線・UI境界責務に寄る。

### Risk & Mitigation
- Risk: 移設時の分岐漏れで host 起動条件やエラーハンドリングが変わる可能性。
- Mitigation: 既存ロジックをFacadeへ等価移植し、失敗時の設定無効化とユーザー向けエラー表示を同一メッセージで維持。
- Risk: 再起動/停止コマンドの参照先変更による回帰。
- Mitigation: 各コマンドをFacade APIへ置換後にビルド・起動スモークを実施。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj` 実行: 0 warning / 0 error。
- `dotnet run --project Hotkey-Translator.csproj --no-build` を6秒監視し、`RUNNING_OK_NO_EARLY_CRASH` を確認。
**2026-02-14 12:09 (Asia/Taipei) — MVVM次段階実装（ResourceHostCommandController抽出）**

### Summary
- Llama/Paddle の再起動・停止コマンド処理を `ResourceHostCommandController` へ抽出し、`MainWindow.xaml.cs` は薄い委譲に置換した。

### Context / Goal
- Doc/MVVM_Implementation_Plan.md の Step 6/7 を継続し、MainWindow の手続きロジックをさらに外出しする。
- 既存挙動（busy overlay、保存、ログ、エラー表示、実行中ガード）を維持したまま責務分離する。

### Changes
- `Services/Application/ResourceHostCommandController.cs` を新規追加。
- `RestartLlamaCppAsync` / `StopLlamaServerAsync` / `RestartPaddleOcrHostsAsync` / `StopPaddleVlHost` の実処理を移設。
- `MainWindow.xaml.cs` に `ResourceHostCommandController` フィールドを追加してコンストラクタ注入。
- MainWindow 側4メソッドは `ResourceHostCommandController` 呼び出しの1行委譲へ変更。
- `SyncSettingsAfterHostFailure(AppSettings,bool)` をコマンド側でも再利用し、設定反映と翻訳ステータス更新を統一。

### Files Touched
- `Services/Application/ResourceHostCommandController.cs` — 新規。Resource host 操作コマンドの実処理を集約。
- `MainWindow.xaml.cs` — controller 注入とメソッド委譲化に変更。

### Behavioral Impact
- コマンド実行時の挙動は維持（同じメッセージ、同じガード条件、同じエラー通知）。
- MainWindow の責務はイベント接続と委譲中心になり、可読性と保守性が向上する。

### Risk & Mitigation
- Risk: 抽出時のメッセージやガード条件の差異による運用回帰。
- Mitigation: 既存文言と条件分岐を移植時に保持し、実行中ガード・WinRT分岐・失敗ダイアログを同等実装にした。
- Risk: コントローラ依存注入の不整合。
- Mitigation: コンストラクタで必須依存として束ね、ビルドで型整合性を確認。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj` 実行: 0 warning / 0 error。
- `dotnet run --project Hotkey-Translator.csproj --no-build` を6秒監視し、`RUNNING_OK_NO_EARLY_CRASH` を確認。
**2026-02-14 12:13 (Asia/Taipei) — MVVM次段階実装（HotkeyCommandController抽出）**

### Summary
- Hotkeyイベントの実処理を `HotkeyCommandController` に抽出し、`MainWindow.xaml.cs` のハンドラを薄い委譲へ置換した。

### Context / Goal
- Doc/MVVM_Implementation_Plan.md の Step 6/7 を進め、MainWindow のイベントハンドラ肥大を抑える。
- F8/F10/F11/F5/F6/F7/F9 系の既存挙動を維持しつつ Application 層へ責務移動する。

### Changes
- `Services/Application/HotkeyCommandController.cs` を新規追加。
- 実処理を以下へ移設。
- Run once（初回/通常）
- Force run
- Force Gemini strict
- Overlay text mode toggle
- Scene auto-translate toggle
- Capture window lock / unlock
- Select ROI
- Overlay show/hide toggle
- `MainWindow.xaml.cs` に `HotkeyCommandController` フィールドを追加し、依存を注入。
- `OnHotkeyPressed` / `OnForceRunHotkeyPressed` / `OnForceGeminiStrictHotkeyPressed` / `OnOcrOnlyHotkeyPressed` / `OnToggleSceneAutoTranslateHotkeyPressed` / `OnLockCaptureWindowHotkeyPressed` / `OnUnlockCaptureWindowHotkeyPressed` / `OnSelectRoiHotkeyPressed` / `OnToggleOverlayHotkeyPressed` を委譲化。
- `EnsureTranslatedOverlayForRunHotkeys` を MainWindow から削除（controller 内部に移動）。

### Files Touched
- `Services/Application/HotkeyCommandController.cs` — 新規。Hotkey系コマンド実処理を集約。
- `MainWindow.xaml.cs` — Hotkeyハンドラを controller 委譲に置換し、重複ロジックを削減。

### Behavioral Impact
- Hotkeyのユーザー挙動（ログ文言、実行ガード、設定保存タイミング）は維持される。
- MainWindow 側はイベント入口のみになり、責務境界が明確になる。

### Risk & Mitigation
- Risk: 抽出時の依存注入漏れで一部ホットキーが無効になる可能性。
- Mitigation: 既存ハンドラを全件置換後に `BuildHotkeyRegistrations` の参照を確認し、ビルドで型整合を検証。
- Risk: 非同期 hotkey 実行時の挙動差異。
- Mitigation: 既存 `async void` ハンドラは維持し、内部実装を `Task` 委譲に留めて呼び出し順序を保持。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj` 実行: 2 warnings / 0 error（起動中プロセスによる exe lock 警告）。
- `dotnet run --project Hotkey-Translator.csproj --no-build` を6秒監視し、`RUNNING_OK_NO_EARLY_CRASH` を確認。
- `MainWindow.xaml.cs` 行数確認: 972行。
**2026-02-14 12:16 (Asia/Taipei) — MVVM次段階実装（ログ表示トリム責務の外出し）**

### Summary
- LogBox への payload 反映・行数トリム責務を `UiLogViewAdapter` へ移し、`MainWindow.xaml.cs` からログ整形ユーティリティ群を削除した。

### Context / Goal
- MainWindow の残存UIロジックを削り、Doc/MVVM_Implementation_Plan.md の縮小目標（500–900レンジ）に近づける。
- `UiLogController` の flush callback を View 専用アダプタへ分離し、MainWindow の責務を委譲中心にする。

### Changes
- `Services/Application/UiLogViewAdapter.cs` を新規追加。
- `FlushPayload` / `TrimLogLines` / 改行カウント処理を集約。
- `MainWindow.xaml.cs` で `_uiLogViewAdapter` フィールドを追加し、`UiLogController` 初期化時に `FlushPayload` を注入。
- `MainWindow.xaml.cs` から以下を削除。
- `_logLineCount` フィールド
- `FlushLogPayload`
- `TrimLogLines`
- `IndexOfNthNewline`
- `CountNewlines`
- `CountLines`

### Files Touched
- `Services/Application/UiLogViewAdapter.cs` — 新規。LogBox表示・行数制御のアダプタを追加。
- `MainWindow.xaml.cs` — UiLogController の flush 経路を adapter 経由へ変更し、旧ログトリム実装を削除。

### Behavioral Impact
- ログ表示のユーザー挙動（追記、自動スクロール、最大行数超過時の先頭削除）は維持される。
- MainWindow の行数がさらに減り、UI補助ロジックの分離が進む。

### Risk & Mitigation
- Risk: アダプタ経由移行でログトリム境界が変わる可能性。
- Mitigation: 既存アルゴリズムを同値移植し、改行ベースの行数計算・切り詰め順序を維持。
- Risk: LogBox 未初期化時のNull参照。
- Mitigation: `Func<TextBox?>` アクセサで毎回 null ガードを実施。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj` 実行: 0 warning / 0 error。
- `dotnet run --project Hotkey-Translator.csproj --no-build` を6秒監視し、`RUNNING_OK_NO_EARLY_CRASH` を確認。
- `MainWindow.xaml.cs` 行数確認: 901行。
**2026-02-14 12:23 (Asia/Taipei) — 推奨実案ドキュメント作成（Post-Refactor安定化計画）**

### Summary
- 追加リファクタ方針として、構造分割より品質安定化を優先する推奨実案を `Doc/` に新規出力した。

### Context / Goal
- ユーザー要望に基づき、現状モジュールに対する次段階の推奨実装案を文書化する。
- テスト整備・失敗時フロー統一・責務境界文書化を中心に、費用対効果の高い実行計画を提示する。

### Changes
- `Doc/MVVM_PostRefactor_Stabilization_Plan.md` を新規追加。
- 実装案テンプレート（概要〜DoD）に沿って、ゴール/非ゴール、手順、リスク、影響範囲を整理。

### Files Touched
- `Doc/MVVM_PostRefactor_Stabilization_Plan.md` — Post-refactor安定化に向けた推奨実案を新規作成。

### Behavioral Impact
- 実行時挙動の変更はない（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: 文書のみ先行して実装差分が伴わない可能性。
- Mitigation: DoDにテスト追加・境界文書更新・build/run確認を明記し、実装時のチェック項目を固定。

### Tests / Verification
- 未実施（ドキュメント追加のみのため）。
**2026-02-14 12:33 (Asia/Taipei) — Layer2 Orchestrationリファクタリング実装案の作成**

### Summary
- マスタープランを参照し、層2（Orchestration）向けの段階的リファクタリング実装案を `Doc/` に新規作成した。

### Context / Goal
- `Doc/Refactoring_Layers_1to5_Master_Plan.md` の Phase 2 方針を具体化し、実行可能な Layer2 計画に落とし込む。
- `PipelineOrchestrator` / `SceneTextSnapshotService` の責務分離と回帰リスク低減の実行手順を明文化する。

### Changes
- `Doc/Refactoring_Layer2_Orchestration_Plan.md` を新規追加。
- 実装案テンプレート（概要〜DoD）に沿って、ゴール/非ゴール、提案アーキテクチャ、I/F案、ステップ分割、リスク、影響範囲を記述。

### Files Touched
- `Doc/Refactoring_Layer2_Orchestration_Plan.md` — 層2の実装案を新規作成。

### Behavioral Impact
- 実行時挙動の変更はない（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: 文書のみ先行し、実装時に範囲が広がる可能性。
- Mitigation: 非ゴールとStep分割（1ステップ1責務）を明記し、フェーズ逸脱を防ぐ。

### Tests / Verification
- 未実施（ドキュメント追加のみのため）。
**2026-02-14 12:42 (Asia/Taipei) — Layer2 Step1実装（PipelineExecutionContext/PipelineStageResult導入）**

### Summary
- Refactoring_Layer2_Orchestration_Plan の Step 1 として、Orchestration層の中間受け皿DTOを追加し、`PipelineOrchestrator` に最小導入した。

### Context / Goal
- 層2の段階移行に向け、既存挙動を変えずにステージ分割可能な土台を作る。
- `RunOnceAsync` 内の中間データを将来ステージへ渡せる形で保持する。

### Changes
- `Services/Orchestration/PipelineExecutionContext.cs` を新規追加。
- settings/options、ROI、OCR結果、diff対象、翻訳結果、overlay項目などの中間状態プロパティを定義。
- `Services/Orchestration/PipelineStageResult.cs` を新規追加。
- `PipelineStopReason` / `PipelineOverlayAction` / `PipelineStageResult` を定義。
- `Services/PipelineOrchestrator.cs` を更新。
- `PipelineExecutionContext` を `RunOnceAsync` 冒頭で生成。
- 既存処理の主要ポイント（capture/roi/hash/ocr/group/diff/translate/overlay）で context に状態を格納。
- 実行ロジック・分岐・return 条件は変更せず、挙動互換を維持。

### Files Touched
- `Services/Orchestration/PipelineExecutionContext.cs` — 新規。実行コンテキストDTOを追加。
- `Services/Orchestration/PipelineStageResult.cs` — 新規。ステージ結果DTO/enumを追加。
- `Services/PipelineOrchestrator.cs` — context 生成と中間状態格納を追加。

### Behavioral Impact
- 実行時挙動の機能差分はない（内部データ保持の追加のみ）。
- Layer2 Step2以降（停止理由の状態化・ステージ抽出）へ進める前提が整う。

### Risk & Mitigation
- Risk: context 保持追加による副作用（状態更新漏れ/順序依存）。
- Mitigation: 既存 return 分岐は変更せず、読み取り専用に近い受け皿として最小導入に留めた。
- Risk: 型追加で依存関係が複雑化。
- Mitigation: `Services/Orchestration` 配下に限定配置し、現時点で外部公開I/Fは増やしていない。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj` 実行: 0 warning / 0 error。
- `dotnet run --project Hotkey-Translator.csproj --no-build` を6秒監視し、`RUNNING_OK_NO_EARLY_CRASH` を確認。
**2026-02-14 12:48 (Asia/Taipei) — Layer2 Step3実装（OCR/Group/Diffのステージ抽出）**

### Summary
- `PipelineOrchestrator` の OCR→Group→Diff 責務をステージへ委譲し、重複ロジックを削減した。

### Context / Goal
- Layer2 Step3 の目的は、`RunOnceAsync` 内の中核分岐を段階責務へ分離し、追跡性と保守性を上げること。
- あわせて `RunWithReadingUnitsAsync` の diff 判定を共通化し、同一ロジックの二重管理を解消する。

### Changes
- `Services/Orchestration/Stages/DiffStage.cs` を新規追加。
- `OcrDiffService` を用いた差分抽出と changed unit 解決を `DiffStage` へ集約。
- `Services/PipelineOrchestrator.cs` を更新。
- `OcrAndGroupStage` / `DiffStage` をフィールドとして導入し、`RunOnceAsync` の OCR/Group/Diff を委譲。
- Paddle confidence filter ログ、NoText 判定、diff ログは既存挙動を維持したままステージ出力を利用。
- `RunWithReadingUnitsAsync` の diff 判定も `DiffStage` 利用へ変更。
- Orchestrator 内で不要になった `ResolveChangedUnitIds` を削除し、差分責務を一本化。

### Files Touched
- `Services/Orchestration/Stages/DiffStage.cs` — 新規。Diff判定と changed unit 算出を実装。
- `Services/PipelineOrchestrator.cs` — ステージ委譲への差し替えと重複ヘルパー削除。

### Behavioral Impact
- 利用者観点の動作は維持（OCR/翻訳/Overlay の出力仕様は不変）。
- 内部構造として OCR/Group/Diff の責務境界が明確になり、次ステップ（Translate/Overlay 抽出）へ移行しやすくなった。

### Risk & Mitigation
- Risk: ステージ化によりログ順序やタイミング計測の値が微妙に変わる可能性。
- Mitigation: 既存ログ文言を維持し、Perf値は各ステージ計測値を同じ項目へ反映。
- Risk: changed unit 算出の移設で diff 結果が変わる可能性。
- Mitigation: 算出ロジックを同等移植し、`RunOnceAsync` と `RunWithReadingUnitsAsync` の双方で同一ステージを利用。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj` 実行: 0 warning / 0 error。
- `dotnet run --project Hotkey-Translator.csproj --no-build` を8秒監視し、`RUNNING_OK_NO_EARLY_CRASH` を確認。
**2026-02-14 12:54 (Asia/Taipei) — Layer2 Step4-6実装（Translate/Overlay抽出・Scene共通化・PerfProbe分離）**

### Summary
- Layer2 の残ステップを進め、翻訳/Overlay責務をステージ化し、Scene側OCR処理共通化とPerf計測分離を実装した。

### Context / Goal
- Step4: `PipelineOrchestrator` から Translate / Overlay の責務を分離する。
- Step5: `SceneTextSnapshotService` の Stage B OCR/Group 処理重複を共通ステージへ寄せる。
- Step6: Perf計測ノイズを専用コンポーネントへ隔離して可読性を上げる。

### Changes
- `Services/Orchestration/Stages/TranslateStage.cs` を新規追加。
- 旧 `ResolveTranslationsAsync` ロジック（cache参照、pending抽出、翻訳呼び出し、payloadログ）を移設。
- `TranslationStarted/Completed` は Orchestrator からコールバック注入し、既存イベント契約を維持。
- `Services/Orchestration/Stages/OverlayStage.cs` を新規追加。
- Overlay item 構築と update 呼び出しを集約し、`TrySetOverlayTextMode` 含め Orchestrator から利用。
- `Services/SceneTextSnapshotService.cs` を更新。
- Stage B OCR/Group/ReadingUnit 構築を `OcrAndGroupStage` 経由へ変更し、重複実装を削除。
- `Services/Orchestration/PipelinePerfProbe.cs` を新規追加。
- queue/capture/crop/ocr/group/diff/overlay/total 計測を集約し、閾値超過時ログ出力を継続。
- `Services/PipelineOrchestrator.cs` を更新。
- Translate/Overlay/Perf の旧直書きロジックを削除し、各ステージ/Probeへ委譲。

### Files Touched
- `Services/Orchestration/Stages/TranslateStage.cs` — 新規。翻訳キャッシュ・翻訳実行ステージを実装。
- `Services/Orchestration/Stages/OverlayStage.cs` — 新規。overlay項目構築と描画呼び出しを実装。
- `Services/Orchestration/PipelinePerfProbe.cs` — 新規。Perf計測と閾値ログ出力を実装。
- `Services/PipelineOrchestrator.cs` — Translate/Overlay/Perf責務をステージへ移譲し、旧ヘルパー群を削除。
- `Services/SceneTextSnapshotService.cs` — `OcrAndGroupStage` を利用して Stage B OCR経路を共通化。

### Behavioral Impact
- OCR/翻訳/Overlay の外部挙動は維持。
- 内部的に責務分割が進み、`PipelineOrchestrator` のロジック追跡性が向上。
- Scene snapshot 側のOCR前処理経路が Pipeline と同一実装を共有するため、今後の調整差分源を低減。

### Risk & Mitigation
- Risk: 翻訳キャッシュの状態保持位置変更でヒット挙動が変わる可能性。
- Mitigation: 旧ロジックを等価移植し、同じキー生成・pending条件・fallback再適用順序を維持。
- Risk: Overlay item 生成移設で表示整形が変わる可能性。
- Mitigation: 既存の改行正規化ロジックを `OverlayStage` にそのまま移植。
- Risk: PerfProbe化でログ出力条件が変わる可能性。
- Mitigation: 同一閾値条件・同一ログ項目名で出力。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj` 実行: 0 warning / 0 error。
- `dotnet run --project Hotkey-Translator.csproj --no-build` を8秒監視し、`RUNNING_OK_NO_EARLY_CRASH` を確認。
**2026-02-14 13:10 (Asia/Taipei) — Layer2 Step7実装（旧経路削除・stage実行一本化）**

### Summary
- `PipelineOrchestrator` の停止分岐直書きを共通処理に集約し、overlay関連の `_last*` 更新を単一コミットに統一した。

### Context / Goal
- Layer2 Step7 の目的は、旧分岐直書き経路を削減し、stage 実行後の停止/状態更新を一貫した流れへ寄せること。
- 併せて将来の回帰源になりやすい `_last*` 更新順序を固定化する。

### Changes
- `Services/PipelineOrchestrator.cs` を更新。
- black frame / ROI out / pHash unchanged / no-text 分岐で重複していた overlay fallback 処理を `ApplyStopResult` に集約。
- `PipelineStageResult` を必ず `ApplyStopResult` 経由で反映する形へ寄せ、停止理由ログを統一。
- overlay関連 state の更新を `CommitOverlayState` に集約し、`RunOnceAsync` と `RunWithReadingUnitsAsync` の重複更新を削減。
- 使われていない旧フィールド（旧経路由来）を削除し、責務境界を整理。
- `UpdateLastRoiSnapshot` を簡素化し、未使用の bounds state を除去。

### Files Touched
- `Services/PipelineOrchestrator.cs` — 停止処理共通化、状態コミット一本化、旧未使用フィールド削除。

### Behavioral Impact
- 外部挙動（OCR/翻訳/overlay更新、例外時fallback）は維持。
- 停止時ログが `reason/overlayAction/message` の形式で揃い、運用時の機械判定性が向上。

### Risk & Mitigation
- Risk: 停止処理共通化で no-text の toast 条件が変わる可能性。
- Mitigation: 既存条件（`SuppressTransientUiFeedback`）を `ApplyStopResult` に移植し、表示/抑制挙動を維持。
- Risk: `_last*` 更新位置の統合で表示モード切替の参照状態が変わる可能性。
- Mitigation: 旧更新内容を同一順序で `CommitOverlayState` に集約し、更新タイミングは overlay描画直前に維持。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj` 実行: 0 warning / 0 error。
- `dotnet run --project Hotkey-Translator.csproj --no-build` を8秒監視し、`RUNNING_OK_NO_EARLY_CRASH` を確認。
**2026-02-14 13:21 (Asia/Taipei) — Layer3実装案作成（Capture層リファクタリング計画）**

### Summary
- Master Plan を基に、層3（Capture）向けの具体的な段階実装案を `Doc/Refactoring_Layer3_Capture_Plan.md` として新規作成した。

### Context / Goal
- `CaptureManager` に集中している provider選択・cooldown・black判定を責務分離し、回帰リスクを下げる実装計画を明文化する。
- 層2完了後に着手可能な粒度で、段階移行とロールバック性を担保する。

### Changes
- `Doc/Refactoring_Layer3_Capture_Plan.md` を新規作成。
- 提案アーキテクチャとして `CaptureTargetResolver` / `CaptureProviderSelector` / `ICapturePolicy` / `CaptureProviderStateStore` / `CaptureAttemptCoordinator` / `CaptureExecutionTrace` を定義。
- Step1〜Step7 の段階実装、非機能要件、リスク緩和、影響範囲、DoD を整理。

### Files Touched
- `Doc/Refactoring_Layer3_Capture_Plan.md` — 層3の実装案をテンプレート準拠で追加。

### Behavioral Impact
- 実行時挙動の変更はなし（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: 章立ての過不足により実装時の解釈ぶれが出る可能性。
- Mitigation: 既存コード（`CaptureManager` / DXGI/WGC/GDI provider）の現状整理を前提に、責務境界とステップを明示した。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。
**2026-02-14 13:30 (Asia/Taipei) — Layer3実装（Capture責務分離 Step1-7）**

### Summary
- `CaptureManager` の分岐を Capture専用コンポーネントへ分離し、Step1〜Step7（ログ固定〜旧経路削除）を一括実装した。

### Context / Goal
- `CaptureManager` に集中していた provider選択・target解決・cooldown/black判定・試行ループ責務を分離し、回帰時の追跡性を上げる。
- Auto/Fixed provider、DXGI timeout特例、black frame threshold の挙動互換を維持する。

### Changes
- `Services/Capture/` 配下に Capture層の責務分離コンポーネントを新規追加。
- `CaptureTargetResolver` で固定ウィンドウ解決を抽出（Step2）。
- `CaptureProviderSelector` で provider順序決定を抽出（Step3）。
- `ICapturePolicy` / `DefaultCapturePolicy` で cooldown・black判定ポリシーを抽出（Step4）。
- `CaptureProviderStateStore` で provider runtime state を抽出（Step5）。
- `CaptureAttemptCoordinator` で provider試行ループを抽出（Step6）。
- `CaptureAttemptResult` / `CaptureExecutionTrace` / `CaptureExecutionOutcome` を追加し、失敗理由追跡を標準化（Step7）。
- `Services/CaptureManager.cs` を facade 化し、内部を上記コンポーネントへ委譲。
- captureログを `stage=capture` キー付きで統一し、skip/fail/black/success/all_failed を機械判定可能にした（Step1）。

### Files Touched
- `Services/CaptureManager.cs` — 旧直書き分岐を削除し、Capture責務コンポーネントへの委譲に変更。
- `Services/Capture/CaptureAttemptCoordinator.cs` — provider試行ループと black/cooldown 判定連携を実装。
- `Services/Capture/CaptureProviderSelector.cs` — Auto/Fixed provider順序決定を実装。
- `Services/Capture/CaptureTargetResolver.cs` — CaptureRequest と固定ウィンドウ解決を実装。
- `Services/Capture/ICapturePolicy.cs` — capture policy 契約を定義。
- `Services/Capture/DefaultCapturePolicy.cs` — DXGI timeout特例を含む既定ポリシー実装。
- `Services/Capture/CaptureProviderStateStore.cs` — provider状態（black count / cooldown / failure reason）管理を実装。
- `Services/Capture/CaptureFailureReason.cs` — 失敗理由enumを追加。
- `Services/Capture/CaptureAttemptResult.cs` — provider試行結果DTOを追加。
- `Services/Capture/CaptureExecutionTrace.cs` — 実行trace要約を追加。
- `Services/Capture/CaptureExecutionOutcome.cs` — capture実行結果DTOを追加。

### Behavioral Impact
- 外部I/F（`CaptureManager.Capture/GetCaptureBounds`）は維持。
- Auto/Fixed provider 選択、DXGI timeout の cooldown非適用、black frame閾値による cooldown 開始の挙動は維持。
- ログ粒度が増え、capture失敗時に provider別理由を追跡しやすくなった。

### Risk & Mitigation
- Risk: 分割後に cooldown/black判定条件が変わる可能性。
- Mitigation: 旧条件（DXGI timeout除外、black threshold比較、cooldown秒数）を `DefaultCapturePolicy` に等価移植。
- Risk: ログ増加によるノイズ。
- Mitigation: `stage=capture` プレフィックスで機械抽出可能にし、調査時のみフィルタできる形に統一。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj` 実行: 0 warning / 0 error。
- `dotnet run --project Hotkey-Translator.csproj --no-build` を8秒監視し、`RUNNING_OK_NO_EARLY_CRASH` を確認。
**2026-02-14 14:23 (Asia/Taipei) — Layer4実装案作成（gRPC Host基盤共通化計画）**

### Summary
- Master Plan を参考に、第四層（gRPC Host）向けの段階実装案を `Doc/Refactoring_Layer4_GrpcHost_Plan.md` として新規作成した。

### Context / Goal
- `Paddle/PaddleVL/CT2/Llama` の Host 管理に重複している起動・監視・再起動・ヘルスチェック責務を共通化する計画を明文化する。
- `ResourceHostFacade` の分岐肥大化を抑え、追加Host時の横展開漏れを減らす。

### Changes
- `Doc/Refactoring_Layer4_GrpcHost_Plan.md` を新規作成。
- 提案アーキテクチャとして `IGrpcHostLifecycle` / `GrpcHostBase` / `GrpcHostRestartPolicy` / `GrpcHostDescriptor` / `GrpcHostOrchestrator` を定義。
- Step1〜Step7（ログ固定→共通基盤導入→Host移行→旧経路削除）を段階化。
- リスク、緩和策、影響範囲、DoD、ロールバック方針を整理。

### Files Touched
- `Doc/Refactoring_Layer4_GrpcHost_Plan.md` — 層4向け実装案をテンプレート準拠で追加。

### Behavioral Impact
- 実行時挙動の変更はない（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: 現行Host差分の把握不足による計画の抽象化過多。
- Mitigation: `Paddle/PaddleVL/CT2/Llama` 各Hostと `ResourceHostFacade` の現行実装を踏まえて、共通化対象と固有処理境界を明記した。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。
**2026-02-14 14:33 (Asia/Taipei) — Layer4実装（gRPC Host共通基盤化 Step1-7）**

### Summary
- `Paddle/PaddleVL/CT2/Llama` Host の重複処理を共通基盤へ寄せ、`ResourceHostFacade` を registry/orchestrator 駆動へ置換した。

### Context / Goal
- 各 gRPC Host に重複していた start/ready/monitor/restart/stop の実装を共通化し、横展開漏れを減らす。
- `ResourceHostFacade` の起動分岐を縮小し、失敗時 disable 挙動を宣言的に統一する。

### Changes
- `Services/GrpcHost/` を新規追加。
- `IGrpcHostLifecycle`, `GrpcHostRestartPolicy`, `GrpcHostBase` を追加し、共通ライフサイクル基盤を実装。
- `GrpcHostDescriptor`, `GrpcHostRegistry`, `GrpcHostOrchestrator` を追加し、Host管理の宣言的構成を導入。
- `Services/PaddleGrpcHost.cs` を `GrpcHostBase` 継承へ移行。
- `Services/PaddleVlGrpcHost.cs` を `GrpcHostBase` 継承へ移行。
- `Services/CTranslate2GrpcHost.cs` を `GrpcHostBase` 継承へ移行。
- `Services/LlamaGrpcHost.cs` を `GrpcHostBase` 継承へ移行。
- Llama固有処理（uv sync/model検証/CUDA検証/orphan kill）は派生側に保持し、`OnAfterStop` / `OnProcessOutputLine` フックへ移設。
- `Services/Application/ResourceHostFacade.cs` を更新。
- 旧 `TryStartXxx` 分岐を削除し、`GrpcHostRegistry` + `GrpcHostOrchestrator` による起動管理へ置換。
- Host競合停止順（Paddle↔PaddleVL、Llama開始前CT2停止）を descriptor で明示。
- 起動失敗時の disable/action は descriptor 経由で統一。

### Files Touched
- `Services/GrpcHost/IGrpcHostLifecycle.cs` — Hostライフサイクル契約を追加。
- `Services/GrpcHost/GrpcHostRestartPolicy.cs` — 再起動ポリシーDTOを追加。
- `Services/GrpcHost/GrpcHostBase.cs` — 共通起動/監視/再起動/停止基盤を実装。
- `Services/GrpcHost/GrpcHostDescriptor.cs` — Host宣言モデルを追加。
- `Services/GrpcHost/GrpcHostRegistry.cs` — Descriptor管理を追加。
- `Services/GrpcHost/GrpcHostOrchestrator.cs` — Host起動オーケストレーションを追加。
- `Services/PaddleGrpcHost.cs` — 共通基盤継承へ移行。
- `Services/PaddleVlGrpcHost.cs` — 共通基盤継承へ移行。
- `Services/CTranslate2GrpcHost.cs` — 共通基盤継承へ移行。
- `Services/LlamaGrpcHost.cs` — 共通基盤継承へ移行（Llama固有処理は保持）。
- `Services/Application/ResourceHostFacade.cs` — registry/orchestrator 駆動へ置換。

### Behavioral Impact
- ResourceHostFacade の外部操作（起動/停止/失敗時設定OFF）は維持。
- Hostログに `stage=grpc_host host=<id> event=<...>` を追加し、start/ready/exit/restart 系イベントの追跡性が向上。
- CTranslate2 退役方針（`ShouldLoadCTranslate2=false`）は維持。

### Risk & Mitigation
- Risk: 共通化により Llama 固有処理が欠落する可能性。
- Mitigation: preflight/モデル処理を派生側に残し、停止後 orphan kill は `OnAfterStop` で維持。
- Risk: ResourceHostFacade 置換で競合停止順が変わる可能性。
- Mitigation: descriptor の `StopBeforeStartHostIds` で順序を明示し、旧挙動（Paddle↔PaddleVL、Llama前CT2停止）を維持。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj` 実行: 0 warning / 0 error。
- `dotnet run --project Hotkey-Translator.csproj --no-build` を8秒監視し、`RUNNING_OK_NO_EARLY_CRASH` を確認。
**2026-02-14 14:41 (Asia/Taipei) — Layer5実装案作成（Settings層モジュール化計画）**

### Summary
- Master Plan を参考に、第五層（Settings）向けの段階実装案を `Doc/Refactoring_Layer5_Settings_Plan.md` として新規作成した。

### Context / Goal
- `AppSettings` / `SettingsService` / `SettingsUiController` に分散している正規化・互換・秘匿化責務を集約する計画を明文化する。
- 既存 `settings.json` 互換を維持しつつ、設定追加時の保守コストを下げる。

### Changes
- `Doc/Refactoring_Layer5_Settings_Plan.md` を新規作成。
- 提案アーキテクチャとして `ISettingsRepository` / `ISecretProtector` / `AppSettingsValidator` / `AppSettingsMigrator` / `FeatureSettings` / `SettingsFacade` を定義。
- Step1〜Step7（ログ固定→責務分離→validator化→migrator化→呼び出し口統一→旧経路削除）を段階化。
- 非機能要件、リスク緩和、影響範囲、DoD、ロールバック方針を整理。

### Files Touched
- `Doc/Refactoring_Layer5_Settings_Plan.md` — 層5向け実装案をテンプレート準拠で追加。

### Behavioral Impact
- 実行時挙動の変更はない（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: 現行の正規化責務分散を過小評価すると計画の実効性が落ちる可能性。
- Mitigation: `SettingsService` と `SettingsUiController` の実装実態（DPAPI、Normalize群、互換処理）を踏まえて責務境界を明示した。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。
**2026-02-14 15:00 (Asia/Taipei) — Layer5 Settings Step5/Step7 実装（FeatureSettings適用と旧正規化経路削除）**

### Summary
- Layer5 Settings 計画の未完了だった Step5/Step7 を実装し、FeatureSettings の利用拡大と SettingsUiController の旧static正規化経路削除を完了した。

### Context / Goal
- SettingsFacade + Validator + Migrator へ設定正規化責務を一本化し、UI層の互換API依存をなくす。
- Resource/Scene/Pipeline 実行系で機能単位設定ビューを使い、AppSettings 直接依存を段階的に縮小する。

### Changes
- ResourceHostFacade に FeatureSettingsProvider を導入し、Host起動条件判定を HostFeatureSettings 経由へ変更。
- ResourceHostFacade の host config 生成時正規化を SettingsUiController.Normalize* から SettingsHostNormalizer 直接呼び出しへ置換。
- SceneChangeController に FeatureSettingsProvider を導入し、watcher有効判定・クールダウン・semantic gate 判定を SceneFeatureSettings 経由へ変更。
- MainWindowRunCoordinator に FeatureSettingsProvider を導入し、scene semantic payload 再利用判定で SceneFeatureSettings を利用。
- MainWindow.xaml.cs の Llamaモデル名fallback正規化を SettingsHostNormalizer 直接呼び出しへ置換。
- SettingsUiController から互換static正規化メソッド群（Normalize*）を削除。
- HotkeyDefaultsRule の未使用コードを整理。

### Files Touched
- Services/Application/ResourceHostFacade.cs — Host判定を feature view 経由へ変更し、旧UI static正規化依存を除去。
- Services/Application/SceneChangeController.cs — Scene watcher 判定・間隔参照を SceneFeatureSettings 経由へ変更。
- Services/Application/MainWindowRunCoordinator.cs — Scene semantic payload 再利用判定に feature view を適用。
- Services/Application/SettingsUiController.cs — 旧static正規化APIを削除し、UI保存トリガ責務に限定。
- MainWindow.xaml.cs — Llamaモデル名正規化呼び出し先を SettingsHostNormalizer に変更。
- Services/Settings/Rules/HotkeyDefaultsRule.cs — 未使用メソッド/usingを削除。

### Behavioral Impact
- 設定ロード/保存時の正規化導線は SettingsFacade 経由に統一され、UI層の静的互換経路は廃止された。
- Host起動判定とScene監視判定が feature settings view に集約され、将来の設定追加時の影響点が明確になった。

### Risk & Mitigation
- Risk: 判定経路変更によりScene watcherやHost起動タイミングが変わる可能性。
- Mitigation: 既存条件式と同じロジックを feature view へ写像し、dotnet build で回帰の一次確認を実施。
- Risk: static正規化API削除漏れによるビルドエラー。
- Mitigation: 参照箇所を全検索し、呼び出し先を SettingsHostNormalizer に全面置換した。

### Tests / Verification
- dotnet build Hotkey-Translator.csproj 実行: 0 warning / 0 error。
**2026-02-14 15:01 (Asia/Taipei) — Layer5 Step5追補（PipelineOrchestratorへのFeatureSettings適用）**

### Summary
- Layer5 Step5 の対象を補完し、PipelineOrchestrator でも FeatureSettings 参照を開始した。

### Context / Goal
- Step5 の「主要利用箇所で feature view 参照を開始」を PipelineOrchestrator まで拡張する。
- 設定取得の責務境界を揃えて、今後の設定追加時の影響範囲を限定する。

### Changes
- PipelineOrchestrator に FeatureSettingsProvider を導入。
- 実行時に GetOcr(settings) を取得し、Paddle系の信頼度フィルタログ分岐で OcrFeatureSettings.OcrEngine を参照するよう変更。

### Files Touched
- Services/PipelineOrchestrator.cs — OCR機能設定を feature view 経由で参照する導線を追加。

### Behavioral Impact
- OCR実行結果自体は変えず、Paddle系判定の参照元を FeatureSettings に寄せた。

### Risk & Mitigation
- Risk: OCRログ分岐条件の変更によりログ出力頻度が変わる可能性。
- Mitigation: 既存のPaddle系条件と同義の OcrEngine 判定を使用し、回帰を最小化。

### Tests / Verification
- dotnet build Hotkey-Translator.csproj 実行: 0 warning / 0 error。
- dotnet run --project Hotkey-Translator.csproj --no-build を8秒監視: RUNNING_OK_NO_EARLY_CRASH。
**2026-02-14 15:08 (Asia/Taipei) — gRPC Hostログ欠落修正（logger参照を遅延評価化）**

### Summary
- PaddleOCR/LlamaCpp 起動時ログがログウィンドウに出ない問題を修正し、gRPC host が最新 AppLogger を参照できるようにした。

### Context / Goal
- ResourceHostFacade 構築時点では MainWindow._logger が未初期化のため、hostへ 
ull logger が固定されていた。
- host起動・ready・stdout/stderr ログを UI ログへ復帰させる。

### Changes
- GrpcHostBase の logger保持を AppLogger? 固定値から Func<AppLogger?> の遅延評価へ変更。
- 各 host コンストラクタ (PaddleGrpcHost / PaddleVlGrpcHost / CTranslate2GrpcHost / LlamaGrpcHost) を accessor受け取りへ更新。
- ResourceHostFacade で host生成時に _loggerAccessor() ではなく _loggerAccessor を渡すよう変更。
- WHYコメントを追加し、OnLoaded 後の logger 割り当てを反映する意図を明示。

### Files Touched
- Services/GrpcHost/GrpcHostBase.cs — logger参照方式を遅延評価へ変更。
- Services/PaddleGrpcHost.cs — コンストラクタ引数を logger accessor 化。
- Services/PaddleVlGrpcHost.cs — コンストラクタ引数を logger accessor 化。
- Services/CTranslate2GrpcHost.cs — コンストラクタ引数を logger accessor 化。
- Services/LlamaGrpcHost.cs — コンストラクタ引数を logger accessor 化。
- Services/Application/ResourceHostFacade.cs — host生成時の logger引き渡しを修正。

### Behavioral Impact
- gRPC host の stage=grpc_host ... と host出力ログが、MainWindow の logger 初期化後にログウィンドウへ表示されるようになる。
- host制御ロジック（起動条件・停止条件）自体は変更なし。

### Risk & Mitigation
- Risk: logger accessor 呼び出し増加による微小なオーバーヘッド。
- Mitigation: アクセサは単純な参照取得のみで、hostイベント頻度に対して影響は限定的。
- Risk: 実行中プロセスが Hotkey-Translator.exe をロックしている状態でビルド警告が出る。
- Mitigation: ビルドは成功しており、検証時は警告内容を記録した。

### Tests / Verification
- dotnet build Hotkey-Translator.csproj 実行: 0 error（Hotkey-Translator.exe ロックに伴う MSB3026 warning は発生）。
- dotnet run --project Hotkey-Translator.csproj --no-build を8秒監視: RUNNING_OK_NO_EARLY_CRASH。
**2026-02-14 15:36 (Asia/Taipei) — ocr_vl_engine.py テストCLI追加**

### Summary
- OcrServiceVL/ocr_vl_engine.py を直接検証するため、全主要パラメータをCLI指定できるテストスクリプトを追加した。

### Context / Goal
- ユーザー要望により PaddleOcrVlEngine の初期化・推論を手元で再現しやすい入口を用意する。
- device/pipeline_version/HPI/TensorRT 等の挙動を引数で切り替えて切り分け可能にする。

### Changes
- OcrServiceVL/test_ocr_vl_engine.py を新規追加。
- PaddleOcrVlEngine の全実質パラメータ（device, pipeline-version, max-pixels, layout-threshold, max-new-tokens, merge-layout-blocks, use-ocr-for-image-block, use-layout-detection, enable-hpi, use-tensorrt, precision）をCLI引数化。
- --output-json / --pretty / --traceback を追加し、結果保存と障害解析を容易化。
- 実行後に close() を必ず呼ぶ後始末を追加。

### Files Touched
- OcrServiceVL/test_ocr_vl_engine.py — ocr_vl_engine.py テスト用CLIを追加。

### Behavioral Impact
- 本体アプリ動作は不変。
- 開発者が uv run で ocr_vl_engine.py の推論経路を直接検証できるようになった。

### Risk & Mitigation
- Risk: 不正な引数値で実行時エラーが発生する可能性。
- Mitigation: argparse の型/choices制約と --traceback で切り分け可能にした。

### Tests / Verification
- uv run --project OcrServiceVL python OcrServiceVL/test_ocr_vl_engine.py --help 実行: 引数一覧が期待通り表示されることを確認。
**2026-02-14 15:58 (Asia/Taipei) — PaddleOCR-VL座標パース修正（parsing_res_list対応）**

### Summary
- ocr_vl_engine.py が parsing_res_list 形式の座標を拾えず 1x1 fallback を返していた問題を修正した。

### Context / Goal
- WPFログでは OCR/翻訳が成功しているのにオーバーレイが見えない症状が発生していた。
- 原因は markdown fallback により ox=[0,0,1,1] が返ることだったため、PaddleOCR-VLの実出力形式を正しくパースする。

### Changes
- OcrServiceVL/ocr_vl_engine.py の _parse_page_dict に es ラッパー辞書の再帰処理を追加。
- parsing_res_list（lock_content + lock_bbox / lock_polygon_points）のパース処理を追加。
- lock_bbox の xyxy / xywh を吸収する _bbox_to_ltrbwh を追加。
- 既存 lines / ocr_res / rec_texts+polys パースは維持し、互換性を確保。

### Files Touched
- OcrServiceVL/ocr_vl_engine.py — PaddleOCR-VLの新出力形式に対応し、座標抽出ロジックを拡張。

### Behavioral Impact
- parsing_res_list 出力時でも OCR結果が実座標（画面上の矩形）で返るようになり、WPFオーバーレイ非表示問題の主因を解消。
- テキストのみfallback（1x1矩形）の発生頻度が大幅に低下。

### Risk & Mitigation
- Risk: box 形式の解釈差（xyxy / xywh）で矩形がずれる可能性。
- Mitigation: x2>x1 && y2>y1 を優先してxyxy解釈し、成立しない場合のみxywhとして扱うフォールバックを実装。

### Tests / Verification
- uv run --project OcrServiceVL python OcrServiceVL/test_ocr_vl_engine.py "OcrServiceVL/test2.png" --device gpu:0 --pipeline-version v1.5 --max-pixels 500000 --layout-threshold 0.5 --max-new-tokens 512 --merge-layout-blocks --use-ocr-for-image-block --use-layout-detection --no-enable-hpi --no-use-tensorrt --precision fp16 --output-json "OcrServiceVL/outputs/ocr_vl_result.json" --pretty --traceback
- 結果確認: ecognized_lines=1、ox=[-14.0, 13.0, 1152.0, 208.0]（1x1 fallback ではない）。
**2026-02-14 16:36 (Asia/Taipei) — OCR gRPC上限/自動縮小 共通対策の実装案作成**

### Summary
- PaddleOCR / PaddleOCR-VL 両方を対象にした、gRPCメッセージ上限対策の実装案を Doc 配下へ追加した。

### Context / Goal
- 両エンジンで ResourceExhausted (Received message larger than max) が発生するため、限定対応ではなく共通対策を設計する。
- 根本対策（server上限設定）と安全弁（自動ダウンサイジング再送）を両立する。

### Changes
- Doc/Ocr_Grpc_MessageSize_Resilience_Plan.md を新規作成。
- サーバ上限引き上げ、Provider共通再試行、座標逆スケール復元、ログ観測設計をステップ化。
- DoD とリスク緩和策、影響範囲を明記。

### Files Touched
- Doc/Ocr_Grpc_MessageSize_Resilience_Plan.md — OCR gRPCメッセージ上限超過への共通実装案を追加。

### Behavioral Impact
- ドキュメント追加のみ。実行時挙動の変更なし。

### Risk & Mitigation
- Risk: 実装時にサーバ/クライアントの上限値が不整合になる可能性。
- Mitigation: 計画内で両サーバ同値設定とログ計測を必須化した。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。
**2026-02-14 16:49 (Asia/Taipei) — OCR gRPC上限引き上げとpayloadログ追加**

### Summary
- 自動縮小は入れず、PaddleOCR/PaddleOCR-VL の gRPC上限引き上げと payloadサイズログのみを実装した。

### Context / Goal
- ResourceExhausted (Received message larger than max) を通信レイヤで抑止する。
- 発生時の原因分析を容易にするため、クライアント/サーバ双方でpayloadサイズを観測可能にする。

### Changes
- OcrService/server.py に --max-message-bytes 引数（既定32MB）を追加し、grpc.server(..., options=[max_receive/max_send]) を設定。
- OcrServiceVL/server.py にも同様の --max-message-bytes と gRPC options を追加。
- 両serverの Recognize で request/response payload bytes を stage=ocr_grpc ログ出力。
- PaddleGrpcOcrProvider / PaddleVlGrpcOcrProvider に送信前PNG payload bytesログを追加。

### Files Touched
- OcrService/server.py — gRPC message上限設定とrequest/response bytesログを追加。
- OcrServiceVL/server.py — gRPC message上限設定とrequest/response bytesログを追加。
- Services/PaddleGrpcOcrProvider.cs — 送信payload bytesログを追加。
- Services/PaddleVlGrpcOcrProvider.cs — 送信payload bytesログを追加。

### Behavioral Impact
- 既定で server受信上限が32MBになり、4MB上限由来の ResourceExhausted を回避しやすくなる。
- OCR実行ごとに payloadサイズログが出力され、運用時のサイズ超過原因追跡が可能になる。

### Risk & Mitigation
- Risk: 上限引き上げにより巨大payload送信時のメモリ使用量が増える可能性。
- Mitigation: 32MBを初期値とし、--max-message-bytes で環境に合わせて調整可能にした。
- Risk: ログ量が増える可能性。
- Mitigation: 出力はbytes値の1行に限定し、解析に必要な最小情報に留めた。

### Tests / Verification
- dotnet build Hotkey-Translator.csproj 実行: 0 warning / 0 error。
- uv run --project OcrService python -m py_compile OcrService/server.py 実行: 成功。
- uv run --project OcrServiceVL python -m py_compile OcrServiceVL/server.py 実行: 成功。
**2026-02-14 17:01 (Asia/Taipei) — PaddleOCR-VL layout-detectionパーサー実装案を追加**

### Summary
- `--use-layout-detection` 時のHTML/画像参照混入を抑えるための実装計画を `Doc/` に新規作成した。

### Context / Goal
- `test4.png` 検証で `<div><img ...>` 由来文字列がOCR結果に混入し、オーバーレイ表示品質を低下させていた。
- 本件に対して、座標変更ではなく「テキスト抽出パーサーの選別強化」を段階実装できる計画を明文化する。

### Changes
- `Doc/Ocr_VL_LayoutDetection_Parser_Plan.md` を新規作成。
- ゴール/非ゴール、提案アーキテクチャ、実装ステップ、リスク、DoD を定義。
- allow-list/rejectルール、fallback厳格化、観測ログ設計を明記。

### Files Touched
- `Doc/Ocr_VL_LayoutDetection_Parser_Plan.md` — layout-detection時のPaddleOCR-VLパーサー改善計画を追加。

### Behavioral Impact
- ドキュメント追加のみ。実行時挙動の変更なし。

### Risk & Mitigation
- Risk: 実装時にallow-listが過剰だと正当テキストを欠落させる可能性。
- Mitigation: 計画にラベル分布の事前観測（Step1）と段階調整を含めた。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。
**2026-02-14 17:11 (Asia/Taipei) — PaddleOCR-VL layout-detection向けパーサー選別を実装**

### Summary
- `OcrServiceVL/ocr_vl_engine.py` に text選別・HTML/画像参照除外・観測ログを追加し、`--use-layout-detection` 時の添付文字列混入を抑止する実装を行った。

### Context / Goal
- `--use-layout-detection` 有効時に `<div ...><img ...>` 由来の非テキストが OCR line に混入し、オーバーレイ表示を汚染していた。
- 非テキスト断片を返さない一方で、既存JSON契約 (`lines[].text/box/confidence`) を維持する。

### Changes
- `block_label` 正規化と text-like 判定 (`_is_text_like_block`) を追加（非テキスト明示ラベルのみ除外）。
- `block_content` / `text` に対する正規化 (`_sanitize_block_text`) と HTML/画像参照パターン除外 (`_looks_like_markup_or_asset_ref`) を追加。
- `parsing_res_list` 解析で reject理由（label/markup/empty/box）を計測し、`stage=ocr_vl_parser` ログを追加。
- `parse_line_list` と markdown fallback でも同じ除外ルールを適用し、`<img>` 系断片をライン化しないよう統一。

### Files Touched
- `OcrServiceVL/ocr_vl_engine.py` — layout-detection時のテキスト選別/除外ロジックと統計ログを実装。

### Behavioral Impact
- OCR結果の `lines` から HTMLタグ・画像参照断片が除外される。
- `parsing_res_list` がある場合、採用/除外統計をログで観測できる。
- `--use-layout-detection` でモデル側がテキストを返さないケースは `lines=[]` となる（非テキスト混入より優先）。

### Risk & Mitigation
- Risk: フィルタ過剰で正当テキストが除外される可能性。
- Mitigation: 非テキスト明示ラベルのみ除外し、未知ラベルは許容。reject内訳ログで継続調整可能にした。
- Risk: `use_layout_detection=True` かつモデル側無検出時に0件応答となる。
- Mitigation: これは現行モデル応答（`parsing_res_list=[]`, markdown空）に準拠し、誤テキスト返却を防ぐ設計を優先した。

### Tests / Verification
- `uv run --project OcrServiceVL python -m py_compile OcrServiceVL/ocr_vl_engine.py` 実行: 成功。
- モック検証（`_parse_parsing_res_list`）: `image`ラベルのHTML断片を除外し、`text`ラベルのみ採用されることを確認。
- `uv run --project OcrServiceVL OcrServiceVL/test_ocr_vl_engine.py OcrServiceVL/.testpic/test4.png ... --no-use-layout-detection` 実行: `recognized_lines=1` を確認。
- `uv run --project OcrServiceVL OcrServiceVL/test_ocr_vl_engine.py OcrServiceVL/.testpic/test4.png ... --use-layout-detection` 実行: `recognized_lines=0`（モデル応答が空）を確認。
- `dotnet build Hotkey-Translator.csproj` 実行: 失敗（`Hotkey-Translator.exe` が実行中プロセスにロックされコピー不可）。
**2026-02-14 17:24 (Asia/Taipei) — test_ocr_vl_engineにPaddleOCR-VL生出力ダンプ引数を追加**

### Summary
- `OcrServiceVL/test_ocr_vl_engine.py` に raw出力確認用オプションを追加し、PaddleOCR-VLの生レスポンスを表示/保存できるようにした。

### Context / Goal
- parser調整前後の比較で、正規化前の `page/json/res/markdown` を直接確認したい要望があった。
- 既存の認識結果出力 (`lines`) は維持しつつ、必要時のみ raw を取れるようにする。

### Changes
- CLI引数 `--dump-raw`, `--raw-output-json`, `--raw-pretty` を追加。
- `predict_iter` の生ページを取得する `_predict_pages` を追加（テストスクリプト内のみ）。
- 生ページのJSON化ヘルパー (`_to_jsonable`, `_extract_raw_page`) を追加。
- `--dump-raw` 時は raw を先に表示/保存し、その同一推論ページから `lines` を生成して通常の件数表示も継続。

### Files Touched
- `OcrServiceVL/test_ocr_vl_engine.py` — rawダンプ用引数と生ページ抽出ロジックを追加。

### Behavioral Impact
- 通常実行（`--dump-raw` なし）の挙動は変更なし。
- `--dump-raw` ありの場合、正規化前のPaddleOCR-VL生出力を標準出力/JSONファイルに保存できる。

### Risk & Mitigation
- Risk: raw JSON が大きくなり標準出力が読みにくくなる可能性。
- Mitigation: `--raw-output-json` を用意し、ファイル保存運用を可能にした。
- Risk: テストスクリプトが内部属性（`_engine`, `_predict_kwargs`, `_extract_lines`）に依存する。
- Mitigation: 本変更はテスト専用スクリプトに限定し、本番サーバコードへは影響させない。

### Tests / Verification
- `uv run --project OcrServiceVL python -m py_compile OcrServiceVL/test_ocr_vl_engine.py` 実行: 成功。
- `uv run --project OcrServiceVL OcrServiceVL/test_ocr_vl_engine.py --help` 実行: 追加引数の表示を確認。
**2026-02-14 17:41 (Asia/Taipei) — 初回起動時のPaddleOCR-VL既定値を現行運用値へ調整**

### Summary
- `settings.json` が未作成の初回起動時のみ、PaddleOCR-VL の既定値を現在運用中の推奨値に合わせて適用するようにした。

### Context / Goal
- 初回起動直後のPaddleOCR-VL設定を、現在の `settings.json` で安定運用している値へ揃えたい。
- 既存ユーザーの保存済み設定は上書きせず、初回のみ反映したい。

### Changes
- `SettingsService.LoadAsync()` の `loaded is null` 分岐で `new AppSettings()` 直返しをやめ、`CreateFirstRunDefaults()` を経由するよう変更。
- `CreateFirstRunDefaults()` を追加し、PaddleOCR-VL 初期値を以下に設定:
  - `PaddleVlMaxPixels=500000`
  - `PaddleVlMaxNewTokens=512`
  - `PaddleVlMergeLayoutBlocks=false`
  - `PaddleVlUseLayoutDetection=true`
  - `PaddleVlEnableHpi=false`
  - `PaddleVlLayoutThreshold=null`
  - `PaddleVlUseOcrForImageBlock=null`
  - `PaddleVlUseTensorrt=null`

### Files Touched
- `Services/SettingsService.cs` — 初回起動時のPaddleOCR-VL既定値注入ロジックを追加。

### Behavioral Impact
- `settings.json` が存在しない新規環境でのみ、PaddleOCR-VLの初期状態が現行推奨値で立ち上がる。
- 既存の `settings.json` を持つ環境の挙動は変わらない。

### Risk & Mitigation
- Risk: 初回値変更により従来デフォルトとの差分が発生する。
- Mitigation: 適用対象を「`loaded is null` のみ」に限定し、既存設定を保護した。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj -p:UseAppHost=false` 実行: 成功（0 warning / 0 error）。
**2026-02-14 17:54 (Asia/Taipei) — PaddleOCR-VL UI露出（LayoutDetection/Precision）実装案を追加**

### Summary
- `PaddleVlUseLayoutDetection` と `PaddleVlPrecision` をUI公開するための実装計画を `Doc/` に新規作成した。

### Context / Goal
- 現状は `PaddleOCR-VL` の一部ランタイム設定のみUIで変更可能で、`UseLayoutDetection` と `Precision` は非公開。
- 運用時に画像特性や環境差へ即応できるよう、UIから設定変更可能にする実装手順を整理する。

### Changes
- 新規ドキュメント `Doc/PaddleOCR_VL_RuntimeControls_UI_Exposure_Plan.md` を作成。
- `bool?` を維持するため `UseLayoutDetection` を `Auto/ON/OFF` の3値UIとする設計を明記。
- `Precision(fp16/fp32)` のUI露出、ViewModel配線、設定正規化、検証手順をステップ化。

### Files Touched
- `Doc/PaddleOCR_VL_RuntimeControls_UI_Exposure_Plan.md` — PaddleOCR-VL Runtime Controls UI露出の実装案を追加。

### Behavioral Impact
- ドキュメント追加のみ。実行時挙動の変更なし。

### Risk & Mitigation
- Risk: 実装時に `bool?` を2値化して `Auto(null)` 意味を失う可能性。
- Mitigation: 計画内で `ComboBox(auto/true/false)` の3値UIを前提に定義した。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。
**2026-02-14 17:58 (Asia/Taipei) — PaddleOCR-VLのLayoutDetection/PrecisionをUI公開**

### Summary
- `Settings > PaddleOCR-VL` に `Layout detection` と `Precision` を追加し、保存/再起動で反映できるようにした。

### Context / Goal
- `PaddleVlUseLayoutDetection` と `PaddleVlPrecision` は設定値として存在するがUI未露出で、運用中に切替できなかった。
- 既存MVVM保存フローを維持しつつ、UIから変更可能にする。

### Changes
- `MainWindow.xaml` の PaddleOCR-VL セクションへ以下を追加。
  - `Layout detection` (`Auto/Enable/Disable` -> `auto/true/false`)
  - `Precision` (`fp16/fp32`)
- `SettingsViewModel` に以下を追加。
  - `PaddleVlUseLayoutDetectionModeTag` / `PaddleVlPrecisionTag` のObservableProperty
  - `LoadFrom` / `ApplyTo` の相互変換配線
  - 変更時の保存トリガ (`On...Changed`)
  - 変換ヘルパー（3値bool<->tag, precision正規化）
- `PaddleOcrSettingsRule` にCPU時の `fp16` 安全補正を追加。
  - `PaddleVlDevice` が `cpu*` かつ `PaddleVlPrecision=fp16` の場合は `fp32` に正規化。

### Files Touched
- `MainWindow.xaml` — PaddleOCR-VL設定UIに `Layout detection` / `Precision` を追加。
- `ViewModels/SettingsViewModel.cs` — 新規UIプロパティとLoad/Apply/保存トリガを追加。
- `Services/Settings/Rules/PaddleOcrSettingsRule.cs` — CPU+fp16の安全補正を追加。

### Behavioral Impact
- ユーザーはUIから `PaddleVlUseLayoutDetection` と `PaddleVlPrecision` を変更できる。
- `Layout detection` は `Auto(null)` を保持できる（3値UI）。
- `device=cpu` で `precision=fp16` を保存しても、正規化で `fp32` に補正される。

### Risk & Mitigation
- Risk: 2値UIにすると `Auto(null)` を失う可能性。
- Mitigation: `ComboBox(auto/true/false)` で3値を明示保持。
- Risk: CPU環境で `fp16` が不安定。
- Mitigation: 設定正規化で `fp32` へ自動補正。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj -p:UseAppHost=false` 実行: 成功（0 warning / 0 error）。
**2026-02-14 18:12 (Asia/Taipei) — PaddleOCR-VL時のみline mergeをスキップ**

### Summary
- `OcrAndGroupStage` で `OcrEngineKind.PaddleVllm` の場合に限り line merge を通さないように変更した。

### Context / Goal
- PaddleOCR-VL は出力が既に段落寄りで、汎用 merge を重ねると過結合しやすい。
- VL経路のみ既定で merge OFF 相当の挙動にして可読性低下を抑える。

### Changes
- `Services/Orchestration/Stages/OcrAndGroupStage.cs` の groupedLines 生成を分岐。
  - `PaddleVllm` のとき: `mappedLines` をそのまま採用。
  - それ以外: 既存どおり `_lineGrouper.MergeLines(...)` を適用。
- WHYコメントを追加して実装意図（VLでの過結合回避）を明示。

### Files Touched
- `Services/Orchestration/Stages/OcrAndGroupStage.cs` — PaddleVllm時のみmergeスキップ分岐を追加。

### Behavioral Impact
- OCRエンジンが `PaddleVllm` のとき、line mergeは実行されない。
- WinRT/Paddleなど他エンジンのmerge挙動は変更なし。

### Risk & Mitigation
- Risk: 既存のVL運用でmerge前提に調整していた表示が変わる可能性。
- Mitigation: 変更対象を `PaddleVllm` のみへ限定し、他エンジンへの影響を遮断した。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj -p:UseAppHost=false` 実行: 成功（0 warning / 0 error）。
**2026-02-14 18:35 (Asia/Taipei) — Home統合+サイドパネル+下部ドロワーUI実装案を追加**

### Summary
- `Main/Settings` 廃止と `Home + サイドパネル + 下部ドロワー` への移行計画を `Doc/` に新規作成した。

### Context / Goal
- 操作画面と設定画面の分断を解消し、UI探索コストを下げる。
- OCRプレビューとログを全画面共通で引き出せる導線へ再設計する。

### Changes
- `Doc/Home_SidePanel_BottomDrawer_UI_Plan.md` を新規作成。
- 段階導入（レイアウト骨格→ドロワー→配線再接続→回帰確認）の実装手順を定義。
- 影響範囲、リスク、DoDを明記。

### Files Touched
- `Doc/Home_SidePanel_BottomDrawer_UI_Plan.md` — UI統合・下部ドロワー構成の実装案を追加。

### Behavioral Impact
- ドキュメント追加のみ。実行時挙動の変更なし。

### Risk & Mitigation
- Risk: 実装時にXAML変更が大きく、レイアウト回帰が起きる可能性。
- Mitigation: 計画内で段階導入と各段階のビルド/手動確認を必須化した。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。
**2026-02-14 18:44 (Asia/Taipei) — Home UI計画をPreview/Log同時表示方針へ更新**

### Summary
- `Doc/Home_SidePanel_BottomDrawer_UI_Plan.md` を更新し、下部ドロワーを `Preview/Log` 同時表示（2ペイン）前提に変更した。

### Context / Goal
- ログとプレビューを同時に見たい運用要件に合わせ、排他切替案から同時表示案へ計画を修正する。
- 比率調整と個別折りたたみを組み合わせ、観測性と作業性を両立する。

### Changes
- 概要/ゴール/アーキテクチャを「2ペイン同時表示 + GridSplitter可変」へ更新。
- UI状態/コマンド設計を `BottomPreviewPaneVisible`, `BottomLogPaneVisible`, 比率保持へ更新。
- 実装手順を「切替UI」から「同時表示+Splitter+個別折りたたみ」へ変更。
- リスクに同時表示時の負荷増加を追加し、更新間引きの緩和策を追記。
- DoD を同時表示・比率変更・個別折りたたみ対応へ更新。

### Files Touched
- `Doc/Home_SidePanel_BottomDrawer_UI_Plan.md` — 下部ドロワー方針を同時表示モデルへ更新。

### Behavioral Impact
- ドキュメント更新のみ。実行時挙動の変更なし。

### Risk & Mitigation
- Risk: 実装時に同時表示で描画負荷が増える可能性。
- Mitigation: 計画で非表示ペインの更新間引きを明示した。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。
**2026-02-14 18:50 (Asia/Taipei) — Home UI計画に運用前提（最新保持/非永続/欠落許容/左サイド）を反映**

### Summary
- `Doc/Home_SidePanel_BottomDrawer_UI_Plan.md` にユーザー指定の運用前提と、実装時の不変条件を反映した。

### Context / Goal
- プレビュー/ログ同時表示方針を具体化する際、更新戦略・状態管理・ログ保証範囲を明示して実装ブレを防ぐ必要があった。
- 指定された前提（最新のみ保持、ドロワー状態非永続、起動初期ログ欠落許容、左サイド固定）を計画に固定化する。

### Changes
- 前提・仮定へ以下を追加。
  - サイドパネル左固定
  - プレビュー最新のみ保持（フレーム間引き許容）
  - ドロワー状態を `settings.json` 非保存
  - 起動初期ログの全件表示非保証
- レイアウト構成を「左サイドパネル + 中央/右コンテンツ」に明記。
- UI状態に不変条件（両ペイン同時折りたたみ禁止、比率クランプ、非永続）を追加。
- 表示仕様/実装手順/リスク/DoDを同前提に合わせて更新。

### Files Touched
- `Doc/Home_SidePanel_BottomDrawer_UI_Plan.md` — 運用前提と不変条件、DoDを更新。

### Behavioral Impact
- ドキュメント更新のみ。実行時挙動の変更なし。

### Risk & Mitigation
- Risk: 実装時に状態管理の不変条件が漏れ、両ペイン非表示などの不整合が起こる可能性。
- Mitigation: 計画にMUST不変条件を明文化し、DoDに自動補正要件を追加した。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。
**2026-02-14 19:08 (Asia/Taipei) — Homeサイドバー/下部ドロワーUI実装（開閉・個別表示・最新プレビュー保持）**

### Summary
- `Doc/Home_SidePanel_BottomDrawer_UI_Plan.md` に沿って、Home統合UIの下部ドロワー操作性とプレビュー更新制御を実装した。

### Context / Goal
- Main/Settings分断を解消した新レイアウト上で、Preview/Log を常時アクセス可能かつ同時表示できる導線が必要だった。
- 指定要件（左サイド固定、ドロワー状態非永続、起動初期ログ欠落許容、プレビュー最新保持）を満たす実装に寄せる。

### Changes
- `MainWindowViewModel` に下部ドロワー状態を追加（開閉、Preview表示、Log表示）し、不変条件を実装。
- ドロワーが開いている時に両ペイン同時非表示にならない自動補正を追加。
- `MainWindow.xaml` の下部領域を状態連動化し、Preview/Log 個別表示と同時表示を切替可能にした。
- ステータスバーに `Drawer/Preview/Log` トグル操作を追加。
- `MainWindow.xaml.cs` のOCRプレビュー更新を「最新のみ保持」に変更し、中間フレームを破棄するようにした。
- `BooleanToGridLengthConverter` を追加し、ドロワー行高さとペイン幅の状態連動に使用。

### Files Touched
- `ViewModels/MainWindowViewModel.cs` — 下部ドロワー状態・トグルコマンド・不変条件を追加。
- `MainWindow.xaml` — Home/Settings再配置済みレイアウトに下部ドロワー開閉/個別表示トグルを実装。
- `MainWindow.xaml.cs` — OCRプレビュー更新を最新フレーム優先に変更し、可視状態でのみフラッシュする制御を追加。
- `Converters/BooleanToGridLengthConverter.cs` — bool値をGridLengthに変換するコンバータを追加。

### Behavioral Impact
- 下部ドロワーはUIから開閉可能になり、Preview/Log を個別に表示/非表示できる。
- ドロワー開時に両ペイン同時非表示にはならず、片側表示へ自動補正される。
- OCRプレビューは最新フレームのみを表示対象として処理し、連続更新時のUI詰まりを抑制する。
- ドロワー状態は `settings.json` に保存されない（セッション内のみ）。

### Risk & Mitigation
- Risk: 行高さバインディングやペイン可視制御の組み合わせでレイアウト崩れが起きる可能性。
- Mitigation: `dotnet build` でXAML/C#整合を確認し、可視状態の不変条件をViewModel側で強制。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj -p:UseAppHost=false` を実行し、0 warnings / 0 errors を確認。
**2026-02-14 20:12 (Asia/Taipei) — マルチプラットフォーム向けCapture/Hotkey/Overlay抽象化案を追加**

### Summary
- `Capture / Hotkey / Overlay` のインターフェース化に向けた大まかな実装案を `Doc/` に新規追加した。

### Context / Goal
- 将来のマルチプラットフォーム化を見据え、OS依存点をどの順で分離するかを明確化する必要があった。
- 既存Windows互換を維持しながら、`Ports & Adapters` へ段階移行する計画を定義する。

### Changes
- `Abstractions` 層を中心にした構成（interface + platform-neutral DTO）を提案。
- Windows 実装を Adapter 化し、Application 側を interface 依存へ移す段階手順を定義。
- `Capture/Hotkey/Overlay` それぞれの高レベルI/F案、リスク、DoD を整理。

### Files Touched
- `Doc/CrossPlatform_CaptureHotkeyOverlay_Abstraction_Plan.md` — 新規作成（大枠の実装計画）。

### Behavioral Impact
- ドキュメント追加のみ。実行時挙動の変更なし。

### Risk & Mitigation
- Risk: 抽象化の粒度が粗いと、実装時に責務境界が再び曖昧になる可能性。
- Mitigation: Step分割とDoDを明示し、Windows互換確認を各ステップの出口条件にした。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。
**2026-02-14 20:25 (Asia/Taipei) — Drawer UX改善案（2ボタン化 + 表示時自動高さ拡張）を追加**

### Summary
- `Preview/Log` 2ボタン導線と、Drawer表示時のWindow自動高さ拡張を含む実装案を `Doc/` に新規追加した。

### Context / Goal
- 現状の `Drawer/Preview/Log` 3ボタン構成は操作意図が分散し、UX上の迷いが生じやすい。
- Drawer表示時に上部設定領域が圧縮される課題を緩和し、被せ感の少ない表示にする。

### Changes
- `Drawer` ボタンを廃止し、`Preview/Log` トグルだけで Drawer 開閉を導出する操作モデルを定義。
- Drawer open/close に連動した Window 高さ自動拡張・復元（WorkingAreaクランプ付き）方針を定義。
- 実装ステップ、リスク、DoD を整理した。

### Files Touched
- `Doc/Home_BottomDrawer_UX_Adjustment_Plan.md` — 新規作成（UX改善の実装案）。

### Behavioral Impact
- ドキュメント追加のみ。実行時挙動の変更なし。

### Risk & Mitigation
- Risk: 自動リサイズと手動リサイズが干渉し、Windowサイズが不安定になる可能性。
- Mitigation: 自動加算分のみ追跡して復元する設計を計画に明記した。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。
**2026-02-14 20:38 (Asia/Taipei) — Bottom Drawer UX調整実装（2ボタン化 + 表示時自動高さ拡張）**

### Summary
- `Doc/Home_BottomDrawer_UX_Adjustment_Plan.md` に基づき、Drawer操作を `Preview/Log` 2ボタンへ統合し、表示時にWindow高さを自動拡張する挙動を実装した。

### Context / Goal
- `Drawer/Preview/Log` の3ボタン導線は操作意図が分散し、UX上の迷いが出やすかった。
- Drawer表示時に上部設定領域が圧縮されるため、表示時にWindow高さを安全に増やして圧迫感を減らす必要があった。

### Changes
- `MainWindow.xaml` のステータスバーから `Drawer` ボタンを削除し、`Preview` / `Log` の2ボタン導線へ変更。
- `MainWindowViewModel` から `ToggleBottomPanelCommand` を削除し、`Preview/Log` 表示状態から `IsBottomPanelOpen` を導出する遷移へ変更。
- 両ペインOFF時はDrawerを自動クローズ、いずれかON時はDrawerを自動オープンする同期ロジックへ更新。
- `MainWindow.xaml.cs` に Drawer open/close 連動の Window 高さ自動拡張/復元を追加。
- 自動拡張は `WorkingArea` クランプ付きで適用し、復元は「自動拡張分のみ」を戻すことで手動リサイズを尊重するようにした。
- 起動直後（Loaded）にもDrawer表示状態に応じて自動拡張が走るよう同期呼び出しを追加。

### Files Touched
- `MainWindow.xaml` — Drawerボタン削除、下部Drawerホストに `x:Name` を追加。
- `ViewModels/MainWindowViewModel.cs` — 2ボタン導線へ状態遷移を変更、不要コマンド削除。
- `MainWindow.xaml.cs` — Drawer開閉時のWindow自動リサイズ制御を追加。

### Behavioral Impact
- UI操作は `Preview` と `Log` の2ボタンに統一された。
- `Preview` / `Log` のどちらかをONにするとDrawerが自動表示される。
- `Preview` / `Log` を両方OFFにするとDrawerが自動非表示になる。
- Drawer表示時にWindow高さが可能な範囲で拡張され、上部設定領域の圧縮が緩和される。

### Risk & Mitigation
- Risk: 自動リサイズとユーザー手動リサイズが衝突し、閉じる時に不自然なサイズ戻りが起こる可能性。
- Mitigation: 自動拡張した目標高さと差分を追跡し、復元は近傍一致時に限定した。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj -p:UseAppHost=false` を実行し、0 warnings / 0 errors を確認。
**2026-02-14 21:16 (Asia/Taipei) — OCRプレビューのクリック拡大ウィンドウ（ホイールズーム）を追加**

### Summary
- Drawer内OCRプレビューをクリックすると、別ウィンドウで拡大表示できる機能を追加した（ホイールズーム対応）。

### Context / Goal
- Drawer高さを固定寄りにする運用でも、プレビュー詳細確認の導線を確保する必要があった。
- まずは最小仕様として、クリックで拡大表示 + ホイールズームのみを実装する。

### Changes
- `MainWindow.xaml` の OCR Preview コンテナにクリックイベントを追加。
- `MainWindow.xaml.cs` に拡大ウィンドウの単一インスタンス管理を追加（生成/再表示/クローズ時解放）。
- OCRプレビュー更新時に、開いている拡大ウィンドウへ最新画像を同期するようにした。
- `UI/OcrPreviewZoomWindow.xaml(.cs)` を新規作成し、以下を実装。
  - 画像表示（ScrollViewer + Image）
  - ホイールズーム（0.2x〜8.0x）
  - 画像未取得時プレースホルダ表示
  - 現在ズーム率表示

### Files Touched
- `MainWindow.xaml` — OCRプレビュークリックイベントを追加。
- `MainWindow.xaml.cs` — 拡大ウィンドウ起動/同期/解放ロジックを追加。
- `UI/OcrPreviewZoomWindow.xaml` — 拡大表示ウィンドウを新規追加。
- `UI/OcrPreviewZoomWindow.xaml.cs` — ホイールズームと表示状態制御を新規追加。

### Behavioral Impact
- Drawer内OCRプレビューをクリックすると、拡大ウィンドウが開く。
- 拡大ウィンドウは単一インスタンスで再利用される。
- OCR更新時は拡大ウィンドウ表示中の画像も最新に追従する。
- ドラッグ移動（パン）は未実装。

### Risk & Mitigation
- Risk: 複数拡大ウィンドウが開いて更新同期が複雑化する可能性。
- Mitigation: 単一インスタンス運用に固定し、Close時に参照を解放する実装にした。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj -p:UseAppHost=false -o .\\bin\\_verify_build` を実行し、0 warnings / 0 errors を確認。
**2026-02-14 21:22 (Asia/Taipei) — MainWindow.xaml.cs 第2ラウンド再リファクタ案を追加**

### Summary
- `MainWindow.xaml.cs` の再肥大化対策として、Round2 の段階的リファクタリング実装案を `Doc/` に新規追加した。

### Context / Goal
- Drawer制御とPreview拡大制御の追加で `MainWindow.xaml.cs` への責務再集約が進み、将来変更時の衝突リスクが上がっていた。
- 境界責務を維持しつつ、画面固有ロジックを専用コンポーネントへ抽出する計画を定義する。

### Changes
- `MainWindow` に残す責務（Composition Root / Lifecycle / WPF境界）を前提にした再分割方針を記載。
- `DrawerLayoutController` / `PreviewZoomCoordinator` / `PreviewFrameDispatcher` 抽出案を定義。
- partial分割 -> controller移送 -> 回帰確認までのステップを具体化。
- リスク、緩和策、DoD を今回の再肥大化課題に合わせて整理。

### Files Touched
- `Doc/MainWindow_XamlCs_Refactoring_Round2_Plan.md` — 新規作成（Round2実装案）。

### Behavioral Impact
- ドキュメント追加のみ。実行時挙動の変更なし。

### Risk & Mitigation
- Risk: 計画だけで止まり、分割しても実質改善が出ない可能性。
- Mitigation: DoDに「partial分割のみで完了にしない」条件を明示した。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。
**2026-02-14 21:33 (Asia/Taipei) — MainWindow.xaml.cs Round2: Drawer/Preview責務を専用コンポーネントへ抽出**

### Summary
- `Doc/MainWindow_XamlCs_Refactoring_Round2_Plan.md` に沿って、Drawerレイアウト制御・Preview拡大制御・Previewフレーム反映を `MainWindow` から抽出した。

### Context / Goal
- `MainWindow.xaml.cs` に Drawer/Preview の詳細ロジックが再集約し、変更衝突と回帰リスクが高まっていた。
- Window境界責務を維持しつつ、画面固有ロジックをサービスへ分離して保守性を改善する。

### Changes
- `DrawerLayoutController` を新規追加し、Drawer開閉時のWindow高さ自動拡張/復元ロジックを移管。
- `PreviewZoomCoordinator` を新規追加し、拡大プレビューWindowの単一インスタンス管理を移管。
- `PreviewFrameDispatcher` を新規追加し、OCRプレビューの latest-only キュー/ディスパッチを移管。
- `MainWindow` は各コンポーネントを生成・購読・破棄する境界責務へ縮退。
- `OnOcrPreprocessPreviewReady` / `OnMainWindowViewModelPropertyChanged` / `OnOcrPreviewClicked` を `MainWindow.Preview.cs` に分離。
- `Doc/MainWindow_View_Boundary.md` を更新し、今回移管した責務を明記。

### Files Touched
- `MainWindow.xaml.cs` — Drawer/Preview詳細処理を削減し、サービス委譲へ変更。
- `MainWindow.Preview.cs` — Preview関連イベントと反映橋渡しを分離。
- `Services/Application/DrawerLayoutController.cs` — Drawer自動リサイズ制御を新規追加。
- `Services/Application/PreviewZoomCoordinator.cs` — 拡大プレビューWindow制御を新規追加。
- `Services/Application/PreviewFrameDispatcher.cs` — latest-onlyプレビュー反映制御を新規追加。
- `Doc/MainWindow_View_Boundary.md` — 境界責務の最新状態に更新。

### Behavioral Impact
- 既存機能（Drawer開閉時の高さ調整、プレビュークリック拡大、最新プレビュー反映）は互換動作を維持。
- 実装責務が分離されたことで、今後のDrawer/Preview変更が `MainWindow.xaml.cs` へ波及しにくくなった。

### Risk & Mitigation
- Risk: 抽出時のイベント購読/解除漏れで二重反応やリークが起きる可能性。
- Mitigation: `MainWindow.OnClosed` で `PreviewZoomCoordinator.Dispose()` / `PreviewFrameDispatcher.Dispose()` / `DrawerLayoutController.Reset()` を明示実行する構造にした。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj -p:UseAppHost=false -o .\\bin\\_verify_build` を実行し、0 warnings / 0 errors を確認。
**2026-02-14 21:48 (Asia/Taipei) — Translation設定の優先度UIを最上段へ移動し見出し/ステータスを削除**

### Summary
- Translation設定パネルの先頭に `Translation priority` を移動し、`Translation (Advanced)` 見出しと同パネル内ステータス表示を削除した。

### Context / Goal
- Translation設定の操作導線を簡素化し、最頻操作である優先度変更を最上段に配置する必要があった。
- ステータス表示は既に下部バーに存在するため、設定パネル内の重複表示を除去する。

### Changes
- `SettingsPanelTranslation` 冒頭の `Translation (Advanced)` 見出しを削除。
- `RuntimeStatus.TranslationStatusMessage` を表示していた説明行を削除。
- `TranslationPriorityList` ブロック（ラベル + ListBox + Up/Down）を `SettingsPanelTranslation` の最上段へ移動。
- 下段にあった既存の `Translation priority` ブロックを削除（重複回避）。

### Files Touched
- `MainWindow.xaml` — Translation設定セクションの並び替えと不要表示の削除。

### Behavioral Impact
- Translation設定を開いた際、優先度順編集が最初に見えるようになった。
- 設定パネル内の Translation status 表示は消え、下部ステータスバー表示のみになる。

### Risk & Mitigation
- Risk: XAMLブロック移動時に `x:Name=TranslationPriorityList` の重複や参照崩れが起きる可能性。
- Mitigation: 旧ブロックを同時削除し、ビルドでXAML整合を確認した。

### Tests / Verification
- `dotnet build Hotkey-Translator.csproj -p:UseAppHost=false -o .\\bin\\_verify_build` を実行し、0 warnings / 0 errors を確認。
**2026-02-14 22:02 (Asia/Taipei) — OCR設定パネルを2カラム化**

### Summary
- OCR設定パネルを2カラムレイアウトへ変更し、視認性を改善した。

### Context / Goal
- OCR設定項目が縦に長く、一覧性が低下していた。
- Paddle/Paddle-VL 設定は現状のまま維持し、OCRパネルのみを先行して整理する。

### Changes
- SettingsPanelOcr の直下に2カラム Grid を追加。
- 左カラムに OCR (Advanced) と Performance を配置。
- 右カラムに OCR Preprocess (Binarization) 以降の既存設定群を配置。
- 既存の設定バインディング・x:Name は変更せず維持。

### Files Touched
- MainWindow.xaml — SettingsPanelOcr を2カラム構成へ変更（Paddle/Paddle-VL 側は未変更）。

### Behavioral Impact
- OCR設定タブで同時に見える情報量が増え、スクロール依存が軽減される。
- 設定値の保存・反映ロジックは変更なし。

### Risk & Mitigation
- Risk: XAMLレイアウト再構成により表示崩れが発生する可能性。
- Mitigation: 既存コントロール定義は保持し、dotnet build でXAML整合を確認済み。

### Tests / Verification
- dotnet build Hotkey-Translator.csproj -p:UseAppHost=false -o .\\bin\\_verify_build を実行し、0 warnings / 0 errors を確認。
**2026-02-14 22:12 (Asia/Taipei) — OCR設定の左右配置を再調整（Performance右・前処理/自動化左）**

### Summary
- OCR設定2カラムの配置を再編し、Performance を右列へ、OCR Preprocess と Scene Change Automation を左列へ移動した。

### Context / Goal
- 操作頻度と設定の意味で、前処理・自動翻訳/非表示の自動化を左側に寄せたい要望があった。
- 既存のバインディングや制御ロジックは維持したまま、視認性のみ改善する。

### Changes
- SettingsPanelOcr を2カラム Grid 構成のまま、セクション配置を入れ替え。
- 左列: OCR (Advanced) + OCR Preprocess (Binarization) + Scene Change Automation。
- 右列: Performance + OCR Input + Overlay Layout。
- 途中で発生したXAML文字列破損を復旧し、既存日本語表示を維持。

### Files Touched
- MainWindow.xaml — OCR設定セクションの左右再配置と整合修正。

### Behavioral Impact
- OCR設定画面で前処理/自動化関連が左列に集約され、Performance は右列へ移動。
- 機能仕様・保存値・コマンド動作は変更なし。

### Risk & Mitigation
- Risk: 大きなXAMLブロック移動時のレイアウト崩れや要素参照切れ。
- Mitigation: x:Name とバインディングを維持し、dotnet build でXAML整合を確認。

### Tests / Verification
- dotnet build Hotkey-Translator.csproj -p:UseAppHost=false -o .\\bin\\_verify_build を実行し、0 warnings / 0 errors を確認。
**2026-02-14 22:19 (Asia/Taipei) — サイドバー見出し削除と全高追従レイアウト化**

### Summary
- 左サイドバーのタイトルを削除し、サイドバー内リストがウィンドウ高さに追従するようにした。

### Context / Goal
- サイドバーの見出し行をなくし、表示領域を有効活用したい。
- 左枠を右ペイン同様に高さいっぱいで使い、ウィンドウサイズ変更時に自然に追従させたい。

### Changes
- Sidebar タイトル (TextBlock) を削除。
- サイドバー内部コンテナを StackPanel から Grid に変更。
- HomeSidebarList に VerticalAlignment="Stretch" を付与し、高さ追従を明示。

### Files Touched
- MainWindow.xaml — 左サイドバーの見出し削除と全高追従レイアウトへ変更。

### Behavioral Impact
- 左サイドバーの先頭見出しが表示されなくなる。
- サイドバー領域がウィンドウ高に合わせて伸縮し、リスト表示の追従性が上がる。

### Risk & Mitigation
- Risk: サイドバー項目数が増えた場合に表示領域を超える可能性。
- Mitigation: ListBox の既定スクロール挙動を維持しているため、超過時はスクロールで対応できる。

### Tests / Verification
- dotnet build Hotkey-Translator.csproj -p:UseAppHost=false -o .\\bin\\_verify_build を実行し、0 warnings / 0 errors を確認。
**2026-02-14 22:41 (Asia/Taipei) — Bottom DrawerにPinnedサムネタブを実装**

### Summary
- Drawer左ペインを OCR/Pinned タブ化し、固定ウィンドウの1回サムネ表示と解除/失敗メッセージ表示を実装した。

### Context / Goal
- 右側Log固定のまま、左側でOCRプレビューと固定サムネを切替表示したい。
- 固定サムネは即時更新ではなく、固定時の1回取得のみで十分という要件。

### Changes
- Drawer左ペインの GroupBox 内を TabControl 化し、OCR と Pinned の2タブを追加。
- Pinned タブに PinnedCaptureThumbnailImage と PinnedCaptureThumbnailHint を追加し、空状態メッセージを表示可能にした。
- ロックホットキー処理の戻り値を FixedCaptureWindowSpec? に変更し、成功時に1回だけサムネ取得する導線を追加。
- 解除ホットキー処理後に 固定対象なし を表示するクリア処理を追加。
- サムネ取得失敗時はログ出力し、UIに サムネ取得失敗 を表示するフォールバックを追加。

### Files Touched
- MainWindow.xaml — Drawer左ペインを OCR/Pinned タブUIへ変更。
- MainWindow.Preview.cs — Pinnedサムネ更新/クリア/取得処理とクリック拡大イベントを追加。
- MainWindow.xaml.cs — Lock/Unlockホットキー後にPinnedサムネ状態を更新する呼び出しを追加。
- Services/Application/HotkeyCommandController.cs — ロック処理の戻り値を FixedCaptureWindowSpec? に変更。

### Behavioral Impact
- Drawer左ペインで OCR と Pinned を切替表示できる。
- 固定ロック成功時に、固定対象のサムネが Pinned タブに表示される。
- 固定解除時は 固定対象なし、取得失敗時は サムネ取得失敗 が表示される。

### Risk & Mitigation
- Risk: ウィンドウサムネ取得（CopyFromScreen）が対象状態によって失敗する可能性。
- Mitigation: 失敗時は例外をログ化し、UIは必ずメッセージ表示へフォールバックする。

### Tests / Verification
- dotnet build Hotkey-Translator.csproj -p:UseAppHost=false -o .\\bin\\_verify_build を実行し、0 warnings / 0 errors を確認。
**2026-02-14 22:53 (Asia/Taipei) — 配布方式と初回Setupウィザードの大まか案を作成**

### Summary
- Setup.exe本線 + 初回Setupウィザードのハイブリッド配布案を Doc/ に新規作成した（CTranslate2除外前提）。

### Context / Goal
- インストーラー形式と実行後Setupウィザードのどちらを採用すべきかを、配布方式まで含めて整理する必要があった。
- CTranslate2 を破棄する前提で、初期セットアップ計画から対象外にする方針を明文化する。

### Changes
- 配布方式比較（インストーラーのみ / ウィザードのみ / ハイブリッド）を整理。
- 推奨方式をハイブリッド（Setup.exe + First-run Wizard）として明記。
- 初回判定、ウィザードステップ、CTranslate2除外、DoD を含む計画書を新規追加。

### Files Touched
- Doc/Distribution_Installer_FirstRunWizard_Plan.md — 配布・初回Setup戦略の実装計画を新規作成。

### Behavioral Impact
- 実行挙動への変更はなし（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: 計画段階のため実装制約との乖離が発生する可能性。
- Mitigation: 実装時にStep単位で検証し、必要な差分を同Docへ追記更新する。

### Tests / Verification
- 未実施（ドキュメント追加のみのため）。
**2026-02-14 22:59 (Asia/Taipei) — 配布計画にuv運用方針とEULA注意を追記**

### Summary
- Distribution_Installer_FirstRunWizard_Plan に、ローカルuv運用方針とEULA/ライセンス同意フロー要件を追記した。

### Context / Goal
- Pythonフォルダ同梱 + Setup経由で依存/モデル準備する方針を計画へ反映する必要があった。
- 配布時のEULA・第三者ライセンス・モデル規約同意を先に設計へ組み込む。

### Changes
- Python/uv 運用方針（ローカル同梱、グローバル非依存、初回 uv sync）を追加。
- EULA/第三者ライセンス/モデル規約の同意設計を追加。
- 実装ステップに uv 依存同期、モデル導線、NOTICE同梱を追加。
- 非機能・リスク・DoD にコンプライアンス項目を追加。

### Files Touched
- Doc/Distribution_Installer_FirstRunWizard_Plan.md — 配布計画へuv/EULA要件を追記。

### Behavioral Impact
- 実行挙動への変更なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: 法務要件の解釈差で実装時に追加修正が必要になる可能性。
- Mitigation: リリース前に法務チェックリスト（EULA/OSS/モデル規約）を運用し、同Docへ差分反映する。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。
**2026-02-14 23:06 (Asia/Taipei) — 配布計画へWinRT OCR昇格導入フローを追記**

### Summary
- 配布計画に、WinRT OCR Capability を管理者昇格PowerShellで導入する任意フローを追加した。

### Context / Goal
- 言語パック全体ではなく OCR認識エンジンのみを導入する方針を計画へ反映する必要があった。
- EULA/注意喚起付きで安全に実行できる導線を先に定義する。

### Changes
- WinRT OCR Capability の事前チェック要件を追加。
- Add-WindowsCapability を用いた昇格PowerShell実行設計（注意ダイアログ/同意/再チェック）を追加。
- 実装ステップへ WinRT導線（任意）を追加し、後続ステップ番号を調整。
- リスク/DoD に昇格実行失敗時の緩和策と完了条件を追加。

### Files Touched
- Doc/Distribution_Installer_FirstRunWizard_Plan.md — WinRT OCR導入項目（昇格PowerShell）を追記。

### Behavioral Impact
- 実行挙動への変更なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: 管理者権限や組織ポリシーで昇格実行が失敗する可能性。
- Mitigation: 導入を任意にし、失敗時は手動導入手順と代替OCR導線を提示する方針を明記。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。
**2026-02-14 23:13 (Asia/Taipei) — 配布計画へLlamaCpp初回セットアップ要件を追記**

### Summary
- Distribution_Installer_FirstRunWizard_Plan に LlamaCpp の初回チェック/起動確認/フォールバック要件を追加した。

### Context / Goal
- 初回Setup設計で LlamaCpp を明示的に考慮し、導入失敗時でもセットアップを完了できる方針を定義する必要があった。

### Changes
- 初回ウィザード責務に Paddle/WinRT/LlamaCpp の疎通対象を明記。
- エラー/バリデーションへ Llama実行ファイル、モデル存在、起動疎通チェックを追加。
- 疎通テスト手順へ Llama起動テストを追加。
- リスク/DoD に Llama失敗時フォールバック要件を追加。

### Files Touched
- Doc/Distribution_Installer_FirstRunWizard_Plan.md — LlamaCpp考慮項目を追記。

### Behavioral Impact
- 実行挙動への変更なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: Llamaモデル未配置や起動失敗で初回設定が中断する可能性。
- Mitigation: Llamaを必須化せず、無効で続行できる導線を計画に明記。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。
**2026-02-14 23:17 (Asia/Taipei) — 配布計画へLlama同梱とローカル翻訳選択導線を反映**

### Summary
- Distribution_Installer_FirstRunWizard_Plan に、LlamaCppバイナリ同梱と初回Setupでのローカル翻訳選択 + uvモデルDL方針を反映した。

### Context / Goal
- LlamaCppバイナリーを同梱し、初回セットアップでローカル翻訳選択時にモデルを準備する運用方針を明確化する必要があった。

### Changes
- インストーラー責務へ LlamaCppバイナリ同梱を追加。
- Python/uv運用方針を「ローカル翻訳選択時にuvでLlamaモデルDL」に更新。
- ウィザードStep3を「ローカル/クラウド選択」へ拡張。
- 実装手順にローカル翻訳選択導線と LlamaモデルDL（uv経由）を追記。
- インストーラー整備ステップに Llamaバイナリ配置反映を追記。

### Files Touched
- Doc/Distribution_Installer_FirstRunWizard_Plan.md — Llama同梱/ローカル翻訳選択/uvモデルDL方針を追記。

### Behavioral Impact
- 実行挙動への変更なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: ローカル翻訳選択時のモデルDL失敗で初回導線が詰まる可能性。
- Mitigation: 既存方針どおり任意DL + 再試行 + Llama無効で続行導線を維持する。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。
**2026-02-14 23:21 (Asia/Taipei) — 配布計画の自己レビュー指摘を反映**

### Summary
- Distribution_Installer_FirstRunWizard_Plan に自己レビューで指摘した矛盾/不足（Llama順序、uv役割、ライセンス、WinRT前提、DoD分岐）を反映した。

### Context / Goal
- 計画書内の実装順矛盾と要件曖昧さを解消し、実装時の手戻りを減らす必要があった。

### Changes
- Llama疎通テスト順序を整理（Step4はモデル既存時のみ、Step6でDL後に本テスト）。
- uv sync とモデルDL処理を明確分離し、uv run <model_download_script> と検証要件を明記。
- Llama再配布ライセンスとモデル別同意記録の要件を追記。
- WinRT昇格導線に事前チェック（Capability/言語タグ/オンライン）を追加。
- DoDに「ローカル翻訳選択有無での分岐条件」を追加。

### Files Touched
- Doc/Distribution_Installer_FirstRunWizard_Plan.md — 自己レビュー反映の修正追記。

### Behavioral Impact
- 実行挙動への変更なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: 実装時にコマンド詳細が確定していない箇所が残る。
- Mitigation: uv run <model_download_script> は実装時に実体スクリプト名を確定し、同Docへ確定値を反映する。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。
**2026-02-14 23:25 (Asia/Taipei) — 配布計画へPaddle/Paddle-VLモデル導線を追記**

### Summary
- Distribution_Installer_FirstRunWizard_Plan に PaddleOCR / PaddleOCR-VL モデルダウンロード導線を明示した。

### Context / Goal
- 初回セットアップで Paddle系モデル取得が主要失敗点になるため、実装計画に明示して漏れを防ぐ必要があった。

### Changes
- 初回ウィザード責務に Paddle/Paddle-VL モデル導線の明示を追加。
- モデルDL方針を Llamaのみでなく Paddle/Paddle-VL も対象に拡張。
- エラー/バリデーションへ Paddle系モデル未導入時の案内とフォールバック方針を追記。
- 実装手順（モデル導線）とDoDに Paddle/Paddle-VL 項目を追加。

### Files Touched
- Doc/Distribution_Installer_FirstRunWizard_Plan.md — PaddleOCR/PaddleOCR-VL のモデル導入フローを追記。

### Behavioral Impact
- 実行挙動への変更なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: モデル容量増加で初回セットアップが長くなる可能性。
- Mitigation: 任意DL・進捗表示・再試行・後回し導線を維持する方針を明記。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。
**2026-02-15 00:24 (Asia/Taipei) — 実行中通知ポリシーの実装案を新規作成**

### Summary
- 実行中通知の分類方針（カテゴリ1除外、カテゴリ2維持、カテゴリ3/4追加）を Doc/Runtime_Notification_Policy_Plan.md に整理した。

### Context / Goal
- Setupを除外し、ランタイム中に表示すべき通知だけを明確化する必要があった。
- 「表示しないもの」と「要操作警告」を区別し、通知過多を防ぐ方針を固定する。

### Changes
- 通知カテゴリを4分類し、採用対象を明示。
- カテゴリ2: OCR実行中 / 翻訳実行中 は現仕様維持と定義。
- カテゴリ3: フォールバック発生 / 一時失敗から復旧 / 固定対象ロック・解除 を追加対象に定義。
- カテゴリ4: OCR/翻訳とも利用不可 / 必須依存欠落停止 / 連続失敗停止 を要操作警告に定義。
- 実装手順、リスク、DoDを通知方針に合わせて整理。

### Files Touched
- Doc/Runtime_Notification_Policy_Plan.md — 実行中通知ポリシーの実装案を新規作成。

### Behavioral Impact
- 実行挙動への変更なし（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: 通知カテゴリの境界が実装時に曖昧化する可能性。
- Mitigation: 通知コード体系（untime.fallback.* など）を計画内で定義し、発火点を固定する。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。
**2026-02-15 00:28 (Asia/Taipei) — 実行中通知計画をオーバーレイ統一方針へ修正**

### Summary
- Runtime_Notification_Policy_Plan を更新し、追加通知（カテゴリ3/4）はすべてオーバーレイ表示に統一した。

### Context / Goal
- 既存通知を維持した上で、追加通知の表示チャネルをオーバーレイへ一本化する方針に合わせる必要があった。

### Changes
- 表示面の役割からモーダル許容表現を削除し、カテゴリ4を「オーバーレイ要操作警告」に統一。
- 表示ルールに「カテゴリ3/4はモーダル不使用、すべてオーバーレイ表示」を追記。
- DoDに「カテゴリ3/4のオーバーレイ統一」確認項目を追加。

### Files Touched
- Doc/Runtime_Notification_Policy_Plan.md — 追加通知チャネルのオーバーレイ統一を追記。

### Behavioral Impact
- 実行挙動への変更なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: 要操作警告の視認性が低い実装になる可能性。
- Mitigation: カテゴリ4はオーバーレイ内でも明示スタイル（強調色/再表示抑制）を適用する前提を維持する。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。
**2026-02-15 00:37 (Asia/Taipei) — PaddleOCR-VLパーサーで先頭Markdown見出し記号を除去**

### Summary
- PaddleOCR-VLの認識テキスト先頭に混入する ##/### などの見出し記号を、パーサー正規化で除去する処理を追加した。

### Context / Goal
- PaddleOCR-VLが行頭に ## 系記号を返すケースがあり、翻訳・差分比較のノイズになっていた。
- 行中の #（例: C##）は保持しつつ、行頭Markdown由来ノイズだけを除去する必要があった。

### Changes
- OcrServiceVL/ocr_vl_engine.py に先頭見出し記号除去用の正規表現を追加。
- _sanitize_block_text を @classmethod 化し、改行単位で ##〜###### の先頭記号を除去してから空白正規化するよう変更。
- コメントを追加し、実装理由（PaddleOCR-VLのmarkdown heading混入対策）を明記。

### Files Touched
- OcrServiceVL/ocr_vl_engine.py — 先頭Markdown見出し記号除去ロジックを追加。

### Behavioral Impact
- ## / ### など行頭見出しノイズは除去される。
- 行中の # は保持されるため、C## 等の文字列は破壊しない。

### Risk & Mitigation
- Risk: 正規表現条件が厳しすぎる/緩すぎると誤除去・取りこぼしが発生する。
- Mitigation: 行頭かつ #{2,6} のみに限定し、直後が英数/CJK文字のケースだけ除去するルールにした。

### Tests / Verification
- python - で PaddleOcrVlEngine._sanitize_block_text(...) を直接実行し、以下を確認。
  - ## Hello world -> Hello world
  - ####123abc -> 123abc
  - C## language は変更なし
  - Unicodeエスケープ経由の日本語 ### 行頭は除去される
**2026-02-15 00:50 (Asia/Taipei) — 起動時Drawerの初期高さジャンプ解消**

### Summary
- 起動時にウィンドウが一度低く表示された後で伸びる挙動を、表示前の先行拡張で解消した。

### Context / Goal
- 起動直後に「短い高さ -> 拡張」の視覚ジャンプが発生していた。
- 起動時は最初から拡張後サイズで表示し、既存のDrawer開閉時の自動リサイズ挙動は維持したい。

### Changes
- DrawerLayoutController に起動時同期用 SyncStartupState() を追加。
- SyncStartupState() で Drawer が開いている場合に同期的に高さ拡張を適用するようにした。
- MainWindow コンストラクタで SyncStartupState() を呼び、初回描画前に高さを確定するようにした。

### Files Touched
- Services/Application/DrawerLayoutController.cs — 起動時同期メソッドを追加し、初回表示前拡張を可能にした。
- MainWindow.xaml.cs — DrawerLayoutController 初期化直後に起動時同期を呼ぶよう変更した。

### Behavioral Impact
- 起動時の高さジャンプが減り、初回表示が安定する。
- Drawer開閉時の既存の自動拡張/復元ロジックは継続して動作する。

### Risk & Mitigation
- Risk: 起動時に常に拡張することで、画面高さが小さい環境で収まりにくくなる可能性。
- Mitigation: 既存の WorkArea クランプ処理をそのまま利用し、画面外にはみ出さないよう維持した。

### Tests / Verification
- dotnet build を実行し、成功（Warnings 0 / Errors 0）を確認。
**2026-02-15 01:11 (Asia/Taipei) — TextBox設定入力の保存トリガーをLostFocusへ変更**

### Summary
- Settings系TextBoxの更新トリガーを PropertyChanged から LostFocus に変更し、入力途中の自動正規化反映を抑止した。

### Context / Goal
- 数値・文字入力中に自動保存と正規化再読込が走り、入力途中の小数や末尾文字が途中で確定される挙動があった。
- TextBox入力はフォーカス離脱時に確定し、編集中の文字列を保持したい。

### Changes
- MainWindow.xaml の Text="{Binding Settings...} を使うTextBoxバインディングについて、UpdateSourceTrigger=PropertyChanged を UpdateSourceTrigger=LostFocus へ変更。
- CheckBox / ComboBox / Slider / PasswordBoxAssistant.BoundPassword のトリガーは変更せず維持。

### Files Touched
- MainWindow.xaml — Settings系TextBoxのUpdateSourceTriggerをLostFocusへ変更。

### Behavioral Impact
- TextBox入力はフォーカスを離すまで設定保存対象にならない。
- 入力途中に正規化値で上書きされる頻度が低下し、直接入力の編集体験が改善される。

### Risk & Mitigation
- Risk: フォーカスを移動しないままアプリ終了すると、直前入力が保存されない可能性。
- Mitigation: 既存の明示保存ボタンと、操作遷移時のフォーカス移動で確定される運用を維持する。

### Tests / Verification
- dotnet build を実行し、成功（Warnings 0 / Errors 0）を確認。
- MainWindow.xaml 内で Text="{Binding Settings...} の PropertyChanged 残存がないことを確認。
**2026-02-15 01:17 (Asia/Taipei) — Hotkey行のGrid化でラベル見切れを解消**

### Summary
- Hotkey設定の行レイアウトを StackPanel から3列 Grid へ変更し、長いラベルの見切れを解消した。

### Context / Goal
- Hotkey画面で Force Gemini (strict) などの長いラベルが右側で切れて読みにくかった。
- ラベル列幅を安定させ、キー/修飾子の列も縦に揃えたい。

### Changes
- SettingsPanelHotkeys 内の各行を Grid レイアウトに再構成（Label / Key / Modifiers の3列）。
- ラベル列に MinWidth=170 を設定し、長文ラベルでも省略されにくい幅を確保。
- 既存の ComboBox / CheckBox の x:Name とバインディングは維持し、コードビハインド連携を破壊しないようにした。

### Files Touched
- MainWindow.xaml — Hotkeyセクションの行コンテナをGridに置換し、列レイアウトを固定化。

### Behavioral Impact
- Hotkeyラベルの視認性が改善し、行ごとの揃いが安定する。
- Hotkey設定値の保存・動作仕様自体は変更なし。

### Risk & Mitigation
- Risk: 低幅ウィンドウ時に修飾子列の横幅が不足する可能性。
- Mitigation: ラベル幅を最小値に留め、修飾子列は * で伸縮可能にしてレイアウト余地を確保。

### Tests / Verification
- dotnet build は実行中プロセスが Hotkey-Translator.exe をロックしていたため失敗（MSB3021/MSB3027）。
- dotnet build -p:UseAppHost=false でビルド成功（XAMLコンパイル含む、warning 1）。
**2026-02-15 02:33 (Asia/Taipei) — Quiet Window＋最新pending payload計画書を追加**

### Summary
- ノベルゲーム向けの Scene Change Auto-Translate 改善案（Quiet Window＋pending payload最新版採用）を Doc/ に新規出力した。

### Context / Goal
- Stage B通過直後の即時実行では、文字送り途中の途中文を拾って止まりやすい課題があった。
- UIでON/OFF可能な形で、停止後に確定文を取りやすい方式を計画化したい。

### Changes
- Quiet Window（最後の変化からN ms無変化で実行）を中心とした設計を定義。
- pending payload を常に最新で上書きし、実行時は最新版のみ使用する仕様を明文化。
- EnableSceneChangeQuietWindow / SceneChangeQuietWindowMs の設定追加、UI露出、バリデーション、DoDまで記載。

### Files Touched
- Doc/SceneChange_AutoTranslate_QuietWindow_Plan.md — Quiet Window方式とpending payload最新化の実装計画を新規作成。

### Behavioral Impact
- 実行挙動の変更なし（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: 既存計画との役割重複で実装時に参照先が分散する可能性。
- Mitigation: 本計画を「Stage B品質改善（ノベル向け）」に限定し、実装時に関連Docを相互参照する前提を明記した。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。
**2026-02-15 02:44 (Asia/Taipei) — Quiet Window計画書に自己レビュー反映（排他維持）**

### Summary
- Doc/SceneChange_AutoTranslate_QuietWindow_Plan.md に自己レビュー指摘を反映し、実装時の詰まりやすい仕様不足を補強した。

### Context / Goal
- 実装前レビューで、TTL競合・drain迂回・Stage A未通過ケース・入力値仕様不足などが判明した。
- uto-hide / auto-translate の排他維持を前提に、実装可能性を高める必要があった。

### Changes
- 前提に「排他維持（同時有効化を扱わない）」を明記。
- Quiet経路のTTLを通常経路と別扱いにする方針を追加。
- TryDrainPendingAutoTranslate() 内でQuiet条件を評価する仕様を追加し、迂回実行を防止。
- pending payload更新の単一路化（保留理由非依存）を明記。
- Quiet window ms入力の lank/parse失敗/範囲外 ルールを追加。
- Stage A未通過ケースは非ゴール側として明示し、推奨しきい値範囲を追記。

### Files Touched
- Doc/SceneChange_AutoTranslate_QuietWindow_Plan.md — 仕様不足の補強と排他維持方針を反映。

### Behavioral Impact
- 実行挙動への変更なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: 既存設計との不整合が残ると実装時に再設計が必要になる。
- Mitigation: 先に排他維持・TTL・drain統制を明文化し、実装分岐の判断軸を固定した。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。
**2026-02-15 02:57 (Asia/Taipei) — Quiet Window自動翻訳を実装（UI設定付き）**

### Summary
- Doc/SceneChange_AutoTranslate_QuietWindow_Plan.md に基づき、Quiet Window待機実行と最新pending payload採用を本体へ実装した。

### Context / Goal
- ノベルゲームの文字送り中にStage B通過直後の即時実行が走り、途中テキストで翻訳が止まりやすかった。
- Stage B検知後に無変化待機してから実行し、実行時は可能な限り最新payloadを使う必要があった。

### Changes
- AppSettings に EnableSceneChangeQuietWindow / SceneChangeQuietWindowMs を追加（既定: true / 450）。
- SettingsViewModel へ対応プロパティを追加し、Load/Apply/保存トリガーを実装。
- Scene Change UI（OCR > Scene Change Automation）に Quiet Window の ON/OFF と ms入力欄を追加。
- SceneSemanticSettingsRule に Quiet Window ms（100..3000, fallback 450）の正規化を追加。
- SceneChangeController に Quiet Window pending 状態を追加し、以下を実装:
  - Stage B通過時は Quiet Window有効なら即実行せず pending 化
  - 変化継続時は quietタイマー延長（quiet_extended）
  - TryDrainPendingAutoTranslate() 内で quiet達成判定を実施（drain迂回防止）
  - pending payload を更新しつつ quiet ready 後に実行
- MainWindowRunCoordinator で auto-scene + quiet有効時の payload TTL判定を別扱いにし、quiet待機で stale 扱いになりにくいよう調整。

### Files Touched
- Models/AppSettings.cs — Quiet Window設定2項目を追加。
- ViewModels/SettingsViewModel.cs — Quiet Window設定のUIバインド/保存ロジックを追加。
- MainWindow.xaml — Scene Change Automationに Quiet Window UI を追加。
- Services/Settings/Rules/SceneSemanticSettingsRule.cs — Quiet Window ms の clamp を追加。
- Services/Settings/FeatureSettings/SceneFeatureSettings.cs — Quiet Window設定をFeatureSettingsへ追加。
- Services/Settings/FeatureSettings/FeatureSettingsProvider.cs — SceneFeatureSettings生成に Quiet Window設定を反映。
- Services/Application/SceneChangeController.cs — quiet pending/ready判定、payload更新、drain統制を実装。
- Services/Application/MainWindowRunCoordinator.cs — quiet経路でpayload TTLを別扱いに調整。

### Behavioral Impact
- auto-translate + quiet有効時、Stage B変化検知後は無変化時間経過まで実行を遅延する。
- quiet待機中に新しい変化が来た場合は待機延長し、pending payloadは最新で更新される。
- quiet無効時は従来どおり即時実行。
- auto-hide / auto-translate の排他仕様は維持。

### Risk & Mitigation
- Risk: quiet待機で翻訳開始が遅く感じる場合がある。
- Mitigation: UIで ms を調整可能にし、設定正規化で過小/過大値を防止。

- Risk: quiet待機中の状態遷移で pending payload が消失すると品質低下する。
- Mitigation: pending中の不要クリアを抑制し、drain前に quiet判定を必須化した。

### Tests / Verification
- dotnet build -p:UseAppHost=false 実行: 成功（0 warnings / 0 errors）。
**2026-02-15 03:11 (Asia/Taipei) — ForceTick自動翻訳モードの実装案Docを追加**

### Summary
- Watch Intervalごとの強制自動翻訳を最終手段として追加するための実装案を Doc/ に新規作成した。

### Context / Goal
- Quiet Windowでも取りこぼすケースに備え、判定をバイパスして定期実行するモードを検討する必要があった。
- UI切替可能で、既存Semanticモードを既定として維持する方針を整理したい。

### Changes
- SceneChangeAutoTranslateMode（SemanticGate / ForceTick）の導入案を定義。
- ForceTick時の挙動、UI露出、バリデーション、ログ観測点、リスク緩和を明文化。
- 既存排他仕様（auto-hide / auto-translate）維持を前提として記載。

### Files Touched
- Doc/SceneChange_ForceTickAutoTranslate_Plan.md — ForceTick最終手段モードの実装計画を新規作成。

### Behavioral Impact
- 実行挙動への変更なし（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: 強制定期実行の負荷増大・不要翻訳増加。
- Mitigation: デフォルトをSemanticGateに固定し、UI警告とinterval下限制御を計画へ含めた。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。
**2026-02-15 03:22 (Asia/Taipei) — AutoTranslateバッジをアイコン形式へ明確化**

### Summary
- AutoTranslate通知プランで「バッジはアイコンのみ」の方針を明文化した。

### Context / Goal
- ユーザー要望として、バッジ表示をテキストではなくアイコン形式へ統一したい。
- 中央Busy復帰案と整合する実装案に更新したい。

### Changes
- 対象Docのタイトルを「バッジアイコン形式」へ更新。
- バッジ表示ルール（文字常時表示なし、色/形状差分、ToolTip補助）を新設。
- 既存章番号を調整し、既存方針との整合セクションを維持。

### Files Touched
- `Doc/AutoTranslate_CenterBusy_BadgeIcon_Plan.md` — バッジをアイコン形式で運用する実装方針を追記・整理。

### Behavioral Impact
- 実行挙動への影響なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: アイコンのみだと意味が伝わりにくい可能性。
- Mitigation: ToolTipで補助説明を表示し、詳細はログに委譲する方針を明記。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。
**2026-02-15 03:27 (Asia/Taipei) — AutoTranslateバッジ方針をON/OFF表示専用へ更新**

### Summary
- AutoTranslateバッジ仕様を「自動翻訳のON/OFFで表示/非表示のみ」に簡素化した。

### Context / Goal
- ユーザー要望として、バッジは色差分や実行状態差分を持たせず、単純な表示/非表示のみにしたい。
- 中央Busy復帰方針と矛盾しない形で実装案を明確化したい。

### Changes
- バッジ状態を `AutoOff/AutoOn` の2状態へ整理。
- 色差分・形状差分による状態表現を非採用に変更。
- API案を `SetAutoTranslateBadgeVisible(bool visible)` 中心に更新。
- 連携イベントを ON/OFF 切替時のみに限定し、run開始/終了でのバッジ状態更新を除外。

### Files Touched
- `Doc/AutoTranslate_CenterBusy_BadgeIcon_Plan.md` — バッジ表示仕様をON/OFF表示専用へ修正。

### Behavioral Impact
- 実行挙動への影響なし（ドキュメント更新のみ）。

### Risk & Mitigation
- Risk: バッジ単体では実行中かどうかの情報が減る。
- Mitigation: 実行中の可視化は中央Busyで担保し、詳細はログ参照とする。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。
**2026-02-15 03:38 (Asia/Taipei) — AutoTranslate中央Busy復帰とバッジアイコン実装**

### Summary
- `Doc/AutoTranslate_CenterBusy_BadgeIcon_Plan.md` に沿って、中央Busy制御とAutoTranslateバッジ表示を実装した。

### Context / Goal
- 自動翻訳時の可視性不足を解消しつつ、過剰な通知を避ける必要があった。
- バッジは実行状態差分を持たせず、AutoTranslate ON/OFF の表示/非表示だけに限定したい。

### Changes
- 設定に `ShowCenterBusyForAutoTranslate` / `ShowAutoTranslateBadgeIcon` を追加（既定 `true`）。
- Scene Change Automation に2つのUIトグルを追加。
- AutoSceneChange 実行時のみ中央Busyを設定値で抑制可能にし、翻訳開始/完了イベント側も同条件で中央Busy更新を抑制。
- Overlay に右下バッジUIを追加し、`OverlayPresenter` から表示/非表示APIで制御。
- 設定反映時（初期化/保存後）にバッジ表示を同期し、`auto_badge_visibility` ログを追加。

### Files Touched
- `Models/AppSettings.cs` — AutoTranslate中央Busy/バッジ表示の設定2項目を追加。
- `ViewModels/SettingsViewModel.cs` — 新設定のバインド、Load/Apply、自動保存トリガーを追加。
- `MainWindow.xaml` — Scene Change Automation に2つのチェックボックスを追加。
- `Services/Application/MainWindowRunCoordinator.cs` — AutoSceneChange時の中央Busy表示可否制御と実行中フラグ公開を追加。
- `MainWindow.xaml.cs` — バッジ表示同期処理、翻訳イベント時の中央Busy抑制条件、設定同期呼び出しを追加。
- `UI/OverlayWindow.xaml` — 右下AutoTranslateバッジのアイコンUIを追加。
- `UI/OverlayWindow.xaml.cs` — バッジ表示/非表示メソッドを追加。
- `Services/OverlayPresenter.cs` — バッジ表示状態の保持とUI反映APIを追加。

### Behavioral Impact
- AutoTranslate が有効かつ `ShowAutoTranslateBadgeIcon=true` のとき、オーバーレイ右下に固定アイコンを表示。
- AutoSceneChange 実行時、`ShowCenterBusyForAutoTranslate=false` なら中央Busyを表示しない。
- 手動実行やその他経路のBusy表示は従来どおり。

### Risk & Mitigation
- Risk: バッジが実行中状態を示さないため情報量が少ない。
- Mitigation: 実行中は中央Busyで補完し、詳細はログで追跡できる構成にした。

- Risk: 設定切替とバッジ表示がずれる可能性。
- Mitigation: 初期化時と保存反映時の双方でバッジ同期を実行し、状態ログを残す。

### Tests / Verification
- `dotnet build -p:UseAppHost=false` を実行し、成功（0 warnings / 0 errors）。
**2026-02-15 03:52 (Asia/Taipei) — AutoTranslateのBusyロールバックとバッジ表示確実化**

### Summary
- Busy表示拡張をロールバックし、AutoTranslateバッジのみを確実に同期表示するよう修正した。

### Context / Goal
- ユーザー方針として、中央Busy表示は不要とし、バッジ表示の確実性を優先したい。
- 現状ではF5ホットキー経由の切替時にバッジ更新が漏れる経路があった。

### Changes
- `ShowCenterBusyForAutoTranslate` の設定項目をモデル/VM/UIから削除。
- AutoSceneChange実行時の中央Busy表示は常に抑制し、Busy拡張を実質ロールバック。
- F5ホットキー切替時の設定同期コールバックで、VM再読込に加えて `UpdateAutoTranslateBadgeVisibility(...)` を必ず実行するよう変更。

### Files Touched
- `Models/AppSettings.cs` — `ShowCenterBusyForAutoTranslate` を削除。
- `ViewModels/SettingsViewModel.cs` — Busy設定のバインド/保存/変更通知を削除。
- `MainWindow.xaml` — Busy表示トグルUIを削除。
- `Services/Application/MainWindowRunCoordinator.cs` — AutoSceneChange時は中央Busyを表示しない制御へ整理。
- `MainWindow.xaml.cs` — ホットキー切替経路でバッジ同期を追加。

### Behavioral Impact
- AutoTranslate ON/OFF バッジは、設定UI変更時だけでなくF5ホットキー変更時にも確実に追従する。
- AutoSceneChange経路では中央Busyを表示しない。

### Risk & Mitigation
- Risk: ドキュメント記載（Busy設定あり）との齟齬が一時的に残る。
- Mitigation: 実装優先で修正し、必要に応じて後続でDocを更新する。

### Tests / Verification
- `dotnet build -p:UseAppHost=false` を実行し、成功（0 warnings / 0 errors）。
**2026-02-15 04:04 (Asia/Taipei) — AutoTranslateバッジを対象ウィンドウ右下へ配置**

### Summary
- バッジ座標を仮想スクリーン固定から、対象ウィンドウ（capture bounds）右下アンカーへ変更した。

### Context / Goal
- バッジが見えない主因として、仮想スクリーン右下固定配置により別モニタ側へ出るケースがあった。
- 要望どおり対象ウィンドウ右下へ表示したい。

### Changes
- OverlayバッジをCanvas配置へ変更し、座標指定で表示できるようにした。
- `OverlayWindow.SetAutoTranslateBadgeVisible` を `visible + anchorDipRect` 受け取りに変更。
- `OverlayPresenter` にバッジアンカー（screen rect）保持を追加し、DIP変換してウィンドウへ反映。
- `MainWindow.UpdateAutoTranslateBadgeVisibility` で `CaptureManager.GetCaptureBounds(settings)` を使ってアンカーを渡すように変更。
- オーバーレイ表示/更新イベントでもバッジ位置を再同期するようにし、ウィンドウ移動時の追従性を改善。
- 初期化順を調整し、`_captureManager` 初期化後に初回バッジ同期するよう変更。

### Files Touched
- `UI/OverlayWindow.xaml` — バッジUIをCanvas配下へ移し、座標配置対応。
- `UI/OverlayWindow.xaml.cs` — バッジ表示APIをアンカー座標対応へ変更。
- `Services/OverlayPresenter.cs` — バッジのscreenアンカー状態を保持し、表示時にDIP変換して反映。
- `MainWindow.xaml.cs` — バッジアンカー解決ロジックを追加し、対象ウィンドウ右下へ同期。

### Behavioral Impact
- AutoTranslateバッジは仮想スクリーン端ではなく、対象ウィンドウ右下に表示される。
- 対象ウィンドウ座標の再解決時（オーバーレイ表示/更新）に位置が再同期される。

### Risk & Mitigation
- Risk: Capture bounds が取得できないタイミングで座標が空になる。
- Mitigation: 既存スピナーアンカー解決へフォールバックしてバッジ消失を避ける。

### Tests / Verification
- `dotnet build -p:UseAppHost=false` を実行し、成功（0 warnings / 0 errors）。
**2026-02-15 13:10 (Asia/Taipei) — Graphics Hook方式の実装案Docを新規作成**

### Summary
- DX/OpenGL/Vulkanフックでのキャプチャ＋オーバーレイ表示に関する段階導入計画を `Doc/` に追加した。

### Context / Goal
- ユーザー要望として、OBSに近いフック方式のアイデアを実装案として文書化したい。
- 既存WPF経路を残しつつ、段階的に導入できる設計を整理したい。

### Changes
- 新規Docにゴール/非ゴール、API別バックエンド、IPC契約、実装ステップ、リスク対策を記載。
- DX11 PoC開始→DX12/OpenGL/Vulkan拡張の順序とフォールバック戦略を明記。

### Files Touched
- `Doc/GraphicsHook_CaptureOverlay_DX_OpenGL_Vulkan_Plan.md` — Graphics Hook方式の実装案を新規作成。

### Behavioral Impact
- 実行挙動への影響なし（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: フック方式は実装難度と運用リスクが高く、計画が過大化しやすい。
- Mitigation: 既定OFFの段階導入、DX11 PoC起点、失敗時フォールバック前提で計画化した。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。
**2026-02-15 13:25 (Asia/Taipei) — DX11フック詳細実装案Docを新規作成**

### Summary
- DX11に限定したGraphics Hook詳細実装案（C++/C#統合）を `Doc/` に追加した。

### Context / Goal
- ユーザー要望として、まずDX11の実装方針を具体化し、C++側実装の詳細まで明文化したい。
- 既存の高レベル計画を実装着手可能な粒度へ落とし込みたい。

### Changes
- C++側（HookHost/HookAgent）の責務、主要クラス、Presentフック、IPC、描画・キャプチャ処理を詳細化。
- C#側統合（provider切替、設定追加、フォールバック、ログ）を具体化。
- ステップ分割、リスク、DoDをDX11専用で再整理。

### Files Touched
- `Doc/GraphicsHook_DX11_Detailed_Implementation_Plan.md` — DX11向け詳細実装案を新規作成。

### Behavioral Impact
- 実行挙動への影響なし（ドキュメント追加のみ）。

### Risk & Mitigation
- Risk: 詳細案の前提（ライブラリ/注入方式）が実装時に変更される可能性。
- Mitigation: 段階導入とフォールバック前提を明記し、差替可能な契約設計で記述した。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。
**2026-02-15 13:44 (Asia/Taipei) — DX11 Hook実装のStep1基盤を導入**

### Summary
- `Doc/GraphicsHook_DX11_Detailed_Implementation_Plan.md` に沿って、DX11 Hookの段階導入基盤（設定/UI/C#クライアント/C++骨組み）を実装した。

### Context / Goal
- DX11 Hook実装は高リスクのため、既存挙動を壊さずに段階導入できる土台を先に整える必要があった。
- Feature Flag と IPC境界を先に作り、次段でPresentフック本体を安全に実装できる状態にしたい。

### Changes
- AppSettings/SettingsViewModel/MainWindow.xaml に DX11 Hook用設定を追加（既定OFF）。
- Settings正規化ルール `Dx11HookSettingsRule` を追加し、FPS/pipe/host path の基本整合を実装。
- C# 側に `Dx11HookClientService` と IPCメッセージ契約を追加し、attach/detach要求送信の土台を実装。
- MainWindow の起動時/設定反映時/終了時に DX11 Hook クライアントを適用する経路を追加。
- C++ 側に `Native/HookHost` と `Native/HookAgentDx11` の最小骨組み（pipe server + DLL entry + hook stub）を追加。

### Files Touched
- `Models/AppSettings.cs` — DX11 Hook設定項目を追加。
- `ViewModels/SettingsViewModel.cs` — DX11 Hook設定のバインド/保存/自動保存トリガーを追加。
- `MainWindow.xaml` — DX11 Hook（Experimental）設定UIを追加。
- `MainWindow.xaml.cs` — DX11 Hookクライアントの起動時適用・設定反映・終了時停止を追加。
- `Services/Settings/Rules/Dx11HookSettingsRule.cs` — DX11 Hook設定の正規化ルールを新規追加。
- `Services/Settings/AppSettingsValidator.cs` — 新ルールを検証パイプラインへ登録。
- `Services/Hook/Contracts/Dx11HookMessages.cs` — Hook IPCメッセージ契約を新規追加。
- `Services/Hook/Dx11HookClientService.cs` — HookHost接続/attach/detachのクライアント基盤を新規追加。
- `Native/CMakeLists.txt` — Native全体ビルド定義を新規追加。
- `Native/HookCommon/HookIpcProtocol.h` — 共有ヘッダ（フレームヘッダ/コマンド種別）を新規追加。
- `Native/HookHost/CMakeLists.txt` — HookHostビルド定義を新規追加。
- `Native/HookHost/main.cpp` — NamedPipe制御サーバーの最小実装を新規追加。
- `Native/HookAgentDx11/CMakeLists.txt` — HookAgentビルド定義を新規追加。
- `Native/HookAgentDx11/Dx11PresentHook.h` — DX11 hook API 宣言を新規追加。
- `Native/HookAgentDx11/Dx11PresentHook.cpp` — Present hookスタブ実装を新規追加。
- `Native/HookAgentDx11/dllmain.cpp` — DLLエントリ/エクスポート関数を新規追加。
- `Native/README.md` — Native骨組みのビルド/運用メモを新規追加。

### Behavioral Impact
- `EnableDx11HookPipeline=true` の場合、起動時・設定反映時に HookHost へ attach 要求を送る土台が動作する。
- HookHost未配置/接続失敗時はログを出し、既存キャプチャ経路（WGC/DXGI/GDI）へ留まる。
- 既定値は OFF のため、通常利用での既存挙動は維持される。

### Risk & Mitigation
- Risk: C++骨組みはまだPresentフック本実装前で、機能が限定的。
- Mitigation: 既定OFF・フォールバック前提にし、次段で段階的に機能追加できる境界を先に固定した。

- Risk: 設定変更時に HookHost 接続失敗ログが増える可能性。
- Mitigation: 接続タイムアウトを短くし（500ms）、失敗時は即座に既存経路へ戻す設計にした。

### Tests / Verification
- `dotnet build -p:UseAppHost=false` 実行: 成功（0 warnings / 0 errors）。
- Native は骨組み追加のみで、アプリ本体ビルドへの回帰がないことを確認。
**2026-02-15 14:12 (Asia/Taipei) — DX11 Presentフック + 共有メモリフレーム出力（Step2）**

### Summary
- HookAgentDx11 に実 Present/ResizeBuffers フックと BGRA8 readback を追加し、共有メモリへ最新フレームを書き出す経路を実装した。

### Context / Goal
- WPF Topmost 競合回避のため、ゲームの Present 経路でフレーム取得できる PoC が必要だった。
- 将来 DX12/OpenGL/Vulkan を追加できるよう、API非依存の共有フレーム契約を先に固定したい。

### Changes
- HookCommon に `GraphicsApi`/共有メモリ命名規約と、共有フレーム writer（header+payload 書き込み）を追加。
- HookAgentDx11 に vtable 差し替え方式の Present/ResizeBuffers フックを実装し、staging readback で BGRA8 フレームを共有メモリへ出力。
- HookHost に Attach/Detach の実装を追加し、対象 PID へ HookAgentDx11.dll を注入して Install を呼ぶ（v1: DLL 常駐、detach は無効化のみ）。
- Native の出力先を `Native/HookHost/bin` に揃え、WPF 側の既定パス `Native\\HookHost\\bin\\HookHost.exe` と整合させた。

### Files Touched
- `Native/HookCommon/HookIpcProtocol.h` — 共有フレームヘッダ拡張（pixelFormat/api/pid）と mapping 名生成を追加。
- `Native/HookCommon/SharedFrameWriter.h` — 共有メモリ writer を新規追加。
- `Native/HookCommon/SharedFrameWriter.cpp` — 共有メモリ writer 実装を新規追加。
- `Native/HookAgentDx11/Dx11PresentHook.cpp` — Present/ResizeBuffers フック、staging readback、共有メモリ書き込み、15fps デフォルト間引きを実装。
- `Native/HookAgentDx11/dllmain.cpp` — CreateRemoteThread 互換の thread-proc 形式エクスポートを追加。
- `Native/HookAgentDx11/CMakeLists.txt` — HookCommon 実装をリンクし、出力先を `Native/HookHost/bin` に統一。
- `Native/HookHost/main.cpp` — Attach/Detach で注入と Install/Uninstall 呼び出しを実装（v1: DLL unload しない）。
- `Native/HookHost/CMakeLists.txt` — HookCommon include と出力先を `Native/HookHost/bin` に統一。

### Behavioral Impact
- DX11 対象プロセスに HookAgentDx11 を注入できた場合、Present 経路でフレームが `Local\\HT_HOOK_FRAME_<api>_<pid>` へ BGRA8 で書き出される。
- Detach は v1 では DLL を unload せず capture を無効化する（vtable フック方式の dangling ポインタ回避）。

### Risk & Mitigation
- Risk: vtable 差し替え方式はタイトル/ドライバ差異で失敗し得る。
- Mitigation: Hook 側は失敗時に何もしない設計にし、上位（WPF）で既存キャプチャへフォールバック可能な前提を維持。

- Risk: detach で DLL を unload するとクラッシュする可能性（vtable が DLL 内関数を指すため）。
- Mitigation: v1 では DLL 常駐とし、detach は無効化のみ。

### Tests / Verification
- `dotnet build -p:UseAppHost=false` 実行: 成功（0 warnings / 0 errors）。
- Native ビルド: 未実施（この環境で `cmake` が未導入のため）。
**2026-02-15 14:28 (Asia/Taipei) — Hook共有メモリフレームをC#キャプチャ経路へ統合（Step3準備）**

### Summary
- DX11 Hook が出力する共有メモリフレームを読む `GraphicsHookCaptureProvider` を追加し、Hook有効時は最優先で試すようにした。

### Context / Goal
- Step2 で HookAgentDx11 が共有メモリへフレームを書き出せるようになったため、WPF 側がそのフレームを既存 OCR/翻訳パイプラインへ流せる入口が必要だった。
- 将来 OpenGL/Vulkan を追加しても provider 差し替えで拡張できるよう、CaptureProviderKind と selector 境界を先に整えたい。

### Changes
- `CaptureProviderKind` に `GraphicsHook` を追加。
- `CaptureProviderSelector` を拡張し、`EnableDx11HookPipeline=true` の場合は `GraphicsHook` を最優先にする。
- 共有メモリ `Local\\HT_HOOK_FRAME_<api>_<pid>` を読み取り、BGRA8 -> Bitmap へ復元する `GraphicsHookCaptureProvider` を新規追加。
- Hook capture 成功時に `PreferredCaptureProvider` を永続化しない（UI/保存設定を汚さない）。

### Files Touched
- `Models/CaptureProviderKind.cs` — `GraphicsHook` を追加。
- `Services/Capture/CaptureProviderSelector.cs` — Hook有効時に Hook provider を最優先。
- `Services/GraphicsHookCaptureProvider.cs` — 共有メモリフレーム読み取り provider を追加。
- `Services/CaptureManager.cs` — provider 一覧に Hook provider を追加。
- `Services/PipelineOrchestrator.cs` — Hook provider を Preferred として保存しない。
- `ViewModels/SettingsViewModel.cs` — 互換性のため `GraphicsHook` タグを解釈可能にする。

### Behavioral Impact
- `EnableDx11HookPipeline=true` かつ固定対象ウィンドウが解決できた場合、キャプチャはまず Hook の共有フレームを使用する（失敗時は既存 WGC/DXGI/GDI にフォールバック）。

### Risk & Mitigation
- Risk: 共有メモリが未生成/不安定な場合、Hook provider が失敗してフォールバックが増える。
- Mitigation: provider は "mapping not found" 等を失敗理由として返し、既存 provider へ即時フォールバックできる設計を維持。

### Tests / Verification
- `dotnet build -p:UseAppHost=false`: 成功。
- `cmake --build Native/build --config Release`: 成功（`Native/HookHost/bin/HookHost.exe` と `HookAgentDx11.dll` を生成）。
**2026-02-15 14:45 (Asia/Taipei) — HookHost応答の受信とframeMap連携（Hook capture 安定化）**

### Summary
- HookHost の `hookState` 応答を C# 側で受信し、返却された `frameMap` を Hookキャプチャが利用するようにした。

### Context / Goal
- 共有メモリ名を C# 側でハードコードすると、将来 OpenGL/Vulkan を追加した際に破綻しやすい。
- また attach 成否が見えないと「注入失敗」と「共有メモリ読取失敗」を切り分けづらい。

### Changes
- `Dx11HookClientService` に NamedPipe 受信ループを追加し、`hookState` をログ化。
- `frameMap` を PID ごとに保存する軽量 registry を追加。
- `GraphicsHookCaptureProvider` は registry があれば `frameMap` を優先使用し、無ければ従来命名へフォールバック。
- HookHost の `hookState` payload に `pid` を追加し、複数対象でも解析しやすくした。

### Files Touched
- `Services/Hook/Dx11HookClientService.cs` — Pipe受信ループ追加、hookState解析、frameMap登録/解除。
- `Services/Hook/HookFrameMapRegistry.cs` — PID->frameMap の一時 registry を新規追加。
- `Services/GraphicsHookCaptureProvider.cs` — 動的 frameMap を使用するよう変更。
- `Native/HookHost/main.cpp` — hookState payload に pid を追加。

### Behavioral Impact
- HookHost が返す `frameMap` を使って共有メモリを読むため、将来 API が増えても C# 側の命名固定に依存しにくくなった。
- attach/detach の状態が `stage=dx11_hook event=hook_state ...` としてログに出る。

### Risk & Mitigation
- Risk: 受信ループが例外で停止すると frameMap が更新されない。
- Mitigation: 受信例外はログ化し、capture 側は従来命名へフォールバックできるようにしている。

### Tests / Verification
- `dotnet build -p:UseAppHost=false`: 成功。
- `cmake --build Native/build --config Release`: 成功。
**2026-02-15 15:03 (Asia/Taipei) — Hookキャプチャのフレーム更新待ちとキャッシュ返却**

### Summary
- Hook共有メモリが未更新でもフォールバック連発しないよう、短時間の更新待ちと前回フレームのキャッシュ返却を追加した。

### Context / Goal
- Watch Interval でキャプチャを回すと、Hook側の更新タイミング次第で「同一フレームの再コピー」や「mapping/header未初期化による失敗→フォールバック」が増えやすい。
- DLL常駐方針のまま、Hook capture を安定させたい。

### Changes
- 共有メモリの header が未初期化の場合、短時間だけ初期化を待つ（最大40ms）。
- frameId が変わらない場合、短時間だけ新フレームを待つ（最大25ms）。
- それでも変わらない場合は、前回成功フレームの Bitmap を clone して返す（同一フレーム再コピー回避）。

### Files Touched
- `Services/GraphicsHookCaptureProvider.cs` — header初期化待ち/新フレーム待ち/Bitmapキャッシュを実装。

### Behavioral Impact
- Hook capture が「mapping未生成」以外の一時状態で失敗しにくくなり、既存 provider へのフォールバック頻度が下がる。

### Risk & Mitigation
- Risk: キャッシュ返却により同一フレームが連続で供給される。
- Mitigation: scene change/pHash/OCR diff の既存抑制ロジックがある前提で、不要なフォールバックより優先。

### Tests / Verification
- `dotnet build -p:UseAppHost=false`: 成功。
- `cmake --build Native/build --config Release`: 成功。
**2026-02-15 15:31 (Asia/Taipei) — DX11 HookをMinHook方式へ移行（常駐前提）**

### Summary
- HookAgentDx11 の Present/ResizeBuffers フックを vtable 差し替えから MinHook ベースに切り替えた。

### Context / Goal
- 将来 OpenGL/Vulkan もフック方式で追加するため、DX11 も関数フック基盤（MinHook）に寄せて統一したい。
- DLL常駐方針は維持しつつ、detach 時にフック解除できる土台を固める。

### Changes
- MinHook v1.3.3 を `Native/ThirdParty/MinHook` に同梱し、CMake で static lib としてビルド。
- HookAgentDx11 はダミースワップチェインから取得した `Present/ResizeBuffers` の関数ポインタに対して `MH_CreateHook` / `MH_EnableHook` を適用。
- detach（Uninstall）時に `MH_DisableHook` / `MH_RemoveHook` を実行してフックを解除（DLLは常駐のまま）。

### Files Touched
- `Native/CMakeLists.txt` — ThirdParty MinHook をビルド対象に追加。
- `Native/ThirdParty/MinHook/CMakeLists.txt` — MinHook static library を追加（この環境向けに C++ としてコンパイル）。
- `Native/ThirdParty/MinHook/src/trampoline.c` — C++コンパイル互換のため `__movsb` の引数を明示キャスト。
- `Native/HookAgentDx11/CMakeLists.txt` — `minhook` をリンクし、include path を追加。
- `Native/HookAgentDx11/Dx11PresentHook.cpp` — MinHook による hook install/uninstall に置換。
- `.gitignore` — MinHook import 時の zip/展開元フォルダを ignore。

### Behavioral Impact
- HookAgentDx11 の detach で Present/ResizeBuffers のフックが解除される（常駐のまま動作を止められる）。

### Risk & Mitigation
- Risk: MinHook を C++ としてコンパイルしているため、将来のアップデート時に追加の互換パッチが必要になる可能性。
- Mitigation: 変更は ThirdParty 側の最小 1 箇所に限定し、明示的に COMPAT コメントを残した。

### Tests / Verification
- `cmake -S Native -B Native/build -A x64`: 成功。
- `cmake --build Native/build --config Release`: 成功。
- `dotnet build -p:UseAppHost=false`: 成功。
**2026-02-15 16:02 (Asia/Taipei) — Hook設定の共有メモリ伝搬（fps/overlay）**

### Summary
- HookHost→HookAgentDx11 へ `captureFpsLimit`/`overlayEnabled` を共有メモリで伝搬し、Agent 側の capture 間引きに反映できるようにした。

### Context / Goal
- これまで attach payload の fps/overlay が Agent に反映されず、UI変更が実動作へ効かない状態だった。
- DX11/将来の OpenGL/Vulkan でも共通化できるよう、API/ PID ベースの config mapping を定義する。

### Changes
- `Local\\HT_HOOK_CFG_<api>_<pid>` の共有メモリ契約を追加（HookConfigHeader）。
- HookHost は attach（再attach含む）時に config mapping を作成/更新し、detach で解放。
- HookAgentDx11 は Present 内で config を読み取り、`captureIntervalQpc`（fpsLimit）を動的に更新（未生成時は env var にフォールバック）。

### Files Touched
- `Native/HookCommon/HookIpcProtocol.h` — config header と config mapping 名生成を追加。
- `Native/HookCommon/SharedHookConfig.h` — config writer/reader を新規追加。
- `Native/HookCommon/SharedHookConfig.cpp` — config writer/reader 実装を新規追加。
- `Native/HookHost/main.cpp` — attach/detach で config mapping を作成/更新/解放。
- `Native/HookHost/CMakeLists.txt` — SharedHookConfig をビルドへ追加。
- `Native/HookAgentDx11/Dx11PresentHook.cpp` — config reader を追加し Present 内で設定を反映。
- `Native/HookAgentDx11/CMakeLists.txt` — SharedHookConfig をビルドへ追加。

### Behavioral Impact
- UI から送った fpsLimit が HookAgent 側の capture 間引きに反映される（従来の env var 固定から改善）。

### Risk & Mitigation
- Risk: HookHost が動作していない/設定 mapping が未生成の場合、設定が反映されない。
- Mitigation: Agent 側は env var (`HT_HOOK_CAPTURE_FPS_LIMIT`) にフォールバックする。

### Tests / Verification
- `cmake --build Native/build --config Release`: 成功。
- `dotnet build -p:UseAppHost=false`: 成功。
**2026-02-15 19:03 (Asia/Taipei) — DX11 Hook OverlayUpdate v1 (Rect-only) 実装**

### Summary
- C#→HookHost→共有メモリ→HookAgent(Present内) の経路で矩形オーバーレイをゲーム内合成表示できるようにした。

### Context / Goal
- WPF Topmost 競合（例: Magpie 等）を根本回避するため、DX11 Present フック側で ROI/テキスト枠などの矩形を直接描画したい。

### Changes
- Local\\HT_HOOK_CMD_<api>_<pid> の共有メモリ契約（header + rect command 配列）を追加。
- HookHost は overlayUpdate を受け取り、rect commands を base64 デコードして共有メモリへ最新上書き。
- HookAgentDx11 は Present 内で共有メモリを読み、ID3D11DeviceContext1::ClearView による枠描画（4辺の細長い矩形）を実装。
- パイプライン完了時に ROI + OverlayItem の枠を HookHost に送信（Hook capture 利用時のみ）。

### Files Touched
- Native/HookCommon/HookIpcProtocol.h — overlay command header/rect command と mapping 名生成を追加。
- Native/HookCommon/SharedOverlayCommands.h — overlay command 共有メモリ writer/reader を追加。
- Native/HookCommon/SharedOverlayCommands.cpp — writer/reader 実装を追加。
- Native/HookHost/main.cpp — overlayUpdate を実装（base64 decode→共有メモリへ書き込み）。detach 時に writer を解放。
- Native/HookHost/CMakeLists.txt — crypt32 リンクと SharedOverlayCommands.cpp を追加。
- Native/HookAgentDx11/Dx11PresentHook.cpp — Present 内の overlay command 読み取り + 枠描画を追加。
- Native/HookAgentDx11/CMakeLists.txt — SharedOverlayCommands.cpp をビルドへ追加。
- Services/Hook/Contracts/Dx11HookMessages.cs — overlayUpdate 用メッセージ/rect 定義を更新。
- Services/Hook/Dx11HookClientService.cs — rect commands をバイナリパック→base64 送信する TrySendOverlayUpdate を追加。
- Services/PipelineOrchestrator.cs — Hook capture 時に ROI + OverlayItem 枠を送信。
- MainWindow.xaml.cs — PipelineOrchestrator に Dx11HookClientService を注入。

### Behavioral Impact
- Hook capture が有効なタイトルでは、WPF オーバーレイとは別にゲーム内へ矩形枠が直接描画される（Topmost 競合の影響を受けにくい）。

### Risk & Mitigation
- Risk: ClearView はアルファブレンドではなくピクセル上書きのため、枠が意図せず目立つ可能性。
- Mitigation: v1 はデバッグ用途として枠のみ（細線）に限定し、必要なら色/太さ/描画方式の改善余地を残す。

### Tests / Verification
- dotnet build -p:UseAppHost=false: 成功。
- cmake --build Native/build --config Release: 成功。
**2026-02-15 19:36 (Asia/Taipei) — DX11 Present1 フック対応（exclusive/flip 対策）**

### Summary
- DX11 swapchain が Present1 経由で描画されるケースでも hook capture / overlay が動くようにした。

### Context / Goal
- 独占フルスクリーンや flip model を使うタイトルでは IDXGISwapChain::Present ではなく IDXGISwapChain1::Present1 が呼ばれ、現状の HookAgentDx11 がフレーム共有を生成できない可能性がある。

### Changes
- IDXGISwapChain1::Present1 を追加フック（MinHook）し、Present と同様にフレーム共有と矩形描画を実行。
- ダミー swapchain から swapchain1 vtable を取得し、Present1 の関数アドレスを解決。
- detach 時に Present1 フックも確実に解除。

### Files Touched
- Native/HookAgentDx11/Dx11PresentHook.cpp — Present1 hook / vtable 解決 / uninstall を追加。

### Behavioral Impact
- Present1 を使う DX11 タイトルでも Local\\HT_HOOK_FRAME_<api>_<pid> が生成され、GraphicsHook capture とゲーム内矩形描画が成立しやすくなる。

### Risk & Mitigation
- Risk: タイトルにより Present/Present1 のどちらも使わない（DX12/Vulkan/OpenGL等）場合は効果がない。
- Mitigation: 既存の WGC/GDI フォールバックは維持し、原因切り分けに役立つよう hook capture 側のログを継続使用する。

### Tests / Verification
- cmake --build Native/build --config Release: 成功（※HookAgentDx11.dll をロードしているプロセスがあるとリンク失敗するため、対象ゲームを終了してからビルド）。
- dotnet build -p:UseAppHost=false: 成功（Hotkey-Translator.exe 実行中は dll copy の warning が出るがビルド自体は成功）。
**2026-02-15 19:54 (Asia/Taipei) — GraphicsHook frame共有のRGBA8対応 + overlayUpdate送信条件緩和**

### Summary
- DX11 swapchain が R8G8B8A8 の場合でもフレーム共有を生成できるようにし、WGCフォールバック中でも overlayUpdate を送って描画経路の切り分けができるようにした。

### Context / Goal
- ログ上 Hook shared frame mapping not found が継続しており、Present は呼ばれていても swapchain format 非対応で WriteFrame まで到達していない可能性がある。
- GraphicsHook が失敗して WGC へフォールバックしている間も、HookAgent の描画経路（矩形合成）が生きているかを切り分けたい。

### Changes
- HookAgentDx11: DXGI_FORMAT_R8G8B8A8(_SRGB) を許容し、readback 後に CPU で RGBA→BGRA スワップして共有フレームへ出力。
- PipelineOrchestrator: overlayUpdate を CaptureProviderKind.GraphicsHook 限定にせず送信（WGC 等でも frame bounds/bitmap を基準に座標変換）。

### Files Touched
- Native/HookAgentDx11/Dx11PresentHook.cpp — RGBA8 対応（swizzle）を追加。
- Services/PipelineOrchestrator.cs — overlayUpdate の送信条件を緩和。

### Behavioral Impact
- DX11タイトルで RGBA8 swapchain の場合でも Local\\HT_HOOK_FRAME_<api>_<pid> が生成されやすくなる。
- GraphicsHook が落ちて WGC で OCR している間でも、HookAgent 側の矩形描画が成立していれば枠が見える可能性がある（座標ズレは起こり得る）。

### Risk & Mitigation
- Risk: RGBA→BGRA の CPU swizzle が高解像度でコストになる可能性。
- Mitigation: v1 は fpsLimit で間引きが効く前提。必要なら将来 GPU 変換パスを検討。

### Tests / Verification
- dotnet build -p:UseAppHost=false: 成功。
- cmake --build Native/build --config Release: 成功（※HookAgentDx11.dll をロードしているプロセスがあるとリンク失敗するため、対象プロセス終了後にビルド）。
**2026-02-15 20:05 (Asia/Taipei) — 固定ターゲットLock/Unlock時にDX11 Hook attach/detachを確実に実行**

### Summary
- Lock/Unlockホットキーで固定対象を変更してもHook attachが走らず HT_HOOK_FRAME が生成されない問題を修正した。

### Context / Goal
- ログに stage=dx11_hook event=attach_skip reason=fixed_window_not_bound が出た後、Lockしても ttach_requested が出ず、GraphicsHook は常に mapping not found になっていた。
- 原因は、Lock/Unlockホットキーが settings を SettingsService.SaveAsync() で直接保存しており、UI保存経路（ApplyRuntimeStateAfterSave）で行っている Dx11HookClientService.ApplySettingsAsync が呼ばれていないこと。

### Changes
- Lockホットキー処理後に Dx11HookClientService.ApplySettingsAsync を明示的に呼び、固定対象PIDに対して attach/inject を発火。
- Unlockホットキー処理後に Dx11HookClientService.StopAsync を呼び、固定対象解除時は確実に detach する。

### Files Touched
- MainWindow.xaml.cs — OnLockCaptureWindowHotkeyPressed / OnUnlockCaptureWindowHotkeyPressed に hook apply/stop を追加。

### Behavioral Impact
- Lock直後にHookHostへ attach が送られ、HookAgent が動作していれば Local\\HT_HOOK_FRAME_1_<pid> が生成されやすくなる（GraphicsHook capture が成立しやすい）。

### Risk & Mitigation
- Risk: Lock/Unlock時に pipe接続や注入が行われるため、一時的に処理が重くなる可能性。
- Mitigation: 既存の attach は短い connect timeout を持ち、失敗時はフォールバック経路（WGC等）を維持。

### Tests / Verification
- dotnet build -p:UseAppHost=false: 成功。
**2026-02-15 20:14 (Asia/Taipei) — overlayUpdate base64 decode 失敗の修正（JSONエスケープ対応）**

### Summary
- HookHost 側で ectsB64 が JSON エスケープ（例: \/ や \u002B）されている場合でも正しくデコードできるようにした。

### Context / Goal
- stage=dx11_hook event=hook_state state=Failed reason=overlay_decode_failed が継続し、HookAgent 側へ矩形コマンドが渡らず枠描画が動かない。
- C# から送る base64 文字列が JSON でエスケープされる場合、現状の HookHost 実装（生文字列抽出）では base64 として不正になり得る。

### Changes
- HookHost: ectsB64 を base64 decode 前に JSON 文字列として unescape（\/, \\, \uXXXX など最小対応）。
- unescape 失敗時は overlay_unescape_failed を返す。

### Files Touched
- Native/HookHost/main.cpp — JSON unescape を追加し、base64 decode へ渡す文字列を正規化。

### Behavioral Impact
- overlayUpdate が成功しやすくなり、HookAgent(Present内)の矩形描画が動作する可能性が上がる。

### Risk & Mitigation
- Risk: 簡易パーサのため、非ASCIIや複雑なエスケープを含む文字列は弾く。
- Mitigation: v1 の ectsB64 は base64（ASCII）前提なので仕様上問題になりにくい。

### Tests / Verification
- cmake --build Native/build --config Release: 成功。
- dotnet build -p:UseAppHost=false: 成功（実行中は dll copy の warning が出ることがある）。
**2026-02-15 20:29 (Asia/Taipei) — GraphicsHook bounds をクライアント領域基準に変更（座標整合）**

### Summary
- GraphicsHook の capture bounds を ExtendedFrameBounds ではなくクライアント領域（ClientToScreen）基準にし、ROI/枠表示の座標ズレを減らした。

### Context / Goal
- Hooked backbuffer のピクセル領域は通常ウィンドウのクライアント領域に対応するため、ExtendedFrameBounds（外枠含む）を bounds として使うと overlayUpdate の座標変換がズレやすい。

### Changes
- GraphicsHookCaptureProvider の bounds 解決で GetClientRect + ClientToScreen を優先し、取得できない場合のみ従来の ExtendedFrameBounds/WindowRect にフォールバック。

### Files Touched
- Services/GraphicsHookCaptureProvider.cs — クライアント領域スクリーンRect取得を追加し、bounds の優先順を変更。

### Behavioral Impact
- GraphicsHook capture 時の rame.Bounds がクライアント領域基準になり、ROI/OverlayRect の画面位置が一致しやすくなる。

### Risk & Mitigation
- Risk: 一部タイトル/レンダラで backbuffer とクライアント領域が一致しない場合がある。
- Mitigation: 取得失敗時は従来の bounds へフォールバックし、挙動が極端に悪化しないようにする。

### Tests / Verification
- dotnet build -p:UseAppHost=false: 成功。
**2026-02-15 20:52 (Asia/Taipei) — overlayUpdate v2(暫定): C#から共有メモリへ直接書き込み**

### Summary
- overlayUpdate の送信経路を安定化するため、C# が HT_HOOK_CMD 共有メモリへ直接 Rect コマンドを書き込む経路を追加し、既存の JSON/base64 パイプ送信はフォールバックにした。

### Context / Goal
- overlay_decode_failed のような decode/escape 系の不安定要素と、JSON/base64 のオーバーヘッドを減らしたい。
- 「最新コマンドのみ保持」の要件は共有メモリ上書きが最も素直。

### Changes
- Dx11HookOverlayCommandWriter を追加し、Local\\HT_HOOK_CMD_1_<pid> へ header+payload を直接書き込み。
- Dx11HookClientService.TrySendOverlayUpdate はまず共有メモリ書き込みを試し、失敗時のみ従来の overlayUpdate (pipe JSON + base64) を送信。
- detach 時に overlay command writer を Reset。

### Files Touched
- Services/Hook/Dx11HookOverlayCommandWriter.cs — HT_HOOK_CMD 共有メモリ writer を追加。
- Services/Hook/Dx11HookClientService.cs — 直接書き込み優先 + フォールバック送信に変更。

### Behavioral Impact
- HookHost の base64 decode が失敗しても、共有メモリ書き込みが成功すれば枠描画は継続できる。

### Risk & Mitigation
- Risk: HookHost と C# が同じ mapping を書く競合が起き得る。
- Mitigation: C# 側を優先し、pipe 送信は「共有メモリ書き込み失敗時のみ」にして競合頻度を抑える。

### Tests / Verification
- dotnet build -p:UseAppHost=false: 成功。
**2026-02-15 21:00 (Asia/Taipei) — C#からHook config共有メモリへ直接反映 + F9でhook overlay表示同期**

### Summary
- Hook設定（fps/overlayEnabled）も C# から HT_HOOK_CFG 共有メモリへ直接書き込みできるようにし、F9のオーバーレイ表示切替で hook側の描画も同期するようにした。

### Context / Goal
- HookHost経由のattach以外でも、実行中に overlayEnabled を即反映したい（WPF overlayを隠した時にゲーム内枠だけ残る等を避ける）。
- 将来的に HookHost を attach/detach に限定し、設定/描画は共有メモリ中心に寄せたい。

### Changes
- Dx11HookConfigWriter を追加し、Local\\HT_HOOK_CFG_1_<pid> に config header を直接書き込み。
- Dx11HookClientService.ApplySettingsAsync で attach 要求後に config を共有メモリへも publish。
- Dx11HookClientService.TryPublishRuntimeConfig を追加（永続化なしのランタイム反映）。
- F9（Overlay表示切替）時に settings.Dx11HookOverlayEnabled && _overlayEnabled を hook runtime config に反映し、WPF overlayとゲーム内枠を同期。

### Files Touched
- Services/Hook/Dx11HookConfigWriter.cs — HT_HOOK_CFG 共有メモリ writer を追加。
- Services/Hook/Dx11HookClientService.cs — config publish と runtime反映APIを追加。
- MainWindow.xaml.cs — F9切替時に hook overlayEnabled をランタイム反映。

### Behavioral Impact
- overlayEnabled の変更が pipe/再attach に依存しにくくなり、ゲーム内枠の表示/非表示が安定する。

### Risk & Mitigation
- Risk: HookHost と C# が同じ config mapping を更新する競合。
- Mitigation: C#側は最新値上書きのみで、HookAgent側は updatedQpc を見て追従するため致命的になりにくい（必要なら将来 HookHost を attach専用に整理）。

### Tests / Verification
- dotnet clean → dotnet build -p:UseAppHost=false: 成功。
**2026-02-15 21:14 (Asia/Taipei) — HookStatus共有メモリを追加（Present/cmd/formatの可観測性）**

### Summary
- HookAgent が Present 経路の状態を共有メモリへ公開し、C#側が mapping not found 等の原因を切り分けやすくした。

### Context / Goal
- HT_HOOK_FRAME が見つからない/更新されない場合に「注入失敗」「Present未呼び出し」「フォーマット非対応」「cmdが届いていない」などを即座に判断できる情報が不足していた。

### Changes
- Native:
  - Local\\HT_HOOK_STAT_<api>_<pid> の共有メモリ契約（HookStatusHeader）を追加。
  - HookAgentDx11 は Present/Present1 内で presentCount/lastPresentQpc/lastPresentKind、backbufferサイズ/format（best-effort）、最後に書いた frameId、overlay cmd の更新Qpc/件数を publish。
- C#:
  - Dx11HookStatusReader を追加。
  - GraphicsHookCaptureProvider の mapping not found エラーに status 情報を添付（presentCount、format、cmdCount など）。

### Files Touched
- Native/HookCommon/HookIpcProtocol.h — status header と mapping 名生成を追加。
- Native/HookCommon/SharedHookStatus.h — status writer/reader を追加。
- Native/HookCommon/SharedHookStatus.cpp — status writer/reader 実装を追加。
- Native/HookAgentDx11/CMakeLists.txt — SharedHookStatus.cpp をビルドへ追加。
- Native/HookAgentDx11/Dx11PresentHook.cpp — Present/Present1 内で status を publish。
- Services/Hook/Dx11HookStatusReader.cs — status mapping reader を追加。
- Services/GraphicsHookCaptureProvider.cs — capture失敗時に status を付与。

### Behavioral Impact
- GraphicsHook が失敗した際、ログに presentCount や format などの診断情報が出るようになり、原因切り分けが速くなる。

### Risk & Mitigation
- Risk: Present毎に status 更新するため、わずかなオーバーヘッドが増える。
- Mitigation: 4KB の単一 memcpy で、capture/ocr に比べれば十分小さい。必要なら将来間引き可能。

### Tests / Verification
- cmake --build Native/build --config Release: 成功。
- dotnet build -p:UseAppHost=false: 成功。
**2026-02-15 22:45 (Asia/Taipei) — Overlay v2 IPC基盤（HT_HOOK_OVL）を追加**

### Summary
- 翻訳テキスト描画向けの v2 オーバーレイ共有メモリ（TextBlock + UTF-8 blob）契約と reader/writer を追加した。

### Context / Goal
- DX11 Present フック内で Dear ImGui により「半透明パネル + 翻訳テキスト」を描画する前提で、v1（Rect-only）を壊さず段階導入できる v2 IPC を先に固めたい。

### Changes
- Native:
  - v2 struct 定義（header + text block）と `Local\\HT_HOOK_OVL_<api>_<pid>` naming を追加。
  - v2 mapping の shared memory reader/writer（256KB固定、updatedSeq）を追加。
  - HookAgentDx11 は Present 内で v2 mapping を読み取り（Step 1: 診断用途のみ、描画はまだ）、status の reserved フィールドへ v2 payload のサイズ情報を載せる。
- C#:
  - v2 mapping writer を追加。
  - Dx11HookClientService に v2 writer のライフサイクルと best-effort 書き込み API を追加。

### Files Touched
- `Native/HookCommon/HookIpcProtocol.h` — v2 overlay structs/constants と `HT_HOOK_OVL` mapping 命名関数を追加。
- `Native/HookCommon/SharedOverlayV2.h` — v2 shared memory writer/reader を追加。
- `Native/HookCommon/SharedOverlayV2.cpp` — v2 shared memory writer/reader 実装を追加。
- `Native/HookAgentDx11/CMakeLists.txt` — `SharedOverlayV2.cpp` をビルドへ追加。
- `Native/HookAgentDx11/Dx11PresentHook.cpp` — Present 内で v2 mapping を読み取り（診断のみ）+ status reserved へ v2 サイズを格納。
- `Services/Hook/Dx11HookOverlayV2CommandWriter.cs` — `HT_HOOK_OVL` v2 writer を追加。
- `Services/Hook/Dx11HookClientService.cs` — v2 writer の Reset/Dispose と `TryWriteOverlayV2` を追加。
- `Doc/GraphicsHook_ImGui_TranslationOverlay_Spec.md` — v2 IPC/座標契約/実装ドラフトを追加。
- `Doc/GraphicsHook_ImGui_TranslationOverlay_Plan.md` — Spec 参照追加 + Header例の文字化け修正。

### Behavioral Impact
- 既存の v1 Rect overlay / hook capture の挙動は変更しない（v2 は読み取りのみで描画しない）。
- HookStatus の reserved フィールドに v2 のブロック数/テキストバイト数が入る（診断用途）。

### Risk & Mitigation
- Risk: Native 側はこの環境で C++ ビルド検証ができていないため、ビルド環境差でコンパイルエラーが残る可能性。
- Mitigation: 変更は HookCommon の追加と HookAgent の単純な reader 呼び出しに限定し、既存コードパスの破壊を避けた。次ステップ開始前に Native のビルド環境で確認する。

### Tests / Verification
- `dotnet build .\\Hotkey-Translator.csproj -c Debug`: 成功。
- Native build: 未実施（この環境に CMake/Visual C++ toolchain が無いため）。

**2026-02-15 23:00 (Asia/Taipei) — Step 2: DX11 HookAgentにDear ImGuiを統合し固定パネルを描画**

### Summary
- HookAgentDx11 の Present フック内で Dear ImGui(DX11 backend) を初期化し、半透明の固定パネルをゲーム内へ合成描画できるようにした（テキストは次ステップ）。

### Context / Goal
- v2 IPC（HT_HOOK_OVL）導入後、まずは ImGui の初期化と state 影響を最小化した描画経路を確立し、ゲーム内合成が安定することを確認したい。

### Changes
- Dear ImGui を `Native/ThirdParty/imgui/` に同梱（v1.92.5 の core + DX11 backend）。
- HookAgentDx11:
  - ImGui context + `imgui_impl_dx11` を Present 内で lazy init。
  - 固定位置（画面下部中央）の半透明パネルを `GetBackgroundDrawList()->AddRectFilled` で描画。
  - ResizeBuffers/Alt+Tab 等のリソース再初期化に備え、device/context を解放する前に ImGui を shutdown する。

### Files Touched
- `Native/ThirdParty/imgui/` — Dear ImGui v1.92.5（core + `backends/imgui_impl_dx11.*`）を同梱。
- `Native/HookAgentDx11/CMakeLists.txt` — ImGui ソースを HookAgentDx11 のビルドに追加、include path を追加。
- `Native/HookAgentDx11/Dx11PresentHook.cpp` — Present 内で ImGui を初期化し、半透明パネルのみ描画（Step 2）。

### Behavioral Impact
- `Dx11HookOverlayEnabled` が有効な場合、v1 の矩形枠描画に加えて半透明の固定パネルがゲーム内に表示される。

### Risk & Mitigation
- Risk: ImGui 描画が D3D11 state を汚し、タイトル側のレンダリングを壊す可能性。
- Mitigation: DX11 backend の state 保存/復帰に依存しつつ、ResizeBuffers/Alt+Tab 時は ImGui を shutdown して次の Present で再初期化する。

### Tests / Verification
- `cmake --build .\\Native\\build --config Release`: 成功（HookAgentDx11/HookHost）。
- `dotnet build .\\Hotkey-Translator.csproj -c Debug`: 成功。

**2026-02-15 23:27 (Asia/Taipei) — Step 3: v2(TextBlock)を読み半透明パネル+テキストをImGuiで描画**

### Summary
- `HT_HOOK_OVL` v2 mapping の TextBlock(UTF-8) を HookAgentDx11 が読み、ImGui で半透明パネルと折り返しテキストを描画する経路を実装した。

### Context / Goal
- Step 2 で ImGui の初期化と固定パネル描画を確認できたので、次は v2 IPC からテキストを受け取り、OCR座標由来の領域に翻訳文を描画できるようにする。

### Changes
- HookAgentDx11:
  - v2 mapping の TextBlock + UTF-8 blob を用いて、背景矩形（角丸/alpha）とテキストを描画。
  - wrap 指定時は ImGui window 内で `PushTextWrapPos(0)` + `TextUnformatted` を用いて折り返し表示。
  - Present 時点で backbuffer が bind されていないタイトル向けに、RTV を一時的に差し替えて描画後に復帰。
- C#:
  - Pipeline の overlayItems から 1ブロックの v2 コマンドを構成し、`Dx11HookClientService.TryWriteOverlayV2` で `HT_HOOK_OVL` へ書き込む（まずは安定化のため 1ブロックのみ）。
  - UTF-8 truncate 時にマルチバイト境界を割らないよう調整。

### Files Touched
- `Native/HookAgentDx11/Dx11PresentHook.cpp` — v2 overlay を ImGui で描画（背景+テキスト、wrap対応、RTV一時復帰）。
- `Services/PipelineOrchestrator.cs` — v2 overlay writer 呼び出しと ScreenRect→Canvas 変換、UTF-8 truncate を追加。

### Behavioral Impact
- `Dx11HookOverlayEnabled` が有効な場合、ゲーム内に「半透明パネル + 翻訳テキスト」が表示される（v2 mapping が空なら従来の固定パネルにフォールバック）。

### Risk & Mitigation
- Risk: タイトルによっては ImGui 描画が state を汚す/RTV復帰が不十分で破綻する可能性。
- Mitigation: ImGui backend の state restore に加え、RenderTarget を明示的に保存/復帰する。問題が出る場合は v2 描画を feature flag で切れる（既存 v1 は維持）。

### Tests / Verification
- `dotnet build .\\Hotkey-Translator.csproj -c Debug`: 成功。
- `cmake --build .\\Native\\build --config Release`: 成功。


**2026-02-15 23:46 (Asia/Taipei) — ImGuiフォント読み込み強化とデバッグログ追加（''?''対策）**

### Summary
- ImGuiのCJK/多言語テキストが''?''になる問題の切り分けと改善のため、フォント読み込みを強化し、ロード成否をOutputDebugStringで出力するようにした。

### Context / Goal
- ゲーム内オーバーレイの背景は描画できるが、翻訳テキストが全て''?''になる。
- 原因候補（フォント未ロード/グリフ範囲不足/テキストUTF-8破損）を切り分けられるようにし、範囲不足も緩和したい。

### Changes
- ImGuiフォントのグリフ範囲を日本語だけでなく中国語(簡体共通)/韓国語/キリル/ベトナム語も含むユニオンに拡張。
- TTC(フォントコレクション)の場合に複数faceを試すようにし、読み込み成功率を上げた。
- フォント読み込み成功/失敗を `OutputDebugStringA` へ出力し、実行時に確認可能にした。

### Files Touched
- `Native/HookAgentDx11/Dx11PresentHook.cpp` — ImGuiフォントのglyph ranges拡張、TTC face試行、デバッグログ追加。

### Behavioral Impact
- 対応グリフ範囲が広がり、環境依存で''?''になっていた文字が表示される可能性が上がる。
- 実行時にデバッグ出力が追加される（動作影響は軽微）。

### Risk & Mitigation
- Risk: グリフ範囲拡大でフォントアトラスが大きくなり、初回生成が重くなる可能性。
- Mitigation: 初期化時一度のみ構築。問題が出る場合は範囲の見直し、または使用文字から動的に範囲を生成する方針へ移行する。

### Tests / Verification
- `cmake --build .\\Native\\build --config Release`: 成功（HookAgentDx11.dll / HookHost.exe）。

**2026-02-16 00:07 (Asia/Taipei) — DX11 hook: ImGui固定テキスト描画テストモード追加**

### Summary
- DX11フック側だけで固定座標に固定テキストを描画するテストモードを追加し、IPC/座標変換とは独立にレンダリング経路を検証できるようにした。

### Context / Goal
- 翻訳テキストがズレて重なってちらつく現象があり、原因切り分けのためにまずネイティブ描画単体で安定性を検証したい。

### Changes
- 環境変数 `HT_HOOK_IMGUI_TEST=1` のとき、v2 mapping を使わずに固定テキストを (40,40) に描画する経路を追加。
- Present/Present1 のネスト呼び出しで二重描画になり得るケースを thread-local 深さカウンタでガード。

### Files Touched
- `Native/HookAgentDx11/Dx11PresentHook.cpp` — ImGuiテスト描画モードと Present 再入ガードを追加。

### Behavioral Impact
- `HT_HOOK_IMGUI_TEST=1` の場合、ゲーム内にテスト文字列が表示される（IPC由来の翻訳描画は抑制される）。

### Risk & Mitigation
- Risk: テストモード有効のまま運用すると実際の翻訳描画が出ない。
- Mitigation: 環境変数でのみ有効化し、デフォルトは無効。

### Tests / Verification
- `cmake --build .\\Native\\build --config Release`: 成功。

**2026-02-16 00:34 (Asia/Taipei) — v2 overlay座標の整数スナップ（ジッタ/二重見え対策）**

### Summary
- Screen→Canvas 変換後の v2 overlay 座標を整数ピクセルにスナップし、サブピクセル由来の視覚的な二重表示/ちらつきを減らす。

### Context / Goal
- ネイティブ固定テキスト描画は安定しているが、通常モード（IPC由来座標）では同じテキストがズレて重なってちらつく。
- まず座標の微小ジッタ/サブピクセル座標を潰して挙動が改善するか確認したい。

### Changes
- ScreenRect→CanvasRect の算出結果を x/y/w/h ともに整数へ丸め、範囲内にクランプしてから Hook へ渡す。

### Files Touched
- `Services/PipelineOrchestrator.cs` — `TryBuildHookCanvasRect` で整数スナップを適用。

### Behavioral Impact
- Hook overlay の描画矩形が整数ピクセルに揃う（サブピクセル位置の描画は行われない）。

### Risk & Mitigation
- Risk: 端で 1px 程度の位置/サイズ誤差が出る可能性。
- Mitigation: まずは安定性優先。必要なら左上はFloor/右下はCeilのようなポリシーに調整する。

### Tests / Verification
- `dotnet build .\\Hotkey-Translator.csproj -c Debug`: 成功。
- `cmake --build .\\Native\\build --config Release`: 成功。

**2026-02-16 00:38 (Asia/Taipei) — 通常モードの二重/ちらつき調査: 座標スナップをロールバックしPresent経路を単一化**

### Summary
- 整数スナップ変更を取り消し、Present/Present1 の両方で描画される可能性を減らすため、DX11フック側で「最初に観測したpresent経路のみ」描画/キャプチャするようにした。

### Context / Goal
- ネイティブ固定テキストは安定するが、IPC由来の通常モードでは同テキストがズレて重なってちらつく。
- 同一フレーム中に Present と Present1 の両方で別スナップショットを描画している可能性を潰して確認したい。

### Changes
- C#:
  - v2 overlay の座標を整数スナップする変更をロールバック（挙動変化を最小化して次の要因を調査）。
- Native(DX11 hook):
  - `activePresentKind` を導入し、デフォルトは最初に観測した present 経路(1=Present / 2=Present1)に固定。
  - 環境変数 `HT_HOOK_PRESENT_KIND` で強制指定可能（0=auto, 1=Present, 2=Present1）。

### Files Touched
- `Services/PipelineOrchestrator.cs` — 座標整数スナップをロールバック。
- `Native/HookAgentDx11/Dx11PresentHook.cpp` — present経路の単一化（activePresentKind）と環境変数オーバーライドを追加。

### Behavioral Impact
- 一部タイトルで Present と Present1 の両方が呼ばれる場合でも、overlay/capture は片方に限定され二重描画の可能性が減る。

### Risk & Mitigation
- Risk: タイトルによっては選ばれた経路側では想定の描画/キャプチャが行われない可能性。
- Mitigation: `HT_HOOK_PRESENT_KIND` で 1/2 を手動で切り替えて検証できる。

### Tests / Verification
- `dotnet build .\\Hotkey-Translator.csproj -c Debug`: 成功。
- `cmake --build .\\Native\\build --config Release`: 成成功。

**2026-02-16 00:52 (Asia/Taipei) — Flip model対応: current backbufferへ描画/キャプチャ**

### Summary
- Flip model swapchain で backbuffer がローテーションするケースに対応し、常に現在の backbuffer index を使って capture と overlay(ImGui/v1) を行うようにした。

### Context / Goal
- 固定テキスト描画は安定だが、通常モードでは座標がズレて重なりちらつく。
- `GetBuffer(0)` を固定で使うと flip model で別バッファに描いてしまい、古い/新しい overlay が交互に出てゴースト化する可能性がある。

### Changes
- `IDXGISwapChain3` が利用可能な場合 `GetCurrentBackBufferIndex()` を使い、RTV を buffer index ごとにキャッシュ。
- overlay(v1 ClearView / ImGui) と capture の両方で current backbuffer index を使用。
- swapchain が変わった場合は RTV キャッシュと swapchain3 をリセットして作り直す。

### Files Touched
- `Native/HookAgentDx11/Dx11PresentHook.cpp` — current backbuffer index 対応（RTVを複数保持、capture/overlayで使用）。

### Behavioral Impact
- Flip model のタイトルで overlay/capture のフレーム整合性が上がり、ゴースト/ちらつきが改善する可能性がある。

### Risk & Mitigation
- Risk: タイトルによって swapchain3 が取得できない/BufferCount が想定外で処理が外れる可能性。
- Mitigation: swapchain3 が無い場合は従来通り index=0 にフォールバックし、RTV範囲外は 0 番にフォールバックする。

### Tests / Verification
- `cmake --build .\\Native\\build --config Release`: 成功。

**2026-02-16 01:16 (Asia/Taipei) — IPC座標ジッタ調査: v2座標のオンスクリーンデバッグ表示**

### Summary
- flip model 対応（current backbuffer index）の試行をロールバックし、IPC(v2)更新で座標が交互に揺れているかを確認するためのオンスクリーンデバッグ表示を追加した。

### Context / Goal
- テストモード（固定テキスト）は安定だが、通常モード（IPC由来）ではテキストがズレて重なってちらつく。
- まず IPC 更新が「2種類の座標を交互に出している」等のジッタを起こしていないかを、ゲーム内で数値として確認したい。

### Changes
- `HT_HOOK_OVL_DEBUG=1` のとき、直近の v2 overlay 更新サンプル（updatedSeq と x/y/w/h）を画面左上に表示。
- 新しい v2 更新を受け取るたびに、直近N件（固定長リングバッファ）へ (seq, canvasW/H, blockCount, textBytes, block0のx/y/w/h) を記録。
- 直近の flip model(current backbuffer index) 対応コードは取り下げ、比較検証しやすい状態に戻した。

### Files Touched
- `Native/HookAgentDx11/Dx11PresentHook.cpp` — v2座標サンプルの記録とオンスクリーン表示を追加、flip model 試行をロールバック。

### Behavioral Impact
- `HT_HOOK_OVL_DEBUG=1` の場合のみ、ゲーム内にデバッグ情報（数値テキスト）が追加表示される。

### Risk & Mitigation
- Risk: デバッグ表示が有効だと描画コストが僅かに増える。
- Mitigation: 環境変数で明示的に有効化した場合のみ表示。

### Tests / Verification
- `cmake --build .\\Native\\build --config Release`: 成功。

**2026-02-16 01:30 (Asia/Taipei) — 調査用デバッグ強化: Present/swapchain情報のオンスクリーン表示**

### Summary
- IPC(v2)座標は安定している前提で、ズレ/二重表示の原因が「複数swapchainの交互Present」などにないかを確認するため、Presentごとのswapchain情報をオンスクリーン表示できるようにした。

### Context / Goal
- `HT_HOOK_OVL_DEBUG` の座標表示で x/y/w/h は安定している。
- それでも二重に見える場合、swapchainが交互にPresentされて別のバックバッファへ描画している等の可能性を切り分けたい。

### Changes
- Present/Present1 のたびに (present kind, swapchainポインタ, swapchain descのBufferCount/SwapEffect/Flags, backbuffer WxH, overlay seq) をリングバッファへ記録。
- `HT_HOOK_OVL_DEBUG=1` 時に、直近のpresentサンプルを画面左上へ表示。

### Files Touched
- `Native/HookAgentDx11/Dx11PresentHook.cpp` — Present/swapchainのデバッグサンプル記録と表示を追加。

### Behavioral Impact
- `HT_HOOK_OVL_DEBUG=1` のときのみデバッグ情報の表示が増える。

### Risk & Mitigation
- Risk: デバッグ表示が有効だとログ用の軽微な計算が増える。
- Mitigation: 固定長バッファへの記録のみで、通常運用では環境変数を無効にする。

### Tests / Verification
- `cmake --build .\\Native\\build --config Release`: 成功。

**2026-02-16 01:41 (Asia/Taipei) — 調査デバッグ追加: Present時のバインドRTV/DSVとImGui描画先を表示**

### Summary
- swapchain/座標が安定しているのに二重/ちらつくケース向けに、Present時にバインドされているRTV/DSVと、ImGuiが実際に描画するRTVをオンスクリーン表示できるようにした。

### Context / Goal
- Present samples と v2座標が安定している。
- それでもゴースト化する場合、flip model等で「このフレームのRTV」と「こちらが描画しているRTV」が一致していない可能性を切り分けたい。

### Changes
- `HT_HOOK_OVL_DEBUG=1` のデバッグ表示に以下を追加:
  - `OMGetRenderTargets` で取得した oldRTV/oldDSV のポインタ値
  - ImGuiが描画した targetRTV のポインタ値（oldRTVを使用したかどうか）
- ImGui描画のターゲットRTVを、oldRTVが存在する場合は oldRTV 優先に変更。

### Files Touched
- `Native/HookAgentDx11/Dx11PresentHook.cpp` — バインドRTV/DSVの表示とImGuiターゲット選択の改善。

### Behavioral Impact
- 通常は挙動変化は小さいが、flip model等で backbuffer が回転しているタイトルでは overlay の整合性が改善する可能性がある。

### Risk & Mitigation
- Risk: oldRTV を使うことで一部タイトルの想定と異なる深度/ステートになる可能性。
- Mitigation: oldRTV/oldDSV を保存し、描画後に必ず復元する。問題が出るタイトルは env フラグで分岐可能。

### Tests / Verification
- `cmake --build .\\Native\\build --config Release`: 成功。

**2026-02-16 01:49 (Asia/Taipei) — 二重表示切り分け: 背景不透明化/テキスト無効化/強制ASCIIのデバッグフラグ追加**

### Summary
- 座標/Present/RTVが安定しているのに二重に見える原因が「元のゲーム字幕が透けて見えている」か「こちらが二重描画している」かを切り分けるため、Hook側にテスト用フラグを追加した。

### Context / Goal
- v2座標とswapchainが安定しており、ImGuiの描画先RTVもPresent時のRTVと一致している。
- それでも二重/ちらつきが残る場合、背景越しに元字幕が見えている可能性を確認したい。

### Changes
- 環境変数（ターゲットプロセス側）で以下を制御可能にした:
  - `HT_HOOK_OVL_FORCE_OPAQUE_BG=1` 背景ARGBのalphaを強制255にして下の字幕を隠せるか確認
  - `HT_HOOK_OVL_SKIP_TEXT=1` v2テキスト描画だけを無効化（背景だけ出す）
  - `HT_HOOK_OVL_FORCE_ASCII_TEXT=1` v2テキストを固定ASCII文字列へ置換（ゲーム字幕と見分ける）
- `HT_HOOK_OVL_DEBUG=1` 表示に上記フラグ状態も表示。

### Files Touched
- `Native/HookAgentDx11/Dx11PresentHook.cpp` — 切り分け用フラグとデバッグ表示を追加。

### Behavioral Impact
- 追加フラグは環境変数で明示的に有効化した場合のみ影響する。

### Risk & Mitigation
- Risk: フラグ有効のまま運用すると通常描画が変わる。
- Mitigation: デフォルト無効。デバッグ目的のみで使用。

### Tests / Verification
- `cmake --build .\\Native\\build --config Release`: 成功。

**2026-02-16 02:00 (Asia/Taipei) — 二重/ちらつき調査強化: テキスト描画パス切替とTID表示**

### Summary
- IPC/RTV/座標が安定しているのにオーバーレイ字幕だけ二重/ちらつく件を切り分けるため、テキスト描画方式を切り替えられるフラグと、PresentサンプルにスレッドIDを追加した。

### Context / Goal
- `HT_HOOK_OVL_DEBUG` で座標やswapchain、RTVが安定していることが確認できた。
- それでも翻訳テキストだけが二重に見える場合、ImGuiの「ウィンドウ内TextUnformatted + SetWindowFontScale + wrap」経路に原因がある可能性を確認したい。

### Changes
- 新しい環境変数（ターゲットプロセス側）:
  - `HT_HOOK_OVL_TEXT_DRAWLIST=1`: per-window経路を使わず、`ImDrawList::AddText` で直接描画（wrapは幅指定）
  - `HT_HOOK_OVL_IGNORE_FONT_PX=1`: `fontPx` を無視してフォントスケールを固定（スケール起因の見え方を切り分け）
- Presentサンプル表示に `tid`（スレッドID）を追加し、複数スレッドでのPresent呼び出しがないか確認可能にした。

### Files Touched
- `Native/HookAgentDx11/Dx11PresentHook.cpp` — テキスト描画パス切替フラグとPresentサンプルのTID追加。

### Behavioral Impact
- 新しいフラグを有効化した場合のみ、翻訳テキストの描画方法が変わる。

### Risk & Mitigation
- Risk: DrawList直描きはウィンドウ経路よりレイアウト機能が少なく、見た目が変わる。
- Mitigation: 調査用フラグとして導入し、原因確定後に恒久対応（複数フォントサイズ/安定レイアウト）へ移行する。

### Tests / Verification
- `cmake --build .\\Native\\build --config Release`: 成功。

**2026-02-16 02:09 (Asia/Taipei) — 修正: v2テキスト描画をDrawList経路をデフォルトに変更（ちらつき/二重表示対策）**

### Summary
- `HT_HOOK_OVL_TEXT_DRAWLIST=1` で二重/ちらつきが解消することが確認できたため、v2テキスト描画のデフォルトを DrawList(AddText) 経路へ切り替えた。

### Context / Goal
- IPC座標/RTV/swapchainが安定しているにも関わらず、翻訳テキストだけが二重に見えてちらつく。
- per-window + `SetWindowFontScale` + wrap + `TextUnformatted` 経路に起因する可能性が高く、DrawList直描きでは改善した。

### Changes
- `HT_HOOK_OVL_TEXT_DRAWLIST` のデフォルト値を `1`（有効）に変更。
- DrawList経路の `AddText` に clip rect を渡し、ブロック内でのクリップを安定化。

### Files Touched
- `Native/HookAgentDx11/Dx11PresentHook.cpp` — v2テキストのデフォルト描画経路をDrawListに変更。

### Behavioral Impact
- 環境変数未設定でも、v2テキストは DrawList 経路で描画される（従来の per-window 経路は `HT_HOOK_OVL_TEXT_DRAWLIST=0` で強制可能）。

### Risk & Mitigation
- Risk: per-window経路と比べてレイアウト挙動（折返し/クリップ）が微妙に変わる可能性。
- Mitigation: クリップ矩形を明示し、問題があるタイトルは env で旧経路に戻せる。

### Tests / Verification
- `cmake --build .\\Native\\build --config Release`: 成功。

**2026-02-16 02:33 (Asia/Taipei) — 12段階フォントロード+PushFontでper-window描画（FontScale廃止）**

### Summary
- per-window ルートの利点（矩形内/折返し/NoInputs）を維持しつつ、`SetWindowFontScale` を廃止して 12 段階のフォントを事前ロードし、ブロックごとに `PushFont/PopFont` で切替える方式に変更した。

### Context / Goal
- `HT_HOOK_OVL_TEXT_DRAWLIST=1` で二重/ちらつきが解消したことから、per-window + FontScale 経路が不安定要因と判断。
- ただし per-window のレイアウト利点は維持したい。

### Changes
- ImGui初期化時に同一フォントファイルから 12 段階（12,14,...,34px）のフォントをロードし、`fontPx` に最も近いフォントを選択して描画。
- per-window 描画で `SetWindowFontScale` を削除し、代わりに `PushFont/PopFont` を使用。
- `HT_HOOK_OVL_TEXT_DRAWLIST` はフォールバックとして残し、デフォルトは per-window 描画に戻した。
- デバッグUIに block0 の desired/selected フォントサイズと steps を表示。

### Files Touched
- `Native/HookAgentDx11/Dx11PresentHook.cpp` — 12段階フォントロード、フォント選択、per-window描画のFontScale廃止。

### Behavioral Impact
- 通常は per-window で描画され、フォントサイズは近似段階に丸められる（12段階）。
- 問題のあるタイトルは `HT_HOOK_OVL_TEXT_DRAWLIST=1` で DrawList 経路へ切替可能。

### Risk & Mitigation
- Risk: フォントを複数サイズロードするため、初回初期化コストとアトラスサイズが増える。
- Mitigation: 初期化時1回のみ。必要なら段階数やサイズレンジを設定化する。

### Tests / Verification
- `cmake --build .\\Native\\build --config Release`: 成功。

**2026-02-16 02:36 (Asia/Taipei) — 12段階フォントサイズを14〜72へ再マッピング**

### Summary
- 12段階フォントサイズのテーブルを 14〜72（両端含む、線形）へ再マッピングし、より広い `fontPx` 範囲を `PushFont` でカバーできるようにした。

### Context / Goal
- オーバーレイの `fontPx` が 34px を超えるケースに備え、12段階を 14〜72 の範囲に広げたい。

### Changes
- ImGui初期化時に作成する 12段階フォントサイズを `[14..72]` に変更（線形補間）。
- probe フォントは最小サイズ（14px）を使用し、既存の `PushFont/PopFont` 選択ロジックは維持。

### Files Touched
- `Native/HookAgentDx11/Dx11PresentHook.cpp` — フォントサイズ配列を14〜72へ変更。

### Behavioral Impact
- `fontPx` が大きいブロックでも近いサイズのフォントを選べる。

### Risk & Mitigation
- Risk: 72px までのフォントをアトラスに含めるため初期化コスト/VRAMが増える可能性。
- Mitigation: 初期化時1回のみ。必要なら上限や段階数を設定化する。

### Tests / Verification
- `cmake --build .\\Native\\build --config Release`: 成功。

**2026-02-16 03:03 (Asia/Taipei) — WPFフォントフィット流用(v2 fontPx)の実装案を詳細化**

### Summary
- WPF側のフォントフィット結果をHook v2の`fontPx`に反映するための実装案Docを、単位系/DPI/スレッド/実装手順まで具体化した。

### Context / Goal
- Hook(ImGui)側で枠に合わせてフォントサイズを拡大/縮小したい。
- 既にWPF overlayが`FormattedText`で安定化込みのフィット算出を持っているため、それをv2 writerへ流用できる形に整理したい。

### Changes
- DIP/Screen px/Canvas pxの単位系整理と、screen px→DIP、DIP→canvas pxの変換式を追記。
- DPI取得方法（WPF visual経由 / Win32 GetDpiForWindow経由）を明記。
- UIスレッド依存を避けるための専用STAワーカー(`OverlayFontFitterWorker`)案を追加。
- Hook native側のフォント段階（12段階・14..72）に合わせた検証観点を追記。

### Files Touched
- `Doc/GraphicsHook_ImGui_TranslationOverlay_FontSizing_FromWpf_Implementation.md` — 変換式、DPI取得、STAワーカー案、実装ステップ詳細を追記。

### Behavioral Impact
- ドキュメント更新のみ（実行時挙動の変更なし）。

### Risk & Mitigation
- Risk: Docと実装が乖離する。
- Mitigation: 実装着手時に本DocのStep順で差分を逐次反映し、`HT_HOOK_OVL_DEBUG`で観測できる状態を維持する。

### Tests / Verification
- 未実施（ドキュメント更新のみ）。

**2026-02-17 18:07 (Asia/Taipei) — FontSizing Step1-3実装（WPF共有フィット→Hook v2 fontPx）**

### Summary
- WPFのフォントフィットロジックを共通クラスへ切り出し、同じ算出結果をDX11 Hook v2の`fontPx`にも反映する経路を実装した。

### Context / Goal
- Hook v2の`fontPx`が固定値(24px)のため、枠サイズに対して文字サイズが追従しない状態だった。
- `Doc/GraphicsHook_ImGui_TranslationOverlay_FontSizing_FromWpf_Implementation.md` の Step1-3 をコードへ反映し、WPF側とHook側のサイズ方針を揃えたい。

### Changes
- `OverlayFontFitter` を追加し、`FormattedText` ベースのフィット計算（量子化/ヒステリシス含む）を `OverlayWindow` から切り出した。
- `OverlayWindow.UpdateItems` を `OverlayFontFitter` 利用に差し替え、既存の描画挙動を維持したまま共通化した。
- `OverlayWindow.TryResolveHookFontPx` を追加し、screen rect から DIP で同じフィット計算を行い、`fontSizeDip -> canvas px` に変換して返す経路を追加した。
- `OverlayPresenter.TryResolveHookFontPx` を追加し、UIスレッドで安全に算出値を取得できるようにした。
- `PipelineOrchestrator.TryUpdateDx11HookOverlayV2` の固定 `fontPx=24` を廃止し、上記算出値を使用。失敗時のみ 24px フォールバックにした。

### Files Touched
- `Services/Overlay/OverlayFontFitter.cs` — フォントフィット共通ロジック（request/context/cache key 含む）を新規追加。
- `UI/OverlayWindow.xaml.cs` — フィット処理を共通クラスへ置換、Hook用 `TryResolveHookFontPx` を追加。
- `Services/OverlayPresenter.cs` — UIスレッド経由の Hook fontPx 算出APIを追加。
- `Services/PipelineOrchestrator.cs` — Hook v2 `fontPx` を動的算出へ変更（フォールバック付き）。

### Behavioral Impact
- WPFオーバーレイとDX11 Hook v2で、同じフォントフィット方針に基づく文字サイズが使われる。
- Hook側の `fontPx` は矩形・テキスト内容・DPI/Canvasスケールに応じて変動し、算出失敗時のみ従来の24pxで表示される。

### Risk & Mitigation
- Risk: UIスレッドがビジーなタイミングで Hook fontPx 算出が失敗/遅延する可能性。
- Mitigation: `PipelineOrchestrator` 側で 24px フォールバックを保持し、表示不能を避ける。

### Tests / Verification
- `dotnet build Hotkey-Translator.sln -c Release` を実行し、成功（0 warnings / 0 errors）。

**2026-02-17 19:15 (Asia/Taipei) — RuntimeConfig publish-last(seq) 移行実装案Docを追加**

### Summary
- F9再表示不具合の止血策と、config共有メモリを publish-last(seq) へ段階移行する詳細実装案を Doc に追加した。

### Context / Goal
- F9でoverlayを再表示できないケースがあり、現行のruntime config反映経路の可観測性と回復性を強化したい。
- 将来のOpenGL/Vulkan拡張を見据えて、設定IPCもv2 overlay同様のseq契約へ揃えたい。

### Changes
- F9不具合の短期対処（publish結果可視化/失敗時ApplySettings再同期）を先行する二段階方針を定義。
- config header v2案、reader/writer判定ルール、publish-last手順、移行手順、DoDを具体化。

### Files Touched
- Doc/GraphicsHook_RuntimeConfig_PublishLastSeq_Plan.md — RuntimeConfigのpublish-last(seq)移行計画を新規追加。

### Behavioral Impact
- ドキュメント追加のみ（実行時挙動の変更なし）。

### Risk & Mitigation
- Risk: ドキュメントと実装順序が乖離する。
- Mitigation: Step 1-2（止血）→ Step 3-6（seq化）の順で段階的に反映し、各段でログ検証を必須化する。

### Tests / Verification
- 未実施（ドキュメント追加のみ）。

**2026-02-20 21:18 (Asia/Taipei) — Hookキャプチャ元A/B切替(OM RTV/GetBuffer)と診断表示を追加**

### Summary
- キャプチャ元を `GetBuffer(0)` と `OM RTV` で切替できるようにし、実際にどのテクスチャを読んだかを Debug UI で可視化した。

### Context / Goal
- Hook取得は正常ログでも、運用中にオーバーレイ混入が起きる事象がある。
- `GetBuffer(0)` と描画時 `OM oldRTV` の不一致仮説をA/Bで検証したい。

### Changes
- `HT_HOOK_CAPTURE_FROM_OM_RTV` 環境変数を追加。
  - `0`: 既存どおり `GetBuffer(0)` を使用
  - `1`: `OM RTV` を優先し、無ければ `GetBuffer(0)` へフォールバック
  - `2`: `OM RTV` 専用（無い場合は fail-fast）
- `CaptureAndShareFrameLocked` に `OMGetRenderTargets -> RTV resource -> ID3D11Texture2D` 取得ルートを追加し、上記モードでキャプチャ元を選択。
- Debug UI (`HT_HOOK_OVL_DEBUG=1`) にキャプチャ診断を追加:
  - mode / used_om / om_eq_getbuf
  - selected/getbuf/om_rtv/om_tex ポインタ

### Files Touched
- `Native/HookAgentDx11/Dx11PresentHook.cpp` — キャプチャ元切替ロジック、診断フィールド、Debug UI表示を追加。

### Behavioral Impact
- 既定動作（mode=0）は従来と同じ。
- 環境変数でキャプチャ元を切替できるため、混入原因の切り分けが可能になった。

### Risk & Mitigation
- Risk: `OM RTV` 経路がタイトルによって無効で、mode=2 ではキャプチャ失敗が増える可能性。
- Mitigation: mode=1 を推奨（優先 + フォールバック）。mode=2 は診断専用として扱う。

### Tests / Verification
- `cmake --build Native/build --config Release` 実行。
- コンパイルは通過したが、リンク時に `HookAgentDx11.dll` ロックで失敗（`LNK1104`）。
- `HookHost` 実行中の可能性があるため、停止後に再ビルドで確認が必要。

**2026-02-21 13:15 (Asia/Taipei) — 調査実装: v2書込失敗可視化と描画完全停止フラグ追加**

### Summary
- 調査専用として、Hook v2 書き込み失敗理由の可視化と、Hook描画を丸ごと停止して混入有無を切り分けるフラグを追加した。

### Context / Goal
- 「認識はされたのに描画がない」「オーバーレイ混入でOCRが乱れる」を切り分けるため、送信失敗と描画起因を独立に観測したい。
- 既存挙動を壊さず、環境変数で診断を有効化できることを優先した。

### Changes
- `Dx11HookClientService.TryWriteOverlayV2` に `out failureReason` を追加し、`disposed / pid_mismatch / sync_busy / writer_failed` を返すようにした。
- `PipelineOrchestrator` に v2 書込ログを追加（`stage=hook_v2_write`）。
  - 失敗時は常時ログ
  - 成功時は `HT_HOOK_OVL_WRITE_DEBUG=1` のときのみログ
- Native Hook に `HT_HOOK_OVL_DISABLE_ALL_DRAW=1` を追加し、v1/v2描画を完全停止（キャプチャと共有フレーム更新は継続）。

### Files Touched
- `Services/Hook/Dx11HookClientService.cs` — v2書込失敗理由を返すAPIへ拡張。
- `Services/PipelineOrchestrator.cs` — v2書込結果ログ（attempt seq, phase, reason）を追加。
- `Native/HookAgentDx11/Dx11PresentHook.cpp` — 描画完全停止フラグ（診断用）を追加。

### Behavioral Impact
- 通常時（環境変数未設定）では機能はほぼ従来どおり。
- `HT_HOOK_OVL_DISABLE_ALL_DRAW=1` 時のみ Hook overlay 描画を止め、自己混入有無の切り分けが可能になる。

### Risk & Mitigation
- Risk: v2書込ログが多くなる可能性。
- Mitigation: 成功ログは `HT_HOOK_OVL_WRITE_DEBUG=1` 時のみ出力し、常時は失敗ログに限定。

### Tests / Verification
- `dotnet build Hotkey-Translator.sln -c Release` 成功（0 warning / 0 error）。
- `cmake --build Native/build --config Release` 成功。

**2026-02-21 17:48 (Asia/Taipei) — GraphicsHook writer race時のWGCフォールバック抑制を実装**

### Summary
- GraphicsHook の `Frame was unstable (writer race)` を一時エラーとして扱い、クールダウン起因のWGCフォールバックと自己混入を減らす修正を入れた。

### Context / Goal
- ログ上、Hookキャプチャ失敗(`writer race`)の直後にWGCへフォールバックし、Hookオーバーレイ混入がOCR入力へ入っていた。
- 目的は `writer race` を致命エラー扱いしないことと、Hook運用中にPreferred providerがWGCへドリフトしないこと。

### Changes
- `CaptureFailureReason` に `HookFrameUnstable` を追加し、`CaptureAttemptCoordinator` で `Frame was unstable (writer race).` を専用分類した。
- `DefaultCapturePolicy` で `GraphicsHook + writer race` はクールダウン開始しないよう変更（DXGI wait-timeoutと同様の扱い）。
- `GraphicsHookCaptureProvider` の読み取りレース時挙動を改善:
  - 読み取り試行回数を 3 -> 5 に増加
  - header再確認条件を `FrameId/PayloadBytes` に加え `Width/Height/Stride` まで一致確認
  - 最終的に不安定でも、同一 pid/map の最新キャッシュがあれば再利用して成功として返す
- `PipelineOrchestrator.RememberPreferredProvider` を更新し、DX11 Hook pipeline有効中はフォールバック成功で `PreferredCaptureProvider` を更新しないようにした。

### Files Touched
- `Services/Capture/CaptureFailureReason.cs` — `HookFrameUnstable` を追加。
- `Services/Capture/CaptureAttemptCoordinator.cs` — writer race の失敗分類を追加。
- `Services/Capture/DefaultCapturePolicy.cs` — writer race 非クールダウン化。
- `Services/GraphicsHookCaptureProvider.cs` — レース耐性強化とキャッシュ再利用を追加。
- `Services/PipelineOrchestrator.cs` — Hook運用中のPreferred provider更新抑止。

### Behavioral Impact
- `writer race` が発生しても、プロバイダのクールダウン入りによる長いWGCフォールバックが起きにくくなる。
- Hookフレームが一時不安定でも、直近キャッシュで連続性を維持できるためOCR入力が安定しやすくなる。
- Hook pipeline有効中は `PreferredCaptureProvider` がWGCへ書き換わらず、hook-first挙動が維持される。

### Risk & Mitigation
- Risk: キャッシュ再利用により、短時間だけ古いフレームでOCRが走る可能性。
- Mitigation: 再利用は同一 pid/map に限定し、ログ(`hook_cached_reuse`)で観測可能にした。

### Tests / Verification
- `dotnet build Hotkey-Translator.sln -c Release` 成功（0 warning / 0 error）。

**2026-02-24 13:41 (Asia/Taipei) — HOOK V1描画の無効化（互換受信は維持）**

### Summary
- HookAgent 側で V1 オーバーレイ描画を停止し、V2 描画のみ有効化した。

### Context / Goal
- V1 は枠描画のみで、実運用上必要な半透明オーバーレイとテキスト描画は V2 が担っている。
- 段階的廃止の第一段として、互換性を保ちながら V1 の描画コストとノイズを削減する。

### Changes
- DrawOverlayLocked を no-op 化し、V1 コマンドの描画処理を削除。
- V1 コマンド読み取り（RefreshOverlayCommandsLocked）は維持し、status/debug 用のコマンド数更新は継続。
- 旧 V1 の矩形描画専用コード（ClampRect と ClearView 描画ループ）を削除。

### Files Touched
- Native/HookAgentDx11/Dx11PresentHook.cpp — V1 描画ロジックを停止し、V1 は互換受信のみ行うよう変更。

### Behavioral Impact
- Hook V1 の枠表示は出なくなる。
- Hook V2 の半透明背景＋テキスト描画は従来どおり動作する。
- V1 IPC 経路は維持されるため、旧送信が存在してもエラーにはならない。

### Risk & Mitigation
- Risk: V1 枠描画に依存したデバッグ手順が使えなくなる。
- Mitigation: V1 コマンド受信自体は維持しているため、次段で必要なら送信側ログで残存利用を可視化できる。

### Tests / Verification
- cmake --build Native/build --config Release 成功。
- dotnet build Hotkey-Translator.sln -c Release 成功（0 warning / 0 error）。

**2026-02-24 13:53 (Asia/Taipei) — Hook時WPF Overlay自動抑止の実装案をDocへ追加**

### Summary
- Hook稼働時に通常WPFオーバーレイを自動抑止する実装案を Doc 配下へ新規作成した。

### Context / Goal
- Hook V2 と WPF の二重描画を避けるため、Hook実行状態に応じた表示ルーティング案が必要だった。
- 実装前に、ゴール/非ゴール/手順/リスクを明文化して合意しやすくする。

### Changes
- Hook稼働時のみWPF描画を抑止し、フォールバック時に復帰する方針の実装案を作成。
- 判定条件、影響範囲、段階実装ステップ、DoD を含む計画書を追加。

### Files Touched
- Doc/GraphicsHook_WpfOverlay_AutoSwitch_Plan.md — 実装案ドキュメントを新規追加。

### Behavioral Impact
- コード変更は未実施。挙動変更はなし。

### Risk & Mitigation
- Risk: 実装時に抑止条件が広すぎると表示消失が発生しうる。
- Mitigation: 計画書で provider=GraphicsHook 限定と失敗時WPF許可フォールバックを明記。

### Tests / Verification
- 未実施（今回はドキュメント追加のみ）。

**2026-02-24 15:55 (Asia/Taipei) — RuntimeConfig止血 + Hook時WPF Overlay自動抑止を実装**

### Summary
- 指定順序どおり、RuntimeConfig publish の止血（成否可視化・F9失敗時再同期）を先行し、その後 Hook 稼働時の WPF overlay 自動抑止を実装した。

### Context / Goal
- F9 で Hook 側 runtime config 反映に失敗すると、overlay 表示状態が不整合になる課題があった。
- Hook V2 稼働時の二重描画（WPF + Hook）を減らし、表示経路を Hook 優先へ寄せたかった。

### Changes
- TryPublishRuntimeConfig を ool 戻り値 + 失敗理由返却に変更し、disposed / invalid_pid / pid_mismatch / sync_busy / writer_failed を判別可能にした。
- F9 トグル時に runtime config publish の成否を INFO ログへ出力し、失敗時は ApplySettingsAsync で再同期する自己回復経路を追加した。
- HookAgent の RefreshConfigLocked に HT_HOOK_CFG_DEBUG=1 時のみ出る設定受理ログ（qpc/fps/overlay/pid）を追加した。
- PipelineOrchestrator で GraphicsHook provider かつ Hook overlay 有効時に WPF overlay 更新を抑止し、stop/cancel/error 時も WPF 側を再表示せず clear 維持する経路を追加した。
- F11 テキストモード切替時も直近 provider が Hook 優先条件なら WPF 再描画を抑止するようにした。

### Files Touched
- Services/Hook/Dx11HookClientService.cs — runtime config publish を結果付きAPIへ変更。
- MainWindow.xaml.cs — F9 publish 成否ログと失敗時 ApplySettingsAsync フォールバックを追加。
- Native/HookAgentDx11/Dx11PresentHook.cpp — config 更新受理デバッグログ（環境変数制御）を追加。
- Services/PipelineOrchestrator.cs — Hook優先時の WPF overlay 自動抑止と stop/cancel/error 連動処理を追加。

### Behavioral Impact
- F9 実行時に Hook runtime config 反映の成功/失敗がログで確認できる。
- publish 失敗時は自動再同期が走るため、overlay 状態不整合の自己回復が期待できる。
- GraphicsHook 実行フレームでは WPF overlay がクリアされ、Hook overlay を優先表示する。

### Risk & Mitigation
- Risk: F9 連打時に publish 失敗が続くと ApplySettingsAsync の再実行が増える。
- Mitigation: publish 成功時は再同期を実行せず、失敗時のみフォールバックする。
- Risk: Hook優先ルート時に WPF toast/showLast が出ず、デバッグ時の見え方が変わる。
- Mitigation: stage=overlay_route ログと stage=dx11_hook event=runtime_config_publish ログで経路と成否を観測可能にした。

### Tests / Verification
- dotnet build Hotkey-Translator.sln -c Release 成功（0 warning / 0 error）。
- cmake --build Native/build --config Release 成功。

**2026-02-24 17:36 (Asia/Taipei) — Hook V2テキスト描画の白枠低減（DrawList既定化）**

### Summary
- Hook V2 のテキスト描画で白枠アーティファクトが出にくいよう、DrawList 経路を既定化し、ウィンドウ経路にも border 無効化を追加した。

### Context / Goal
- 現状、半透明背景の上にテキスト描画した際に、タイトル依存で白い枠線が見えるケースがあった。
- まずは小差分で白枠ノイズを減らし、必要時は環境変数で旧挙動へ戻せる状態を維持する。

### Changes
- HT_HOOK_OVL_TEXT_DRAWLIST の既定値を   から 1 に変更し、DrawList テキスト描画をデフォルト化。
- 互換のため HT_HOOK_OVL_TEXT_DRAWLIST=0 で legacy のウィンドウ描画経路へ戻せるようにした。
- legacy ウィンドウ経路に ImGuiWindowFlags_NoBackground を追加し、WindowBorderSize=0 + ImGuiCol_Border=transparent を適用して白枠が出にくいようにした。

### Files Touched
- Native/HookAgentDx11/Dx11PresentHook.cpp — DrawList既定化と legacy ウィンドウ経路の border/background 無効化を追加。

### Behavioral Impact
- デフォルトでは DrawList 経路が使われ、テキスト周囲の白枠アーティファクト発生率が低下する。
- 環境変数 HT_HOOK_OVL_TEXT_DRAWLIST=0 を設定すると、従来経路の確認・切り戻しが可能。

### Risk & Mitigation
- Risk: タイトルによっては DrawList 経路で別の描画相性問題が出る可能性。
- Mitigation: 環境変数で即時に legacy 経路へ戻せるようにし、フォールバックを保持した。

### Tests / Verification
- cmake --build Native/build --config Release 成功。

**2026-02-24 18:00 (Asia/Taipei) — Run Once(F8/F10)時にHook overlay表示を再同期**

### Summary
- F9でHook overlayを隠した後でも、F8/F10のRun Once開始時にHook runtime configを再publishして表示を復帰させるようにした。

### Context / Goal
- 通常オーバーレイはF9で一時非表示後、F8/F10実行で再表示される挙動だった。
- Hookパイプラインでも同じ期待挙動にそろえる必要があった。

### Changes
- EnableOverlay() 内で _overlayEnabled=true 後、Hook有効時に TryPublishRuntimeConfig(...) を実行するよう追加。
- publish結果を stage=dx11_hook event=runtime_config_publish source=enable_overlay ログで可視化。
- publish失敗時は ApplySettingsAsync(settings) を fire-and-forget で実行し、再同期フォールバックを追加。

### Files Touched
- MainWindow.xaml.cs — EnableOverlay() に Hook runtime config 再publishと失敗時フォールバックを追加。

### Behavioral Impact
- F9で非表示にしたHook overlayは、次のF8/F10実行時に再表示される。
- runtime config publishの成否がログで追跡できる。

### Risk & Mitigation
- Risk: Run開始時に publish失敗すると再同期処理が追加で走る。
- Mitigation: publish成功時は追加処理を行わず、失敗時のみフォールバックする。

### Tests / Verification
- dotnet build Hotkey-Translator.sln -c Release 成功（0 warning / 0 error）。

**2026-02-24 18:15 (Asia/Taipei) — Hook V2 複数テキストブロック送信の最小実装**

### Summary
- Hook V2 の送信を単一ブロック固定から複数ブロック送信へ変更し、複数枠描画を有効化した。

### Context / Goal
- これまで Hook V2 は overlayItems[0] のみ送信しており、複数 OCR ユニットがあっても 1 枠しか表示されなかった。
- 最小変更で複数枠を描画できる状態にすることが目的。

### Changes
- TryUpdateDx11HookOverlayV2 を更新し、overlayItems を走査して複数 TextBlockV2 を構築するよう変更。
- TextOffset / TextLen を使って 1 つの 	extBlob に連結し、複数ブロックを一括 publish。
- 最小ガードとして maxBlocks=64 と maxTextBytes=64KB を設定し、上限到達時は残りを切り捨て。
- 有効ブロックが 0 件のときは clear 経路へ送るよう変更（clear_no_valid_blocks）。
- publish ログ phase を publish_text_blocks に変更し、実ブロック数と text bytes を記録。

### Files Touched
- Services/PipelineOrchestrator.cs — Hook V2 送信ロジックを単一ブロックから複数ブロックへ変更。

### Behavioral Impact
- Hook V2 で複数オーバーレイ枠・複数テキストを描画できるようになる。
- 送信上限を超える場合は上限内のみ描画される。

### Risk & Mitigation
- Risk: ブロック数増加により表示重なりや可読性低下が起きる可能性。
- Mitigation: 今回は最小実装に留め、上限ガードを入れて異常肥大を防止。必要なら次段で重なり回避を追加する。

### Tests / Verification
- dotnet build Hotkey-Translator.sln -c Release 成功（0 warning / 0 error）。
