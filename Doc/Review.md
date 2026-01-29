# Code Review

## Findings (ordered by severity)

### Medium
- Stale in-memory translations ignore setting changes; `_lastTranslations` is keyed only by normalized text, so switching source/target/style can reuse prior translations. Consider scoping to settings or clearing on change. (`Services/PipelineOrchestrator.cs:25`)
- Translation retry gap: only `changedSet` lines are sent to Gemini; unchanged lines that were missing translation (e.g., Gemini disabled or a transient failure) will never be translated until the text moves/changes. (`Services/PipelineOrchestrator.cs:195`, `Services/PipelineOrchestrator.cs:222`)
- Cache key collisions are possible because components are concatenated with `_` without escaping; different setting combinations can map to the same key. This can serve the wrong cached translation. (`Services/CacheKeyBuilder.cs:9`)
- Gemini schema hints may be ignored if the API expects camelCase (`responseMimeType`, `responseSchema`), which would yield non-JSON responses and silent translation failures. (`Services/GeminiClient.cs:60`)

### Low
- Duplicate source texts are collapsed into a dictionary keyed by `source_text`; if the model returns different translations per occurrence, only one survives. (`Services/GeminiClient.cs:142`)
- The pipeline runs on the UI thread; capture/OCR/translation can block UI responsiveness and hotkey handling. Consider moving work off-thread and only marshaling overlay updates. (`MainWindow.xaml.cs:95`)

## Open Questions / Assumptions
- Should translations refresh immediately when language/style settings change even if OCR output is unchanged?
- Is the Gemini endpoint confirmed to accept snake_case `response_mime_type`/`response_schema`, or should this be camelCase?

## Change Summary
- Review only; no code changes.

## Testing / Verification Gaps
- No automated tests were found for cache key generation, translation retry behavior, or OCR diff logic.