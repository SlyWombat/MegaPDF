#!/usr/bin/env bash
# Fetch the #434 public corpus from manifest.tsv and prove every byte.
#
#   tools/stress/public-corpus/fetch.sh [<dest>] [--jobs N] [--jobs-per-host N]
#       [--category form,tagged,...] [--source verapdf,qpdf,pdfium]
#       [--verify-only] [--dry-run] [--manifest <path>]
#
# <dest> defaults to ~/megapdf-public-corpus -- OUTSIDE the repository, on purpose. The
# PDFs are never committed (#434): the manifest is what the repository holds, and the
# .gitignore entry covers the in-tree path for anyone who points --dest at one anyway.
#
# What it guarantees:
#   * every file that lands is checked against the sha256 in the manifest;
#   * a file already on disk with the right hash is left alone and not re-downloaded;
#   * a file on disk with the WRONG hash is a hard failure, named, and the run exits
#     non-zero -- it is never silently re-fetched over, because a corpus that heals
#     itself is a corpus that cannot tell you its source changed under you.
#
# Politeness: --jobs is the total worker count, --jobs-per-host caps how many of those
# hit any one host at once (default 2). govinfo.gov asks for rate limiting explicitly
# and the GitHub raw endpoint will start refusing a host that hammers it; two
# connections per host is the setting that has never been refused in testing. Raising it
# is a decision about someone else's server, so it is a flag rather than a default.
set -uo pipefail

HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
MANIFEST="$HERE/manifest.tsv"
DEST=""
JOBS=4
JOBS_PER_HOST=2
CATEGORIES=""
SOURCES=""
VERIFY_ONLY=0
DRY_RUN=0

while [ $# -gt 0 ]; do
    case "$1" in
        --jobs) JOBS=$2; shift 2 ;;
        --jobs-per-host) JOBS_PER_HOST=$2; shift 2 ;;
        --category) CATEGORIES=$2; shift 2 ;;
        --source) SOURCES=$2; shift 2 ;;
        --manifest) MANIFEST=$2; shift 2 ;;
        --verify-only) VERIFY_ONLY=1; shift ;;
        --dry-run) DRY_RUN=1; shift ;;
        -h|--help) sed -n '2,30p' "${BASH_SOURCE[0]}"; exit 0 ;;
        -*) echo "unknown option: $1" >&2; exit 2 ;;
        *) if [ -n "$DEST" ]; then echo "unexpected argument: $1" >&2; exit 2; fi
           DEST=$1; shift ;;
    esac
done

DEST=${DEST:-$HOME/megapdf-public-corpus}
[ -f "$MANIFEST" ] || { echo "no manifest at $MANIFEST" >&2; exit 2; }
command -v sha256sum >/dev/null || { echo "sha256sum not found" >&2; exit 2; }
command -v curl >/dev/null || { echo "curl not found" >&2; exit 2; }

WORK=$(mktemp -d) || exit 2
trap 'rm -rf "$WORK"' EXIT

# ---- select rows -----------------------------------------------------------------
# Columns: url sha256 bytes source licence category path
awk -v cats="$CATEGORIES" -v srcs="$SOURCES" '
    BEGIN { FS = OFS = "\t"
            n = split(cats, c, ","); for (i = 1; i <= n; i++) want_cat[c[i]] = 1
            m = split(srcs, s, ","); for (i = 1; i <= m; i++) want_src[s[i]] = 1 }
    /^#/ { next }
    !seen_header { seen_header = 1; next }
    NF < 7 { next }
    { if (cats != "" && !($6 in want_cat)) next
      if (srcs != "" && !($4 in want_src)) next
      print $1, $2, $3, $7 }
' "$MANIFEST" > "$WORK/rows.tsv"

TOTAL=$(wc -l < "$WORK/rows.tsv")
BYTES=$(awk -F'\t' '{s += $3} END {printf "%.1f", s / 1048576}' "$WORK/rows.tsv")
echo "manifest: $MANIFEST"
echo "selected: $TOTAL documents, $BYTES MB"
echo "dest:     $DEST"

if [ "$TOTAL" -eq 0 ]; then
    echo "nothing selected" >&2
    exit 2
fi
if [ "$DRY_RUN" -eq 1 ]; then
    awk -F'\t' '{print $4}' "$WORK/rows.tsv"
    exit 0
fi

mkdir -p "$DEST" || exit 2
# The corpus directory says what it is, so a stray `git add` in the wrong place, or a
# later reader wondering what these files are, has an answer sitting next to them.
cat > "$DEST/WHERE-THESE-CAME-FROM.txt" <<TXT
MegaPDF public test corpus (#434) -- fetched by tools/stress/public-corpus/fetch.sh
from tools/stress/public-corpus/manifest.tsv. Every file here is redistributable;
licences and attribution are in that directory's README.md. Nothing here is committed
to the MegaPDF repository and nothing here is the owner's private corpus.
Files under malformed/ are DELIBERATELY BROKEN and may trip anti-malware. Open them
with the engine under test, not with anything you care about.
TXT

# ---- one document ----------------------------------------------------------------
fetch_one() {
    local url=$1 want=$2 size=$3 rel=$4
    local out="$DEST/$rel"
    local dir; dir=$(dirname "$out")

    if [ -f "$out" ]; then
        local have; have=$(sha256sum "$out" | cut -d' ' -f1)
        if [ "$have" = "$want" ]; then
            echo "skip $rel"
            return 0
        fi
        # Not repaired by re-fetching: an existing file with the wrong hash means either
        # the source moved or the local copy is damaged, and both are worth knowing.
        echo "MISMATCH-ON-DISK $rel have=$have want=$want" >&2
        return 3
    fi

    mkdir -p "$dir" || return 1
    local tmp="$out.part.$$"
    local attempt delay=2
    for attempt in 1 2 3 4; do
        if curl -sS -L --fail --max-time 300 --connect-timeout 20 \
                -o "$tmp" "$url"; then
            local got; got=$(sha256sum "$tmp" | cut -d' ' -f1)
            if [ "$got" != "$want" ]; then
                rm -f "$tmp"
                echo "MISMATCH-ON-FETCH $rel have=$got want=$want url=$url" >&2
                return 3
            fi
            local bytes; bytes=$(wc -c < "$tmp")
            if [ "$bytes" != "$size" ]; then
                rm -f "$tmp"
                echo "SIZE-MISMATCH $rel have=$bytes want=$size" >&2
                return 3
            fi
            mv -f "$tmp" "$out" || return 1
            echo "got  $rel"
            return 0
        fi
        rm -f "$tmp"
        [ "$attempt" -lt 4 ] && sleep "$delay" && delay=$((delay * 2))
    done
    echo "FETCH-FAILED $rel url=$url" >&2
    return 1
}
export -f fetch_one
export DEST

if [ "$VERIFY_ONLY" -eq 1 ]; then
    # No network at all: check what is on disk against the manifest.
    rc=0
    missing=0 ok=0 bad=0
    while IFS=$'\t' read -r url want size rel; do
        if [ ! -f "$DEST/$rel" ]; then missing=$((missing + 1)); continue; fi
        have=$(sha256sum "$DEST/$rel" | cut -d' ' -f1)
        if [ "$have" = "$want" ]; then ok=$((ok + 1))
        else bad=$((bad + 1)); echo "MISMATCH $rel have=$have want=$want" >&2; rc=1; fi
    done < "$WORK/rows.tsv"
    echo "verified $ok, missing $missing, mismatched $bad"
    [ "$bad" -gt 0 ] && exit 1
    exit 0
fi

# ---- run, grouped by host so the per-host cap means something ---------------------
awk -F'\t' '{ split($1, u, "/"); print u[3] }' "$WORK/rows.tsv" | sort -u > "$WORK/hosts"
HOSTS=$(wc -l < "$WORK/hosts")
PER_HOST=$JOBS_PER_HOST
if [ "$HOSTS" -gt 0 ]; then
    # Never let the per-host cap multiply past the total worker budget.
    max_each=$(( JOBS / HOSTS )); [ "$max_each" -lt 1 ] && max_each=1
    [ "$PER_HOST" -gt "$max_each" ] && PER_HOST=$max_each
fi
echo "hosts:    $HOSTS, $PER_HOST connection(s) each"
echo

rc=0
pids=()
while read -r host; do
    # The line goes in as an ARGUMENT, not spliced into the script text: veraPDF's
    # paths carry spaces and parentheses, and an -I{} substitution would let a quote in
    # a path rewrite the command.
    ( grep -F "://$host/" "$WORK/rows.tsv" \
        | xargs -P "$PER_HOST" -d '\n' -I LINE bash -c '
              IFS=$'"'"'\t'"'"' read -r u w s r <<< "$1"
              fetch_one "$u" "$w" "$s" "$r"' _ LINE ) &
    pids+=($!)
done < "$WORK/hosts"
for pid in "${pids[@]}"; do wait "$pid" || rc=1; done

echo
if [ "$rc" -ne 0 ]; then
    echo "FETCH INCOMPLETE -- see the MISMATCH / FETCH-FAILED lines above." >&2
    echo "A mismatch is never re-fetched over: fix or remove the file, then re-run." >&2
    exit 1
fi
echo "corpus complete at $DEST"
