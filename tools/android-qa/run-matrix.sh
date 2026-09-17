#!/usr/bin/env bash
# The whole capture matrix for one device: three languages x light and dark,
# then the same three languages at the largest system text size.
#   run-matrix.sh <device-tag> <serial>
set -u

DEVICE="${1:?device tag}"
SERIAL="${2:?adb serial}"
OUT="${OUT:-/work/out/shots}"
HERE="$(cd "$(dirname "$0")" && pwd)"

for lang in en fr-CA fr-FR; do
    for theme in light dark; do
        python3 "$HERE/capture.py" --serial "$SERIAL" --device "$DEVICE" \
            --lang "$lang" --theme "$theme" --out "$OUT"
    done
done

for lang in en fr-CA fr-FR; do
    python3 "$HERE/capture.py" --serial "$SERIAL" --device "$DEVICE" \
        --lang "$lang" --theme light --font-scale 2.0 --out "$OUT"
done

# Leave the device on the defaults for whatever runs next.
adb -s "$SERIAL" shell settings put system font_scale 1.0
adb -s "$SERIAL" shell cmd uimode night no
echo "matrix done for $DEVICE"
