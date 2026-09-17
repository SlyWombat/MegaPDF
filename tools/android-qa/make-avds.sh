#!/usr/bin/env bash
# Creates the three AVDs the #146 Android QA pass uses. Device profiles are
# written by hand rather than taken from a stock definition, so the dp sizes are
# exactly what the pass claims to cover.
#
#   megapdf-small    720 x 1440 @ 320dpi  =  360 x  720 dp  (a budget phone)
#   megapdf-large   1440 x 3200 @ 480dpi  =  480 x 1067 dp  (the biggest phone)
#   megapdf-tablet  1600 x 2560 @ 320dpi  =  800 x 1280 dp  (a 10" tablet)
set -euo pipefail

IMAGE="${AVD_IMAGE:-system-images;android-36;google_apis;x86_64}"

make_avd() {
    local name="$1" w="$2" h="$3" dpi="$4" ram="$5"
    rm -rf "$HOME/.android/avd/$name.avd" "$HOME/.android/avd/$name.ini"
    echo no | avdmanager create avd -n "$name" -k "$IMAGE" --force > /dev/null
    local cfg="$HOME/.android/avd/$name.avd/config.ini"
    # Drop every key we are about to set, then append ours.
    grep -vE '^(hw\.lcd\.(width|height|density)|hw\.ramSize|vm\.heapSize|hw\.keyboard|disk\.dataPartition\.size|hw\.gpu\.(enabled|mode)|skin\.(name|path)|hw\.initialOrientation|showDeviceFrame)=' \
        "$cfg" > "$cfg.tmp" && mv "$cfg.tmp" "$cfg"
    cat >> "$cfg" <<EOF
hw.lcd.width=$w
hw.lcd.height=$h
hw.lcd.density=$dpi
hw.ramSize=$ram
vm.heapSize=512
hw.keyboard=yes
hw.initialOrientation=portrait
disk.dataPartition.size=8G
hw.gpu.enabled=yes
hw.gpu.mode=swiftshader_indirect
skin.name=${w}x${h}
showDeviceFrame=no
EOF
    echo "created $name  ${w}x${h} @ ${dpi}dpi"
}

make_avd megapdf-small   720 1440 320 3072
make_avd megapdf-large  1440 3200 480 6144
make_avd megapdf-tablet 1600 2560 320 6144

avdmanager list avd 2>/dev/null | grep -E 'Name:|Path:' || true
