# RAW camera preview and targeted browsing corrections

The normal RAW viewing path completes when the camera's embedded preview is available. It no longer unconditionally develops RAW pixels after showing that preview. Zoom and magnifier requests retrieve higher resolution embedded pixels, preserving the camera rendering. Dimensions and 100% scaling describe the embedded image, not sensor pixels. When no embedded preview can be decoded, the existing bounded RAW decoder remains a fallback; cancellation, stale input and access errors do not trigger fallback. No source file is modified and no new persistent cache is introduced.

Decoded images are presented before supplementary metadata writes. Metadata tasks use the browser operation lifetime, source generation checks and error reporting. They cannot prolong the opening indicator merely because the catalog writer is busy.

Related confirmed review corrections:

- Slideshow ticks work against both the first page and the completed virtual results. Non-images are skipped, retired reads cannot navigate, and an active slideshow resumes after a retired tick.
- Deleting the displayed collection clears its invalid browser scope and returns to the initial browser. Only collection mappings are deleted; source files are untouched.
- Menu path copying reports clipboard failure through the existing UI error path.
- Markdown images lease a decoder from the same bounded thumbnail pool instead of calling an instance that may already be leased to a visible thumbnail. The lease is returned after releasing the output, including cancellation and failure paths.

Review items not adopted: .NET Windows file-path URI construction correctly escapes `#` and literal `%`; the 3-second Markdown parsing deadline is an explicit existing performance constraint. Nonanimated multipage inputs are currently classified as `other`, whose icon is visible after thumbnail removal; changing that classification requires a separate supported-format decision, not removing one clearing statement.

Validation: the extracted old RAW request sequence failed the successful-embedded-preview test (returned `fit`); the corrected sequence passes success, decode fallback, cancellation and invalid-source cases. A real public Sony ARW was read through WorkerClient with only `rawEmbedded`, producing pixels and releasing its output. This is decoder evidence, not native presentation latency or an assertion about every RAF file. Native regression scenarios cover first-page slideshow, clipboard failure and deleting the active collection, but remain NOT_RUN in this environment. Full tests and build results are recorded in the local development log.
