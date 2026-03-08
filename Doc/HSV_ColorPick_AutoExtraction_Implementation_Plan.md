# HSV Color Pick Auto Extraction Implementation Plan

## 1. Overview

This plan adds an OCR preprocessing assist feature that lets the user click a subtitle color on screen, then uses that sample to auto-initialize an HSV extraction mask.

The goal is not full automatic segmentation. The goal is to make HSV tuning practical by generating a good initial mask from a user-picked subtitle color.

## 2. Goal / Non-Goal

### Goal
- Let the user pick a subtitle color directly from the captured image or overlay selection UI.
- Convert the sampled color into HSV extraction parameters automatically.
- Apply the HSV mask before grayscale / contrast / binarization so colored subtitles can be isolated more reliably.
- Keep manual adjustment available after auto-pick.

### Non-Goal
- Fully automatic subtitle detection without user interaction.
- Multi-cluster color learning in the first implementation.
- Per-game automatic profile generation.
- Complex segmentation such as outline separation, shadow estimation, or ML-based color classification.

## 3. Assumptions

- Existing OCR preprocessing already supports ordered image filters such as grayscale and contrast.
- Subtitle color is reasonably stable within one game scene or one UI style.
- A single clicked sample is not reliable enough by itself, so the implementation should sample a small local area rather than one pixel.
- The first implementation can target one HSV mask only.

## 4. Current State

### Existing Constraints
- Current preprocessing is mainly luminance-based. It is effective for white subtitles but weaker for colored subtitles or UI-heavy backgrounds.
- Users currently need manual threshold tuning when subtitle colors vary from the background.
- ROI selection and capture-based interactions already exist, so color pick can reuse similar input flow instead of creating a completely new mechanism.

### Problem
- One-pixel RGB sampling is too unstable because of anti-aliasing, outline, compression noise, scaling artifacts, and blended background pixels.
- Manual HSV tuning is too technical for normal use.

## 5. Proposed Architecture

### Components
- `AppSettings`
  - Store HSV extraction enable flag and HSV parameters.
  - Store temporary sampled base color if needed for UI preview.
- OCR preprocess service
  - Add an HSV extraction stage before grayscale / contrast / binarization.
- Main window / settings UI
  - Add `Pick Subtitle Color` action.
  - Expose sampled HSV values and allow manual fine tuning.
- Overlay / picker interaction
  - Reuse existing selection-style interaction to capture a clicked point safely.

### Data Flow
1. User enables HSV extraction support in OCR preprocessing settings.
2. User clicks `Pick Subtitle Color`.
3. The app captures the clicked position on the current image/canvas.
4. The app samples a small local neighborhood around the clicked point, such as `5x5` or `7x7`.
5. The app computes a robust representative color from that neighborhood.
6. The representative RGB color is converted to HSV.
7. The app auto-initializes:
   - `HueCenter`
   - `HueTolerance`
   - `MinSaturation`
   - `MinValue`
8. OCR preview updates immediately so the user can verify the extracted mask.
9. User can fine-tune the HSV parameters manually if needed.

### Why this architecture
- WHY: The click action gives the user an intuitive entry point without requiring HSV knowledge.
- WHY: Auto-initialization is safer than hard full automation because subtitle styles vary and anti-aliasing makes exact color matching fragile.
- WHY: Keeping manual tuning after auto-pick avoids overfitting the first sample.

## 6. Interface Design

### Settings
- `EnableHsvExtraction: bool`
- `HsvHueCenter: double`
- `HsvHueTolerance: double`
- `HsvMinSaturation: double`
- `HsvMinValue: double`
- Optional later:
  - `HsvMaxValue: double`
  - `HsvPreviewSampleRgb: string`

### UI
- Checkbox: `Enable HSV Extraction`
- Button: `Pick Subtitle Color`
- Sliders / numeric inputs:
  - `Hue Center`
  - `Hue Tolerance`
  - `Min Saturation`
  - `Min Value`

### Behavior
- Clicking `Pick Subtitle Color` enters a temporary pick mode.
- The next valid click on the preview / target image samples subtitle color.
- Sampling should use a neighborhood, not one pixel.
- The UI should refresh preview immediately after the parameters are updated.

### Validation
- Clamp hue to valid range, for example `0..360`.
- Clamp saturation and value to valid normalized range, for example `0..1`.
- Handle hue wrap-around correctly when tolerance crosses `0/360`.

## 7. Implementation Steps

### Step 1: Settings and preprocessing stage
- Add HSV extraction settings to `AppSettings`.
- Add an HSV mask preprocessing stage.
- Insert the stage before grayscale / contrast / binarization.
- Output a simple binary-like mask:
  - matched pixels -> white
  - non-matched pixels -> black

### Step 2: UI controls
- Add `Enable HSV Extraction`.
- Add numeric controls for hue center, hue tolerance, min saturation, and min value.
- Connect them to live OCR preview refresh if preview flow already exists.

### Step 3: Color pick mode
- Add `Pick Subtitle Color` action.
- Reuse the existing image/overlay interaction path used for ROI-like selection where possible.
- On click, sample a local neighborhood such as `5x5`.

### Step 4: Auto-initialize HSV parameters
- Convert the sampled RGB representative color to HSV.
- Set:
  - `HueCenter = sampled hue`
  - `HueTolerance = conservative default`, for example `10..20`
  - `MinSaturation = derived but clamped`
  - `MinValue = derived but clamped`
- Show immediate preview result.

### Step 5: Logging and verification
- Add concise logs for:
  - pick started
  - sampled point
  - representative RGB/HSV
  - resulting HSV thresholds
- Keep logs short enough to avoid noise during normal OCR operation.

## 8. Non-Functional Checks

### Performance
- Neighborhood sampling is negligible compared to OCR.
- HSV mask per frame is lightweight enough for preprocessing use.

### Reliability
- Prefer median-like representative color over single-pixel sampling.
- Handle hue wrap-around for red-like subtitles.

### Usability
- Do not require the user to understand HSV before first use.
- Keep manual override available because one sample cannot represent all subtitle variants.

### Compatibility
- Fail fast if pick mode has no valid image source.
- Do not add fallback chains unless explicitly required.

## 9. Risks and Mitigations

- Risk: User clicks an anti-aliased edge or background pixel.
- Mitigation: Sample a local neighborhood and use a robust representative color rather than one pixel.

- Risk: White or near-gray subtitles have unstable hue.
- Mitigation: Depend on `MinSaturation` and `MinValue` guards, and keep manual tuning available.

- Risk: Outline and fill colors differ significantly.
- Mitigation: Phase 1 targets dominant fill color only. Outline-aware extraction is out of scope.

- Risk: Overly narrow hue tolerance misses valid subtitle pixels.
- Mitigation: Use a conservative default tolerance and let the user widen it.

## 10. Impacted Files

Expected implementation touch points:

- `Models/AppSettings.cs`
  - Add HSV extraction settings.
- `Services/OcrPreprocessService.cs`
  - Add HSV mask stage and ordering.
- `MainWindow.xaml`
  - Add HSV extraction controls and color-pick action.
- `MainWindow.xaml.cs`
  - Add picker mode wiring and settings persistence.
- Optional overlay / presenter files
  - Reuse or extend existing click interaction flow if needed.

## 11. Definition of Done

- User can enable HSV extraction from the UI.
- User can click `Pick Subtitle Color` and sample a subtitle color from the current image/target.
- The app derives HSV thresholds automatically from the sample neighborhood.
- OCR preview reflects the HSV extraction result.
- The user can fine-tune the auto-generated thresholds manually.
- Hue wrap-around is handled correctly.
- Logs are sufficient for troubleshooting without excessive spam.

## 12. Recommended MVP Decision

The recommended first implementation is:

- one HSV mask only
- one picked sample at a time
- local neighborhood sampling (`5x5` or `7x7`)
- auto-initialize `HueCenter`, `HueTolerance`, `MinSaturation`, `MinValue`
- manual fine tuning remains mandatory support

This keeps the feature practical while avoiding a brittle "fully automatic" design.
