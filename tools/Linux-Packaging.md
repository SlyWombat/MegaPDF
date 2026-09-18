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
3. **Screenshots.** The metainfo currently points at the product's own published
   screenshots on `electricrv.ca`. They are not Linux captures, and reviewers expect the
   listing to show the app as it looks on the platform. Linux ones can be taken here —
   `--screenshot`, `--screenshot-state` and `--story` all work under Xvfb, and
   `X11PlatformOptions.OverlayPopups` is set so flyout states capture — but they then
   have to be **deployed to electricrv.ca** before submission, because AppStream
   screenshots are URLs and Flathub's linter fetches them.
4. **The summary and description** in the metainfo are adapted from the App Store copy in
   `docs/app-store-listing.md`. Read them once as a Linux listing rather than an iOS one.
5. **A release history.** The metainfo has one `<release>` entry for the version it was
   built against. A submission wants the same history the other stores have, from
   `docs/release-notes/`.
6. **Which branch.** Flathub builds from a manifest in its own repository. This one
   installs a prebuilt tree, which Flathub accepts for a project whose source is public —
   MegaPDF is Apache-2.0 — but it means the manifest there fetches a release tarball from
   a URL with a checksum, rather than the local directory this one uses. That is a small
   edit to the `sources:` block and a step in the release process to publish the tarball.

## Before a Flathub submission: what is needed from the code

1. **Printing has no portal route.** `LinuxPrinter` shells out to `lp`, which does not
   exist inside the sandbox. The app detects `/.flatpak-info` and says so rather than
   failing obscurely — `--print-check` inside the sandbox reports exactly that — but
   "printing does not work in the Flatpak" is not a thing to ship. `org.freedesktop.portal.Print`
   is real work and wants its own issue.
2. **The file-dialog portal has to be confirmed by hand.** Avalonia chains the XDG portal
   ahead of its own fallback, but which one it picks is only observable when a dialog
   opens, and no headless check can tell them apart. One GNOME session, one Open, one
   Save.
3. **Recent documents will not reopen across a restart.** A file opened through the
   portal is a handle in the document store, and the app records the path it was given.
   Whether that path survives a restart depends on the portal's persistence, which the
   app does not currently ask for. Worth checking in the same GNOME session as (2).
4. **`linux-arm64` is out** until the patched PDFium series builds it; `pdfium-build.yml`
   needs a `linux/arm64` entry first. The manifest and the `.deb` are x86-64 only.
