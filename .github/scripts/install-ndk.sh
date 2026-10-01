#!/usr/bin/env bash
# #624: android/engine's build.gradle.kts does not set ndkVersion, so AGP falls back to
# its own default -- 27.0.12077973 for AGP 8.10.x (see the AGP 8.10 release notes). That
# version is not one of the three the ubuntu-latest runner image preinstalls
# (27.3.13750724 "default", 28.2.13676358, 29.0.14206865), so an unpinned `./gradlew`
# resolves and downloads it from Google's SDK repository on every run. That download has
# twice arrived corrupted and failed the build before any test ran, which now blocks
# merges (#600 and this week's merge-blocking changes).
#
# This deliberately does NOT pin ndkVersion to one of the preinstalled versions: that
# would change the NDK the app ships against, which is a real change to the shipped
# binary and Dave's call, not this script's (see #624). Instead it installs the exact
# version AGP already defaults to, explicitly and with its own retry-and-verify loop, so
# a corrupted download is caught and retried here rather than failing the build/test step
# it would otherwise run inside of.
set -uo pipefail

ndk_version="27.0.12077973"
ndk_dir="$ANDROID_HOME/ndk/$ndk_version"
sdkmanager="$ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager"
log_file="$(mktemp)"

for attempt in 1 2 3; do
  echo "::group::Install NDK $ndk_version (attempt $attempt/3)"
  rm -rf "$ndk_dir"
  yes | "$sdkmanager" --install "ndk;$ndk_version" >"$log_file" 2>&1
  install_status=$?
  if [ "$install_status" -eq 0 ] && grep -q "^Pkg.Revision = $ndk_version$" "$ndk_dir/source.properties" 2>/dev/null; then
    echo "NDK $ndk_version installed and verified at $ndk_dir."
    echo "::endgroup::"
    rm -f "$log_file"
    exit 0
  fi
  echo "Attempt $attempt failed (sdkmanager exit $install_status) or left no verified install at $ndk_dir."
  tail -n 40 "$log_file" || true
  echo "::endgroup::"
  sleep 10
done

echo "::error::NDK $ndk_version did not install correctly after 3 attempts"
rm -f "$log_file"
exit 1
