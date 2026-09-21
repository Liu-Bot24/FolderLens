# Text thumbnail validation — 2026-09-22

Current adjustment baseline: `4c42827`. Initial implementation baseline: `bb04083`.

## Behavior and resource bounds

MD/TXT and other text cards show a plain-text prefix. Cards below 144 DIPs retain the icon without requesting an excerpt. The slider now reaches 300, including saved-setting restoration. The text uses a 12-DIP font and 16-DIP line spacing. Empty and whitespace-only lines are removed from the displayed excerpt before applying its character limit; indentation and line breaks between nonempty lines remain. Source files and the full document reader are unchanged.

Layout follows the actual filled card width, capped at 320 DIPs for budgeting, with at most 512 displayed UTF-16 units. WinUI wraps and trims at the calculated line limit. Markdown markup is plain text: no renderer, image loading or network requests.

The worker receives a size-dependent prefix budget rounded to 256-byte increments, at most 2048 bytes. Both encoding detection and the single text window are limited to that budget, so the two reads total at most 4 KiB plus a few boundary bytes, regardless of source size. Empty-line removal only examines this bounded prefix; it does not search an entire blank-heavy file for later content.

Requests run serially on a separate visible-priority Content Worker with a five-second deadline and the existing shared resource budget, thumbnail admission, cancellation, file-version ownership, source signatures and cloud approval. No source reads execute on the UI thread. The reader closes after each excerpt and does not build a line index.

Slider changes use a 180 ms debounce. Existing prefixes serve smaller cards; larger cards read again only if their budget exceeds cached data and EOF was not reached. Retention is limited to realized cards plus 128 offscreen excerpts. Root changes clear this cache. No excerpts are persisted to disk.

## Latest verification

Local ignored evidence: `artifacts/text-thumbnails-compact-20260922/`.

- Release build passed; existing NU1900 vulnerability-feed warnings remain.
- Full unit suite: **589/589 passed**, `tests/all.trx`.
- Empty-line cases cover CRLF/LF/CR, whitespace-only lines, leading blanks, preserved indentation, unchanged nonempty lines and surrogate boundaries.
- Remote worker cases cover UTF-8, UTF-16LE/BE, GB18030, 512-byte and 2048-byte windows over an 8 MiB file with an invalid distant tail, empty/binary content, replacement with preserved size/time, cancellation and rejection beyond the hard budget.
- Real hidden WinUI verification: `native/native-refresh.json` passes. Slider 160 displays 5 lines, 240 displays 8, and 300 displays 11 at actual filled widths 166/242/312. The slider reaches the requested value; rendered excerpts have no empty lines. Screenshots at all sizes were inspected.
- Minimum-size cards request no excerpts; shrinking and regrowing reuse data. The offscreen cache remains bounded, replaced file versions clear old excerpts, and errors remain distinct.
- Previous complete checks, including native full-text reading and Markdown demand loading, remain under `artifacts/text-thumbnails-20260922/`.

These checks use real WinUI controls and rendering in hidden windows. Physical mouse dragging and SMB-server performance were not separately measured. Deployment and deployed-native checks are recorded separately under `deployment/` and `deployed-*`; these results do not establish overall release acceptance.