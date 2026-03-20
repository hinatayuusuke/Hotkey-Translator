# OneOCR Native Helper

This helper isolates `oneocr.dll` in a separate process and communicates with the
WPF app over a named pipe with length-prefixed UTF-8 JSON messages.

## Expected vendor files

Place these files under `vendor/` manually:

- `oneocr.dll`
- `oneocr.onemodel`
- `onnxruntime.dll`

The helper does not extract or redistribute Snipping Tool assets automatically.
