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
#     itself is a corpus that cannot tell you its source changed under you;
#   * a hash that no longer verifies costs the one document it belongs to, never the
#     rest of the run (#525) -- a re-rendering source (Wikipedia's PDF export is the
#     known case) moves its own bytes on its own schedule, and the final summary
#     counts exactly how many of the selected documents that happened to, by name.
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

if [ "$VERIFY_ONLY" -eq 0 ]; then
    mkdir -p "$DEST" || exit 2
    # The corpus directory says what it is, so a stray `git add` in the wrong place, or a
    # later reader wondering what these files are, has an answer sitting next to them.
    # Skipped in --verify-only (#470): that mode only reads, and a permanently staged
    # corpus is chmod'd read-only, so writing this marker on every check would fail --
    # harmlessly (set -u but not -e, so the script presses on), but noisily.
    cat > "$DEST/WHERE-THESE-CAME-FROM.txt" <<TXT
MegaPDF public test corpus (#434) -- fetched by tools/stress/public-corpus/fetch.sh
from tools/stress/public-corpus/manifest.tsv. Every file here is redistributable;
licences and attribution are in that directory's README.md. Nothing here is committed
to the MegaPDF repository and nothing here is the owner's private corpus.
Files under malformed/ are DELIBERATELY BROKEN and may trip anti-malware. Open them
with the engine under test, not with anything you care about.
TXT
fi

# ---- one document ----------------------------------------------------------------
#
# #525: every outcome below also appends one word to $RESULTS ("ok", "mismatch" or
# "failed") ahead of $rel, so the run can total what happened to every row it touched
# rather than collapsing the whole fetch to a single pass/fail bit. A mismatch (the
# hash the manifest pinned no longer matches what the source serves -- Wikipedia
# re-renders its PDF export, #525) refuses that one row: it costs the document, not
# the run. $RESULTS is opened with >>, and every write here is one line short enough
# to land in one write(2) -- the usual guarantee that keeps concurrent appends to the
# same file from interleaving mid-line, which is all that is asked of it, is a count
# read back afterwards, not a lock.
fetch_one() {
    local url=$1 want=$2 size=$3 rel=$4
    local out="$DEST/$rel"
    local dir; dir=$(dirname "$out")

    if [ -f "$out" ]; then
        local have; have=$(sha256sum "$out" | cut -d' ' -f1)
        if [ "$have" = "$want" ]; then
            echo "skip $rel"
            echo "ok $rel" >> "$RESULTS"
            return 0
        fi
        # Not repaired by re-fetching: an existing file with the wrong hash means either
        # the source moved or the local copy is damaged, and both are worth knowing.
        echo "MISMATCH-ON-DISK $rel have=$have want=$want" >&2
        echo "mismatch $rel" >> "$RESULTS"
        return 3
    fi

    mkdir -p "$dir" || return 1
    local tmp="$out.part.$$"
    local attempt delay=2
    for attempt in 1 2 3 4; do
        # --tls-max 1.2 (#434 federal-forms extension): www.uscis.gov's Akamai front end
        # answers a bare 403 to at least one curl build's default TLS 1.3 handshake --
        # verified on kdocker3, 2026-09-27, with no proxy, no IP block and a browser
        # User-Agent all ruled out first. Forcing TLS 1.2 clears it and was re-checked
        # against irs.gov and raw.githubusercontent.com too, so it is applied to every
        # fetch rather than singled out for one host.
        if curl -sS -L --fail --max-time 300 --connect-timeout 20 --tls-max 1.2 \
                -o "$tmp" "$url"; then
            local got; got=$(sha256sum "$tmp" | cut -d' ' -f1)
            if [ "$got" != "$want" ]; then
                rm -f "$tmp"
                echo "MISMATCH-ON-FETCH $rel have=$got want=$want url=$url" >&2
                echo "mismatch $rel" >> "$RESULTS"
                return 3
            fi
            local bytes; bytes=$(wc -c < "$tmp")
            if [ "$bytes" != "$size" ]; then
                rm -f "$tmp"
                echo "SIZE-MISMATCH $rel have=$bytes want=$size" >&2
                echo "mismatch $rel" >> "$RESULTS"
                return 3
            fi
            mv -f "$tmp" "$out" || return 1
            echo "got  $rel"
            echo "ok $rel" >> "$RESULTS"
            return 0
        fi
        rm -f "$tmp"
        [ "$attempt" -lt 4 ] && sleep "$delay" && delay=$((delay * 2))
    done
    echo "FETCH-FAILED $rel url=$url" >&2
    echo "failed $rel" >> "$RESULTS"
    return 1
}
export -f fetch_one
export DEST
RESULTS="$WORK/results.tsv"
: > "$RESULTS"
export RESULTS

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
for pid in "${pids[@]}"; do wait "$pid"; done

# #525: counted from $RESULTS, one word per row fetch_one actually touched, rather than
# the single pass/fail bit `wait`'s exit status would give -- so a maintainer reads
# "3 of 1777 documents" off the summary itself instead of scrolling back through
# MISMATCH/FETCH-FAILED lines to work out how much of the corpus is actually affected.
OK=$(grep -c '^ok ' "$RESULTS")
MISMATCH=$(grep -c '^mismatch ' "$RESULTS")
FAILED=$(grep -c '^failed ' "$RESULTS")

echo
echo "fetched or verified: $OK of $TOTAL"
if [ "$MISMATCH" -gt 0 ]; then
    echo "could not verify:    $MISMATCH of $TOTAL -- the pinned hash no longer matches what" >&2
    echo "                      the source serves (see the MISMATCH lines above). A" >&2
    echo "                      re-rendering source is the known cause (#525, wiki-*); each" >&2
    echo "                      such row is refused on its own -- it costs that document," >&2
    echo "                      not the rest of the corpus." >&2
fi
if [ "$FAILED" -gt 0 ]; then
    echo "failed to fetch:      $FAILED of $TOTAL -- see the FETCH-FAILED lines above." >&2
fi
if [ "$MISMATCH" -gt 0 ] || [ "$FAILED" -gt 0 ]; then
    echo "$DEST is missing $((MISMATCH + FAILED)) of $TOTAL documents named above; every" >&2
    echo "other row is complete. A mismatch is never re-fetched over: fix or remove the" >&2
    echo "file, then re-run." >&2
    exit 1
fi
echo "corpus complete at $DEST"
