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
python3 tools/capture-gate/gate.py --store ios  docs/qa/captures-2.2/ios-screenshots  --out /tmp/gate
python3 tools/capture-gate/gate.py --store microsoft docs/qa/captures-2.2/microsoft-screenshots --out /tmp/gate
```

The Microsoft set arrived here last, after 2.2.0 had shipped: it was the one 2.2
set the repository never held, because the rig writes to `artifacts/store/screenshots/`
and that path is gitignored. A post-release cleanup is what surfaced it — the
images live on the Store right now and the only copy was on one machine's disk.
Its `<lang>/work/` folders are deliberately **not** here: they are the rig's
working frames, including poses that are shot every run but are not listing
slots, and the stray demo PDFs the run opened.

## What is here

| Folder | Set | Slots | Rig | Runbook |
|---|---|---|---|---|
| `macos-screenshots/<lang>/` | Mac App Store, 1440x900 | 7, `light-NN-<pose>.png` | `tools/macos-store-captures.sh` | `docs/qa/mac-store-captures.md` |
| `play/phone/<lang>/` | Google Play phone, 1080x2400 | 8, `android-<pose>.png` | **Android Screenshots** workflow | `docs/qa/android-store-captures.md` |
| `ios-screenshots/<lang>/listing/` | App Store (iOS), two devices | 9 × 2, `<device>-<pose>.png` | `tools/ios-screenshots.sh` | `docs/app-store-listing.md` § Screenshots |
| `microsoft-screenshots/<lang>/` | Microsoft Store | 8, `NN-<pose>.png` | `tools/screenshots-windows/Shoot-Set.ps1` | `docs/microsoft-store-listing.md` § Screenshots |

Languages are `en`, `fr-CA` and `fr-FR` throughout. (The Mac rig spells France's
`fr`, which is what App Store Connect calls that localisation; the folder is
named for it and the gate reads it as `fr-FR`. The iOS rig does the same.)

Each Mac language folder carries the run's own `RUN.txt`: the bundle, the
binary's sha256, the language, the window size, which demo document each slot
opened, and when it was taken. `ios-screenshots/<lang>/listing/` carries a
`.out.log`/`.err.log` pair beside every PNG instead — what
`ViewerModel.applyScreenshotModeIfNeeded` printed on that launch, which is also
what `tools/ios-screenshots.sh` itself checked before trusting the image: a
`reading` or `pages` shot whose mode did not actually turn on fails the run
rather than being filed quietly.

**The French has not been read by a francophone.** That is a gate on store
submission, not on these captures (Dave, #343/#613).

**The iOS iPhone and iPad sets were not shot in the same pass.** The original
three-locale run crossed midnight between `en`/`fr-CA` and `fr`, and the iPad's
date indicator — the one piece of its posed status bar `simctl status_bar`
cannot override — came back a calendar day apart, which the gate's
cross-language comparison caught on all nine `fr-FR` iPad poses (a 30% status-bar
difference against `en`'s). Re-shooting just the iPad for `fr` did not fix it:
real time had already moved past midnight, so it only produced a *third* date.
The iPad set for all three locales was re-shot together instead, back to back,
so all three land on the same day; the iPhone set is untouched from the
original run. Both runs are reflected in the committed files' timestamps —
`iphone-6_9-*` predates `ipad-13-*` by roughly 45 minutes in every language
folder — and the gate is clean over the result (`0 image(s) to look at` in all
three languages).
