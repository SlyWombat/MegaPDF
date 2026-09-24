// tools/structure-check — the #354 corpus tool for contract 9 (megapdf_structure_*, #353).
//
// Built exactly as tools/leakcheck is (core/CMakeLists.txt: the core tests' pdfium-beside-
// the-binary target, the same $ORIGIN/@loader_path handling), so a change to the contract is
// a change both the core tests and this tool see. tools/stress/structure-battery.sh drives it
// over the corpus, one document per process, the way redaction-battery.sh drives leakcheck.
//
//   structure_check check <pdf> [--dump <dir> --dump-id <id>] [--reference <file>]
//       One line of numbers on stdout ("result=..."): outcome, pages, tagged/textless/
//       multi-column/many-cut page counts (the #354 census, computed the same way --census
//       does), confidence deciles, block counts by kind, ms/page, peak RSS, and the four
//       measures (design #142 comment, 2026-09-24, section 7):
//         1. token fidelity  — per-page token-multiset F1 of the heuristic blocks' text
//            (KEEP_FURNITURE | ALL_FIELDS, FIELD blocks excluded) against FPDFText_GetText.
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
    std::vector<std::string> reference_pages;
    if (!opt.reference_file.empty()) {
        std::ifstream f(opt.reference_file, std::ios::binary);
        if (f.good()) {
            std::string all((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
            std::string cur;
            for (char c : all) {
                if (c == '\f') { reference_pages.push_back(cur); cur.clear(); }
                else cur.push_back(c);
            }
            reference_pages.push_back(cur);
        }
    }

    std::vector<int> confidences;
    std::vector<double> ms_per_page;
    int tagged_pages = 0, textless_pages = 0, multicol_pages = 0, manycut_pages = 0;
    FidelityCounts fidelity_total;
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
            std::vector<unsigned int> page_codepoints;
            page_codepoints.reserve(static_cast<size_t>((std::max)(chars, 0)));
            for (int i = 0; i < chars; i++) {
                const unsigned int cp = FPDFText_GetUnicode(textpage, i);
                if (cp != 0) page_codepoints.push_back(cp);
            }
            const std::vector<Token> pdfium_tokens = Tokenize(page_codepoints);
            const FidelityCounts fc = MultisetF1(tokens_by_page[static_cast<size_t>(p)], pdfium_tokens);
            fidelity_total.matched += fc.matched;
            fidelity_total.a += fc.a;
            fidelity_total.b += fc.b;
            if (F1(fc) < 0.9) fidelity_low09++;

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
                "tau_tree_n=%zu tau_tree=%s tau_ref_n=%zu tau_ref=%s\n",
                pages, tagged_pages, textless_pages, multicol_pages, manycut_pages, ms_avg, PeakRssKb(),
                ConfidenceDeciles(confidences).c_str(), blocks_field.str().c_str(), fidelity_total.matched,
                fidelity_total.a, fidelity_total.b, fidelity_low09, tau_tree_x1000.size(),
                JoinInts(tau_tree_x1000).c_str(), tau_ref_x1000.size(), JoinInts(tau_ref_x1000).c_str());
    return 0;
}

int RunCensus(const std::string& pdf) {
    Options opt;
    opt.pdf = pdf;
    opt.census_only = true;
    return RunCheck(opt);
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
        }
        return RunCheck(opt);
    }
    std::printf("usage:\n"
                "  structure_check check <pdf> [--dump <dir> --dump-id <id>] [--reference <pdftotext-file>]\n"
                "  structure_check census <pdf>\n");
    return 64;
}
