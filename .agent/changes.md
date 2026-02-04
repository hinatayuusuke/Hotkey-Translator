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
