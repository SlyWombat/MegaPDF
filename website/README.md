# electricrv.ca/megapdf — site source

Deployed to `/public_html/megapdf/` on electricrv.ca via cPanel UAPI
(the login lives in the Lighting Arduino project's `.env` —
CPANEL_HOST/PORT/USER/TOKEN; upload client pattern: that project's
`server/deploy.py`). **It is on Dave's laptop and nowhere else**, so every other
machine is limited to `deploy.py --dry-run`.

- `megapdf/index.html` — landing page (linked from the main-page teaser)
- `megapdf/privacy/index.html` — privacy policy, the URL both app-store
  listings reference. **Update this file and redeploy BEFORE shipping any
  feature that collects data** (policy §9 promises that ordering).
- `icon.png` (512, from the iOS AppIcon)
- `screenshot-viewer.png`, `shot-*.png` — the gallery. Where each one comes
  from is below.
- `megapdf/screenshots/linux/` — the AppStream screenshots for the Flatpak
  (#254 A1). The metainfo points at
  `https://electricrv.ca/megapdf/screenshots/linux/…`, so **those URLs only
  resolve after a deploy**, and a Flathub submission has to follow one.
- `megapdf/linux/index.html` — how to install MegaPDF on Linux: the APT
  repository, the Snap Store, the .deb, the tarball, and how updates arrive for
  each (#158). **Only goes up with `--linux`.**
- `megapdf/apt/` — the APT repository that `https://electricrv.ca/megapdf/apt`
  serves. Committed: `megapdf.gpg` (the public signing key, for
  `/etc/apt/keyrings`), `megapdf.asc` (the same, readable), `megapdf.sources`
  (the deb822 entry), and `FINGERPRINT` (what `make-apt-repo.sh` checks the
  signing key against; not uploaded). Generated at release time and git-ignored:
  `dists/` and `pool/`. **Only goes up with `--linux`.**

The main-page teaser block (`.megapdf-teaser` CSS + section) lives in
`/public_html/index.html` on the server; a pre-edit backup was uploaded as
`/public_html/index.pre-megapdf-backup.html`.

## Deploying

```
/usr/bin/python3 website/deploy.py --dry-run     # lists every file and where it goes
/usr/bin/python3 website/deploy.py               # the landing page, images and screenshots/
/usr/bin/python3 website/deploy.py --privacy     # the same, plus privacy/
/usr/bin/python3 website/deploy.py --linux       # also linux/ and apt/, and every page links them
/usr/bin/python3 website/deploy.py --linux --snap  # ...and the Snap Store section on linux/
/usr/bin/python3 website/deploy.py --dry-run --privacy --dest /public_html/megapdf-preview
/usr/bin/python3 website/deploy.py --linux --privacy --only linux,apt,privacy,screenshots/linux \
    --landing live-index.html                    # only those parts, and this file as index.html
```

`--only` is for a partial release. Linux 2.0 (2026-09-19) went out before the
stores approved 2.0, so only the Linux page, the APT repository, the privacy
policy and the AppStream screenshots went up. The landing page stayed the live
1.x one, with a single "Install on Linux" link added, passed in with
`--landing`. The staged 2.0 landing and feature pages go up once the stores
approve 2.0: a plain `deploy.py --linux --privacy` then.

`--dry-run` touches nothing: it does not read the `.env`, does not open a
socket, and does not call the UAPI. It is the only mode that runs anywhere but
Dave's laptop, and it is worth running before every real deploy — it is the
only thing that tells you the byte count and the file list you are about to
overwrite.

`--dest` writes the same tree somewhere else, so a release can be looked at in a
real browser before it replaces the live page:

```
/usr/bin/python3 website/deploy.py --dest /public_html/megapdf-preview --privacy
# then open https://electricrv.ca/megapdf-preview/
```

Pass `--privacy` to a preview deploy even if the policy has not changed —
without it the preview's "Privacy policy" link is a 404 and you cannot check the
page you are previewing. The preview path is not linked from anywhere and is not
in `robots.txt`; it is a URL you know, not a secret. Delete it in cPanel's file
manager when you are done with it.

Subdirectories are walked, so `screenshots/linux/` goes up with everything else,
and a destination directory that does not exist yet is created a level at a
time. `--dry-run` names each directory it would create.

### Linux is gated

Linux ships after the other platforms, so the Linux parts of the site are held
back until a deploy says otherwise. Every page can carry gated regions:

```html
<!--linux:live-->what goes up with --linux<!--linux:soon what goes up without it linux:end-->
```

and `snap:` regions the same way for `--snap`. A browser opening the file
straight from the repository sees the live text (the "soon" text is inside a
comment), which is also what a `--linux` deploy uploads. **Without `--linux`**,
`linux/` and `apt/` stay off the server and every page goes up with its "soon"
text: the landing page's Linux chip is the dashed *Coming to Linux* and nothing
links a page that is not there. Today the regions are in `index.html` (both
meta descriptions, the Linux chip, the Linux gallery caption), `privacy/`
(§6: the Linux channels and the APT repository's server log) and `linux/`
(everything about the Snap Store).

**`--linux` refuses to start** unless `apt/` is a complete repository whose
`InRelease` and `Release.gpg` verify against `apt/megapdf.gpg`, and whose `.deb`
is the version `linux/index.html` offers. `--dry-run --linux` runs the same
checks, so it is the rehearsal.

## Where the gallery images come from

All of them are from the **2.0.0 capture sets** measured in
`docs/release-notes/2.0/capture-gate-report.md` — every set gate-clean and read
image by image. Nothing here is a mockup or a re-shoot for the website.

| File | Source | Size |
|---|---|---|
| `shot-viewer/text/search/sign/draw/home.png` | `gate-ios/en/listing/iphone-6_9-<state>.png` (1320×2868) | exactly a third: **440×956** |
| `screenshot-viewer.png` | `gate-ios/en/listing/iphone-6_9-viewer.png` | exactly a half: **660×1434** |
| `shot-desktop.png` | `gate-macos/en/light-05-redact.png` (1440×900) | exactly two-thirds: **960×600** |
| `screenshots/linux/{en,fr-CA,fr-FR}/*` | the Xvfb captures of #254 A1 (PR #255), 1280×800 | as captured |

The iPhone set is the App Store listing set, so its poses and the captions under
them are the ones in `docs/app-store-listing.md` § Screenshots. The desktop shot
is Mac slot #5 and carries that slot's caption. Windows would have done as well;
the Mac set is the one that is not only on Dave's laptop.

All eighteen Linux captures are staged, because the AppStream metainfo points at
every one of them per language. The gallery itself shows **one** —
`screenshots/linux/en/01-viewer.png`, beside the Mac shot — because the other
five are the same poses the iPhone row already shows. Referenced in place rather
than resized into a `shot-linux.png`, so there is one copy of each Linux capture
and part A stays the only thing that writes them.

Refresh them by re-running the capture sets (the **iOS Screenshots** workflow for
iPhone, `tools/macos-store-captures.sh` for the Mac) and resizing with
`convert <src> -filter Lanczos -resize <w>x<h>! -strip`.

## Store URLs, and the 200 check

A listing 404s until it is actually public, and no console field is a substitute:
the Play API cannot report Google's verdict, and a track status of `completed`
only describes the rollout. **The store URL returning 200 is the signal.**

```sh
for u in \
  https://apps.microsoft.com/detail/9PF4TRRH4M76 \
  https://apps.apple.com/app/id6799522972 \
  "https://apps.apple.com/app/id6799522972?platform=mac" \
  "https://play.google.com/store/apps/details?id=ca.electricrv.megapdf"
do
  printf '%s  %s\n' "$(curl -sS -o /dev/null -w '%{http_code}' -L --max-time 25 "$u")" "$u"
done
```

| Platform | URL | State |
|---|---|---|
| Windows | `https://apps.microsoft.com/detail/9PF4TRRH4M76` | live — public 2026-09-10 |
| iPhone / iPad | `https://apps.apple.com/app/id6799522972` | live — approved 2026-09-16 |
| Mac | `https://apps.apple.com/app/id6799522972?platform=mac` | live — approved 2026-09-16 |
| Android | `https://play.google.com/store/apps/details?id=ca.electricrv.megapdf` | live — public 2026-09-09 |
| Linux | `https://electricrv.ca/megapdf/linux/` | our own page: goes up with `--linux`, on Linux's own day (below) |

**Apple is one app ID for both platforms.** `6799522972` is the whole record;
`tools/asc_publish.py` switches platform with `ASC_PLATFORM=MAC_OS` against the
same `ASC_APP_ID`, and the live listing names iPhone, iPad *and* Mac
requirements on the one page. The `?platform=mac` parameter only decides which
tab the App Store app opens on — both URLs return 200 and both are the same
listing, so if one is ever broken, both are.

All four were checked at 200 on 2026-09-18, and each one really is this app:
the Microsoft listing is the reserved name "Mega PDF", Play reads 1.2.0, and
Apple's own lookup API puts the record at iOS 1.7.0. (The Microsoft Store title
keeps the space because the *reservation* does — `tools/Store-Submission.md`.
The product is MegaPDF everywhere else, this site included.)

## Before 2.0: the App Store link on its own

The live page still says the iPhone/iPad app is "in testing now" and keeps the
App Store link commented out. That has been untrue since **2026-09-16**, when
iOS 1.7.0 and Mac 1.7.1 were approved. Turning it on is a one-file change to
`index.html` and one deploy, and it does not wait for 2.0. It is on branch
`website/254-app-store-link-now` — pushed, deliberately **not** merged, and
deliberately not part of the 2.0 staging commits.

```
git checkout website/254-app-store-link-now
/usr/bin/python3 website/deploy.py --dry-run
/usr/bin/python3 website/deploy.py
```

Check `https://electricrv.ca/megapdf/` afterwards: the Apple chips appear, the
"in testing now" line is gone.

**Before the 2.0 deploy, never after.** That branch carries the 1.x page and the
1.x gallery on purpose — deploying it once 2.0 is live would put the old page
back. After the 2.0 deploy it has no use and can be deleted.

## Launch runbook — 2.0

The site is the one thing here that must never lead. Every store link on the
page 404s until that listing is public, so **the deploy follows the go-lives; it
never precedes them.** Nothing on this page is time-sensitive to the minute:
if the stores go public at different times on different days, deploy when the
last one you are linking to is up, or deploy twice.

**Order, on the day:**

1. **The store go-lives happen first** — Microsoft Store, App Store (iOS), Mac
   App Store, Google Play. Each has its own runbook
   (`tools/Store-Submission.md`, `docs/app-store-listing.md`,
   `android/RELEASING.md`); none of them is this file's business.
2. **Run the 200 check above.** Every URL you are about to link must return 200
   *and* show 2.0. Apple's two URLs go live together, because they are one
   record. Google Play's rollout can be at 100 % in the console and still be
   propagating — the URL is the truth.
3. **Preview, if you want one** (~2 minutes):
   `deploy.py --dry-run --privacy --dest /public_html/megapdf-preview`, then the
   same without `--dry-run`, then open
   `https://electricrv.ca/megapdf-preview/` and click every chip.
4. **Deploy the privacy policy with the site**, in the same command:
   `deploy.py --privacy`. The 2.0 revision is what makes the page true for
   macOS, Linux, redaction and document protection; the old one names three
   platforms. Both store listings point at this URL, so it should be right
   before a reviewer follows it, not after.
5. **Check the live page**: `https://electricrv.ca/megapdf/` — the five
   platform chips, the "New in 2.0" cards, the gallery, and the footer's privacy
   link. Then `https://electricrv.ca/megapdf/privacy/` and confirm the header
   reads *Effective 18 September 2026*. If 2.0 lands a long way past that date,
   bump the date in the file and redeploy rather than shipping a stale one.
6. **Tear down the preview**, if you made one.

**Linux, afterwards, not on the day.** The 2.0 deploy is made *without*
`--linux`, so the Linux chip says *Coming to Linux* and no Linux page or
repository goes up. `screenshots/linux/` still goes up with it like any other
file, which is what makes the AppStream `<screenshot>` URLs resolve. Linux's own
day is its own runbook, `tools/Linux-Packaging.md` § "Going live": publish the
draft `linux-v*` release, put the release's signed repository into `apt/`,
`deploy.py --dry-run --linux`, then `deploy.py --linux --privacy`. Add `--snap`
once the Snap Store listing is public.

**Rolling back** is a redeploy of the previous commit: `git checkout <sha> --
website/` then `deploy.py`. Uploads overwrite; nothing is versioned on the
server.
