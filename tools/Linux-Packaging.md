# Linux packaging and the Flathub submission (#158)

What exists, how to build it, and what is still Dave's to decide before MegaPDF can be
submitted anywhere. Companion to `tools/Store-Submission.md`, which covers the other
three stores.

**Nothing here has been submitted, uploaded or registered.** No Flathub account, no app
ID reserved, no repository requested. Everything below was built and run on kdocker2 and
runs again in CI on every push.

---

## What is in the tree

| | |
|---|---|
| `tools/build-linux-app.sh` | the self-contained publish: apphost, both native libraries, desktop entry, icons, licences, notices, install/uninstall scripts. Everything else wraps this. |
| `tools/linux/build-flatpak.sh` | the Flatpak, from that tree. Validates the desktop entry and the metainfo first, exports an ostree repo, writes a single-file bundle. |
| `tools/linux/build-deb.sh` | the `.deb`, from that tree. For GitHub Releases. |
| `tools/linux/package-check.sh` | what any package must be true of. Run against whatever a package installed. |
| `tools/linux/check-flatpak.sh` | installs the bundle and drives the app inside the sandbox. |
| `tools/linux/check-deb.sh` | installs the `.deb` on a bare machine, runs the app out of it, removes it again. |
| `tools/linux/flatpak/ca.electricrv.MegaPDF.yml` | the manifest. |
| `tools/linux/flatpak/ca.electricrv.MegaPDF.metainfo.xml` | the AppStream data a software centre shows. |
| `tools/linux/store-captures.sh` | the six listing screenshots the metainfo points at, one language per run, under its own Xvfb. |
| `tools/linux/check-metainfo.sh` | the listing, through both the tools a Flathub reviewer runs: `appstreamcli validate` and `flatpak-builder-lint`. In CI on every push. |
| `tools/linux/make-release-tarball.sh` | `megapdf-linux-x64-<ver>.tar.gz` and its sha256 — the archive the Flathub manifest fetches. |
| `tools/linux/flatpak/flathub/…yml.in` | the manifest as Flathub would build it, with the `sources:` block left to be filled in. |
| `tools/linux/make-flathub-manifest.sh` | fills it in, from a tarball's URL and checksum (or its path, for a dry run). |
| `tools/linux/build-flathub-flatpak.sh` | builds *that* manifest, so the one Flathub runs is the one that has been run. |
| `tools/linux/qa/` | the desktop-session rigs: a headless session with a named portal backend, the file-dialog check, and the recent-document check inside and outside the sandbox. |

CI builds both on every push (`linux-package` in `ci.yml`) and attaches them to the run
as `MegaPDF-linux-packages`.

## Building them by hand

```sh
tools/build-linux-app.sh linux-x64 artifacts/linux
python3 tools/gen_test_fixtures.py artifacts/fixtures
python3 tools/gen_redaction_fixtures.py artifacts/fixtures

tools/linux/build-deb.sh                 # -> artifacts/deb/megapdf_<version>_amd64.deb
sudo tools/linux/check-deb.sh            # installs it, runs it, removes it

tools/linux/build-flatpak.sh             # -> artifacts/flatpak/ca.electricrv.MegaPDF.flatpak
tools/linux/check-flatpak.sh             # installs it, runs the app in the sandbox

tools/linux/check-metainfo.sh            # the listing, through both validators
```

And the release side of it — what the tag build does, by hand:

```sh
tools/linux/make-release-tarball.sh                      # -> artifacts/release/megapdf-linux-x64-<ver>.tar.gz
T=artifacts/release/megapdf-linux-x64-*.tar.gz
tools/linux/make-flathub-manifest.sh $T                  # the manifest for the Flathub PR
tools/linux/make-flathub-manifest.sh $T --local \
    artifacts/release/local.yml                          # the same, pointed at the file
tools/linux/build-flathub-flatpak.sh artifacts/release/local.yml
tools/linux/check-flatpak.sh artifacts/flathub/ca.electricrv.MegaPDF.flatpak artifacts/fixtures
```

`check-metainfo.sh` needs Flathub's own linter, which is a flatpak:

```sh
flatpak install --user flathub org.flatpak.Builder
```

The listing screenshots, which are not part of either package but are what the metainfo
in both of them points at:

```sh
for l in en fr-CA fr-FR; do tools/linux/store-captures.sh "$l"; done
python3 tools/capture-gate/gate.py --store linux artifacts/store/linux -o /tmp/gate
cp artifacts/store/linux/en/*.png website/megapdf/screenshots/linux/en/          # and the two French
```

Needs `flatpak flatpak-builder appstream desktop-file-utils dpkg-dev librsvg2-common
xvfb dbus`, and the runtime the manifest names:

```sh
flatpak remote-add --if-not-exists --user flathub https://dl.flathub.org/repo/flathub.flatpakrepo
flatpak install --user flathub org.freedesktop.Platform//25.08 org.freedesktop.Sdk//25.08
```

### In a container

`flatpak build` and `flatpak run` both go through bubblewrap, which needs user
namespaces, and Docker does not give a container those by default. On a host with
`kernel.apparmor_restrict_unprivileged_userns=1` — Ubuntu 24.04's default — it takes all
three of these, and the container has to run as root, because a capability added to a
container is not in an unprivileged user's permitted set:

```sh
docker run --security-opt seccomp=unconfined --security-opt apparmor=unconfined \
           --cap-add SYS_ADMIN --cap-add NET_ADMIN ...
```

`NET_ADMIN` is the non-obvious one: without it the build fails at the first build
command with `bwrap: loopback: Failed RTM_NEWADDR`, because bubblewrap brings up a
loopback interface in the network namespace it just made.

This is less than `--privileged` but it is not nothing, which is a reason to prefer CI
for the Flatpak: a GitHub runner is a VM and needs none of it.

### Three things that cost an hour each to find

- **`librsvg2-common` on the build machine.** flatpak-builder runs `appstreamcli compose`
  from the machine, not from inside the sandbox, and that reads the scalable icon through
  gdk-pixbuf. Without the SVG loader the build fails at its very last step with
  `Unrecognized image file format`, naming a file that is a perfectly good SVG.
  `build-flatpak.sh` warns for this before it spends the build.
- **flatpak-builder's state directory** defaults to beside the working directory and it
  refuses to run when that is on another filesystem from the output. `--state-dir` is
  passed for you.
- **`flatpak run` needs a session bus *and* a system bus.** Without the second it stops
  with `Could not connect: No such file or directory` and names nothing at all.
  `check-flatpak.sh` starts both.

---

## Decisions already made, and why

**App ID: `ca.electricrv.MegaPDF`.** Flathub requires an ID based on a domain or code
hosting the publisher controls. `electricrv.ca` is where the product page is deployed
from (`website/deploy.py`), the privacy policy the other two stores point at is on it,
and Android already ships as `ca.electricrv.megapdf`. **Dave confirms this before
anything is reserved** — see the checklist.

**Runtime: `org.freedesktop.Platform` 25.08.** freedesktop rather than GNOME or KDE
because MegaPDF draws its own interface with Skia through Avalonia and would never load
a library from either; theirs would be a few hundred megabytes of nothing. 25.08 rather
than 26.08 because it is the current stable series with a year behind it, and rather
than 24.08 because that is close enough to end-of-life not to start on. The bump is two
lines in the manifest.

**Permissions: `--socket=x11 --share=ipc --device=dri`, and nothing else.**

- No `--filesystem` of any kind. Opening and saving go through
  `org.freedesktop.portal.FileChooser`, which needs no permission, and which gives the
  app exactly the file the person picked. Verified: a file in the real home is not
  readable from inside the sandbox.
- No `--share=network`. MegaPDF makes no network connection, and leaving the permission
  out is what makes that a fact about the package rather than a claim in the listing.
- No `--socket=wayland`. `Avalonia.X11` is the only Linux windowing backend in the
  published output, so a Wayland session runs through XWayland. Adding the socket would
  advertise support that is not there.

**The published tree goes in one piece** under `/app/lib/megapdf` (Flatpak) and
`/opt/MegaPDF` (`.deb`). The apphost finds `libmegapdf_core.so` and `libpdfium.so`
beside itself; splitting it the way a distribution package would is the one thing that
breaks it.

**`.deb` rather than AppImage.** `dpkg-deb --build` needs no privileges, no FUSE and no
tool that is not already on a build machine; appimagetool wants all three. And it is
what someone who has just downloaded a file from a Releases page will double click.

---

## Two things a packager must not do

1. **Do not unbundle PDFium.** The app carries its own build with 25 MegaPDF patches
   (`libs/pdfium/RELEASE`). A distribution's `libpdfium` is a different renderer, and
   every corpus result the project has is against ours. `package-check.sh` asserts the
   runpath of `libmegapdf_core.so` is `$ORIGIN` **and nothing else** — a build machine's
   own directory in there would load the wrong engine on a machine where that path
   happened to exist, which is how a build path shipped in the first place (fixed, #158).
2. **Do not self-extract the native libraries.** `PublishSingleFile` without
   `IncludeNativeLibrariesForSelfExtract`: extracting `libpdfium.so` into a temp
   directory on first run fails outright where `/tmp` is mounted `noexec`, with an error
   that names neither the file nor the mount.

## Where the third-party notices have to live

**Beside the binary, not only in `/usr/share/doc`** (#194). Debian and Ubuntu ship dpkg
configurations with `path-exclude=/usr/share/doc/*` — every Docker image of either does,
and so do minimal installs — so a package whose notices live only there has them
discarded on install while dpkg reports success. That was found by installing this
`.deb` on a bare machine. Several of the licences require the text to travel with the
binary, so both packages carry a copy beside the app and a second one under `share/doc`
for whoever looks where the convention says to. `copyright` survives the exclusion
because the same configurations path-include it.

The app itself never depends on either copy: the notices are embedded in the assembly
(`avares://MegaPDF/Assets/THIRD-PARTY-NOTICES.txt`), so Help reaches them however the
app was installed.

## What lintian says about the .deb, and why each answer is "yes, on purpose"

CI reports lintian and does not enforce it, because most of what it says follows from
this being a bundled third-party package rather than one for the Debian archive.

| Tag | Answer |
|---|---|
| `dir-or-file-in-opt` | Deliberate. FHS puts add-on software in `/opt/<provider>`, and the tree cannot be split. |
| `embedded-library` (freetype, libpng, libjpeg, expat, lcms2, openjpeg) | Deliberate. They are inside PDFium and SkiaSharp, which are upstream binaries; unbundling them means building both from source, and for PDFium it means losing the patches. |
| `unstripped-binary-or-object` | Deliberate. Stripping the engine would ship a binary that is not the one the corpus batteries ran against. |
| `no-changelog` | Not a Debian-archive package; the release notes are in `docs/release-notes/`. |
| `copyright-file-contains-full-apache-2-license` | The `copyright` file is the project's `LICENSE` verbatim, which is the honest thing for a package that is not in the archive to ship. |
| `no-manual-page` | Fair. There is no man page. |
| `custom-library-search-path` | **This one was real** and is fixed: the shipped `libmegapdf_core.so` carried the build machine's own directory in its runpath. |

---

## Before a Flathub submission: what is needed from Dave

Flathub is a pull request against `flathub/flathub`, reviewed by people. None of the
following can be decided here.

1. **Confirm the app ID** `ca.electricrv.MegaPDF`, and that `electricrv.ca` will still
   be the product's domain. It cannot be changed after publication without republishing
   as a new app.
2. **A Flathub account** (a GitHub account, added to the Flathub organisation on
   acceptance) and the decision to submit at all.
3. **Screenshots — taken; the site deploy is what is left.** The metainfo now points at
   eighteen Linux captures of this app: the six listing slots the Mac listing uses
   (viewer, text, search, sign, redact, home) at 1280x800, in en, fr-CA and fr-FR.
   `tools/linux/store-captures.sh <lang>` shoots a language in one command, under its own
   Xvfb, and `tools/capture-gate/gate.py` with the `linux` store profile reviews the
   result. The files are staged in this repo under
   `website/megapdf/screenshots/linux/<lang>/`.

   **They have to be deployed to electricrv.ca before a submission, not after.** AppStream
   screenshots are URLs, and both Flathub's linter and `appstreamcli validate` fetch every
   one of them: run today, validation fails with eighteen `screenshot-image-not-found`
   warnings, and passes with `--no-net`. Two things have to happen first, in this order:

   - `website/deploy.py` has to upload them, which it now does: it walks subdirectories
     as of #254 B6, so `screenshots/linux/` goes up with the rest of the page. It did not
     when these captures were staged, and would have uploaded nothing at all.
   - Then the site has to actually be deployed, which is Dave's call under the release
     hold (#146). `deploy.py --dry-run` lists what would go where without contacting
     anything; the eighteen screenshots should be in that list. The exact check
     afterwards, from any machine:

     ```sh
     for l in en fr-CA fr-FR; do for s in 01-viewer 02-text 03-search 04-sign 05-redact 06-home; do
       printf '%s/%s ' "$l" "$s"
       curl -s -o /dev/null -w '%{http_code}\n' \
         "https://electricrv.ca/megapdf/screenshots/linux/$l/$s.png"
     done; done
     ```

     Eighteen `200`s, and then `appstreamcli validate --pedantic` (no `--no-net`) passes
     on its own.
4. **The summary and description — rewritten for this platform; the French needs a
   reader.** They were adapted from the App Store copy in `docs/app-store-listing.md`,
   which talks about tapping, about "your device", and about opening files from Mail and
   iCloud Drive. They now describe this package on this machine: the form work, the text
   correction, redaction, protected documents, very large files, printing through the
   desktop's own dialogue, and the two permissions the package does *not* ask for. No
   sentence claims anything about another platform.

   **What is still owed:** the French `<summary>`, `<description>` and screenshot
   captions are new copy, written here rather than taken from anything already approved.
   They belong in the same francophone review as the rest of the 2.0 listing copy
   (#146 §2, `docs/release-notes/2.0/README.md`) and should go to the same reviewer.
   The English is the version to trust until they do.
5. **A release history — present, and honest about what it is.** `<releases>` now runs
   2.0.0, 1.7.0, 1.6.2, 1.5.0, 1.4.0, 1.3.0. Only 2.0.0 carries a description, taken
   from the approved 2.0 copy in `docs/release-notes/2.0/`; the earlier entries carry a
   version and the date of their tag in this repository, because the repository keeps no
   notes for them and inventing some is worse than a bare entry. The list is the
   product's history, not a claim that those versions ran on Linux — 2.0.0 is the first
   MegaPDF built for it, and its own description says so.

   **2.0.0's date is the day the packaging and the 2.0 copy were finished, not a release
   date.** Both the version and the date have to be right when 2.0 is tagged; the tag
   build is where that is checked, so it is not left to whoever remembers.
6. **Which manifest — written, and built from.** Flathub builds from a manifest in its
   own repository, which can reach nothing of ours except by URL. That manifest is
   generated rather than kept as a second copy to drift:
   `tools/linux/flatpak/flathub/ca.electricrv.MegaPDF.yml.in` is the template, and
   `tools/linux/make-flathub-manifest.sh` fills its one `sources:` entry in from a
   release tarball's URL and sha256.

   **One source and one checksum**, because the tarball carries the launcher, the desktop
   entry and the metainfo inside it under `flatpak/`. A manifest with four sources is
   four things to get right at release time and four things to notice when one is stale;
   this way, what Flathub builds is provably the listing this repository reviewed.

   The tag build (`.github/workflows/linux-release.yml`) makes the tarball, generates
   both the real manifest and a copy pointed at the file on disk, **builds the Flatpak
   from that copy**, and runs `check-flatpak.sh` against the result — so the manifest a
   reviewer will run is one that has been run. On a tag it attaches the tarball and its
   checksum to that release; `workflow_dispatch` does everything except the attaching.

   What is still a person's step: **opening the pull request against `flathub/flathub`**
   with the generated manifest. Nothing here does that, and nothing here pushes a tag.

## Before a Flathub submission: what is needed from the code

1. **Printing goes through the portal inside the sandbox** (`Platform/PortalPrinter.cs`).
   `LinuxPrinter` still shells out to `lp` outside it; inside, the app hands
   `org.freedesktop.portal.Print` a file descriptor and the desktop shows its own print
   dialog. Nothing is added to `finish-args` for it — every Flatpak may talk to
   `org.freedesktop.portal.Desktop` — and the app's own printer dialog is skipped there,
   because two dialogs asking the same question is worse than one.

   **What has been proved, and where it stops.** In a container with a session bus and a
   real `xdg-desktop-portal` behind `xdg-desktop-portal-gtk`: the app reads the Print
   interface's version (1), hands over a PDF, the portal takes the descriptor and opens
   its dialog titled after the job, and the app receives the `Response` signal on exactly
   the path it predicted. What a machine cannot do is *use* that dialog — choose a
   printer and press Print — so the one outcome never yet seen from a terminal is a
   successful print, response `0`. Run it by hand once on a desktop with a printer:

   ```
   flatpak run ca.electricrv.MegaPDF --print-check                 # which portal is there
   flatpak run ca.electricrv.MegaPDF --portal-print-check doc.pdf  # hand one over
   ```

   `--portal-print-check` prints the stage it reached: `Accepted` means the portal took
   the document and the dialog is open, which is everything the app is responsible for.
2. **The file dialogs go through the portal. Confirmed, from a terminal.** This used to
   say the choice "is only observable when a dialog opens, and no headless check can tell
   them apart". It is observable twice over without anyone touching a mouse: a portal
   dialog is a method call on the session bus, and Avalonia's own fallback makes no
   D-Bus call at all.

   ```sh
   tools/linux/qa/filechooser-check.sh gtk artifacts/linux/MegaPDF     # or gnome, or kde
   ```

   brings up Xvfb, a window manager, `xdg-desktop-portal` and the backend named, opens
   the app on a document, presses Ctrl+O and Ctrl+Shift+S into it, and reads
   `dbus-monitor`. Measured on 2026-09-18, with both the GTK and the KDE backends:

   ```
   interface=org.freedesktop.portal.FileChooser; member=OpenFile
   interface=org.freedesktop.impl.portal.FileChooser; member=OpenFile
   interface=org.freedesktop.portal.FileChooser; member=SaveFile
   interface=org.freedesktop.impl.portal.FileChooser; member=SaveFile
   ```

   The app calls the portal, the portal hands it to the backend, and the app gets its
   `Response` on the path it predicted.

   **What a machine still cannot do is work the dialog.** The one thing left for a person
   is therefore not "which dialog is it" — that is answered — but "the dialog appears,
   a file can be picked in it, and the app opens what came back". On a desktop with a
   GNOME or KDE session:

   ```
   flatpak run ca.electricrv.MegaPDF        # then Ctrl+O, pick a PDF, then Ctrl+Shift+S
   ```

   The dialog that opens should be the desktop's own, not one drawn by the app.

   One container finding worth keeping: `xdg-desktop-portal-gnome` 46 advertises
   `org.freedesktop.impl.portal.FileChooser` through the deprecated `UseIn` key and then
   answers `No such interface`, because its FileChooser needs a GNOME session behind it.
   The call and the response are the app's side and are unaffected; the dialog is what
   does not appear. The GTK and KDE backends implement it in the container.
3. **A recent document does reopen across a restart.** This used to say it would not. It
   does, and the check is one command:

   ```sh
   tools/linux/qa/recent-check.sh artifacts/linux/MegaPDF           # outside the sandbox
   tools/linux/qa/recent-sandbox-check.sh artifacts/flatpak/ca.electricrv.MegaPDF.flatpak
   ```

   `org.freedesktop.portal.Documents.Add` plus `GrantPermissions` makes exactly what the
   FileChooser portal makes when someone picks a file — the same store entry, the same
   permission, the same path inside the sandbox — so the app can be handed the result of
   a file dialog without one being operated. Measured 2026-09-18, **inside the Flatpak**:

   - the app opens `/run/user/1000/doc/<id>/<name>.pdf` and records exactly that path,
     with an Avalonia bookmark that wraps the same string and adds nothing;
   - after the app has quit and started again, that path opens: `render-check: PASS`;
   - after `xdg-document-portal` itself has been restarted — what a log out and back in
     does to it — that path still opens: `render-check: PASS`. The document store is on
     disk under `$XDG_DATA_HOME/flatpak/db/documents`, and the handle is in it;
   - the same file by its **real** path is `No such file or directory` from inside the
     sandbox, while the granted path lists normally. The permission is what is being
     tested, not the filesystem.

   **A caution for anyone repeating this.** `xdg-document-portal` keeps its store under
   `$XDG_DATA_HOME`. A run that starts the portal under one `HOME` and restarts it under
   another reads an empty database, every handle looks lost, and the wrong answer arrives
   looking exactly like the right one. This is how the first run of the check said "NO".
4. **`linux-arm64` is out** until the patched PDFium series builds it; `pdfium-build.yml`
   needs a `linux/arm64` entry first. The manifest and the `.deb` are x86-64 only.
