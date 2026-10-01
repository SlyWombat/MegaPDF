# Working on MegaPDF

Read this before starting. It is the operational knowledge that is not derivable from the
code, written down because it was being re-explained in every agent brief and relearned by
hitting it anyway.

## The project

A PDF reader and editor on five platforms over one shared C++ core.

| area | path | notes |
|---|---|---|
| core | `core/` | C++ over PDFium, the single source of behaviour |
| shared .NET | `src/MegaPDF.Core/` | P/Invoke layer, used by both desktop apps |
| Windows | `src/MegaPDF.App/` | WinUI 3 |
| Mac + Linux | `src/MegaPDF.Avalonia/` | **one binary for both** — a change here lands on two platforms |
| iOS | `ios/` | SwiftUI |
| Android | `android/` | Compose |

We build our own PDFium with a patch series (`MEGAPDF_PDFIUM_PATCHES` gates them at compile
time). Do not assume upstream PDFium behaviour.

**Platform-native conventions are a rule, not a preference.** Same goal on every platform, each
in its own idiom. Never transplant a Windows pattern onto a Mac or a phone. A desktop checkbox
and an Android bottom sheet can be the same feature done right twice.

## Machines

**`GPD-DAVE` is the machine you are already on. It is not an SSH target.** `uname -a` says
GPD-Dave and your `/mnt/d/Projects/MegaPDF` *is* `D:\Projects\MegaPDF` there. Windows work means
driving PowerShell from WSL, not opening a session. Two agents have burned time on
`ssh GPD-DAVE` and one reported that the box "needs a key added". It does not.

| machine | reach | use | disk |
|---|---|---|---|
| **GPD-DAVE** | you are on it | WinUI builds, Windows self-test | `D:` |
| **kdocker3** | `ssh kdocker3` | general builds, corpus batteries | `/home/claude`, 832G |
| **kdocker2** | `ssh kdocker2` | Android (`mingc/android-build-box`, SDK, **no emulator**) | **`/data/megapdf-work` — root is only 119G and Docker lives on it** |
| **Mac mini** | `ssh mac-mini` as `claude` | iOS/macOS builds and simulators | `~` |

On kdocker2 put `GRADLE_USER_HOME` on `/data` too, or you fill the root volume. `/data` is
Dave's; only touch `/data/megapdf-work`.

**Windows specifics:** per-user dotnet at `C:\Users\Sly\AppData\Local\Microsoft\dotnet` (the
system one has no SDK); no Visual Studio; `gh.exe` not `gh`; `sleep` is blocked; `grep` is a
shell function, not the binary. Use a `.ps1` file, never inline `-Command` — quoting and
redirection get mangled through bash. Stage scripts in `C:\temp`, read output back from there,
clean up after. **Killing the app writes a crash-recovery journal on the way down, so delete it
*after* the kill, not alongside it.**

**Leave every machine as you found it.** No leftover processes, no toggled settings, no open
windows, no scratch. This is explicit and repeated.

## Worktrees — the shared checkout is not yours

Several agents and the coordinator use `/mnt/d/Projects/MegaPDF` simultaneously. Editing it
blocks `main` from updating, which blocks merges, and has nearly destroyed an agent's
uncommitted work.

    git -C /mnt/d/Projects/MegaPDF worktree add .claude/worktrees/<name> -b wip/<issue>-<slug> origin/main

Read the shared tree freely; write only in your worktree. **Your Edit/Write tools do not work
over SSH**, so for remote builds author in the local worktree and sync up to a remote clone.
That constraint is why the shared tree keeps getting dirtied — it is the nearest local surface.

If you must recover a dirty shared tree: `git stash push -u -m "<tag>" -- <narrow path>` naming
only your files, then apply and drop **by SHA**. Never a bare `pop` — the stash stack is shared
and a pop can take someone else's work.

**Scratch** goes in the session scratchpad or `D:\megapdf-qa` on Windows. Use unique filenames;
two agents both writing `commit-msg.txt` once meant one picked up the other's message.

## Never end your turn waiting on a job

A background command, a `Monitor`, or a CI run **will not wake you**. You stop and stall until
someone nudges you. This has happened six times. Poll in the foreground instead, inside the same
turn:

    loop on `gh pr checks <n>` with a sleep between polls until the checks settle

Standing by for a *message* is fine and does reach you. Waiting on a *job* does not.

## CI

**`tests/matrix/coverage.toml` is enforced.** Every test file must be cited by a cell or CI
fails with `unmapped`. `docs/qa/test-matrix.md` is generated from it and goes stale. Always:

    python3 tools/qa/check-matrix.py --check

**The Windows self-test blocks merges:** `MegaPDF.exe --screenshot <png> --screenshot-state
<state> <pdf>`. Add a state when you add UI. Run it locally — a state that has never run
locally reds `main` for everyone.

**Four ways a PR gets no CI at all while looking mergeable.** `gh pr checks` printing "no checks
reported" is a red flag, never a pass:

1. **Stacked base** — workflows trigger on `pull_request: branches: [main]` only. Check
   `gh pr view <n> --json baseRefName` before trusting any check state.
2. **Unmergeable branch** — GitHub cannot build the merge commit, so no run is created.
3. **`workflow_dispatch` runs** — they pass, they look green, they gate nothing.
4. **A force-push after a rebase** sometimes fires nothing. Remedy: an empty nudge commit.

Retargeting with `gh pr edit --base main` fires `pull_request: edited`, which the default
triggers ignore, so **no CI starts**. Closing and reopening the PR fires `reopened`, which works.

Verify a merge honestly: a run whose `event` is `pull_request` **and** whose `head_sha` matches
the PR's current `headRefOid`.

**Long iOS `build` is usually the GitHub macOS queue** (one run waited 25m56s), not a hang. When
`MAC_SELF_HOSTED` is `on`, those jobs go to our Mac mini and start in seconds; the compute is a
dead heat, so what it buys is the queue.

## Localisation

Three locales: `en`, `fr-CA`, `fr-FR`.

- **fr-CA is hand-written. fr-FR is derived** by `tools/gen_strings.py`. Edit the Canadian
  source and regenerate; **never hand-edit fr-FR.**
- `docs/localisation-glossary.md` is binding. **Glossary merge conflicts are always resolved by
  keeping BOTH sides' rows** — picking one side has lost real content.
- Mark every new French string `FR-REVIEW #<issue>` in that platform's own idiom: an XML comment
  on Android, the `comment` field on iOS (**never** `stringUnit.state`, which parity tests read).
  Unmarked strings are invisible to the review that gates releases.
- France needs a non-breaking space before `; ? !`. Verify **by code point** — U+00A0 and a plain
  space are visually identical, which is how a bug survived a full audit.

**The signature vocabulary rule, which has caused repeated rework.** Follow Adobe:

- **electronic signature** — what the user draws, types or photographs and places on a page.
  Not cryptographic, not verifiable, not legally binding. Never imply otherwise.
- **digital signature** / **digital ID** — the certificate-backed kind. We do not implement it;
  we only detect and warn about one already in a document.
- Never "encrypted" for either. In French never « chiffrement » in this context — two
  independent authors reached for « celle du chiffrement » and both were wrong.

## Testing

Corpus batteries in `tools/stress/`: structure, markdown, pages, redaction. Gates are
F1 ≥ 0.998, order τ ≥ 0.9, zero crashes, zero hangs, zero out-of-contract refusals. Runs are
recorded in `TESTING.md` — add yours rather than replacing.

Four corpora: `~/pdf-test` (private), `~/pdf-public`, `~/pdf-test-ca`, `~/pdf-test-un`. Staged
documents are read-only by convention. Exclusions are recorded in
`EXCLUDED-FROM-THIS-CORPUS.tsv` with a stated reason, and every battery prints them by name.

**When a decision turns on a duration, measure the duration.** This has gone wrong twice in one
week, expensively. A test fixture was sized for a write "assumed to take real wall time" and
measured 373 ms, so the test asserted a Stop button for work already finished. A toolbar flicker
was designed around as visible and measures 7 ms against a 16.7 ms frame. Both traded a real
property away for a cost that did not exist.

The sibling error: **checking that an API is used nearby in the same style is not the same as
checking that a specific argument combination is a real overload.**

## Issues and commits

- Milestones are **Dave's alone**. Never create one, never move an issue between them. Deferred
  work is filed **unmilestoned**.
- A label that does not exist makes `gh issue create` fail. Run `gh label list` first.
- Write closing comments for someone reading in six months who will not open the PR: what was
  verified, where, and what remains elsewhere.
- **A secrets hook blocks some shell text** on words like `credentials`, `process` and `history`,
  and blocks any command whose *output* could carry a secret — `ps aux`, `env`, `docker inspect`.
  These are false positives on prose, not errors to fight: write bodies to a file with the Write
  tool and pass `--body-file`. To inspect processes use `ps -eo pid,etime,comm` or `pgrep`
  without `-a`. Never pass a secret in argv.

End commit messages with the attribution lines the session gives you, and PR descriptions with
the generated-with footer.

## Report honestly

The thing most valued here is an accurate account. Say what you verified and how, say what you
did not, and say when the issue's own theory was wrong — it usually is. Of the diagnoses made in
one recent day, the stated theory was wrong in nearly every case, and what settled each one was
measuring something rather than reasoning about it. A refusal to close an issue, with evidence,
is worth more than three closures where one was a stretch.
