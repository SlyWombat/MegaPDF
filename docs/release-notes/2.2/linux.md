# Linux — the AppStream listing, 2.2

The Linux channel has no store console. What a software centre shows — and what
Flathub's linter reads — is
[`tools/linux/flatpak/ca.electricrv.MegaPDF.metainfo.xml`](../../../tools/linux/flatpak/ca.electricrv.MegaPDF.metainfo.xml),
one file for every Linux channel: the Flatpak and the Snap install it as it is,
and the `.deb` and the tarball install it with the launchable repointed. So the
slots map onto AppStream elements:

| Store slot | AppStream element | Limit |
|---|---|---|
| Title | `<name>` | none enforced; `appstreamcli validate --pedantic` wants it short and not a sentence |
| Subtitle | `<summary>` | **90** (`summary-too-long` above that), no trailing full stop, no markup |
| Description | `<description>` | none; `<p>`, `<ul>`, `<li>` only |
| What's new | `<release><description>` | none |

**Nothing in this file has been applied to the metainfo, on purpose.**
`tools/linux/make-release-tarball.sh` refuses a build whose version is not the
metainfo's newest `<release>` version ("the metainfo's newest `<release>` is
$TOP_VERSION, but this build is $VERSION"), and the tree is still 2.1.1
everywhere. A `<release version="2.2">` added now would break the next Linux tag
build of 2.1.x, and the new description paragraphs would ship inside the **2.1.1**
package and promise features that package does not have. Both go in at the version
bump, from the blocks below. `docs/RELEASING.md` § "Linux metainfo" is the step.

**The date.** `make-release-tarball.sh` also refuses a newest entry older than 30
days or dated in the future, so `date=` is the day of the tag, not today.

**One language.** The metainfo translates `<name>`, `<summary>` and every
screenshot `<caption>` with `xml:lang`, and does **not** translate
`<description>` — that has been true since the file was written for 2.0 (#158).
The blocks below follow it: English only. This is the one channel where the
three-locale rule does not apply today, and it is a decision for Dave rather than
something to change quietly in a listing-copy pull request — adding French for
only the two new paragraphs, while the other seven stay English, would read worse
than either consistent answer. `<summary>` is unchanged and stays translated.

**`<summary>` stays as it is.** "Fill in, sign and redact PDF forms" is 33 of 90
characters and is still true; it was part of the copy a francophone signed off for
2.0, in all three languages, and 2.2 gives no reason to spend that review again.

---

## Description — the two paragraphs to add

Into `<description>`, as a new `<p>` + `<ul>` pair **between** the "Changing what
is already on the page" list and the "Very large documents open straight away"
paragraph. Reading first, because reading is the first thing anyone does with a
PDF and it is 2.2's headline; the page list after it, because it is the same kind
of thing as the lists above it.

```xml
    <p>Reading it:</p>
    <ul>
      <li>Reading mode (Ctrl+H) takes the toolbar and the side pane off the screen
      and leaves the page. A small floating bar keeps the page number and the way
      back; Escape returns the window to normal, and full screen (F11) is offered
      once you are in it.</li>
      <li>Page colours: normal, sepia for a long read, and night for a dark room.
      Night inverts the page, pictures included — that is deliberate, and the
      setting says so, because a photograph read at night is a photograph in
      negative.</li>
      <li>Documents can open in reading mode, if that is mostly what you do with
      them.</li>
    </ul>
    <p>Rearranging the pages:</p>
    <ul>
      <li>The page thumbnails (F9) are somewhere to work, not only somewhere to
      look: rotate a page that was scanned sideways, delete one, drag pages into
      another order, insert a blank page, insert the pages of another PDF, or save
      the pages you selected as a file of their own.</li>
      <li>Each of those is one step in the undo history, so one undo puts the
      document back.</li>
    </ul>
```

**And one existing sentence has to go.** The closing paragraph of the
*description* in the four other channels says MegaPDF "doesn't rearrange pages",
which stopped being true with #174. The metainfo never carried that sentence, so
there is nothing to remove here — but the same claim is in the Linux install page,
`website/megapdf/linux/index.html`, whose intro names tabs, Markdown and
`megapdf-cli` and not reading mode. That page is not a store listing and is left
alone here; it wants a pass of its own before the Linux 2.2 release.

## What's new — the `<release>` entry to add

First in `<releases>`, above the `2.1.1` entry.

```xml
    <release version="2.2" date="REPLACE-AT-TAG">
      <description>
        <p>Reading, and the pages:</p>
        <ul>
          <li>Reading mode: Ctrl+H takes the tools off the screen and leaves the
          page, with a small floating bar for the page number and Escape to bring
          everything back. Page colours are normal, sepia and night; night inverts
          the page, pictures included, and the setting says so. Documents can open
          in reading mode.</li>
          <li>The page thumbnails became a place to work: rotate, delete, reorder,
          insert a blank page, insert the pages of another PDF, and save a
          selection of pages as a file of its own. Each is one undo step.</li>
          <li>Zoom holds the point you are pointing at instead of the corner of the
          page, and Ctrl and the wheel now zoom, which they did not before.</li>
          <li>A whiteout stays selected once you draw it, to move, resize or
          remove, and added text takes more than one line.</li>
          <li>Long work says what it is doing and how far it has got. Searching,
          making a smaller copy and saving pages out can be stopped partway, and
          stopping leaves no half-written file behind.</li>
          <li>Saving over a document that carries a digital signature of its own
          now warns that the signature will stop verifying, and offers to save a
          copy so the signed original stays intact.</li>
        </ul>
      </description>
    </release>
```

> **Not claimed here.** A withheld permission still refuses on Linux (#558 landed
> on Android only), so this entry does not say the choice is yours. There is no
> trackpad pinch on Linux either — Avalonia bridges a magnify gesture only on
> macOS — which is why the zoom line says Ctrl and the wheel. Reflow is not in 2.2
> on any platform and is not mentioned.
>
> **"Digital signature" (#602)** is the cryptographic kind, the same term the
> app's own warning uses. The signature a person places is not mentioned in this
> entry, so the two senses never meet in a sentence; the description's own
> signature bullet ("Sign by drawing, or from a photograph of a signature on
> paper") is unchanged by 2.2 and claims nothing about verifiability.
