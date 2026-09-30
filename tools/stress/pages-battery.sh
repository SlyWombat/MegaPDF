#!/usr/bin/env bash
# The #174 corpus battery for contract 10 (page tools): rotate, delete, move, extract,
# insert-blank and import on every document, through the shipped megapdf-cli `pages`
# command, one process per operation, and every output checked by qpdf and for its page count.
#
#   tools/stress/pages-battery.sh <megapdf-cli> <corpus-dir> <out-dir> [--limit N] [--jobs N]
#
# Per document, seven runs on a scratch copy of the output path (the corpus is never written):
#   rotate      every page a quarter turn clockwise           → page count unchanged
#   delete      page 1                                         → one page fewer (skipped on a one-page document)
#   move        the last page to the front                     → page count unchanged (skipped on a one-page document)
#   extract     the first and last pages to a new file         → 2 pages (1 on a one-page document)
#   blank       an empty page appended after the last page,
#               sized like it (#567)                           → one page more
#   importself  every page of the SAME document, imported
#               into a fresh copy of itself, appended (#567)   → page count doubled
#   importpair  every page of a SECOND, different document,
#               imported and appended (#567)                   → this document's count + the other's
# Each output must open for `qpdf --check` (exit 0, or 3 for warnings, which are counted and
# reported) and have the page count expected. Required of a run: 0 crashes, 0 hangs, 0 qpdf
# failures, 0 page-count mismatches, 0 write failures, and no refusal that is not one of the
# two the contract documents: a document whose security forbids the operation (exit 8) and a
# copy of pages whose form fields sit in a /Parent hierarchy (MEGAPDF_ERR_FIELDS, exit 9 with
# a message naming "hierarchy") — this applies to extract as documented, and, since #567 added
# it, to blank's neighbour import in exactly the same way (megapdf_pages_import can raise the
# same MEGAPDF_ERR_FIELDS, for a different reason — see "#567: import" below). Any other engine
# refusal fails the run and is listed. A document that needs a password or has an unsupported
# security handler is counted and skipped; a document that does not open at all is counted (the
# corpus has broken files) and skipped.
#
# #567 (this battery's eighth run found the gap, #566/TESTING.md): `megapdf-cli pages --help`
# has always listed --blank and --import alongside the four operations above; this script
# never drove either, so the eighth run's clean result was true of four of #174's six
# operations, not six. Both are added here, on the same gates as the rest.
#
# #567: blank. The new page is sized from the page it follows — the same rule the desktop
# apps use (DocumentViewModel.Pages.cs's BlankPageSize): a blank page in an A4 document is A4,
# one in a Letter document is Letter — so a corpus of mixed and unusual page sizes, not the
# CLI's own Letter default, is the point of running this at all. Each document gets one blank
# page appended after its last, sized to that last page's own dimensions (`pdfinfo`'s "Page
# size", already rotation-adjusted the same way the app's own page list is; rounded to the
# nearest whole point because --blank takes whole points only, floored at 1 so a degenerate,
# near-zero box still reaches the CLI's own usage check rather than nothing to pass it at
# all). A page pdfinfo cannot size — should not happen, since this document already opened
# earlier in the same run — is counted as blank=dims-unknown and not gated: the harness
# failed to form a request, which is not the same thing as the engine refusing one.
#
# #567: import. This is the operation the issue named as mattering most: it is the one that
# can refuse deliberately (a page whose form fields sit in a hierarchy the copy cannot carry,
# same MEGAPDF_ERR_FIELDS as extract's), and it is the only one of the six that takes a SECOND
# document, so its interesting cases are combinations, not single files. Two calls per
# document, both population-wide, neither sampled, and together they are O(n) rather than the
# O(n²) a full pairwise sweep would cost:
#
#   importself   imports every page of the document into a fresh copy of ITSELF, appended
#                after the last page. Cheap — one file, already open — and, on this PDFium
#                (patch >= 33; core/megapdf_core.cpp), now the *only* reliable way left to
#                reach the field-hierarchy refusal at corpus scale. Extracting into a brand
#                new document never collides with a pre-existing field name (that is exactly
#                why the eighth run's extract measured 0 refused-fields on every corpus, per
#                core/megapdf_core.h's own comment on it), but importing a document into
#                itself guarantees the collision: every field name in the copy already
#                exists, verbatim, in the target. A document with ANY hierarchical (/Parent)
#                form field therefore refuses under importself — not a sampled fraction of
#                one, the whole population that has one. Expected page count: 2n.
#   importpair   imports every page of a SECOND, different document — the one standing half
#                the corpus's length further along this same sorted file listing, wrapping
#                around — appended the same way. One pairing per document (N calls total,
#                not N², and every document appears as an import target exactly once and as
#                an import source exactly once). Far enough apart in the sort order that a
#                pair rarely shares a source directory, this is the shape that exercises two
#                genuinely different producers' fonts, resources and field names meeting
#                inside one file — the case importself cannot manufacture, since there is
#                only ever one document in it.
#
# The pairing rule stated plainly, since the next person needs to know what the number
# covers: importself is exhaustive (every document, exercising the hierarchy-collision path
# on its own fields) and importpair is a single fixed, population-wide sample (every document
# once, against one deterministic partner) rather than every pair (quadratic, and not shown to
# be worth the added cost against what a fixed offset already reaches). Neither is a
# sub-sample of the corpus itself — every document that opens is visited by both.
#
# Either import call's OTHER document is independently qpdf --check'd and its own
# qpdf --show-npages folded into the expected page count and the damaged-input carve-out,
# exactly as the primary document already is for every other operation — a refusal or a
# page-count surprise caused by damage on the far side of an import must not be blamed on
# this document's own input_check, or a corpus with a `malformed` category (#434, #445) would
# false-gate a document that was clean going in for the misfortune of its pairing. When the
# other document's own page count cannot be established (a password, an unreadable file, or a
# timeout — the same three reasons the primary document's own count sometimes cannot),
# importpair still runs, with the page-count comparison skipped rather than made against a
# guess, same as #445 point 2 already does for the primary document.
#
# #445: these gates only mean what they say over documents that were valid to begin with. A
# corpus with a `malformed` category (#434) breaks that assumption, so each document's own
# `qpdf --check` result is recorded once (0 or 3, same two outcomes this battery accepts for
# our own output, count as clean; anything else, including a timeout, counts as damaged -- a
# timeout cannot prove the input clean) and carried into every operation on it:
#   - exit 1 (a usage error -- e.g. an argument that cannot apply, such as rotating a page
#     range against a document report of 0 pages) is its own outcome, not a crash.
#   - when qpdf could not count the input's own pages at all, the rotate probe below passes
#     no page-count expectation, instead of the placeholder 0 the code used to compare
#     against -- a real page count is not "wrong" for not being zero.
#   - a `qpdf --check` failure on OUR output is only a genuine defect (QPDF FAILED, gated)
#     when the input itself was clean; carrying an already-damaged stream through an
#     operation it was not asked to repair is correctly reported as qpdf-carried-damage
#     instead (counted, not gated).
#   - the same applies to an engine refusal (exit 9, not the documented /Parent-hierarchy
#     case): reported as refused-damaged-input (counted, not gated) on a damaged input,
#     REFUSED/other (gated) on a clean one. The issue's own alternative -- hard-coding "the
#     page could not be loaded" as a third documented refusal -- was not taken: that message
#     is specific to today's two known fixtures, while gating on the input's own qpdf --check
#     result also covers any other legitimate refusal a damaged document produces, without
#     naming it.
#
# The corpus is personal (#151, #173). Nothing about a document leaves <out-dir>: the log holds
# one line per document keyed by a hash of its path, never its name, and the summary holds
# counts and timings only (importpair's log line adds the OTHER document's own page count,
# a number, never its path or hash). Outputs are deleted as soon as they have been checked.
# Do not commit, upload or paste <out-dir>.
#
# --jobs N runs N documents at once (default 1). TIMEOUT (seconds per operation, default 300)
# bounds a hang. qpdf and pdfinfo (poppler-utils) must be on PATH.
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
# #567: blank needs a real page size to be worth running (see the header comment) --
# pdfinfo (poppler-utils) is what reads it back.
command -v pdfinfo >/dev/null 2>&1 || { echo "pdfinfo (poppler-utils) is not on PATH" >&2; exit 2; }
if command -v timeout >/dev/null 2>&1; then TIMEOUT_CMD=timeout
elif command -v gtimeout >/dev/null 2>&1; then TIMEOUT_CMD=gtimeout
else TIMEOUT_CMD=""; echo "warning: no 'timeout' on PATH — hangs will not be bounded" >&2; fi

export CLI OUT TIMEOUT_CMD
export TIMEOUT=${TIMEOUT:-300}

# #442: the same run_with_timeout shape structure-battery.sh and markdown-battery.sh use for
# every external call. Before this, only the megapdf-cli invocation below was bounded; the
# qpdf calls (on both the corpus input and our own freshly-written output) were not, which is
# exactly the unbounded-oracle-call shape #442 found in structure-battery.sh's pdftotext --
# a qpdf hang on a malformed input, or on a malformed output the engine produced, would stall
# a worker (and with the default --jobs 1, the whole battery) forever, with no counted outcome.
run_with_timeout() {
    if [ -n "$TIMEOUT_CMD" ]; then
        "$TIMEOUT_CMD" "$@"
        return $?
    fi
    shift
    "$@"
}
export -f run_with_timeout

mkdir -p "$OUT/scratch" "$OUT/lines"
LOG="$OUT/battery-pages.log"
SUMMARY="$OUT/summary-pages.txt"
rm -f "$OUT/lines"/* "$LOG"

# One operation: runs megapdf-cli, checks the output, prints one result token. Never the
# document's name: the scratch file is named by the path hash.
one_op() {   # <pdf> <id> <op> <expected-pages> <input-check: clean|damaged> <cli args...>
    local pdf=$1 id=$2 op=$3 expected=$4 input_check=$5
    shift 5
    local out="$OUT/scratch/$id-$op.pdf"
    local err rc
    err=$(run_with_timeout "$TIMEOUT" "$CLI" pages "$pdf" --out "$out" --quiet "$@" 2>&1)
    rc=$?
    rm -f "$OUT/scratch/.$id-$op.pdf."*.megapdf-tmp
    case $rc in
        0) ;;
        124) echo hung; rm -f "$out"; return ;;
        1) echo usage-error; rm -f "$out"; return ;;   # #445: a usage error, not a crash
        2) echo open-failed; return ;;
        3) echo protected; return ;;
        4) echo unsupported-security; return ;;
        7) echo write-failed; rm -f "$out"; return ;;
        8) echo restricted; return ;;
        9) case "$err" in
               *"hierarchy"*) echo refused-fields ;;
               *)
                   # #445 point 4: a refusal the contract does not name (not the /Parent-
                   # hierarchy case) is only a genuine problem when the input itself was
                   # not already damaged -- refusing to touch a page that cannot be loaded
                   # in a document that already fails qpdf --check is correct behaviour,
                   # not a harness failure.
                   if [ "$input_check" = "damaged" ]; then
                       echo "refused-damaged-input"
                   else
                       echo "refused"
                   fi
                   ;;
           esac
           return ;;
        *) echo "crashed:$rc"; rm -f "$out"; return ;;
    esac
    run_with_timeout "$TIMEOUT" qpdf --requires-password "$out" >/dev/null 2>&1
    local rp_rc=$?
    if [ "$rp_rc" -eq 124 ]; then echo qpdf-timeout; rm -f "$out"; return; fi
    if [ "$rp_rc" -eq 0 ]; then
        # The document opened without a password and was saved with its security kept; qpdf
        # cannot look inside without one. Counted as checked by the read-back only.
        echo ok-unchecked; rm -f "$out"; return
    fi
    local pages
    pages=$(run_with_timeout "$TIMEOUT" qpdf --show-npages "$out" 2>/dev/null)
    if [ $? -eq 124 ]; then echo qpdf-timeout; rm -f "$out"; return; fi
    # #445 point 2: an empty $expected means the input's own page count could not be
    # established (run_one's rotate probe below, or importpair's own other-document probe)
    # -- there is no real expectation to compare against, so the comparison is skipped
    # rather than made against a placeholder 0.
    if [ -n "$expected" ] && [ "$pages" != "$expected" ]; then
        echo "count-mismatch:${pages:-?}/$expected"; rm -f "$out"; return
    fi
    run_with_timeout "$TIMEOUT" qpdf --check "$out" >/dev/null 2>&1
    local q=$?
    rm -f "$out"
    case $q in
        0) echo ok ;;
        3) echo ok-warn ;;
        124) echo qpdf-timeout ;;
        *)
            # #445 point 3: the output failing qpdf --check only means something when the
            # input passed it. Carrying an existing, undamaged-by-us stream error through an
            # operation megapdf-cli was never asked to repair is the honest outcome, not a
            # defect -- reported separately (qpdf-carried-damage) and not gated.
            if [ "$input_check" = "damaged" ]; then
                echo "qpdf-carried-damage:$q"
            else
                echo "qpdf-failed:$q"
            fi
            ;;
    esac
}

# #567: the size a blank page takes when appended after page <page> of <pdf> -- "<w> <h>"
# in whole points (>= 1 each), or nothing at all when pdfinfo cannot say. pdfinfo's own
# "Page size" line is already adjusted for that page's /Rotate (the same visual width and
# height MegaPDF's own page list uses), e.g. "Page    3 size:  612 x 792 pts (letter)" or
# "...(rotated 90 degrees)" -- the trailing annotation, when there is one, is deliberately
# not part of the match below. Rounded to the nearest whole point because --blank takes
# whole points only (core/cli/megapdf_cli.cpp), and floored at 1 rather than rejected: a
# degenerate box should still reach the CLI's own "expected <at>:<w>x<h> in whole points"
# usage check, not silently skip the operation.
page_size_pts() {   # <pdf> <page-1based>
    local pdf=$1 page=$2 line w h
    line=$(run_with_timeout "$TIMEOUT" pdfinfo -f "$page" -l "$page" "$pdf" 2>/dev/null \
           | sed -n 's/^Page *[0-9]* *size: *\([0-9][0-9.]*\) x \([0-9][0-9.]*\) pts.*/\1 \2/p')
    [ -n "$line" ] || return 1
    read -r w h <<<"$line"
    w=$(awk -v v="$w" 'BEGIN { n = int(v + 0.5); print (n < 1) ? 1 : n }' 2>/dev/null)
    h=$(awk -v v="$h" 'BEGIN { n = int(v + 0.5); print (n < 1) ? 1 : n }' 2>/dev/null)
    [ -n "$w" ] && [ -n "$h" ] || return 1
    printf '%s %s\n' "$w" "$h"
}

run_one() {   # <pdf> <paired-pdf>
    local pdf=$1 paired=$2
    local id
    id=$(printf '%s' "$pdf" | sha256sum | cut -c1-12)

    # #445: the input's own qpdf --check result, carried into every one_op call below (see
    # this script's header comment). Bounded like every other qpdf call (#442); a timeout
    # cannot prove the input clean, so it counts as damaged, same as an outright failure.
    run_with_timeout "$TIMEOUT" qpdf --check "$pdf" >/dev/null 2>&1
    local check_rc=$? input_check=damaged
    case "$check_rc" in
        0|3) input_check=clean ;;
    esac

    local n
    # #442: bounded like every other external call in this script now -- a qpdf hang counting
    # the corpus input's own pages used to be unbounded, and (unlike the calls in one_op,
    # which run on our own freshly-written output) this runs on an arbitrary, possibly
    # malformed, corpus document. A timeout here leaves $n empty, which the existing
    # "could not count the pages" branch below already handles the same as a password or an
    # unreadable file.
    n=$(run_with_timeout "$TIMEOUT" qpdf --show-npages "$pdf" 2>/dev/null)
    if ! [ "${n:-0}" -ge 1 ] 2>/dev/null; then
        # qpdf could not count the pages (a password, or a file it cannot read): let the
        # engine say what it is, through the rotate run alone. #445 point 2: no expected page
        # count is passed (empty, not the placeholder 0 this used to compare against) --
        # there is nothing real to compare the output's page count to.
        local r
        r=$(one_op "$pdf" "$id" rotate "" "$input_check" --rotate 1-:1)
        case "$r" in
            protected|unsupported-security|open-failed|hung|crashed:*) echo "$id $r" >"$OUT/lines/$id"; return ;;
            *) echo "$id rotate=$r delete=skipped move=skipped extract=skipped blank=skipped importself=skipped importpair=skipped (pages unknown, input=$input_check)" >"$OUT/lines/$id"; return ;;
        esac
    fi
    local rotate delete move extract
    rotate=$(one_op "$pdf" "$id" rotate "$n" "$input_check" --rotate 1-:1)
    if [ "$n" -ge 2 ]; then
        delete=$(one_op "$pdf" "$id" delete $((n - 1)) "$input_check" --delete 1)
        move=$(one_op "$pdf" "$id" move "$n" "$input_check" --move "$n:1")
        extract=$(one_op "$pdf" "$id" extract 2 "$input_check" --extract "1,$n")
    else
        delete=skipped
        move=skipped
        extract=$(one_op "$pdf" "$id" extract 1 "$input_check" --extract 1)
    fi

    # #567: blank, appended after the last page, sized like it (see page_size_pts and the
    # header comment). dims-unknown is counted, not gated: the harness could not form a
    # request, which is not the engine refusing one.
    local blank bw bh
    read -r bw bh < <(page_size_pts "$pdf" "$n")
    if [ -n "$bw" ] && [ -n "$bh" ]; then
        blank=$(one_op "$pdf" "$id" blank $((n + 1)) "$input_check" --blank "$((n + 1)):${bw}x${bh}")
    else
        blank=dims-unknown
    fi

    # #567: import. importself's expected count is exact (2n, both sides already known).
    # importpair needs the OTHER document's own page count and its own qpdf --check result
    # -- see the header comment on why damage on that side must not be blamed on this
    # document's input_check, and why an unknown other-side count skips the comparison
    # (one_op's own #445 point 2 handling) rather than gating on a guess.
    local importself
    importself=$(one_op "$pdf" "$id" importself $((n * 2)) "$input_check" --import "${pdf}@$((n + 1))")

    local pair_check=damaged
    run_with_timeout "$TIMEOUT" qpdf --check "$paired" >/dev/null 2>&1
    case $? in 0|3) pair_check=clean ;; esac
    local combined_check=clean
    if [ "$input_check" = damaged ] || [ "$pair_check" = damaged ]; then combined_check=damaged; fi
    local pm pair_expected=""
    pm=$(run_with_timeout "$TIMEOUT" qpdf --show-npages "$paired" 2>/dev/null)
    if [ "${pm:-0}" -ge 1 ] 2>/dev/null; then pair_expected=$((n + pm)); fi
    local importpair
    importpair=$(one_op "$pdf" "$id" importpair "$pair_expected" "$combined_check" --import "${paired}@$((n + 1))")

    echo "$id pages=$n input=$input_check rotate=$rotate delete=$delete move=$move extract=$extract blank=$blank importself=$importself pairpages=${pm:--} importpair=$importpair" >"$OUT/lines/$id"
}
export -f one_op run_one page_size_pts

started=$(date +%s)
# #567: import needs a second document per call (the header comment above states the
# pairing rule and why). Read the whole (limited) file listing once, so every document can
# be handed the path of the partner half the listing further along, wrapping around --
# N == 0 produces no pairs, and N == 1 pairs the one document with itself, both safely (no
# division by a zero JOBS/N, no partial pair possible: exactly two paths go in per document).
mapfile -t FILES < <(find "$CORPUS" -type f -name '*.pdf' -print | sort)
if [ "$LIMIT" -gt 0 ]; then FILES=("${FILES[@]:0:$LIMIT}"); fi
N=${#FILES[@]}
HALF=$(( N / 2 ))
{
    for ((i = 0; i < N; i++)); do
        j=$(( (i + HALF) % N ))
        printf '%s\0%s\0' "${FILES[i]}" "${FILES[j]}"
    done
} | xargs -0 -n 2 -P "$JOBS" bash -c 'run_one "$0" "$1"'
cat "$OUT/lines"/* >"$LOG" 2>/dev/null
elapsed=$(( $(date +%s) - started ))

count() { grep -c -E -- "$1" "$LOG" 2>/dev/null || true; }
seen=$(grep -c "" "$LOG")
{
    echo "documents seen:        $seen"
    echo "protected:             $(count ' protected$') (need a password)"
    echo "unsupported security:  $(count ' unsupported-security$')"
    echo "did not open:          $(count ' open-failed$')"
    echo "input already damaged: $(count 'input=damaged') (failed its own qpdf --check before megapdf-cli touched it; #445 -- gates below only fire for a document that was clean going in)"
    echo "import-pair, other document's page count unknown (comparison skipped): $(count ' pairpages=-( |$)')"
    echo "seconds:               $elapsed"
    for op in rotate delete move extract blank importself importpair; do
        echo "$op:"
        echo "  ok:                  $(count "$op=ok( |$)")"
        echo "  ok, qpdf warnings:   $(count "$op=ok-warn")"
        echo "  ok, unchecked:       $(count "$op=ok-unchecked") (kept its security; qpdf cannot look inside)"
        echo "  skipped:             $(count "$op=skipped")"
        echo "  dims unknown:        $(count "$op=dims-unknown") (#567 -- pdfinfo could not size the page blank follows; harness could not form the request, not gated)"
        # #567: found while chasing an accounting gap in import's own per-op breakdown --
        # these three outcomes (a per-operation rc of 3, 4 or 2, exactly like the top-level
        # protected/unsupported-security/did-not-open counts above, but for the SECOND
        # document import opens) were always possible on any operation, including rotate,
        # whenever qpdf's own --show-npages tolerates a document megapdf-cli's own open does
        # not; they were simply never broken out per operation before, only folded silently
        # into "the rest" of a hand count. Not gated (rc 2/3/4 were never crashes); shown so
        # every opened document's outcome on every operation is now visible somewhere.
        echo "  protected:           $(count "$op=protected")"
        echo "  unsupported security: $(count "$op=unsupported-security")"
        echo "  did not open:        $(count "$op=open-failed")"
        echo "  restricted:          $(count "$op=restricted")"
        echo "  refused, fields:     $(count "$op=refused-fields")"
        echo "  refused, damaged input: $(count "$op=refused-damaged-input") (#445 point 4 -- not gated: the input already failed qpdf --check)"
        echo "  REFUSED, other:      $(count "$op=refused( |$)")"
        echo "  usage error:         $(count "$op=usage-error") (#445 point 1 -- exit 1, not a crash; e.g. an argument that cannot apply to this document)"
        echo "  WRITE FAILED:        $(count "$op=write-failed")"
        echo "  COUNT MISMATCH:      $(count "$op=count-mismatch")"
        echo "  QPDF FAILED:         $(count "$op=qpdf-failed")"
        echo "  qpdf, carried damage: $(count "$op=qpdf-carried-damage") (#445 point 3 -- not gated: the input already failed qpdf --check)"
        echo "  QPDF TIMEOUT:        $(count "$op=qpdf-timeout") (#442 -- qpdf itself did not finish within ${TIMEOUT}s)"
        echo "  CRASHED:             $(count "$op=crashed")"
        echo "  HUNG:                $(count "$op=hung")"
    done
    echo "crashed or hung before the page count was known: $(( $(count ' hung$') + $(count ' crashed:') ))"
} | tee "$SUMMARY"

# #445: usage-error, refused-damaged-input, qpdf-carried-damage and (#567) dims-unknown are
# counted and reported above but never gate the run -- each is either not a crash
# (usage-error), a document that was already broken before megapdf-cli touched it (the other
# two #445 outcomes), or the harness itself declining to form a request (dims-unknown).
# Everything else required to be 0 by this script's header comment still is, across every
# operation including blank and the two import calls: the patterns below are not anchored to
# an operation name, so a new op's own crash, hang, qpdf failure, count mismatch, write
# failure or undocumented refusal is caught the same way rotate/delete/move/extract's always
# have been.
bad=$(( $(count '=crashed') + $(count ' crashed:') + $(count '=hung') + $(count ' hung$') + $(count '=qpdf-failed') \
      + $(count '=qpdf-timeout') + $(count '=count-mismatch') + $(count '=write-failed') + $(count '=refused( |$)') ))
[ "$bad" -eq 0 ]
