# Local packaging evidence

- Windows PowerShell 5.1 parsed all authored entry scripts and their `-Help` paths ran successfully with process-scoped `-ExecutionPolicy Bypass`. Machine execution policy remains unchanged. The literal `powershell -NoProfile -File` command is blocked by this host's policy; `build-release.cmd` supplies the process-scoped setting.
- `Check-Environment.ps1` inspected the pinned SDK and required tool/runtime paths; report: `artifacts/environment.json` and `.md`. Presence checks do not prove actual toolchain or product correctness.
- `Bootstrap.ps1 -NonInteractive` completed the already-installed dependency path. Restore-after-removal was not run; this script only extracts locally available, hash-approved archives and never silently downloads or installs system compilers.
- `Write-Sbom.ps1 -AppRoot artifacts/m8/notice-check` generated the dependency inventory and copied available notices. A check-only Inno installer compiled successfully against that notice fixture. It is not an application package and was not installed.
- Shared process logging was exercised with stdout, stderr and an intentional exit code 7. Its first version exposed PowerShell `VoidTaskResult` objects; explicit void suppression fixed the issue. Final check retains both streams and correctly reports exit 7. Logs: `artifacts/logs/packaging-script-check/nonzero-fixed.*`.
- Final application publish, portable ZIP, actual setup EXE, portable launch, clean-machine installation, upgrade/cancel/uninstall and P1 acceptance are not proved by these checks. Their current status remains NOT_RUN until actual evidence is generated.

# WebView2 notice review

The exact fixed runtime is 153.0.4234.32 x64. The full verified CAB extraction is retained, including Widevine and protection-list license files and `show_third_party_software_licenses.bat`. That upstream batch file invokes `msedgewebview2.exe --embedded-browser-webview=show-credits`; embedded Chromium credits are therefore preserved in the untouched runtime, but have not been independently exported/read in this check. No UI was opened just to run this batch file.

The Microsoft.Web.WebView2 NuGet package has a BSD-style SDK license and NOTICE.txt. Those are preserved, but they are not substituted for the separately licensed fixed browser runtime. The runtime's top-level Microsoft fixed-version license text has not yet been captured, so this remains part of the redistribution review gap.

Microsoft's first-party runtime distribution guidance confirms that a fixed runtime is packaged with the application and is updated through new application packages: https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution . The full license/source obligations of the complete application remain uncleared; this is not a legal clearance.

The actual RawBridge and LibRaw DLL imports were inspected with MSVC dumpbin. Both require MSVCP140, VCRUNTIME140 and VCRUNTIME140_1 plus Windows UCRT/API-set dependencies; Publish copies the matching x64 Microsoft.VC143.CRT from the official Visual Studio redistributable directory and pins its file hashes. Logs: artifacts/logs/packaging-script-check/rawbridge-dependencies.* and libraw-dependencies.*. This is dependency inspection, not clean-Windows execution.
