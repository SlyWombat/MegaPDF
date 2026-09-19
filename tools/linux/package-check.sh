#!/usr/bin/env bash
# What every MegaPDF Linux package must be true of, whatever its format (#158).
#
#     tools/linux/package-check.sh <app-root> <fixtures-dir> [notices-file] [label] [install-kind]
#
# <app-root> is the directory the package installs the tree under — /app/lib/megapdf
# inside a Flatpak, /opt/MegaPDF from the .deb, <out>/MegaPDF/bin from an unpacked
# build. It must contain the apphost and the two native libraries.
#
# <notices-file> defaults to the copy beside the binary, which is where every package
# must have one: /usr/share/doc is thrown away by the dpkg path-excludes that every
# Debian and Ubuntu container image ships, and a licence that requires the text to
# travel with the binary is not satisfied by a copy in a directory the packaging system
# is configured to discard. Named rather than searched for, because a search that
# misses reports a package as missing notices it has — the kind of failure that gets
# the check switched off.
#
# The checks are the ones a broken package passes silently:
#
#   * the third-party notices are in it. Several of the licences require the text to
#     travel with the binary and no channel accepts a package without it (#194). The
#     Mac build asserts the same thing, which is why that one cannot ship without them.
#   * the engine is the app's own. libmegapdf_core.so must carry a $ORIGIN runpath, or
#     a machine with some other libpdfium on its library path gets an engine without
#     the MegaPDF patches — a different renderer, silently.
#   * the native libraries are beside the apphost rather than waiting to be unpacked
#     into a temp directory on first run, which fails outright where /tmp is noexec.
#   * the app runs: the engine loads, a page renders, the locale chain is read, and
#     the real view model fills, checks, signs, saves and reopens a document.
#
# Nothing here needs a display. Everything here has failed at least once.
set -uo pipefail

USAGE='usage: package-check.sh <app-root> <fixtures-dir> [notices-file] [label] [install-kind]'
ROOT=${1:?$USAGE}
FIXTURES=${2:?$USAGE}
NOTICES=${3:-$ROOT/THIRD-PARTY-NOTICES.txt}
LABEL=${4:-package}
# What the app must say it is: the About window tells a person where updates come from
# by this, so a .deb that thinks it is a tarball sends them to the wrong place (#158).
EXPECTED_KIND=${5:-}

failures=0
ok()   { printf '  ok    %s\n' "$1"; }
fail() { printf '  FAIL  %s\n' "$1"; failures=$((failures + 1)); }

echo "== $LABEL: $ROOT"

# --- what must be in the package ------------------------------------------------

for f in MegaPDF libmegapdf_core.so libpdfium.so; do
    if [ -f "$ROOT/$f" ]; then ok "$f is in the package"
    else fail "$f is missing from the package"; fi
done
[ -x "$ROOT/MegaPDF" ] || fail "the apphost is not executable"

if [ -s "$NOTICES" ]; then
    # A file of the right name with nothing in it would pass a test for its presence,
    # and so would the wrong file.
    if grep -q 'PDFium' "$NOTICES" && grep -q 'Avalonia' "$NOTICES"; then
        ok "third-party notices are in the package ($(wc -l <"$NOTICES") lines)"
    else
        fail "$NOTICES does not name PDFium and Avalonia — wrong file?"
    fi
else
    fail "$NOTICES is missing from the package (#194)"
fi

# --- the engine is ours ---------------------------------------------------------

runpath=$(objdump -p "$ROOT/libmegapdf_core.so" 2>/dev/null | awk '/R(UN)?PATH/ {print $2; exit}')
if [ "$runpath" = '$ORIGIN' ]; then
    ok "libmegapdf_core.so has exactly one runpath entry, \$ORIGIN"
elif [ -z "$runpath" ]; then
    fail "libmegapdf_core.so has no runpath at all — it would load a system libpdfium"
else
    # Not just "does it contain $ORIGIN". A build machine's own directory, appended by
    # CMake and shipped, would satisfy that and still load the wrong engine on a machine
    # where the path happens to exist (#158).
    fail "libmegapdf_core.so has more in its runpath than \$ORIGIN: $runpath"
fi

# A single-file publish that self-extracts would have no .so beside the apphost at
# all; these being real files is the check that it did not.
for lib in libmegapdf_core.so libpdfium.so; do
    if [ -s "$ROOT/$lib" ]; then ok "$lib is a real file beside the apphost, not self-extracted"
    else fail "$lib is not beside the apphost"; fi
done

# --- it runs --------------------------------------------------------------------

run() {  # <description> <args...>
    local what=$1; shift
    local output
    if output=$("$ROOT/MegaPDF" "$@" 2>&1); then
        ok "$what"
    else
        fail "$what (exit $?)"
        printf '%s\n' "$output" | tail -5 | sed 's/^/        /'
    fi
}

run "--render-check: the engine loads and a page renders" --render-check "$FIXTURES/stamped.pdf"
run "--language-check: the POSIX locale chain is read"    --language-check
if [ "${NO_PRINT_CLIENT:-0}" = 1 ]; then
    # An install without recommends (check-apt-repo.sh's +norecs image) has no
    # cups-client: printing is a Recommends, not a Depends, so a machine that doesn't
    # print isn't made to install CUPS. What has to hold then is that the app knows,
    # and says what to install, rather than failing somewhere later (#316).
    if pc=$("$ROOT/MegaPDF" --print-check "$FIXTURES/fixture.pdf" 2>&1); then
        fail "--print-check passed although lp is not installed"
    elif printf '%s\n' "$pc" | grep -q 'lp is not on PATH'; then
        ok "--print-check: with no cups-client, the app says lp is missing and what to install"
    else
        fail "--print-check failed for another reason"; printf '%s\n' "$pc" | tail -5 | sed 's/^/        /'
    fi
else
    run "--print-check: the CUPS route is sound"          --print-check "$FIXTURES/fixture.pdf"
fi
run "--self-test: fill, check, sign, save, reopen"        --self-test "$FIXTURES"

if [ -n "$EXPECTED_KIND" ]; then
    kind=$("$ROOT/MegaPDF" --install-kind 2>/dev/null | sed -n 's/^install-kind: //p')
    if [ "$kind" = "$EXPECTED_KIND" ]; then ok "--install-kind: $kind, so About says where updates come from"
    else fail "--install-kind says '${kind:-nothing}', expected $EXPECTED_KIND"; fi
fi

echo
if [ "$failures" -eq 0 ]; then
    echo "$LABEL: all checks passed"
else
    echo "::error::$LABEL: $failures check(s) failed"
fi
exit "$failures"
