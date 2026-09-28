# Windows QA harness (2.0 RC pass)

The PowerShell scripts that drove the Windows 2.0 release-candidate pass
(2026-09-17/18): the flows (open, sign, redact, shrink, print, save, recover),
the large-file checks (#267/#270), French at several widths, and the
screen-reader pass. They're kept as working references, not polished tools:
read one before running it.

- `common.ps1`: shared helpers (UI Automation, window activation, clicks).
  Every script dot-sources it from beside itself.
- `nvda.ps1` / `nvda268.ps1`: screen-reader checks. NVDA with debug logging
  writes every utterance to `%TEMP%\nvda.log`, and these slice it per step,
  so no one has to listen.
- `xrefcheck.py`: reads a saved file's cross-reference table and reports
  negative or out-of-range offsets (#267).
- `wack-prep.ps1` / `wack-launch.ps1`: register and raise the elevated
  "MegaPDF WACK" scheduled task. The UAC prompt must be answered within about
  2 minutes, so only run it with someone at the console.
  `tools/Store-Submission.md` has the full recipe.
- `redirect-toolbar-check.ps1` (#427): a standalone regression check, not tied to the
  rc2 scratch layout below — it makes its own scratch copies of two repo fixtures under
  `%TEMP%` and cleans up after itself. Launches an unpackaged `MegaPDF.exe` with one file,
  then launches it again with a second (the single-instance redirect from #348 phase 2),
  and asserts Signatures/Add text/Whiteout/Redact are all UIA `IsEnabled=True` on the
  resulting second tab. This is a race (WinUI's x:Bind occasionally drops a PropertyChanged
  notification fired in the burst a fast redirect-opened tab settles in — see
  `DocumentViewModel.OpenSettled`), not a deterministic sequencing bug, so it repeats the
  scenario (`-Attempts`, default 15) rather than trying it once:
  `powershell.exe -NoProfile -ExecutionPolicy Bypass -File redirect-toolbar-check.ps1 -Exe <path to MegaPDF.exe>`
  (or set `$env:MEGAPDF_EXE` and omit `-Exe`). Exits 0 on an all-enabled result every
  attempt, 1 otherwise with a per-attempt report. There is no automated Windows
  equivalent of the Avalonia apps' `--self-test` to fold this into — this is the closest
  thing on this platform today.

**Scratch location.** The scripts read and write their documents under
`D:\megapdf-qa\rc2\`, the one Windows scratch folder for MegaPDF QA. Create it
before a run, put the fixtures there (the store demo documents come from
`tools/screenshots-windows/gen_store_docs.py`), and delete the folder when the
run is finished. Don't scatter scratch anywhere else on `C:` or `D:`.

The traps these scripts work around are in `docs/qa/` and the
Store-Submission runbook: title-bar clicks steal focus from dialog fields,
`SetForegroundWindow` is refused for background processes, and
`Add-AppxPackage` silently does nothing when the same version is installed.
