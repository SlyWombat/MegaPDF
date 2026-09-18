#!/bin/sh
# /app/bin/megapdf — what `command: megapdf` in the manifest resolves to, and what the
# exported desktop entry runs (#158).
#
# The published tree lives in one piece under /app/lib/megapdf because the apphost finds
# libmegapdf_core.so and libpdfium.so beside itself; /app/bin holds this instead so that
# PATH has a launcher on it rather than ninety megabytes of engine.
exec /app/lib/megapdf/MegaPDF "$@"
