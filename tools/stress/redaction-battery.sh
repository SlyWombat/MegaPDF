#!/usr/bin/env bash
# The #173 corpus battery: apply a redaction to a random text area and a random image area
# per page, across a whole corpus, and check the result.
#
#   tools/stress/redaction-battery.sh <leakcheck-binary> <corpus-dir> <out-dir> [--pages N]
#                                     [--seed N] [--baseline] [--limit N]
#
# Required of a run: 0 leaks, 0 crashes, 0 hangs, and no render change outside the areas.
# Every refusal is counted by reason and has to be explainable. `--baseline` applies no
# redaction and times the open, render and save alone, so a battery run can be compared
# with one.
#
# The corpus is personal (#151, #173). Nothing about a document leaves <out-dir>: the log
# holds a per-document line keyed by a hash of its path, never its name, and the summary
# holds counts and timings only. Do not commit, upload or paste <out-dir>.
set -uo pipefail

LEAKCHECK=${1:?usage: redaction-battery.sh <leakcheck> <corpus> <out-dir> [options]}
CORPUS=${2:?}
OUT=${3:?}
shift 3

PAGES=4
SEED=1
LIMIT=0
MODE=redact
TIMEOUT=${TIMEOUT:-120}
while [ $# -gt 0 ]; do
    case "$1" in
        --pages) PAGES=$2; shift 2 ;;
        --seed) SEED=$2; shift 2 ;;
        --limit) LIMIT=$2; shift 2 ;;
        --baseline) MODE=baseline; shift ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
done

mkdir -p "$OUT/scratch"
LOG="$OUT/battery-$MODE.log"
SUMMARY="$OUT/summary-$MODE.txt"
: >"$LOG"

ok=0; refused=0; leaked=0; crashed=0; hung=0; opened=0; changed=0
declare -A reasons
started=$(date +%s)

while IFS= read -r pdf; do
    [ -e "$pdf" ] || continue
    opened=$((opened + 1))
    [ "$LIMIT" -gt 0 ] && [ "$opened" -gt "$LIMIT" ] && break
    # The corpus is personal: a document is identified in the log by a hash of its path.
    id=$(printf '%s' "$pdf" | sha256sum | cut -c1-12)
    out=$(timeout "$TIMEOUT" "$LEAKCHECK" battery "$pdf" "$OUT/scratch" --seed "$SEED" --pages "$PAGES" \
              $( [ "$MODE" = baseline ] && echo --baseline ) 2>&1)
    rc=$?
    if [ $rc -eq 124 ]; then
        hung=$((hung + 1))
        echo "$id hung" >>"$LOG"
        continue
    fi
    if [ $rc -gt 1 ] && [ $rc -ne 2 ]; then
        crashed=$((crashed + 1))
        echo "$id crashed rc=$rc" >>"$LOG"
        continue
    fi
    line=$(printf '%s' "$out" | grep -o 'result=[^ ]*.*' | head -1)
    echo "$id $line" >>"$LOG"
    case "$line" in
        result=open-failed*) ;;                      # not a redaction failure; the corpus has broken files
        result=refused*)
            refused=$((refused + 1))
            for r in $(printf '%s' "$out" | grep -o 'refusal: refused page [0-9]* ([a-z-]*)' | grep -o '([a-z-]*)' | tr -d '()'); do
                reasons[$r]=$(( ${reasons[$r]:-0} + 1 ))
            done ;;
        result=ok*)
            ok=$((ok + 1))
            leaks=$(printf '%s' "$line" | grep -o 'leaks=[0-9]*' | cut -d= -f2)
            outside=$(printf '%s' "$line" | grep -o 'outside_changed=[0-9]*' | cut -d= -f2)
            [ "${leaks:-0}" -gt 0 ] && leaked=$((leaked + 1))
            [ "${outside:-0}" -gt 0 ] && changed=$((changed + 1)) ;;
        result=baseline*) ok=$((ok + 1)) ;;
        *) crashed=$((crashed + 1)) ;;
    esac
done < <(find "$CORPUS" -type f -name '*.pdf' -print | sort)

elapsed=$(( $(date +%s) - started ))
{
    echo "mode:            $MODE"
    echo "documents seen:  $opened"
    echo "redacted:        $ok"
    echo "refused:         $refused"
    echo "leaked:          $leaked"
    echo "changed outside: $changed"
    echo "crashed:         $crashed"
    echo "hung:            $hung"
    echo "seconds:         $elapsed"
    echo "refusals by reason:"
    for r in "${!reasons[@]}"; do echo "  $r: ${reasons[$r]}"; done
} | tee "$SUMMARY"

[ "$leaked" -eq 0 ] && [ "$crashed" -eq 0 ] && [ "$hung" -eq 0 ] && [ "$changed" -eq 0 ]
