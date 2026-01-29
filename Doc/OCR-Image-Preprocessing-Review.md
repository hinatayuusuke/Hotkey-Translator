# OCR-Image-Preprocessing Review (EN)

## Findings (ordered by severity)

### High
- The document recommends choosing the “highest confidence score,” but the current WinRT OCR path does not expose per-line confidence. This makes the selection criterion non-actionable without additional instrumentation or alternative scoring. `Doc/OCR-Image-PreprocessingEN.md:78`

### Medium
- The 2x–3x scaling recommendation lacks a max-dimension cap. On large captures this can explode CPU and memory cost. Introduce a max width/height and skip scaling when already above the threshold. `Doc/OCR-Image-PreprocessingEN.md:17`
- The “run multiple patterns in parallel” suggestion risks CPU spikes and latency in a live capture loop. Consider a single fallback attempt only when OCR returns zero lines or low confidence. `Doc/OCR-Image-PreprocessingEN.md:78`
- The inversion rule (“black text on white background”) does not specify a measurable trigger. Without a luminance/histogram heuristic, behavior will vary widely across scenes. `Doc/OCR-Image-PreprocessingEN.md:31`

### Low
- Adaptive threshold vs. Otsu is presented without a decision rule. Add guidance on when to prefer each to avoid inconsistent outputs. `Doc/OCR-Image-PreprocessingEN.md:41`
- Morphology parameters (kernel size/iterations) are not specified; over-dilation can merge glyphs. Provide safe defaults and bounds. `Doc/OCR-Image-PreprocessingEN.md:55`

## Performance Notes
- Crop ROI before scaling and binarization to reduce pixel work.
- Cap the max dimension (e.g., 1600–2000 px) and reuse buffers where possible.
- Avoid parallel multi-pass by default; use a single fallback on failure.

## Open Questions
- What preprocessing latency budget is acceptable per capture?
- Should preprocessing be skipped when pHash indicates no visual changes?

## Summary
- The plan is directionally solid but needs concrete thresholds, caps, and measurable rules to be implementable and performant.