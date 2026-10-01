#!/usr/bin/env bash
# Say what a battery's document count is a count OF (#609).
#
#   tools/stress/corpus_coverage.sh <corpus-dir> <documents-visited> [--manifest <path>]
#
# Every battery in this directory enumerates its corpus with `find -type f -name '*.pdf'`
# and prints how many documents it visited. For ten recorded runs that number was printed
# alone -- "public 1,381" -- and nothing in the summary said what it was 1,381 *of*. The
# public corpus's own manifest has 1,777 rows, so 396 of them had never been exercised by a
# local battery, two of the three missing groups had been added to the manifest precisely
# because they found defects, and #567's refusal path read as zero for two full runs partly
# because the documents that would have tripped it were not on the machine. A number is only
# as good as the population behind it; this script makes the battery state the population.
#
# When the corpus is the #434 public corpus -- recognised by the WHERE-THESE-CAME-FROM.txt
# marker fetch.sh leaves in it -- the battery's own count is reconciled against the manifest
# and the gap is broken down. A corpus with no manifest (the owner's private corpus, the
# Canadian set, the UN set) gets one line saying so rather than no line at all: an absent
# line reads as "nobody checked", which is the state this issue exists to end.
#
# Three numbers, deliberately separate, because the gaps between them are each a different
# kind of problem:
#
#   manifest rows            what the corpus is defined to be, from the committed manifest
#   rows present on disk     what was actually staged (a staging gap -- #609's own subject)
#   rows the *.pdf walk sees what the battery will measure (a tooling gap: the `.Pdf`
#                            case-mismatched qpdf fixture every run since the second has
#                            noted as "assumed / not verified" is exactly this, and it has
#                            only ever been visible to somebody who went looking)
#
# Stated exclusions: a corpus directory may hold EXCLUDED-FROM-THIS-CORPUS.tsv, one
# `<manifest source>` TAB `<reason>` line per deliberately absent source. Those rows are
# then reported as a decision rather than counted in the unexplained gap, which is #609's
# third point -- the 280 non-Latin rows are deferred by decision, so their absence should be
# a stated exclusion in the run's own record and not a silent hole.
#
# Nothing here gates, and that is on purpose. A partial corpus is a fact about the machine,
# not a regression in the code under test, and a battery that refused to run on 1,381 of
# 1,777 rows would simply stop being run -- which is how a reporting problem becomes a
# testing problem. It announces, in the summary, every time.
#
# Privacy: the only paths this can print are the corpus directory it was handed and the
# manifest's own `source` names, both public. It never prints a document path, from any
# corpus, so it is safe in a summary that gets pasted into an issue.
set -uo pipefail

HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
CORPUS=${1:?usage: corpus_coverage.sh <corpus-dir> <documents-visited> [--manifest <path>]}
VISITED=${2:?usage: corpus_coverage.sh <corpus-dir> <documents-visited> [--manifest <path>]}
shift 2
MANIFEST=""
while [ $# -gt 0 ]; do
    case "$1" in
        --manifest) MANIFEST=$2; shift 2 ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
done

# Auto-detection rather than a flag the caller has to remember: the whole failure #609
# records is that nobody remembered. A battery run by hand, with no extra argument, now
# reconciles the public corpus against its manifest because it is the public corpus.
if [ -z "$MANIFEST" ] && [ -f "$CORPUS/WHERE-THESE-CAME-FROM.txt" ] \
   && [ -f "$HERE/public-corpus/manifest.tsv" ]; then
    MANIFEST="$HERE/public-corpus/manifest.tsv"
fi

if [ -z "$MANIFEST" ] || [ ! -f "$MANIFEST" ]; then
    echo "--- #609: what this document count is a count of ---"
    echo "manifest:              none -- this corpus is not defined by a committed manifest,"
    echo "                       so $VISITED document(s) is the whole of what is on the"
    echo "                       machine and there is nothing to reconcile it against."
    exit 0
fi

EXCLUDES="$CORPUS/EXCLUDED-FROM-THIS-CORPUS.tsv"
WORK=$(mktemp -d) || exit 2
trap 'rm -rf "$WORK"' EXIT

# Two listings, relative to the corpus root, so a manifest `path` can be looked up directly
# in each. `find -printf '%P\n'` gives the path with the corpus root already stripped, which
# is safer than editing it off afterwards -- a corpus root with a regex metacharacter in it
# would survive this and not a sed.
find "$CORPUS" -type f -printf '%P\n' 2>/dev/null | sort > "$WORK/ondisk"
find "$CORPUS" -type f -name '*.pdf' -printf '%P\n' 2>/dev/null | sort > "$WORK/walk"

awk -F'\t' -v ondisk="$WORK/ondisk" -v walk="$WORK/walk" -v excl="$EXCLUDES" \
    -v visited="$VISITED" -v manifest="$MANIFEST" '
function commas(n,   s, out) {   # 1777 -> 1,777; a five-figure corpus is coming
    s = sprintf("%d", n); out = ""
    while (length(s) > 3) { out = "," substr(s, length(s) - 2) out; s = substr(s, 1, length(s) - 3) }
    return s out
}
BEGIN {
    while ((getline l < ondisk) > 0) have[l] = 1
    while ((getline l < walk) > 0) seen[l] = 1
    # An absent exclusions file is the normal case, not an error: getline returns -1 and the
    # loop never runs.
    while ((getline l < excl) > 0) {
        if (l ~ /^[ \t]*#/ || l ~ /^[ \t]*$/) continue
        split(l, e, "\t")
        gsub(/^[ \t]+|[ \t]+$/, "", e[1])
        if (e[1] == "") continue
        excluded[e[1]] = 1
        reason[e[1]] = (e[2] == "" ? "no reason given" : e[2])
    }
}
/^#/ { next }
!hdr { hdr = 1; next }                      # the manifest`s column-name row
NF < 7 { next }
{
    rows++
    src = $4; path = $7
    # Sources are reported in the order the manifest lists them, so two runs over the same
    # corpus print the same block. awk`s own `for (x in array)` order is unspecified and
    # mawk and gawk really do differ, which would make two identical runs look different.
    if (!(src in src_seen)) { src_seen[src] = 1; srcorder[++nsrc] = src }
    if (path in have) {
        present++
        if (path in seen) reachable++
        else { unreachable++; unreach_src[src]++ }
    } else if (src in excluded) {
        absent_stated++; stated_src[src]++
    } else {
        absent_unexplained++; unexplained_src[src]++
    }
    manifest_path[path] = 1
}
END {
    for (p in have) if (!(p in manifest_path)) extra++
    printf "--- #609: what this document count is a count of ---\n"
    printf "manifest:              %s\n", manifest
    printf "manifest rows:         %s\n", commas(rows)
    printf "rows present on disk:  %s\n", commas(present)
    printf "rows the *.pdf walk reaches: %s  <- the population every measure below is over\n", commas(reachable)
    if (unreachable > 0) {
        printf "  staged but not reached: %s -- find -name \"*.pdf\" does not match the name, so\n", commas(unreachable)
        printf "                        these rows are in NO number this run prints:\n"
        for (i = 1; i <= nsrc; i++) if (unreach_src[srcorder[i]] > 0)
            printf "                          %-16s %4d\n", srcorder[i], unreach_src[srcorder[i]]
    }
    printf "rows not on disk:      %s\n", commas(rows - present)
    if (absent_stated > 0) {
        printf "  stated exclusion:    %s -- absent by a recorded decision:\n", commas(absent_stated)
        for (i = 1; i <= nsrc; i++) if (stated_src[srcorder[i]] > 0)
            printf "                          %-16s %4d  %s\n", srcorder[i], stated_src[srcorder[i]],
                   reason[srcorder[i]]
    }
    if (absent_unexplained > 0) {
        printf "  NOT accounted for:   %s -- absent with no stated reason; either stage them\n", commas(absent_unexplained)
        printf "                        (fetch.sh --source <name>) or record the decision in\n"
        printf "                        <corpus>/EXCLUDED-FROM-THIS-CORPUS.tsv:\n"
        for (i = 1; i <= nsrc; i++) if (unexplained_src[srcorder[i]] > 0)
            printf "                          %-16s %4d\n", srcorder[i], unexplained_src[srcorder[i]]
    }
    if (extra > 0)
        printf "files on disk, not in the manifest: %s (a fetch marker, a kept *.part, local scratch)\n", commas(extra)
    pct = (rows > 0 ? 100 * reachable / rows : 0)
    verdict = (reachable == rows ? "COMPLETE" : \
               (absent_unexplained == 0 && unreachable == 0 ? "PARTIAL, every gap stated" : "PARTIAL"))
    printf "coverage:              %s of %s manifest rows (%.1f%%) -- %s\n",
           commas(reachable), commas(rows), pct, verdict
    # The battery counted for itself; if the two walks disagree something moved underneath
    # the run (or a --limit was passed), and saying so is cheaper than wondering later.
    if (visited + 0 == reachable) printf "documents visited:     %s (agrees)\n", commas(visited)
    else printf "documents visited:     %s -- DISAGREES with the walk above (a --limit, or the corpus changed mid-run)\n", commas(visited)
}
' "$MANIFEST"
