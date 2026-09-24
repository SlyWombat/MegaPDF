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
//            census finds tagged. Contract 9 has no tagged path until #358, so this reimplements
//            just the tree-to-text mapping from design #1.1 (marked-content IDs, not element
//            classification) — a phase-1 proxy, not #358's real order-source comparison.
//         3. agreement with poppler — the same tau against `pdftotext -layout` output, read
//            from --reference (a file the battery already produced; this tool never shells
//            out). Informational only.
//         4. robustness — timing and memory; crashes and hangs are the caller's business
//            (a segfault or a timeout means this process does not get to print anything).
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
#include <vector>

#include "megapdf_core.h"
#include "fpdfview.h"
#include "fpdf_text.h"
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
// megapdf_block_string / megapdf_block_span helpers.
// ---------------------------------------------------------------------------
std::vector<unsigned short> BlockString(const megapdf_structure* s, size_t i, megapdf_block_field which) {
    const size_t n = megapdf_block_string(s, i, which, nullptr, 0);
    std::vector<unsigned short> buf(n);
    if (n > 0) megapdf_block_string(s, i, which, buf.data(), n);
    return buf;
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
        const std::vector<unsigned short> text16 = BlockString(s, i, MEGAPDF_BLOCK_TEXT);
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
        return text;
    };
    std::vector<std::string> cli_reference_pages;
    if (!opt.cli_reference_file.empty()) {
        cli_reference_pages = split_on_page_markers(opt.cli_reference_file);
        for (std::string& page : cli_reference_pages) page = strip_page_image_placeholder(page);
    }

    std::vector<int> confidences;
    std::vector<double> ms_per_page;
    int tagged_pages = 0, textless_pages = 0, multicol_pages = 0, manycut_pages = 0;
    FidelityCounts fidelity_total;
    FidelityCounts cli_fidelity_total;   // #355: the same measure 1, through megapdf-cli's own output
    int fidelity_low09 = 0;
    std::vector<long long> tau_tree_x1000, tau_ref_x1000;

    for (int p = 0; p < pages; p++) {
        const auto page_t0 = std::chrono::steady_clock::now();
        confidences.push_back(megapdf_structure_page_confidence(s, p));

        const ColumnCensus cc = ColumnCensusForPage(blocks_by_page[static_cast<size_t>(p)], body_size);
        if (cc.multi_column) multicol_pages++;
        if (cc.many_cut) manycut_pages++;

        FPDF_PAGE raw_page = raw != nullptr ? FPDF_LoadPage(raw, p) : nullptr;
        FPDF_TEXTPAGE textpage = raw_page != nullptr ? FPDFText_LoadPage(raw_page) : nullptr;
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

            if (!opt.census_only) {
                std::vector<int> mcids;
                if (raw_page != nullptr && TaggedPageTree(raw_page, &mcids)) {
                    tagged_pages++;
                    const std::vector<Token> tree_tokens = TreeTokenOrder(textpage, mcids);
                    const double tau = KendallTau(tokens_by_page[static_cast<size_t>(p)], tree_tokens);
                    if (tau > kTauNotEnoughData) tau_tree_x1000.push_back(static_cast<long long>(tau * 1000.0));
                } else if (raw_page != nullptr) {
                    std::vector<int> discard;
                    if (TaggedPageTree(raw_page, &discard)) tagged_pages++;
                }
                if (static_cast<size_t>(p) < reference_pages.size()) {
                    const std::vector<Token> ref_tokens = Tokenize(Utf8ToCodepoints(reference_pages[static_cast<size_t>(p)]));
                    const double tau = KendallTau(tokens_by_page[static_cast<size_t>(p)], ref_tokens);
                    if (tau > kTauNotEnoughData) tau_ref_x1000.push_back(static_cast<long long>(tau * 1000.0));
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
            const std::vector<unsigned short> text16 = BlockString(s, i, MEGAPDF_BLOCK_TEXT);
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

    std::printf("result=ok pages=%d tagged=%d tree=0 textless=%d multicol=%d manycut=%d ms_per_page=%.3f rss_kb=%lld "
                "conf_deciles=%s %s fid_match=%lld fid_a=%lld fid_b=%lld fid_low09=%d "
                "cli_fid_match=%lld cli_fid_a=%lld cli_fid_b=%lld "
                "tau_tree_n=%zu tau_tree=%s tau_ref_n=%zu tau_ref=%s\n",
                pages, tagged_pages, textless_pages, multicol_pages, manycut_pages, ms_avg, PeakRssKb(),
                ConfidenceDeciles(confidences).c_str(), blocks_field.str().c_str(), fidelity_total.matched,
                fidelity_total.a, fidelity_total.b, fidelity_low09,
                cli_fidelity_total.matched, cli_fidelity_total.a, cli_fidelity_total.b,
                tau_tree_x1000.size(), JoinInts(tau_tree_x1000).c_str(), tau_ref_x1000.size(),
                JoinInts(tau_ref_x1000).c_str());
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
        const std::vector<unsigned short> text16 = BlockString(s, i, MEGAPDF_BLOCK_TEXT);
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

}  // namespace

int main(int argc, char** argv) {
    if (argc >= 3 && std::strcmp(argv[1], "census") == 0) {
        return RunCensus(argv[2]);
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
    std::printf("usage:\n"
                "  structure_check check <pdf> [--dump <dir> --dump-id <id>] [--reference <pdftotext-file>]\n"
                "                              [--cli-reference <megapdf-cli-output-file>]\n"
                "  structure_check census <pdf>\n"
                "  structure_check diag <pdf> [<pdf> ...]   (#363 investigation only)\n");
    return 64;
}
