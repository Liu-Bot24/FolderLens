# Audio-only media, video controls and image zoom validation — 2026-09-22

Baseline: `b9fb1fb`. This change addresses three preview behaviors; it is not a complete release acceptance result.

## Findings and changes

- Audio-only MP4: a cold thumbnail request captured the original video classification before probing. The probe correctly returned no visual stream and updated the catalog to audio, but the request still attempted to load a nonexistent image. Completion now uses the returned stream/cover metadata. A ready audio classification displays a neutral music icon and “仅音频”; unknown metadata and real decoding errors are not converted into success. File-version checks remain enforced.
- Video controls: disabling `MediaPlayer.CommandManager` disconnected the built-in transport controls. The command manager is enabled again. The input filter also incorrectly treated the transport template's full-surface root as an interactive button, rejecting taps on the video itself. Only interactive controls and their descendants are excluded from surface toggling. The existing play state events continue to synchronize the upper button.
- Image zoom: all user-facing fit actions now toggle whole-image fit and width fit aligned to the top. The internal whole-image reset remains separate for point-zoom gestures. The 100% action resets pan before applying native physical-pixel scale, so it centers the image. Point-based zoom retains its existing anchor calculation.

Native transport dependency: [Microsoft MediaPlaybackCommandManager.IsEnabled documentation](https://learn.microsoft.com/en-us/uwp/api/windows.media.playback.mediaplaybackcommandmanager.isenabled?view=winrt-26100).

## Evidence

Local evidence is under the ignored `artifacts/audio-only-mp4-20260922/` directory. Private media and extracted images are not included in the repository.

| Check | Result |
| --- | --- |
| Original audio-only sample, cold thumbnail path | Red in `red/native-refresh.json`; green in `audio-green/native-refresh.json`; real card render inspected |
| Stale media metadata / replaced file version / actual thumbnail failure | Native audio-only verification preserves distinct states |
| Actual native transport automation | `controls-red2` fails on the disabled play control; `controls-final` passes pause, resume, seek and upper-button synchronization |
| Video surface target | Actual WinUI hit test identifies the full-surface transport root; fixed filter allows it and rejects native buttons to avoid double toggling |
| Video lifetime | Five retired players, zero reachable after diagnostic GC; normal stop, deadline and invalid video covered |
| Ordinary video covers | `video-regression` passes cold cards, foreground previews and navigation |
| Long-image fit / 100% / point zoom | `zoom-red` reproduces inert fit button; `zoom-green` passes button automation in small and window preview layouts, top/center/point anchors, Alt/Ctrl/plain wheel |
| Existing image behavior | `regression-fit-lock`, `regression-press-gesture`, `regression-keyboard-completion` pass |
| Release build | Pass; NU1900 vulnerability-feed access warnings remain |
| Complete unit suite | First run: 576/577, a temporary fixture executable was locked during `LiveWorkerRemainsBoundToKillOnCloseJob` teardown. No production or test change was made to hide it. Targeted rerun: 22/22. Full rerun: 577/577. Original failure retained in `tests/all.trx`, reruns in `retest/`. |

The teardown lock was intermittent and its external/kernel ownership was not established. Passing reruns do not establish that it is fixed.

These checks use real WinUI controls, native automation providers, hit testing and real media playback in hidden verification windows. They do not inject physical mouse events or claim foreground/fullscreen desktop acceptance. Fullscreen uses the same fit action as the verified preview controls. Final deployment checks are stored separately under `deployment/` and `deployed-*` after the user closes the application; source checks alone are not evidence of deployment.

Existing Explorer-location and long-duration memory issues are outside these fixes and remain unresolved.