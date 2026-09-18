#!/usr/bin/env bash
# The AVDs the Play gate set is shot on (#146).
#
#   megapdf-store-phone    1080 x 2400 @ 420 dpi  = 411 x 914 dp
#   megapdf-store-tablet   1600 x 2560 @ 320 dpi  = 800 x 1280 dp
#
# The phone is `profile: pixel_6` on API 33 `default` — the geometry *and* the
# system image the Android Screenshots workflow uses, so a local run produces the
# pixels that workflow would. Do not "upgrade" the API level without changing the
# workflow to match: API 36 draws a gesture pill where 33 draws no navigation bar
# at all, which moves every pixel below the page.
#
# The tablet has no workflow of its own; the listing's tablet slots have to be
# checked in the Play Console, which is out of reach from here. It is shot on the
# same image so the two sets are comparable.
set -euo pipefail

IMAGE="${AVD_IMAGE:-system-images;android-33;default;x86_64}"

make_avd() {
    local name="$1" w="$2" h="$3" dpi="$4" ram="$5"
    rm -rf "$HOME/.android/avd/$name.avd" "$HOME/.android/avd/$name.ini"
    echo no | avdmanager create avd -n "$name" -k "$IMAGE" --force > /dev/null 2>&1
    local cfg="$HOME/.android/avd/$name.avd/config.ini"
    grep -vE '^(hw\.lcd\.(width|height|density)|hw\.ramSize|vm\.heapSize|hw\.keyboard|disk\.dataPartition\.size|hw\.gpu\.(enabled|mode)|skin\.(name|path)|hw\.initialOrientation|showDeviceFrame)=' \
        "$cfg" > "$cfg.tmp" && mv "$cfg.tmp" "$cfg"
    cat >> "$cfg" <<CFG
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
CFG
    echo "created $name  ${w}x${h} @ ${dpi}dpi"
}

make_avd megapdf-store-phone  1080 2400 420 4096
make_avd megapdf-store-tablet 1600 2560 320 6144
