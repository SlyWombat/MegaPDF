# The 2.2 listing captures, as shot and as read

The reviewed store images for 2.2, kept here so that the set that was looked at
is the set that goes up, and so that a reviewer can disagree with the reading
rather than re-shoot to find out what was in the frame.

**Nothing here is published.** Putting images on a listing is a separate,
deliberate act in each console (App Store Connect, the Play Console, Partner
Center) and is Dave's call. This folder stops at reviewed images.

## Why they are in `docs/qa/` and not in `artifacts/`

`artifacts/` is in `.gitignore`, so the 2.0, 2.1 and 2.1.1 sets under
`artifacts/store/captures-*/` exist only on the machines that shot them — the
repository has never held a Mac or a Play set. The Linux images are the
exception, and only because they are also the website's screenshots
(`website/megapdf/screenshots/linux/`), which is a published tree rather than a
record of a shoot. `docs/qa/linux-fr/` is the precedent for reviewed capture
images living beside the runbook that describes them.

The capture scripts still write to `artifacts/store/…`; what is here is a copy
of the run that was read, filed in the same shape, so the gate runs over it
unchanged:

```
python3 tools/capture-gate/gate.py --store mac  docs/qa/captures-2.2/macos-screenshots --out /tmp/gate
python3 tools/capture-gate/gate.py --store play docs/qa/captures-2.2/play/phone      --out /tmp/gate
```

## What is here

| Folder | Set | Slots | Rig | Runbook |
|---|---|---|---|---|
| `macos-screenshots/<lang>/` | Mac App Store, 1440x900 | 7, `light-NN-<pose>.png` | `tools/macos-store-captures.sh` | `docs/qa/mac-store-captures.md` |
| `play/phone/<lang>/` | Google Play phone, 1080x2400 | 8, `android-<pose>.png` | **Android Screenshots** workflow | `docs/qa/android-store-captures.md` |

Languages are `en`, `fr-CA` and `fr-FR` throughout. (The Mac rig spells France's
`fr`, which is what App Store Connect calls that localisation; the folder is
named for it and the gate reads it as `fr-FR`.)

Each Mac language folder carries the run's own `RUN.txt`: the bundle, the
binary's sha256, the language, the window size, which demo document each slot
opened, and when it was taken.

**The French has not been read by a francophone.** That is a gate on store
submission, not on these captures (Dave, #343/#613).
