#!/usr/bin/env bash
# Encrypted test fixtures for #131: one per standard security handler the apps must
# open, an owner-only document with every restriction, non-ASCII passwords and
# cleartext metadata. qpdf writes them with random salts, so they are committed
# (tests/MegaPDF.Core.Tests/Fixtures/security) rather than regenerated in CI, and
# copied into the iOS and Android test fixtures. Every password here is test data.
#
#     python3 tools/gen_test_fixtures.py /tmp/megapdf-fixtures
#     tools/gen_security_fixtures.sh /tmp/megapdf-fixtures
#
# | file                  | handler            | user        | owner          |
# |-----------------------|--------------------|-------------|----------------|
# | rc4-40.pdf            | R2, RC4 40-bit     | u-rc4-40    | o-rc4-40       |
# | rc4-128.pdf           | R3, RC4 128-bit    | u-rc4-128   | o-rc4-128      |
# | aes-128.pdf           | R4, AES-128        | u-aes-128   | o-aes-128      |
# | aes-256.pdf           | R6, AES-256        | u-aes-256   | o-aes-256      |
# | owner-only.pdf        | R6, no user        | (none)      | o-restricted   |
# | nonascii-aes-256.pdf  | R6, UTF-8          | clé-été     | o-nonascii     |
# | nonascii-rc4-128.pdf  | R3, PDFDocEncoding | clé         | o-nonascii-rc4 |
# | metadata-clear.pdf    | R6, clear metadata | u-meta      | o-meta         |
#
# owner-only.pdf allows nothing to a plain open: no printing, modifying, copying,
# annotating, form filling or assembly.
#
# The remove-*.pdf family is eight handlers over secure-source.pdf, the fixture with
# two pages of text, a filled text field, a checked checkbox and two annotations
# (#241). Removing protection from each must write a plain file that still draws,
# fills and annotates exactly the same. Six of them, with a classic cross-reference
# table:
#
# | file                    | handler         | user           | owner            | restricts |
# |-------------------------|-----------------|----------------|------------------|-----------|
# | remove-rc4-40.pdf       | R2, RC4 40-bit  | u-remove-40    | o-remove-40      | nothing   |
# | remove-rc4-128.pdf      | R3, RC4 128-bit | u-remove-128   | o-remove-128     | nothing   |
# | remove-aes-128.pdf      | R4, AES-128     | u-remove-a128  | o-remove-a128    | nothing   |
# | remove-aes-256.pdf      | R6, AES-256     | u-remove-a256  | o-remove-a256    | nothing   |
# | remove-owner-only.pdf   | R6, no user     | (none)         | o-remove-owner   | all       |
# | remove-user-owner.pdf   | R6, both        | u-remove-both  | o-remove-both    | all       |
#
# and two of the same handlers written with object streams and a cross-reference
# stream, because the cross-reference stream is itself the trailer and so has an
# object number: a removal that leaves it in the file leaves /Encrypt in the file.
#
# | remove-objstm-aes256.pdf  | R6, AES-256, object streams  | u-remove-x256 | o-remove-x256 |
# | remove-objstm-rc4-128.pdf | R3, RC4-128, object streams  | u-remove-x128 | o-remove-x128 |
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FIXTURES="${1:?usage: tools/gen_security_fixtures.sh <directory written by tools/gen_test_fixtures.py>}"
SRC="$FIXTURES/fixture.pdf"
RICH="$FIXTURES/secure-source.pdf"
OUT="$ROOT/tests/MegaPDF.Core.Tests/Fixtures/security"
mkdir -p "$OUT"

# qpdf reads its arguments from a file, so no password appears in a process listing.
ARGS="$(mktemp)"
trap 'rm -f "$ARGS"' EXIT

fixture() {
    local name=$1
    shift
    printf '%s\n' "$@" "--" "$SRC" "$OUT/$name.pdf" > "$ARGS"
    qpdf @"$ARGS"
    echo "wrote $name.pdf"
}

# The same, over the richer source the removal is checked against (#241).
rich_fixture() {
    local name=$1
    shift
    printf '%s\n' "$@" "--" "$RICH" "$OUT/$name.pdf" > "$ARGS"
    qpdf @"$ARGS"
    echo "wrote $name.pdf"
}

deny_everything=(--print=none --modify=none --extract=n --annotate=n --form=n --assemble=n)

fixture rc4-40            --allow-weak-crypto --encrypt u-rc4-40 o-rc4-40 40
fixture rc4-128           --allow-weak-crypto --encrypt u-rc4-128 o-rc4-128 128 --use-aes=n
fixture aes-128           --encrypt u-aes-128 o-aes-128 128 --use-aes=y
fixture aes-256           --encrypt u-aes-256 o-aes-256 256
fixture owner-only        --encrypt --user-password= --owner-password=o-restricted --bits=256 \
                          --print=none --modify=none --extract=n --annotate=n --form=n --assemble=n
fixture nonascii-aes-256  --encrypt clé-été o-nonascii 256
fixture nonascii-rc4-128  --allow-weak-crypto --encrypt clé o-nonascii-rc4 128 --use-aes=n
fixture metadata-clear    --encrypt u-meta o-meta 256 --cleartext-metadata

rich_fixture remove-rc4-40      --allow-weak-crypto --encrypt u-remove-40 o-remove-40 40
rich_fixture remove-rc4-128     --allow-weak-crypto --encrypt u-remove-128 o-remove-128 128 --use-aes=n
rich_fixture remove-aes-128     --encrypt u-remove-a128 o-remove-a128 128 --use-aes=y
rich_fixture remove-aes-256     --encrypt u-remove-a256 o-remove-a256 256
rich_fixture remove-owner-only  --encrypt "" o-remove-owner 256 "${deny_everything[@]}"
rich_fixture remove-user-owner  --encrypt u-remove-both o-remove-both 256 "${deny_everything[@]}"
rich_fixture remove-objstm-aes256  --object-streams=generate --encrypt u-remove-x256 o-remove-x256 256
rich_fixture remove-objstm-rc4-128 --allow-weak-crypto --object-streams=generate \
                                   --encrypt u-remove-x128 o-remove-x128 128 --use-aes=n
