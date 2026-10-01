// tools/structure-check — the #354 corpus tool for contract 9 (megapdf_structure_*, #353).
//
// Built exactly as tools/leakcheck is (core/CMakeLists.txt: the core tests' pdfium-beside-
// the-binary target, the same $ORIGIN/@loader_path handling), so a change to the contract is
// a change both the core tests and this tool see. tools/stress/structure-battery.sh drives it
// over the corpus, one document per process, the way redaction-battery.sh drives leakcheck.
//
//   structure_check check <pdf> [--dump <dir> --dump-id <id>] [--reference <file>]
//                               [--cli-reference <file>]
//       One line of numbers on stdout ("result=..."): outcome, pages, tagged/textless/
//       multi-column/many-cut page counts (the #354 census, computed the same way --census
//       does), confidence deciles, block counts by kind, ms/page, peak RSS, and the four
//       measures (design #142 comment, 2026-09-24, section 7):
//         1. token fidelity  — per-page token-multiset F1 of the heuristic blocks' text
//            (KEEP_FURNITURE | ALL_FIELDS, FIELD blocks excluded) against FPDFText_GetText.
//            #360: a line-wrap hyphen is joined into one token on the raw side too before this
//            comparison (JoinLineWrapHyphens), mirroring megapdf_structure.cpp's own join --
//            see that function's comment. This affects measure 1 only; measures 2-3 below
//            still tokenize FPDFText_GetUnicode literally.
//         2. order agreement — Kendall tau between the heuristic blocks' token order and the
//            structure tree's own token order (from its marked-content IDs), on pages the
//            census finds tagged. The tree side reimplements just the tree-to-text mapping
//            from design #1.1 (marked-content IDs in depth-first order, which is exactly the
//            order #358's tagged path reads); the heuristic side is forced through
//            MEGAPDF_STRUCTURE_HEURISTIC_ONLY on pages contract 9 read through the tree
//            (HeuristicTokensForPage), so the measure always compares the two sources.
//            #358 also reports `tree=` (pages whose source is TAGGED — the census's `tagged=`
//            minus this is what the trust rule rejected) and, per tagged page with a
//            --reference, four index-aligned lists for the trust-threshold measurement:
//            tree_cov_page, tree_cov (the tree's character coverage x1000), tau_treeref (the
//            tree order against poppler) and tau_heurref (the heuristic order against poppler);
//            tools/stress/trust_threshold.py bins them over a battery log.
//         3. agreement with poppler — the same tau against `pdftotext -layout` output, read
//            from --reference (a file the battery already produced; this tool never shells
//            out). Informational only.
//            #384 investigation: alongside the flat tau_ref list, five more comma lists of the
//            same length, index-aligned to it -- tau_ref_page (the page index each tau came
//            from), tau_ref_tokens (that page's heuristic block-token count, the same count
//            measure 1 tallies), tau_ref_area (that page's width*height in points^2, rounded),
//            tau_ref_jump (MaxBackwardJumpUnits for that page, x1000 -- the #384 mitigation's
//            grounding measure: how far, in body-size units, the worst same-column backward
//            reading-order jump on the page was, or 0 if none), and tau_ref_conf (that page's
//            real megapdf_structure_page_confidence(), not a proxy -- so the mitigation's
//            actual signal, not a stand-in for it, can be checked against tau_ref directly).
//            These let a corpus-scale slice by page shape (e.g. "page 0 of a multi-page
//            document" or "low token count for the page area") be computed after the fact from
//            the existing battery log, without a second corpus pass -- see
//            tools/stress/structure_titlepage_slice.py. Numbers only, same as everything else
//            this tool prints.
//         4. robustness — timing and memory; crashes and hangs are the caller's business
//            (a segfault or a timeout means this process does not get to print anything).
//       #514's reflow-spike columns: fourteen more index-aligned comma lists, one entry per page
//            of the document (pg_n = the page count, so these are the only lists here that cover
//            EVERY page, including the ones with no text) — pg_index, pg_conf (the page's real
//            confidence), pg_text (1 when the page has a text layer at all), pg_field /
//            pg_widget / pg_pageimg / pg_content (the counts plan §2 tier 3's "show this page as
//            a picture" rule is written in terms of: FIELD blocks, widget annotations counted
//            independently of them, PAGE_IMAGE blocks, and blocks that would become reflowed
//            text), and a script census over the page's real characters — pg_latin, pg_cjk,
//            pg_rtl, pg_cyrgrk, pg_brahmic, pg_other. The script columns exist because this
//            file's tokenizer is Latin-only (see "Tokens" below and #523): they say, per page,
//            how much of the page measures 1 and 3 could actually see, so a page they could not
//            see can be excluded from a gate rather than silently averaged into it.
//       --cli-reference <file> (#355): the same measure 1, but against megapdf-cli's own
//            stdout for this document (run with --page-marker, not the default form feed — see
//            split_on_page_markers()'s comment for why) instead of this process's own
//            megapdf_structure_load() call — printed as cli_fid_match/cli_fid_a/cli_fid_b, so
//            tools/stress/structure-battery.sh --cli can gate the fidelity measure through the
//            real shipped binary.
//       --dump <dir> --dump-id <id> writes the extracted block text to <dir>/<id>.txt and
//       creates <dir>/PRIVATE — never on stdout, never keyed by the document's real name.
//
//   structure_check census <pdf>
//       The lighter #354 deliverable 4 pass alone (tagged/textless/multi-column/many-cut page
//       counts), skipping the fidelity and order measures — for a fast first sweep of the
//       whole corpus to size the tagged-page population before the full battery runs.
//
// Tokens (measures 1-3): maximal runs of "word" code points, matched identically on both
// sides of every comparison. NOT full Unicode NFKC + general-category letter/digit
// classification (no ICU here) — ASCII, Latin-1 Supplement and Latin Extended-A, which is
// what the corpus's English/French documents need (#91). A corpus with a lot of other scripts
// would need this widened; the census does not measure script mix, so that is a follow-up to
// notice by hand, not something this tool claims to have checked.
//
// Nothing about a document is printed beyond counts, indices and timings: the corpus is
// personal (#151, #173, #354).
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <map>
#include <sstream>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include "megapdf_core.h"
#include "fpdfview.h"
#include "fpdf_text.h"
#include "fpdf_annot.h"
#include "fpdf_edit.h"
#include "fpdf_structtree.h"

#if defined(_WIN32)
#include <windows.h>
#include <psapi.h>
#else
#include <sys/resource.h>
#endif

namespace {

// Same code point as core/megapdf_structure.cpp:116 (kSoftHyphen) -- kept in sync by hand
// (this tool builds standalone against the core headers, not against megapdf_structure.cpp's
// internals) since #360's join must match that file's join exactly. megapdf_structure.cpp
// also names kHyphenMinus/kHyphenChar (U+002D/U+2010) as literal-character fallbacks for a
// line-end hyphen FPDFText_IsHyphen missed; JoinLineWrapHyphens's own comment explains why
// this tool does not replicate that fallback (no line geometry here to gate it safely on).
constexpr unsigned int kSoftHyphen = 0x00AD;

// ---------------------------------------------------------------------------
// Peak RSS, cross-platform (leakcheck has no equivalent: a battery run is one process per
// document there too, but nothing before #354 needed memory numbers).
// ---------------------------------------------------------------------------
long long PeakRssKb() {
#if defined(_WIN32)
    PROCESS_MEMORY_COUNTERS pmc{};
    if (GetProcessMemoryInfo(GetCurrentProcess(), &pmc, sizeof(pmc))) {
        return static_cast<long long>(pmc.PeakWorkingSetSize) / 1024;
    }
    return -1;
#else
    struct rusage ru {};
    if (getrusage(RUSAGE_SELF, &ru) != 0) return -1;
    // Linux reports ru_maxrss in KB already; macOS reports bytes.
#if defined(__APPLE__)
    return static_cast<long long>(ru.ru_maxrss) / 1024;
#else
    return static_cast<long long>(ru.ru_maxrss);
#endif
#endif
}

// ---------------------------------------------------------------------------
// Tokens: vector<char32_t> code points -> vector<u32string> maximal word runs. See the file
// header for what "word" covers here.
// ---------------------------------------------------------------------------
bool IsWordCodepoint(unsigned int c) {
    if (c >= '0' && c <= '9') return true;
    if (c >= 'A' && c <= 'Z') return true;
    if (c >= 'a' && c <= 'z') return true;
    if (c >= 0xC0 && c <= 0xFF && c != 0xD7 && c != 0xF7) return true;  // Latin-1 Supplement letters
    if (c >= 0x100 && c <= 0x17F) return true;                          // Latin Extended-A
    return false;
}

// ---------------------------------------------------------------------------
// #514 / #523: which script a page is written in.
//
// This does NOT widen the tokenizer -- that is #523's own job and Dave has deferred it. What it
// does is make the blindness VISIBLE, so a number computed on a page the tokenizer cannot see is
// not quoted as if it covered the page. IsWordCodepoint above admits ASCII, Latin-1 Supplement
// and Latin Extended-A and silently drops everything else, so on a Japanese page measure 1 and
// measure 3 both grade whatever Latin happens to be embedded in it (#523 measured 3,720 of
// 24,541 characters on one such page). The #514 spike needs to EXCLUDE those pages from its
// eligible set and report them separately -- which plan §7's RTL/CJK risk note already asked for
// -- and that needs a per-page count of which script the characters are in.
//
// Groups are coarse on purpose: the question is only "can the gate see this page", so the
// relevant split is Latin (seen) against each family of not-seen. Ranges are the Unicode blocks,
// letters and syllables only; punctuation, digits and symbols shared between scripts are
// deliberately NOT counted on either side, so a page of Japanese with ASCII digits in it does not
// read as part Latin because of the digits.
enum ScriptGroup {
    kScriptNone = 0,
    kScriptLatin = 1,
    kScriptCjk = 2,       // Han, Hiragana, Katakana, Hangul, Bopomofo -- #482/#521's population
    kScriptRtl = 3,       // Arabic, Hebrew, Syriac, Thaana, NKo
    kScriptCyrGrk = 4,    // Cyrillic, Greek
    kScriptBrahmic = 5,   // Devanagari..Sinhala, Thai, Lao, Tibetan, Myanmar, Khmer
    kScriptOther = 6      // a letter in none of the above (Ethiopic, Cherokee, Georgian, ...)
};

int ScriptOf(unsigned int c) {
    // Latin: exactly what IsWordCodepoint admits, minus the digits (shared, see above).
    if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) return kScriptLatin;
    if (c >= 0xC0 && c <= 0xFF && c != 0xD7 && c != 0xF7) return kScriptLatin;
    if (c >= 0x100 && c <= 0x24F) return kScriptLatin;              // Latin Extended-A/-B
    if (c >= 0x1E00 && c <= 0x1EFF) return kScriptLatin;            // Latin Extended Additional
    if (c >= 0xFF21 && c <= 0xFF5A) return kScriptLatin;            // fullwidth ASCII letters
    // Greek and Cyrillic.
    if ((c >= 0x370 && c <= 0x3FF) || (c >= 0x1F00 && c <= 0x1FFF)) return kScriptCyrGrk;
    if (c >= 0x400 && c <= 0x52F) return kScriptCyrGrk;
    // Right-to-left.
    if (c >= 0x590 && c <= 0x5FF) return kScriptRtl;                // Hebrew
    if (c >= 0x600 && c <= 0x6FF) return kScriptRtl;                // Arabic
    if (c >= 0x700 && c <= 0x74F) return kScriptRtl;                // Syriac
    if (c >= 0x750 && c <= 0x77F) return kScriptRtl;                // Arabic Supplement
    if (c >= 0x780 && c <= 0x7BF) return kScriptRtl;                // Thaana
    if (c >= 0x7C0 && c <= 0x7FF) return kScriptRtl;                // NKo
    if (c >= 0x8A0 && c <= 0x8FF) return kScriptRtl;                // Arabic Extended-A
    if (c >= 0xFB1D && c <= 0xFB4F) return kScriptRtl;              // Hebrew presentation forms
    if (c >= 0xFB50 && c <= 0xFDFF) return kScriptRtl;              // Arabic presentation forms A
    if (c >= 0xFE70 && c <= 0xFEFF) return kScriptRtl;              // Arabic presentation forms B
    // Brahmic and south-east Asian.
    if (c >= 0x900 && c <= 0xDFF) return kScriptBrahmic;            // Devanagari .. Sinhala
    if (c >= 0xE00 && c <= 0xE7F) return kScriptBrahmic;            // Thai
    if (c >= 0xE80 && c <= 0xEFF) return kScriptBrahmic;            // Lao
    if (c >= 0xF00 && c <= 0xFFF) return kScriptBrahmic;            // Tibetan
    if (c >= 0x1000 && c <= 0x109F) return kScriptBrahmic;          // Myanmar
    if (c >= 0x1780 && c <= 0x17FF) return kScriptBrahmic;          // Khmer
    // CJK.
    if (c >= 0x1100 && c <= 0x11FF) return kScriptCjk;              // Hangul Jamo
    if (c >= 0x2E80 && c <= 0x2EFF) return kScriptCjk;              // CJK radicals supplement
    if (c >= 0x3040 && c <= 0x30FF) return kScriptCjk;              // Hiragana, Katakana
    if (c >= 0x3100 && c <= 0x312F) return kScriptCjk;              // Bopomofo
    if (c >= 0x3130 && c <= 0x318F) return kScriptCjk;              // Hangul compatibility Jamo
    if (c >= 0x3400 && c <= 0x4DBF) return kScriptCjk;              // CJK Unified Extension A
    if (c >= 0x4E00 && c <= 0x9FFF) return kScriptCjk;              // CJK Unified
    if (c >= 0xA960 && c <= 0xA97F) return kScriptCjk;              // Hangul Jamo Extended-A
    if (c >= 0xAC00 && c <= 0xD7FF) return kScriptCjk;              // Hangul syllables + Extended-B
    if (c >= 0xF900 && c <= 0xFAFF) return kScriptCjk;              // CJK compatibility ideographs
    if (c >= 0xFF66 && c <= 0xFF9F) return kScriptCjk;              // halfwidth Katakana
    if (c >= 0x20000 && c <= 0x3FFFF) return kScriptCjk;            // CJK Unified Extensions B+
    // A letter in no named group. Approximated by "not ASCII punctuation, digit or control, and
    // not a symbol block" -- deliberately generous, because an unrecognised letter must land
    // somewhere other than Latin or the exclusion under-counts.
    if (c < 0x370) return kScriptNone;                              // ASCII/Latin-1 punctuation etc.
    if (c >= 0x2000 && c <= 0x2BFF) return kScriptNone;             // general punctuation, symbols, arrows
    if (c >= 0x3000 && c <= 0x303F) return kScriptNone;             // CJK punctuation (shared)
    if (c >= 0xE000 && c <= 0xF8FF) return kScriptNone;             // private use: no script to claim
    if (c >= 0xFE00 && c <= 0xFE6F) return kScriptNone;             // variation selectors, verticals
    if (c >= 0xFF00 && c <= 0xFF20) return kScriptNone;             // fullwidth punctuation
    if (c >= 0xFFF0) return kScriptNone;                            // specials
    return kScriptOther;
}

using Token = std::u32string;

std::vector<Token> Tokenize(const std::vector<unsigned int>& codepoints) {
    std::vector<Token> out;
    Token cur;
    for (unsigned int c : codepoints) {
        if (IsWordCodepoint(c)) {
            cur.push_back(static_cast<char32_t>(c));
        } else if (!cur.empty()) {
            out.push_back(cur);
            cur.clear();
        }
    }
    if (!cur.empty()) out.push_back(cur);
    return out;
}

// ---------------------------------------------------------------------------
// #360: measure 1's raw (FPDFText_GetText) side does no hyphen-joining, while
// megapdf_structure.cpp's heuristic side joins a line-wrap hyphen into the surrounding word
// (core/megapdf_structure.cpp:838-855, design §1.2 "Hyphenation") -- e.g. "encod-" + "ings"
// becomes the single token "encodings" there, but stays "encod" + "ings" (two tokens) here, so
// the two can never multiset-match under this tool's own token definition. This mirrors that
// same join, in the fidelity comparison only, so the raw side counts it the same way:
//   - a soft hyphen (U+00AD) is always joinable, exactly like core/megapdf_structure.cpp.
//   - a character PDFium itself flags as a hyphen (FPDFText_IsHyphen -- the exact test
//     megapdf_structure.cpp's own ReadChars uses to set Char::is_hyphen, and the reason such a
//     character's own GetUnicode often reads back as 2, not its real code point) is joinable
//     only when the next real character is ASCII lowercase -- the same "next line starts
//     lowercase" test BuildPieces uses to decide `strip`.
// A literal ASCII hyphen (U+002D) or Unicode hyphen (U+2010) that FPDFText_IsHyphen does NOT
// flag is deliberately left alone, even when the following character is lowercase: without
// megapdf_structure.cpp's own line geometry, this tool cannot otherwise tell a true line-wrap
// apart from a mid-line hyphen in a genuine compound word ("well-known", "state-of-the-art")
// -- exactly the over-generalization #360 itself warns against. (An earlier version of this
// fix treated every literal hyphen as a candidate whenever the next letter was lowercase; on
// the full corpus that merged compound-word halves that BuildPieces never touches, so it
// *lowered* the aggregate F1 instead of raising it -- gating on FPDFText_IsHyphen instead
// fixed that regression.) BuildPieces' own literal-hyphen fallback (used when a line's last
// character is a plain '-'/U+2010 that PDFium's IsHyphen missed) has no raw-side equivalent
// here for the same reason; those rarer cases are left unmatched, same as before this fix.
bool IsAsciiLowerCp(unsigned int c) { return c >= 'a' && c <= 'z'; }

bool IsWhitespaceCpLocal(unsigned int c) {
    return c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == 0x00A0 || c == 0x2028 || c == 0x2029 ||
           (c >= 0x2000 && c <= 0x200B) || c == 0x202F || c == 0x205F || c == 0x3000;
}

std::vector<unsigned int> JoinLineWrapHyphens(FPDF_TEXTPAGE textpage, int char_count) {
    std::vector<unsigned int> out;
    out.reserve(static_cast<size_t>((std::max)(char_count, 0)));
    for (int i = 0; i < char_count; i++) {
        const unsigned int cp = FPDFText_GetUnicode(textpage, i);
        if (cp == 0) continue;
        const bool is_soft_hyphen = cp == kSoftHyphen;
        const bool hyphen_like = is_soft_hyphen || FPDFText_IsHyphen(textpage, i) == 1;
        if (!hyphen_like) {
            out.push_back(cp);
            continue;
        }
        bool join = is_soft_hyphen;
        if (!join) {
            // Next real (non-generated, non-whitespace) character -- the continuation's first
            // letter, the same value megapdf_structure.cpp's `next_cps[0]` names.
            for (int j = i + 1; j < char_count; j++) {
                if (FPDFText_IsGenerated(textpage, j) == 1) continue;
                const unsigned int next_cp = FPDFText_GetUnicode(textpage, j);
                if (next_cp == 0 || IsWhitespaceCpLocal(next_cp)) continue;
                join = IsAsciiLowerCp(next_cp);
                break;
            }
        }
        if (!join) out.push_back(cp);  // not a line-wrap join: keep it as its own separator
        // else: drop the hyphen so the tokenizer merges the surrounding runs into one token.
    }
    return out;
}

std::vector<unsigned int> Utf16ToCodepoints(const std::vector<unsigned short>& u) {
    std::vector<unsigned int> out;
    out.reserve(u.size());
    for (size_t i = 0; i < u.size(); i++) {
        unsigned int c = u[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < u.size() && u[i + 1] >= 0xDC00 && u[i + 1] <= 0xDFFF) {
            c = 0x10000 + ((c - 0xD800) << 10) + (u[i + 1] - 0xDC00);
            i++;
        }
        out.push_back(c);
    }
    return out;
}

// A minimal UTF-8 decoder for --reference files (pdftotext's output). Invalid sequences are
// skipped byte-by-byte rather than aborting: a battery run over 4,000+ documents cannot let
// one odd poppler encoding choice take the measure down.
std::vector<unsigned int> Utf8ToCodepoints(const std::string& s) {
    std::vector<unsigned int> out;
    size_t i = 0;
    while (i < s.size()) {
        const unsigned char b0 = static_cast<unsigned char>(s[i]);
        unsigned int cp = 0;
        int extra = 0;
        if ((b0 & 0x80) == 0) { cp = b0; extra = 0; }
        else if ((b0 & 0xE0) == 0xC0) { cp = b0 & 0x1F; extra = 1; }
        else if ((b0 & 0xF0) == 0xE0) { cp = b0 & 0x0F; extra = 2; }
        else if ((b0 & 0xF8) == 0xF0) { cp = b0 & 0x07; extra = 3; }
        else { i++; continue; }
        if (i + extra >= s.size()) { i++; continue; }
        bool ok = true;
        for (int k = 1; k <= extra; k++) {
            const unsigned char bk = static_cast<unsigned char>(s[i + k]);
            if ((bk & 0xC0) != 0x80) { ok = false; break; }
            cp = (cp << 6) | (bk & 0x3F);
        }
        if (!ok) { i++; continue; }
        out.push_back(cp);
        i += extra + 1;
    }
    return out;
}

// ---------------------------------------------------------------------------
// Measure 1: token-multiset F1.
// ---------------------------------------------------------------------------
struct FidelityCounts { long long matched = 0, a = 0, b = 0; };

FidelityCounts MultisetF1(const std::vector<Token>& a, const std::vector<Token>& b) {
    std::map<Token, int> ca, cb;
    for (const Token& t : a) ca[t]++;
    for (const Token& t : b) cb[t]++;
    FidelityCounts r;
    r.a = static_cast<long long>(a.size());
    r.b = static_cast<long long>(b.size());
    for (const auto& kv : ca) {
        auto it = cb.find(kv.first);
        if (it != cb.end()) r.matched += (std::min)(kv.second, it->second);
    }
    return r;
}

double F1(const FidelityCounts& c) {
    if (c.a + c.b == 0) return 1.0;  // both sides empty: nothing to disagree about
    return 2.0 * static_cast<double>(c.matched) / static_cast<double>(c.a + c.b);
}

// ---------------------------------------------------------------------------
// Measures 2-3: Kendall tau-a over matched token positions.
//
// Tokens repeat, so identity alone does not pick out ONE pair per token: the i-th occurrence
// of a token in `base` is matched to the i-th occurrence of the same token in `other`, in the
// order each sequence presents them. What remains is a set of (base_index, other_index)
// pairs; tau is computed over the `other_index` values taken in `base_index` order, which is
// exactly the classic "how many adjacent transpositions to sort this into base's order"
// question, counted as inversions with a Fenwick tree (O(n log n) — a page's token count is
// small, but the corpus runs this tens of thousands of times).
// ---------------------------------------------------------------------------
long long CountInversions(const std::vector<int>& seq) {
    if (seq.size() < 2) return 0;
    int maxv = 0;
    for (int v : seq) maxv = (std::max)(maxv, v);
    std::vector<int> bit(static_cast<size_t>(maxv) + 2, 0);
    auto update = [&](int i) { for (i++; i <= maxv + 1; i += i & (-i)) bit[i]++; };
    auto query = [&](int i) {  // count of values in [0, i]
        long long s = 0;
        for (i++; i > 0; i -= i & (-i)) s += bit[i];
        return s;
    };
    long long inversions = 0;
    for (size_t idx = seq.size(); idx-- > 0;) {
        if (seq[idx] > 0) inversions += query(seq[idx] - 1);
        update(seq[idx]);
    }
    return inversions;
}

// Returns tau in [-1, 1], or a sentinel below -1 when fewer than 2 tokens matched (not enough
// to order at all — the caller skips these rather than reporting a fake tau of 0 or 1).
constexpr double kTauNotEnoughData = -2.0;

double KendallTau(const std::vector<Token>& base, const std::vector<Token>& other) {
    std::unordered_map<Token, std::vector<int>> positions;
    for (int i = 0; i < static_cast<int>(other.size()); i++) positions[other[i]].push_back(i);
    std::unordered_map<Token, size_t> cursor;
    std::vector<int> matched;
    matched.reserve(base.size());
    for (const Token& t : base) {
        auto it = positions.find(t);
        if (it == positions.end()) continue;
        size_t& idx = cursor[t];
        if (idx >= it->second.size()) continue;
        matched.push_back(it->second[idx]);
        idx++;
    }
    const long long n = static_cast<long long>(matched.size());
    if (n < 2) return kTauNotEnoughData;
    const long long total = n * (n - 1) / 2;
    const long long inversions = CountInversions(matched);
    const long long concordant = total - inversions;
    return total > 0 ? static_cast<double>(concordant - inversions) / static_cast<double>(total) : kTauNotEnoughData;
}

// ---------------------------------------------------------------------------
// Struct-tree walk: DFS over elements and their marked-content children, exactly the
// interleaving order design #142/#1.1 describes (an element's children are, in document
// order, either more elements or direct marked-content references — never sorted apart).
// ---------------------------------------------------------------------------
void WalkElement(FPDF_STRUCTELEMENT el, std::vector<int>* mcids, int depth) {
    if (el == nullptr || depth > 64) return;
    const int n = FPDF_StructElement_CountChildren(el);
    for (int i = 0; i < n; i++) {
        const int mcid = FPDF_StructElement_GetChildMarkedContentID(el, i);
        if (mcid >= 0) {
            mcids->push_back(mcid);
            continue;
        }
        FPDF_STRUCTELEMENT child = FPDF_StructElement_GetChildAtIndex(el, i);
        if (child != nullptr) WalkElement(child, mcids, depth + 1);
    }
}

// True (and *mcids_in_order filled) when the page is tagged by the #354 census definition:
// FPDF_StructTree_GetForPage non-null with >= 1 child.
bool TaggedPageTree(FPDF_PAGE page, std::vector<int>* mcids_in_order) {
    FPDF_STRUCTTREE tree = FPDF_StructTree_GetForPage(page);
    if (tree == nullptr) return false;
    const int n = FPDF_StructTree_CountChildren(tree);
    bool tagged = n > 0;
    if (tagged) {
        for (int i = 0; i < n; i++) {
            FPDF_STRUCTELEMENT el = FPDF_StructTree_GetChildAtIndex(tree, i);
            if (el != nullptr) WalkElement(el, mcids_in_order, 0);
        }
    }
    FPDF_StructTree_Close(tree);
    return tagged;
}

// The tree's own token order for a page: each marked-content ID's characters, gathered in
// character-index order (their natural order in the page's content), tokenized and appended,
// in the order the DFS above visits the IDs.
std::vector<Token> TreeTokenOrder(FPDF_TEXTPAGE textpage, const std::vector<int>& mcids_in_order) {
    const int chars = FPDFText_CountChars(textpage);
    std::unordered_map<int, std::vector<unsigned int>> by_mcid;
    for (int i = 0; i < chars; i++) {
        FPDF_PAGEOBJECT obj = FPDFText_GetTextObject(textpage, i);
        if (obj == nullptr) continue;
        const int mcid = FPDFPageObj_GetMarkedContentID(obj);
        if (mcid < 0) continue;
        const unsigned int cp = FPDFText_GetUnicode(textpage, i);
        if (cp != 0) by_mcid[mcid].push_back(cp);
    }
    std::vector<Token> out;
    for (int mcid : mcids_in_order) {
        auto it = by_mcid.find(mcid);
        if (it == by_mcid.end()) continue;
        for (const Token& t : Tokenize(it->second)) out.push_back(t);
    }
    return out;
}

// ---------------------------------------------------------------------------
// Census proxy for "multi-column" / "more than three cuts": contract 9 does not expose the
// XY-cut's own cut count (#142/#1.2 is internal to megapdf_structure.cpp), so this clusters
// the blocks' left edges instead. Two clusters whose y-ranges overlap read as columns; more
// than three such clusters reads as a table/form. An approximation of the real rule, not a
// readout of it — documented as such wherever it is used.
// ---------------------------------------------------------------------------
struct ColumnCensus { bool multi_column = false; bool many_cut = false; };

ColumnCensus ColumnCensusForPage(const std::vector<megapdf_block>& page_blocks, double body_size) {
    ColumnCensus out;
    std::vector<double> lefts;
    std::vector<std::pair<double, double>> ranges;  // (bottom, top) per block, for overlap
    for (const megapdf_block& b : page_blocks) {
        if (b.kind != MEGAPDF_BLOCK_HEADING && b.kind != MEGAPDF_BLOCK_PARAGRAPH &&
            b.kind != MEGAPDF_BLOCK_LIST_ITEM && b.kind != MEGAPDF_BLOCK_TABLE_ROW) {
            continue;
        }
        lefts.push_back(b.bounds.left);
        ranges.push_back({b.bounds.bottom, b.bounds.top});
    }
    if (lefts.size() < 2) return out;
    const double gap = (std::max)(20.0, body_size * 3.0);
    std::vector<size_t> order(lefts.size());
    for (size_t i = 0; i < order.size(); i++) order[i] = i;
    std::sort(order.begin(), order.end(), [&](size_t a, size_t b) { return lefts[a] < lefts[b]; });
    std::vector<int> cluster_of(lefts.size(), 0);
    int clusters = 1;
    cluster_of[order[0]] = 0;
    for (size_t i = 1; i < order.size(); i++) {
        if (lefts[order[i]] - lefts[order[i - 1]] > gap) clusters++;
        cluster_of[order[i]] = clusters - 1;
    }
    if (clusters < 2) return out;
    std::vector<std::pair<double, double>> cluster_extent(clusters, {1e18, -1e18});
    for (size_t i = 0; i < ranges.size(); i++) {
        auto& e = cluster_extent[cluster_of[i]];
        e.first = (std::min)(e.first, ranges[i].first);
        e.second = (std::max)(e.second, ranges[i].second);
    }
    // A cluster whose own y-extent is about one line tall is a signature line, a date, an
    // indented reply header — not a column candidate. A real column (or a table's column of
    // cells) spans several lines; the filter is on the cluster's height, not its block count,
    // so a two-column page with one tall PARAGRAPH block per column still counts.
    const double min_extent = (std::max)(8.0, body_size * 2.0);
    // Overlap check: at least two distinct tall clusters must share a y-range by more than a
    // token line's height, or this is headings/quotes at different indents stacked
    // vertically, not columns.
    const double min_overlap = (std::max)(4.0, body_size * 0.5);
    int overlapping_pairs = 0;
    int tall_clusters = 0;
    for (int i = 0; i < clusters; i++) {
        if (cluster_extent[i].second - cluster_extent[i].first < min_extent) continue;
        tall_clusters++;
        for (int j = i + 1; j < clusters; j++) {
            if (cluster_extent[j].second - cluster_extent[j].first < min_extent) continue;
            const double overlap = (std::min)(cluster_extent[i].second, cluster_extent[j].second) -
                                   (std::max)(cluster_extent[i].first, cluster_extent[j].first);
            if (overlap > min_overlap) overlapping_pairs++;
        }
    }
    out.multi_column = overlapping_pairs > 0;
    out.many_cut = clusters > 3 && tall_clusters >= 2;
    return out;
}

// ---------------------------------------------------------------------------
// #384 investigation: a page-shape-independent proxy for "this page's reading order took an
// implausible jump", measured directly on the public bounds contract 9 already returns (no
// core change here -- this is the grounding measurement the #384 mitigation's threshold is
// picked from, per that issue's request to check what distance/gap actually correlates with
// low tau_ref before hard-coding a number).
//
// Definition: walk the page's ordinary content blocks (HEADING/PARAGRAPH/LIST_ITEM/TABLE_ROW
// -- the same kind filter ColumnCensusForPage uses, and the same set megapdf_structure.cpp's
// own ComputeConfidence sees before FIGURE/FIELD/FURNITURE are spliced in) in their existing
// order. For each adjacent pair (a, b), if their horizontal extents overlap by more than
// kJumpOverlapFrac of the narrower block's width -- i.e. b sits in essentially the same
// horizontal band as a, not a different column -- a legitimate top-to-bottom flow has b start
// at or below where a ends (b.top <= a.bottom). When instead b.top is ABOVE a.bottom, the
// reading order moved backward within what looks like the same column: exactly the "distant,
// not a normal column-to-column transition" case #384 asks about. The page's value is the
// worst (largest) such backward jump, in body-size units (body_size is already a whole-
// document scale-free unit this file uses elsewhere, e.g. ColumnCensusForPage's own gap), or 0
// when no pair qualifies. A different column (no horizontal overlap) never triggers this,
// whatever the distance -- that is the "normal column-to-column transition" this deliberately
// leaves alone.
constexpr double kJumpOverlapFrac = 0.3;

double MaxBackwardJumpUnits(const std::vector<megapdf_block>& page_blocks, double body_size) {
    if (body_size <= 0) return 0.0;
    std::vector<const megapdf_block*> content;
    for (const auto& b : page_blocks) {
        if (b.kind == MEGAPDF_BLOCK_HEADING || b.kind == MEGAPDF_BLOCK_PARAGRAPH ||
            b.kind == MEGAPDF_BLOCK_LIST_ITEM || b.kind == MEGAPDF_BLOCK_TABLE_ROW) {
            content.push_back(&b);
        }
    }
    double worst = 0.0;
    for (size_t i = 1; i < content.size(); i++) {
        const megapdf_rect& a = content[i - 1]->bounds;
        const megapdf_rect& b = content[i]->bounds;
        const double overlap = (std::min)(a.right, b.right) - (std::max)(a.left, b.left);
        const double min_width = (std::min)(a.right - a.left, b.right - b.left);
        if (min_width <= 0 || overlap <= kJumpOverlapFrac * min_width) continue;   // different column: skip
        const double backward = b.top - a.bottom;   // > 0 means b starts above where a ended
        if (backward > 0) worst = (std::max)(worst, backward / body_size);
    }
    return worst;
}

// ---------------------------------------------------------------------------
// megapdf_block_string / megapdf_block_span helpers.
// ---------------------------------------------------------------------------
std::vector<unsigned short> BlockString(const megapdf_structure* s, size_t i, megapdf_block_field which) {
    const size_t n = megapdf_block_string(s, i, which, nullptr, 0);
    std::vector<unsigned short> buf(n);
    if (n > 0) megapdf_block_string(s, i, which, buf.data(), n);
    return buf;
}

// #479/#495: a LIST_ITEM's marker ("73.", "(a)", "*") is real text drawn on the page --
// core/megapdf_core.h:1287 -- but contract 9 (design §1.2 "Lists") deliberately keeps it out
// of MEGAPDF_BLOCK_TEXT, in its own MEGAPDF_BLOCK_MARKER field, so a screen reader can announce
// "list item 3: ..." instead of reading the "3." as body prose (core/megapdf_structure.cpp's
// LIST_ITEM cases, both the tagged-tree and heuristic paths, split it out the same way). The
// CLI's own plain-text/Markdown writer (core/megapdf_write_text.cpp, ~L726-793) puts the marker
// back in front of the item's text when it renders a line, so `megapdf-cli extract`'s token
// count already includes it and matches PDFium's raw GetText almost exactly. Every place in
// this file that assembles "the tokens the engine produced for this page/block" to compare
// against PDFium's raw side (or against megapdf-cli's own rendered output) has to do the same
// concatenation, or every alphanumeric list marker in the document counts as a token the engine
// "lost" -- it was never lost, it is one field over. Order matches the writer's own rendering:
// marker, then body text, with the same single-space join megapdf_write_text.cpp uses between
// them (WriteMarkdown/WritePlain, "marker + \" \" + text" when both are non-empty) -- without
// it, the marker's last token and the text's first token concatenate into one fused token
// (e.g. a "1.42" marker butted straight against "Name at birth:" reads as "42Name"), which is
// its own new mismatch against PDFium's raw side, where the two are two separate tokens.
std::vector<unsigned short> BlockContentString(const megapdf_structure* s, size_t i, int kind) {
    std::vector<unsigned short> out;
    if (kind == MEGAPDF_BLOCK_LIST_ITEM) out = BlockString(s, i, MEGAPDF_BLOCK_MARKER);
    std::vector<unsigned short> text = BlockString(s, i, MEGAPDF_BLOCK_TEXT);
    if (!out.empty() && !text.empty()) out.push_back(static_cast<unsigned short>(' '));
    out.insert(out.end(), text.begin(), text.end());
    return out;
}

// ---------------------------------------------------------------------------
// Percentile deciles of an int vector already in [0, 100] (confidence): count per decile
// bucket 0-9 (bucket k = [10k, 10k+10), bucket 9 also takes 100).
// ---------------------------------------------------------------------------
std::string ConfidenceDeciles(const std::vector<int>& confidences) {
    int deciles[10] = {0};
    for (int c : confidences) {
        int d = c / 10;
        if (d > 9) d = 9;
        if (d < 0) d = 0;
        deciles[d]++;
    }
    std::ostringstream out;
    for (int i = 0; i < 10; i++) { if (i) out << ","; out << deciles[i]; }
    return out.str();
}

std::string JoinInts(const std::vector<long long>& v) {
    std::ostringstream out;
    for (size_t i = 0; i < v.size(); i++) { if (i) out << ","; out << v[i]; }
    return out.str();
}

// #358: the heuristic path's tokens for page `p`. When contract 9 read the page through its
// tree, the default load's tokens are the TREE's order, so measures 2 and 3's "heuristic"
// column comes from a second, one-page load with MEGAPDF_STRUCTURE_HEURISTIC_ONLY (the same
// KEEP_FURNITURE | ALL_FIELDS flags, FIELD blocks excluded as everywhere else); otherwise the
// default load already IS the heuristic answer and is returned as given.
std::vector<Token> HeuristicTokensForPage(megapdf_document* doc, int p, const std::vector<Token>& default_tokens,
                                          bool page_was_tagged) {
    if (!page_was_tagged) return default_tokens;
    megapdf_structure* h = megapdf_structure_load(
        doc, p, 1, MEGAPDF_STRUCTURE_KEEP_FURNITURE | MEGAPDF_STRUCTURE_ALL_FIELDS | MEGAPDF_STRUCTURE_HEURISTIC_ONLY,
        nullptr);
    if (h == nullptr) return default_tokens;
    std::vector<Token> out;
    const size_t n = megapdf_block_count(h);
    for (size_t i = 0; i < n; i++) {
        megapdf_block b{};
        if (megapdf_block_get(h, i, &b) != MEGAPDF_OK || b.kind == MEGAPDF_BLOCK_FIELD) continue;
        const std::vector<Token> toks = Tokenize(Utf16ToCodepoints(BlockContentString(h, i, b.kind)));
        out.insert(out.end(), toks.begin(), toks.end());
    }
    megapdf_structure_free(h);
    return out;
}

// ---------------------------------------------------------------------------
// check mode
// ---------------------------------------------------------------------------
struct Options {
    std::string pdf;
    std::string dump_dir;
    std::string dump_id;
    std::string reference_file;
    std::string cli_reference_file;   // #355: megapdf-cli's own extracted text, for measure 1 through the real binary
    bool census_only = false;
};

const char* OpenOutcome(unsigned int err) {
    switch (err) {
        case FPDF_ERR_PASSWORD: return "encrypted";
        case FPDF_ERR_SECURITY: return "encrypted";
        case FPDF_ERR_FILE: return "format";
        case FPDF_ERR_FORMAT: return "format";
        case FPDF_ERR_PAGE: return "format";
        case MEGAPDF_OPEN_ERR_TOO_LARGE: return "format";
        default: return "format";
    }
}

int RunCheck(const Options& opt) {
    const auto t0 = std::chrono::steady_clock::now();
    megapdf_document* doc = megapdf_open_file(opt.pdf.c_str(), nullptr);
    if (doc == nullptr) {
        std::printf("result=%s\n", OpenOutcome(megapdf_last_error()));
        return 0;
    }
    const int pages = megapdf_page_count(doc);
    if (pages <= 0) {
        std::printf("result=format pages=0\n");
        megapdf_close(doc);
        return 0;
    }

    megapdf_structure* s = megapdf_structure_load(doc, 0, pages, MEGAPDF_STRUCTURE_KEEP_FURNITURE |
                                                                       MEGAPDF_STRUCTURE_ALL_FIELDS, nullptr);
    if (s == nullptr) {
        std::printf("result=format pages=%d\n", pages);
        megapdf_close(doc);
        return 0;
    }
    const double body_size = megapdf_structure_body_size(s);

    // Blocks grouped by page, in structure order (== reading order).
    const size_t n_blocks = megapdf_block_count(s);
    std::vector<std::vector<megapdf_block>> blocks_by_page(static_cast<size_t>(pages));
    std::vector<std::vector<Token>> tokens_by_page(static_cast<size_t>(pages));
    int block_kind_counts[9] = {0};  // index by megapdf_block_kind (1..8)
    for (size_t i = 0; i < n_blocks; i++) {
        megapdf_block b{};
        if (megapdf_block_get(s, i, &b) != MEGAPDF_OK) continue;
        if (b.page < 0 || b.page >= pages) continue;
        if (b.kind >= 1 && b.kind <= 8) block_kind_counts[b.kind]++;
        blocks_by_page[static_cast<size_t>(b.page)].push_back(b);
        if (b.kind == MEGAPDF_BLOCK_FIELD) continue;  // measures exclude FIELD blocks
        const std::vector<unsigned short> text16 = BlockContentString(s, i, b.kind);
        const std::vector<Token> toks = Tokenize(Utf16ToCodepoints(text16));
        tokens_by_page[static_cast<size_t>(b.page)].insert(tokens_by_page[static_cast<size_t>(b.page)].end(),
                                                            toks.begin(), toks.end());
    }

    // Raw PDFium, for FPDFText_GetText / the struct tree / --dump's confirmation that
    // megapdf_open_file's file stayed readable (leakcheck's OccurrencesInText does the same
    // double-open; see this file's header comment).
    FPDF_DOCUMENT raw = FPDF_LoadDocument(opt.pdf.c_str(), nullptr);

    // --reference: split pdftotext -layout's output on form feed, one chunk per page.
    auto split_on_form_feed = [](const std::string& path) {
        std::vector<std::string> out;
        std::ifstream f(path, std::ios::binary);
        if (f.good()) {
            std::string all((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
            std::string cur;
            for (char c : all) {
                if (c == '\f') { out.push_back(cur); cur.clear(); }
                else cur.push_back(c);
            }
            out.push_back(cur);
        }
        return out;
    };
    const std::vector<std::string> reference_pages = split_on_form_feed(opt.reference_file);

    // --cli-reference (#355): megapdf-cli's own output for this document, run by the battery
    // with --page-marker rather than the default form feed. A page separator that is a
    // multi-word line ("--- page N ---") cannot be confused with real page content the way a
    // single control character can: a real document's text occasionally DOES contain a literal
    // U+000C (a bad ToUnicode mapping is enough), which silently shifts every later page's
    // split by one and was observed corrupting this exact comparison on a real corpus document
    // before this was changed to marker-based splitting. Lines are matched against the writer's
    // exact format (megapdf_write_text.cpp / megapdf_cli.cpp's WriteSeparator) with sscanf's "no
    // trailing characters" idiom (the `%n`-free "%d %c" pair below only matches when nothing
    // follows the number and the word "page").
    auto split_on_page_markers = [](const std::string& path) {
        std::vector<std::string> out;
        std::ifstream f(path, std::ios::binary);
        if (!f.good()) return out;
        std::string line, cur;
        while (std::getline(f, line)) {
            if (!line.empty() && line.back() == '\r') line.pop_back();
            int page_number = 0;
            char extra = 0;
            const int parsed = std::sscanf(line.c_str(), "--- page %d ---%c", &page_number, &extra);
            if (parsed == 1) {
                out.push_back(cur);
                cur.clear();
                continue;
            }
            cur += line;
            cur += '\n';
        }
        out.push_back(cur);
        return out;
    };
    // The writer's PAGE_IMAGE placeholder (design §2/§5, "[Page N has no text layer]") is
    // synthesised text, not a contract-9 block's own TEXT field -- the internal fidelity
    // measure above never sees it (it reads megapdf_block_string() directly), so it must be
    // stripped from the CLI's actual stdout before tokenizing or a textless page would count as
    // an "invented" mismatch against the raw side's correctly-empty token set.
    // #358: likewise a tagged page's "[Figure: alt]" line -- the tree's description of a
    // picture, not text on the page (the writer keeps it on one line, so the whole line goes).
    auto strip_page_image_placeholder = [](std::string text) {
        const std::string prefix = "[Page ";
        const std::string suffix = " has no text layer]";
        for (size_t at = text.find(prefix); at != std::string::npos; at = text.find(prefix, at)) {
            size_t digits_end = at + prefix.size();
            while (digits_end < text.size() && text[digits_end] >= '0' && text[digits_end] <= '9') digits_end++;
            if (digits_end == at + prefix.size() || text.compare(digits_end, suffix.size(), suffix) != 0) {
                at += prefix.size();
                continue;
            }
            text.erase(at, digits_end + suffix.size() - at);
        }
        std::string kept;
        size_t start = 0;
        while (start <= text.size()) {
            size_t end = text.find('\n', start);
            if (end == std::string::npos) end = text.size();
            const std::string line = text.substr(start, end - start);
            const bool figure = line.size() >= 10 && line.compare(0, 9, "[Figure: ") == 0 && line.back() == ']';
            if (!figure) {
                kept += line;
                kept += '\n';
            }
            if (end == text.size()) break;
            start = end + 1;
        }
        return kept;
    };
    std::vector<std::string> cli_reference_pages;
    if (!opt.cli_reference_file.empty()) {
        cli_reference_pages = split_on_page_markers(opt.cli_reference_file);
        for (std::string& page : cli_reference_pages) page = strip_page_image_placeholder(page);
    }

    std::vector<int> confidences;
    std::vector<double> ms_per_page;
    int tagged_pages = 0, textless_pages = 0, multicol_pages = 0, manycut_pages = 0;
    // #358: pages contract 9 actually read through the tree (source == TAGGED) -- the census's
    // "tagged" count minus this is what the trust rule rejected.
    int tree_pages = 0;
    // #358's trust-threshold measurement, one entry per tagged page with a --reference (index-
    // aligned): the page index, the tree's character coverage (x1000: real characters whose
    // text object carries a marked-content ID the page's tree references, over all real
    // characters -- the type-agnostic proxy for the core's own rule), tau of the TREE order
    // (TreeTokenOrder: the DFS marked-content order, i.e. exactly the order the tagged path
    // reads) against poppler, and tau of the heuristic order against poppler (the same number
    // tau_ref holds for that page). Binned by coverage over the corpus, the two tau columns say
    // at which coverage the tree stops beating the heuristics -- kTaggedMinCoverage's evidence.
    std::vector<long long> tree_cov_page, tree_cov_x1000, tau_treeref_x1000, tau_heurref_x1000;
    FidelityCounts fidelity_total;
    FidelityCounts cli_fidelity_total;   // #355: the same measure 1, through megapdf-cli's own output
    int fidelity_low09 = 0;
    std::vector<long long> tau_tree_x1000, tau_ref_x1000;
    // #384 investigation: page shape metadata, index-aligned to tau_ref_x1000 (see the --reference
    // doc comment above) -- filled at the same push_back site as tau_ref_x1000 below, never
    // independently, so the three vectors and tau_ref_x1000 always have equal length.
    // #384 mitigation grounding: MaxBackwardJumpUnits' result for this same page, x1000 like
    // tau itself, same index alignment as the three above. tau_ref_conf is this page's REAL
    // megapdf_structure_page_confidence() (not a tool-side proxy, unlike tau_ref_jump) -- the
    // most direct way to confirm the mitigation's actual confidence signal, not a
    // stand-in for it, correlates with tau_ref.
    std::vector<long long> tau_ref_page, tau_ref_tokens, tau_ref_area, tau_ref_jump, tau_ref_conf;

    // #514 spike, criterion 4 ("scope honesty": how often reflow would decline and show a page
    // as a picture) and criterion 1's eligibility rule (#523: a gate blind to a script must not
    // be quoted over it). One entry per page of the document, EVERY page -- the lists below are
    // filled before any branch that can skip a page, because the pages that matter most to
    // criterion 4 are exactly the ones with no text to measure: a scan reaches none of the
    // fidelity or tau code, and a decline count assembled inside that code would read zero for
    // the whole textless population and look like good news.
    //
    // Raw inputs, not verdicts: the confidence GATE is derived from criterion 1 after the fact,
    // so "low confidence" cannot be decided here. Post-processing applies plan §2 tier 3's rule
    // (a page with FIELD blocks or any widget annotation shows as a page image; a page with no
    // text layer shows as a page image; a page under the gate is refused) to these columns.
    std::vector<long long> pg_index;      // page index, 0-based
    std::vector<long long> pg_conf;       // megapdf_structure_page_confidence for the page
    std::vector<long long> pg_text;       // 1 when FPDFText_CountChars > 0, else 0 (a scan is 0)
    std::vector<long long> pg_field;      // FIELD blocks on the page (ALL_FIELDS, so empty fields count)
    std::vector<long long> pg_widget;     // widget annotations on the page, counted independently
    std::vector<long long> pg_pageimg;    // PAGE_IMAGE blocks on the page
    std::vector<long long> pg_content;    // blocks that would become reflowed TEXT (not FIELD/PAGE_IMAGE)
    // Script census over the page's real characters, letters and syllables only (ScriptOf).
    std::vector<long long> pg_latin, pg_cjk, pg_rtl, pg_cyrgrk, pg_brahmic, pg_other;
    // The census's own per-page column verdicts, which until #514 were only ever summed into
    // multicol=/manycut=. Per page they answer the question a corpus-wide tau cannot: whether the
    // pages that disagree with `pdftotext -layout` are the multi-column ones -- where -layout
    // reads ACROSS columns by design and the disagreement may be the reference's, not ours (the
    // #521 lesson: a measure can mark down an engine that was right).
    std::vector<long long> pg_multicol, pg_manycut;

    for (int p = 0; p < pages; p++) {
        const auto page_t0 = std::chrono::steady_clock::now();
        confidences.push_back(megapdf_structure_page_confidence(s, p));

        const ColumnCensus cc = ColumnCensusForPage(blocks_by_page[static_cast<size_t>(p)], body_size);
        if (cc.multi_column) multicol_pages++;
        if (cc.many_cut) manycut_pages++;
        pg_multicol.push_back(cc.multi_column ? 1 : 0);
        pg_manycut.push_back(cc.many_cut ? 1 : 0);

        FPDF_PAGE raw_page = raw != nullptr ? FPDF_LoadPage(raw, p) : nullptr;
        FPDF_TEXTPAGE textpage = raw_page != nullptr ? FPDFText_LoadPage(raw_page) : nullptr;

        // #514 criterion 4: the per-page columns, every page, before anything can `continue` or
        // fall into the textless branch. Block kinds come from the blocks this page already has
        // (blocks_by_page), so they are the shipped answer for this page, whichever path read it.
        {
            long long n_field = 0, n_pageimg = 0, n_content = 0;
            for (const megapdf_block& b : blocks_by_page[static_cast<size_t>(p)]) {
                if (b.kind == MEGAPDF_BLOCK_FIELD) n_field++;
                else if (b.kind == MEGAPDF_BLOCK_PAGE_IMAGE) n_pageimg++;
                else if (b.kind != MEGAPDF_BLOCK_FURNITURE) n_content++;
            }
            // Widget annotations, counted from PDFium rather than from FIELD blocks, because plan
            // §2 tier 3's rule is "FIELD blocks OR any widget annotation" and the two can
            // disagree: a page whose AcroForm PDFium will not load, or an XFA-only form, has
            // widgets and no FIELD blocks. Counting both is what makes that disagreement show up
            // in the numbers instead of being assumed away.
            long long n_widget = 0;
            if (raw_page != nullptr) {
                const int annots = FPDFPage_GetAnnotCount(raw_page);
                for (int a = 0; a < annots; a++) {
                    FPDF_ANNOTATION an = FPDFPage_GetAnnot(raw_page, a);
                    if (an == nullptr) continue;
                    if (FPDFAnnot_GetSubtype(an) == FPDF_ANNOT_WIDGET) n_widget++;
                    FPDFPage_CloseAnnot(an);
                }
            }
            long long latin = 0, cjk = 0, rtl = 0, cyrgrk = 0, brahmic = 0, other = 0;
            long long has_text = 0;
            if (textpage != nullptr) {
                const int n_chars = FPDFText_CountChars(textpage);
                if (n_chars > 0) has_text = 1;
                for (int i = 0; i < n_chars; i++) {
                    if (FPDFText_IsGenerated(textpage, i) == 1) continue;
                    const unsigned int u = FPDFText_GetUnicode(textpage, i);
                    if (u == 0) continue;
                    switch (ScriptOf(u)) {
                        case kScriptLatin: latin++; break;
                        case kScriptCjk: cjk++; break;
                        case kScriptRtl: rtl++; break;
                        case kScriptCyrGrk: cyrgrk++; break;
                        case kScriptBrahmic: brahmic++; break;
                        case kScriptOther: other++; break;
                        default: break;
                    }
                }
            }
            pg_index.push_back(p);
            pg_conf.push_back(confidences[static_cast<size_t>(p)]);
            pg_text.push_back(has_text);
            pg_field.push_back(n_field);
            pg_widget.push_back(n_widget);
            pg_pageimg.push_back(n_pageimg);
            pg_content.push_back(n_content);
            pg_latin.push_back(latin);
            pg_cjk.push_back(cjk);
            pg_rtl.push_back(rtl);
            pg_cyrgrk.push_back(cyrgrk);
            pg_brahmic.push_back(brahmic);
            pg_other.push_back(other);
        }

        if (textpage != nullptr) {
            const int chars = FPDFText_CountChars(textpage);
            if (chars <= 0) textless_pages++;
            // #360: line-wrap hyphens joined the same way megapdf_structure.cpp joins them,
            // so measure 1 compares "encodings" (one token) to "encodings" (one token), not to
            // "encod"+"ings" (two).
            const std::vector<Token> pdfium_tokens = Tokenize(JoinLineWrapHyphens(textpage, chars));
            const FidelityCounts fc = MultisetF1(tokens_by_page[static_cast<size_t>(p)], pdfium_tokens);
            fidelity_total.matched += fc.matched;
            fidelity_total.a += fc.a;
            fidelity_total.b += fc.b;
            if (F1(fc) < 0.9) fidelity_low09++;

            // #355: the same measure 1, but with the real megapdf-cli binary's own output
            // (--cli-reference) standing in for tokens_by_page -- so the fidelity gate is
            // proven through the shipped tool, not only through this process's direct
            // megapdf_structure_load() call. The CLI run this compares against uses
            // --keep-furniture --no-fields --page-marker, i.e. the same KEEP_FURNITURE flag
            // and the same FIELD-block exclusion measure 1 itself uses (see
            // split_on_page_markers()'s comment for --page-marker's own reason).
            if (static_cast<size_t>(p) < cli_reference_pages.size()) {
                const std::vector<Token> cli_tokens = Tokenize(Utf8ToCodepoints(cli_reference_pages[static_cast<size_t>(p)]));
                const FidelityCounts cli_fc = MultisetF1(cli_tokens, pdfium_tokens);
                cli_fidelity_total.matched += cli_fc.matched;
                cli_fidelity_total.a += cli_fc.a;
                cli_fidelity_total.b += cli_fc.b;
            }

            if (megapdf_structure_page_source(s, p) == MEGAPDF_STRUCTURE_SOURCE_TAGGED) tree_pages++;
            if (!opt.census_only) {
                std::vector<int> mcids;
                std::vector<Token> tree_tokens;
                bool page_tagged = false;
                double tree_coverage = -1;
                if (raw_page != nullptr && TaggedPageTree(raw_page, &mcids)) {
                    tagged_pages++;
                    page_tagged = true;
                    tree_tokens = TreeTokenOrder(textpage, mcids);
                    // measure 2 (#354): the HEURISTIC order against the tree's. Forced through
                    // HEURISTIC_ONLY now that the default path may itself be the tree (#358),
                    // so the measure keeps comparing the two sources rather than the tree with
                    // itself.
                    const std::vector<Token> heuristic_tokens =
                        HeuristicTokensForPage(doc, p, tokens_by_page[static_cast<size_t>(p)],
                                               megapdf_structure_page_source(s, p) == MEGAPDF_STRUCTURE_SOURCE_TAGGED);
                    const double tau = KendallTau(heuristic_tokens, tree_tokens);
                    if (tau > kTauNotEnoughData) tau_tree_x1000.push_back(static_cast<long long>(tau * 1000.0));
                    // #358: type-agnostic coverage of the page's real characters by the tree.
                    std::unordered_set<int> referenced(mcids.begin(), mcids.end());
                    long long real = 0, covered = 0;
                    for (int i = 0; i < chars; i++) {
                        if (FPDFText_IsGenerated(textpage, i) == 1) continue;
                        const unsigned int u = FPDFText_GetUnicode(textpage, i);
                        if (u == 0 || IsWhitespaceCpLocal(u)) continue;
                        real++;
                        FPDF_PAGEOBJECT obj = FPDFText_GetTextObject(textpage, i);
                        if (obj != nullptr && referenced.count(FPDFPageObj_GetMarkedContentID(obj)) != 0) covered++;
                    }
                    if (real > 0) tree_coverage = static_cast<double>(covered) / static_cast<double>(real);
                }
                if (static_cast<size_t>(p) < reference_pages.size()) {
                    const std::vector<Token> ref_tokens = Tokenize(Utf8ToCodepoints(reference_pages[static_cast<size_t>(p)]));
                    // #358: measure 3 stays "this page's blocks against poppler", whichever
                    // path produced them (the shipped answer). The trust-threshold columns
                    // below separate the two sources explicitly.
                    const double tau = KendallTau(tokens_by_page[static_cast<size_t>(p)], ref_tokens);
                    if (page_tagged && tree_coverage >= 0) {
                        const double tau_tree_ref = KendallTau(tree_tokens, ref_tokens);
                        const std::vector<Token> heuristic_tokens =
                            HeuristicTokensForPage(doc, p, tokens_by_page[static_cast<size_t>(p)],
                                                   megapdf_structure_page_source(s, p) == MEGAPDF_STRUCTURE_SOURCE_TAGGED);
                        const double tau_heur_ref = KendallTau(heuristic_tokens, ref_tokens);
                        if (tau_tree_ref > kTauNotEnoughData && tau_heur_ref > kTauNotEnoughData) {
                            tree_cov_page.push_back(p);
                            tree_cov_x1000.push_back(static_cast<long long>(tree_coverage * 1000.0));
                            tau_treeref_x1000.push_back(static_cast<long long>(tau_tree_ref * 1000.0));
                            tau_heurref_x1000.push_back(static_cast<long long>(tau_heur_ref * 1000.0));
                        }
                    }
                    if (tau > kTauNotEnoughData) {
                        tau_ref_x1000.push_back(static_cast<long long>(tau * 1000.0));
                        // #384: page-shape metadata for this same tau, so a slice by "title-page-
                        // like" proxy can be computed post-hoc from the battery log. Page area
                        // comes from the raw page itself (points^2, rounded) -- available even
                        // when raw_page's text page has zero characters, which cannot happen here
                        // since ref_tokens/tau above already required a loaded textpage.
                        tau_ref_page.push_back(p);
                        tau_ref_tokens.push_back(
                            static_cast<long long>(tokens_by_page[static_cast<size_t>(p)].size()));
                        double pw = 0.0, ph = 0.0;
                        if (raw_page != nullptr) {
                            pw = FPDF_GetPageWidth(raw_page);
                            ph = FPDF_GetPageHeight(raw_page);
                        }
                        tau_ref_area.push_back(static_cast<long long>(pw * ph));
                        tau_ref_jump.push_back(static_cast<long long>(
                            MaxBackwardJumpUnits(blocks_by_page[static_cast<size_t>(p)], body_size) * 1000.0));
                        tau_ref_conf.push_back(confidences[static_cast<size_t>(p)]);
                    }
                }
            } else {
                std::vector<int> discard;
                if (raw_page != nullptr && TaggedPageTree(raw_page, &discard)) tagged_pages++;
            }
            FPDFText_ClosePage(textpage);
        } else {
            textless_pages++;
        }
        if (raw_page != nullptr) FPDF_ClosePage(raw_page);
        ms_per_page.push_back(std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - page_t0).count());
    }

    // --dump: the extracted text, never on stdout.
    if (!opt.dump_dir.empty() && !opt.dump_id.empty()) {
        const std::string marker = opt.dump_dir + "/PRIVATE";
        std::ifstream check_marker(marker);
        if (!check_marker.good()) {
            std::ofstream m(marker);
            m << "Extracted document text (#354). Not committed, uploaded or pasted anywhere:\n"
                 "these are Dave's own documents. Delete this directory when you are done reading it.\n";
        }
        std::ofstream out(opt.dump_dir + "/" + opt.dump_id + ".txt", std::ios::binary);
        // Blocks in structure order, one per line, a form feed between pages.
        int last_page = -1;
        for (size_t i = 0; i < n_blocks; i++) {
            megapdf_block b{};
            if (megapdf_block_get(s, i, &b) != MEGAPDF_OK) continue;
            if (b.kind == MEGAPDF_BLOCK_FIELD) continue;
            if (b.page != last_page) {
                if (last_page != -1) out << "\f";
                last_page = b.page;
            }
            const std::vector<unsigned short> text16 = BlockContentString(s, i, b.kind);
            for (unsigned short u : text16) {
                // Minimal UTF-16 -> UTF-8 for the dump file only (never printed).
                if (u < 0x80) out << static_cast<char>(u);
                else if (u < 0x800) {
                    out << static_cast<char>(0xC0 | (u >> 6)) << static_cast<char>(0x80 | (u & 0x3F));
                } else {
                    out << static_cast<char>(0xE0 | (u >> 12)) << static_cast<char>(0x80 | ((u >> 6) & 0x3F))
                        << static_cast<char>(0x80 | (u & 0x3F));
                }
            }
            out << "\n";
        }
    }

    if (raw != nullptr) FPDF_CloseDocument(raw);
    megapdf_structure_free(s);
    megapdf_close(doc);

    const double total_ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
    const double ms_avg = pages > 0 ? total_ms / pages : 0.0;
    std::ostringstream blocks_field;
    // heading, paragraph, list_item, table_row, figure, page_image, furniture, field
    blocks_field << "blocks_heading=" << block_kind_counts[MEGAPDF_BLOCK_HEADING]
                 << " blocks_paragraph=" << block_kind_counts[MEGAPDF_BLOCK_PARAGRAPH]
                 << " blocks_list=" << block_kind_counts[MEGAPDF_BLOCK_LIST_ITEM]
                 << " blocks_table=" << block_kind_counts[MEGAPDF_BLOCK_TABLE_ROW]
                 << " blocks_figure=" << block_kind_counts[MEGAPDF_BLOCK_FIGURE]
                 << " blocks_pageimg=" << block_kind_counts[MEGAPDF_BLOCK_PAGE_IMAGE]
                 << " blocks_furniture=" << block_kind_counts[MEGAPDF_BLOCK_FURNITURE]
                 << " blocks_field=" << block_kind_counts[MEGAPDF_BLOCK_FIELD];

    std::printf("result=ok pages=%d tagged=%d tree=%d textless=%d multicol=%d manycut=%d ms_per_page=%.3f rss_kb=%lld "
                "conf_deciles=%s %s fid_match=%lld fid_a=%lld fid_b=%lld fid_low09=%d "
                "cli_fid_match=%lld cli_fid_a=%lld cli_fid_b=%lld "
                "tau_tree_n=%zu tau_tree=%s tau_ref_n=%zu tau_ref=%s "
                "tau_ref_page=%s tau_ref_tokens=%s tau_ref_area=%s tau_ref_jump=%s tau_ref_conf=%s "
                "tree_cov_n=%zu tree_cov_page=%s tree_cov=%s tau_treeref=%s tau_heurref=%s "
                "pg_n=%zu pg_index=%s pg_conf=%s pg_text=%s pg_field=%s pg_widget=%s pg_pageimg=%s "
                "pg_content=%s pg_latin=%s pg_cjk=%s pg_rtl=%s pg_cyrgrk=%s pg_brahmic=%s pg_other=%s "
                "pg_multicol=%s pg_manycut=%s\n",
                pages, tagged_pages, tree_pages, textless_pages, multicol_pages, manycut_pages, ms_avg, PeakRssKb(),
                ConfidenceDeciles(confidences).c_str(), blocks_field.str().c_str(), fidelity_total.matched,
                fidelity_total.a, fidelity_total.b, fidelity_low09,
                cli_fidelity_total.matched, cli_fidelity_total.a, cli_fidelity_total.b,
                tau_tree_x1000.size(), JoinInts(tau_tree_x1000).c_str(), tau_ref_x1000.size(),
                JoinInts(tau_ref_x1000).c_str(), JoinInts(tau_ref_page).c_str(),
                JoinInts(tau_ref_tokens).c_str(), JoinInts(tau_ref_area).c_str(), JoinInts(tau_ref_jump).c_str(),
                JoinInts(tau_ref_conf).c_str(), tree_cov_page.size(), JoinInts(tree_cov_page).c_str(),
                JoinInts(tree_cov_x1000).c_str(), JoinInts(tau_treeref_x1000).c_str(), JoinInts(tau_heurref_x1000).c_str(),
                pg_index.size(), JoinInts(pg_index).c_str(), JoinInts(pg_conf).c_str(), JoinInts(pg_text).c_str(),
                JoinInts(pg_field).c_str(), JoinInts(pg_widget).c_str(), JoinInts(pg_pageimg).c_str(),
                JoinInts(pg_content).c_str(), JoinInts(pg_latin).c_str(), JoinInts(pg_cjk).c_str(),
                JoinInts(pg_rtl).c_str(), JoinInts(pg_cyrgrk).c_str(), JoinInts(pg_brahmic).c_str(),
                JoinInts(pg_other).c_str(), JoinInts(pg_multicol).c_str(), JoinInts(pg_manycut).c_str());
    return 0;
}

// #514 criterion 3: megapdf_structure_load over a whole document, timed and measured ALONE --
// no per-page text pages, no reference, no fidelity pass, because the criterion is about that one
// call. Peak RSS is reported before and after the load so the load's own share is visible rather
// than inferred from a whole process that also built token multisets.
//
// This cannot be a phone number. It is the number of whatever machine it runs on, and the spike's
// write-up says which machine that was: a container figure must never be presented as a device
// figure (and an x86_64 figure is not an arm64 one either).
int RunBench(const std::string& pdf, int repeats) {
    std::vector<unsigned char> bytes;
    {
        std::ifstream in(pdf, std::ios::binary);
        if (!in.good()) { std::printf("result=unreadable\n"); return 0; }
        bytes.assign(std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>());
    }
    megapdf_document* doc = megapdf_open(bytes.data(), bytes.size(), nullptr);
    if (doc == nullptr) { std::printf("result=cannot-open\n"); return 0; }
    const int pages = megapdf_page_count(doc);
    const long long rss_before = PeakRssKb();
    double best = 0, worst = 0, total = 0;
    size_t blocks = 0;
    for (int r = 0; r < repeats; r++) {
        const auto t0 = std::chrono::steady_clock::now();
        megapdf_structure* s = megapdf_structure_load(doc, 0, pages, MEGAPDF_STRUCTURE_DEFAULT, nullptr);
        const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
        if (s == nullptr) { std::printf("result=load-failed pages=%d\n", pages); megapdf_close(doc); return 0; }
        blocks = megapdf_block_count(s);
        // Held, not freed, for the LAST repeat only, so the peak below includes a live structure:
        // what a reflow view actually holds while the reader is reading.
        if (r + 1 < repeats) megapdf_structure_free(s);
        else {
            std::printf("result=ok pages=%d blocks=%zu load_ms_best=%.1f load_ms_worst=%.1f load_ms_mean=%.1f "
                        "rss_before_kb=%lld rss_peak_kb=%lld repeats=%d\n",
                        pages, blocks, best == 0 ? ms : (std::min)(best, ms), (std::max)(worst, ms),
                        (total + ms) / repeats, rss_before, PeakRssKb(), repeats);
            megapdf_structure_free(s);
            megapdf_close(doc);
            return 0;
        }
        best = best == 0 ? ms : (std::min)(best, ms);
        worst = (std::max)(worst, ms);
        total += ms;
    }
    megapdf_close(doc);
    return 0;
}

int RunCensus(const std::string& pdf) {
    Options opt;
    opt.pdf = pdf;
    opt.census_only = true;
    return RunCheck(opt);
}

// ---------------------------------------------------------------------------
// diag mode (#363 investigation only -- not part of the #354 battery/gate; a numbers-only
// breakdown of measure 1's "heuristic side has more tokens than the raw side" excess, by
// block kind and by two non-exclusive signals:
//   - "dup": this exact token text occurs more than once among ALL of the page's heuristic
//     tokens (across any block) -- a sign the SAME content was read into the heuristic
//     output more times than it exists in the page at all.
//   - "crossblock": stronger version of "dup" -- the token occurs in >= 2 DISTINCT blocks on
//     the page (not just repeated within one block's own running text, e.g. "the"), which is
//     what an actual duplication bug (furniture kept twice, a field's value re-read as page
//     text, a continuation re-emitting a join) would produce. Paired with the OTHER block
//     kind that also carries the token, tallied as an unordered (kind, other_kind) matrix.
//   - "invented": the token never appears in the page's raw (FPDFText_GetText) token multiset
//     at all -- not merely short of copies, but absent -- suggesting synthesized text (a list
//     marker or separator leaking into block text) rather than a re-read of real content.
// A token can be both "dup"/"crossblock" and "invented" (e.g. a synthetic marker repeated on
// every block of a run). Every unmatched (excess) heuristic-side token occurrence is counted
// in exactly one kind bucket and is independently tallied against each signal it satisfies,
// so kind totals sum to the overall excess count but the signal counts do not have to.
// Numbers only, per design's corpus-privacy discipline: no filenames, paths or text.
// ---------------------------------------------------------------------------
struct DiagTotals {
    long long pages_seen = 0;
    long long excess_total = 0;
    long long kind_excess[9] = {0};   // index by megapdf_block_kind (1..8)
    long long dup_excess = 0;
    long long invented_excess = 0;
    long long crossblock_excess = 0;
    long long crossblock_pair[9][9] = {{0}};  // (min kind, max kind) -> count
    // Of the "invented" (R==0) occurrences: how many equal the literal concatenation of two
    // (or three) CONSECUTIVE raw tokens with nothing between them -- i.e. the heuristic word-
    // gap test failed to see a real inter-word gap the raw side's own generated-break/space
    // handling did see, so two (or three) real words were read as one "word" and therefore one
    // token. Not printed with the token text itself: a boolean per occurrence, tallied.
    long long invented_merge2 = 0;
    long long invented_merge3 = 0;
    // Of the "invented" occurrences: how many are a SUBSTRING of some single raw token (or
    // vice versa) -- i.e. the heuristic word-gap test OVER-split one real word into several
    // pieces (kWordGapEm / the loose-char-box advance not covering some glyph's true width),
    // so a whole word's real characters get counted as two-or-more separate, shorter,
    // "invented" tokens that individually never occur in the raw stream, which only ever
    // produces the whole word.
    long long invented_substring_of_raw = 0;
    // Same three numbers `check` reports (fid_a/fid_b/fid_match), so this mode's F1 can be
    // compared directly against a battery run without a second invocation.
    long long fid_a = 0, fid_b = 0, fid_match = 0;
};

void RunDiagOnDoc(const std::string& pdf, DiagTotals* totals) {
    megapdf_document* doc = megapdf_open_file(pdf.c_str(), nullptr);
    if (doc == nullptr) return;
    const int pages = megapdf_page_count(doc);
    if (pages <= 0) { megapdf_close(doc); return; }

    megapdf_structure* s = megapdf_structure_load(doc, 0, pages, MEGAPDF_STRUCTURE_KEEP_FURNITURE |
                                                                       MEGAPDF_STRUCTURE_ALL_FIELDS, nullptr);
    if (s == nullptr) { megapdf_close(doc); return; }

    struct BlockToks {
        int kind = 0;
        std::vector<Token> tokens;
    };
    const size_t n_blocks = megapdf_block_count(s);
    std::vector<std::vector<BlockToks>> by_page(static_cast<size_t>(pages));
    for (size_t i = 0; i < n_blocks; i++) {
        megapdf_block b{};
        if (megapdf_block_get(s, i, &b) != MEGAPDF_OK) continue;
        if (b.page < 0 || b.page >= pages) continue;
        if (b.kind == MEGAPDF_BLOCK_FIELD) continue;  // measure 1 excludes FIELD blocks
        BlockToks bt;
        bt.kind = b.kind;
        const std::vector<unsigned short> text16 = BlockContentString(s, i, b.kind);
        bt.tokens = Tokenize(Utf16ToCodepoints(text16));
        by_page[static_cast<size_t>(b.page)].push_back(std::move(bt));
    }

    FPDF_DOCUMENT raw = FPDF_LoadDocument(pdf.c_str(), nullptr);
    for (int p = 0; p < pages; p++) {
        FPDF_PAGE raw_page = raw != nullptr ? FPDF_LoadPage(raw, p) : nullptr;
        FPDF_TEXTPAGE textpage = raw_page != nullptr ? FPDFText_LoadPage(raw_page) : nullptr;
        if (textpage == nullptr) {
            if (raw_page != nullptr) FPDF_ClosePage(raw_page);
            continue;
        }
        const int chars = FPDFText_CountChars(textpage);
        const std::vector<Token> pdfium_tokens = Tokenize(JoinLineWrapHyphens(textpage, chars));
        FPDFText_ClosePage(textpage);
        FPDF_ClosePage(raw_page);
        totals->pages_seen++;

        const auto& blocks = by_page[static_cast<size_t>(p)];

        // H: heuristic-side multiset count per token, over the whole page.
        // BlockSet: which block indices (within `blocks`) carry each token, up to a small cap
        // (we only need "how many distinct blocks", not an exhaustive list, and a token like
        // "the" can legitimately recur in dozens of blocks on a text-heavy page).
        std::map<Token, int> H;
        std::map<Token, std::vector<int>> block_set;
        for (size_t bi = 0; bi < blocks.size(); bi++) {
            for (const Token& t : blocks[bi].tokens) {
                H[t]++;
                auto& v = block_set[t];
                if (v.empty() || v.back() != static_cast<int>(bi)) {
                    if (v.size() < 8) v.push_back(static_cast<int>(bi));
                }
            }
        }
        std::map<Token, int> R;
        for (const Token& t : pdfium_tokens) R[t]++;

        {
            long long a_page = 0;
            for (const auto& kv : H) a_page += kv.second;
            long long matched_page = 0;
            for (const auto& kv : H) {
                const auto it = R.find(kv.first);
                matched_page += (std::min)(kv.second, it != R.end() ? it->second : 0);
            }
            totals->fid_a += a_page;
            totals->fid_b += static_cast<long long>(pdfium_tokens.size());
            totals->fid_match += matched_page;
        }

        // Adjacent-token concatenations of the RAW side's own ordered token stream, so an
        // "invented" heuristic token can be tested against "is this just two/three real words
        // the raw side kept apart, that we ran together?".
        std::map<Token, int> merge2, merge3;
        for (size_t k = 0; k + 1 < pdfium_tokens.size(); k++) {
            merge2[pdfium_tokens[k] + pdfium_tokens[k + 1]]++;
        }
        for (size_t k = 0; k + 2 < pdfium_tokens.size(); k++) {
            merge3[pdfium_tokens[k] + pdfium_tokens[k + 1] + pdfium_tokens[k + 2]]++;
        }
        std::vector<const Token*> raw_keys;
        raw_keys.reserve(R.size());
        for (const auto& kv : R) raw_keys.push_back(&kv.first);

        std::map<Token, int> remaining = R;
        for (size_t bi = 0; bi < blocks.size(); bi++) {
            const int kind = blocks[bi].kind;
            for (const Token& t : blocks[bi].tokens) {
                int& rem = remaining[t];
                if (rem > 0) { rem--; continue; }
                // Excess: this occurrence has no raw-side counterpart left to match.
                totals->excess_total++;
                if (kind >= 1 && kind <= 8) totals->kind_excess[kind]++;
                const int h_count = H[t];
                const int r_count = R.count(t) ? R[t] : 0;
                const bool dup = h_count > 1;
                const bool invented = r_count == 0;
                if (dup) totals->dup_excess++;
                if (invented) totals->invented_excess++;
                if (invented) {
                    if (merge2.count(t)) totals->invented_merge2++;
                    else if (merge3.count(t)) totals->invented_merge3++;
                    for (const Token* rk : raw_keys) {
                        const bool t_in_rk = rk->size() > t.size() && rk->find(t) != Token::npos;
                        const bool rk_in_t = t.size() > rk->size() && t.find(*rk) != Token::npos;
                        if (t_in_rk || rk_in_t) { totals->invented_substring_of_raw++; break; }
                    }
                }
                const auto& holders = block_set[t];
                if (holders.size() > 1) {
                    totals->crossblock_excess++;
                    int other_kind = kind;
                    for (int hb : holders) {
                        if (hb != static_cast<int>(bi)) { other_kind = blocks[static_cast<size_t>(hb)].kind; break; }
                    }
                    if (kind >= 1 && kind <= 8 && other_kind >= 1 && other_kind <= 8) {
                        const int lo = (std::min)(kind, other_kind), hi = (std::max)(kind, other_kind);
                        totals->crossblock_pair[lo][hi]++;
                    }
                }
            }
        }
    }
    if (raw != nullptr) FPDF_CloseDocument(raw);
    megapdf_structure_free(s);
    megapdf_close(doc);
}

int RunDiag(const std::vector<std::string>& pdfs) {
    DiagTotals totals;
    for (const auto& pdf : pdfs) RunDiagOnDoc(pdf, &totals);
    std::printf("diag docs=%zu pages=%lld excess_total=%lld\n", pdfs.size(), totals.pages_seen, totals.excess_total);
    const double f1 = (totals.fid_a + totals.fid_b) > 0
                           ? 2.0 * static_cast<double>(totals.fid_match) / static_cast<double>(totals.fid_a + totals.fid_b)
                           : 1.0;
    std::printf("fid_a=%lld fid_b=%lld fid_match=%lld f1=%.6f\n", totals.fid_a, totals.fid_b, totals.fid_match, f1);
    std::printf("excess_by_kind heading=%lld paragraph=%lld list_item=%lld table_row=%lld figure=%lld "
                "page_image=%lld furniture=%lld field=%lld\n",
                totals.kind_excess[MEGAPDF_BLOCK_HEADING], totals.kind_excess[MEGAPDF_BLOCK_PARAGRAPH],
                totals.kind_excess[MEGAPDF_BLOCK_LIST_ITEM], totals.kind_excess[MEGAPDF_BLOCK_TABLE_ROW],
                totals.kind_excess[MEGAPDF_BLOCK_FIGURE], totals.kind_excess[MEGAPDF_BLOCK_PAGE_IMAGE],
                totals.kind_excess[MEGAPDF_BLOCK_FURNITURE], totals.kind_excess[MEGAPDF_BLOCK_FIELD]);
    std::printf("signals dup=%lld invented=%lld crossblock=%lld\n", totals.dup_excess, totals.invented_excess,
                totals.crossblock_excess);
    std::printf("invented_explained_by_adjacent_raw_merge merge2=%lld merge3=%lld of invented=%lld\n",
                totals.invented_merge2, totals.invented_merge3, totals.invented_excess);
    std::printf("invented_substring_of_raw=%lld of invented=%lld\n", totals.invented_substring_of_raw,
                totals.invented_excess);
    static const char* kKindName[9] = {"", "heading", "paragraph", "list_item", "table_row",
                                        "figure", "page_image", "furniture", "field"};
    for (int a = 1; a <= 8; a++) {
        for (int b = a; b <= 8; b++) {
            if (totals.crossblock_pair[a][b] > 0) {
                std::printf("crossblock_pair %s+%s=%lld\n", kKindName[a], kKindName[b], totals.crossblock_pair[a][b]);
            }
        }
    }
    return 0;
}

// ---------------------------------------------------------------------------
// diagbaseline mode (#363 follow-up investigation only -- not part of the #354 battery/gate).
//
// PR #364 fixed the dominant token-over-splitting mechanism (kWordGapEm 0.2 -> 0.8) and left
// a smaller, distinct one open: some same-run (no PDFium-generated break) character pairs
// pass the horizontal word-gap test but fail BuildWords' baseline test (core/
// megapdf_structure.cpp's kBaselineEm, |origin_y delta| <= 0.35 em) despite a small horizontal
// gap -- which should rule out "these are on different lines". This mode replicates that
// exact walk (ReadChars + BuildStructure's normal_idx/rotated_idx split + BuildWords' per-pair
// test) over raw PDFium calls -- this tool is built standalone against core headers, not
// against megapdf_structure.cpp's internals (see the kSoftHyphen comment above) -- and, for
// every pair that lands in that residual bucket, tallies numeric characteristics only: no
// text, no filenames, no paths (corpus privacy, same discipline as the diag mode above).
//
// This mode's own fix for the residual bucket it measures landed in the SAME PR as this
// comment (megapdf_structure.cpp's kSuperscriptGapEm/kSuperscriptOffsetEm) -- kept here
// afterwards, not deleted, because it is still the tool that would characterize the NEXT
// residual bucket, the same way #364's diag mode (above) still is.
//
// Two things this mode got wrong in an earlier version, caught by a sanity check against the
// real pipeline before trusting any of its numbers (kept here as the reason, not just fixed
// silently): BuildWords never tests object identity between consecutive characters (only
// preceded_by_break and the gap/baseline tests) -- a "run" can cross a text-object boundary
// as long as PDFium inserted no generated break -- and adjacency is over BuildStructure's
// *filtered* normal_idx (rotated characters, kRotationSkewTolerance-flagged, are pulled into
// a wholly separate leftover_words pass, so a normal character's "previous" character is the
// previous NON-ROTATED one, which can be several real characters back in the raw stream).
// Skipping the rotation split inflated the first measurement's fail count with pairs the real
// BuildWords(normal_idx) walk never actually forms.
//
// kWordGapEmMirror/kBaselineEmMirror/kRotationSkewToleranceMirror below mirror core/
// megapdf_structure.cpp's kWordGapEm/kBaselineEm/kRotationSkewTolerance -- kept in sync by
// hand, same as kSoftHyphen (deliberately not pinned to line numbers here: that file's own
// kSuperscriptGapEm/kSuperscriptOffsetEm fix already shifted every one of them once, which is
// what caught this comment's first, now-corrected, stale line-number set).
// ---------------------------------------------------------------------------
struct BaselineChar {
    unsigned int unicode = 0;
    double loose_l = 0, loose_r = 0;
    double origin_x = 0, origin_y = 0;
    double font_size = 0;
    FPDF_PAGEOBJECT obj = nullptr;
    double skew_b = 0, skew_c = 0;  // relative to |a|, same test as megapdf_structure.cpp's kRotationSkewTolerance
    bool rotated = false;
    bool preceded_by_break = false;
};

constexpr double kWordGapEmMirror = 0.8;
constexpr double kBaselineEmMirror = 0.35;
constexpr double kRotationSkewToleranceMirror = 0.02;

bool IsWhitespaceCpBaseline(unsigned int c) { return IsWhitespaceCpLocal(c); }

std::vector<BaselineChar> ReadBaselineChars(FPDF_TEXTPAGE tp) {
    std::vector<BaselineChar> out;
    const int count = FPDFText_CountChars(tp);
    out.reserve(static_cast<size_t>((std::max)(count, 0)));
    bool pending_break = false;
    for (int i = 0; i < count; i++) {
        if (FPDFText_IsGenerated(tp, i) == 1) { pending_break = true; continue; }
        const unsigned int u = FPDFText_GetUnicode(tp, i);
        if (u == 0 || IsWhitespaceCpBaseline(u)) { pending_break = true; continue; }
        BaselineChar c;
        c.unicode = u;
        double ox = 0, oy = 0;
        FPDFText_GetCharOrigin(tp, i, &ox, &oy);
        c.origin_x = ox;
        c.origin_y = oy;
        c.font_size = FPDFText_GetFontSize(tp, i);
        FS_RECTF loose{};
        if (FPDFText_GetLooseCharBox(tp, i, &loose)) {
            c.loose_l = (std::min)(loose.left, loose.right);
            c.loose_r = (std::max)(loose.left, loose.right);
        }
        c.obj = FPDFText_GetTextObject(tp, i);
        FS_MATRIX m{1, 0, 0, 1, 0, 0};
        if (FPDFText_GetMatrix(tp, i, &m)) {
            const double a = std::fabs(m.a) > 1e-6 ? std::fabs(m.a) : 1.0;
            c.skew_b = m.b / a;
            c.skew_c = m.c / a;
            c.rotated = std::fabs(c.skew_b) > kRotationSkewToleranceMirror ||
                        std::fabs(c.skew_c) > kRotationSkewToleranceMirror;
        }
        c.preceded_by_break = pending_break;
        pending_break = false;
        out.push_back(c);
    }
    return out;
}

struct BaselineTotals {
    long long docs = 0, pages = 0;
    long long rotated_chars = 0, normal_chars = 0;  // BuildStructure's own split, before any pairing
    long long same_run_pairs = 0;       // no break, gap test candidates (within normal_idx only)
    long long gap_ok_baseline_fail = 0; // the residual population this mode exists to describe
    // Direction of the origin_y jump (current - previous), in the page's own y-up space.
    long long dir_up = 0, dir_down = 0;
    // |delta| buckets, in em of the CURRENT character's font size.
    long long bucket_035_05 = 0, bucket_05_1 = 0, bucket_1_2 = 0, bucket_2_5 = 0, bucket_5_plus = 0;
    // The horizontal gap itself (c.loose_l - prev.loose_r), in em -- the word-gap test only
    // has an upper bound (BuildWords: `gap > kWordGapEm * em` starts a new word), never a
    // lower one, so a character whose loose box starts well to the LEFT of the previous
    // character's loose box (a large NEGATIVE gap -- consistent with a line wrapping back to
    // the page's left margin after a line that ran far to the right) passes this "small gap"
    // test just as trivially as a genuine near-zero gap does. Bucketed to tell the two apart.
    long long gap_very_negative = 0;   // < -1 em -- consistent with a real line-wrap, not one word
    long long gap_negative = 0;        // -1 em .. 0
    long long gap_small_positive = 0;  // 0 .. 0.2 em -- a normal intra-word gap
    long long gap_near_threshold = 0;  // 0.2 .. 0.8 em -- close to kWordGapEm itself
    // Same delta/direction/font breakdown, restricted to gap_small_positive only -- the only
    // gap bucket a genuine same-line, same-word continuation (superscript, subscript, kerning
    // jitter) can plausibly fall in; a negative gap cannot be "the next glyph of this word".
    long long fwd_pairs = 0;
    long long fwd_dir_up = 0, fwd_dir_down = 0;
    long long fwd_bucket_035_05 = 0, fwd_bucket_05_1 = 0, fwd_bucket_1_2 = 0, fwd_bucket_2_5 = 0, fwd_bucket_5_plus = 0;
    long long fwd_fsize_equal = 0, fwd_fsize_smaller = 0, fwd_fsize_much_smaller = 0;
    // Font-size ratio min/max between the pair.
    long long fsize_equal = 0;      // ratio > 0.95 -- same size
    long long fsize_smaller = 0;    // 0.5 < ratio <= 0.95 -- modestly smaller (either side, see fsize_current_*)
    long long fsize_much_smaller = 0; // ratio <= 0.5 -- current or previous much smaller than the other
    long long fsize_current_smaller = 0; // of the non-equal ones, which side was smaller
    long long fsize_current_larger = 0;
    long long same_object = 0, diff_object = 0;
    long long skew_nonzero = 0;   // either character's |skew_b| or |skew_c| > 0.001 (near-zero floor)
    long long skew_near_rotation_threshold = 0;  // > 0.01 -- half of kRotationSkewToleranceMirror (0.02), still passing but close
    // Excursion-and-return: the NEXT same-run pair (current -> next) jumps back within 0.15 em
    // of cancelling this one's delta -- the signature of a brief baseline excursion (one glyph,
    // or a short run, offset and then rejoining the main baseline) rather than a sustained one.
    long long excursion_return = 0;
    long long excursion_return_font_restored = 0;  // ...and the font size also returns to the pre-jump size
    // Position on the page (thirds), to rule out a running-header/footer artifact.
    long long pos_top_third = 0, pos_mid_third = 0, pos_bottom_third = 0;
};

void RunDiagBaselineOnDoc(const std::string& pdf, BaselineTotals* totals) {
    FPDF_DOCUMENT doc = FPDF_LoadDocument(pdf.c_str(), nullptr);
    if (doc == nullptr) return;
    totals->docs++;
    const int pages = FPDF_GetPageCount(doc);
    for (int p = 0; p < pages; p++) {
        FPDF_PAGE page = FPDF_LoadPage(doc, p);
        if (page == nullptr) continue;
        FPDF_TEXTPAGE tp = FPDFText_LoadPage(page);
        if (tp == nullptr) { FPDF_ClosePage(page); continue; }
        const double page_h = FPDF_GetPageHeight(page);
        const std::vector<BaselineChar> chars = ReadBaselineChars(tp);
        totals->pages++;

        // BuildStructure's own split (core/megapdf_structure.cpp:1466-1472): only non-rotated
        // characters feed the normal BuildWords/BuildLines/BuildPieces path this residual
        // bucket is about. `normal_idx` mirrors that filtered index list exactly, so adjacency
        // below is "previous NORMAL character", not "previous character in the raw stream".
        std::vector<size_t> normal_idx;
        normal_idx.reserve(chars.size());
        for (size_t ci = 0; ci < chars.size(); ci++) {
            if (!chars[ci].rotated) normal_idx.push_back(ci);
        }
        totals->rotated_chars += static_cast<long long>(chars.size() - normal_idx.size());
        totals->normal_chars += static_cast<long long>(normal_idx.size());

        for (size_t k = 1; k < normal_idx.size(); k++) {
            const BaselineChar& c = chars[normal_idx[k]];
            const BaselineChar& prev = chars[normal_idx[k - 1]];
            if (c.preceded_by_break) continue;
            if (c.obj == prev.obj) totals->same_object++; else totals->diff_object++;  // stat only -- BuildWords does not gate on this
            const double em = c.font_size > 0 ? c.font_size : (prev.font_size > 0 ? prev.font_size : 1.0);
            const double gap = c.loose_l - prev.loose_r;
            const bool gap_ok = gap <= kWordGapEmMirror * em;
            if (!gap_ok) continue;
            totals->same_run_pairs++;
            const double delta = c.origin_y - prev.origin_y;
            const double delta_em = std::fabs(delta) / em;
            if (delta_em <= kBaselineEmMirror) continue;
            totals->gap_ok_baseline_fail++;

            if (delta > 0) totals->dir_up++; else totals->dir_down++;
            if (delta_em <= 0.5) totals->bucket_035_05++;
            else if (delta_em <= 1.0) totals->bucket_05_1++;
            else if (delta_em <= 2.0) totals->bucket_1_2++;
            else if (delta_em <= 5.0) totals->bucket_2_5++;
            else totals->bucket_5_plus++;

            const double gap_em = gap / em;
            const bool fwd = gap_em >= 0.0 && gap_em <= 0.2;
            if (gap_em < -1.0) totals->gap_very_negative++;
            else if (gap_em < 0.0) totals->gap_negative++;
            else if (gap_em <= 0.2) totals->gap_small_positive++;
            else totals->gap_near_threshold++;
            if (fwd) {
                totals->fwd_pairs++;
                if (delta > 0) totals->fwd_dir_up++; else totals->fwd_dir_down++;
                if (delta_em <= 0.5) totals->fwd_bucket_035_05++;
                else if (delta_em <= 1.0) totals->fwd_bucket_05_1++;
                else if (delta_em <= 2.0) totals->fwd_bucket_1_2++;
                else if (delta_em <= 5.0) totals->fwd_bucket_2_5++;
                else totals->fwd_bucket_5_plus++;
            }

            const double fmin = (std::min)(c.font_size, prev.font_size);
            const double fmax = (std::max)(c.font_size, prev.font_size);
            const double fratio = fmax > 0 ? fmin / fmax : 1.0;
            if (fratio > 0.95) totals->fsize_equal++;
            else if (fratio > 0.5) totals->fsize_smaller++;
            else totals->fsize_much_smaller++;
            if (fratio <= 0.95) {
                if (c.font_size < prev.font_size) totals->fsize_current_smaller++;
                else totals->fsize_current_larger++;
            }
            if (fwd) {
                if (fratio > 0.95) totals->fwd_fsize_equal++;
                else if (fratio > 0.5) totals->fwd_fsize_smaller++;
                else totals->fwd_fsize_much_smaller++;
            }

            const double sb = (std::max)(std::fabs(c.skew_b), std::fabs(prev.skew_b));
            const double sc = (std::max)(std::fabs(c.skew_c), std::fabs(prev.skew_c));
            if (sb > 0.001 || sc > 0.001) totals->skew_nonzero++;
            // Both characters are, by construction (normal_idx), already below
            // kRotationSkewToleranceMirror individually -- this checks whether EITHER's skew
            // sits in the upper part of that still-passing range (a near-miss on the rotation
            // test), not a contradiction of the filter above.
            if (sb > 0.01 || sc > 0.01) totals->skew_near_rotation_threshold++;

            if (k + 1 < normal_idx.size()) {
                const BaselineChar& n = chars[normal_idx[k + 1]];
                if (!n.preceded_by_break) {
                    const double gap2 = n.loose_l - c.loose_r;
                    const double em2 = n.font_size > 0 ? n.font_size : em;
                    if (gap2 <= kWordGapEmMirror * em2) {
                        const double delta2 = n.origin_y - c.origin_y;
                        if (std::fabs(delta + delta2) <= 0.15 * em2) {
                            totals->excursion_return++;
                            const double f0 = prev.font_size, f2 = n.font_size;
                            const double rmax = (std::max)(f0, f2), rmin = (std::min)(f0, f2);
                            if (rmax <= 0 || rmin / rmax > 0.9) totals->excursion_return_font_restored++;
                        }
                    }
                }
            }

            const double y_frac = page_h > 0 ? (c.origin_y / page_h) : 0.5;  // 0 = bottom, 1 = top
            if (y_frac > 2.0 / 3.0) totals->pos_top_third++;
            else if (y_frac > 1.0 / 3.0) totals->pos_mid_third++;
            else totals->pos_bottom_third++;
        }
        FPDFText_ClosePage(tp);
        FPDF_ClosePage(page);
    }
    FPDF_CloseDocument(doc);
}

int RunDiagBaseline(const std::vector<std::string>& pdfs) {
    // Unlike every other mode here, this one never calls a megapdf_* entry point (RunDiag's
    // own raw FPDF_LoadDocument call, above, piggybacks on megapdf_open_file's lazy
    // FPDF_InitLibrary()) -- it talks to PDFium directly from the first document, so it must
    // initialize the library itself.
    FPDF_InitLibrary();
    BaselineTotals t;
    for (const auto& pdf : pdfs) RunDiagBaselineOnDoc(pdf, &t);
    std::printf("diagbaseline docs=%lld pages=%lld\n", t.docs, t.pages);
    std::printf("chars rotated=%lld normal=%lld\n", t.rotated_chars, t.normal_chars);
    std::printf("pairs same_object=%lld diff_object=%lld same_run_pairs(gap_ok)=%lld "
                "gap_ok_baseline_fail=%lld\n",
                t.same_object, t.diff_object, t.same_run_pairs, t.gap_ok_baseline_fail);
    std::printf("direction up=%lld down=%lld\n", t.dir_up, t.dir_down);
    std::printf("delta_em_buckets 0.35-0.5=%lld 0.5-1=%lld 1-2=%lld 2-5=%lld 5+=%lld\n",
                t.bucket_035_05, t.bucket_05_1, t.bucket_1_2, t.bucket_2_5, t.bucket_5_plus);
    std::printf("gap_em_buckets very_negative(<-1)=%lld negative(-1..0)=%lld small_positive(0..0.2)=%lld "
                "near_threshold(0.2..0.8)=%lld\n",
                t.gap_very_negative, t.gap_negative, t.gap_small_positive, t.gap_near_threshold);
    std::printf("fwd(gap 0..0.2em only) pairs=%lld dir_up=%lld dir_down=%lld\n", t.fwd_pairs, t.fwd_dir_up,
                t.fwd_dir_down);
    std::printf("fwd delta_em_buckets 0.35-0.5=%lld 0.5-1=%lld 1-2=%lld 2-5=%lld 5+=%lld\n",
                t.fwd_bucket_035_05, t.fwd_bucket_05_1, t.fwd_bucket_1_2, t.fwd_bucket_2_5, t.fwd_bucket_5_plus);
    std::printf("fwd font_size_ratio equal(>0.95)=%lld smaller(0.5-0.95)=%lld much_smaller(<=0.5)=%lld\n",
                t.fwd_fsize_equal, t.fwd_fsize_smaller, t.fwd_fsize_much_smaller);
    std::printf("font_size_ratio equal(>0.95)=%lld smaller(0.5-0.95)=%lld much_smaller(<=0.5)=%lld\n",
                t.fsize_equal, t.fsize_smaller, t.fsize_much_smaller);
    std::printf("font_size_side current_smaller=%lld current_larger=%lld\n",
                t.fsize_current_smaller, t.fsize_current_larger);
    std::printf("skew nonzero=%lld near_rotation_threshold=%lld\n", t.skew_nonzero,
                t.skew_near_rotation_threshold);
    std::printf("excursion_return=%lld (of gap_ok_baseline_fail=%lld) font_restored=%lld\n",
                t.excursion_return, t.gap_ok_baseline_fail, t.excursion_return_font_restored);
    std::printf("position top_third=%lld mid_third=%lld bottom_third=%lld\n", t.pos_top_third, t.pos_mid_third,
                t.pos_bottom_third);
    return 0;
}

// ---------------------------------------------------------------------------
// headingdiag mode (#375 investigation only -- not part of the #354 battery/gate).
//
// Characterizes the bold-at-body-size HEADING rule (core/megapdf_structure.cpp's
// GatherOneBlock; design #142 section 1.2: "bold at >= body [size], < 70% column width,
// followed by a gap") corpus-wide, to test #375's report that this rule over-fires on
// tabular/invoice/statement documents (table headers, address fragments, dollar values,
// form-field labels wrongly read as headings) and #375's own coordinator follow-up (a
// 25-document hand read finding the same over-firing on documents an XY-cut column signal
// alone would not flag: a single-column forwarded-email header block and a label/value form
// summary, neither one a literal multi-column layout).
//
// This mode never reads megapdf_structure.cpp's own internal cutter state (this tool is built
// standalone against the public contract-9 ABI, same discipline as every mode above). It
// distinguishes the heading rule's two routes from megapdf_span::size_ratio alone: given a
// HEADING block, GatherOneBlock only reaches the bold-at-body branch when the earlier
// size->=1.15x-body branch already failed for every line in the group, so a HEADING block
// whose spans' char-weighted average size_ratio is < kHeadingSizeRatioMirror (1.15, mirroring
// megapdf_structure.cpp's kHeadingSizeRatio) came from the bold branch, not the size branch --
// exact, given the two routes the current code has, not an approximation.
//
// Two structural signals, both measured, neither assumed correct in advance:
//   1. RUN LENGTH -- how many bold-at-body HEADING blocks appear back-to-back in a page's own
//      content stream (HEADING/PARAGRAPH/LIST_ITEM blocks only, in reading order -- the same
//      set GatherOneBlock's `content` vector holds before figures/fields/furniture are spliced
//      in, so a figure or a filled form field between two headings does not break a run any
//      more than it would in the real pipeline). A genuine heading is a rare, isolated
//      structural marker (design's own two/three examples, and this issue's legal-contract
//      counter-example: 3 correct headings out of 137 lines, none adjacent to another). A
//      table column, a forwarded email's header block or a form's label/value list produces
//      MANY short bold-at-body "headings" one after another with nothing else between them.
//   2. XY-CUT COLUMN CENSUS -- the SAME per-page multi_column/many_cut proxy `check`/`census`
//      already compute (ColumnCensusForPage, above) -- the issue's OWN suggested direction
//      (suppress inside a detected multi-cut/tabular region). Tallied per bold-at-body heading,
//      split by isolated (run length 1) vs. clustered (run length >= 3), to measure how much of
//      the clustered population a column-count-only signal would actually have covered.
// Plus two independent, purely-numeric proxies for "this individual heading looks like a false
// positive" (the text itself is inspected to compute these but never printed, stored or
// otherwise leaves this process -- same corpus-privacy discipline as every mode above):
//   - SHORT: at most kShortTextChars UTF-16 code units in the block's whole text (a rough,
//     surrogate-pair-insensitive proxy for "short label", not an exact character count).
//   - NUMERIC_LIKE: a POSITIVE allowlist match, mirroring megapdf_structure.cpp's own
//     IsNumericLikeText exactly (see that function's comment for why this is an allowlist of
//     digits/punctuation rather than "contains no Latin letter" -- the latter would misread a
//     genuine heading in any script outside ASCII/Latin-1/Latin-Extended-A as numeric-like) --
//     a dollar amount, an account number, a bare code, a lone colon or dash all qualify;
//     "Total:" does not (it has a letter), which is deliberate: that case is exactly the "short
//     label" SHORT alone is for.
// ---------------------------------------------------------------------------
constexpr double kHeadingSizeRatioMirror = 1.15;   // mirrors megapdf_structure.cpp's kHeadingSizeRatio.
constexpr int kShortTextChars = 20;

bool IsAsciiDigitLocal(unsigned int c) { return c >= '0' && c <= '9'; }

// Mirrors megapdf_structure.cpp's IsNumericLikePunctuation exactly.
bool IsNumericLikePunctuationLocal(unsigned int c) {
    switch (c) {
        case '.': case ',': case ':': case ';': case '-': case '(': case ')': case '/':
        case '%': case '#': case '$': case 0x00A3 /* £ */: case 0x20AC /* € */: case ' ':
            return true;
        default:
            return false;
    }
}

double BlockAvgSizeRatio(const megapdf_structure* s, size_t idx) {
    const size_t n = megapdf_block_span_count(s, idx);
    double weighted = 0;
    long long total_chars = 0;
    for (size_t si = 0; si < n; si++) {
        megapdf_span sp{};
        if (megapdf_block_span_get(s, idx, si, &sp) != MEGAPDF_OK) continue;
        const size_t len = megapdf_block_span_string(s, idx, si, nullptr, 0);
        weighted += sp.size_ratio * static_cast<double>(len);
        total_chars += static_cast<long long>(len);
    }
    // A block whose span text cannot be read back through the ABI (should not happen for a
    // real HEADING block, but this is a diagnostic reading the public surface, not the
    // internals) defaults to a ratio comfortably ABOVE kHeadingSizeRatioMirror, not below it --
    // misreading it as "size-based, not bold" undercounts the mechanism this tool measures;
    // misreading it as "bold" (the old default of 1.0 would have) overcounts it, which is the
    // direction that would make this PR's own corpus numbers look more dramatic than they are.
    return total_chars > 0 ? weighted / static_cast<double>(total_chars) : 999.0;
}

// See the file comment above: exact given the two current heading routes, not a heuristic
// guess -- a HEADING block reaches this branch only because GatherOneBlock's earlier
// size->=1.15x-body test already failed for it.
bool BlockIsBoldAtBodyHeading(const megapdf_structure* s, size_t idx) {
    return BlockAvgSizeRatio(s, idx) < kHeadingSizeRatioMirror;
}

struct HeadingDiagTotals {
    long long docs = 0, pages = 0;
    long long heading_size_based = 0;   // the size->=1.15x-body route -- not this issue's rule
    long long heading_bold_total = 0;   // every bold-at-body HEADING block, any run length

    // Run-length histogram: RUNS (one count per maximal back-to-back run) and BLOCKS (sum of
    // run length over every run in that bucket, i.e. how many HEADING blocks the bucket holds).
    long long runs_run1 = 0, runs_run2 = 0, runs_run3_9 = 0, runs_run10_49 = 0, runs_run50_plus = 0;
    long long blocks_run1 = 0, blocks_run2 = 0, blocks_run3_9 = 0, blocks_run10_49 = 0, blocks_run50_plus = 0;
    long long max_run_len = 0;
    long long pages_with_extreme_run = 0;   // >= 1 run of length >= 50 on the page (#375's "1,000+" document's shape)
    // A DIFFERENT, pre-existing defect this investigation surfaced but does not fix (out of
    // #375's scope: it degenerates the size->=1.15x-body route, not the bold-at-body one):
    // megapdf_structure_body_size() rounds to exactly 0 for a document whose modal
    // (character-count-weighted) font-size bucket is dominated by a near-zero reported size
    // (ComputeBodySize's own 0.5pt rounding, core/megapdf_structure.cpp) -- ordinarily
    // impossible to hit with real body text, but seen on a small minority of real corpus
    // documents (a hidden OCR text layer with a degenerate font-size/matrix combination is the
    // likely source, not confirmed here). With body_size == 0, LineQualifiesBySize's own
    // `line_size >= 1.15 * body_size` degenerates to `line_size >= 0`, true for essentially
    // every line on the page, AND megapdf_span::size_ratio is left at its zero default
    // (AssignSizeRatios's own `if (body_size <= 0) return;` guard) -- which is what an extreme
    // run's size_ratio_min/max both reading exactly 0 here means. Tracked so a persisting
    // extreme run is not mistaken for this fix's run/numeric-like suppression failing to fire:
    // a block on one of these documents is virtually always classified by the SIZE route, not
    // the bold-at-body one this fix targets, so DemoteFalsePositiveHeadingRuns correctly leaves
    // it alone.
    long long docs_zero_body_size = 0;
    long long extreme_runs_on_zero_body_size_docs = 0;

    // Proxies, tallied for isolated (run==1), run==2 (the boundary case) and clustered (run>=3,
    // the threshold the fix hypothesis below tests) heading blocks separately.
    long long isolated_total = 0, isolated_short = 0, isolated_numeric = 0;
    long long run2_total = 0, run2_short = 0, run2_numeric = 0;
    long long clustered_total = 0, clustered_short = 0, clustered_numeric = 0;

    // Column-census overlap (isolated vs. clustered only -- the contrast the issue's suggested
    // fix direction needs measured).
    long long isolated_on_multicol_page = 0, isolated_on_manycut_page = 0;
    long long clustered_on_multicol_page = 0, clustered_on_manycut_page = 0;
};

void RunHeadingDiagOnDoc(const std::string& pdf, HeadingDiagTotals* totals) {
    megapdf_document* doc = megapdf_open_file(pdf.c_str(), nullptr);
    if (doc == nullptr) return;
    const int pages = megapdf_page_count(doc);
    if (pages <= 0) { megapdf_close(doc); return; }
    // MEGAPDF_STRUCTURE_DEFAULT (0): furniture dropped, only non-empty fields kept -- the
    // ordinary consumer's own view (e.g. #357's Markdown export), which is what #375's
    // hand-check actually read.
    megapdf_structure* s = megapdf_structure_load(doc, 0, pages, MEGAPDF_STRUCTURE_DEFAULT, nullptr);
    if (s == nullptr) { megapdf_close(doc); return; }
    const double body_size = megapdf_structure_body_size(s);

    const size_t n_blocks = megapdf_block_count(s);
    std::vector<std::vector<std::pair<size_t, megapdf_block>>> by_page(static_cast<size_t>(pages));
    for (size_t i = 0; i < n_blocks; i++) {
        megapdf_block b{};
        if (megapdf_block_get(s, i, &b) != MEGAPDF_OK) continue;
        if (b.page < 0 || b.page >= pages) continue;
        by_page[static_cast<size_t>(b.page)].push_back({i, b});
    }

    totals->docs++;
    // See docs_zero_body_size's comment: with body_size <= 0, megapdf_span::size_ratio is left
    // at its zero default for every span (AssignSizeRatios's own guard), so
    // BlockIsBoldAtBodyHeading's re-derivation cannot tell the two heading routes apart on this
    // document at all -- everything would misread as "bold". Such a document is excluded from
    // the bold/run measurement entirely (kept out of heading_bold_total, the run histogram and
    // the proxies) rather than silently polluting them; `extreme_runs_on_zero_body_size_docs`
    // separately tallies its own (kind == HEADING, any route) run lengths so the residual is
    // still explained, not just hidden.
    const bool degenerate_body_size = body_size <= 0;
    if (degenerate_body_size) totals->docs_zero_body_size++;
    for (int p = 0; p < pages; p++) {
        const auto& page_blocks_all = by_page[static_cast<size_t>(p)];
        totals->pages++;

        if (degenerate_body_size) {
            size_t run = 0;
            for (const auto& pr : page_blocks_all) {
                if (pr.second.kind == MEGAPDF_BLOCK_HEADING) {
                    run++;
                } else if (pr.second.kind == MEGAPDF_BLOCK_PARAGRAPH || pr.second.kind == MEGAPDF_BLOCK_LIST_ITEM) {
                    if (run >= 50) totals->extreme_runs_on_zero_body_size_docs++;
                    run = 0;
                }
            }
            if (run >= 50) totals->extreme_runs_on_zero_body_size_docs++;
            continue;
        }

        // The same page-level census `check`/`census` already compute, over the same block set
        // (ColumnCensusForPage filters to HEADING/PARAGRAPH/LIST_ITEM/TABLE_ROW itself).
        std::vector<megapdf_block> plain;
        plain.reserve(page_blocks_all.size());
        for (const auto& pr : page_blocks_all) plain.push_back(pr.second);
        const ColumnCensus cc = ColumnCensusForPage(plain, body_size);

        // The content stream GatherOneBlock itself builds a page from, before figures/fields/
        // furniture are spliced in: HEADING/PARAGRAPH/LIST_ITEM only, in reading order.
        struct Item {
            size_t idx;
            bool is_heading;
            bool is_bold;
        };
        std::vector<Item> stream;
        stream.reserve(page_blocks_all.size());
        for (const auto& pr : page_blocks_all) {
            const megapdf_block& b = pr.second;
            if (b.kind != MEGAPDF_BLOCK_HEADING && b.kind != MEGAPDF_BLOCK_PARAGRAPH &&
                b.kind != MEGAPDF_BLOCK_LIST_ITEM) {
                continue;
            }
            Item it;
            it.idx = pr.first;
            it.is_heading = b.kind == MEGAPDF_BLOCK_HEADING;
            it.is_bold = it.is_heading && BlockIsBoldAtBodyHeading(s, pr.first);
            if (it.is_heading && !it.is_bold) totals->heading_size_based++;
            stream.push_back(it);
        }

        bool page_has_extreme_run = false;
        for (size_t k = 0; k < stream.size();) {
            if (!stream[k].is_bold) { k++; continue; }
            size_t j = k;
            while (j < stream.size() && stream[j].is_bold) j++;
            const size_t run_len = j - k;
            totals->heading_bold_total += static_cast<long long>(run_len);
            totals->max_run_len = (std::max)(totals->max_run_len, static_cast<long long>(run_len));
            if (run_len >= 50) page_has_extreme_run = true;

            long long* runs_bucket;
            long long* blocks_bucket;
            if (run_len == 1) { runs_bucket = &totals->runs_run1; blocks_bucket = &totals->blocks_run1; }
            else if (run_len == 2) { runs_bucket = &totals->runs_run2; blocks_bucket = &totals->blocks_run2; }
            else if (run_len <= 9) { runs_bucket = &totals->runs_run3_9; blocks_bucket = &totals->blocks_run3_9; }
            else if (run_len <= 49) { runs_bucket = &totals->runs_run10_49; blocks_bucket = &totals->blocks_run10_49; }
            else { runs_bucket = &totals->runs_run50_plus; blocks_bucket = &totals->blocks_run50_plus; }
            (*runs_bucket)++;
            (*blocks_bucket) += static_cast<long long>(run_len);

            const bool isolated = run_len == 1;
            const bool clustered = run_len >= 3;
            for (size_t m = k; m < j; m++) {
                const size_t bidx = stream[m].idx;
                const std::vector<unsigned short> text16 = BlockString(s, bidx, MEGAPDF_BLOCK_TEXT);
                const std::vector<unsigned int> cps = Utf16ToCodepoints(text16);
                bool saw_digit = false, numeric_like = true;
                for (unsigned int c : cps) {
                    if (IsAsciiDigitLocal(c)) { saw_digit = true; continue; }
                    if (!IsNumericLikePunctuationLocal(c)) { numeric_like = false; break; }
                }
                numeric_like = numeric_like && saw_digit;
                const bool is_short = cps.size() <= static_cast<size_t>(kShortTextChars);
                if (isolated) {
                    totals->isolated_total++;
                    if (is_short) totals->isolated_short++;
                    if (numeric_like) totals->isolated_numeric++;
                    if (cc.multi_column) totals->isolated_on_multicol_page++;
                    if (cc.many_cut) totals->isolated_on_manycut_page++;
                } else if (clustered) {
                    totals->clustered_total++;
                    if (is_short) totals->clustered_short++;
                    if (numeric_like) totals->clustered_numeric++;
                    if (cc.multi_column) totals->clustered_on_multicol_page++;
                    if (cc.many_cut) totals->clustered_on_manycut_page++;
                } else {
                    totals->run2_total++;
                    if (is_short) totals->run2_short++;
                    if (numeric_like) totals->run2_numeric++;
                }
            }
            k = j;
        }
        if (page_has_extreme_run) totals->pages_with_extreme_run++;
    }
    megapdf_structure_free(s);
    megapdf_close(doc);
}

int RunHeadingDiag(const std::vector<std::string>& pdfs) {
    HeadingDiagTotals t;
    for (const auto& pdf : pdfs) RunHeadingDiagOnDoc(pdf, &t);
    std::printf("headingdiag docs=%lld pages=%lld\n", t.docs, t.pages);
    std::printf("heading_size_based=%lld heading_bold_total=%lld max_run_len=%lld pages_with_extreme_run(>=50)=%lld\n",
                t.heading_size_based, t.heading_bold_total, t.max_run_len, t.pages_with_extreme_run);
    std::printf("runs_by_length 1=%lld 2=%lld 3-9=%lld 10-49=%lld 50+=%lld\n", t.runs_run1, t.runs_run2,
                t.runs_run3_9, t.runs_run10_49, t.runs_run50_plus);
    std::printf("bold_heading_blocks_by_run_length 1=%lld 2=%lld 3-9=%lld 10-49=%lld 50+=%lld\n", t.blocks_run1,
                t.blocks_run2, t.blocks_run3_9, t.blocks_run10_49, t.blocks_run50_plus);
    std::printf("isolated(run=1) total=%lld short=%lld numeric_like=%lld on_multicol_page=%lld on_manycut_page=%lld\n",
                t.isolated_total, t.isolated_short, t.isolated_numeric, t.isolated_on_multicol_page,
                t.isolated_on_manycut_page);
    std::printf("run2 total=%lld short=%lld numeric_like=%lld\n", t.run2_total, t.run2_short, t.run2_numeric);
    std::printf("clustered(run>=3) total=%lld short=%lld numeric_like=%lld on_multicol_page=%lld on_manycut_page=%lld\n",
                t.clustered_total, t.clustered_short, t.clustered_numeric, t.clustered_on_multicol_page,
                t.clustered_on_manycut_page);
    std::printf("docs_zero_body_size=%lld extreme_runs_on_zero_body_size_docs=%lld (excluded from every number "
                "above -- see HeadingDiagTotals's own comment)\n",
                t.docs_zero_body_size, t.extreme_runs_on_zero_body_size_docs);
    return 0;
}

// ---------------------------------------------------------------------------
// bodysizediag mode (#382 investigation only -- not part of the #354 battery/gate).
//
// #382: megapdf_structure_body_size() rounds to exactly 0 on a small minority of corpus
// documents, degenerating the size-based heading rule (headingdiag's docs_zero_body_size
// above found 17 of 4,263). This mode characterizes, numbers only, WHAT those sub-half-point
// reported font sizes are, so ComputeBodySize's floor (megapdf_structure.cpp's
// kBodySizeFloorPt) is a measured value and not a guess:
//
//   - the reported size is FPDFText_GetFontSize -- the raw Tf operand, which is what
//     megapdf_structure.cpp's Char::font_size is -- and its "drawn" size is that times the
//     text matrix's scale (sqrt|det| of FPDFText_GetMatrix's a/b/c/d): a `/F 0.01 Tf` run
//     scaled up through Tm reports 0.01 and draws at something ordinary. If the sub-floor
//     population's drawn sizes sit in the normal 6-14 pt band, the "tiny Tf, big Tm" shape
//     is confirmed as the cause.
//   - the text render mode of the sub-floor characters' objects (FPDFTextObj_GetTextRenderMode:
//     3 / 7 are invisible) tests the hidden-OCR-layer hypothesis directly.
//   - a fine histogram of every reported size under 2 pt, corpus-wide, shows whether any real
//     body text lives between the degenerate population and 1 pt -- i.e. whether a 1 pt floor
//     can ever demote a genuine body size.
//   - modal_old / modal_new: the character-weighted modal 0.5 pt bucket over the document's
//     raw characters (a proxy for ComputeBodySize, which votes over non-furniture lines --
//     close enough for this question) with the pre-#382 `size <= 0` filter and with the floor.
//
// One aggregate report over every document given (the corpus battery's own privacy rule:
// counts and histograms, never a name or any text).
// ---------------------------------------------------------------------------
struct BodySizeDiagTotals {
    long long docs = 0, docs_opened = 0, chars = 0;
    long long docs_modal_old_zero = 0;       // the #382 population: modal bucket rounds to 0
    long long docs_modal_old_half = 0;       // modal bucket 0.5 (would ALSO be caught by a 1 pt floor)
    long long docs_nothing_above_floor = 0;  // every character under the floor: falls back to the 12 pt default
    long long docs_modal_changed = 0;        // modal_new != modal_old
    // Reported-size histogram under 2 pt (corpus-wide characters).
    long long sz_lt_0_05 = 0, sz_0_05_0_25 = 0, sz_0_25_0_5 = 0, sz_0_5_1 = 0, sz_1_2 = 0, sz_ge_2 = 0;
    // Sub-0.25 characters: drawn size (reported x matrix scale) histogram and render mode.
    long long tiny_drawn_lt_4 = 0, tiny_drawn_4_6 = 0, tiny_drawn_6_14 = 0, tiny_drawn_14_30 = 0, tiny_drawn_ge_30 = 0;
    long long tiny_invisible = 0, tiny_visible = 0, tiny_no_object = 0;
    // The core's own answer on the #382 population: HEADING blocks it produces on those
    // documents (before the fix: virtually every line; after: the genuine ones).
    long long zero_docs_heading_blocks = 0, zero_docs_blocks = 0, zero_docs_pages_extreme_run = 0;
};

void RunBodySizeDiagOnDoc(const std::string& pdf, BodySizeDiagTotals* t) {
    constexpr double kFloorMirror = 1.0;   // mirrors megapdf_structure.cpp's kBodySizeFloorPt
    t->docs++;
    // Through the core first (as every other mode does): megapdf_open_file initialises PDFium,
    // which the raw FPDF_LoadDocument below needs; the core handle also serves the
    // structure-load check at the end.
    megapdf_document* doc = megapdf_open_file(pdf.c_str(), nullptr);
    if (doc == nullptr) return;
    FPDF_DOCUMENT raw = FPDF_LoadDocument(pdf.c_str(), nullptr);
    if (raw == nullptr) { megapdf_close(doc); return; }
    t->docs_opened++;
    std::map<double, long long> counts_old, counts_new;
    const int pages = FPDF_GetPageCount(raw);
    for (int p = 0; p < pages; p++) {
        FPDF_PAGE page = FPDF_LoadPage(raw, p);
        if (page == nullptr) continue;
        FPDF_TEXTPAGE tp = FPDFText_LoadPage(page);
        if (tp != nullptr) {
            const int n = FPDFText_CountChars(tp);
            for (int i = 0; i < n; i++) {
                if (FPDFText_IsGenerated(tp, i) == 1) continue;
                const unsigned int u = FPDFText_GetUnicode(tp, i);
                if (u == 0 || IsWhitespaceCpLocal(u)) continue;
                const double size = FPDFText_GetFontSize(tp, i);
                t->chars++;
                if (size < 0.05) t->sz_lt_0_05++;
                else if (size < 0.25) t->sz_0_05_0_25++;
                else if (size < 0.5) t->sz_0_25_0_5++;
                else if (size < 1.0) t->sz_0_5_1++;
                else if (size < 2.0) t->sz_1_2++;
                else t->sz_ge_2++;
                if (size > 0) counts_old[std::round(size * 2.0) / 2.0]++;
                if (size >= kFloorMirror) counts_new[std::round(size * 2.0) / 2.0]++;
                if (size < 0.25) {
                    FS_MATRIX m{1, 0, 0, 1, 0, 0};
                    double scale = 1.0;
                    if (FPDFText_GetMatrix(tp, i, &m)) scale = std::sqrt(std::fabs(m.a * m.d - m.b * m.c));
                    const double drawn = size * scale;
                    if (drawn < 4) t->tiny_drawn_lt_4++;
                    else if (drawn < 6) t->tiny_drawn_4_6++;
                    else if (drawn < 14) t->tiny_drawn_6_14++;
                    else if (drawn < 30) t->tiny_drawn_14_30++;
                    else t->tiny_drawn_ge_30++;
                    FPDF_PAGEOBJECT obj = FPDFText_GetTextObject(tp, i);
                    if (obj == nullptr) {
                        t->tiny_no_object++;
                    } else {
                        const int mode = static_cast<int>(FPDFTextObj_GetTextRenderMode(obj));
                        if (mode == 3 || mode == 7) t->tiny_invisible++; else t->tiny_visible++;
                    }
                }
            }
            FPDFText_ClosePage(tp);
        }
        FPDF_ClosePage(page);
    }
    auto modal = [](const std::map<double, long long>& counts, double dflt) {
        double best = dflt;
        long long best_count = -1;
        for (const auto& e : counts) if (e.second > best_count) { best_count = e.second; best = e.first; }
        return best;
    };
    const double modal_old = modal(counts_old, 12.0);
    const double modal_new = modal(counts_new, 12.0);
    if (modal_old == 0.0) t->docs_modal_old_zero++;
    if (modal_old == 0.5) t->docs_modal_old_half++;
    if (counts_new.empty() && !counts_old.empty()) t->docs_nothing_above_floor++;
    if (modal_old != modal_new) t->docs_modal_changed++;
    FPDF_CloseDocument(raw);

    // The core's own answer on the #382 population, through the linked build (before or after).
    if (modal_old == 0.0) {
        const int n_pages = megapdf_page_count(doc);
        megapdf_structure* s = n_pages > 0 ? megapdf_structure_load(doc, 0, n_pages, 0, nullptr) : nullptr;
        if (s != nullptr) {
            const size_t n = megapdf_block_count(s);
            std::map<int, long long> run_by_page;
            long long run = 0;
            int run_page = -1;
            for (size_t i = 0; i < n; i++) {
                megapdf_block b{};
                if (megapdf_block_get(s, i, &b) != MEGAPDF_OK) continue;
                t->zero_docs_blocks++;
                if (b.kind == MEGAPDF_BLOCK_HEADING) {
                    t->zero_docs_heading_blocks++;
                    if (b.page == run_page) run++; else { run = 1; run_page = b.page; }
                    if (run >= 50 && run_by_page[b.page] == 0) run_by_page[b.page] = 1;
                } else {
                    run = 0;
                    run_page = -1;
                }
            }
            t->zero_docs_pages_extreme_run += static_cast<long long>(run_by_page.size());
            megapdf_structure_free(s);
        }
    }
    megapdf_close(doc);
}

int RunBodySizeDiag(const std::vector<std::string>& pdfs) {
    BodySizeDiagTotals t;
    for (const auto& pdf : pdfs) RunBodySizeDiagOnDoc(pdf, &t);
    std::printf("bodysizediag docs=%lld opened=%lld chars=%lld\n", t.docs, t.docs_opened, t.chars);
    std::printf("docs modal_old_zero=%lld modal_old_half=%lld nothing_above_floor=%lld modal_changed_by_floor=%lld\n",
                t.docs_modal_old_zero, t.docs_modal_old_half, t.docs_nothing_above_floor, t.docs_modal_changed);
    std::printf("reported_size_hist lt0.05=%lld 0.05-0.25=%lld 0.25-0.5=%lld 0.5-1=%lld 1-2=%lld ge2=%lld\n",
                t.sz_lt_0_05, t.sz_0_05_0_25, t.sz_0_25_0_5, t.sz_0_5_1, t.sz_1_2, t.sz_ge_2);
    std::printf("sub0.25_drawn_size_hist lt4=%lld 4-6=%lld 6-14=%lld 14-30=%lld ge30=%lld\n", t.tiny_drawn_lt_4,
                t.tiny_drawn_4_6, t.tiny_drawn_6_14, t.tiny_drawn_14_30, t.tiny_drawn_ge_30);
    std::printf("sub0.25_render_mode invisible=%lld visible=%lld no_object=%lld\n", t.tiny_invisible, t.tiny_visible,
                t.tiny_no_object);
    std::printf("zero_body_docs_via_core blocks=%lld heading_blocks=%lld pages_with_heading_run_ge50=%lld\n",
                t.zero_docs_blocks, t.zero_docs_heading_blocks, t.zero_docs_pages_extreme_run);
    return 0;
}

// ---------------------------------------------------------------------------
// garbage mode (#385 investigation only -- not part of the #354 battery/gate).
//
// #385: a hand-found document extracts to unrecognizable glyph garbage on part of a page
// (runs like `l"`, `qQ`), suspected cause a broken/symbolic embedded font with no usable
// ToUnicode CMap. fid_low09 (measure 1's per-page F1 < 0.9 gate, #354) is the closest existing
// signal, but it mixes this failure mode in with ordinary hyphenation/spacing noise
// (#360/#362-#365/#375) -- all of those are token-COUNT disagreements between the heuristic
// and FPDFText_GetText; none of them asks whether either side's CHARACTERS are actually right.
// This mode scores two independent, numbers-only signals on fid_low09 pages to see which (if
// either) actually tracks character-level garbage rather than ordinary structural noise:
//
//   1. PDFium's own per-character signal, FPDFText_GetUnicode alongside FPDFText_HasUnicodeMapError
//      (an experimental API already present in the pinned PDFium build -- not a MegaPDF patch,
//      see fpdf_text.h). Set per real (non-generated, non-whitespace) character; `maperr`/
//      `chars` below is the fraction of a page's characters PDFium itself flags as having an
//      invalid Unicode mapping -- the most direct possible confirmation of #385's "no usable
//      ToUnicode map" theory, straight from the source rather than guessed from output text.
//   2. A wordlikeness heuristic on the RAW (FPDFText_GetUnicode) token stream -- the side of
//      measure 1's comparison that would actually carry character-level corruption, since it
//      reads PDFium's own text, not the structure heuristic's reconstruction. Using this
//      file's usual IsWordCodepoint token runs: an all-digit token is "numeric" (an invoice is
//      full of real numbers -- not a garbage signal on its own); a single-letter token is
//      "single" (a real initial, bullet or abbreviation -- ambiguous either way, not scored).
//      Everything else is "alpha" and scored wordlike/not: does it contain at least one vowel
//      (English+French, accents folded to their base letter for this test) and no run of more
//      than kMaxConsonantRun consecutive non-vowel letters? (5, not 4: real English words have
//      5-consonant runs -- "strengths" ends n-g-t-h-s -- so 4 flagged too many real words in a
//      quick sanity check against ordinary prose before this mode was used on the corpus.)
//      `known` is a stricter, lower-recall companion: an exact match (again accents folded)
//      against a small hardcoded English+French common-word list.
//
// A sanity check against a synthetic PDF (a font with a /Differences encoding naming glyphs
// that resolve to no Unicode value, no ToUnicode CMap) found FPDFText_HasUnicodeMapError firing
// on 100% of that document's characters -- and measure 1's F1 STILL scored 1.0 (not low09):
// the structure heuristic reads characters through the same FPDFText_GetUnicode PDFium itself
// reads, so when a font is wrong, heuristic and raw agree on the SAME wrong text and measure 1
// sees no disagreement at all. That means fid_low09 can miss this failure mode entirely, not
// just mix it with noise -- so this mode scores BOTH populations: every fid_low09 page (the
// population #385 was found through), and every page regardless of fid_low09 (the only way to
// measure the failure mode's real corpus-wide prevalence, since a page can have it without ever
// being fid_low09).
//
// One line per document actually opened:
//   result=ok pages=<n> low09=<n> ctrl_chars=.. ctrl_maperr=.. ctrl_tokens=.. ctrl_alpha=..
//             ctrl_wordlike=.. ctrl_known=.. ctrl_numeric=.. ctrl_single=..
//   -- "ctrl" sums both signals over every NON-fid_low09 page on the document (the population
//   measure 1 already treats as fine) -- the baseline / false-positive-rate context the
//   fid_low09 population is compared against.
// One line per fid_low09 page:
//   low09page [id=<id> page=<n>] chars=.. maperr=.. tokens=.. alpha=.. wordlike=.. known=..
//             numeric=.. single=..
//   -- id/page are only printed when --dump-id is given (a hand-labeling run over a handful of
//   documents); a full-corpus pass gives neither, since a bare page index identifies nothing
//   on its own and this tool prints nothing that identifies a document (#151/#173/#354).
// One line per page, low09 or not -- the corpus-wide prevalence measure, per the sanity check
// above (a real-corpus check further down found the same false-positive shape by hand: a
// maperr majority on a page that reads perfectly once the actual fallback-decoded characters
// were read -- so maperr alone over-reports; wordlikeness needs the same all-pages coverage to
// be trustworthy at corpus scale, not just the fid_low09 slice):
//   page [id=<id> page=<n>] low09=0|1 chars=<c> maperr=<m> tokens=<t> alpha=<a> wordlike=<w>
//        known=<k> numeric=<n> single=<s>
//   -- id/page (like low09page's) are only printed when --dump-id is given.
//
// --dump-garbage <dir> --dump-id <id>: writes a page's raw PDFium text (the exact GetUnicode
// stream the signals above are scored from) to <dir>/<id>-p<n>.txt, PRIVATE-marked exactly like
// check mode's --dump, for every page "worth" hand-labeling: fid_low09, OR wordlikeness < 0.7
// with >= 5 alpha tokens (a genuine-garbage candidate even without fid_low09 -- see the sanity
// check above), OR a map-error rate > 0.2 (a candidate despite the false-positive risk, kept
// for completeness). Meant for a small labeled sample, never run over the full corpus.
// ---------------------------------------------------------------------------
struct RawTextSignals {
    long long chars = 0, maperr = 0;
    long long tokens = 0, alpha = 0, wordlike = 0, known = 0, numeric = 0, single = 0;
};

bool IsAllDigitsToken(const Token& t) {
    if (t.empty()) return false;
    for (char32_t c : t) {
        if (!(c >= '0' && c <= '9')) return false;
    }
    return true;
}

// Latin-1 Supplement / Latin Extended-A upper -> lower, ASCII upper -> lower. Approximate
// (Latin Extended-A's even/odd upper/lower pairing has a handful of irregular spots this does
// not special-case), but this file's own IsWordCodepoint already restricts tokens to exactly
// ASCII + Latin-1 Supplement + Latin Extended-A, so it covers every codepoint a token can
// actually contain.
char32_t ToLowerLatin(char32_t c) {
    if (c >= 'A' && c <= 'Z') return c + 32;
    if (c >= 0xC0 && c <= 0xDE && c != 0xD7) return c + 0x20;  // À-Þ (not ×) -> à-þ
    if (c >= 0x100 && c <= 0x177 && (c % 2) == 0) return c + 1;  // Ā/ā .. Ŵ/ŵ pairs
    return c;
}

// Folds an accented vowel/consonant to its plain ASCII base letter, for the known-word test
// only (the word list itself is plain ASCII to avoid any source-encoding ambiguity, see the
// CommonWordSet() comment).
char32_t StripAccent(char32_t c) {
    switch (c) {
        case 0xE0: case 0xE1: case 0xE2: case 0xE3: case 0xE4: case 0xE5: return U'a';  // à á â ã ä å
        case 0xE8: case 0xE9: case 0xEA: case 0xEB: return U'e';                        // è é ê ë
        case 0xEC: case 0xED: case 0xEE: case 0xEF: return U'i';                        // ì í î ï
        case 0xF2: case 0xF3: case 0xF4: case 0xF5: case 0xF6: return U'o';             // ò ó ô õ ö
        case 0xF9: case 0xFA: case 0xFB: case 0xFC: return U'u';                        // ù ú û ü
        case 0xFD: case 0xFF: return U'y';                                              // ý ÿ
        case 0xE7: return U'c';                                                         // ç
        case 0xF1: return U'n';                                                         // ñ
        default: return c;
    }
}

bool IsVowelish(char32_t lower) {
    switch (lower) {
        case U'a': case U'e': case U'i': case U'o': case U'u': case U'y':
        case 0xE0: case 0xE1: case 0xE2: case 0xE3: case 0xE4: case 0xE5:  // à á â ã ä å
        case 0xE8: case 0xE9: case 0xEA: case 0xEB:                       // è é ê ë
        case 0xEC: case 0xED: case 0xEE: case 0xEF:                       // ì í î ï
        case 0xF2: case 0xF3: case 0xF4: case 0xF5: case 0xF6:            // ò ó ô õ ö
        case 0xF9: case 0xFA: case 0xFB: case 0xFC:                       // ù ú û ü
        case 0xFD: case 0xFF:                                             // ý ÿ
        case 0x153: case 0xE6:                                            // œ æ
            return true;
        default:
            return false;
    }
}

constexpr size_t kMaxConsonantRun = 5;

bool IsWordlikeAlpha(const Token& t) {
    bool has_vowel = false;
    size_t run = 0, max_run = 0;
    for (char32_t c : t) {
        const char32_t lower = ToLowerLatin(c);
        if (IsVowelish(lower)) {
            has_vowel = true;
            run = 0;
        } else {
            run++;
            max_run = (std::max)(max_run, run);
        }
    }
    return has_vowel && max_run <= kMaxConsonantRun;
}

// A small, hardcoded English+French common-word list -- function words plus a handful of
// invoice/document vocabulary, since #385's own example is an invoice. Deliberately plain
// ASCII (no accented literals in source): IsKnownWord() folds each document token's accents
// off before comparing, so "être"/"numéro" in a real document still match "etre"/"numero"
// here. Not meant to be exhaustive -- this is `known`, the stricter/lower-recall companion to
// the vowel/consonant-run wordlikeness test above, not the primary signal.
const std::unordered_set<Token>& CommonWordSet() {
    static const std::unordered_set<Token> words = [] {
        std::unordered_set<Token> s;
        static const char* const kWords[] = {
            "the", "of", "and", "a", "to", "in", "is", "you", "that", "it", "he", "was", "for", "on", "are",
            "as", "with", "his", "they", "at", "be", "this", "have", "from", "or", "one", "had", "by", "not",
            "what", "all", "were", "we", "when", "your", "can", "there", "use", "an", "each", "which", "she",
            "do", "how", "their", "if", "will", "up", "other", "about", "out", "many", "then", "them", "these",
            "so", "some", "her", "would", "make", "like", "him", "into", "time", "has", "look", "two", "more",
            "write", "go", "see", "number", "no", "way", "could", "people", "my", "than", "first", "water",
            "been", "call", "who", "its", "now", "find", "long", "down", "day", "did", "get", "come", "made",
            "may", "part", "over", "new", "sound", "take", "only", "little", "work", "know", "place", "year",
            "live", "me", "back", "give", "most", "very", "after", "thing", "name", "good", "man", "think",
            "say", "great", "where", "help", "through", "much", "before", "line", "right", "too", "mean",
            "old", "any", "same", "tell", "boy", "follow", "came", "want", "show", "also", "around", "form",
            "three", "small", "set", "put", "end", "does", "another", "well", "large", "must", "big", "even",
            "such", "because", "turn", "here", "why", "ask", "went", "men", "read", "need", "land", "home",
            "us", "move", "try", "kind", "hand", "again", "change", "off", "play", "air", "away", "house",
            "point", "page", "letter", "mother", "answer", "found", "study", "still", "learn", "should",
            "world", "invoice", "total", "amount", "payment", "date", "address", "company", "account", "order",
            "due", "tax", "subtotal", "description", "quantity", "price", "email", "phone", "thank", "please",
            "le", "la", "les", "de", "des", "et", "un", "une", "du", "que", "qui", "dans", "pour", "sur",
            "avec", "par", "est", "sont", "au", "aux", "ce", "cette", "ces", "ne", "pas", "plus", "ou", "mais",
            "comme", "il", "elle", "nous", "vous", "ils", "elles", "je", "tu", "son", "sa", "ses", "leur",
            "leurs", "etre", "avoir", "fait", "faire", "tout", "tous", "toute", "toutes", "facture", "montant",
            "paiement", "nom", "adresse", "numero", "societe", "compte", "merci", "cordialement", "monsieur",
            "madame", "bonjour", "veuillez", "trouver", "svp", "merci",
        };
        for (const char* w : kWords) {
            Token t;
            for (const char* p = w; *p != '\0'; p++) t.push_back(static_cast<char32_t>(*p));
            s.insert(t);
        }
        return s;
    }();
    return words;
}

bool IsKnownWord(const Token& t) {
    Token folded;
    folded.reserve(t.size());
    for (char32_t c : t) folded.push_back(StripAccent(ToLowerLatin(c)));
    return CommonWordSet().count(folded) > 0;
}

void AppendUtf8(std::string* out, unsigned int cp) {
    if (cp < 0x80) {
        out->push_back(static_cast<char>(cp));
    } else if (cp < 0x800) {
        out->push_back(static_cast<char>(0xC0 | (cp >> 6)));
        out->push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    } else if (cp < 0x10000) {
        out->push_back(static_cast<char>(0xE0 | (cp >> 12)));
        out->push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
        out->push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    } else {
        out->push_back(static_cast<char>(0xF0 | (cp >> 18)));
        out->push_back(static_cast<char>(0x80 | ((cp >> 12) & 0x3F)));
        out->push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
        out->push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    }
}

RawTextSignals ScoreRawText(FPDF_TEXTPAGE tp, int char_count) {
    RawTextSignals sig;
    std::vector<unsigned int> cps;
    cps.reserve(static_cast<size_t>((std::max)(char_count, 0)));
    for (int i = 0; i < char_count; i++) {
        if (FPDFText_IsGenerated(tp, i) == 1) continue;
        const unsigned int cp = FPDFText_GetUnicode(tp, i);
        if (cp == 0) continue;
        if (!IsWhitespaceCpLocal(cp)) {
            sig.chars++;
            if (FPDFText_HasUnicodeMapError(tp, i) == 1) sig.maperr++;
        }
        cps.push_back(cp);
    }
    const std::vector<Token> tokens = Tokenize(cps);
    sig.tokens = static_cast<long long>(tokens.size());
    for (const Token& t : tokens) {
        if (t.size() == 1) { sig.single++; continue; }
        if (IsAllDigitsToken(t)) { sig.numeric++; continue; }
        sig.alpha++;
        if (IsWordlikeAlpha(t)) sig.wordlike++;
        if (IsKnownWord(t)) sig.known++;
    }
    return sig;
}

void RunGarbageOnDoc(const std::string& pdf, const std::string& dump_dir, const std::string& dump_id) {
    megapdf_document* doc = megapdf_open_file(pdf.c_str(), nullptr);
    if (doc == nullptr) {
        std::printf("result=%s\n", OpenOutcome(megapdf_last_error()));
        return;
    }
    const int pages = megapdf_page_count(doc);
    if (pages <= 0) {
        std::printf("result=format pages=0\n");
        megapdf_close(doc);
        return;
    }
    megapdf_structure* s = megapdf_structure_load(doc, 0, pages, MEGAPDF_STRUCTURE_KEEP_FURNITURE |
                                                                       MEGAPDF_STRUCTURE_ALL_FIELDS, nullptr);
    if (s == nullptr) {
        std::printf("result=format pages=%d\n", pages);
        megapdf_close(doc);
        return;
    }

    const size_t n_blocks = megapdf_block_count(s);
    std::vector<std::vector<Token>> tokens_by_page(static_cast<size_t>(pages));
    for (size_t i = 0; i < n_blocks; i++) {
        megapdf_block b{};
        if (megapdf_block_get(s, i, &b) != MEGAPDF_OK) continue;
        if (b.page < 0 || b.page >= pages) continue;
        if (b.kind == MEGAPDF_BLOCK_FIELD) continue;  // measure 1 excludes FIELD blocks
        const std::vector<unsigned short> text16 = BlockContentString(s, i, b.kind);
        const std::vector<Token> toks = Tokenize(Utf16ToCodepoints(text16));
        tokens_by_page[static_cast<size_t>(b.page)].insert(tokens_by_page[static_cast<size_t>(b.page)].end(),
                                                            toks.begin(), toks.end());
    }

    FPDF_DOCUMENT raw = FPDF_LoadDocument(pdf.c_str(), nullptr);
    int low09_count = 0;
    RawTextSignals ctrl;
    std::vector<std::pair<int, RawTextSignals>> low09_pages;

    const bool dumping = !dump_dir.empty() && !dump_id.empty();
    if (dumping) {
        const std::string marker = dump_dir + "/PRIVATE";
        std::ifstream check_marker(marker);
        if (!check_marker.good()) {
            std::ofstream m(marker);
            m << "Extracted document text (#385 investigation). Not committed, uploaded or pasted "
                 "anywhere: these are Dave's own documents. Delete this directory when you are done reading it.\n";
        }
    }

    for (int p = 0; p < pages; p++) {
        FPDF_PAGE raw_page = raw != nullptr ? FPDF_LoadPage(raw, p) : nullptr;
        FPDF_TEXTPAGE tp = raw_page != nullptr ? FPDFText_LoadPage(raw_page) : nullptr;
        if (tp != nullptr) {
            const int chars = FPDFText_CountChars(tp);
            const std::vector<Token> pdfium_tokens = Tokenize(JoinLineWrapHyphens(tp, chars));
            const FidelityCounts fc = MultisetF1(tokens_by_page[static_cast<size_t>(p)], pdfium_tokens);
            const bool low09 = F1(fc) < 0.9;
            const RawTextSignals sig = ScoreRawText(tp, chars);
            // Corpus-wide prevalence measure: every page, not just fid_low09 -- see this mode's
            // header comment for why fid_low09 alone can miss this failure mode entirely. id/page
            // are only printed when --dump-id is given (a hand-labeling run), same rule as
            // low09page below.
            if (dumping) {
                std::printf("page id=%s page=%d low09=%d chars=%lld maperr=%lld tokens=%lld alpha=%lld "
                            "wordlike=%lld known=%lld numeric=%lld single=%lld\n",
                            dump_id.c_str(), p, low09 ? 1 : 0, sig.chars, sig.maperr, sig.tokens, sig.alpha,
                            sig.wordlike, sig.known, sig.numeric, sig.single);
            } else {
                std::printf("page low09=%d chars=%lld maperr=%lld tokens=%lld alpha=%lld wordlike=%lld "
                            "known=%lld numeric=%lld single=%lld\n",
                            low09 ? 1 : 0, sig.chars, sig.maperr, sig.tokens, sig.alpha, sig.wordlike, sig.known,
                            sig.numeric, sig.single);
            }
            // A page is worth dumping (for hand-labeling) when it's fid_low09, OR its wordlikeness
            // is low enough to be a genuine-garbage candidate on its own (#385's own document may
            // not even be fid_low09 -- see this mode's header comment) OR its map-error rate is
            // high enough to be a candidate despite the false-positive risk seen in the CIBC-
            // statement-shaped sanity check.
            const double wrate = sig.alpha > 0 ? static_cast<double>(sig.wordlike) / static_cast<double>(sig.alpha)
                                                : 1.0;
            const double mrate = sig.chars > 0 ? static_cast<double>(sig.maperr) / static_cast<double>(sig.chars)
                                                : 0.0;
            const bool worth_dumping = low09 || (sig.alpha >= 5 && wrate < 0.7) || (sig.chars >= 20 && mrate > 0.2);
            if (dumping && worth_dumping) {
                std::string text;
                for (int i = 0; i < chars; i++) {
                    if (FPDFText_IsGenerated(tp, i) == 1) { text += '\n'; continue; }
                    const unsigned int cp = FPDFText_GetUnicode(tp, i);
                    if (cp == 0) continue;
                    AppendUtf8(&text, cp);
                }
                std::ofstream out(dump_dir + "/" + dump_id + "-p" + std::to_string(p) + ".txt", std::ios::binary);
                out << text;
            }
            if (low09) {
                low09_count++;
                low09_pages.push_back({p, sig});
            } else {
                ctrl.chars += sig.chars;
                ctrl.maperr += sig.maperr;
                ctrl.tokens += sig.tokens;
                ctrl.alpha += sig.alpha;
                ctrl.wordlike += sig.wordlike;
                ctrl.known += sig.known;
                ctrl.numeric += sig.numeric;
                ctrl.single += sig.single;
            }
            FPDFText_ClosePage(tp);
        }
        if (raw_page != nullptr) FPDF_ClosePage(raw_page);
    }
    if (raw != nullptr) FPDF_CloseDocument(raw);
    megapdf_structure_free(s);
    megapdf_close(doc);

    std::printf("result=ok pages=%d low09=%d ctrl_chars=%lld ctrl_maperr=%lld ctrl_tokens=%lld "
                "ctrl_alpha=%lld ctrl_wordlike=%lld ctrl_known=%lld ctrl_numeric=%lld ctrl_single=%lld\n",
                pages, low09_count, ctrl.chars, ctrl.maperr, ctrl.tokens, ctrl.alpha, ctrl.wordlike, ctrl.known,
                ctrl.numeric, ctrl.single);

    for (size_t k = 0; k < low09_pages.size(); k++) {
        const int p = low09_pages[k].first;
        const RawTextSignals& sig = low09_pages[k].second;
        if (dumping) {
            std::printf("low09page id=%s page=%d chars=%lld maperr=%lld tokens=%lld alpha=%lld wordlike=%lld "
                        "known=%lld numeric=%lld single=%lld\n",
                        dump_id.c_str(), p, sig.chars, sig.maperr, sig.tokens, sig.alpha, sig.wordlike, sig.known,
                        sig.numeric, sig.single);
        } else {
            std::printf("low09page chars=%lld maperr=%lld tokens=%lld alpha=%lld wordlike=%lld known=%lld "
                        "numeric=%lld single=%lld\n",
                        sig.chars, sig.maperr, sig.tokens, sig.alpha, sig.wordlike, sig.known, sig.numeric,
                        sig.single);
        }
    }
}


}  // namespace

int main(int argc, char** argv) {
    if (argc >= 3 && std::strcmp(argv[1], "census") == 0) {
        return RunCensus(argv[2]);
    }
    if (argc >= 3 && std::strcmp(argv[1], "bench") == 0) {
        // #514 criterion 3: megapdf_structure_load alone, timed and measured (see RunBench).
        int repeats = 3;
        for (int i = 3; i < argc; i++) {
            if (std::strcmp(argv[i], "--repeats") == 0 && i + 1 < argc) repeats = std::atoi(argv[++i]);
        }
        return RunBench(argv[2], repeats < 1 ? 1 : repeats);
    }
    if (argc >= 3 && std::strcmp(argv[1], "check") == 0) {
        Options opt;
        opt.pdf = argv[2];
        for (int i = 3; i < argc; i++) {
            if (std::strcmp(argv[i], "--dump") == 0 && i + 1 < argc) opt.dump_dir = argv[++i];
            else if (std::strcmp(argv[i], "--dump-id") == 0 && i + 1 < argc) opt.dump_id = argv[++i];
            else if (std::strcmp(argv[i], "--reference") == 0 && i + 1 < argc) opt.reference_file = argv[++i];
            else if (std::strcmp(argv[i], "--cli-reference") == 0 && i + 1 < argc) opt.cli_reference_file = argv[++i];
        }
        return RunCheck(opt);
    }
    if (argc >= 3 && std::strcmp(argv[1], "diag") == 0) {
        // #363 investigation only: structure_check diag <pdf> [<pdf> ...]
        // One aggregate numbers-only report over all documents given (see the DiagTotals
        // comment above) -- not part of the #354 battery or gate.
        std::vector<std::string> pdfs;
        for (int i = 2; i < argc; i++) pdfs.push_back(argv[i]);
        return RunDiag(pdfs);
    }
    if (argc >= 3 && std::strcmp(argv[1], "diagbaseline") == 0) {
        // #363 follow-up investigation only: structure_check diagbaseline <pdf> [<pdf> ...]
        // Characterizes the residual same-run, small-gap, large-baseline-offset pairs left
        // after PR #364's kWordGapEm fix (see the BaselineTotals comment above).
        std::vector<std::string> pdfs;
        for (int i = 2; i < argc; i++) pdfs.push_back(argv[i]);
        return RunDiagBaseline(pdfs);
    }
    if (argc >= 3 && std::strcmp(argv[1], "headingdiag") == 0) {
        // #375 investigation only: structure_check headingdiag <pdf> [<pdf> ...]
        // Characterizes the bold-at-body-size HEADING rule's over-firing (see the
        // HeadingDiagTotals comment above): run-length clustering, XY-cut column-census
        // overlap, and short/numeric-text proxies.
        std::vector<std::string> pdfs;
        for (int i = 2; i < argc; i++) pdfs.push_back(argv[i]);
        return RunHeadingDiag(pdfs);
    }
    if (argc >= 3 && std::strcmp(argv[1], "bodysizediag") == 0) {
        // #382 investigation only: structure_check bodysizediag <pdf> [<pdf> ...]
        // Characterizes the sub-half-point reported font sizes that round the body size to 0
        // (see the BodySizeDiagTotals comment above).
        std::vector<std::string> pdfs;
        for (int i = 2; i < argc; i++) pdfs.push_back(argv[i]);
        return RunBodySizeDiag(pdfs);
    }
    if (argc >= 3 && std::strcmp(argv[1], "garbage") == 0) {
        // #385 investigation only: structure_check garbage <pdf> [--dump-garbage <dir> --dump-id <id>]
        // Scores fid_low09 pages (and every page, corpus-wide) for genuine character-level
        // font/ToUnicode garbage vs ordinary hyphenation/spacing noise (see the RawTextSignals
        // comment above).
        std::string pdf = argv[2];
        std::string dump_dir, dump_id;
        for (int i = 3; i < argc; i++) {
            if (std::strcmp(argv[i], "--dump-garbage") == 0 && i + 1 < argc) dump_dir = argv[++i];
            else if (std::strcmp(argv[i], "--dump-id") == 0 && i + 1 < argc) dump_id = argv[++i];
        }
        RunGarbageOnDoc(pdf, dump_dir, dump_id);
        return 0;
    }
    std::printf("usage:\n"
                "  structure_check check <pdf> [--dump <dir> --dump-id <id>] [--reference <pdftotext-file>]\n"
                "                              [--cli-reference <megapdf-cli-output-file>]\n"
                "  structure_check census <pdf>\n"
                "  structure_check bench <pdf> [--repeats N]   (#514: megapdf_structure_load alone)\n"
                "  structure_check diag <pdf> [<pdf> ...]   (#363 investigation only)\n"
                "  structure_check diagbaseline <pdf> [<pdf> ...]   (#363 follow-up investigation only)\n"
                "  structure_check headingdiag <pdf> [<pdf> ...]   (#375 investigation only)\n"
                "  structure_check bodysizediag <pdf> [<pdf> ...]   (#382 investigation only)\n"
                "  structure_check garbage <pdf> [--dump-garbage <dir> --dump-id <id>]   (#385 investigation only)\n");
    return 64;
}
