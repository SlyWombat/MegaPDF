#!/usr/bin/env bash
# The iOS file flows end to end, through the real Files picker (#146): redaction read back
# from outside the app, save, save a copy, set / refuse / remove a password, and a 1 GB file.
# Drives ios/MegaPDFUITests/FilesEndToEndUITests one test at a time and, between tests,
# reads what the app wrote out of the simulator's "On My iPhone" with qpdf and poppler —
# never PDFium, which is the engine that did the writing.
#
# Usage: tools/ios-files-e2e.sh [device-name] [out-dir]
#   device-name  default "iPhone 17 Pro Max"
#   out-dir      default artifacts/ios-files-e2e
# Env: BIG_PDF=<path>   the 1 GB fixture (tools/gen_large_fixtures.py big-1gb); without it
#                       the 1 GB test is left out.
#      SKIP_BUILD=1     reuse the build in $DERIVED_DATA (default ~/dd-ios-e2e).
#      ONLY="<test> …"  run only these tests.
#
# Needs Xcode, xcodegen, qpdf and poppler (brew install qpdf poppler), and Pillow.
#
# The staging trap this exists to get past: a simulator has THREE folders named "File
# Provider Storage" — Photos, iCloud Drive and the local "On My iPhone" — and which one
# `find` lists first is arbitrary. Copy a fixture into the wrong one and the picker never
# shows it. The right one is the app group group.com.apple.FileProvider.LocalStorage,
# found by the identifier in its container metadata, and it exists only once Files has
# run on that simulator.
set -euo pipefail
export PATH="/opt/homebrew/bin:$PATH"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEVICE="${1:-iPhone 17 Pro Max}"
OUT="${2:-$ROOT/artifacts/ios-files-e2e}"
DD="${DERIVED_DATA:-$HOME/dd-ios-e2e}"
mkdir -p "$OUT"
LOG="$OUT/run.log"
: > "$LOG"
say() { echo "$*" | tee -a "$LOG"; }

cd "$ROOT/ios"
if [ "${SKIP_BUILD:-0}" != 1 ]; then
    [ -d Vendor/pdfium.xcframework ] || bash scripts/fetch-pdfium.sh
    xcodegen generate >/dev/null
    xcodebuild build-for-testing -project MegaPDF.xcodeproj -scheme MegaPDFDemo \
        -destination "platform=iOS Simulator,name=$DEVICE" -derivedDataPath "$DD" \
        CODE_SIGNING_ALLOWED=NO -quiet 2>&1 | grep -v "ld: warning" || true
fi
say "engine: $(grep -h -E 'MEGAPDF_(SERIES|PATCHES)' Vendor/pdfium/VERSION | tr '\n' ' ')"

UDID=$(xcrun simctl list devices available -j | python3 -c "
import json, sys
devs = json.load(sys.stdin)['devices']
print(next(d['udid'] for v in devs.values() for d in v if d['name'] == sys.argv[1]))" "$DEVICE")
xcrun simctl boot "$UDID" 2>/dev/null || true
xcrun simctl bootstatus "$UDID" -b >/dev/null

# "On My iPhone", by identity rather than by name.
local_storage() {
    local g
    for g in "$HOME/Library/Developer/CoreSimulator/Devices/$UDID/data/Containers/Shared/AppGroup"/*/; do
        if [ "$(plutil -extract MCMMetadataIdentifier raw "$g.com.apple.mobile_container_manager.metadata.plist" 2>/dev/null)" \
             = group.com.apple.FileProvider.LocalStorage ]; then
            echo "${g}File Provider Storage"; return
        fi
    done
}
STORE=$(local_storage)
if [ -z "$STORE" ] || [ ! -d "$STORE" ]; then
    xcrun simctl launch "$UDID" com.apple.DocumentsApp >/dev/null 2>&1 || true
    sleep 8
    xcrun simctl terminate "$UDID" com.apple.DocumentsApp >/dev/null 2>&1 || true
    STORE=$(local_storage)
fi
[ -n "$STORE" ] && [ -d "$STORE" ] || { say "no On My iPhone storage on $DEVICE"; exit 1; }
say "On My iPhone: $STORE"

# The fixtures: the canary alone on its line (drawn twice for fake bold, inside an
# /ActualText span), KEEP lines above and below that must survive.
rm -f "$STORE"/e2e-*.pdf "$STORE"/big-1gb*.pdf
python3 - "$ROOT" "$STORE" <<'PY'
import os, sys
sys.path.insert(0, os.path.join(sys.argv[1], "tools"))
from gen_redaction_fixtures import _one_page
store = sys.argv[2]
canary = _one_page(b"BT /F1 20 Tf 72 700 Td (KEEP Sunrise Tool Rental) Tj ET\n"
                   b"/Span << /ActualText (CANARY-42-XYZ) >> BDC\n"
                   b"BT /F1 20 Tf 72 600 Td (CANARY-42-XYZ) Tj ET\n"
                   b"BT /F1 20 Tf 72.3 600 Td (CANARY-42-XYZ) Tj ET\n"
                   b"EMC\n"
                   b"BT /F1 20 Tf 72 500 Td (KEEP invoice total 1234) Tj ET\n")
plain = _one_page(b"BT /F1 20 Tf 72 700 Td (Protection round trip KEEP) Tj ET\n")
for name, data in [("e2e-redact", canary), ("e2e-redact2", canary), ("e2e-control", canary),
                   ("e2e-save", plain), ("e2e-protect", plain)]:
    open(os.path.join(store, name + ".pdf"), "wb").write(data)
PY
cp "$STORE/e2e-control.pdf" "$OUT/e2e-original.pdf"
TESTS="test1_redactAndOverwriteTheOriginal test2_redactAndSaveACopy test3_controlSaveACopy test4_saveAndSaveACopy test5_setPassword test6_wrongPasswordThenRemove"
if [ -n "${BIG_PDF:-}" ]; then
    cp -c "$BIG_PDF" "$STORE/big-1gb.pdf" 2>/dev/null || cp "$BIG_PDF" "$STORE/big-1gb.pdf"
    TESTS="$TESTS test7_oneGigabyteFile"
fi
TESTS="${ONLY:-$TESTS}"   # ONLY="test1_… test3_…" runs a subset

# A clean app: no recents, so every open goes through the picker.
xcrun simctl uninstall "$UDID" com.megapdf.ios >/dev/null 2>&1 || true
xcrun simctl install "$UDID" "$DD/Build/Products/Debug-iphonesimulator/MegaPDF.app"

LEAK="python3 $ROOT/tools/leakcheck/outside.py"
# Inside the canary's line (72..230 x 599..615 pt): a mark snaps to the text it covers, so
# the drag's own rectangle is larger than what is filled.
AREA="--area 75,601,225,612:1"
FAILS=0
check() {   # check <label> <command…>: the command's exit status is the verdict
    local label="$1"; shift
    if "$@" >>"$LOG" 2>&1; then say "  PASS  $label"; else say "  FAIL  $label"; FAILS=$((FAILS + 1)); fi
}
# A file that was never written would fail every search and so "pass" expect_fail: each
# expect_fail target is first checked to exist, as its own line in the verdict.
expect_fail() {
    local label="$1"; shift
    local f
    for f in "$@"; do
        case $f in *.pdf) [ -s "$f" ] || { say "  FAIL  $label ($(basename "$f") was not written)"; FAILS=$((FAILS + 1)); return; } ;; esac
    done
    if "$@" >>"$LOG" 2>&1; then say "  FAIL  $label (it passed)"; FAILS=$((FAILS + 1)); else say "  PASS  $label"; fi
}
encrypted() { qpdf --is-encrypted "$1"; }   # exit 0 when encrypted; needs no password
opens_with() { qpdf --password="$2" --check "$1" >/dev/null 2>&1; }
no_encrypt_dict() { ! qpdf --is-encrypted "$1" && ! qpdf --json --json-key=trailer "$1" | grep -q '"/Encrypt"'; }

# The copy a test wrote: iOS 26's export sheet has no name field, so a copy takes the
# document's name plus a number, and is found as the one file that was not there before.
new_file() { comm -13 "$OUT/.before" <(ls "$STORE") | head -1; }
take_copy() {   # take_copy <canonical name> <document name>: also checks the copy's name (#278)
    local n; n=$(new_file)
    if [ -n "$n" ]; then say "  copy written as \"$n\""; cp "$STORE/$n" "$OUT/$1"; else rm -f "$OUT/$1"; fi
    check "the copy is named after the document ($2…), not the staging file" sh -c "case '$n' in '$2'*.pdf) exit 0;; *) exit 1;; esac"
}

for t in $TESTS; do
    say "== $t"
    ls "$STORE" > "$OUT/.before"
    TEST_RUNNER_E2E_FILES=1 xcodebuild test-without-building -project MegaPDF.xcodeproj -scheme MegaPDFDemo \
        -destination "id=$UDID" -derivedDataPath "$DD" \
        -only-testing:"MegaPDFUITests/FilesEndToEndUITests/$t" > "$OUT/$t.log" 2>&1 || true
    grep -E "^E2E (1GB|wrong|alert|note)" "$OUT/$t.log" | sed 's/^/  /' | tee -a "$LOG" || true
    if grep -q "Test Case .*$t.* passed" "$OUT/$t.log"; then say "  PASS  UI flow"; else
        say "  FAIL  UI flow — $(grep -m1 -E 'error:' "$OUT/$t.log" | sed 's/.*error: //')"; FAILS=$((FAILS + 1)); fi
    case $t in
    test1_*)
        cp "$STORE/e2e-redact.pdf" "$OUT/"
        check "redacted in place: nothing of the canary survives, KEEP lines kept" \
            $LEAK "$OUT/e2e-redact.pdf" --gone CANARY-42-XYZ --kept "KEEP Sunrise Tool Rental" --kept "KEEP invoice total 1234" $AREA ;;
    test2_*)
        take_copy e2e-redact2-copy.pdf e2e-redact2; cp "$STORE/e2e-redact2.pdf" "$OUT/"
        check "redacted copy: nothing of the canary survives, KEEP lines kept" \
            $LEAK "$OUT/e2e-redact2-copy.pdf" --gone CANARY-42-XYZ --kept "KEEP Sunrise Tool Rental" --kept "KEEP invoice total 1234" $AREA
        expect_fail "the original beside the copy is untouched (the searches find the canary)" \
            $LEAK "$OUT/e2e-redact2.pdf" --gone CANARY-42-XYZ ;;
    test3_*)
        take_copy e2e-control-copy.pdf e2e-control
        expect_fail "control: the unredacted original is caught" $LEAK "$OUT/e2e-original.pdf" --gone CANARY-42-XYZ
        expect_fail "control: a copy the app saved with nothing marked is caught" $LEAK "$OUT/e2e-control-copy.pdf" --gone CANARY-42-XYZ ;;
    test4_*)
        take_copy e2e-save-copy.pdf e2e-save; cp "$STORE/e2e-save.pdf" "$OUT/"
        check "save a copy wrote e2e-save-copy.pdf" test -s "$OUT/e2e-save-copy.pdf"
        check "save wrote the first text in place" sh -c "pdftotext '$OUT/e2e-save.pdf' - | grep -q 'E2E SAVED TEXT'"
        check "save in place does not carry the later edit" sh -c "! pdftotext '$OUT/e2e-save.pdf' - | grep -q 'E2E COPY TEXT'"
        check "save a copy carries both" sh -c "pdftotext '$OUT/e2e-save-copy.pdf' - | grep -q 'E2E SAVED TEXT' && pdftotext '$OUT/e2e-save-copy.pdf' - | grep -q 'E2E COPY TEXT'"
        check "both pass qpdf --check" sh -c "qpdf --check '$OUT/e2e-save.pdf' && qpdf --check '$OUT/e2e-save-copy.pdf'" ;;
    test5_*)
        cp "$STORE/e2e-protect.pdf" "$OUT/e2e-protect-set.pdf"
        qpdf --show-encryption --password=e2e-pass-1 "$OUT/e2e-protect-set.pdf" 2>&1 | grep -E '^R =|^P =|method' | sed 's/^/    /' | tee -a "$LOG" || true
        check "password set: the file is encrypted" encrypted "$OUT/e2e-protect-set.pdf"
        check "password set: it opens with the password" opens_with "$OUT/e2e-protect-set.pdf" e2e-pass-1
        expect_fail "password set: it does not open without it" pdftotext "$OUT/e2e-protect-set.pdf" /dev/null ;;
    test6_*)
        cp "$STORE/e2e-protect.pdf" "$OUT/e2e-protect-removed.pdf"
        check "password removed: no encryption and no /Encrypt in the trailer (#241)" no_encrypt_dict "$OUT/e2e-protect-removed.pdf"
        check "password removed: opens with no password, text intact" sh -c "pdftotext '$OUT/e2e-protect-removed.pdf' - | grep -q 'Protection round trip KEEP'" ;;
    test7_*)
        n=$(new_file)
        check "the copy is named after the document (big-1gb…)" sh -c "case '$n' in big-1gb*.pdf) exit 0;; *) exit 1;; esac"
        ls -l "$STORE"/big-1gb*.pdf | sed 's/^/    /' | tee -a "$LOG"
        check "1 GB copy exists, full size, and poppler reads it" sh -c "[ -n '$n' ] && test \$(stat -f %z '$STORE/$n') -gt 1000000000 && pdfinfo '$STORE/$n' | grep -q 'Pages: *400'"
        [ -n "$n" ] && rm -f "$STORE/$n" ;;
    esac
done
say "== $FAILS failure(s)"
[ "$FAILS" = 0 ]
