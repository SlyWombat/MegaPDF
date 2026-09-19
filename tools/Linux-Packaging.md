# Linux packaging: the .deb, the Flatpak, the snap and the Flathub submission (#158)

What exists, how to build it, and what is still Dave's to decide before MegaPDF can be
submitted anywhere. Companion to `tools/Store-Submission.md`, which covers the other
three stores.

**Nothing here has been submitted, uploaded or registered.** No Flathub account, no app
ID reserved, no repository requested. Everything below was built and run on kdocker2 and
runs again in CI on every push.

**The channels (Dave, 2026-09-19):** GitHub Releases, our own signed APT repository on
electricrv.ca, and the Snap Store. Flathub is on hold: it now accepts AI-assisted apps only
on disclosure and at a reviewer's discretion, and its submission pull request must be
written by a person. The Flathub work below stays because CI proves it and it costs
nothing. How each channel goes live is § "Going live".

**MegaPDF has no update check, on purpose.** It opens no network connection of its own,
on any platform, and says so in the privacy policy and every listing. On Linux the package
manager is the update mechanism, and About MegaPDF says which one this copy has
(`MegaPDF.Core/Services/LinuxInstall.cs`): apt, snapd or Flatpak. A .deb installed from a
file, or the tarball, has none, so About says so and links the download page, which the
person's browser opens. Each tree carries an `INSTALL-KIND` marker beside the binary
(`tarball`, rewritten to `deb` by `build-deb.sh`), and Snap and Flatpak are recognised
by their environment. `MegaPDF --install-kind` prints the decision, and `package-check.sh`,
`check-flatpak.sh` and `check-apt-repo.sh` assert it.

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
| `tools/linux/make-apt-repo.sh` | the signed APT repository (`dists/stable`, `pool/main`) from one or more `.deb`s. Refuses any key but the one in `website/megapdf/apt/FINGERPRINT`, and checks its own signature with gpgv before it finishes. |
| `tools/linux/check-apt-repo.sh` | subscribes to that repository in clean Debian 12, Ubuntu 22.04 and 24.04 containers exactly as the website says, installs, runs the app, publishes a newer version and watches `apt upgrade` take it, and checks that a forged signature is refused. |
| `website/megapdf/apt/` | the public half of the repository: `megapdf.gpg`, `megapdf.asc`, `megapdf.sources`, `FINGERPRINT`. |
| `tools/linux/flatpak/ca.electricrv.MegaPDF.yml` | the manifest. |
| `tools/linux/flatpak/ca.electricrv.MegaPDF.metainfo.xml` | the AppStream data a software centre shows. |
| `tools/linux/store-captures.sh` | the six listing screenshots the metainfo points at, one language per run, under its own Xvfb. |
| `tools/linux/check-metainfo.sh` | the listing, through both the tools a Flathub reviewer runs: `appstreamcli validate` and `flatpak-builder-lint`. In CI on every push. |
| `tools/linux/make-release-tarball.sh` | `megapdf-linux-x64-<ver>.tar.gz` and its sha256 — the archive the Flathub manifest fetches. |
| `tools/linux/flatpak/flathub/…yml.in` | the manifest as Flathub would build it, with the `sources:` block left to be filled in. |
| `tools/linux/make-flathub-manifest.sh` | fills it in, from a tarball's URL and checksum (or its path, for a dry run). |
| `tools/linux/build-flathub-flatpak.sh` | builds *that* manifest, so the one Flathub runs is the one that has been run. |
| `tools/linux/qa/` | the desktop-session rigs: a headless session with a named portal backend, the file-dialog check, the recent-document check inside and outside the sandbox, and the KDE pass in a whole Plasma session. |
| `tools/linux/snap/snapcraft.yaml.in` | the snap, as a template: version and listing text are filled in from the tree and the metainfo. |
| `tools/linux/snap/make-snapcraft-yaml.py` | fills it in, replacing the one metainfo paragraph that is only true of the Flatpak. |
| `tools/linux/build-snap.sh` | the snap, from that tree, with `snapcraft pack --destructive-mode` (Ubuntu 24.04 only). |
| `tools/linux/check-snap.sh` | installs the snap and drives the app under strict confinement: what it can and cannot reach, a save at the top of the home folder, the portal dialogs, printing, French, and what AppArmor refused. |

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

**Keyboard: Ctrl+W closes and Ctrl+Q quits, on Linux and nowhere else.** GNOME's HIG
and KDE's `KStandardShortcut` both name those two, and the Linux build answers them
(`BindLinuxWindowShortcuts`, #158) through the same unsaved-changes question the
window's close button asks. They are guarded by `OperatingSystem.IsLinux()`: macOS gets
⌘W and ⌘Q from its real menu bar, and a second route there would only disagree with it.
**Ctrl+M does not minimize** — on both desktops that belongs to the window manager, not
to the application. The full list is in `docs/qa/linux-screen-inventory.md` §3.1, and
`tools/linux/qa/kde-smoke.sh` presses both keys in a Plasma session.

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

## Going live

Linux ships after the other platforms, on Dave's go-ahead. Nothing below has been done.

### The signing key

`MegaPDF APT repository <noreply@electricrv.ca>`, ed25519, no expiry, fingerprint
**`1982 176F F9E6 14A7 C60D 20D1 0097 49E2 44B5 848B`** (created 2026-09-19). The
private key exists in two places and nowhere else: `~/secrets/megapdf/apt-signing-key.asc` (mode 0600) in this
laptop's WSL, and the `APT_SIGNING_KEY` Actions secret. It has no passphrase because CI
signs with it; the secret store and the file mode are its protection. **Back the file up
somewhere offline**: if both copies are lost, every existing user has to fetch a new key
before apt will take another update from us. No expiry was chosen on purpose: an expired
key stops every user's updates silently, and a one-person project is the one most likely
to miss the date.

### The day

1. **Tag.** `git tag -a linux-v2.0.0 <sha> -m "MegaPDF 2.0.0 for Linux"` and push it.
   `linux-release.yml` builds the tarball, the .deb and the signed repository, installs
   each one and runs the app out of it, and puts the tarball, the .deb and their sha256s
   in a **draft** release named `linux-v2.0.0`. The tag is `linux-v*`, like `ios-v*` and
   `android-v*`. The bare `v*` of v1.3.0 … v1.6.2 belonged to the retired Windows
   sideload builds. Their updater reads `/releases/latest` and offers only a newer,
   parseable tag with a `.msix` in it, so no Linux release can reach them. That was
   proved by running the retired `UpdateVersion.cs` against every candidate tag and the
   live `/releases/latest` (#158).
2. **Publish the draft** on GitHub. The Linux page's download links and the Flathub
   manifest's URL only resolve after this.
3. **Put the repository into the site.** Download the tag run's `MegaPDF-apt-repository`
   artefact into `website/megapdf/apt/` (it holds `dists/` and `pool/`, and the same key
   files). Or build it locally from the release's own .deb, with `APT_SIGNING_KEY_FILE`
   pointing at the key file: `tools/linux/make-apt-repo.sh website/megapdf/apt
   megapdf_2.0.0_amd64.deb`. For a later release, keep the old `pool/` there and add the
   new .deb, so a machine that is a version behind can still resolve what it has.
4. **Rehearse.** `python3 website/deploy.py --dry-run --linux --privacy`. It refuses
   unless the repository verifies against the committed key and holds the version
   `linux/index.html` offers.
5. **Deploy.** `python3 website/deploy.py --linux --privacy`. That uploads `linux/`,
   `apt/`, and the landing page and privacy policy with their Linux text live (the Linux
   chip links the Linux page).
6. **Check it from outside**, on any Debian or Ubuntu machine or container, with the
   commands on `https://electricrv.ca/megapdf/linux/` exactly as written:
   `apt update` must fetch `electricrv.ca/megapdf/apt stable InRelease`, and after
   `apt install megapdf`, `/opt/MegaPDF/MegaPDF --install-kind` must print `AptRepository`.
7. **The Snap Store**, when its listing is public: `deploy.py --linux --snap --privacy`,
   which adds the Snap section of the Linux page and the Snap Store in the privacy
   policy's list.

**A later release** is the same, from step 1, with the Linux page's version (its download
links and the `.deb` file name) bumped in `website/megapdf/linux/index.html`.
`deploy.py --linux` refuses if the page and the repository disagree.

## The Snap Store

Dave chose the Snap Store as a Linux channel on 2026-09-19, after Flathub's policy on
AI-written apps made that channel uncertain. **Nothing has been registered or uploaded.**

### What the snap is

`megapdf`, core24, **strict confinement**, amd64. It wraps the same published tree as
the `.deb` and the Flatpak, in one piece under `$SNAP/lib/megapdf`. The listing text is
the metainfo's (`make-snapcraft-yaml.py`) with one paragraph swapped: the Flatpak asks
for no access to your files and the snap does, so the snap's listing says what the snap
does.

| plug | why |
|---|---|
| `home` | a PDF double-clicked in a file manager arrives as a path, and a snap has no document-portal forwarding for command-line files the way a Flatpak does. Auto-connected. Hidden files at the top of the home folder stay out of reach. |
| `removable-media` | `/media`, `/mnt`, `/run/media`. **Not auto-connected**: `snap connect megapdf:removable-media`, or the switch in the software centre. |
| desktop, x11, opengl, wayland… | from the `gnome` extension, which also brings the shared `gnome-46-2404` content snap (fontconfig, the X libraries, ICU). MegaPDF uses nothing from GNOME; the extension is the standard desktop plumbing for a core24 snap. |
| no `network` | MegaPDF makes no connection, and the snap updates through snapd. |
| no `cups` | printing goes through `org.freedesktop.portal.Print`, as in the Flatpak (`LinuxPrinter.InSandbox`). |

**One product change came out of it.** `AtomicFileWriter` writes a hidden temporary file
beside the document and swaps it in. The `home` plug refuses a hidden file at the top of
the home folder, so a document at `~/form.pdf` opened and then never saved. It now falls
back to the same swap under a visible name when, and only when, the hidden name is
refused. `check-snap.sh` proves both halves in the real snap: a new hidden file at the
top of the home folder is refused, and the self-test's by-path Save made there succeeds.

### Building and checking it

```sh
tools/build-linux-app.sh linux-x64 artifacts/linux
sudo snap install snapcraft --classic
sudo tools/linux/build-snap.sh                 # -> artifacts/snap/megapdf_<ver>_amd64.snap
tools/linux/check-snap.sh artifacts/snap/megapdf_*_amd64.snap artifacts/fixtures artifacts/linux/MegaPDF
```

On an Ubuntu 24.04 machine with snapd and AppArmor, not in a container: snapd needs
systemd, and the confinement being checked is AppArmor. CI does exactly this on a GitHub
`ubuntu-24.04` runner, which is a VM (`.github/workflows/snap.yml`, on every change to
the Linux app).

**Run the check from inside a user session.** snapd tracks every snap process in a
transient scope under the user's systemd manager and refuses to start one from anywhere
else, with `… is not a snap cgroup for tag snap.megapdf.megapdf`. A desktop terminal is
already such a session. A CI step is a system service, so the workflow enables lingering
and runs the check through `systemd-run --machine=$USER@.host --user`. The first CI run
without this failed every confined check and passed every refusal, because nothing ran:
each probe now prints `RAN` from inside the snap before its answer counts.

**What AppArmor refuses, and why each one is expected** (the check lists them):

| refused | why |
|---|---|
| the check's own probes: a hidden file at the top of the home folder, `/opt`, `/media` before removable-media is connected | on purpose |
| `create` of an `inet` / `inet6` datagram socket | .NET asking the kernel which address families exist, the first time anything opens a socket. Here that is Tmds.DBus opening its Unix socket to the session bus. No connection is attempted, and the answer ("neither") changes nothing for an app that makes no network connection. |
| `file_lock` on `/proc/<pid>/stat` | the .NET runtime reading its own process statistics. |

**Startup cost**, measured by the check on the CI runner (best of three, warm cache;
cold is one run after dropping the page cache):

| | snap | unpacked tree |
|---|---|---|
| `--language-check`, cold | 763 ms | 168 ms |
| `--language-check`, warm | 186 ms | 54 ms |
| `--render-check`, warm | 199 ms | 74 ms |

About 130 ms per launch is `snap run` and the gnome extension's `desktop-launch` chain.
The cold figure also includes reading the compressed squashfs, which is why the snap is
built with lzo rather than xz.

### What Dave has to do before the first upload

In this order. Every step happens in Dave's own account, so none of it can be delegated.

1. **An Ubuntu One account.** Then sign in at <https://snapcraft.io/account> and accept
   the developer agreement.
2. **Register the name `megapdf`** at <https://snapcraft.io/register-snap>. On
   2026-09-19 no published snap is called `megapdf`, `mega-pdf` or `megapdf-editor` (the
   store API answers 404 for all three), but a name that was registered and never
   published cannot be seen from outside an account. If `megapdf` is taken, pick another
   and change the `name:` line in `snapcraft.yaml.in`, the `snap/gui/megapdf.*` names in
   `build-snap.sh`, and the website's install line.
3. **A store credential for CI**, made on any machine with snapcraft after
   `snapcraft login`: `snapcraft export-login` with `--snaps=megapdf`,
   `--acls=package_access,package_push,package_update,package_release` and an
   `--expires` date a year out, written to a file. Put that file's contents into the
   repository secret `SNAPCRAFT_STORE_CREDENTIALS` with `gh secret set`, reading it from
   the file rather than typing it, then delete the file. Scoped to this one snap and
   expiring; never pasted anywhere else.
4. **The listing page** (snapcraft.io/megapdf/listing): the six Linux screenshots in
   `website/megapdf/screenshots/linux/en/`, the category (Productivity or Office), the
   website and the contact. The summary and description arrive with the upload.
5. **The first upload**: Actions → Snap → Run workflow, with `upload` ticked. It goes to
   the **edge** channel only. Try it with `sudo snap install megapdf --edge`.
6. **Stable**: promote that revision in the dashboard's Releases tab. From then on
   `sudo snap install megapdf` works for everyone, and Ubuntu's App Center lists it.

Nothing here needs a store review of the app's permissions: any snap may plug `home` and
`removable-media`, and neither is asked to auto-connect. Asking for `removable-media` to
auto-connect would be a request on forum.snapcraft.io, in Dave's own words.

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
4. **`linux-arm64`: the engine cross-builds. The app cannot ship from it yet, and should
   not be in 2.0.** (#254 A6.)

   This used to say the patched PDFium series did not build for arm64. It does, without a
   patch or a workaround. `tools/pdfium/build-pdfium.sh <work> linux arm64` runs
   pdfium-binaries' own arm64 path — it downloads the Debian arm64 sysroot itself and
   `steps/05-configure.sh` has a `linux-arm64` case already — and produces an
   `ELF 64-bit LSB shared object, ARM aarch64` `libpdfium.so` exporting **exactly the same
   435 `FPDF*` symbols** as the x64 build of the same series, at a comparable size
   (7.86 MB against 7.66 MB). Measured 2026-09-18 on series `a02dc04f63e3` (30 patches).
   `pdfium-build.yml` now has the `linux/arm64` entry.

   The rest was measured too, on the same day:

   | | |
   |---|---|
   | `core/` → `libmegapdf_core.so` for aarch64 | **builds**, with a four-line CMake toolchain file and `g++-aarch64-linux-gnu`. `core/CMakeLists.txt` needed no change at all. |
   | `dotnet publish -r linux-arm64 --self-contained` | **works**. The apphost comes out AArch64, and SkiaSharp and HarfBuzzSharp both ship arm64 natives. |
   | the two native libraries in that publish | **x86-64**. `MegaPDF.Core.csproj` copies from `libs/pdfium/linux-x64` and `core/build/linux-x64` whatever the runtime identifier is, so the output is an arm64 app carrying an x64 engine. |

   So three code changes remain, all small and none of them unknown:

   1. `tools/fetch-pdfium-linux.sh` takes an architecture and fetches
      `pdfium-linux-arm64.tgz` (it hardcodes `pdfium-linux-x64.tgz`);
   2. `tools/build-core.sh` takes an architecture and passes a toolchain file (it
      hardcodes `linux-x64` from `uname`);
   3. `MegaPDF.Core.csproj` picks the native pair by runtime identifier, and
      `tools/build-linux-app.sh` stops refusing `linux-arm64`.

   **What blocks all three is not code.** The pinned PDFium release carries no
   `pdfium-linux-arm64.tgz`, so nothing can fetch one. Publishing it is Dave's, because
   it means running the release workflow and pushing a release tag:

   ```
   gh workflow run "PDFium (patched) build" -f branch=chromium/7934 -f release=true
   ```

   That rebuilds every target and publishes a new `pdfium-<branch>-megapdf-<series>`
   prerelease. It does **not** change the patch series, so the x64, Windows, macOS, iOS
   and Android binaries are the same sources as the pinned ones — but it is a new release
   and a new pin, and re-pinning every platform during a release hold is not a thing to do
   for a stretch item.

   **The recommendation is to leave `linux-arm64` out of 2.0.** Not because it cannot be
   built — it can — but because nothing about it has been *run*. There is no corpus
   battery on arm64, no QA pass, no baseline to compare against, and this server cannot
   execute an arm64 binary (no `qemu-aarch64` binfmt, and registering one is a change to
   the host). GitHub's `ubuntu-24.04-arm` runners would make an arm64 `--self-test` and a
   battery possible, which is what 2.1 should do. Shipping a Flathub arm64 package that
   has never been executed is worse than shipping none.

   The manifest and the `.deb` are therefore still x86-64 only, deliberately.
