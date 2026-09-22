# SMB image identity and file location validation — 2026-09-22

## Changes

On an SMB server without `FileIdInfo` support, directory scanning and source probing already returned a legacy 64-bit file identity. Image decoding used `FileReadObservation`, which lacked that fallback. Its signature therefore had an empty identity, and the image worker rejected an unchanged JPEG with `FileChanged` before decoding.

`FileHandleIdentity` now supplies the same handle-based identity to both `FileAllocation` and `FileReadObservation`. Extended and legacy signature formats are preserved. Legacy IDs still require a nonzero file index and a matching creation time from the same handle. No timestamp comparison, replacement check, or worker source validation is removed.

The file-location command previously passed `/select,` plus the entire source path as one `ProcessStartInfo.ArgumentList` value. That relied on Explorer command-line parsing and reproduced the user's wrong-folder symptom at the UI level; the exact Explorer parser behavior was not independently captured. It now passes a fully qualified Shell item through `SHParseDisplayName` and `SHOpenFolderAndSelectItems`. There is no fallback to Documents or another directory. Missing/inaccessible paths and native failures are reported. Work runs on one bounded STA, has a ten-second caller deadline, and honors selection cancellation before dispatch. An OS namespace call already in progress may finish later; the single admission slot remains held until cleanup, and canceled parsing cannot subsequently dispatch a location request.

The single-item API contract is documented by [Microsoft](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shopenfolderandselectitems). [Shell path parsing](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shparsedisplayname) is performed off the UI thread.

## Evidence

- Before repair, the reported mapped-share JPEG had identical scan/probe signatures but an empty identity in the decoder observation. A real media-worker thumbnail request failed with `FileChanged`.
- After repair, the same file passed thumbnail, fit, and full-resolution tile requests. The same checks passed through its UNC path, for a second SMB JPEG, and for the previously reported local path containing spaces, Unicode symbols, and a long folder name. All source observations remained unchanged.
- Actual offscreen WinUI browsing of the affected 39-image folder passed five viewport positions, each with six loaded thumbnails and zero errors. First rendered thumbnail was observed at approximately 929 ms; this is one warm-OS-cache sample, not a performance guarantee.
- Actual offscreen preview selection and drawing passed 111 successive image switches from that SMB folder. The first narrow-window test stopped before measurement because the existing large-view layout threshold was not met. It was rerun with the existing wide-window flag without changing the assertion.
- Release unit tests: **601 passed, 0 failed, 0 skipped**. New coverage checks decoder/scan identity agreement for extended and legacy IDs, rejects replacement despite equal size/timestamps, verifies exact Shell PIDLs for ordinary, Unicode/punctuation, and over-260-character paths, rejects missing/canceled requests, propagates native failure, and prevents a canceled queued request from dispatching later.
- Release application/workers build: zero errors; three existing `NU1900` warnings because the NuGet vulnerability endpoint was unavailable.
- Real mapped, UNC, and long local paths round-tripped through the exact Shell item supplied at the selection boundary. These checks intercepted the final Explorer call, so they did not open a foreground window.

Raw local evidence: `artifacts/smb-images-reveal-20260922` (ignored). It includes the original failure, repaired source signatures/worker operations, native UI reports, and `tests/all.trx`.

## Remaining verification boundary

The final Explorer window opening and visible selection still require an interactive acceptance check. PIDL tests establish the target supplied to the API, not the physical Explorer result. This change does not close unrelated phase-one performance, environment, installer, or full network-failure acceptance items.
