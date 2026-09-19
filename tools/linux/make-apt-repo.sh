#!/usr/bin/env bash
# Builds MegaPDF's signed APT repository from one or more .debs (#158).
#
#     APT_SIGNING_KEY_FILE=~/secrets/megapdf/apt-signing-key.asc \
#         tools/linux/make-apt-repo.sh <out-dir> <deb>...
#
# or, in CI, with the armoured key in APT_SIGNING_KEY (the Actions secret of that name).
#
# <out-dir> becomes the repository root that https://electricrv.ca/megapdf/apt/ serves:
#
#     pool/main/m/megapdf/megapdf_<version>_amd64.deb
#     dists/stable/main/binary-amd64/Packages{,.gz,.xz}
#     dists/stable/{Release,Release.gpg,InRelease}
#
# plus the three files a person needs to subscribe to it, copied from
# website/megapdf/apt/ where they are committed: megapdf.gpg (the public key, binary,
# for /etc/apt/keyrings), megapdf.asc (the same key, armoured, to read) and
# megapdf.sources (the deb822 entry for /etc/apt/sources.list.d). dists/ and pool/
# are generated and never committed; they are built from the release's own .deb when
# the site is deployed.
#
# Every .deb already in <out-dir>/pool is kept, so a repository can be grown one
# release at a time rather than rebuilt from nothing. APT picks the newest.
#
# The script refuses to sign with any key but the one whose fingerprint is committed
# in website/megapdf/apt/FINGERPRINT, and checks its own InRelease against the
# committed public key with gpgv before it finishes — the same check apt makes on a
# user's machine — so a repository that would fail `apt update` never leaves here.
# It prints the fingerprint and nothing else about the key.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PUBLIC="$ROOT/website/megapdf/apt"
USAGE='usage: make-apt-repo.sh <out-dir> <deb>...'
OUT=${1:?$USAGE}; shift
[ $# -gt 0 ] || { echo "$USAGE" >&2; exit 2; }

SUITE=stable
COMPONENT=main
ARCH=amd64
EXPECTED="$(tr -d '[:space:]' < "$PUBLIC/FINGERPRINT")"

for tool in gpg gpgv apt-ftparchive dpkg-deb; do
    command -v "$tool" >/dev/null || { echo "::error::$tool is not installed" >&2; exit 1; }
done

# --- the key, in a keyring of its own that is deleted on the way out --------------
GNUPGHOME="$(mktemp -d)"; export GNUPGHOME
trap 'rm -rf "$GNUPGHOME"' EXIT
if [ -n "${APT_SIGNING_KEY:-}" ]; then
    printf '%s\n' "$APT_SIGNING_KEY" | gpg --batch --quiet --import 2>/dev/null
elif [ -n "${APT_SIGNING_KEY_FILE:-}" ] && [ -r "$APT_SIGNING_KEY_FILE" ]; then
    gpg --batch --quiet --import < "$APT_SIGNING_KEY_FILE" 2>/dev/null
else
    echo "::error::no signing key: set APT_SIGNING_KEY or APT_SIGNING_KEY_FILE" >&2
    exit 1
fi
FPR="$(gpg --batch --with-colons --list-secret-keys | awk -F: '/^fpr/{print $10; exit}')"
if [ "$FPR" != "$EXPECTED" ]; then
    echo "::error::the signing key is $FPR, but website/megapdf/apt/FINGERPRINT says $EXPECTED" >&2
    exit 1
fi
echo "signing with $FPR"

# --- pool ---------------------------------------------------------------------------
POOL="pool/$COMPONENT/m/megapdf"
mkdir -p "$OUT/$POOL" "$OUT/dists/$SUITE/$COMPONENT/binary-$ARCH"
for deb in "$@"; do
    pkg="$(dpkg-deb -f "$deb" Package)"
    ver="$(dpkg-deb -f "$deb" Version)"
    arch="$(dpkg-deb -f "$deb" Architecture)"
    [ "$pkg" = megapdf ] && [ "$arch" = "$ARCH" ] || {
        echo "::error::$deb is $pkg/$arch, not megapdf/$ARCH" >&2; exit 1; }
    cp "$deb" "$OUT/$POOL/${pkg}_${ver}_${arch}.deb"
    echo "  pool: ${pkg}_${ver}_${arch}.deb"
done

# --- indices ------------------------------------------------------------------------
# apt-ftparchive writes paths relative to the directory it runs in, which has to be
# the repository root for the Filename: fields to resolve against the URL.
BIN="dists/$SUITE/$COMPONENT/binary-$ARCH"
(
    cd "$OUT"
    apt-ftparchive packages "pool/$COMPONENT" > "$BIN/Packages"
    gzip -9nkf "$BIN/Packages"
    xz -9kf "$BIN/Packages"
    apt-ftparchive \
        -o "APT::FTPArchive::Release::Origin=Electric RV" \
        -o "APT::FTPArchive::Release::Label=MegaPDF" \
        -o "APT::FTPArchive::Release::Suite=$SUITE" \
        -o "APT::FTPArchive::Release::Codename=$SUITE" \
        -o "APT::FTPArchive::Release::Architectures=$ARCH" \
        -o "APT::FTPArchive::Release::Components=$COMPONENT" \
        -o "APT::FTPArchive::Release::Description=MegaPDF for Debian and Ubuntu" \
        release "dists/$SUITE" > "dists/$SUITE/Release.tmp"
    mv "dists/$SUITE/Release.tmp" "dists/$SUITE/Release"
    rm -f "dists/$SUITE/InRelease" "dists/$SUITE/Release.gpg"
    gpg --batch --yes --local-user "$FPR" --clearsign \
        -o "dists/$SUITE/InRelease" "dists/$SUITE/Release"
    gpg --batch --yes --local-user "$FPR" --armor --detach-sign \
        -o "dists/$SUITE/Release.gpg" "dists/$SUITE/Release"
)

# --- the files a person subscribes with -----------------------------------------------
if [ "$(cd "$OUT" && pwd)" != "$(cd "$PUBLIC" && pwd)" ]; then
    cp "$PUBLIC/megapdf.gpg" "$PUBLIC/megapdf.asc" "$PUBLIC/megapdf.sources" "$OUT/"
fi

# --- the check apt makes --------------------------------------------------------------
gpgv --keyring "$PUBLIC/megapdf.gpg" "$OUT/dists/$SUITE/InRelease" 2>/dev/null \
    || { echo "::error::InRelease does not verify against website/megapdf/apt/megapdf.gpg" >&2; exit 1; }
gpgv --keyring "$PUBLIC/megapdf.gpg" "$OUT/dists/$SUITE/Release.gpg" "$OUT/dists/$SUITE/Release" 2>/dev/null \
    || { echo "::error::Release.gpg does not verify against website/megapdf/apt/megapdf.gpg" >&2; exit 1; }
echo "  InRelease and Release.gpg verify against the committed public key"
grep -E '^(Package|Version|Filename):' "$OUT/$BIN/Packages" | paste - - - | sed 's/^/  /'
echo "repository: $OUT"
