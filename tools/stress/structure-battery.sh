#!/usr/bin/env bash
# The #354 corpus battery for contract 9 (#353, megapdf_structure_*): runs
# megapdf_structure_check over a corpus and gates on the four measures from the 2026-09-24
# design comment on #142, section 7.
#
#   tools/stress/structure-battery.sh <megapdf_structure_check> <corpus-dir> <out-dir>
#       [--limit N] [--seed N] [--dump <dir>] [--reference] [--census] [--ms-budget MS]
#
# Required of a run: 0 crashes, 0 hangs, aggregate token-fidelity F1 >= 0.999. Order
# agreement and poppler agreement are reported but only gate when the corpus has enough
# tagged pages (see below); ms/page only gates when --ms-budget is given (no validated
# cross-machine baseline for this workload ships with #354 — see structure-battery's own
# summary for what number, if any, was used and where it came from).
#
# --census switches to the lighter `megapdf_structure_check census` pass (#354 deliverable
# 4): tagged/textless/multi-column/many-cut page counts only, no fidelity or order measures,
# for a fast first sweep to size the tagged-page population.
#
# --reference additionally runs `pdftotext -layout` (poppler, already the independent oracle
# in core-tests.yml:148) per document and hands its output to --reference, for measure 3.
#
# --dump <dir> marks <dir> with a PRIVATE file and has megapdf_structure_check write each
# document's extracted text there, keyed by the same path hash the log uses — never a real
# file name. Nothing under <dir> or <out-dir> is committed, uploaded or pasted: the corpus is
# personal (#151, #173, #354).
#
# --seed is accepted for parity with redaction-battery.sh's option shape; this battery visits
# every document deterministically (sorted find order) and does not sample within a document,
# so it is otherwise unused today.
set -uo pipefail

CHECK=${1:?usage: structure-battery.sh <megapdf_structure_check> <corpus> <out-dir> [options]}
CORPUS=${2:?}
OUT=${3:?}
shift 3

LIMIT=0
SEED=1
DUMP=""
REFERENCE=0
CENSUS=0
MS_BUDGET=0
TIMEOUT=${TIMEOUT:-120}
while [ $# -gt 0 ]; do
    case "$1" in
        --limit) LIMIT=$2; shift 2 ;;
        --seed) SEED=$2; shift 2 ;;
        --dump) DUMP=$2; shift 2 ;;
        --reference) REFERENCE=1; shift ;;
        --census) CENSUS=1; shift ;;
        --ms-budget) MS_BUDGET=$2; shift 2 ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
done

# macOS ships no `timeout` (it is GNU coreutils); Homebrew's is `gtimeout` unless coreutils'
# gnubin is on PATH. Without either, hangs cannot be bounded — the run still produces
# numbers, but a hang no longer gets a TIMEOUT line, it gets no line at all and the run never
# finishes, which is worse. Warn once and fall back to running unbounded rather than fail
# outright, since a document that never hung on any run so far is more likely on a fresh
# machine than "no timeout binary" is worth aborting for.
if command -v timeout >/dev/null 2>&1; then
    TIMEOUT_CMD=timeout
elif command -v gtimeout >/dev/null 2>&1; then
    TIMEOUT_CMD=gtimeout
else
    TIMEOUT_CMD=""
    echo "warning: no 'timeout' or 'gtimeout' on PATH — hangs will not be bounded. On macOS: brew install coreutils." >&2
fi
run_with_timeout() {  # <seconds> <command...> — echoes output, returns 124 on our own timeout
    if [ -n "$TIMEOUT_CMD" ]; then
        "$TIMEOUT_CMD" "$@"
        return $?
    fi
    shift  # drop the seconds argument; nothing enforces it without a timeout binary
    "$@"
}

mkdir -p "$OUT/scratch"
[ -n "$DUMP" ] && mkdir -p "$DUMP"
MODE=check
[ "$CENSUS" -eq 1 ] && MODE=census
LOG="$OUT/battery-$MODE.log"
SUMMARY="$OUT/summary.txt"
TAU_TREE_FILE="$OUT/scratch/tau_tree.txt"
TAU_REF_FILE="$OUT/scratch/tau_ref.txt"
MS_FILE="$OUT/scratch/ms_per_page.txt"
: >"$LOG"
: >"$TAU_TREE_FILE"
: >"$TAU_REF_FILE"
: >"$MS_FILE"

opened=0; ok=0; encrypted=0; format=0; crashed=0; hung=0
sum_pages=0; sum_tagged=0; sum_textless=0; sum_multicol=0; sum_manycut=0
sum_fid_matched=0; sum_fid_a=0; sum_fid_b=0; sum_fid_low09=0
max_rss=0
started=$(date +%s)

field() {  # <line> <key> -> value, "" if absent
    printf '%s\n' "$1" | grep -o "$2=[^ ]*" | head -1 | cut -d= -f2
}

while IFS= read -r pdf; do
    [ -e "$pdf" ] || continue
    opened=$((opened + 1))
    [ "$LIMIT" -gt 0 ] && [ "$opened" -gt "$LIMIT" ] && break
    # The corpus is personal: a document is identified in the log by a hash of its path.
    id=$(printf '%s' "$pdf" | sha256sum | cut -c1-12)

    refarg=()
    if [ "$REFERENCE" -eq 1 ] && [ "$CENSUS" -eq 0 ]; then
        reffile="$OUT/scratch/ref-$id.txt"
        pdftotext -layout "$pdf" "$reffile" >/dev/null 2>&1
        [ -s "$reffile" ] && refarg=(--reference "$reffile")
    fi
    dumparg=()
    [ -n "$DUMP" ] && [ "$CENSUS" -eq 0 ] && dumparg=(--dump "$DUMP" --dump-id "$id")

    if [ "$CENSUS" -eq 1 ]; then
        out=$(run_with_timeout "$TIMEOUT" "$CHECK" census "$pdf" 2>&1)
    else
        out=$(run_with_timeout "$TIMEOUT" "$CHECK" check "$pdf" "${dumparg[@]}" "${refarg[@]}" 2>&1)
    fi
    rc=$?
    [ -n "${reffile:-}" ] && rm -f "$reffile"

    if [ $rc -eq 124 ]; then
        hung=$((hung + 1))
        echo "$id hung" >>"$LOG"
        continue
    fi
    if [ $rc -ne 0 ]; then
        crashed=$((crashed + 1))
        echo "$id crashed rc=$rc" >>"$LOG"
        continue
    fi
    line=$(printf '%s' "$out" | grep -o 'result=[^ ]*.*' | head -1)
    echo "$id $line" >>"$LOG"
    result=$(field "$line" result)
    case "$result" in
        encrypted) encrypted=$((encrypted + 1)) ;;
        format) format=$((format + 1)) ;;
        ok)
            ok=$((ok + 1))
            pages=$(field "$line" pages); sum_pages=$((sum_pages + ${pages:-0}))
            sum_tagged=$((sum_tagged + $(field "$line" tagged 2>/dev/null || echo 0)))
            sum_textless=$((sum_textless + $(field "$line" textless 2>/dev/null || echo 0)))
            sum_multicol=$((sum_multicol + $(field "$line" multicol 2>/dev/null || echo 0)))
            sum_manycut=$((sum_manycut + $(field "$line" manycut 2>/dev/null || echo 0)))
            if [ "$CENSUS" -eq 0 ]; then
                sum_fid_matched=$((sum_fid_matched + $(field "$line" fid_match 2>/dev/null || echo 0)))
                sum_fid_a=$((sum_fid_a + $(field "$line" fid_a 2>/dev/null || echo 0)))
                sum_fid_b=$((sum_fid_b + $(field "$line" fid_b 2>/dev/null || echo 0)))
                sum_fid_low09=$((sum_fid_low09 + $(field "$line" fid_low09 2>/dev/null || echo 0)))
                rss=$(field "$line" rss_kb); [ -n "$rss" ] && [ "$rss" -gt "$max_rss" ] && max_rss=$rss
                msv=$(field "$line" ms_per_page); [ -n "$msv" ] && echo "$msv" >>"$MS_FILE"
                tt=$(field "$line" tau_tree); [ -n "$tt" ] && printf '%s\n' "${tt//,/$'\n'}" >>"$TAU_TREE_FILE"
                tr=$(field "$line" tau_ref); [ -n "$tr" ] && printf '%s\n' "${tr//,/$'\n'}" >>"$TAU_REF_FILE"
            fi
            ;;
        *) crashed=$((crashed + 1)) ;;
    esac
done < <(find "$CORPUS" -type f -name '*.pdf' -print | sort)

elapsed=$(( $(date +%s) - started ))

median() {  # <file of numbers, one per line> -> median, or "n/a"
    [ -s "$1" ] || { echo "n/a"; return; }
    sort -n "$1" | awk '{a[NR]=$1} END{if(NR==0){print "n/a"} else if(NR%2==1){print a[(NR+1)/2]} else {print (a[NR/2]+a[NR/2+1])/2}}'
}
pctl() {  # <file> <p 0-100> -> value at that percentile, or "n/a"
    [ -s "$1" ] || { echo "n/a"; return; }
    sort -n "$1" | awk -v p="$2" '{a[NR]=$1} END{if(NR==0){print "n/a"; exit} k=int((NR-1)*p/100)+1; if(k>NR)k=NR; print a[k]}'
}

agg_f1="n/a"
if [ "$CENSUS" -eq 0 ] && [ $((sum_fid_a + sum_fid_b)) -gt 0 ]; then
    agg_f1=$(awk -v m="$sum_fid_matched" -v a="$sum_fid_a" -v b="$sum_fid_b" 'BEGIN{printf "%.6f", 2*m/(a+b)}')
fi
tau_tree_median=$(median "$TAU_TREE_FILE")
tau_ref_median=$(median "$TAU_REF_FILE")
tau_tree_n=$(wc -l <"$TAU_TREE_FILE" | tr -d ' ')
tau_ref_n=$(wc -l <"$TAU_REF_FILE" | tr -d ' ')
ms_p50=$(pctl "$MS_FILE" 50)
ms_p95=$(pctl "$MS_FILE" 95)
ms_p99=$(pctl "$MS_FILE" 99)

# tau values are stored x1000 (integers); a human-readable median divides back down.
tau_tree_median_h="n/a"; [ "$tau_tree_median" != "n/a" ] && tau_tree_median_h=$(awk -v v="$tau_tree_median" 'BEGIN{printf "%.3f", v/1000}')
tau_ref_median_h="n/a"; [ "$tau_ref_median" != "n/a" ] && tau_ref_median_h=$(awk -v v="$tau_ref_median" 'BEGIN{printf "%.3f", v/1000}')

# Gate 2 (order agreement) is informational unless the census found ~200+ tagged pages
# across the whole corpus (design #142 §7's own threshold); with fewer, a single run's
# median cannot be trusted to mean anything.
order_gates=0
[ "$sum_tagged" -ge 200 ] && order_gates=1

{
    echo "mode:                 $MODE"
    echo "documents seen:       $opened"
    echo "opened ok:            $ok"
    echo "encrypted:            $encrypted"
    echo "format/unreadable:    $format"
    echo "crashed:              $crashed"
    echo "hung:                 $hung"
    echo "seconds:              $elapsed"
    echo "pages processed:      $sum_pages"
    echo "tagged pages:         $sum_tagged"
    echo "textless pages:       $sum_textless"
    echo "multi-column pages:   $sum_multicol (approximation: block left-edge clustering, not the core's own cut count — see structure_check.cpp)"
    echo "many-cut pages:       $sum_manycut (same approximation, >3 clusters)"
    if [ "$CENSUS" -eq 0 ]; then
        echo "--- measure 1: token fidelity ---"
        echo "aggregate F1:         $agg_f1 (gate: >= 0.999)"
        echo "pages F1 < 0.9:       $sum_fid_low09"
        echo "peak RSS (max, KB):   $max_rss"
        echo "ms/page p50/p95/p99:  $ms_p50 / $ms_p95 / $ms_p99"
        if [ "$MS_BUDGET" != "0" ]; then
            echo "ms/page budget:       $MS_BUDGET (gate: p95 <= budget)"
        else
            echo "ms/page budget:       not set (informational only; pass --ms-budget to gate)"
        fi
        echo "--- measure 2: order agreement (tagged pages, phase-1 proxy — see structure_check.cpp) ---"
        echo "tagged pages measured: $tau_tree_n"
        echo "tau median:            $tau_tree_median_h"
        echo "gates this run:        $([ "$order_gates" -eq 1 ] && echo yes || echo "no (informational: corpus has fewer than ~200 tagged pages)")"
        echo "--- measure 3: agreement with pdftotext -layout (poppler, informational only) ---"
        echo "pages measured:        $tau_ref_n"
        echo "tau median:            $tau_ref_median_h"
    fi
} | tee "$SUMMARY"

# Exit 0 only when every gate that applies holds.
gate_fidelity=0
if [ "$CENSUS" -eq 0 ]; then
    if [ "$agg_f1" != "n/a" ]; then
        awk -v f="$agg_f1" 'BEGIN{exit !(f>=0.999)}'
        gate_fidelity=$?
    else
        gate_fidelity=1   # no documents opened ok: nothing to measure, treat as a failed gate
    fi
fi
# Both default to "passes" (0): each only gates when it applies (informational otherwise),
# and neither applies at all in --census mode.
gate_order=0
if [ "$CENSUS" -eq 0 ] && [ "$order_gates" -eq 1 ]; then
    awk -v v="$tau_tree_median_h" 'BEGIN{exit !(v>=0.9)}'
    gate_order=$?
fi
gate_ms=0
if [ "$CENSUS" -eq 0 ] && [ "$MS_BUDGET" != "0" ] && [ "$ms_p95" != "n/a" ]; then
    awk -v v="$ms_p95" -v b="$MS_BUDGET" 'BEGIN{exit !(v<=b)}'
    gate_ms=$?
fi

[ "$crashed" -eq 0 ] && [ "$hung" -eq 0 ] && [ "$gate_fidelity" -eq 0 ] && \
    [ "$gate_order" -eq 0 ] && [ "$gate_ms" -eq 0 ]
