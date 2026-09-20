#!/usr/bin/env bash
#
# Opens the top three code-bloat refactoring issues on the current GitHub
# repository. Run from anywhere inside the repo; `gh` resolves the remote.
#
#   ./create_issues.sh
#
# Requires the GitHub CLI (https://cli.github.com) and an authenticated session
# (`gh auth login`). Each issue is created with `gh issue create`; the script
# stops at the first failure so a partial run is obvious.

set -euo pipefail

if ! command -v gh >/dev/null 2>&1; then
    echo "error: the GitHub CLI (gh) is not installed." >&2
    echo "       see https://cli.github.com" >&2
    exit 1
fi

if ! gh auth status >/dev/null 2>&1; then
    echo "error: gh is not authenticated. Run 'gh auth login' first." >&2
    exit 1
fi

if ! gh repo view >/dev/null 2>&1; then
    echo "error: not inside a GitHub repository, or the remote is not reachable." >&2
    exit 1
fi

echo "Creating issues in $(gh repo view --json nameWithOwner --jq .nameWithOwner)…"

# ---------------------------------------------------------------------------
# 1. Port the Swift PageCheckCoordinator model to C# and Kotlin
# ---------------------------------------------------------------------------
gh issue create \
    --title "Port the Swift PageCheckCoordinator model to C# and Kotlin" \
    --body-file - <<'EOF'
The once-per-page regeneration check (#139, #145) is implemented three times, in three
languages, with three different data models for the same policy:

- **Swift** — `ios/MegaPDF/PageCheckCoordinator.swift`: a single `running: (page, task)`
  tuple, a `Set<Int> settled`, an `awaitedPage` guard, and a `generation` counter.
- **C#** — `src/MegaPDF.Core/Editing/PageRegenerationWarnings.cs`: a
  `Dictionary<int, RunningCheck>` keyed by page, a `HashSet<int> _settled`, a
  `_generation` counter, and a `_gate` lock.
- **Kotlin** — `android/app/src/main/java/com/megapdf/android/PageCheckGate.kt`: a
  `CompletableDeferred<Boolean>` for the warning question mixed into the same class as
  the check itself.

The policy is identical in all three:

- one shared check per page;
- starting a check for one page cancels an unfinished check for another, except one a
  change is waiting on;
- settled pages are never checked again;
- a change waits at most the budget (1.5 s), then the check is cancelled and the change
  applies;
- a page that keeps its look, or can't be judged, is settled; one that would change is
  left unsettled.

Three shapes for one policy is a drift risk: a fix to the budget race in one language
does not reach the other two.

## Recommendation

The Swift model is the cleanest — a single running check makes the "cancel the others"
rule true by construction, and the `awaitedPage` guard is explicit. Port it to C# and
Kotlin.

- **C#**: replace the `Dictionary<int, RunningCheck>` with a single
  `(int Page, Task<LayoutVerdict?> Task, CancellationTokenSource Cancel)?` field and
  drop the `_running` dictionary.
- **Kotlin**: separate the check from the question. The `CompletableDeferred` belongs in
  the view model, not the gate.

## Acceptance

- All three platforms have the same data model for the check.
- The budget race (check answers vs budget expires) is decided the same way on all three.
- A per-platform test exercises the race: start a check, close the document, assert the
  check returns cancelled and the document is freed.
EOF

# ---------------------------------------------------------------------------
# 2. Bring the mobile signature stores up to the desktop's behaviour
# ---------------------------------------------------------------------------
gh issue create \
    --title "Bring the mobile signature stores up to the desktop's behaviour" \
    --body-file - <<'EOF'
The signature library is implemented three times, and the mobile versions are missing
two behaviours the desktop version has:

- **C#** — `src/MegaPDF.Core/Services/SignatureLibrary.cs`
- **Swift** — `ios/MegaPDF/SignatureStore.swift`
- **Kotlin** — `android/app/src/main/java/com/megapdf/android/SignatureLibraryStore.kt`

## Differences

1. **Soft limit.** Only the C# version enforces `SoftLimit = 20`. The Swift and Kotlin
   versions will grow without bound.

2. **Missing-file pruning.** Only the C# `Load` drops index entries whose PNG is gone.
   The Swift and Kotlin versions will show broken thumbnails for a deleted or
   unreadable image.

3. **Delete order.** The C# `Remove` removes the index entry first, then deletes the
   PNG; the Swift and Kotlin versions delete the PNG first, then the index entry.
   Neither is clearly better, but they should agree. The C# order is safer: an orphan
   PNG is invisible, an orphan index entry is a broken row.

## Recommendation

Bring the Swift and Kotlin versions up to the C# behaviour (soft limit + missing-file
pruning), and pick one delete order for all three. The C# order (index first) is the
one to standardise on.

## Acceptance

- All three stores enforce the same soft limit.
- All three drop index entries whose image file is missing.
- All three delete in the same order, and the order is documented.
EOF

# ---------------------------------------------------------------------------
# 3. Stop VerifiedSave.ToPath creating two temp files per save
# ---------------------------------------------------------------------------
gh issue create \
    --title "Stop VerifiedSave.ToPath creating two temp files per save" \
    --body-file - <<'EOF'
`VerifiedSave.ToPath` (`src/MegaPDF.Core/Services/VerifiedSave.cs`) creates a staging
file **beside the destination** (#193), then hands the staged path to
`AtomicFileWriter.Write`, which creates a **second** temp file in the same folder and
swaps that one into place.

So a save to a path holds two full copies of the document on disk at once: the
verification staging file and the atomic-swap temp file. On a large document that is
the cost #147 and #148 took out of opening one, reintroduced on the save path.

## Recommendation

Pass the staging file's path through to `AtomicFileWriter` and have it swap that file
directly, instead of copying it into a second temp file. The staging file is already
in the destination's folder, so the swap stays a rename and stays atomic.

This needs a small change to `AtomicFileWriter`'s API — a variant that takes an
already-written temp file rather than a `writeContent` callback — or a new method on
`VerifiedSave` that does the swap itself.

## Acceptance

- A save to a path creates one temp file, not two.
- The swap is still a rename on the same filesystem, so it is still atomic.
- The existing `AtomicFileWriter` behaviour (including the hidden-file fallback for the
  snap's `home` plug) is unchanged for callers that still use it.
EOF

echo "Done."
