#!/usr/bin/env bash
# Proves the APT repository the way a person meets it (#158): in a clean container,
# subscribe exactly as the website says, install, run the app, then publish a newer
# version and watch `apt upgrade` take it. And one refusal: the same repository
# re-signed by a stranger's key must stop `apt update` cold.
#
#     APT_SIGNING_KEY[_FILE]=… tools/linux/check-apt-repo.sh <repo-dir> <fixtures-dir> [image...]
#
# <repo-dir> is what tools/linux/make-apt-repo.sh built. The signing key is needed
# because the upgrade step builds a second repository: the same .deb repacked as
# <version>.1, added to a copy of the first, and signed like a real release would be.
# Images default to debian:12, debian:13, ubuntu:22.04, ubuntu:24.04 and ubuntu:26.04,
# plus debian:12+norecs: the same Debian with `--no-install-recommends`, which is how a
# minimal system installs and how an undeclared dependency shows up (#316). Each
# release a distribution adds is a new ICU soname, and a Depends line that doesn't
# know it refuses to install there (#315), so the newest of each belongs in this list.
#
# When the repository holds more than one version (it keeps every published .deb),
# each image first installs the oldest and checks that `apt upgrade` takes it to the
# newest: the upgrade a real user makes when a release comes out.
#
# The repositories are served over HTTP from this machine and the containers reach
# them on the host network, so apt goes through its real transport, its real
# signature check and its real Signed-By handling. The only line that differs from
# the website's instructions is the URI.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
USAGE='usage: check-apt-repo.sh <repo-dir> <fixtures-dir> [image...]'
REPO="$(cd "${1:?$USAGE}" && pwd)"
FIXTURES="$(cd "${2:?$USAGE}" && pwd)"
shift 2
IMAGES=("$@")
[ ${#IMAGES[@]} -gt 0 ] || IMAGES=(debian:12 debian:13 ubuntu:22.04 ubuntu:24.04 ubuntu:26.04 debian:12+norecs)

WORK="$(mktemp -d)"
cleanup() { [ -n "${SERVER:-}" ] && kill "$SERVER" 2>/dev/null; rm -rf "$WORK"; }
trap cleanup EXIT
SERVE="$WORK/serve"
mkdir -p "$SERVE"

# --- r1: the repository as built ------------------------------------------------------
cp -a "$REPO" "$SERVE/r1"

# --- r2: r1 plus the next version, signed the same way --------------------------------
# Newest and oldest by dpkg's own ordering: sort -V puts 2.0.0-2 and 2.0.0 the wrong
# way round, and apt goes by dpkg.
VERSION="" OLDEST="" DEB=""
for d in "$REPO"/pool/main/m/megapdf/megapdf_*_amd64.deb; do
    v="$(dpkg-deb -f "$d" Version)"
    if [ -z "$VERSION" ] || dpkg --compare-versions "$v" gt "$VERSION"; then VERSION="$v"; DEB="$d"; fi
    if [ -z "$OLDEST" ] || dpkg --compare-versions "$v" lt "$OLDEST"; then OLDEST="$v"; fi
done
[ -n "$DEB" ] || { echo "::error::no .deb in $REPO/pool" >&2; exit 1; }
echo "repository holds $OLDEST … $VERSION"
NEXT="$VERSION.1"
dpkg-deb -R "$DEB" "$WORK/repack"
sed -i "s/^Version: .*/Version: $NEXT/" "$WORK/repack/DEBIAN/control"
echo "$NEXT" > "$WORK/repack/opt/MegaPDF/UPGRADE-TEST"
dpkg-deb --root-owner-group -Zxz --build "$WORK/repack" "$WORK/megapdf_${NEXT}_amd64.deb" >/dev/null
cp -a "$REPO" "$SERVE/r2"
"$ROOT/tools/linux/make-apt-repo.sh" "$SERVE/r2" "$WORK/megapdf_${NEXT}_amd64.deb" >/dev/null

# --- forged: r1 re-signed by a key nobody trusts ----------------------------------------
cp -a "$REPO" "$SERVE/forged"
(
    export GNUPGHOME="$WORK/stranger"; mkdir -m 700 "$GNUPGHOME"
    gpg --batch --quiet --passphrase '' --quick-gen-key "Not MegaPDF <x@example.invalid>" ed25519 sign never 2>/dev/null
    rm -f "$SERVE/forged/dists/stable/InRelease" "$SERVE/forged/dists/stable/Release.gpg"
    gpg --batch --clearsign -o "$SERVE/forged/dists/stable/InRelease" "$SERVE/forged/dists/stable/Release"
    gpg --batch --armor --detach-sign -o "$SERVE/forged/dists/stable/Release.gpg" "$SERVE/forged/dists/stable/Release"
)

PORT=$(( 20000 + RANDOM % 20000 ))
python3 -m http.server "$PORT" --bind 127.0.0.1 --directory "$SERVE" >/dev/null 2>&1 &
SERVER=$!
for _ in $(seq 1 50); do curl -fsS "http://127.0.0.1:$PORT/r1/megapdf.sources" >/dev/null 2>&1 && break; sleep 0.1; done

cat > "$WORK/inside.sh" <<'INSIDE'
set -uo pipefail
export DEBIAN_FRONTEND=noninteractive
BASE="http://127.0.0.1:$PORT"
fails=0
ok()   { printf '  ok    %s\n' "$1"; }
fail() { printf '  FAIL  %s\n' "$1"; fails=$((fails + 1)); }
point() { sed -i "s|^URIs: .*|URIs: $BASE/$1|" /etc/apt/sources.list.d/megapdf.sources; }

apt-get update -qq >/dev/null 2>&1
# binutils for objdump, which package-check.sh reads the engine's runpath with.
apt-get install -y -qq curl ca-certificates binutils >/dev/null 2>&1
# A minimal install: no recommends, so every library the app needs has to be declared.
INSTALL=(apt-get install -y -qq)
[ "${NORECS:-0}" = 1 ] && INSTALL+=(--no-install-recommends)

echo "-- subscribe, exactly as the website says (URI aside)"
install -d -m 0755 /etc/apt/keyrings
curl -fsSL "$BASE/r1/megapdf.gpg" | tee /etc/apt/keyrings/megapdf.gpg > /dev/null
curl -fsSL "$BASE/r1/megapdf.sources" | tee /etc/apt/sources.list.d/megapdf.sources > /dev/null
grep -q '^URIs: https://electricrv.ca/megapdf/apt$' /etc/apt/sources.list.d/megapdf.sources \
    && ok "the published .sources names https://electricrv.ca/megapdf/apt" \
    || fail "the published .sources does not name the site"
point r1

echo "-- a stranger's signature is refused"
point forged
# Captured first: under pipefail, `apt-get update | grep -q` reports apt's own failure
# status — which is exactly the failure this is looking for.
forged=$(apt-get update 2>&1 || true)
if printf '%s\n' "$forged" | grep -qE 'NO_PUBKEY|is not signed|BADSIG'; then
    ok "apt update refuses the forged repository"
else
    fail "apt update accepted a repository signed by another key"
fi
point r1

echo "-- install"
apt-get update 2>&1 | grep -E 'megapdf|electricrv|127.0.0.1' | sed 's/^/     /' | head -4
if [ "$OLDEST" != "$VERSION" ]; then
    # The upgrade a user makes on release day: the previous release installed, then
    # the new one arriving through apt upgrade.
    # An older release that never installed on this distribution (2.0.0 on Ubuntu
    # 26.04, #315) is noted rather than failed: there is no upgrade path to test there,
    # and the install of the newest below is what has to work.
    if "${INSTALL[@]}" "megapdf=$OLDEST" >/dev/null 2>&1; then
        ok "apt install megapdf=$OLDEST (the oldest the repository keeps)"
        apt-get upgrade -y -qq >/dev/null 2>&1
        now=$(dpkg-query -W -f='${Version}' megapdf 2>/dev/null)
        [ "$now" = "$VERSION" ] && ok "apt upgrade moved $OLDEST to $VERSION" \
            || fail "after apt upgrade megapdf is ${now:-missing}, expected $VERSION"
    else
        printf '  note  megapdf %s does not install on this release; no upgrade path to test\n' "$OLDEST"
    fi
fi
if "${INSTALL[@]}" megapdf >/dev/null 2>&1; then
    ok "apt install megapdf: $(dpkg-query -W -f='${Version}' megapdf)"
else
    fail "apt install megapdf failed"; "${INSTALL[@]}" megapdf 2>&1 | tail -5
fi
[ "$(dpkg-query -W -f='${Version}' megapdf 2>/dev/null)" = "$VERSION" ] \
    && ok "the installed version is the newest, $VERSION" \
    || fail "the installed version is not the newest ($VERSION)"
[ -f /usr/share/metainfo/ca.electricrv.MegaPDF.metainfo.xml ] \
    && grep -q '<launchable type="desktop-id">megapdf.desktop</launchable>' /usr/share/metainfo/ca.electricrv.MegaPDF.metainfo.xml \
    && ok "the AppStream metainfo is installed and launches megapdf.desktop" \
    || fail "no AppStream metainfo for megapdf.desktop in /usr/share/metainfo"
apt-cache policy megapdf | sed -n '1,6p' | sed 's/^/     /'
bash /src/tools/linux/package-check.sh /opt/MegaPDF /fixtures "" apt AptRepository || fails=$((fails + 1))

echo "-- a newer version is published: apt upgrade takes it"
point r2
apt-get update -qq >/dev/null 2>&1
apt-get upgrade -y -qq >/dev/null 2>&1
now=$(dpkg-query -W -f='${Version}' megapdf)
if [ "$now" = "$NEXT" ] && [ "$(cat /opt/MegaPDF/UPGRADE-TEST 2>/dev/null)" = "$NEXT" ]; then
    ok "apt upgrade moved megapdf to $now, and the new files are on disk"
else
    fail "after apt upgrade megapdf is $now, expected $NEXT"
fi

echo "-- remove"
apt-get remove -y -qq megapdf >/dev/null 2>&1
[ ! -e /opt/MegaPDF/MegaPDF ] && [ ! -e /usr/bin/megapdf ] \
    && ok "apt remove takes the app and its launcher away" || fail "files left after apt remove"

exit $fails
INSIDE

total=0
for entry in "${IMAGES[@]}"; do
    image="${entry%+norecs}"
    norecs=0; [ "$image" != "$entry" ] && norecs=1
    echo
    echo "=== $entry"
    if docker run --rm --network host -e PORT="$PORT" -e NEXT="$NEXT" \
            -e VERSION="$VERSION" -e OLDEST="$OLDEST" -e NORECS="$norecs" \
            -v "$ROOT:/src:ro" -v "$FIXTURES:/fixtures:ro" -v "$WORK/inside.sh:/inside.sh:ro" \
            "$image" bash /inside.sh; then
        echo "=== $entry: all passed"
    else
        echo "=== $entry: FAILED"
        total=$((total + 1))
    fi
done
echo
[ "$total" -eq 0 ] && echo "APT repository: every image passed" || echo "APT repository: $total image(s) failed"
exit "$total"
