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
# Images default to debian:12, ubuntu:22.04 and ubuntu:24.04.
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
[ ${#IMAGES[@]} -gt 0 ] || IMAGES=(debian:12 ubuntu:22.04 ubuntu:24.04)

WORK="$(mktemp -d)"
cleanup() { [ -n "${SERVER:-}" ] && kill "$SERVER" 2>/dev/null; rm -rf "$WORK"; }
trap cleanup EXIT
SERVE="$WORK/serve"
mkdir -p "$SERVE"

# --- r1: the repository as built ------------------------------------------------------
cp -a "$REPO" "$SERVE/r1"

# --- r2: r1 plus the next version, signed the same way --------------------------------
DEB="$(ls "$REPO"/pool/main/m/megapdf/megapdf_*_amd64.deb | sort -V | tail -1)"
VERSION="$(dpkg-deb -f "$DEB" Version)"
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
if apt-get install -y -qq megapdf >/dev/null 2>&1; then
    ok "apt install megapdf: $(dpkg-query -W -f='${Version}' megapdf)"
else
    fail "apt install megapdf failed"; apt-get install -y megapdf 2>&1 | tail -5
fi
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
for image in "${IMAGES[@]}"; do
    echo
    echo "=== $image"
    if docker run --rm --network host -e PORT="$PORT" -e NEXT="$NEXT" \
            -v "$ROOT:/src:ro" -v "$FIXTURES:/fixtures:ro" -v "$WORK/inside.sh:/inside.sh:ro" \
            "$image" bash /inside.sh; then
        echo "=== $image: all passed"
    else
        echo "=== $image: FAILED"
        total=$((total + 1))
    fi
done
echo
[ "$total" -eq 0 ] && echo "APT repository: every image passed" || echo "APT repository: $total image(s) failed"
exit "$total"
