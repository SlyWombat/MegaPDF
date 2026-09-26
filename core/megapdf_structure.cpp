// Contract 9: document structure (#142, #353, SDD §3.9/§6.2 contract 6).
//
// The heuristic path only — the tagged-PDF structure-tree path is #358. Every page in the
// loaded range is read from PDFium's FPDF_TEXTPAGE (the same source megapdf_search_page
// reads, not megapdf_text_load's page-level text objects — see megapdf_core.h's contract 9
// banner and design §1 item 2, the 2026-09-24 staged-design comment on #142), grouped into
// words, lines, regions and blocks by the rules in design §1.2, and returned as one
// reading-ordered, page-range-wide snapshot.
//
// Pipeline, in the order the code below runs it, per page:
//   1. characters (from FPDF_TEXTPAGE) -> words (glyph-gap grouping) -> lines (BuildLines'
//      vertical-centre-overlap rule, reused at word granularity)
//   2. furniture candidates set aside (top/bottom-band lines, matched across the range)
//   3. body size over the whole range (character-weighted modal size)
//   4. remaining lines -> reading order, by recursive XY-cut
//   5. reading order -> blocks: headings, list items, paragraphs (hyphen-joining; a
//      trailing paragraph for rotated/unclassified text)
//   6. figures (image page objects) and form fields (contract 3) spliced into the order
//   7. per-page confidence
// then a whole-range pass assigns heading levels and cross-page paragraph continuation.
//
// Every numeric threshold below is a single named constant with a comment saying whether it
// is design §1.2's own stated value (this phase has not run the corpus battery; #354 does
// that) or an implementation choice the design left unstated (span segmentation, style
// detection — below the design's level of description).

#include "megapdf_core.h"
#include "megapdf_core_internal.h"

#include "fpdf_edit.h"
#include "fpdf_text.h"
#include "fpdfview.h"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <map>
#include <memory>
#include <new>
#include <string>
#include <unordered_map>
#include <vector>

namespace {

using megapdf_internal::IsCancelled;
using megapdf_internal::Lock;
using megapdf_internal::PageHandle;
using megapdf_internal::PageUnit;
using megapdf_internal::SetLastError;
using megapdf_internal::ToCropRect;
using megapdf_internal::ToCropX;
using megapdf_internal::ToCropY;

using U16 = std::vector<unsigned short>;

// ---------------------------------------------------------------------------
// Named, corpus-tuned constants (design §1.2).
// ---------------------------------------------------------------------------

// Words: gap > kWordGapEm ends a word. Design §1.2 states 0.2 em; #363's corpus-scale
// investigation (2026-09-24) found that value does not survive contact with
// FPDFText_GetLooseCharBox's real-world behaviour: measured over a sample of the pdf-test
// corpus, the loose-box gap between two CONSECUTIVE characters of the SAME text run (same
// page object, weight, italic, mono and font size -- not a style change, not a different
// line) exceeded 0.2 em for tens of thousands of character pairs per few hundred documents,
// with a median just above the threshold (~0.31 em) and a long tail. Comparing every
// "invented" excess token (present in the heuristic output but nowhere in FPDFText_GetText
// for that page, per #354's measure 1) against the page's own raw token stream showed 100%
// of them are a SUBSTRING of some real raw token -- i.e. this is real single words being
// chopped into multiple fragments by exactly this test, not fabricated or duplicated text.
// A controlled sweep (kWordGapEm alone, `structure_check diag`, #363) found F1 improving
// from 0.962 to 0.987 by 0.8 em with sharply diminishing returns beyond it (1.5 em: 0.988),
// and the reverse-direction mismatch (raw tokens with no heuristic match) *falling* at every
// step rather than rising, i.e. no sign this trades over-splitting for over-merging. 0.8 is
// therefore a corpus-measured recalibration of design's own constant for the box
// representation actually in use, not a free hyperparameter tweak: the file header's own
// note that these values await #354's corpus battery is exactly what ran here. A further,
// smaller contributor (same-run characters split by the baseline test despite a normal
// horizontal gap) was investigated in a follow-up round -- see the kBaselineEm/
// kSuperscriptGapEm comment just below.
constexpr double kWordGapEm = 0.8;
// #363 follow-up (2026-09-24): the baseline test itself (BuildWords, below) also splits some
// same-run pairs that pass the word-gap test above. `structure_check diagbaseline`'s corpus
// evidence found that residual population dominated -- roughly 9 in 10 of a 500-document
// sample's 2,011 such pairs -- by a coincidentally small (often NEGATIVE) horizontal gap
// between the last character of one line and the first of a wholly unrelated next line or
// paragraph, not a genuine same-word offset: matrix skew measured EXACTLY zero on every one
// of them (ruling out a rotated line outright) and font size was within 5% on 78% (ruling out
// a classic shrink-based superscript as the dominant cause). The baseline test is CORRECTLY
// keeping those apart; the prior round's broad loosening attempts mostly admitted more of
// that false-join majority, which is why they under-delivered, and comparing against the
// word's start instead of its immediate predecessor (widening the effective net further)
// actively hurt F1. A narrow minority is different in kind: a genuinely TIGHT forward gap (a
// coincidental different-line proximity is never this tight -- it clusters at 0.2-0.8 em or
// is negative) together with a moderate vertical offset, which the two constants below admit
// as the same word without touching the majority case above.
constexpr double kBaselineEm = 0.35;
constexpr double kSuperscriptGapEm = 0.2;     // tight forward gap only -- a letter-spacing range, not kWordGapEm's line-proximity range.
constexpr double kSuperscriptOffsetEm = 1.0;  // vertical offset ceiling for that tight-gap case only.
constexpr double kLineCentreOverlapFactor = 0.5;        // Lines: BuildLines' own constant, reused (megapdf_core.h:292-295).
constexpr double kLineSplitFontSizeFactor = 2.0;        // ...and its horizontal-gap line split, mirrored.

constexpr int kMaxCutDepth = 3;                                // XY-cut depth.
constexpr double kHorizontalBandPitchFactor = 1.5;             // a horizontal gap > 1.5x median pitch cuts.
constexpr double kVerticalGutterEm = 1.5;                      // a full-height gutter > 1.5 em cuts.
constexpr int kVerticalGutterMinLines = 4;                     // ...over a region of >= 4 lines.
constexpr int kMaxVerticalCuts = 3;                            // more than 3 -> row-by-row, confidence penalty.

constexpr double kHeadingSizeRatio = 1.15;                     // size >= 1.15x body is a heading on size alone.
constexpr double kHeadingBoldColumnWidthFactor = 0.70;         // a bold-at-body heading is < 70% of its column width.
constexpr double kHeadingBoldGapPitchFactor = 0.6;             // ...followed by a gap >= 0.6x line pitch.
constexpr int kHeadingMaxLines = 3;                            // a longer group is a paragraph.
constexpr int kMaxHeadingLevel = 6;

// #375: the bold-at-body-size heading rule above over-fires on tabular/invoice/statement
// documents -- table column headers, address fragments, dollar values, form-field labels all
// pass "bold, <= body size, narrower than 70% of the column, followed by a gap" just as
// easily as a genuine subheading does. tools/structure-check headingdiag's corpus-scale
// diagnosis (#375's PR description has the full numbers) found the column-width test alone
// (this rule's only existing per-region signal) does not separate the two cases -- a real
// two-column article's subheading and an invoice's table-cell label both sit in a narrow
// leaf the XY-cut produced -- and neither does the issue's own suggested XY-cut-multi-column
// proxy on its own: a coordinator hand-read of 25 corpus documents found the same over-firing
// on a single-column forwarded-email header block and a label/value form summary, neither one
// a multi-column layout by any column-count definition.
//
// What the diagnosis DID find, corpus-wide: a genuine heading is a rare, isolated structural
// marker (this issue's own legal-contract counter-example -- 3 correct headings out of 137
// lines, none adjacent to another); a false positive from this rule overwhelmingly comes
// stacked with others of its own kind, back-to-back in the page's own content stream, with
// nothing else between them -- a table's column of cells, a forwarded email's header block, a
// form's label/value list. kHeadingRunSuppressThreshold names how many back-to-back
// bold-at-body HEADING candidates it takes before DemoteFalsePositiveHeadingRuns (below)
// stops trusting the rule for that whole run: 2, not 3 -- the coordinator's own form-summary
// example ("Status" then "No", two lines) is a run of exactly two, and the corpus data shows
// no meaningful population of genuine two-heading-with-nothing-between-them runs to protect
// (a real document almost always puts body text, not another same-size bold line, right after
// a heading). kHeadingNumericLikeSuppressed is independent of run length: no genuine section
// heading is pure digits/currency/punctuation with no letter in it at all, so a single
// numeric-like bold-at-body candidate is demoted even when it stands alone (the diagnosis
// found isolated numeric-like false positives -- a lone dollar figure or account number
// classified as a heading with nothing else nearby to cluster it into a run).
constexpr size_t kHeadingRunSuppressThreshold = 2;

constexpr double kParagraphPitchFactor = 1.4;                  // consecutive lines within 1.4x median pitch.
constexpr double kParagraphLeftEdgeToleranceEm = 1.0;          // left edges within 1 em: same paragraph.
constexpr double kParagraphIndentEm = 1.0;                     // indent >= 1 em starts a new paragraph.
constexpr double kParagraphGapPitchFactor = 1.5;               // gap >= 1.5x pitch ends a paragraph.
// Not in design §1.2: "median pitch" is measured from the page's own lines, so a page whose
// every line sits in isolation (a form, a list of one-line fields — `doubled.pdf`'s six
// unrelated single lines, each 40 pt apart, is the fixture that found this) has no small
// pitch on it to be the outlier against, and the median IS the isolated gap, so nothing
// merges that should not, but nothing SHOULD merge either — every "paragraph" candidate then
// measures within its own (large) median and wrongly reads as one block. Ordinary single
// line spacing is rarely more than about 2x the font size in practice, so block-grouping
// caps the pitch it compares against at that multiple of the body size, never trusting a
// bigger page-median than that. #354's corpus measurement may move the multiple; it does not
// remove the need for some absolute anchor alongside the page-relative one design §1.2 gives.
constexpr double kMaxSingleLinePitchToBodySizeRatio = 1.5;

constexpr double kListMarkerGapEm = 0.5;                       // marker -> body text gap >= 0.5 em.
constexpr double kListDepthClusterEm = 1.0;                    // marker x-clusters, 1 em tolerance.

constexpr double kFurnitureBandFraction = 0.08;                // top/bottom 8% of the page.

constexpr double kFigureMinAreaCm2 = 1.0;                      // image objects smaller than this are not figures.
constexpr double kPointsPerCm = 72.0 / 2.54;

// Confidence penalties (design §1 item 6): the design's three fixed deductions. The fourth
// ("minus the share of characters left in no block") is proportional, so there is no fixed
// number to name for it — see ComputeConfidence() below.
constexpr int kConfidenceTooManyColumnsPenalty = 30;
constexpr int kConfidenceRotatedTextPenalty = 30;
constexpr double kConfidenceRotatedTextShare = 0.10;
constexpr int kConfidenceOverlapPenalty = 20;

// #384 mitigation: a fourth deduction, same scale/mechanism as the three above (one fixed,
// named, one-strike-per-page penalty), for a page whose heuristic reading order takes an
// implausible jump between two adjacent blocks -- see HasImplausibleReadingOrderJump()'s own
// comment for the exact geometric test. Grounded in a corpus-scale sweep (#384/#387's
// prevalence measurement, extended with tools/structure-check's MaxBackwardJumpUnits over the
// full personal corpus, /mnt/pdf-test: 4,084 documents opened ok, 17,388 tau_ref-measured
// pages): pages with NO qualifying jump scored tau_ref < 0.9 (the reading-order-quality line
// #354 already uses) at 21.6%, in line with the corpus's own 21.8% baseline, while pages with
// a jump at or above kConfidenceReadingOrderJumpBodySizes scored below that line 36.7% of the
// time (578 pages, 3.3% of all measured pages) -- a real, sizeable lift (a much looser 0.5x
// cutoff gives only 27.8%, barely above baseline; the lift keeps climbing at 3x/5x but the
// flagged population shrinks further, so 2x sits at the point of real, not marginal,
// separation), comparable in magnitude to #387's own sparsest-decile finding (37.2%). This is
// a distinct population from that sparsest-decile one, not a re-discovery of it: of the pages
// this flags, only 5.4% are also in the corpus's sparsest quartile by token count (vs. 25.2%
// of all pages) -- i.e. this signal is not, in practice, just another way of saying "short
// page" (the false-positive class #384's own review worried about), it independently finds a
// real, comparably-sized population of bad-reading-order pages via geometry alone. Reusing
// kConfidenceOverlapPenalty's point value rather than inventing a new number: both are "a
// geometric implausibility on this page", the same class of signal design §1 item 6 already
// covers.
constexpr int kConfidenceReadingOrderJumpPenalty = kConfidenceOverlapPenalty;
constexpr double kConfidenceReadingOrderJumpBodySizes = 2.0;
// Two blocks count as "the same column" (a jump between them is eligible for the penalty
// above) only when their horizontal extents overlap by more than this fraction of the
// narrower block's width -- otherwise the jump is exactly the ordinary column-to-column or
// page-to-page transition #384 asks this mitigation NOT to penalize, however large the jump.
constexpr double kReadingOrderJumpOverlapFrac = 0.3;

// Implementation choices below design §1.2's level of description:
constexpr double kFontSizeSpanToleranceRatio = 0.02;   // +-2%: two adjacent same-style runs count as one span.
constexpr int kBoldWeightThreshold = 600;              // FPDFText_GetFontWeight >= this is bold (400 normal, 700 bold).
constexpr unsigned int kFontDescriptorItalicFlag = 0x40;      // PDF 1.7 Table 123, bit 7.
constexpr unsigned int kFontDescriptorFixedPitchFlag = 0x1;   // ...bit 1.
constexpr double kRotationSkewTolerance = 0.02;        // FS_MATRIX b/c beyond this (relative to a) is "rotated".

constexpr unsigned int kSoftHyphen = 0x00AD;
constexpr unsigned int kHyphenMinus = 0x002D;
constexpr unsigned int kHyphenChar = 0x2010;

// Bullet code points design §1.2 names explicitly, plus the Symbol/ZapfDingbats bullets Word
// and LibreOffice are documented to export via their PDF ToUnicode CMap into the Private Use
// Area (Symbol and ZapfDingbats have no natural Unicode bullet of their own). U+F0B7 is the
// common, well-documented case (Symbol's bullet, code 0xB7 in Symbol's built-in encoding);
// U+F0A7 is LibreOffice's own square-bullet export through the same mechanism. This is the
// design's stated starting set; #354's corpus census is expected to find others.
bool IsBulletCodepoint(unsigned int cp) {
    switch (cp) {
        case 0x2022: case 0x25E6: case 0x25AA: case 0x2013: case 0x2014: case 0x00B7:
        case '*': case '-':
        case 0xF0B7: case 0xF0A7:
            return true;
        default:
            return false;
    }
}

bool IsRomanLetter(unsigned int cp) {
    const unsigned int lower = (cp >= 'A' && cp <= 'Z') ? cp + 32 : cp;
    return lower == 'i' || lower == 'v' || lower == 'x' || lower == 'l' || lower == 'c' || lower == 'd' || lower == 'm';
}
bool IsAsciiDigit(unsigned int cp) { return cp >= '0' && cp <= '9'; }
bool IsAsciiLetter(unsigned int cp) { return (cp >= 'a' && cp <= 'z') || (cp >= 'A' && cp <= 'Z'); }
bool IsAsciiLower(unsigned int cp) { return cp >= 'a' && cp <= 'z'; }

bool IsWhitespaceCp(unsigned int cp) {
    return cp == ' ' || cp == '\t' || cp == '\r' || cp == '\n' || cp == 0x00A0 || cp == 0x2028 || cp == 0x2029 ||
           (cp >= 0x2000 && cp <= 0x200B) || cp == 0x202F || cp == 0x205F || cp == 0x3000;
}

// design §1.2 "Lists": a single bullet glyph, or an ordinal — \d+[.)], [a-zA-Z][.)], a roman
// numeral + [.)], or (\d+) — as the WHOLE marker word (the gap to body text is the caller's).
bool IsListMarker(const std::vector<unsigned int>& cps) {
    if (cps.empty()) return false;
    if (cps.size() == 1) return IsBulletCodepoint(cps[0]);
    if (cps.front() == '(' && cps.back() == ')' && cps.size() >= 3) {
        bool digits = true;
        for (size_t i = 1; i + 1 < cps.size(); i++) {
            if (!IsAsciiDigit(cps[i])) { digits = false; break; }
        }
        if (digits) return true;
    }
    const unsigned int last = cps.back();
    if (last != '.' && last != ')') return false;
    const size_t body_len = cps.size() - 1;
    if (body_len == 0) return false;
    bool all_digits = true, all_roman = true;
    for (size_t i = 0; i < body_len; i++) {
        if (!IsAsciiDigit(cps[i])) all_digits = false;
        if (!IsRomanLetter(cps[i])) all_roman = false;
    }
    if (all_digits) return true;
    if (body_len == 1 && IsAsciiLetter(cps[0])) return true;
    if (all_roman) return true;
    return false;
}

U16 EncodeUtf16(const std::vector<unsigned int>& cps) {
    U16 out;
    out.reserve(cps.size());
    for (unsigned int cp : cps) {
        if (cp > 0xFFFF && cp <= 0x10FFFF) {
            const unsigned int v = cp - 0x10000;
            out.push_back(static_cast<unsigned short>(0xD800 + (v >> 10)));
            out.push_back(static_cast<unsigned short>(0xDC00 + (v & 0x3FF)));
        } else {
            out.push_back(static_cast<unsigned short>(cp));
        }
    }
    return out;
}

// ---------------------------------------------------------------------------
// Characters, words, lines (per page)
// ---------------------------------------------------------------------------

struct Char {
    unsigned int unicode = 0;
    double l = 0, b = 0, r = 0, t = 0;                    // crop space, tight ink box (display/span bounds)
    double loose_l = 0, loose_r = 0;                       // crop space, FPDFText_GetLooseCharBox (word-gap test)
    double loose_t = 0, loose_b = 0;                       // ...its top/bottom, needed only for #363's
                                                            // rotation-aware BuildWords (a rotated box's
                                                            // leading corner along the advance is not always
                                                            // the one loose_l/loose_r alone would pick).
    double origin_x = 0, origin_y = 0;   // crop space
    double font_size = 0;                // crop space points (the raw Tf operand -- #363's diagnosis on
                                          // FPDFText_GetFontSize -- times the page's own unit scale)
    // The linear (rotation/scale, no translation) part of FPDFText_GetMatrix, unscaled -- #363's
    // rotation-aware BuildWords inverts this to map a page-space gap/baseline test into this
    // character's own text space, where its advance runs along local +x and font_size (above) is
    // genuinely the em, whether or not the character is rotated. Defaults to the identity so a
    // character PDFium fails to hand back a matrix for behaves exactly as the old page-space code
    // did (identity's inverse is itself).
    double mat_a = 1, mat_b = 0, mat_c = 0, mat_d = 1;
    int object_index = -1;               // the page-level text object, -1 for a form-XObject run
    bool bold = false, italic = false, mono = false;
    bool rotated = false;
    // PDFium inserted at least one generated character (a space or a CR/LF pair) between
    // this character and the previous real one, so it judged this NOT a continuation of the
    // same run even when the two sit close together geometrically — found on the #98
    // schematic, whose producer draws every glyph as its own text-showing object: PDFium
    // generates a CR/LF between consecutive same-line, same-object-boundary characters, and
    // its own FPDFText_FindNext (and so search parity, design §2 bar 2) never matches across
    // one. Design §1.2 says a generated character's TEXT is ignored ("spacing is ours"); this
    // is the read that its BREAK is not — PDFium already decided these are not one run.
    bool preceded_by_break = false;
    // FPDFText_IsHyphen(tp, i): PDFium's own judgement that this character is a hyphen at a
    // line-wrap point. Found necessary, not optional: for such a character
    // FPDFText_GetUnicode returns 2, not the hyphen's real code point (measured on this
    // fixture's own "hyphen-" / "ISO-" lines — U+002D read back as U+0002 both times), so
    // BuildPieces' hyphen-joining test cannot rely on the unicode value alone.
    bool is_hyphen = false;
};

// A run of consecutive (PDFium's own character order) real characters joined while the
// glyph-box gap stays within kWordGapEm of the font size (design §1.2 "Words"). Indices are
// into the owning PageWork's `chars`.
struct Word {
    std::vector<int> chars;
    double l = 0, b = 0, r = 0, t = 0;
    double font_size = 0;
};

struct Line {
    std::vector<int> words;   // indices into PageWork::words, left to right
    double l = 0, b = 0, r = 0, t = 0;
};

struct PageWork {
    int page_index = 0;
    double width = 0, height = 0;
    std::vector<Char> chars;
    std::vector<Word> words;          // excludes characters flagged rotated
    std::vector<Word> leftover_words; // rotated characters, grouped the same way, kept separately
    std::vector<Line> lines;          // built from `words`; furniture lines removed before block-building
    int total_real_chars = 0;
    int rotated_chars = 0;
};

double Em(double font_size) { return font_size > 0 ? font_size : 1.0; }

void ClassifyStyle(FPDF_TEXTPAGE tp, int i, bool* bold, bool* italic, bool* mono) {
    *bold = *italic = *mono = false;
    const int weight = FPDFText_GetFontWeight(tp, i);
    if (weight >= kBoldWeightThreshold) *bold = true;
    char name_buf[256] = {0};
    int flags = 0;
    const unsigned long len = FPDFText_GetFontInfo(tp, i, name_buf, sizeof(name_buf), &flags);
    std::string name;
    if (len > 0 && len <= sizeof(name_buf)) {
        const size_t n = name_buf[len - 1] == '\0' ? len - 1 : len;
        name.assign(name_buf, n <= sizeof(name_buf) ? n : sizeof(name_buf));
    }
    if (flags & static_cast<int>(kFontDescriptorItalicFlag)) *italic = true;
    if (flags & static_cast<int>(kFontDescriptorFixedPitchFlag)) *mono = true;
    std::string lower;
    lower.reserve(name.size());
    for (char c : name) lower += static_cast<char>((c >= 'A' && c <= 'Z') ? c + 32 : c);
    if (!*bold && lower.find("bold") != std::string::npos) *bold = true;
    if (!*italic && (lower.find("italic") != std::string::npos || lower.find("oblique") != std::string::npos)) *italic = true;
    if (!*mono && (lower.find("mono") != std::string::npos || lower.find("courier") != std::string::npos)) *mono = true;
}

// The page-level object a character's text object is, or -1 for a form-XObject run (design
// §1 item 2: FPDFText_GetTextObject maps a character back to its object; a page-object index
// map is built once so the O(1) lookup does not repeat megapdf_text_load's #149 fix).
std::unordered_map<FPDF_PAGEOBJECT, int> IndexPageObjects(FPDF_PAGE page) {
    std::unordered_map<FPDF_PAGEOBJECT, int> map;
    const int count = FPDFPage_CountObjects(page);
    for (int i = 0; i < count; i++) {
        FPDF_PAGEOBJECT obj = FPDFPage_GetObject(page, i);
        if (obj != nullptr) map[obj] = i;
    }
    return map;
}

// (std::min) and (std::max) in parentheses throughout this file: <windef.h>, which pdfium
// pulls in on Windows, defines min and max macros (see megapdf_core.cpp's PaintBox comment).
//
// Reads every character of `page` into crop space and splits real content from generated
// separators (design §1.2 "Words": "generated characters are ignored").
void ReadChars(const megapdf_page* page, PageWork* out) {
    FPDF_PAGE raw = PageHandle(page);
    FPDF_TEXTPAGE tp = FPDFText_LoadPage(raw);
    if (tp == nullptr) return;
    const double unit = PageUnit(page);
    const auto obj_index = IndexPageObjects(raw);
    const int count = FPDFText_CountChars(tp);
    out->chars.reserve(static_cast<size_t>((std::max)(0, count)));
    bool pending_break = false;   // a generated or whitespace character was skipped since the last real one
    for (int i = 0; i < count; i++) {
        if (FPDFText_IsGenerated(tp, i) == 1) { pending_break = true; continue; }
        const unsigned int u = FPDFText_GetUnicode(tp, i);
        if (u == 0 || IsWhitespaceCp(u)) { pending_break = true; continue; }
        double l = 0, r = 0, b = 0, t = 0;
        if (!FPDFText_GetCharBox(tp, i, &l, &r, &b, &t)) continue;
        double ox = 0, oy = 0;
        FPDFText_GetCharOrigin(tp, i, &ox, &oy);
        Char c;
        c.unicode = u;
        c.l = ToCropX(page, l);
        c.r = ToCropX(page, r);
        c.b = ToCropY(page, b);
        c.t = ToCropY(page, t);
        if (c.r < c.l) std::swap(c.l, c.r);
        if (c.t < c.b) std::swap(c.b, c.t);
        c.origin_x = ToCropX(page, ox);
        c.origin_y = ToCropY(page, oy);
        c.font_size = FPDFText_GetFontSize(tp, i) * unit;
        // The tight ink box (above) understates many glyphs' true advance — "l", "i", a
        // narrow numeral — so gapping words on it alone over-splits exactly those words
        // (measured on the #98 schematic while building this: "Hardware" split into five
        // words). FPDFText_GetLooseCharBox covers the glyph's full advance rather than its
        // ink, and is what BuildWords compares against kWordGapEm.
        FS_RECTF loose{};
        if (FPDFText_GetLooseCharBox(tp, i, &loose)) {
            c.loose_l = ToCropX(page, loose.left);
            c.loose_r = ToCropX(page, loose.right);
            if (c.loose_r < c.loose_l) std::swap(c.loose_l, c.loose_r);
            c.loose_t = ToCropY(page, loose.top);
            c.loose_b = ToCropY(page, loose.bottom);
            if (c.loose_t < c.loose_b) std::swap(c.loose_t, c.loose_b);
        } else {
            c.loose_l = c.l;
            c.loose_r = c.r;
            c.loose_t = c.t;
            c.loose_b = c.b;
        }
        FPDF_PAGEOBJECT obj = FPDFText_GetTextObject(tp, i);
        const auto it = obj != nullptr ? obj_index.find(obj) : obj_index.end();
        c.object_index = it != obj_index.end() ? it->second : -1;
        ClassifyStyle(tp, i, &c.bold, &c.italic, &c.mono);
        c.preceded_by_break = pending_break;
        pending_break = false;
        c.is_hyphen = FPDFText_IsHyphen(tp, i) == 1;
        FS_MATRIX m{1, 0, 0, 1, 0, 0};
        if (FPDFText_GetMatrix(tp, i, &m)) {
            const double a = std::fabs(m.a) > 1e-6 ? std::fabs(m.a) : 1.0;
            c.rotated = std::fabs(m.b) > kRotationSkewTolerance * a || std::fabs(m.c) > kRotationSkewTolerance * a;
        }
        // Stored regardless of whether FPDFText_GetMatrix succeeded above: on failure `m` is
        // still the identity it was initialised to, which is exactly the fallback #363's
        // rotation-aware BuildWords wants (see the Char::mat_a comment).
        c.mat_a = m.a; c.mat_b = m.b; c.mat_c = m.c; c.mat_d = m.d;
        out->chars.push_back(c);
    }
    FPDFText_ClosePage(tp);
    out->total_real_chars = static_cast<int>(out->chars.size());
    for (const Char& c : out->chars) if (c.rotated) out->rotated_chars++;
}

// #363 (rotation-aware BuildWords, "variant H" of the issue's investigation): the gap and
// baseline tests below are run in a character's own TEXT space rather than page space, so a
// 90-degree-rotated run (whose advance moves along page Y, not page X) is judged by the exact
// same rule that already works for upright text, instead of having every consecutive pair fail
// the page-space baseline test and every rotated character come out as its own one-glyph word
// (measured on the real corpus: 3.4% of characters, ~97% of the token-fidelity excess this
// closes). `Linear2` is the linear (rotation/scale, translation dropped) part of a character's
// FPDFText_GetMatrix; every use below transforms a DIFFERENCE of two page-space points (a gap,
// a baseline delta), and translation cancels exactly in a difference, which is why dropping it
// is safe rather than an approximation. For upright, unscaled text this matrix is the identity,
// its inverse is the identity too, and the arithmetic below reduces to exactly the page-space
// gap/baseline arithmetic kWordGapEm/kBaselineEm were calibrated against — one code path, both
// halves, matching the design's requirement that the normal case not regress.
struct Linear2 { double a = 1, b = 0, c = 0, d = 1; };

// Inverts the 2x2 linear map [a c; b d] (FS_MATRIX's own a/b/c/d convention, fpdfview.h: x' =
// a*x + c*y, y' = b*x + d*y). A near-singular matrix should not occur for a real glyph, but
// falls back to the identity — i.e. the old page-space arithmetic — rather than dividing by
// (near) zero.
Linear2 InvertLinear(const Linear2& m) {
    const double det = m.a * m.d - m.b * m.c;
    if (std::fabs(det) < 1e-9) return Linear2();
    const double inv_det = 1.0 / det;
    Linear2 r;
    r.a = m.d * inv_det;
    r.b = -m.b * inv_det;
    r.c = -m.c * inv_det;
    r.d = m.a * inv_det;
    return r;
}

void ApplyLinear(const Linear2& inv, double x, double y, double* xp, double* yp) {
    *xp = inv.a * x + inv.c * y;
    *yp = inv.b * x + inv.d * y;
}

// The min/max x' (`inv`'s space) among a page-space axis-aligned box's four corners — needed
// because a box that is axis-aligned in page space is not, in general, axis-aligned once mapped
// through a rotated character's inverse matrix, so its "leading" and "trailing" corners along
// the transformed advance are not always the ones loose_l/loose_r alone would pick.
double TransformBoxMinX(const Linear2& inv, double l, double r, double b, double t) {
    double x, y, m;
    ApplyLinear(inv, l, b, &x, &y); m = x;
    ApplyLinear(inv, l, t, &x, &y); m = (std::min)(m, x);
    ApplyLinear(inv, r, b, &x, &y); m = (std::min)(m, x);
    ApplyLinear(inv, r, t, &x, &y); m = (std::min)(m, x);
    return m;
}
double TransformBoxMaxX(const Linear2& inv, double l, double r, double b, double t) {
    double x, y, m;
    ApplyLinear(inv, l, b, &x, &y); m = x;
    ApplyLinear(inv, l, t, &x, &y); m = (std::max)(m, x);
    ApplyLinear(inv, r, b, &x, &y); m = (std::max)(m, x);
    ApplyLinear(inv, r, t, &x, &y); m = (std::max)(m, x);
    return m;
}

// design §1.2 "Words": consecutive real characters (PDFium's own order) on one baseline join
// while the glyph-box gap is <= 0.2 em. `only_rotated` selects which half of PageWork::chars
// this builds words from (the design's "unclassified" text — rotated, overlapping — is kept
// out of normal line/heading/paragraph grouping and becomes one trailing paragraph instead).
std::vector<Word> BuildWords(const std::vector<Char>& chars, const std::vector<int>& indices) {
    std::vector<Word> words;
    Word cur;
    bool have = false;
    int prev = -1;
    for (int i : indices) {
        const Char& c = chars[static_cast<size_t>(i)];
        bool start_new = !have || c.preceded_by_break;   // PDFium's own generated break, respected (see Char::preceded_by_break)
        if (have && !start_new) {
            const Char& p = chars[static_cast<size_t>(prev)];
            // Both characters' loose boxes and origins, mapped into c's own text space (#363,
            // "variant H" -- see the comment above Linear2).
            const Linear2 inv = InvertLinear(Linear2{c.mat_a, c.mat_b, c.mat_c, c.mat_d});
            const double p_max_x = TransformBoxMaxX(inv, p.loose_l, p.loose_r, p.loose_b, p.loose_t);
            const double c_min_x = TransformBoxMinX(inv, c.loose_l, c.loose_r, c.loose_b, c.loose_t);
            const double gap = c_min_x - p_max_x;
            double p_origin_xp, p_origin_yp, c_origin_xp, c_origin_yp;
            ApplyLinear(inv, p.origin_x, p.origin_y, &p_origin_xp, &p_origin_yp);
            ApplyLinear(inv, c.origin_x, c.origin_y, &c_origin_xp, &c_origin_yp);
            const double baseline_delta = std::fabs(c_origin_yp - p_origin_yp);
            const double em = Em(c.font_size > 0 ? c.font_size : cur.font_size);
            bool same_baseline = baseline_delta <= kBaselineEm * em;
            // #363 follow-up: a tight forward gap with a moderate vertical offset (see the
            // kSuperscriptGapEm/kSuperscriptOffsetEm comment above) is treated as one word even
            // though the plain baseline test above rejects it.
            if (!same_baseline && gap >= 0.0 && gap <= kSuperscriptGapEm * em &&
                baseline_delta <= kSuperscriptOffsetEm * em) {
                same_baseline = true;
            }
            if (!same_baseline || gap > kWordGapEm * em) start_new = true;
        }
        if (start_new) {
            if (have) words.push_back(cur);
            cur = Word();
            cur.l = c.l; cur.b = c.b; cur.r = c.r; cur.t = c.t; cur.font_size = c.font_size;
            have = true;
        } else {
            cur.l = (std::min)(cur.l, c.l);
            cur.b = (std::min)(cur.b, c.b);
            cur.r = (std::max)(cur.r, c.r);
            cur.t = (std::max)(cur.t, c.t);
        }
        cur.chars.push_back(i);
        prev = i;
    }
    if (have) words.push_back(cur);
    return words;
}

double WordHeight(const Word& w) { return w.t - w.b; }
double WordCentre(const Word& w) { return (w.t + w.b) / 2.0; }
double LineCentre(const Line& l) { return (l.t + l.b) / 2.0; }

// design §1.2 "Lines": the same rule BuildLines uses (megapdf_core.h:292-295), reused here at
// word granularity so the two policies never drift apart: words whose vertical centres are
// within half the taller word's height share a baseline cluster; the cluster then splits
// wherever the horizontal gap between consecutive (left-to-right) words exceeds twice the
// larger of the two font sizes — the column/page-number-gutter split BuildLines also makes.
std::vector<Line> BuildLines(const std::vector<Word>& words) {
    std::vector<Line> lines;
    std::vector<bool> used(words.size(), false);
    for (size_t i = 0; i < words.size(); i++) {
        if (used[i]) continue;
        std::vector<size_t> members{i};
        used[i] = true;
        for (size_t j = i + 1; j < words.size(); j++) {
            if (used[j]) continue;
            const double tol = (std::max)(WordHeight(words[i]), WordHeight(words[j])) * kLineCentreOverlapFactor;
            if (std::fabs(WordCentre(words[i]) - WordCentre(words[j])) <= tol) {
                members.push_back(j);
                used[j] = true;
            }
        }
        std::stable_sort(members.begin(), members.end(), [&](size_t a, size_t b) { return words[a].l < words[b].l; });
        std::vector<size_t> current{members[0]};
        auto flush = [&]() {
            Line line;
            line.words.assign(current.begin(), current.end());
            const auto& first = words[current[0]];
            line.l = first.l; line.b = first.b; line.r = first.r; line.t = first.t;
            for (size_t k = 1; k < current.size(); k++) {
                const auto& w = words[current[k]];
                line.l = (std::min)(line.l, w.l);
                line.b = (std::min)(line.b, w.b);
                line.r = (std::max)(line.r, w.r);
                line.t = (std::max)(line.t, w.t);
            }
            lines.push_back(std::move(line));
        };
        for (size_t k = 1; k < members.size(); k++) {
            const auto& prev = words[current.back()];
            const auto& next = words[members[k]];
            const double gap = next.l - prev.r;
            const double bigger = (std::max)(prev.font_size, next.font_size);
            if (gap > bigger * kLineSplitFontSizeFactor) {
                flush();
                current.clear();
            }
            current.push_back(members[k]);
        }
        flush();
    }
    std::stable_sort(lines.begin(), lines.end(), [](const Line& a, const Line& b) {
        if (a.t != b.t) return a.t > b.t;
        if (a.l != b.l) return a.l < b.l;
        return a.words[0] < b.words[0];
    });
    return lines;
}

std::vector<unsigned int> WordCodepoints(const std::vector<Char>& chars, const Word& w) {
    std::vector<unsigned int> cps;
    cps.reserve(w.chars.size());
    for (int i : w.chars) cps.push_back(chars[static_cast<size_t>(i)].unicode);
    return cps;
}

// The line's words, digit-normalised (a maximal run of digits becomes one '#') and joined by
// single spaces — the key furniture repeats are matched on (design §1.2 "Furniture").
std::u32string NormaliseLineText(const std::vector<Char>& chars, const std::vector<Word>& words, const Line& line) {
    std::u32string out;
    for (size_t k = 0; k < line.words.size(); k++) {
        if (k > 0) out.push_back(U' ');
        const auto cps = WordCodepoints(chars, words[static_cast<size_t>(line.words[k])]);
        bool in_digits = false;
        for (unsigned int cp : cps) {
            const unsigned int lower = (cp >= U'A' && cp <= U'Z') ? cp + 32 : cp;
            if (IsAsciiDigit(cp)) {
                if (!in_digits) { out.push_back(U'#'); in_digits = true; }
            } else {
                in_digits = false;
                out.push_back(static_cast<char32_t>(lower));
            }
        }
    }
    return out;
}

// design §1.2 "Furniture": "any such line that is only a number, `Page # of #`, `#/#` or
// `- # -`" — checked against the normalised text with its spaces collapsed away, since the
// exact spacing of a page-number line is not the point.
bool MatchesBarePageNumberShape(const std::u32string& normalised) {
    std::u32string tight;
    for (char32_t c : normalised) if (c != U' ') tight.push_back(c);
    if (tight == U"#") return true;
    if (tight == U"#/#") return true;
    if (tight == U"-#-") return true;
    // "page # of #", spaces already stripped from `tight`.
    if (tight == U"page#of#") return true;
    return false;
}

struct FurnitureLine {
    int page_index;
    int line_index;   // index into that page's `lines` before removal
};

// Cross-page furniture detection (design §1.2 "Furniture"): a top/bottom-8%-band line whose
// digit-normalised text recurs on at least three pages or half the loaded range, whichever
// is smaller, or that alone matches a bare page-number shape.
//
// "Recurs" needs at least two occurrences by its ordinary meaning; design §1.2's "at least
// three pages or half the loaded pages, whichever is smaller" is degenerate for a one-page
// load (min(3, ceil(1/2)) = 1), which the single-page page-number-shape rule exists to
// cover on its own — so the repeat rule here has a floor of 2 pages regardless of the
// formula, a judgment call this file's PR description explains.
std::vector<FurnitureLine> DetectFurniture(std::vector<PageWork>& pages) {
    std::vector<FurnitureLine> furniture;
    std::map<std::u32string, std::vector<FurnitureLine>> groups;
    for (auto& pw : pages) {
        for (size_t li = 0; li < pw.lines.size(); li++) {
            const Line& line = pw.lines[li];
            if (pw.height <= 0) continue;
            const double centre = (line.t + line.b) / 2.0;
            const bool top_band = (pw.height - centre) <= pw.height * kFurnitureBandFraction;
            const bool bottom_band = centre <= pw.height * kFurnitureBandFraction;
            if (!top_band && !bottom_band) continue;
            const std::u32string norm = NormaliseLineText(pw.chars, pw.words, line);
            if (norm.empty()) continue;
            if (MatchesBarePageNumberShape(norm)) {
                furniture.push_back({pw.page_index, static_cast<int>(li)});
                continue;
            }
            groups[norm].push_back({pw.page_index, static_cast<int>(li)});
        }
    }
    const int page_count = static_cast<int>(pages.size());
    const int required = (std::max)(2, (std::min)(3, (page_count + 1) / 2));
    for (auto& entry : groups) {
        if (static_cast<int>(entry.second.size()) >= required) {
            furniture.insert(furniture.end(), entry.second.begin(), entry.second.end());
        }
    }
    return furniture;
}

// design §1.2 "Body size": the character-count-weighted modal font size over the loaded
// range, rounded to 0.5 pt. Ties (equally frequent sizes) resolve to the smaller size, since
// std::map iterates its keys ascending — deterministic, and documented here rather than left
// to iteration order accidentally deciding it.
// Character-weighted modal size over `pages`' LINES, not their raw character lists: called
// after furniture is pulled out of each page's `lines` (BuildStructure does this before
// calling), so a running header/footer repeated on every page cannot out-vote the body text
// it surrounds. A furniture.pdf fixture with a 10 pt header/footer and 12 pt body is what
// found this — counted over every character, the two 10 pt furniture lines per page
// out-weigh the one 12 pt body line, so the body text itself came out above the (wrong) 10 pt
// "body" size and mis-read as a heading.
double ComputeBodySize(const std::vector<PageWork>& pages) {
    std::map<double, long long> counts;
    for (const auto& pw : pages) {
        for (const auto& line : pw.lines) {
            for (int wi : line.words) {
                const Word& w = pw.words[static_cast<size_t>(wi)];
                for (int ci : w.chars) {
                    const double size = pw.chars[static_cast<size_t>(ci)].font_size;
                    if (size <= 0) continue;
                    counts[std::round(size * 2.0) / 2.0]++;
                }
            }
        }
    }
    double best = 12.0;
    long long best_count = -1;
    for (const auto& entry : counts) {
        if (entry.second > best_count) { best_count = entry.second; best = entry.first; }
    }
    return best;
}

// ---------------------------------------------------------------------------
// Reading order: recursive XY-cut (design §1.2 "Columns and reading order").
// ---------------------------------------------------------------------------

struct XyCutter {
    const std::vector<Line>& lines;
    int vertical_cuts = 0;
    std::vector<int> order;
    // The width of the leaf region each line ended up in — an approximation of "column
    // width" for the heading test (design §1.2: a bold-at-body heading is "shorter than 70%
    // of its column width"), which the design does not otherwise define outside a column
    // layout. Indexed by the same line index as `lines`; -1 until a leaf assigns it.
    std::vector<double> column_width;

    explicit XyCutter(const std::vector<Line>& l) : lines(l), column_width(l.size(), -1) {}

    void SortLeaf(std::vector<int>* idx) {
        std::stable_sort(idx->begin(), idx->end(), [&](int a, int b) {
            if (lines[static_cast<size_t>(a)].t != lines[static_cast<size_t>(b)].t)
                return lines[static_cast<size_t>(a)].t > lines[static_cast<size_t>(b)].t;
            return lines[static_cast<size_t>(a)].l < lines[static_cast<size_t>(b)].l;
        });
        double lo = 1e18, hi = -1e18;
        for (int i : *idx) {
            lo = (std::min)(lo, lines[static_cast<size_t>(i)].l);
            hi = (std::max)(hi, lines[static_cast<size_t>(i)].r);
        }
        const double width = idx->empty() ? 0 : (hi - lo);
        for (int i : *idx) column_width[static_cast<size_t>(i)] = width;
    }

    double MedianPitch(const std::vector<int>& idx) const {
        std::vector<double> centres;
        centres.reserve(idx.size());
        for (int i : idx) centres.push_back((lines[static_cast<size_t>(i)].t + lines[static_cast<size_t>(i)].b) / 2.0);
        std::sort(centres.begin(), centres.end(), std::greater<double>());
        std::vector<double> pitches;
        for (size_t i = 1; i < centres.size(); i++) pitches.push_back(centres[i - 1] - centres[i]);
        if (pitches.empty()) return 12.0;
        std::sort(pitches.begin(), pitches.end());
        return pitches[pitches.size() / 2];
    }

    // The largest horizontal whitespace band between vertically-adjacent lines, if it beats
    // the threshold. Returns {gap_size, split_y} or gap_size < 0 when none qualifies.
    std::pair<double, double> BestHorizontalCut(const std::vector<int>& idx, double median_pitch) const {
        std::vector<int> byTop = idx;
        std::stable_sort(byTop.begin(), byTop.end(), [&](int a, int b) {
            return lines[static_cast<size_t>(a)].t > lines[static_cast<size_t>(b)].t;
        });
        double best_gap = -1, best_y = 0;
        for (size_t i = 1; i < byTop.size(); i++) {
            const Line& above = lines[static_cast<size_t>(byTop[i - 1])];
            const Line& below = lines[static_cast<size_t>(byTop[i])];
            const double gap = above.b - below.t;
            if (gap > kHorizontalBandPitchFactor * median_pitch && gap > best_gap) {
                best_gap = gap;
                best_y = (above.b + below.t) / 2.0;
            }
        }
        return {best_gap, best_y};
    }

    // A vertical whitespace gutter with no line's [l, r] interval crossing it, over the
    // region's full height (implicit: it is free across every line, regardless of that
    // line's own y-position). Returns {gutter_width, split_x} or width < 0 when none
    // qualifies, or the region has fewer than kVerticalGutterMinLines lines.
    std::pair<double, double> BestVerticalCut(const std::vector<int>& idx, double em) const {
        if (static_cast<int>(idx.size()) < kVerticalGutterMinLines) return {-1, 0};
        std::vector<std::pair<double, int>> events;   // x, +1 start / -1 end
        for (int i : idx) {
            events.emplace_back(lines[static_cast<size_t>(i)].l, 1);
            events.emplace_back(lines[static_cast<size_t>(i)].r, -1);
        }
        std::sort(events.begin(), events.end(), [](const auto& a, const auto& b) { return a.first < b.first; });
        double best_width = -1, best_x = 0;
        int coverage = 0;
        double free_start = 0;
        bool in_free = false;
        for (size_t k = 0; k < events.size(); k++) {
            if (coverage == 0 && k > 0 && events[k].first > events[k - 1].first) {
                in_free = true;
                free_start = events[k - 1].first;
            }
            if (in_free && coverage > 0) in_free = false;
            coverage += events[k].second;
            if (in_free && coverage != 0) {
                const double width = events[k].first - free_start;
                if (width > kVerticalGutterEm * em && width > best_width) {
                    best_width = width;
                    best_x = (free_start + events[k].first) / 2.0;
                }
                in_free = false;
            }
        }
        return {best_width, best_x};
    }

    void Cut(std::vector<int> idx, int depth) {
        if (idx.size() <= 1 || depth > kMaxCutDepth) {
            SortLeaf(&idx);
            order.insert(order.end(), idx.begin(), idx.end());
            return;
        }
        double sum_size = 0;
        int n = 0;
        for (int i : idx) {
            const Line& l = lines[static_cast<size_t>(i)];
            sum_size += (l.t - l.b);
            n++;
        }
        const double em = n > 0 ? sum_size / n : 10.0;
        const double median_pitch = MedianPitch(idx);
        const auto horiz = BestHorizontalCut(idx, median_pitch);
        const auto vert = BestVerticalCut(idx, em);
        const bool has_h = horiz.first > 0;
        const bool has_v = vert.first > 0;
        if (!has_h && !has_v) {
            SortLeaf(&idx);
            order.insert(order.end(), idx.begin(), idx.end());
            return;
        }
        // "cut on the larger gap first".
        if (has_v && (!has_h || vert.first >= horiz.first)) {
            vertical_cuts++;
            std::vector<int> left, right;
            for (int i : idx) {
                const Line& l = lines[static_cast<size_t>(i)];
                ((l.l + l.r) / 2.0 < vert.second ? left : right).push_back(i);
            }
            if (left.empty() || right.empty()) {   // degenerate: treat as a leaf instead of looping
                SortLeaf(&idx);
                order.insert(order.end(), idx.begin(), idx.end());
                return;
            }
            Cut(std::move(left), depth + 1);
            Cut(std::move(right), depth + 1);
        } else {
            std::vector<int> top, bottom;
            for (int i : idx) {
                const Line& l = lines[static_cast<size_t>(i)];
                (((l.t + l.b) / 2.0) > horiz.second ? top : bottom).push_back(i);
            }
            if (top.empty() || bottom.empty()) {
                SortLeaf(&idx);
                order.insert(order.end(), idx.begin(), idx.end());
                return;
            }
            Cut(std::move(top), depth + 1);
            Cut(std::move(bottom), depth + 1);
        }
    }
};

// Returns the reading order (indices into `lines`) and whether the row-by-row fallback fired
// (design §1.2: "A page that needs more than three vertical cuts ... is read row by row ...
// with the confidence penalty"). `column_width` comes back parallel to `lines`: the width of
// the leaf region each line ended up in (see XyCutter::SortLeaf).
std::vector<int> OrderLines(const std::vector<Line>& lines, bool* too_many_columns, std::vector<double>* column_width) {
    *too_many_columns = false;
    column_width->assign(lines.size(), 0.0);
    if (lines.empty()) return {};
    std::vector<int> all(lines.size());
    for (size_t i = 0; i < lines.size(); i++) all[i] = static_cast<int>(i);
    XyCutter cutter(lines);
    cutter.Cut(all, 0);
    std::vector<int> result = cutter.order;
    if (cutter.vertical_cuts > kMaxVerticalCuts) {
        *too_many_columns = true;
        cutter.SortLeaf(&all);
        result = all;
    }
    *column_width = cutter.column_width;
    return result;
}

// ---------------------------------------------------------------------------
// Blocks: spans, headings, list items, paragraphs, hyphen-joining.
// ---------------------------------------------------------------------------

struct SpanImpl {
    megapdf_span info{};
    U16 text;
};

struct BlockImpl {
    megapdf_block info{};   // zero-initialised: every construction site sets only the fields it needs
    U16 text;
    U16 marker;
    U16 alt;
    std::vector<SpanImpl> spans;
    // Bookkeeping for the whole-range passes below; not part of the public surface.
    double heading_size = 0;
    bool heading_bold_at_body = false;
};

// One character (or one synthetic separator: a space, or nothing when a hyphen is joined
// away) in a block's final text, in order. `has_bounds` is false for a separator: it has a
// glyph-less place in the text but contributes nothing to any span's bounds. Building a
// block's text and its spans from the same Piece stream is what keeps
// "MEGAPDF_BLOCK_TEXT is exactly the concatenation of its spans" (SDD §6.2 contract 6)
// true by construction rather than by convention.
struct Piece {
    unsigned int cp = 0;
    bool has_bounds = false;
    double l = 0, b = 0, r = 0, t = 0;
    int object_index = -1;
    bool bold = false, italic = false, mono = false;
    double font_size = 0;
};

void AppendWordPieces(std::vector<Piece>* out, const PageWork& pw, const Word& w) {
    for (int ci : w.chars) {
        const Char& c = pw.chars[static_cast<size_t>(ci)];
        Piece p;
        p.cp = c.unicode;
        p.has_bounds = true;
        p.l = c.l; p.b = c.b; p.r = c.r; p.t = c.t;
        p.object_index = c.object_index;
        p.bold = c.bold; p.italic = c.italic; p.mono = c.mono;
        p.font_size = c.font_size;
        out->push_back(p);
    }
}

void AppendSeparator(std::vector<Piece>* out, unsigned int cp) {
    Piece p;
    p.cp = cp;
    p.has_bounds = false;
    if (!out->empty()) {
        const Piece& prev = out->back();
        p.object_index = prev.object_index;
        p.bold = prev.bold; p.italic = prev.italic; p.mono = prev.mono;
        p.font_size = prev.font_size;
    }
    out->push_back(p);
}

// Builds the piece stream for a run of lines already chosen as one block (design §1.2):
// words within a line join with a single space; a wrapped line joins the next with a single
// space, EXCEPT when the line ends in a hyphen-like character (design §1.2 "Hyphenation"):
// U+00AD is always removed and the halves joined with no space; a trailing U+002D/U+2010 is
// removed and joined with no space when the next line starts with a lowercase letter,
// otherwise it stays and the halves still join with no space. `first_word_offset` skips a
// list item's marker word (index 0) on its first line.
std::vector<Piece> BuildPieces(const PageWork& pw, const std::vector<int>& line_indices, size_t first_word_offset) {
    std::vector<Piece> pieces;
    bool any_emitted = false;
    bool suppress_next_separator = false;
    for (size_t li = 0; li < line_indices.size(); li++) {
        const Line& line = pw.lines[static_cast<size_t>(line_indices[li])];
        const size_t wstart = (li == 0) ? (std::min)(first_word_offset, line.words.size()) : 0;
        for (size_t wi = wstart; wi < line.words.size(); wi++) {
            if (any_emitted && !suppress_next_separator) AppendSeparator(&pieces, ' ');
            suppress_next_separator = false;
            AppendWordPieces(&pieces, pw, pw.words[static_cast<size_t>(line.words[wi])]);
            any_emitted = true;
        }
        if (li + 1 < line_indices.size() && line.words.size() > wstart) {
            const Word& last_word = pw.words[static_cast<size_t>(line.words.back())];
            const auto last_cps = WordCodepoints(pw.chars, last_word);
            const unsigned int last_cp = last_cps.empty() ? 0 : last_cps.back();
            // Char::is_hyphen's comment explains why this cannot just compare last_cp: PDFium
            // masks a line-end hyphen's own GetUnicode to 2, so it is checked directly, and
            // the literal code points are kept as a second path for a hyphen PDFium did not
            // flag (e.g. one it did not consider to be at a line-wrap position).
            const bool last_is_hyphen_char = !last_word.chars.empty() && pw.chars[static_cast<size_t>(last_word.chars.back())].is_hyphen;
            const Line& next_line = pw.lines[static_cast<size_t>(line_indices[li + 1])];
            bool hyphen_join = false, strip = false;
            if (last_cp == kSoftHyphen) {
                hyphen_join = true;
                strip = true;
            } else if (last_cp == kHyphenMinus || last_cp == kHyphenChar || last_is_hyphen_char) {
                hyphen_join = true;
                if (!next_line.words.empty()) {
                    const auto next_cps = WordCodepoints(pw.chars, pw.words[static_cast<size_t>(next_line.words[0])]);
                    strip = !next_cps.empty() && IsAsciiLower(next_cps[0]);
                }
            }
            if (hyphen_join) {
                if (strip) {
                    if (!pieces.empty()) pieces.pop_back();
                } else if (!pieces.empty() && last_cp != kHyphenMinus && last_cp != kHyphenChar) {
                    // Kept, but PDFium's masked code point (2, not the real character —
                    // Char::is_hyphen's comment) cannot go out in the block's text as-is;
                    // U+002D is design §1.2's own plain-hyphen spelling for this case.
                    pieces.back().cp = kHyphenMinus;
                }
                suppress_next_separator = true;
            }
        }
    }
    return pieces;
}

// Slices a piece stream into spans (design §1's megapdf_span): maximal runs sharing the same
// page-level object, weight/italic/monospace flags and font size (within
// kFontSizeSpanToleranceRatio of the run's first real character) — a break the design leaves
// to the implementation (it specifies spans' *fields*, not their segmentation).
std::vector<SpanImpl> SliceIntoSpans(const std::vector<Piece>& pieces) {
    std::vector<SpanImpl> spans;
    size_t i = 0;
    while (i < pieces.size()) {
        const Piece& anchor = pieces[i];
        size_t j = i;
        double l = 1e18, b = 1e18, r = -1e18, t = -1e18;
        bool any_bounds = false;
        std::vector<unsigned int> cps;
        while (j < pieces.size()) {
            const Piece& p = pieces[j];
            if (p.has_bounds) {
                const bool same_style = p.object_index == anchor.object_index && p.bold == anchor.bold &&
                    p.italic == anchor.italic && p.mono == anchor.mono &&
                    std::fabs(p.font_size - anchor.font_size) <= kFontSizeSpanToleranceRatio * (std::max)(1.0, anchor.font_size);
                if (!same_style) break;
                l = (std::min)(l, p.l); b = (std::min)(b, p.b); r = (std::max)(r, p.r); t = (std::max)(t, p.t);
                any_bounds = true;
            }
            cps.push_back(p.cp);
            j++;
        }
        SpanImpl span;
        span.info.flags = (anchor.bold ? MEGAPDF_SPAN_BOLD : 0) | (anchor.italic ? MEGAPDF_SPAN_ITALIC : 0) |
                          (anchor.mono ? MEGAPDF_SPAN_MONOSPACE : 0);
        span.info.font_size = anchor.font_size;
        span.info.size_ratio = 0;   // filled in once the range's body size is known (needs a second pass)
        span.info.bounds = any_bounds ? megapdf_rect{l, b, r, t} : megapdf_rect{0, 0, 0, 0};
        span.info.object_index = anchor.object_index;
        span.text = EncodeUtf16(cps);
        spans.push_back(std::move(span));
        i = j;
    }
    return spans;
}

U16 ConcatSpanText(const std::vector<SpanImpl>& spans) {
    U16 out;
    for (const auto& s : spans) out.insert(out.end(), s.text.begin(), s.text.end());
    return out;
}

double LineFontSize(const PageWork& pw, const Line& line) {
    double weighted = 0;
    int n = 0;
    for (int wi : line.words) {
        const Word& w = pw.words[static_cast<size_t>(wi)];
        weighted += w.font_size * static_cast<double>(w.chars.size());
        n += static_cast<int>(w.chars.size());
    }
    return n > 0 ? weighted / n : 0;
}

bool LineIsBold(const PageWork& pw, const Line& line) {
    int bold_chars = 0, total = 0;
    for (int wi : line.words) {
        for (int ci : pw.words[static_cast<size_t>(wi)].chars) {
            total++;
            if (pw.chars[static_cast<size_t>(ci)].bold) bold_chars++;
        }
    }
    return total > 0 && bold_chars * 2 >= total;
}

double PageMedianPitch(const std::vector<Line>& lines) {
    std::vector<double> centres;
    centres.reserve(lines.size());
    for (const auto& l : lines) centres.push_back((l.t + l.b) / 2.0);
    std::sort(centres.begin(), centres.end(), std::greater<double>());
    std::vector<double> pitches;
    for (size_t i = 1; i < centres.size(); i++) pitches.push_back(centres[i - 1] - centres[i]);
    if (pitches.empty()) return 12.0;
    std::sort(pitches.begin(), pitches.end());
    return pitches[pitches.size() / 2];
}

// A line's first word is a marker (design §1.2 "Lists") followed by a gap >= 0.5 em and more
// body text on the same line.
bool LineStartsListItem(const PageWork& pw, const Line& line) {
    if (line.words.size() < 2) return false;
    const Word& marker = pw.words[static_cast<size_t>(line.words[0])];
    const auto cps = WordCodepoints(pw.chars, marker);
    if (!IsListMarker(cps)) return false;
    const Word& body0 = pw.words[static_cast<size_t>(line.words[1])];
    const double gap = body0.l - marker.r;
    return gap >= kListMarkerGapEm * Em(marker.font_size);
}

std::vector<double> ComputeListDepthClusters(const PageWork& pw, const std::vector<int>& order, double body_size) {
    std::vector<double> xs;
    for (int li : order) {
        const Line& line = pw.lines[static_cast<size_t>(li)];
        if (LineStartsListItem(pw, line)) xs.push_back(line.l);
    }
    std::sort(xs.begin(), xs.end());
    std::vector<double> clusters;
    for (double x : xs) {
        if (clusters.empty() || x - clusters.back() > kListDepthClusterEm * Em(body_size)) clusters.push_back(x);
    }
    return clusters;
}

int DepthForX(const std::vector<double>& clusters, double x) {
    int best = 0;
    double best_dist = 1e18;
    for (size_t i = 0; i < clusters.size(); i++) {
        const double d = std::fabs(x - clusters[i]);
        if (d < best_dist) { best_dist = d; best = static_cast<int>(i); }
    }
    return best + 1;
}

// A tentative heading test for ONE line (design §1.2 "Headings"), used both to decide
// whether to start a heading group and, for the size-based route, whether a following line
// continues it. The bold-at-body route additionally needs the gap that follows the whole
// group, which the caller checks once the group's extent is known.
bool LineQualifiesBySize(double line_size, double body_size) { return line_size >= kHeadingSizeRatio * body_size; }

// design §1.2 "Paragraphs" / "Headings": gathers consecutive lines from `order[i]` into one
// content block, dispatching to a heading, a list item or a paragraph. Returns the number of
// lines consumed (always >= 1).
size_t GatherOneBlock(const PageWork& pw, const std::vector<int>& order, size_t i, double body_size,
                      double raw_median_pitch, const std::vector<double>& column_width,
                      const std::vector<double>& list_clusters, int page_index, BlockImpl* out) {
    const int li = order[i];
    const Line& line = pw.lines[static_cast<size_t>(li)];
    const double line_size = LineFontSize(pw, line);
    const double col_w = (li < static_cast<int>(column_width.size()) && column_width[static_cast<size_t>(li)] > 0)
                              ? column_width[static_cast<size_t>(li)]
                              : pw.width;
    // kMaxSingleLinePitchToBodySizeRatio's comment explains why: the page-relative median
    // alone cannot tell isolated lines from a wrapped paragraph when every line on the page
    // is equally isolated.
    const double median_pitch = (std::min)(raw_median_pitch, body_size * kMaxSingleLinePitchToBodySizeRatio);

    // --- Heading: size-based, up to kHeadingMaxLines lines ---
    if (LineQualifiesBySize(line_size, body_size)) {
        std::vector<int> group{li};
        size_t j = i + 1;
        while (j < order.size() && group.size() < static_cast<size_t>(kHeadingMaxLines)) {
            const int nli = order[j];
            const Line& nline = pw.lines[static_cast<size_t>(nli)];
            const double nsize = LineFontSize(pw, nline);
            const double pitch = LineCentre(pw.lines[static_cast<size_t>(group.back())]) - LineCentre(nline);
            if (LineQualifiesBySize(nsize, body_size) && pitch >= 0.5 * median_pitch && pitch < kParagraphPitchFactor * median_pitch) {
                group.push_back(nli);
                j++;
            } else {
                break;
            }
        }
        out->info.kind = MEGAPDF_BLOCK_HEADING;
        out->info.page = page_index;
        out->info.object_index = -1;
        out->heading_size = line_size;
        out->heading_bold_at_body = false;
        const auto pieces = BuildPieces(pw, group, 0);
        out->spans = SliceIntoSpans(pieces);
        out->text = ConcatSpanText(out->spans);
        double l = 1e18, b = 1e18, r = -1e18, t = -1e18;
        for (int gi : group) {
            const Line& gl = pw.lines[static_cast<size_t>(gi)];
            l = (std::min)(l, gl.l); b = (std::min)(b, gl.b); r = (std::max)(r, gl.r); t = (std::max)(t, gl.t);
        }
        out->info.bounds = megapdf_rect{l, b, r, t};
        return group.size();
    }

    // --- Heading: bold-at-body, one line, needs the trailing gap ---
    const bool line_bold = LineIsBold(pw, line);
    double next_gap = -1;
    if (i + 1 < order.size()) {
        const Line& nxt = pw.lines[static_cast<size_t>(order[i + 1])];
        next_gap = LineCentre(line) - LineCentre(nxt);
    }
    const double line_width = line.r - line.l;
    if (line_bold && line_size >= body_size - 0.01 && line_width < kHeadingBoldColumnWidthFactor * col_w &&
        next_gap >= kHeadingBoldGapPitchFactor * median_pitch) {
        out->info.kind = MEGAPDF_BLOCK_HEADING;
        out->info.page = page_index;
        out->info.object_index = -1;
        out->heading_size = line_size;
        out->heading_bold_at_body = true;
        const std::vector<int> group{li};
        const auto pieces = BuildPieces(pw, group, 0);
        out->spans = SliceIntoSpans(pieces);
        out->text = ConcatSpanText(out->spans);
        out->info.bounds = megapdf_rect{line.l, line.b, line.r, line.t};
        return 1;
    }

    // --- List item ---
    if (LineStartsListItem(pw, line)) {
        const Word& marker_word = pw.words[static_cast<size_t>(line.words[0])];
        const Word& first_body_word = pw.words[static_cast<size_t>(line.words[1])];
        std::vector<int> group{li};
        size_t j = i + 1;
        const double item_text_left = first_body_word.l;
        while (j < order.size()) {
            const int nli = order[j];
            const Line& nline = pw.lines[static_cast<size_t>(nli)];
            if (LineStartsListItem(pw, nline)) break;
            if (LineQualifiesBySize(LineFontSize(pw, nline), body_size)) break;
            if (std::fabs(nline.l - item_text_left) > kParagraphLeftEdgeToleranceEm * Em(body_size)) break;
            const double pitch = LineCentre(pw.lines[static_cast<size_t>(group.back())]) - LineCentre(nline);
            if (pitch >= kParagraphGapPitchFactor * median_pitch) break;
            group.push_back(nli);
            j++;
        }
        out->info.kind = MEGAPDF_BLOCK_LIST_ITEM;
        out->info.page = page_index;
        out->info.object_index = -1;
        out->info.level = DepthForX(list_clusters, line.l);
        const auto marker_cps = WordCodepoints(pw.chars, marker_word);
        out->marker = EncodeUtf16(marker_cps);
        const auto pieces = BuildPieces(pw, group, 1);
        out->spans = SliceIntoSpans(pieces);
        out->text = ConcatSpanText(out->spans);
        double l = 1e18, b = 1e18, r = -1e18, t = -1e18;
        for (int gi : group) {
            const Line& gl = pw.lines[static_cast<size_t>(gi)];
            l = (std::min)(l, gl.l); b = (std::min)(b, gl.b); r = (std::max)(r, gl.r); t = (std::max)(t, gl.t);
        }
        out->info.bounds = megapdf_rect{l, b, r, t};
        return group.size();
    }

    // --- Paragraph (the default) ---
    {
        std::vector<int> group{li};
        size_t j = i + 1;
        const double first_left = line.l;
        while (j < order.size()) {
            const int nli = order[j];
            const Line& nline = pw.lines[static_cast<size_t>(nli)];
            if (LineQualifiesBySize(LineFontSize(pw, nline), body_size)) break;
            if (LineStartsListItem(pw, nline)) break;
            const double pitch = LineCentre(pw.lines[static_cast<size_t>(group.back())]) - LineCentre(nline);
            if (pitch >= kParagraphGapPitchFactor * median_pitch || pitch >= kParagraphPitchFactor * median_pitch) break;
            const double left_diff = nline.l - first_left;
            if (left_diff > kParagraphIndentEm * Em(body_size)) break;   // a first-line indent starts a new paragraph
            if (std::fabs(left_diff) > kParagraphLeftEdgeToleranceEm * Em(body_size)) break;
            group.push_back(nli);
            j++;
        }
        out->info.kind = MEGAPDF_BLOCK_PARAGRAPH;
        out->info.page = page_index;
        out->info.object_index = -1;
        const auto pieces = BuildPieces(pw, group, 0);
        out->spans = SliceIntoSpans(pieces);
        out->text = ConcatSpanText(out->spans);
        double l = 1e18, b = 1e18, r = -1e18, t = -1e18;
        for (int gi : group) {
            const Line& gl = pw.lines[static_cast<size_t>(gi)];
            l = (std::min)(l, gl.l); b = (std::min)(b, gl.b); r = (std::max)(r, gl.r); t = (std::max)(t, gl.t);
        }
        out->info.bounds = megapdf_rect{l, b, r, t};
        return group.size();
    }
}

// No genuine section heading is pure digits/currency/punctuation with no letter in it at all
// (a dollar amount, an account number, a bare code, a lone colon or dash) -- but this is a
// POSITIVE allowlist (digits plus the punctuation such a value is made of), not "contains no
// Latin letter": code review on this PR caught that the obvious negative phrasing (this file
// already has an ASCII/Latin-1/Latin-Extended-A IsLetterCp-style test elsewhere, e.g.
// IsAsciiLetter/IsRomanLetter above) would demote a genuine, isolated heading written in any
// script that test does not cover -- Cyrillic, Greek, Arabic, CJK -- merely because none of its
// letters are recognised, which is exactly the over-suppression this fix must not introduce.
// An allowlist cannot make that mistake: a real word in ANY script is not made entirely of
// digits and this punctuation set, whether or not this file's letter-detection covers its
// alphabet. At least one digit is required, so a heading that is pure punctuation (not a
// realistic bold-at-body heading, and not a numeric label either) does not qualify.
bool IsNumericLikePunctuation(unsigned int cp) {
    switch (cp) {
        case '.': case ',': case ':': case ';': case '-': case '(': case ')': case '/':
        case '%': case '#': case '$': case 0x00A3 /* £ */: case 0x20AC /* € */: case ' ':
            return true;
        default:
            return false;
    }
}

bool IsNumericLikeText(const U16& text) {
    bool saw_digit = false;
    for (unsigned short u : text) {
        if (IsAsciiDigit(u)) { saw_digit = true; continue; }
        if (!IsNumericLikePunctuation(u)) return false;
    }
    return saw_digit;
}

// #375: demotes (to PARAGRAPH -- every character stays in a block, design §1 item 8) the
// bold-at-body-size HEADING blocks kHeadingRunSuppressThreshold's comment explains are almost
// never real headings: a run of kHeadingRunSuppressThreshold or more of them back-to-back in
// `content` (HEADING/PARAGRAPH/LIST_ITEM blocks only, in reading order -- the same set
// BuildPageContent's own content stream holds before figures/fields/furniture are spliced in,
// so one of those between two heading candidates does not itself break a run), or a single one
// whose whole text is numeric-like regardless of run length. Runs are found by kind and
// heading_bold_at_body alone (not by proximity/style beyond that): a run's members already
// share "bold, <= body size, wide enough of a gap to end the previous block" by construction,
// since GatherOneBlock only emits a HEADING block that way.
//
// Called once per page (BuildPageContent), on that page's own `content` alone: a tabular/label
// run whose last row falls on one page and continues at the top of the next is judged as two
// separate, shorter runs rather than one, which can leave a short remainder run (as short as a
// single block on each side) unsuppressed by the run-length signal alone. Not joined across the
// page boundary here -- BuildPageContent has no easy access to the next/previous page's already-
// built content at the point this runs, and the corpus measurement in this PR's PR description
// (run lengths, not page boundaries) does not show this materially understating the fix's
// effect. The numeric-like check is unaffected either way, since it does not depend on run
// length.
void DemoteFalsePositiveHeadingRuns(std::vector<BlockImpl>* content) {
    size_t k = 0;
    while (k < content->size()) {
        BlockImpl& first = (*content)[k];
        if (first.info.kind != MEGAPDF_BLOCK_HEADING || !first.heading_bold_at_body) {
            k++;
            continue;
        }
        size_t j = k + 1;
        while (j < content->size() && (*content)[j].info.kind == MEGAPDF_BLOCK_HEADING &&
               (*content)[j].heading_bold_at_body) {
            j++;
        }
        const size_t run_len = j - k;
        const bool demote_run = run_len >= kHeadingRunSuppressThreshold;
        for (size_t m = k; m < j; m++) {
            BlockImpl& cand = (*content)[m];
            if (demote_run || IsNumericLikeText(cand.text)) {
                cand.info.kind = MEGAPDF_BLOCK_PARAGRAPH;
            }
        }
        k = j;
    }
}

// design §1 item 8 / §1.2: rotated or otherwise unclassified characters are kept out of
// normal grouping (BuildOnePage never hands them to GatherOneBlock) and become one trailing
// paragraph at the end of the page's order, rather than vanishing.
bool BuildLeftoverParagraph(const PageWork& pw, int page_index, BlockImpl* out) {
    if (pw.leftover_words.empty()) return false;
    std::vector<Line> leftover_lines = BuildLines(pw.leftover_words);
    if (leftover_lines.empty()) return false;
    std::vector<int> all(leftover_lines.size());
    for (size_t i = 0; i < all.size(); i++) all[i] = static_cast<int>(i);
    std::stable_sort(all.begin(), all.end(), [&](int a, int b) {
        if (leftover_lines[static_cast<size_t>(a)].t != leftover_lines[static_cast<size_t>(b)].t)
            return leftover_lines[static_cast<size_t>(a)].t > leftover_lines[static_cast<size_t>(b)].t;
        return leftover_lines[static_cast<size_t>(a)].l < leftover_lines[static_cast<size_t>(b)].l;
    });
    // BuildPieces/SliceIntoSpans read words through PageWork's own `words`/`chars`, so this
    // paragraph is assembled directly against a small local PageWork standing in for the
    // leftover words, keeping the rest of the pipeline (which only ever knows one `words`
    // list) unchanged.
    PageWork stand_in;
    stand_in.chars = pw.chars;
    stand_in.words = pw.leftover_words;
    stand_in.lines = leftover_lines;
    const auto pieces = BuildPieces(stand_in, all, 0);
    out->info.kind = MEGAPDF_BLOCK_PARAGRAPH;
    out->info.page = page_index;
    out->info.object_index = -1;
    out->spans = SliceIntoSpans(pieces);
    out->text = ConcatSpanText(out->spans);
    double l = 1e18, b = 1e18, r = -1e18, t = -1e18;
    for (int gi : all) {
        const Line& gl = leftover_lines[static_cast<size_t>(gi)];
        l = (std::min)(l, gl.l); b = (std::min)(b, gl.b); r = (std::max)(r, gl.r); t = (std::max)(t, gl.t);
    }
    out->info.bounds = megapdf_rect{l, b, r, t};
    return true;
}

// design §1.2 "Figures": image page objects with an area >= 1 cm^2.
std::vector<BlockImpl> BuildFigures(const megapdf_page* page, int page_index) {
    std::vector<BlockImpl> figures;
    const int count = megapdf_page_object_count(page);
    for (int i = 0; i < count; i++) {
        if (megapdf_object_type(page, i) != 3 /* FPDF_PAGEOBJ_IMAGE */) continue;
        megapdf_rect bounds{};
        if (megapdf_object_bounds(page, i, &bounds) != MEGAPDF_OK) continue;
        const double w_cm = (bounds.right - bounds.left) / kPointsPerCm;
        const double h_cm = (bounds.top - bounds.bottom) / kPointsPerCm;
        if (w_cm * h_cm < kFigureMinAreaCm2) continue;
        BlockImpl b;
        b.info.kind = MEGAPDF_BLOCK_FIGURE;
        b.info.page = page_index;
        b.info.bounds = bounds;
        b.info.object_index = i;
        figures.push_back(std::move(b));
    }
    return figures;
}

// design §1 item 7: form fields are blocks (kind FIELD), from the existing
// megapdf_form_fields_load (contract 3); empty text fields and unchecked boxes are omitted
// unless MEGAPDF_STRUCTURE_ALL_FIELDS.
std::vector<BlockImpl> BuildFields(const megapdf_page* page, int page_index, unsigned int flags) {
    std::vector<BlockImpl> out;
    megapdf_form_fields* fields = megapdf_form_fields_load(page);
    if (fields == nullptr) return out;
    const bool all_fields = (flags & MEGAPDF_STRUCTURE_ALL_FIELDS) != 0;
    const size_t n = megapdf_form_field_count(fields);
    for (size_t i = 0; i < n; i++) {
        megapdf_form_field f{};
        if (megapdf_form_field_get(fields, i, &f) != MEGAPDF_OK) continue;
        const size_t name_len = megapdf_form_field_string(fields, i, MEGAPDF_FIELD_NAME, nullptr, 0);
        U16 name(name_len);
        if (name_len > 0) megapdf_form_field_string(fields, i, MEGAPDF_FIELD_NAME, name.data(), name_len);
        const size_t value_len = megapdf_form_field_string(fields, i, MEGAPDF_FIELD_VALUE, nullptr, 0);
        U16 value(value_len);
        if (value_len > 0) megapdf_form_field_string(fields, i, MEGAPDF_FIELD_VALUE, value.data(), value_len);

        const bool is_checkish = f.kind == MEGAPDF_FIELD_CHECKBOX || f.kind == MEGAPDF_FIELD_RADIO;
        const bool has_content = is_checkish ? f.is_checked != 0 : !value.empty();
        if (!has_content && !all_fields) continue;

        BlockImpl b;
        b.info.kind = MEGAPDF_BLOCK_FIELD;
        b.info.page = page_index;
        b.info.bounds = f.bounds;
        b.info.object_index = -1;
        b.marker = name;
        b.text = value;
        out.push_back(std::move(b));
    }
    megapdf_form_fields_free(fields);
    return out;
}

// Splices `extra` blocks (figures, fields) into the already-ordered `content` sequence by
// vertical position: `content`'s own relative order is authoritative (it is what the XY-cut
// worked out), so an extra block is inserted just before the first content block that is
// below it, never used to reorder the content blocks themselves.
void SpliceByPosition(std::vector<BlockImpl>* content, std::vector<BlockImpl>&& extra) {
    for (auto& item : extra) {
        size_t pos = content->size();
        for (size_t k = 0; k < content->size(); k++) {
            if ((*content)[k].info.bounds.top < item.info.bounds.top) { pos = k; break; }
        }
        content->insert(content->begin() + static_cast<long>(pos), std::move(item));
    }
}

bool RectsOverlap(const megapdf_rect& a, const megapdf_rect& b) {
    constexpr double kEps = 0.5;   // points; touching edges are not an overlap
    return a.left < b.right - kEps && b.left < a.right - kEps && a.bottom < b.top - kEps && b.bottom < a.top - kEps;
}

// #384: true when `content_blocks` (already in final reading order) contains a pair of
// reading-order-adjacent blocks that are "geometrically distant" in the specific sense #384
// describes -- a jump that does not correspond to a normal column-to-column or page-to-page
// transition. Concretely: the two blocks sit in essentially the same horizontal band
// (kReadingOrderJumpOverlapFrac of the narrower one's width or more overlaps the other's) --
// so this is NOT a move to a new column, which legitimately jumps far with no horizontal
// overlap at all, whatever the distance -- yet the later block starts above where the earlier
// one ended by more than kConfidenceReadingOrderJumpBodySizes body-size units, i.e. the
// reading order went backward within what looks like one column. tools/structure-check's
// MaxBackwardJumpUnits (kept in sync by hand, the same convention structure_check.cpp's own
// kSoftHyphen comment documents for another #354-era measure) computes the identical test
// against contract 9's public bounds, which is how the constants above were picked and how
// #384's PR verifies this fires on the pages the corpus measurement flagged.
bool HasImplausibleReadingOrderJump(const std::vector<BlockImpl>& content_blocks, double body_size) {
    if (body_size <= 0) return false;
    for (size_t i = 1; i < content_blocks.size(); i++) {
        const megapdf_rect& a = content_blocks[i - 1].info.bounds;
        const megapdf_rect& b = content_blocks[i].info.bounds;
        const double overlap = (std::min)(a.right, b.right) - (std::max)(a.left, b.left);
        const double min_width = (std::min)(a.right - a.left, b.right - b.left);
        if (min_width <= 0 || overlap <= kReadingOrderJumpOverlapFrac * min_width) continue;   // different column
        const double backward = b.top - a.bottom;   // > 0: b starts above where a ended
        if (backward > kConfidenceReadingOrderJumpBodySizes * body_size) return true;
    }
    return false;
}

// design §1 item 6: 0-100 per page. Starts at 100; the three fixed deductions are named
// constants above; the fourth ("the share of characters left in no block") has none to name
// since it is proportional — see the comment at BuildOnePage's PAGE_IMAGE-free-text branch
// for why this implementation's share is close to zero in practice (rotated/unclassified
// text still becomes a block, just a lower-confidence trailing one). A fifth, #384's own
// reading-order-jump deduction, is folded in here too rather than kept as a separate pass, for
// the same reason the other four already share one function: this is the one place design §1
// item 6's whole point -- one page-level confidence number -- gets computed.
int ComputeConfidence(const PageWork& pw, bool too_many_columns, const std::vector<BlockImpl>& content_blocks,
                      double body_size) {
    int score = 100;
    if (pw.total_real_chars > 0) {
        const double rotated_share = static_cast<double>(pw.rotated_chars) / pw.total_real_chars;
        if (rotated_share > kConfidenceRotatedTextShare) score -= kConfidenceRotatedTextPenalty;
    }
    if (too_many_columns) score -= kConfidenceTooManyColumnsPenalty;
    for (size_t i = 0; i < content_blocks.size(); i++) {
        for (size_t j = i + 1; j < content_blocks.size(); j++) {
            if (RectsOverlap(content_blocks[i].info.bounds, content_blocks[j].info.bounds)) {
                score -= kConfidenceOverlapPenalty;
                i = content_blocks.size();   // once is enough (design: "minus 20 when blocks overlap", not per pair)
                break;
            }
        }
    }
    if (HasImplausibleReadingOrderJump(content_blocks, body_size)) score -= kConfidenceReadingOrderJumpPenalty;
    return (std::max)(0, (std::min)(100, score));
}

struct PageResult {
    std::vector<BlockImpl> blocks;
    int confidence = 100;
};

// One page's blocks, in final reading order: PAGE_IMAGE alone for a page with no usable
// text (design §1 item 8); otherwise headings/paragraphs/list items in XY-cut order, a
// trailing paragraph for rotated/unclassified text, then figures and fields spliced in by
// position, furniture already removed (or kept, per `flags`) by the caller.
PageResult BuildPageContent(const megapdf_page* page, PageWork* pw, int page_index, double body_size,
                            unsigned int flags, const std::vector<BlockImpl>& furniture_blocks) {
    PageResult result;
    if (pw->words.empty() && pw->leftover_words.empty()) {
        BlockImpl b;
        b.info.kind = MEGAPDF_BLOCK_PAGE_IMAGE;
        b.info.page = page_index;
        b.info.bounds = megapdf_rect{0, 0, pw->width, pw->height};
        b.info.object_index = -1;
        b.info.confidence = 100;
        result.blocks.push_back(std::move(b));
        result.confidence = 100;
        return result;
    }

    bool too_many_columns = false;
    std::vector<double> column_width;
    const std::vector<int> order = OrderLines(pw->lines, &too_many_columns, &column_width);
    const double median_pitch = PageMedianPitch(pw->lines);
    const std::vector<double> list_clusters = ComputeListDepthClusters(*pw, order, body_size);

    std::vector<BlockImpl> content;
    size_t i = 0;
    while (i < order.size()) {
        BlockImpl block;
        const size_t consumed = GatherOneBlock(*pw, order, i, body_size, median_pitch, column_width, list_clusters,
                                               page_index, &block);
        block.info.source = MEGAPDF_STRUCTURE_SOURCE_HEURISTIC;
        content.push_back(std::move(block));
        i += (std::max<size_t>)(1, consumed);
    }
    DemoteFalsePositiveHeadingRuns(&content);
    BlockImpl leftover;
    if (BuildLeftoverParagraph(*pw, page_index, &leftover)) {
        leftover.info.source = MEGAPDF_STRUCTURE_SOURCE_HEURISTIC;
        content.push_back(std::move(leftover));
    }

    result.confidence = ComputeConfidence(*pw, too_many_columns, content, body_size);

    if (flags & MEGAPDF_STRUCTURE_KEEP_FURNITURE) {
        std::vector<BlockImpl> furniture_copy = furniture_blocks;
        SpliceByPosition(&content, std::move(furniture_copy));
    }

    SpliceByPosition(&content, BuildFigures(page, page_index));
    SpliceByPosition(&content, BuildFields(page, page_index, flags));
    for (auto& b : content) {
        b.info.source = MEGAPDF_STRUCTURE_SOURCE_HEURISTIC;
        b.info.confidence = result.confidence;   // one score per page (design §1 item 6); every block on it carries it
    }
    result.blocks = std::move(content);
    return result;
}

BlockImpl BuildFurnitureBlock(const PageWork& pw, const Line& line, int page_index) {
    BlockImpl b;
    b.info.kind = MEGAPDF_BLOCK_FURNITURE;
    b.info.page = page_index;
    b.info.object_index = -1;
    b.info.bounds = megapdf_rect{line.l, line.b, line.r, line.t};
    PageWork stand_in;
    stand_in.chars = pw.chars;
    stand_in.words = pw.words;
    stand_in.lines = {line};
    const std::vector<int> single{0};
    const auto pieces = BuildPieces(stand_in, single, 0);
    b.spans = SliceIntoSpans(pieces);
    b.text = ConcatSpanText(b.spans);
    return b;
}

// Global pass: heading levels (design §1.2 "Headings": "distinct heading sizes across the
// range, sorted descending, map to 1..6 (capped at 6); bold-at-body-size headings take the
// level after the smallest size-based one").
void AssignHeadingLevels(std::vector<BlockImpl>* blocks) {
    std::vector<double> sizes;
    bool any_bold_at_body = false;
    for (const auto& b : *blocks) {
        if (b.info.kind != MEGAPDF_BLOCK_HEADING) continue;
        if (b.heading_bold_at_body) { any_bold_at_body = true; continue; }
        sizes.push_back(b.heading_size);
    }
    std::sort(sizes.begin(), sizes.end(), std::greater<double>());
    sizes.erase(std::unique(sizes.begin(), sizes.end(), [](double a, double bb) { return std::fabs(a - bb) < 0.01; }),
               sizes.end());
    std::vector<std::pair<double, int>> level_of;   // size -> level, largest first
    for (size_t i = 0; i < sizes.size(); i++) level_of.emplace_back(sizes[i], (std::min)(static_cast<int>(i) + 1, kMaxHeadingLevel));
    const int bold_level = (std::min)(static_cast<int>(sizes.size()) + 1, kMaxHeadingLevel);
    for (auto& b : *blocks) {
        if (b.info.kind != MEGAPDF_BLOCK_HEADING) continue;
        if (b.heading_bold_at_body) {
            b.info.level = any_bold_at_body || !sizes.empty() ? bold_level : 1;
            continue;
        }
        int best_level = kMaxHeadingLevel;
        double best_dist = 1e18;
        for (const auto& e : level_of) {
            const double d = std::fabs(e.first - b.heading_size);
            if (d < best_dist) { best_dist = d; best_level = e.second; }
        }
        b.info.level = best_level;
    }
}

// Global pass: cross-region/page paragraph continuation (design §1.2 "Paragraphs" / §1 item
// 3). A documented simplification of the design's full rule: "next region's first line is
// not a heading, list item or indented first line" is covered by requiring both blocks to be
// PARAGRAPH kind (a heading or list item is a different kind and never matches); "does not
// end mid-sentence" stands in for the indent check, which would need per-block first-line
// geometry this whole-range pass no longer has once blocks are built page by page.
void AssignContinuation(std::vector<BlockImpl>* blocks) {
    for (size_t i = 1; i < blocks->size(); i++) {
        BlockImpl& prev = (*blocks)[i - 1];
        BlockImpl& cur = (*blocks)[i];
        if (prev.info.kind != MEGAPDF_BLOCK_PARAGRAPH || cur.info.kind != MEGAPDF_BLOCK_PARAGRAPH) continue;
        const bool new_region = prev.info.page != cur.info.page || cur.info.bounds.top >= prev.info.bounds.top;
        if (!new_region) continue;
        const unsigned int last_cp = prev.text.empty() ? 0 : prev.text.back();
        const bool ends_sentence = last_cp == '.' || last_cp == '!' || last_cp == '?' || last_cp == ':' || last_cp == ';';
        if (!ends_sentence) cur.info.continues = 1;
    }
}

void AssignSizeRatios(std::vector<BlockImpl>* blocks, double body_size) {
    if (body_size <= 0) return;
    for (auto& b : *blocks) {
        for (auto& s : b.spans) s.info.size_ratio = s.info.font_size / body_size;
    }
}

}  // namespace

// ---------------------------------------------------------------------------
// The handle and the public ABI.
// ---------------------------------------------------------------------------

struct megapdf_structure {
    std::vector<BlockImpl> blocks;
    double body_size = 12.0;
    std::map<int, int> page_confidence;   // document page index -> 0..100
    std::map<int, int> page_source;       // document page index -> MEGAPDF_STRUCTURE_SOURCE_*
};

namespace {

// The whole build, outside the ABI's error-reporting surface so megapdf_structure_load can
// take the core's mutex once around the entire call, matching every other snapshot-loading
// contract (megapdf_text_load, megapdf_form_fields_load, megapdf_stamps_load).
std::unique_ptr<megapdf_structure> BuildStructure(megapdf_document* document, int first_page, int page_count,
                                                   unsigned int flags, const megapdf_cancel* cancel) {
    auto result = std::make_unique<megapdf_structure>();

    std::vector<PageWork> pages(static_cast<size_t>(page_count));
    std::vector<megapdf_page*> loaded(static_cast<size_t>(page_count), nullptr);
    auto close_all = [&]() { for (auto* p : loaded) megapdf_close_page(p); };

    for (int k = 0; k < page_count; k++) {
        if (IsCancelled(cancel)) {
            close_all();
            SetLastError(static_cast<unsigned long>(MEGAPDF_ERR_CANCELLED), "structure load cancelled");
            return nullptr;
        }
        const int doc_page = first_page + k;
        megapdf_page* page = megapdf_load_page(document, doc_page);
        if (page == nullptr) { close_all(); return nullptr; }
        loaded[static_cast<size_t>(k)] = page;
        PageWork& pw = pages[static_cast<size_t>(k)];
        pw.page_index = doc_page;
        pw.width = megapdf_page_width(page);
        pw.height = megapdf_page_height(page);
        ReadChars(page, &pw);
        std::vector<int> normal_idx, rotated_idx;
        normal_idx.reserve(pw.chars.size());
        for (size_t ci = 0; ci < pw.chars.size(); ci++) {
            (pw.chars[ci].rotated ? rotated_idx : normal_idx).push_back(static_cast<int>(ci));
        }
        pw.words = BuildWords(pw.chars, normal_idx);
        pw.leftover_words = BuildWords(pw.chars, rotated_idx);
        pw.lines = BuildLines(pw.words);
    }

    // Furniture (design §1.2 "Furniture"): detected once across the whole range, then pulled
    // out of each page's line list; kept as blocks only with MEGAPDF_STRUCTURE_KEEP_FURNITURE.
    std::vector<std::vector<BlockImpl>> furniture_blocks(static_cast<size_t>(page_count));
    {
        const auto furniture = DetectFurniture(pages);
        std::vector<std::vector<bool>> is_furniture(static_cast<size_t>(page_count));
        for (int k = 0; k < page_count; k++) is_furniture[static_cast<size_t>(k)].assign(pages[static_cast<size_t>(k)].lines.size(), false);
        for (const auto& f : furniture) {
            const int local = f.page_index - first_page;
            if (local < 0 || local >= page_count) continue;
            auto& flags_for_page = is_furniture[static_cast<size_t>(local)];
            if (f.line_index < 0 || static_cast<size_t>(f.line_index) >= flags_for_page.size()) continue;
            flags_for_page[static_cast<size_t>(f.line_index)] = true;
        }
        for (int k = 0; k < page_count; k++) {
            PageWork& pw = pages[static_cast<size_t>(k)];
            std::vector<Line> kept;
            kept.reserve(pw.lines.size());
            for (size_t li = 0; li < pw.lines.size(); li++) {
                if (is_furniture[static_cast<size_t>(k)][li]) {
                    furniture_blocks[static_cast<size_t>(k)].push_back(BuildFurnitureBlock(pw, pw.lines[li], pw.page_index));
                } else {
                    kept.push_back(pw.lines[li]);
                }
            }
            pw.lines = std::move(kept);
        }
    }

    result->body_size = ComputeBodySize(pages);

    if (IsCancelled(cancel)) {
        close_all();
        SetLastError(static_cast<unsigned long>(MEGAPDF_ERR_CANCELLED), "structure load cancelled");
        return nullptr;
    }

    for (int k = 0; k < page_count; k++) {
        if (IsCancelled(cancel)) {
            close_all();
            SetLastError(static_cast<unsigned long>(MEGAPDF_ERR_CANCELLED), "structure load cancelled");
            return nullptr;
        }
        PageResult pr = BuildPageContent(loaded[static_cast<size_t>(k)], &pages[static_cast<size_t>(k)],
                                         pages[static_cast<size_t>(k)].page_index, result->body_size, flags,
                                         furniture_blocks[static_cast<size_t>(k)]);
        result->page_confidence[pages[static_cast<size_t>(k)].page_index] = pr.confidence;
        result->page_source[pages[static_cast<size_t>(k)].page_index] = MEGAPDF_STRUCTURE_SOURCE_HEURISTIC;
        result->blocks.insert(result->blocks.end(), pr.blocks.begin(), pr.blocks.end());
    }

    close_all();

    AssignHeadingLevels(&result->blocks);
    AssignContinuation(&result->blocks);
    AssignSizeRatios(&result->blocks, result->body_size);
    return result;
}

}  // namespace

extern "C" {

MEGAPDF_API megapdf_structure* megapdf_structure_load(megapdf_document* document, int first_page, int page_count,
                                                       unsigned int flags, const megapdf_cancel* cancel) {
    if (document == nullptr || page_count <= 0 || first_page < 0) return nullptr;
    std::lock_guard<std::recursive_mutex> guard(Lock());
    const int total_pages = megapdf_page_count(document);
    if (first_page >= total_pages || page_count > total_pages - first_page) return nullptr;
    return BuildStructure(document, first_page, page_count, flags, cancel).release();
}

MEGAPDF_API void megapdf_structure_free(megapdf_structure* s) { delete s; }

MEGAPDF_API double megapdf_structure_body_size(const megapdf_structure* s) { return s ? s->body_size : 0.0; }

MEGAPDF_API int megapdf_structure_page_confidence(const megapdf_structure* s, int page) {
    if (s == nullptr) return MEGAPDF_ERR_ARGUMENT;
    const auto it = s->page_confidence.find(page);
    return it != s->page_confidence.end() ? it->second : MEGAPDF_ERR_ARGUMENT;
}

MEGAPDF_API int megapdf_structure_page_source(const megapdf_structure* s, int page) {
    if (s == nullptr) return MEGAPDF_ERR_ARGUMENT;
    const auto it = s->page_source.find(page);
    return it != s->page_source.end() ? it->second : MEGAPDF_ERR_ARGUMENT;
}

MEGAPDF_API size_t megapdf_block_count(const megapdf_structure* s) { return s ? s->blocks.size() : 0; }

MEGAPDF_API int megapdf_block_get(const megapdf_structure* s, size_t index, megapdf_block* out) {
    if (s == nullptr || out == nullptr || index >= s->blocks.size()) return MEGAPDF_ERR_ARGUMENT;
    *out = s->blocks[index].info;
    return MEGAPDF_OK;
}

MEGAPDF_API size_t megapdf_block_string(const megapdf_structure* s, size_t index, megapdf_block_field which,
                                        unsigned short* out, size_t capacity) {
    if (s == nullptr || index >= s->blocks.size()) return 0;
    const U16* str = nullptr;
    switch (which) {
        case MEGAPDF_BLOCK_TEXT: str = &s->blocks[index].text; break;
        case MEGAPDF_BLOCK_MARKER: str = &s->blocks[index].marker; break;
        case MEGAPDF_BLOCK_ALT: str = &s->blocks[index].alt; break;
        default: return 0;
    }
    if (out != nullptr) {
        const size_t n = (std::min)(str->size(), capacity);
        for (size_t i = 0; i < n; i++) out[i] = (*str)[i];
    }
    return str->size();
}

MEGAPDF_API size_t megapdf_block_span_count(const megapdf_structure* s, size_t index) {
    if (s == nullptr || index >= s->blocks.size()) return 0;
    return s->blocks[index].spans.size();
}

MEGAPDF_API int megapdf_block_span_get(const megapdf_structure* s, size_t index, size_t span, megapdf_span* out) {
    if (s == nullptr || out == nullptr || index >= s->blocks.size()) return MEGAPDF_ERR_ARGUMENT;
    const auto& spans = s->blocks[index].spans;
    if (span >= spans.size()) return MEGAPDF_ERR_ARGUMENT;
    *out = spans[span].info;
    return MEGAPDF_OK;
}

MEGAPDF_API size_t megapdf_block_span_string(const megapdf_structure* s, size_t index, size_t span,
                                             unsigned short* out, size_t capacity) {
    if (s == nullptr || index >= s->blocks.size()) return 0;
    const auto& spans = s->blocks[index].spans;
    if (span >= spans.size()) return 0;
    const U16& str = spans[span].text;
    if (out != nullptr) {
        const size_t n = (std::min)(str.size(), capacity);
        for (size_t i = 0; i < n; i++) out[i] = str[i];
    }
    return str.size();
}

}  // extern "C"
