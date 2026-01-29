### 1. Basic Processing Pipeline Configuration (Layered Structure)

Process images through a small, predictable pipeline instead of trying to solve everything in one step:

1. **Normalization:** Standardize size and color format.
2. **Grayscale + Inversion:** Reduce noise and normalize contrast.
3. **Adaptive Thresholding:** Robust binarization for mixed/gradient backgrounds.
4. **Morphology (optional):** Light dilation for thin fonts.

---

### 2. Implementation Notes

#### Step 1: Scaling (Resolution)
WinRT OCR accuracy degrades with small glyphs. Upscale only when necessary:

- If the ROI is smaller than a target size, scale up (e.g., 2x).
- Cap maximum dimension (e.g., 1600–2000 px) to avoid excessive CPU/memory.
- Prefer **Bicubic** or **Lanczos** over Linear interpolation to reduce aliasing.

#### Step 2: Grayscale + Conditional Inversion
Color often adds noise. Convert to grayscale, then normalize contrast direction:

- Compute average luminance (or histogram median).
- If the background is darker than the text, invert to **black text on white background**.
- Keep the heuristic simple and stable; avoid flipping on minor changes.

#### Step 3: Adaptive Thresholding (Chosen Strategy)
Adopt **Adaptive Thresholding** as the default:

- Use a local window (e.g., 11x11) to handle gradients and semi‑transparent UI.
- This is more stable for game scenes than a global threshold.
- Otsu’s method is not used by default in this plan.

#### Step 4: Morphology (Optional)
Use light dilation only when fonts are thin:

- Single‑pass dilation with a small kernel (e.g., 3x3).
- Avoid over‑dilation which merges glyphs.

---

### 3. Skip OCR on Unchanged Frames
If the current capture is visually identical to the previous frame, OCR can be skipped:

- Use pHash (already in pipeline) to detect no‑change frames.
- If pHash difference is below threshold, **skip OCR** and keep last overlay.

This reduces redundant OCR calls and improves responsiveness.

---

### 4. Performance Guardrails
- Always crop to ROI before scaling and thresholding.
- Cap maximum pixel dimensions.
- Avoid multi‑pass retries; do a single deterministic pass.
- Reuse buffers where possible to reduce allocations.

---

### Summary: Recommended Initial Implementation
1. **ROI crop → 2x scaling (Bicubic/Lanczos)** with max dimension cap.
2. **Grayscale + conditional inversion** (black text on white background).
3. **Adaptive Thresholding** as the default binarization.
4. Optional **light dilation** for thin fonts.
5. **Skip OCR** when pHash indicates no change.

---