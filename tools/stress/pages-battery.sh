#!/usr/bin/env bash
# The #174 corpus battery for contract 10 (page tools): rotate, delete, move and extract on
# every document, through the shipped megapdf-cli `pages` command, one process per operation,
# and every output checked by qpdf and for its page count.
#
#   tools/stress/pages-battery.sh <megapdf-cli> <corpus-dir> <out-dir> [--limit N] [--jobs N]
#
# Per document, four runs on a scratch copy of the output path (the corpus is never written):
#   rotate   every page a quarter turn clockwise            → page count unchanged
#   delete   page 1                                          → one page fewer (skipped on a one-page document)
#   move     the last page to the front                      → page count unchanged (skipped on a one-page document)
#   extract  the first and last pages to a new file          → 2 pages (1 on a one-page document)
# Each output must open for `qpdf --check` (exit 0, or 3 for warnings, which are counted and
# reported) and have the page count expected. Required of a run: 0 crashes, 0 hangs, 0 qpdf
# failures, 0 page-count mismatches, 0 write failures, and no refusal that is not one of the
# two the contract documents: a document whose security forbids the operation (exit 8) and an
# extract of pages whose form fields sit in a /Parent hierarchy (MEGAPDF_ERR_FIELDS, exit 9 with
# that message). Any other engine refusal fails the run and is listed. A document that needs a
# password or has an unsupported security handler is counted and skipped; a document that does
# not open at all is counted (the corpus has broken files) and skipped.
#
# The corpus is personal (#151, #173). Nothing about a document leaves <out-dir>: the log holds
# one line per document keyed by a hash of its path, never its name, and the summary holds
# counts and timings only. Outputs are deleted as soon as they have been checked. Do not commit,
# upload or paste <out-dir>.
#
# --jobs N runs N documents at once (default 1). TIMEOUT (seconds per operation, default 300)
# bounds a hang. qpdf must be on PATH.
set -uo pipefail

CLI=${1:?usage: pages-battery.sh <megapdf-cli> <corpus> <out-dir> [--limit N] [--jobs N]}
CORPUS=${2:?}
OUT=${3:?}
shift 3

LIMIT=0
JOBS=1
while [ $# -gt 0 ]; do
    case "$1" in
        --limit) LIMIT=$2; shift 2 ;;
        --jobs) JOBS=$2; shift 2 ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
done
[ -x "$CLI" ] || { echo "$CLI is not an executable file" >&2; exit 2; }
command -v qpdf >/dev/null 2>&1 || { echo "qpdf is not on PATH" >&2; exit 2; }
if command -v timeout >/dev/null 2>&1; then TIMEOUT_CMD=timeout
elif command -v gtimeout >/dev/null 2>&1; then TIMEOUT_CMD=gtimeout
else TIMEOUT_CMD=""; echo "warning: no 'timeout' on PATH — hangs will not be bounded" >&2; fi

export CLI OUT TIMEOUT_CMD
export TIMEOUT=${TIMEOUT:-300}
mkdir -p "$OUT/scratch" "$OUT/lines"
LOG="$OUT/battery-pages.log"
SUMMARY="$OUT/summary-pages.txt"
rm -f "$OUT/lines"/* "$LOG"

# One operation: runs megapdf-cli, checks the output, prints one result token. Never the
# document's name: the scratch file is named by the path hash.
one_op() {   # <pdf> <id> <op> <expected-pages> <cli args...>
    local pdf=$1 id=$2 op=$3 expected=$4
    shift 4
    local out="$OUT/scratch/$id-$op.pdf"
    local err rc
    if [ -n "$TIMEOUT_CMD" ]; then
        err=$("$TIMEOUT_CMD" "$TIMEOUT" "$CLI" pages "$pdf" --out "$out" --quiet "$@" 2>&1)
    else
        err=$("$CLI" pages "$pdf" --out "$out" --quiet "$@" 2>&1)
    fi
    rc=$?
    rm -f "$OUT/scratch/.$id-$op.pdf."*.megapdf-tmp
    case $rc in
        0) ;;
        124) echo hung; rm -f "$out"; return ;;
        2) echo open-failed; return ;;
        3) echo protected; return ;;
        4) echo unsupported-security; return ;;
        7) echo write-failed; rm -f "$out"; return ;;
        8) echo restricted; return ;;
        9) case "$err" in
               *"hierarchy"*) echo refused-fields ;;
               *) echo "refused" ;;
           esac
           return ;;
        *) echo "crashed:$rc"; rm -f "$out"; return ;;
    esac
    if qpdf --requires-password "$out" >/dev/null 2>&1; then
        # The document opened without a password and was saved with its security kept; qpdf
        # cannot look inside without one. Counted as checked by the read-back only.
        echo ok-unchecked; rm -f "$out"; return
    fi
    local pages
    pages=$(qpdf --show-npages "$out" 2>/dev/null)
    if [ "$pages" != "$expected" ]; then echo "count-mismatch:${pages:-?}/$expected"; rm -f "$out"; return; fi
    qpdf --check "$out" >/dev/null 2>&1
    local q=$?
    rm -f "$out"
    case $q in
        0) echo ok ;;
        3) echo ok-warn ;;
        *) echo "qpdf-failed:$q" ;;
    esac
}

run_one() {   # <pdf>
    local pdf=$1
    local id
    id=$(printf '%s' "$pdf" | sha256sum | cut -c1-12)
    local n
    n=$(qpdf --show-npages "$pdf" 2>/dev/null)
    if ! [ "${n:-0}" -ge 1 ] 2>/dev/null; then
        # qpdf could not count the pages (a password, or a file it cannot read): let the
        # engine say what it is, through the rotate run alone.
        local r
        r=$(one_op "$pdf" "$id" rotate 0 --rotate 1-:1)
        case "$r" in
            protected|unsupported-security|open-failed|hung|crashed:*) echo "$id $r" >"$OUT/lines/$id"; return ;;
            *) echo "$id rotate=$r delete=skipped move=skipped extract=skipped (pages unknown)" >"$OUT/lines/$id"; return ;;
        esac
    fi
    local rotate delete move extract
    rotate=$(one_op "$pdf" "$id" rotate "$n" --rotate 1-:1)
    if [ "$n" -ge 2 ]; then
        delete=$(one_op "$pdf" "$id" delete $((n - 1)) --delete 1)
        move=$(one_op "$pdf" "$id" move "$n" --move "$n:1")
        extract=$(one_op "$pdf" "$id" extract 2 --extract "1,$n")
    else
        delete=skipped
        move=skipped
        extract=$(one_op "$pdf" "$id" extract 1 --extract 1)
    fi
    echo "$id pages=$n rotate=$rotate delete=$delete move=$move extract=$extract" >"$OUT/lines/$id"
}
export -f one_op run_one

started=$(date +%s)
if [ "$LIMIT" -gt 0 ]; then
    find "$CORPUS" -type f -name '*.pdf' -print | sort | head -n "$LIMIT"
else
    find "$CORPUS" -type f -name '*.pdf' -print | sort
fi | tr '\n' '\0' | xargs -0 -n 1 -P "$JOBS" bash -c 'run_one "$0"'
cat "$OUT/lines"/* >"$LOG" 2>/dev/null
elapsed=$(( $(date +%s) - started ))

count() { grep -c -E -- "$1" "$LOG" 2>/dev/null || true; }
seen=$(grep -c "" "$LOG")
{
    echo "documents seen:        $seen"
    echo "protected:             $(count ' protected$') (need a password)"
    echo "unsupported security:  $(count ' unsupported-security$')"
    echo "did not open:          $(count ' open-failed$')"
    echo "seconds:               $elapsed"
    for op in rotate delete move extract; do
        echo "$op:"
        echo "  ok:                  $(count "$op=ok( |$)")"
        echo "  ok, qpdf warnings:   $(count "$op=ok-warn")"
        echo "  ok, unchecked:       $(count "$op=ok-unchecked") (kept its security; qpdf cannot look inside)"
        echo "  skipped:             $(count "$op=skipped")"
        echo "  restricted:          $(count "$op=restricted")"
        echo "  refused, fields:     $(count "$op=refused-fields")"
        echo "  REFUSED, other:      $(count "$op=refused( |$)")"
        echo "  WRITE FAILED:        $(count "$op=write-failed")"
        echo "  COUNT MISMATCH:      $(count "$op=count-mismatch")"
        echo "  QPDF FAILED:         $(count "$op=qpdf-failed")"
        echo "  CRASHED:             $(count "$op=crashed")"
        echo "  HUNG:                $(count "$op=hung")"
    done
    echo "crashed or hung before the page count was known: $(( $(count ' hung$') + $(count ' crashed:') ))"
} | tee "$SUMMARY"

bad=$(( $(count '=crashed') + $(count ' crashed:') + $(count '=hung') + $(count ' hung$') + $(count '=qpdf-failed') \
      + $(count '=count-mismatch') + $(count '=write-failed') + $(count '=refused( |$)') ))
[ "$bad" -eq 0 ]
