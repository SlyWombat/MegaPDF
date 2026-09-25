#!/usr/bin/env bash
# The #357 corpus check for the Markdown writer (MEGAPDF_WRITE_MARKDOWN), a sibling to #354's
# structure-battery.sh: that script gates token fidelity for megapdf-cli's *plain-text* output
# (structure_check.cpp's F1 against PDFium's own text); this one is Markdown-specific and does
# not duplicate it. Design §3/#357's own "Validation against the corpus" ask is narrower than a
# fidelity measure -- there is no ground truth for "is this heading level right" outside a human
# reader (the 30-document hand-checked sample, done separately, by hand, with --dump) -- so what
# this script gates is robustness at corpus scale: every document's --format md output, run
# through an independent CommonMark parser (`cmark`), parses without error. A stray unescaped
# character turning real document text into malformed or wildly misinterpreted Markdown would
# either make cmark fail outright or (more likely, since CommonMark is a very permissive grammar)
# never fail syntactically at all -- so this is a coarse "the writer never produces something an
# independent parser rejects" signal, not a substitute for the round-trip block-count assertion
# core_tests.cpp already makes on the golden fixtures (design §3's actual acceptance test).
#
#   tools/stress/markdown-battery.sh <megapdf-cli> <cmark> <corpus-dir> <out-dir>
#       [--limit N] [--seed N]
#
# Required of a run: 0 crashes, 0 hangs, 0 cmark parse failures on any document that produced
# output. Privacy discipline (design #142 §7, restated for #357's own text): summary.txt has
# counts only, keyed by the 12-char sha256 path hash the structure battery already uses --
# never a file name, never any document text, and nothing under <out-dir> is committed, uploaded
# or pasted.
set -uo pipefail

CLI=${1:?usage: markdown-battery.sh <megapdf-cli> <cmark> <corpus> <out-dir> [options]}
CMARK=${2:?}
CORPUS=${3:?}
OUT=${4:?}
shift 4

LIMIT=0
SEED=1
TIMEOUT=${TIMEOUT:-120}
while [ $# -gt 0 ]; do
    case "$1" in
        --limit) LIMIT=$2; shift 2 ;;
        --seed) SEED=$2; shift 2 ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
done
[ -x "$CLI" ] || { echo "$CLI is not an executable file" >&2; exit 2; }
command -v "$CMARK" >/dev/null 2>&1 || [ -x "$CMARK" ] || { echo "$CMARK is not runnable" >&2; exit 2; }

if command -v timeout >/dev/null 2>&1; then
    TIMEOUT_CMD=timeout
elif command -v gtimeout >/dev/null 2>&1; then
    TIMEOUT_CMD=gtimeout
else
    TIMEOUT_CMD=""
    echo "warning: no 'timeout' or 'gtimeout' on PATH — hangs will not be bounded." >&2
fi
run_with_timeout() {
    if [ -n "$TIMEOUT_CMD" ]; then "$TIMEOUT_CMD" "$@"; return $?; fi
    shift
    "$@"
}

mkdir -p "$OUT/scratch"
LOG="$OUT/markdown-battery.log"
SUMMARY="$OUT/markdown-summary.txt"
: >"$LOG"

visited=0; extracted=0; textless_only=0; encrypted=0; format_err=0
crashed=0; hung=0; cmark_fail=0; bad_exit=0
started=$(date +%s)

while IFS= read -r pdf; do
    visited=$((visited + 1))
    [ "$LIMIT" -gt 0 ] && [ "$visited" -gt "$LIMIT" ] && { visited=$((visited - 1)); break; }
    id=$(printf '%s' "$pdf" | sha256sum | cut -c1-12)

    mdfile="$OUT/scratch/md-$id.md"
    run_with_timeout "$TIMEOUT" "$CLI" extract "$pdf" --format md --quiet >"$mdfile" 2>/dev/null
    rc=$?

    if [ "$rc" -eq 124 ]; then
        hung=$((hung + 1))
        echo "$id hung" >>"$LOG"
        rm -f "$mdfile"
        continue
    fi
    case "$rc" in
        0) extracted=$((extracted + 1)) ;;
        5) textless_only=$((textless_only + 1)) ;;
        2) : ;;  # cannot open (format error or otherwise) — not this tool's concern
        3|4) encrypted=$((encrypted + 1)) ;;
        *)
            if [ "$rc" -ge 128 ]; then
                # A process killed by a signal (segfault, abort, ...): bash reports 128+signal.
                crashed=$((crashed + 1))
                echo "$id crashed rc=$rc" >>"$LOG"
            else
                bad_exit=$((bad_exit + 1))
                echo "$id bad-exit rc=$rc" >>"$LOG"
            fi
            ;;
    esac

    if [ -s "$mdfile" ]; then
        if ! "$CMARK" "$mdfile" >/dev/null 2>>"$LOG"; then
            cmark_fail=$((cmark_fail + 1))
            echo "$id cmark-parse-failed" >>"$LOG"
        fi
    fi
    rm -f "$mdfile"
done < <(find "$CORPUS" -type f -name '*.pdf' -print | sort)

elapsed=$(( $(date +%s) - started ))

{
    echo "markdown battery: $visited document(s) visited, ${elapsed}s (seed $SEED, limit $LIMIT)"
    echo "extracted (exit 0):      $extracted"
    echo "no text on any page (5): $textless_only"
    echo "password-gated (3/4):    $encrypted"
    echo "crashed:                 $crashed (gate: must be 0)"
    echo "hung (> ${TIMEOUT}s):    $hung (gate: must be 0)"
    echo "other bad exit code:     $bad_exit (gate: must be 0 -- valid codes here are 0/2/3/4/5, never 1/6/7/130)"
    echo "cmark parse failures:    $cmark_fail (gate: must be 0)"
} | tee "$SUMMARY"

[ "$crashed" -eq 0 ] && [ "$hung" -eq 0 ] && [ "$bad_exit" -eq 0 ] && [ "$cmark_fail" -eq 0 ]
