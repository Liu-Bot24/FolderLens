# Text thumbnail validation — 2026-09-22

Baseline: `bb04083`.

## Behavior and resource bounds

Text and Markdown cards show a plain-text prefix at readable sizes. Cards below 144 DIPs retain the file icon without requesting an excerpt. The fixed 13-DIP font and 18-DIP line spacing determine a capped line count from the card size; WinUI wraps and trims inside the existing thumbnail frame. Markdown markup is displayed as text, with no renderer, network resource loading or image decoding.

The UI requests a 256/512/768/1024-byte prefix budget according to card size. Encoding detection uses the same bounded sample rather than the normal reader's 256 KiB sample. The existing reader then decodes one bounded window at a valid character boundary. Thus the two reads total at most 2 KiB plus a few boundary bytes, irrespective of source size; this is not a one-read or entire-document operation. Small documents may naturally fit within that bound.

Requests run serially on a separate visible-priority Content Worker, within the shared process/resource budget, with a five-second request deadline. Existing thumbnail admission, cancellation, file-version ownership, source signatures and cloud approval remain in effect. No source reads run on the UI thread. The worker closes the excerpt reader after each response, builds no line index and retains no open source handle between excerpts.

Slider changes use a 180 ms debounce. Existing excerpts serve smaller cards immediately; larger cards request more only if the cached byte budget is insufficient and EOF was not reached. Cache retention is limited to currently realized cards plus 128 offscreen excerpts, independent of WinUI's retained row identities. Switching result roots clears the cache. No excerpt is persisted to disk.

## Verification

Evidence: local ignored `artifacts/text-thumbnails-20260922/`.

- Release build passed; existing NU1900 package vulnerability-feed access warnings remain.
- Targeted text/layout tests: 20 passed before the final cache addition.
- Final complete unit suite: **588/588 passed**, `tests/all.trx`.
- Remote worker tests cover UTF-8, UTF-16LE/BE and GB18030, multibyte boundaries, an 8 MiB file with an invalid distant tail, empty files, binary prefix rejection, source replacement with preserved size/time, cancellation and budget rejection. The invalid distant tail is a negative control against inadvertently reading/detecting the full document.
- Real WinUI hidden-window verification: `final-text-thumbnails/native-refresh.json` passes. Minimum-size cards request no excerpts. Slider 160 yields four lines (actual filled card width 166); slider 240 yields seven lines (width 242). Reads are two 512-byte budgets and one 1024-byte budget; the small complete Markdown file needs no second read. Shrinking and regrowing reuses cached content. File-version/error state and bounded offscreen cache eviction pass.
- Actual card renders at sizes 100/160/240 were inspected; wrapping and clipping remain within the card. Screenshot data is synthetic.
- Existing native text reader and Markdown demand-loading checks pass in `final-text-reader` and `final-markdown-demand`.

These are real WinUI control/render checks in hidden windows. Physical mouse dragging, the full font/encoding matrix and SMB-server performance were not separately measured. Passing the checks above does not establish overall release acceptance. Deployment status and verification, when executed, are recorded separately under `deployment/` and `deployed-*`.