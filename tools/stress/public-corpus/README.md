# The public corpus (#434)

A redistributable test corpus, assembled rather than inherited: this directory holds a
**manifest**, never a PDF. `fetch.sh` reproduces the documents from it on any machine and
checks every byte against a sha256, so the structure, Markdown and pages batteries can run
in CI, in a cloud sandbox, and on anyone's laptop.

That is the whole point. The private corpus is 4,337 of the owner's real documents; it can
never leave his machines, so until now the gates could only ever be run by him. This one
anybody can reproduce.

    tools/stress/public-corpus/fetch.sh                  # → ~/megapdf-public-corpus
    tools/stress/public-corpus/fetch.sh /data/corpus --jobs 8
    tools/stress/public-corpus/fetch.sh --verify-only    # re-check what is on disk

The two corpora are different populations and are **expected to give different numbers**.
See TESTING.md, "Which corpus gates what".

## Network reality — what this sandbox could actually reach

Measured 2026-09-27 from the Anthropic cloud container this corpus was built in. The
statuses are real probe results, not assumptions, and they are why the corpus looks the way
it does. `403 CONNECT` is the container's egress proxy refusing the tunnel outright: the
request never reached the host.

| #434 source | host | status | in the corpus? |
|---|---|---|---|
| IRS fillable forms | `www.irs.gov` | **403 CONNECT** | **no** |
| USCIS forms | `www.uscis.gov` | **403 CONNECT** | **no** |
| govinfo bulk | `www.govinfo.gov`, `api.govinfo.gov` | **403 CONNECT** | **no** |
| SafeDocs / UNSAFE-DOCS | `digitalcorpora.org`, `downloads.digitalcorpora.org` | **403 CONNECT** | **no** |
| Isartor suite | `pdfa.org` | **403 CONNECT** | yes, *via veraPDF* (below) |
| veraPDF corpus | `github.com` (git), `raw.githubusercontent.com` | **200 / 206** | yes |
| qpdf, PDFium fixtures | `github.com` (git), `raw.githubusercontent.com` | **200 / 206** | yes |

Two details worth knowing before you conclude a source is unreachable:

* Plain HTTPS to `github.com` is **scoped to the session's own repositories** and answers
  403 with a JSON body for anything else — but the **anonymous git lane** (`git clone`,
  `git ls-remote`) serves any public repository, and `raw.githubusercontent.com` serves
  individual files at a pinned commit with no scoping at all. That is the door every row in
  this manifest goes through.
* Everything else tried was refused: `chromium.googlesource.com`, `pdfium.googlesource.com`,
  `www.gpo.gov`, `www.sec.gov`, `europa.eu`, `archive.org`.

**The cost of this is real and should not be glossed over.** #434 calls US federal fillable
forms "the highest-value category — it is what MegaPDF is *for*", and asks for 150–300 of
them. There are none here, because there is no route to one. What forms the corpus does have
are synthetic single-feature fixtures, which exercise field *syntax* but not the deep
`/Parent` hierarchies, repeating subforms and XFA-adjacent shapes a real IRS form carries.
Anyone who can reach `irs.gov` should extend the manifest; see "Extending" below. Until then,
a green run here is not evidence that real government forms work.

## What is in it

1,037 documents, 63.6 MB. Categories are assigned from each file's own bytes by
`build-manifest.py`'s `classify()`, not from the directory it arrived in, so a row says what
the battery will actually meet.

| category | count | size | what it is |
|---|---:|---:|---|
| `form` | 237 | 13.5 MB | `/AcroForm` with `/Widget` annotations — fields, checkboxes, radio groups, signature fields |
| `tagged` | 300 | 15.3 MB | a `/StructTreeRoot`, for the tagged-PDF path (#358) |
| `report` | 250 | 18.5 MB | ordinary text documents, for extraction fidelity and reading order |
| `malformed` | 150 | 3.0 MB | deliberately broken or deliberately non-conforming — crash/hang resistance only |
| `scan` | 100 | 13.5 MB | image pages with no font resources |

By source: veraPDF 742, qpdf 179, PDFium 116.

`form` is the one category taken **whole** — every one of the 237 the sources contain — because
it is what MegaPDF is for and the category #434 calls the private corpus thinnest on. The
others are sampled on an even stride across the sorted tree, which is reproducible and does
not hand back every test for one specification clause and nothing after it.

## ⚠ The malformed set

The 150 documents under `malformed` are **deliberately broken by design**. They exist to prove
the engine does not crash or hang, and nothing else. Some may trip anti-malware on the way
past. Open them with the engine under test; do not open them with something you care about,
and do not treat a refusal to parse one as a defect — a refusal is the correct answer.

Note this is *not* the SafeDocs sample #434 asks for: `digitalcorpora.org` is unreachable from
here. These are Isartor's PDF/A-violating files and qpdf's damaged-file fixtures, which are
adjacent but milder. They are spec violations and structural damage, not the adversarial
fuzzed population SafeDocs provides.

## Licences and attribution

Every document here is redistributable. That was settled in #434 and is not re-litigated by
adding a source: nothing goes in the manifest without a licence that lets a third party fetch
and keep the file.

| source | licence | attribution required |
|---|---|---|
| [veraPDF-corpus](https://github.com/veraPDF/veraPDF-corpus) @ `bb75f4f0` | **CC BY 4.0** | **yes** — see below |
| [qpdf](https://github.com/qpdf/qpdf) test suite @ `4eba9589` | Apache-2.0 | notice retained |
| [PDFium](https://github.com/chromium/pdfium) `testing/resources` @ `a8432342` | BSD-3-Clause | notice retained |

CC BY 4.0 requires attribution wherever these files or results derived from them are
published. The required credit:

> veraPDF test corpus © the veraPDF Consortium, used under
> [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). Not modified.

**One licence nuance, stated rather than buried.** 117 of the `malformed` rows are the
**Isartor test files**, which arrive inside the veraPDF repository and are therefore covered
by that repository's blanket CC BY 4.0 statement. Isartor's own terms (pdfa.org) are narrower:
free to use for checking validation software, and **must not be used to claim that MegaPDF is
certified or conformant**. Both constraints are treated as binding here. What was *not*
verified — `pdfa.org` being unreachable — is whether the veraPDF Consortium holds the right to
relicense Isartor's files under CC BY 4.0. Treat those 117 rows as Isartor-terms files that
happen to be distributed by veraPDF, and never cite a run over them as a conformance claim.

## Extending

`build-manifest.py` regenerates the manifest from scratch, so the corpus is a function of its
sources rather than a pile someone once assembled:

    tools/stress/public-corpus/build-manifest.py --work /var/tmp/pc-build

Each source is pinned to a commit, never a branch — a branch would silently invalidate every
sha256 in the file. To add a source: give it a licence that permits redistribution, add a
`Source` row, pin the commit, and rebuild.

To add the federal forms this corpus is missing, from a machine that can reach the host:
fetch the documents, hash them, and append rows in the same seven-column shape
(`url sha256 bytes source licence category path`). `--add-source irs` deliberately refuses
rather than pretending — a generator that has never produced a verified row does not belong
in the repository, and a manifest row whose hash nobody computed is worse than a missing one.

## Files

| file | what it is |
|---|---|
| `manifest.tsv` | the corpus: one row per document. Generated — never edit by hand |
| `fetch.sh` | reproduces the documents and proves every byte |
| `build-manifest.py` | how the manifest was generated, so it can be regenerated and extended |
