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
trap 'rm -f "$log_file"' EXIT

# Drops sdkmanager's own \r-driven progress-bar spam (hundreds of lines per attempt) so
# whatever real message it printed -- a license prompt, an HTTP error, a checksum
# complaint -- is what's left and easy to spot in the step's own output. Never silences
# the exit code or the full log: the full log is still in $log_file for anyone who needs
# it (named in the final error below), this is only what gets echoed into the job output.
meaningful_lines() {
  grep -avE '^\[[= ]*\][[:space:]]*[0-9]+%' "$1"
}

for attempt in 1 2 3; do
  echo "::group::Install NDK $ndk_version (attempt $attempt/3)"
  rm -rf "$ndk_dir"

  # `yes` feeds license-prompt answers sdkmanager asks for on an image where the NDK
  # license was not pre-accepted; this runner image does pre-accept it, so in practice
  # `yes` finds nothing to answer and sdkmanager just exits once it's done. That exit
  # closes yes's stdin out from under it, yes dies to SIGPIPE/EPIPE and (GNU coreutils)
  # prints "yes: standard output: Broken pipe" and exits 1 -- and because this script
  # runs under `pipefail`, that 1 from yes, not sdkmanager's own (successful) status, is
  # what `$?` reported. That bug reliably turned a clean install into a reported failure
  # on every single run (PR #638): three attempts, three real successes, three false
  # failures, one red job. `yes`'s stderr is dropped and PIPESTATUS is used explicitly so
  # only sdkmanager's own exit code decides anything here.
  yes 2>/dev/null | "$sdkmanager" --install "ndk;$ndk_version" >"$log_file" 2>&1
  sdkmanager_status=${PIPESTATUS[1]}

  if [ "$sdkmanager_status" -ne 0 ]; then
    echo "::warning::NDK install attempt $attempt/3: sdkmanager exited $sdkmanager_status (likely the network or a corrupted download) -- retrying"
    echo "--- sdkmanager output, progress bar stripped ---"
    meaningful_lines "$log_file" || true
    echo "::endgroup::"
    sleep 10
    continue
  fi

  # sdkmanager exited 0 -- the half of a corrupted-download failure this is actually
  # guarding against lands here: a package sdkmanager thinks it installed but whose
  # bytes don't check out. Pkg.Revision is read rather than assumed present, so a
  # missing/short/CRLF source.properties reads as "could not verify" rather than
  # silently passing a grep that happened not to match.
  revision=""
  if [ -f "$ndk_dir/source.properties" ]; then
    revision=$(tr -d '\r' < "$ndk_dir/source.properties" | sed -n 's/^Pkg\.Revision[[:space:]]*=[[:space:]]*//p' | head -n1)
  fi
  if [ "$revision" = "$ndk_version" ]; then
    echo "NDK $ndk_version installed and verified at $ndk_dir (Pkg.Revision matches)."
    echo "::endgroup::"
    exit 0
  fi

  echo "::warning::NDK install attempt $attempt/3: sdkmanager exited 0 but $ndk_dir/source.properties does not confirm $ndk_version (got Pkg.Revision='${revision:-<missing>}') -- treating as a bad install and retrying"
  echo "--- sdkmanager output, progress bar stripped ---"
  meaningful_lines "$log_file" || true
  echo "::endgroup::"
  sleep 10
done

echo "::error::NDK $ndk_version failed to install after 3 attempts -- see the per-attempt ::warning:: lines and 'Install NDK ... (attempt N/3)' groups above for sdkmanager's own exit code and output on each try"
exit 1
