#!/usr/bin/env bash
# Puts everything the capture and flow scripts open into the emulator's Downloads,
# and makes the system picker see it. The large fixtures are optional: pass
# --large to include them (the 2.5 GB one takes a few minutes to push).
set -euo pipefail

SERIAL="${SERIAL:-emulator-5554}"
FIXTURES="${FIXTURES:-/work/out/fixtures}"
LARGE="${LARGE:-/work/large}"

push() {
    local path="$1"
    [ -f "$path" ] || { echo "missing: $path"; return 0; }
    adb -s "$SERIAL" push "$path" "/sdcard/Download/$(basename "$path")" > /dev/null
    adb -s "$SERIAL" shell content call --uri content://media/external/file \
        --method scan_file --arg "/sdcard/Download/$(basename "$path")" > /dev/null
    echo "pushed $(basename "$path")"
}

for name in MegaPDF-Test-Form.pdf corrupt.pdf demo.pdf demo-fr.pdf forms.pdf \
            formtext.pdf textbox.pdf cropped.pdf userunit.pdf stamped.pdf; do
    push "$FIXTURES/$name"
done
for name in aes-256.pdf owner-only.pdf rc4-128.pdf; do
    push "$FIXTURES/security/$name"
done
for name in wide-poster.pdf tall-receipt.pdf wide-userunit.pdf mixed-sizes.pdf \
            many-fields.pdf many-objects.pdf deep-2000.pdf; do
    push "$LARGE/$name"
done

if [ "${1:-}" = "--large" ]; then
    for name in deep-10000.pdf wide-deep.pdf huge-image-page.pdf \
                big-scan-250mb.pdf big-1gb.pdf huge-2_5gb.pdf; do
        push "$LARGE/$name"
    done
fi

adb -s "$SERIAL" shell ls -la /sdcard/Download/
