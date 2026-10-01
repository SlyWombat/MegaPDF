# electricrv.ca/megapdf — site source

Deployed to `/public_html/megapdf/` on electricrv.ca via cPanel UAPI
(the login lives in the Lighting Arduino project's `.env` —
CPANEL_HOST/PORT/USER/TOKEN; upload client pattern: that project's
`server/deploy.py`). **It is on Dave's laptop and nowhere else**, so every other
machine is limited to `deploy.py --dry-run`.

- `megapdf/index.html` — landing page (linked from the main-page teaser)
- `megapdf/privacy/index.html` — privacy policy, the URL both app-store
  listings reference. **Update this file and redeploy BEFORE shipping any
  feature that collects data** (policy §11 promises that ordering — it was §9
  until the page-view counter added §3 and the support page added §4).
- `megapdf/support/index.html` — the support page (#418): the known answers, a
  browser-side matcher over them, and a composer that hands a message to the
  reader's own email program. **Only goes up with `--support`, which itself
  refuses to run without `--privacy`.** Policy §4 is its disclosure.
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
/usr/bin/python3 website/deploy.py --support --privacy   # also support/ — needs --privacy
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

and `snap:` regions the same way for `--snap`, and `support:` regions for
`--support`. A browser opening the file straight from the repository sees the
live text (the "soon" text is inside a comment), which is also what a `--linux`
deploy uploads. **Without `--linux`**, `linux/` and `apt/` stay off the server
and every page goes up with its "soon" text: the landing page's Linux chip is
the dashed *Coming to Linux* and nothing links a page that is not there. Today
the regions are in `index.html` (both meta descriptions, the Linux chip, the
Linux gallery caption), `privacy/` (§8: the Linux channels and the APT
repository's server log) and `linux/` (everything about the Snap Store).

### The support page is gated too, and it drags the policy with it

`support/` goes up only with `--support`, and `--support` **refuses to start
without `--privacy`**. The support page is the page that composes a message to
us, so the policy section describing what happens to that message has to be part
of the same upload — the ordering §11 promises and the one the page-view counter
followed (#501). `--support` also refuses unless the policy it is about to
upload really carries that section (an `id="support"` heading, the anchor the
support page links), so a policy edited back to its old text cannot slip through.

`--dry-run --support --privacy` runs the same checks, so it is the rehearsal.
Besides the policy, it validates the answers list, because that list is the one
copy of every answer — the page's matcher reads those very elements, so an
attribute the matcher cannot read is an answer nobody is ever shown:

- every `<details class="answer">` has an `id` (the composed message names
  answers by it), a `<summary>` and a `.body`;
- `data-plat` is present and names only `all`, `windows`, `mac`, `ios`,
  `android`, `linux` — and `all` is not mixed with named platforms;
- `data-match` has at least one term, and no term contains a capital or a symbol,
  because the matcher lowercases the reader's text and strips everything that is
  not a letter or a digit before looking for it;
- ids are unique, there are at least five answers, the page names
  `info@electricrv.ca`, and nothing in it links `../linux/` outside a
  `linux:` region (which would 404 on a deploy without `--linux`).

It also refuses a support page that could send what someone typed — a `<form>`, a
`fetch(`, `XMLHttpRequest`, `sendBeacon`, a `WebSocket`, a second external script
or an origin the policy does not account for — checked on the bytes that would go
up, both with Linux live and with it held back. Policy §4 promises the page cannot
do any of that, so the deploy is where that promise is kept rather than a thing to
notice in a diff later. If it ever *should* change, the policy changes first and
this check changes with it.

`support:` regions currently appear in `index.html` (the support strip above the
footer, and the footer link), `privacy/` (§4, where the page's URL is a link only
once the page is up, so the policy can go first) and `linux/` (the footer link).

### Checking the triage itself

`website/check-support-page.js` drives the page in a real DOM and checks the
matcher's behaviour, which no static check can see: that one ordinary word
surfaces nothing, that a sentence someone would actually type surfaces the right
answer, that a desktop-only answer is withheld on a phone, that "Not it" and
"That was it" are recorded in the composed message, and that every answer is
reachable by its own terms. **Run it by hand after editing the page or its
answers** — it found three real faults in the first draft:

```sh
npm install jsdom      # once
node website/check-support-page.js
```

Not in CI, on purpose: the site has no build and nothing else here needs node.
`deploy.py --dry-run --support --privacy` is the check that always runs.

**The page is deliberately not the whole of #418.** It runs its triage in the
reader's browser and posts nothing anywhere; there is no intake endpoint and no
automatic ticket. What it is and is not is written down in the scope comment on
#418, and the answers themselves were checked against the app's code rather than
against a listing. When an answer stops being true, it is wrong on a live page —
so treat `support/index.html` like the Linux page's version numbers: part of a
release, not set-and-forget.

**`--linux` refuses to start** unless `apt/` is a complete repository whose
`InRelease` and `Release.gpg` verify against `apt/megapdf.gpg`, and whose `.deb`
is the version `linux/index.html` offers. `--dry-run --linux` runs the same
checks, so it is the rehearsal.

## Where the gallery images come from

All of them are from the **2.1.1 capture sets** (#395): the store sets in
`artifacts/store/captures-2.1.1/` (gitignored, on Dave's laptop; read image by
image on 2026-09-26) and a Linux run of the same day. Nothing here is a mockup
or a re-shoot for the website. The 2.0 images they replace came from the 2.0.0
sets measured in `docs/release-notes/2.0/capture-gate-report.md`; every desktop
one went stale with the 2.1 tab strip (#348).

| File | Source | Size |
|---|---|---|
| `shot-viewer/text/search/sign/draw/home.png` | `captures-2.1.1/ios-screenshots/en/iphone-6_9-<state>.png` (1320×2868) | exactly a third: **440×956** |
| `screenshot-viewer.png` | `captures-2.1.1/ios-screenshots/en/iphone-6_9-viewer.png` | exactly a half: **660×1434** |
| `shot-desktop.png` | `captures-2.1.1/macos-screenshots/en/light-05-redact.png` (1440×900) | exactly two-thirds: **960×600** |
| `screenshots/linux/{en,fr-CA,fr-FR}/02-text, 03-sign, 05-search, 06-redact, 07-home` | `tools/linux/store-captures.sh` on 2026-10-01 (#310), from a `tools/build-linux-app.sh` build of main at `47c6e79`, 1280×800. Byte-for-byte the images #310 shot — the 2026-10-01 re-shoot for #613 reproduced all five exactly and only renumbered them | as captured |
| `screenshots/linux/{en,fr-CA,fr-FR}/01-reading.png, 04-pages.png` | the same script and the same build, with the two slots #613 added: reading mode on the six-page demo agreement, and the Pages sidebar with two pages selected | as captured |

The iPhone set is the App Store listing set, so its poses and the captions under
them are the ones in `docs/app-store-listing.md` § Screenshots. The desktop shot
is Mac slot #5 and carries that slot's caption: since 2.1 the pose is the mark
*selected*, with the ✕ that removes it at its corner, under the document's tab.
Windows would have done as well; the Mac set is the one that is not only on
Dave's laptop.

All twenty-one Linux captures are staged, because the AppStream metainfo points
at every one of them per language. The gallery itself shows **one** —
`screenshots/linux/en/04-pages.png`, beside the Mac shot — because the rest are
the same poses the iPhone row already shows. It was `01-viewer.png` until #613
dropped that slot; the page-tools shot took its place because it is the one new
slot that still shows the whole window, and the gallery's job there is "the same
app on Linux". Referenced in place rather than resized into a `shot-linux.png`,
so there is one copy of each Linux capture and part A stays the only thing that
writes them.

Refresh them by re-running the capture sets (the **iOS Screenshots** workflow for
iPhone, `tools/macos-store-captures.sh` for the Mac, `tools/linux/store-captures.sh`
per language for Linux) and resizing with
`convert <src> -filter Lanczos -resize <w>x<h>! -strip`. The Linux captures are
copied in as they are. Render the pages afterwards the way
`docs/release-notes/2.1.1/website-renders/` was made (headless Chromium at 1280
and 390, the 390 render tiled into 1300 px columns) and look at them.

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

## Store badges

The landing page links each store with that store's own badge, vendored in
`megapdf/badges/` exactly as downloaded (Dave, 2026-09-19: "the website should
be using the logos for the respective stores"). Don't recolour, crop, restyle or
re-export them. To refresh one, download it again from the source below.

| File | Source | Guideline followed |
|---|---|---|
| `download-on-the-app-store.svg` | `https://toolbox.marketingtools.apple.com/api/v2/badges/download-on-the-app-store/black/en-us` (identical to `developer.apple.com/assets/elements/badges/download-on-the-app-store.svg`) | Apple App Store Marketing Guidelines: the black badge, unaltered, at least 40px tall, with clear space of a quarter of its height |
| `download-on-the-mac-app-store.svg` | `https://toolbox.marketingtools.apple.com/api/v2/badges/download-on-the-mac-app-store/black/en-us` | the same Apple guidelines |
| `get-it-on-google-play.png` | `https://play.google.com/intl/en_us/badges/static/images/badges/en_badge_web_generic.png` | Google Play badge guidelines (play.google.com/intl/en_us/badges/): the PNG carries its own transparent clear space (41 of 250px on every side), and the badge must be at least as large as the other stores' badges. The CSS draws it larger and pulls it in by exactly that padding, so the visible badge matches the others' 48px height without cropping anything |
| `get-it-from-microsoft.svg` | `https://get.microsoft.com/images/en-us%20dark.svg` (the badge generator at get.microsoft.com) | Microsoft Store badge guidelines: the dark badge ("Download from the Microsoft Store"), unaltered |
| `get-it-from-the-snap-store.svg` | `https://snapcraft.io/static/images/badges/en/snap-store-black.svg` | Snap Store brand guidelines (snapcraft.io/docs/snap-store-brand-guidelines). It's used only in the Linux page's Snap section, which goes live with `--snap` (#314). Its SVG has no viewBox, so it's shown at its own 182×56 |

The site has a single dark theme, so every badge is the black version (Apple's
and Google's black badges carry a grey keyline made for dark backgrounds).
Every badge is 48px tall and 16px apart. The Linux link has no store badge: no
store sells it. It's a plain black button of the same height and shape. The
Tux mark is left out because its licence asks for attribution on request.
Badges keep their own look on hover, and keyboard focus shows as an outline
around the link.

## Deployed: 2.0, 2026-09-19

The full 2.0 site went up on 2026-09-19 at 14:25 EDT with
`deploy.py --linux --privacy` (41 files), after Google Play 2.0.1 and Linux
2.0.0-2 were live and while Apple and Microsoft 2.0 were still in review (Dave:
"update website completely"). The APT repository was re-uploaded byte for byte
(it holds 2.0.0 and 2.0.0-2), and the Snap section is still held back (#314).
It stays held back until the snap reaches the **stable** channel: as of 2026-09-28 the
store has 2.1.1 on edge only, and the section's install line is `snap install megapdf`
with no channel flag, which would fail for every reader.
The `website/254-app-store-link-now` branch this section used to describe
had no use after this deploy and was deleted.

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
   reads *Effective 19 September 2026*. If 2.0 lands a long way past that date,
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
