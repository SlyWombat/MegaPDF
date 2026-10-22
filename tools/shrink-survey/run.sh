#!/usr/bin/env bash
# The #6 shrink-for-email survey driver: runs megapdf_shrink_survey over a corpus, one document
# per process, and appends the JSON lines to <out-dir>/survey.jsonl.
#
#   tools/shrink-survey/run.sh <megapdf_shrink_survey> <corpus-root> <out-dir> [--jobs N] [--limit N]
#
# Conventions copied from tools/stress/structure-battery.sh: the file walk (sorted `find`), the
# document id (sha256 of the path, first 12 hex), the timeout helper, and the corpus-coverage block
# at the end. A document is bounded by SURVEY_TIMEOUT (default 300 s); a timeout or a crash becomes a
# {"type":"doc","outcome":"timeout"|"crashed"} line, never a silent gap, and its partial image lines
# are dropped so a bucket never counts half a document.
#
# Privacy: the corpora are personal and read-only. Nothing is written into them. The out-dir holds
# hashed ids only, plus index.tsv (id <TAB> path), which exists so a person can open a flagged
# document by hand; it stays on the machine and is never pasted, committed or uploaded. Delete the
# out-dir when you are done with it.
set -uo pipefail

STRESS_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/../stress" && pwd)

SURVEY=${1:?usage: run.sh <megapdf_shrink_survey> <corpus-root> <out-dir> [--jobs N] [--limit N]}
CORPUS=${2:?}
OUT=${3:?}
shift 3

JOBS=4
LIMIT=0
while [ $# -gt 0 ]; do
    case "$1" in
        --jobs) JOBS=$2; shift 2 ;;
        --limit) LIMIT=$2; shift 2 ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
done
[ -x "$SURVEY" ] || { echo "$SURVEY is not an executable file" >&2; exit 2; }
[ -d "$CORPUS" ] || { echo "$CORPUS is not a directory" >&2; exit 2; }

TIMEOUT=${SURVEY_TIMEOUT:-300}
if command -v timeout >/dev/null 2>&1; then TIMEOUT_CMD=timeout
elif command -v gtimeout >/dev/null 2>&1; then TIMEOUT_CMD=gtimeout
else
    TIMEOUT_CMD=""
    echo "warning: no 'timeout' or 'gtimeout' on PATH -- hangs will not be bounded." >&2
fi
run_with_timeout() {  # <seconds> <command...> -- returns 124 on our own timeout
    if [ -n "$TIMEOUT_CMD" ]; then "$TIMEOUT_CMD" "$@"; return $?; fi
    shift
    "$@"
}

mkdir -p "$OUT/scratch"
: >"$OUT/survey.jsonl"
: >"$OUT/index.tsv"
find "$CORPUS" -type f -name '*.pdf' -print | sort >"$OUT/scratch/files.txt"
if [ "$LIMIT" -gt 0 ]; then head -n "$LIMIT" "$OUT/scratch/files.txt" >"$OUT/scratch/files.limited" && mv "$OUT/scratch/files.limited" "$OUT/scratch/files.txt"; fi
VISITED=$(wc -l <"$OUT/scratch/files.txt")

one() {  # <pdf> -- writes $OUT/scratch/<id>.jsonl
    local pdf=$1 id rc
    id=$(printf '%s' "$pdf" | sha256sum | cut -c1-12)
    printf '%s\t%s\n' "$id" "$pdf" >"$OUT/scratch/$id.idx"
    run_with_timeout "$TIMEOUT" "$SURVEY" "$pdf" "$id" >"$OUT/scratch/$id.raw" 2>"$OUT/scratch/$id.err"
    rc=$?
    if [ "$rc" -eq 0 ] && grep -q '"type":"doc"' "$OUT/scratch/$id.raw"; then
        mv "$OUT/scratch/$id.raw" "$OUT/scratch/$id.jsonl"
    else
        local outcome=crashed
        [ "$rc" -eq 124 ] && outcome=timeout
        printf '{"type":"doc","doc":"%s","outcome":"%s","rc":%d}\n' "$id" "$outcome" "$rc" >"$OUT/scratch/$id.jsonl"
        rm -f "$OUT/scratch/$id.raw"
    fi
    rm -f "$OUT/scratch/$id.err"
}
export -f one run_with_timeout
export SURVEY OUT TIMEOUT TIMEOUT_CMD

started=$(date +%s)
tr '\n' '\0' <"$OUT/scratch/files.txt" | xargs -0 -P "$JOBS" -I{} bash -c 'one "$1"' _ {}

# Assemble in walk order so a rerun is diffable.
while IFS= read -r pdf; do
    id=$(printf '%s' "$pdf" | sha256sum | cut -c1-12)
    cat "$OUT/scratch/$id.jsonl" >>"$OUT/survey.jsonl" 2>/dev/null
    cat "$OUT/scratch/$id.idx" >>"$OUT/index.tsv" 2>/dev/null
done <"$OUT/scratch/files.txt"
rm -rf "$OUT/scratch"
elapsed=$(( $(date +%s) - started ))

echo "documents visited: $VISITED, $(grep -c '"type":"doc"' "$OUT/survey.jsonl") with a result line, in ${elapsed}s, $JOBS jobs"
echo "outcomes:"
grep '"type":"doc"' "$OUT/survey.jsonl" | grep -o '"outcome":"[a-z_]*"' | sort | uniq -c | sort -rn
echo
# Every battery says what its document count is a count of, and names its exclusions (#609).
bash "$STRESS_DIR/corpus_coverage.sh" "$CORPUS" "$VISITED"
