// The plain-text and Markdown writers (#142, #355, #357), the two consumers of contract 9
// (megapdf_structure_*, #353). `megapdf-cli extract` (core/cli/megapdf_cli.cpp) is a thin shell
// over megapdf_write_text(): it opens, calls this, and maps the return value and
// megapdf_last_error() to an exit code.
//
// The whole output is built into one in-memory buffer and handed to `write` once. Contract 9
// already materialises the whole page range's blocks in memory (megapdf_structure_load), so
// this adds no new order-of-magnitude cost, and it means a mid-stream abort never has to unwind
// partially-written state: either the callback gets the complete text, or (on cancellation) it
// gets nothing at all, matching megapdf_structure_load's own "cancelled means NULL, not partial"
// contract (#145).
//
// keep_lines is a best-effort feature, not an exact one, and this file's own comment on
// RenderBlockText()/RenderBlockMarkdown() below explains why: contract 9's megapdf_span is a
// same-*style* run (megapdf_structure.cpp's SliceIntoSpans slices on font/weight/style, not on
// line), so a plain paragraph in one face commonly comes back as ONE span spanning several
// visual lines, with no public accessor for where the lines inside it were. Both writers recover
// a break only where two consecutive spans' vertical centres do not overlap (BuildLines' own
// rule, reused at span granularity) -- which catches a heading followed by body text, a bold run
// on its own line, and similar style-driven breaks, but not a plain paragraph's internal
// wrapping. Good enough for what --keep-lines exists for (diffing against `pdftotext`'s coarse
// shape), not a promise that every original line comes back.
//
// The Markdown writer (design: the 2026-09-24 comment on #142, §3) renders contract 9's blocks
// and infers nothing itself, so the reflow view (#168) and this writer can never disagree about
// what a heading is. It always walks spans (unlike the plain-text writer's shortcut when
// !keep_lines), because span style (bold/italic/monospace) has to survive into the output even
// on a single unwrapped line.
#include "megapdf_core.h"
#include "megapdf_core_internal.h"

#include <algorithm>
#include <cmath>
#include <string>
#include <vector>

namespace {

using megapdf_internal::IsCancelled;
using megapdf_internal::SetLastError;

using U16 = std::vector<unsigned short>;

void AppendUtf8(std::string* out, unsigned int cp) {
    if (cp <= 0x7Fu) {
        out->push_back(static_cast<char>(cp));
    } else if (cp <= 0x7FFu) {
        out->push_back(static_cast<char>(0xC0u | (cp >> 6)));
        out->push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
    } else if (cp <= 0xFFFFu) {
        out->push_back(static_cast<char>(0xE0u | (cp >> 12)));
        out->push_back(static_cast<char>(0x80u | ((cp >> 6) & 0x3Fu)));
        out->push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
    } else {
        out->push_back(static_cast<char>(0xF0u | (cp >> 18)));
        out->push_back(static_cast<char>(0x80u | ((cp >> 12) & 0x3Fu)));
        out->push_back(static_cast<char>(0x80u | ((cp >> 6) & 0x3Fu)));
        out->push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
    }
}

// UTF-16 (possibly with surrogate pairs; contract 9's strings can carry any code point) to
// UTF-8. Every megapdf_block_string()/megapdf_block_span_string() result is well-formed UTF-16
// (it comes from PDFium's own FPDFText_GetUnicode/ToUnicode path), so no repair pass is needed.
std::string Utf16ToUtf8(const U16& u) {
    std::string out;
    out.reserve(u.size());
    for (size_t i = 0; i < u.size(); i++) {
        unsigned int cp = u[i];
        if (cp >= 0xD800u && cp <= 0xDBFFu && i + 1 < u.size() && u[i + 1] >= 0xDC00u && u[i + 1] <= 0xDFFFu) {
            cp = 0x10000u + ((cp - 0xD800u) << 10) + (u[i + 1] - 0xDC00u);
            i++;
        }
        AppendUtf8(&out, cp);
    }
    return out;
}

// Decodes UTF-16 (with surrogate pairs) into code points, for marker classification below --
// this needs to inspect individual characters (is this a digit? a roman letter?), which is
// awkward over raw UTF-8 bytes.
std::vector<unsigned int> DecodeUtf16(const U16& u) {
    std::vector<unsigned int> cps;
    cps.reserve(u.size());
    for (size_t i = 0; i < u.size(); i++) {
        unsigned int cp = u[i];
        if (cp >= 0xD800u && cp <= 0xDBFFu && i + 1 < u.size() && u[i + 1] >= 0xDC00u && u[i + 1] <= 0xDFFFu) {
            cp = 0x10000u + ((cp - 0xD800u) << 10) + (u[i + 1] - 0xDC00u);
            i++;
        }
        cps.push_back(cp);
    }
    return cps;
}

U16 BlockString(const megapdf_structure* s, size_t i, megapdf_block_field which) {
    const size_t n = megapdf_block_string(s, i, which, nullptr, 0);
    U16 buf(n);
    if (n > 0) megapdf_block_string(s, i, which, buf.data(), n);
    return buf;
}

U16 SpanString(const megapdf_structure* s, size_t block, size_t span) {
    const size_t n = megapdf_block_span_string(s, block, span, nullptr, 0);
    U16 buf(n);
    if (n > 0) megapdf_block_span_string(s, block, span, buf.data(), n);
    return buf;
}

// See this file's header comment: recovers a line break only at a span boundary whose vertical
// centres do not overlap (BuildLines'/megapdf_core.h's own half-the-taller-height rule).
std::string RenderBlockText(const megapdf_structure* s, size_t index, bool keep_lines) {
    const size_t span_count = megapdf_block_span_count(s, index);
    if (!keep_lines || span_count <= 1) {
        return Utf16ToUtf8(BlockString(s, index, MEGAPDF_BLOCK_TEXT));
    }
    std::string out;
    megapdf_span prev{};
    bool have_prev = false;
    for (size_t si = 0; si < span_count; si++) {
        megapdf_span cur{};
        if (megapdf_block_span_get(s, index, si, &cur) != MEGAPDF_OK) continue;
        const U16 text = SpanString(s, index, si);
        if (text.empty()) continue;
        if (have_prev) {
            const double prev_h = prev.bounds.top - prev.bounds.bottom;
            const double cur_h = cur.bounds.top - cur.bounds.bottom;
            const double prev_c = (prev.bounds.top + prev.bounds.bottom) / 2.0;
            const double cur_c = (cur.bounds.top + cur.bounds.bottom) / 2.0;
            const double tol = 0.5 * (std::max)(prev_h, cur_h);
            const bool same_line = std::fabs(prev_c - cur_c) <= tol;
            if (!same_line) out += "\n";
        }
        out += Utf16ToUtf8(text);
        prev = cur;
        have_prev = true;
    }
    return out;
}

// --------------------------------------------------------------------------
// Markdown (#357, design §3): escaping, span styling and marker classification.
// --------------------------------------------------------------------------

// Escapes `\`, `` ` ``, `*`, `_`, `[`, `]` unconditionally. When `at_line_start` is non-null and
// currently true, also escapes a leading `#`/`>` (design's blunt "at line start" rule -- not
// conditioned on what follows, matching the design text literally) and a leading `-` or digit
// ordinal (`\d+[.)]`) that CommonMark would otherwise read as a list marker, only when it is
// followed by whitespace or the end of the string (i.e. it would actually parse as one).
// `*at_line_start` is cleared after the first character consumed either way, so only position 0
// of a call is ever treated as a line start -- callers reset it to true themselves after any
// internal line break they insert. `escape_pipe` additionally escapes `|`, for table cell text
// (design's "and `|` inside a table cell").
void AppendEscaped(std::string* out, const std::string& text, bool* at_line_start, bool escape_pipe = false) {
    size_t i = 0;
    if (at_line_start != nullptr && *at_line_start && !text.empty()) {
        if (text[0] == '-' && (text.size() == 1 || text[1] == ' ' || text[1] == '\t')) {
            *out += "\\-";
            i = 1;
            *at_line_start = false;
        } else {
            size_t d = 0;
            while (d < text.size() && text[d] >= '0' && text[d] <= '9') d++;
            if (d > 0 && d < text.size() && (text[d] == '.' || text[d] == ')') &&
                (d + 1 == text.size() || text[d + 1] == ' ' || text[d + 1] == '\t')) {
                out->append(text, 0, d);
                out->push_back('\\');
                out->push_back(text[d]);
                i = d + 1;
                *at_line_start = false;
            }
        }
    }
    for (; i < text.size(); i++) {
        const char c = text[i];
        bool escape = (c == '\\' || c == '`' || c == '*' || c == '_' || c == '[' || c == ']');
        if (!escape && escape_pipe && c == '|') escape = true;
        if (!escape && at_line_start != nullptr && *at_line_start && (c == '#' || c == '>')) escape = true;
        if (escape) out->push_back('\\');
        out->push_back(c);
        if (at_line_start != nullptr) *at_line_start = false;
    }
}

std::string EscapePlainMd(const std::string& text) {
    std::string out;
    AppendEscaped(&out, text, nullptr);
    return out;
}

std::string EscapeTableCellMd(const std::string& text) {
    std::string out;
    AppendEscaped(&out, text, nullptr, /*escape_pipe=*/true);
    return out;
}

// A CommonMark code span: backtick-fenced with a fence one longer than the longest run of
// backticks already in `raw`, and surrounded by a single space on each side when `raw` starts or
// ends with a backtick or is all spaces (the spec's own padding rule -- otherwise a leading/
// trailing backtick in the content would merge into the fence).
std::string RenderCodeSpan(const std::string& raw) {
    size_t max_run = 0, cur = 0;
    for (char c : raw) {
        if (c == '`') {
            cur++;
            if (cur > max_run) max_run = cur;
        } else {
            cur = 0;
        }
    }
    const std::string fence(max_run + 1, '`');
    bool all_space = true;
    for (char c : raw) {
        if (c != ' ') {
            all_space = false;
            break;
        }
    }
    const bool pad = raw.front() == '`' || raw.back() == '`' || all_space;
    std::string out = fence;
    if (pad) out += ' ';
    out += raw;
    if (pad) out += ' ';
    out += fence;
    return out;
}

enum class MdStyle { Plain, Bold, Italic, BoldItalic, Mono };

// SPAN_CELL_START/SPAN_LINK are tagged-only (#358) and not rendered as Markdown decoration here
// (link targets are explicitly deferred -- design §3 "v1 vs deferred"). Monospace wins over
// bold/italic when a span somehow carries both: a CommonMark code span's content is literal (no
// nested emphasis), so there is no combined syntax to fall back on.
MdStyle EffectiveStyle(int flags) {
    if (flags & MEGAPDF_SPAN_MONOSPACE) return MdStyle::Mono;
    const bool bold = (flags & MEGAPDF_SPAN_BOLD) != 0;
    const bool italic = (flags & MEGAPDF_SPAN_ITALIC) != 0;
    if (bold && italic) return MdStyle::BoldItalic;
    if (bold) return MdStyle::Bold;
    if (italic) return MdStyle::Italic;
    return MdStyle::Plain;
}

const char* StyleMarker(MdStyle st) {
    switch (st) {
        case MdStyle::Bold: return "**";
        case MdStyle::Italic: return "*";
        case MdStyle::BoldItalic: return "***";
        default: return "";
    }
}

// Renders a block's spans as Markdown: bold/italic/monospace decoration (adjacent spans with the
// same effective style merge, so styling never closes and reopens mid-run), each span's text
// escaped (AppendEscaped above), and -- when `keep_lines` recovers a line break (this file's
// header comment) -- any open style is closed before the break and reopened after, so a marker
// is never left spanning a line break (design §3, "never opened across a line break"). Every
// recovered line's first character is again eligible for the line-start escapes (a recovered
// break can start what CommonMark would read as a new block). `protect_first_line` controls
// whether position 0 of the whole block is itself eligible: true for a block that renders as its
// own line from column 0 (PARAGRAPH, FURNITURE), false for one that follows a marker on the same
// line (a LIST_ITEM's text, after "- " or "1. ").
std::string RenderBlockMarkdown(const megapdf_structure* s, size_t index, bool keep_lines, bool protect_first_line) {
    const size_t span_count = megapdf_block_span_count(s, index);
    std::string out;
    MdStyle current = MdStyle::Plain;
    std::string mono_buf;
    bool at_line_start = protect_first_line;
    megapdf_span prev{};
    bool have_prev = false;

    auto close_current = [&]() {
        if (current == MdStyle::Mono) {
            if (!mono_buf.empty()) out += RenderCodeSpan(mono_buf);
            mono_buf.clear();
        } else {
            out += StyleMarker(current);
        }
        current = MdStyle::Plain;
    };

    for (size_t si = 0; si < span_count; si++) {
        megapdf_span cur{};
        if (megapdf_block_span_get(s, index, si, &cur) != MEGAPDF_OK) continue;
        const U16 text16 = SpanString(s, index, si);
        if (text16.empty()) continue;
        const std::string text = Utf16ToUtf8(text16);

        if (keep_lines && have_prev) {
            const double prev_h = prev.bounds.top - prev.bounds.bottom;
            const double cur_h = cur.bounds.top - cur.bounds.bottom;
            const double prev_c = (prev.bounds.top + prev.bounds.bottom) / 2.0;
            const double cur_c = (cur.bounds.top + cur.bounds.bottom) / 2.0;
            const double tol = 0.5 * (std::max)(prev_h, cur_h);
            const bool same_line = std::fabs(prev_c - cur_c) <= tol;
            if (!same_line) {
                close_current();
                out += "\n";
                at_line_start = true;
            }
        }

        const MdStyle style = EffectiveStyle(cur.flags);
        if (style != current) {
            close_current();
            current = style;
            if (style != MdStyle::Mono) out += StyleMarker(style);
        }

        if (current == MdStyle::Mono) {
            mono_buf += text;
            at_line_start = false;
        } else {
            AppendEscaped(&out, text, &at_line_start);
        }

        prev = cur;
        have_prev = true;
    }
    close_current();
    return out;
}

enum class MarkerKind { Glyph, Numeric, Alpha };

// The design's starting bullet set (§1.2 "Lists"), matched exactly so a marker this writer calls
// a glyph is the same one megapdf_structure.cpp's IsBulletCodepoint already decided was one.
bool IsBulletCp(unsigned int cp) {
    switch (cp) {
        case 0x2022: case 0x25E6: case 0x25AA: case 0x2013: case 0x2014: case 0x00B7:
        case '*': case '-':
        case 0xF0B7: case 0xF0A7:
            return true;
        default:
            return false;
    }
}
bool IsRomanCp(unsigned int cp) {
    const unsigned int lower = (cp >= 'A' && cp <= 'Z') ? cp + 32 : cp;
    return lower == 'i' || lower == 'v' || lower == 'x' || lower == 'l' || lower == 'c' || lower == 'd' ||
           lower == 'm';
}
bool IsDigitCp(unsigned int cp) { return cp >= '0' && cp <= '9'; }
bool IsLetterCp(unsigned int cp) { return (cp >= 'a' && cp <= 'z') || (cp >= 'A' && cp <= 'Z'); }

// Mirrors megapdf_structure.cpp's IsListMarker (design §1.2) closely enough to tell a bullet
// glyph from a numeric or lettered/roman ordinal -- the three cases design §3 renders
// differently ("- text", "N. text", "1. <marker> text"). Not IsListMarker itself (a separate
// translation unit, and that function only answers "is this a marker at all", which contract 9
// has already decided by the time a LIST_ITEM block exists) -- just its category. An
// unrecognised shape (should not happen for a marker contract 9 produced) falls back to Glyph,
// so it still renders as a bullet rather than being dropped.
MarkerKind ClassifyMarker(const std::vector<unsigned int>& cps, long* number) {
    *number = 0;
    if (cps.empty()) return MarkerKind::Glyph;
    if (cps.size() == 1 && IsBulletCp(cps[0])) return MarkerKind::Glyph;
    if (cps.front() == '(' && cps.back() == ')' && cps.size() >= 3) {
        bool digits = true;
        long n = 0;
        for (size_t i = 1; i + 1 < cps.size(); i++) {
            if (!IsDigitCp(cps[i])) { digits = false; break; }
            n = n * 10 + static_cast<long>(cps[i] - '0');
        }
        if (digits) {
            *number = n;
            return MarkerKind::Numeric;
        }
    }
    const unsigned int last = cps.back();
    if (last == '.' || last == ')') {
        const size_t body_len = cps.size() - 1;
        if (body_len > 0) {
            bool all_digits = true, all_roman = true;
            long n = 0;
            for (size_t i = 0; i < body_len; i++) {
                if (IsDigitCp(cps[i])) {
                    n = n * 10 + static_cast<long>(cps[i] - '0');
                } else {
                    all_digits = false;
                }
                if (!IsRomanCp(cps[i])) all_roman = false;
            }
            if (all_digits) {
                *number = n;
                return MarkerKind::Numeric;
            }
            if (body_len == 1 && IsLetterCp(cps[0])) return MarkerKind::Alpha;
            if (all_roman) return MarkerKind::Alpha;
        }
    }
    return MarkerKind::Glyph;
}

// design §3: "none by default in Markdown (a blank line); --page-marker writes <!-- page N -->"
// -- unlike the plain-text writer, the default and MEGAPDF_PAGE_BREAK_NONE render identically in
// Markdown (neither a form feed nor "--- page N ---" belongs in CommonMark output).
void AppendPageSeparatorMarkdown(std::string* out, int page_break, int next_page1based) {
    if (page_break == MEGAPDF_PAGE_BREAK_MARKER) {
        *out += "\n<!-- page " + std::to_string(next_page1based) + " -->\n\n";
    } else {
        *out += "\n";
    }
}

void AppendPageSeparatorText(std::string* out, int page_break, int next_page1based) {
    switch (page_break) {
        case MEGAPDF_PAGE_BREAK_FORM_FEED: *out += "\f\n"; break;
        case MEGAPDF_PAGE_BREAK_MARKER: *out += "\n--- page " + std::to_string(next_page1based) + " ---\n\n"; break;
        case MEGAPDF_PAGE_BREAK_NONE: *out += "\n"; break;
        default: break;
    }
}

struct FieldInfo {
    megapdf_rect bounds{};
    int kind = MEGAPDF_FIELD_OTHER;
    int is_checked = 0;
};

// Contract 9's FIELD block carries the field's name (marker) and value/state (text) but not its
// kind (megapdf_block has no field-kind slot; #142's design comment does not add one). BuildFields
// (megapdf_structure.cpp) copies a field's bounds onto the block unchanged, so the field that
// produced a given FIELD block is found again by an exact bounds match against a fresh
// megapdf_form_fields_load() of the same page -- the same source contract 9 itself reads.
bool RectsEqual(const megapdf_rect& a, const megapdf_rect& b) {
    constexpr double kEps = 1e-6;
    return std::fabs(a.left - b.left) < kEps && std::fabs(a.bottom - b.bottom) < kEps &&
           std::fabs(a.right - b.right) < kEps && std::fabs(a.top - b.top) < kEps;
}

std::vector<FieldInfo> LoadFieldInfos(megapdf_document* document, int page_index) {
    std::vector<FieldInfo> out;
    megapdf_page* page = megapdf_load_page(document, page_index);
    if (page == nullptr) return out;
    megapdf_form_fields* fields = megapdf_form_fields_load(page);
    if (fields != nullptr) {
        const size_t n = megapdf_form_field_count(fields);
        for (size_t i = 0; i < n; i++) {
            megapdf_form_field f{};
            if (megapdf_form_field_get(fields, i, &f) != MEGAPDF_OK) continue;
            out.push_back(FieldInfo{f.bounds, f.kind, f.is_checked});
        }
        megapdf_form_fields_free(fields);
    }
    megapdf_close_page(page);
    return out;
}

const FieldInfo* FindField(const std::vector<FieldInfo>& fields, const megapdf_rect& bounds) {
    for (const FieldInfo& f : fields) {
        if (RectsEqual(f.bounds, bounds)) return &f;
    }
    return nullptr;
}

}  // namespace

extern "C" {

MEGAPDF_API int megapdf_write_text(megapdf_document* document, int first_page, int page_count, int format,
                                   const megapdf_write_options* options, megapdf_write_fn write, void* context,
                                   const megapdf_cancel* cancel) {
    if (document == nullptr || write == nullptr || page_count <= 0 ||
        (format != MEGAPDF_WRITE_TEXT && format != MEGAPDF_WRITE_MARKDOWN)) {
        return MEGAPDF_ERR_ARGUMENT;
    }
    const bool is_markdown = format == MEGAPDF_WRITE_MARKDOWN;
    megapdf_write_options opt{};
    if (options != nullptr) opt = *options;
    if (opt.page_break < MEGAPDF_PAGE_BREAK_FORM_FEED || opt.page_break > MEGAPDF_PAGE_BREAK_NONE ||
        opt.fields < MEGAPDF_WRITE_FIELDS_FILLED || opt.fields > MEGAPDF_WRITE_FIELDS_NONE) {
        return MEGAPDF_ERR_ARGUMENT;
    }
    const bool keep_lines = opt.keep_lines != 0;

    unsigned int structure_flags = MEGAPDF_STRUCTURE_DEFAULT;
    if (opt.heuristic_only) structure_flags |= MEGAPDF_STRUCTURE_HEURISTIC_ONLY;
    if (opt.keep_furniture) structure_flags |= MEGAPDF_STRUCTURE_KEEP_FURNITURE;
    if (opt.fields == MEGAPDF_WRITE_FIELDS_ALL) structure_flags |= MEGAPDF_STRUCTURE_ALL_FIELDS;

    megapdf_structure* s = megapdf_structure_load(document, first_page, page_count, structure_flags, cancel);
    if (s == nullptr) {
        return IsCancelled(cancel) ? MEGAPDF_ERR_CANCELLED : MEGAPDF_ERR_ARGUMENT;
    }

    // Block indices bucketed by page, relative to first_page; blocks come back from contract 9
    // already in reading order, page by page, so this only needs one pass.
    const size_t n_blocks = megapdf_block_count(s);
    std::vector<std::vector<size_t>> by_page(static_cast<size_t>(page_count));
    for (size_t i = 0; i < n_blocks; i++) {
        megapdf_block b{};
        if (megapdf_block_get(s, i, &b) != MEGAPDF_OK) continue;
        const int rel = b.page - first_page;
        if (rel < 0 || rel >= page_count) continue;
        by_page[static_cast<size_t>(rel)].push_back(i);
    }

    std::string out;
    bool wrote_any = false;         // whether a block has already been flushed into `out`
    bool pending_open = false;      // a rendered block is buffered, waiting to see if it continues
    bool pending_is_paragraph = false;
    std::string pending_text;

    auto flush_pending = [&]() {
        if (!pending_open) return;
        if (wrote_any) out += "\n";   // a blank line between blocks
        out += pending_text;
        out += "\n";
        wrote_any = true;
        pending_open = false;
        pending_is_paragraph = false;
        pending_text.clear();
    };

    int pages_with_text = 0;
    bool cancelled = false;
    for (int rel = 0; rel < page_count; rel++) {
        if (IsCancelled(cancel)) { cancelled = true; break; }
        const int page_index = first_page + rel;

        if (rel > 0) {
            // A continuing paragraph never joins across a printed page separator, even when
            // contract 9 marks the next block `continues` (a paragraph split across a page, not
            // a column): the separator always closes out whatever was pending first.
            flush_pending();
            if (is_markdown) AppendPageSeparatorMarkdown(&out, opt.page_break, page_index + 1);
            else AppendPageSeparatorText(&out, opt.page_break, page_index + 1);
            wrote_any = false;   // the separator already provides the gap before the next block
        }

        bool page_has_text = false;
        std::vector<FieldInfo> field_infos;
        bool field_infos_loaded = false;

        for (size_t bi : by_page[static_cast<size_t>(rel)]) {
            megapdf_block b{};
            if (megapdf_block_get(s, bi, &b) != MEGAPDF_OK) continue;
            if (b.kind != MEGAPDF_BLOCK_PAGE_IMAGE) page_has_text = true;
            if (b.kind == MEGAPDF_BLOCK_FIELD && opt.fields == MEGAPDF_WRITE_FIELDS_NONE) continue;

            std::string line;
            bool skip = false;
            if (is_markdown) {
                switch (b.kind) {
                    case MEGAPDF_BLOCK_HEADING: {
                        const int level = (std::min)(6, (std::max)(1, b.level));
                        line = std::string(static_cast<size_t>(level), '#') + " " +
                               EscapePlainMd(Utf16ToUtf8(BlockString(s, bi, MEGAPDF_BLOCK_TEXT)));
                        break;
                    }
                    case MEGAPDF_BLOCK_PARAGRAPH:
                    case MEGAPDF_BLOCK_FURNITURE:
                        line = RenderBlockMarkdown(s, bi, keep_lines, /*protect_first_line=*/true);
                        break;
                    case MEGAPDF_BLOCK_LIST_ITEM: {
                        const std::string indent(static_cast<size_t>((std::max)(0, b.level - 1)) * 2, ' ');
                        const U16 marker16 = BlockString(s, bi, MEGAPDF_BLOCK_MARKER);
                        const std::string text = RenderBlockMarkdown(s, bi, keep_lines, /*protect_first_line=*/false);
                        long number = 0;
                        const MarkerKind kind = ClassifyMarker(DecodeUtf16(marker16), &number);
                        std::string md_marker;
                        switch (kind) {
                            case MarkerKind::Numeric: md_marker = std::to_string(number) + "."; break;
                            case MarkerKind::Alpha: md_marker = "1."; break;
                            default: md_marker = "-"; break;
                        }
                        if (kind == MarkerKind::Alpha) {
                            const std::string original = EscapePlainMd(Utf16ToUtf8(marker16));
                            line = indent + md_marker + " " + original + (text.empty() ? "" : " ") + text;
                        } else {
                            line = indent + md_marker + (text.empty() ? "" : " ") + text;
                        }
                        break;
                    }
                    case MEGAPDF_BLOCK_FIELD: {
                        if (!field_infos_loaded) {
                            field_infos_loaded = true;
                            field_infos = LoadFieldInfos(document, page_index);
                        }
                        const FieldInfo* fi = FindField(field_infos, b.bounds);
                        const std::string name = EscapePlainMd(Utf16ToUtf8(BlockString(s, bi, MEGAPDF_BLOCK_MARKER)));
                        const std::string value = EscapePlainMd(Utf16ToUtf8(BlockString(s, bi, MEGAPDF_BLOCK_TEXT)));
                        const bool checkish =
                            fi != nullptr && (fi->kind == MEGAPDF_FIELD_CHECKBOX || fi->kind == MEGAPDF_FIELD_RADIO);
                        line = checkish ? (std::string("- [") + (fi->is_checked ? "x" : " ") + "] " + name)
                                        : ("**" + name + ":** " + value);
                        break;
                    }
                    case MEGAPDF_BLOCK_FIGURE: {
                        // Alt text needs the tagged path (#358): every FIGURE this phase has
                        // none, so this is currently always a skip -- kept, ahead of that phase,
                        // so a figure never silently prints as empty text once alt text exists.
                        const U16 alt = BlockString(s, bi, MEGAPDF_BLOCK_ALT);
                        if (alt.empty()) { skip = true; break; }
                        line = "*[Figure: " + EscapePlainMd(Utf16ToUtf8(alt)) + "]*";
                        break;
                    }
                    case MEGAPDF_BLOCK_TABLE_ROW: {
                        // Tagged-only, unused before #358 (no TABLE_ROW block is produced by the
                        // heuristic path). Design §3: a pipe table needs a known header row
                        // (cells tagged TH), which contract 9 does not yet expose (no per-cell
                        // TH/TD flag exists); until #358 adds one, every row renders through the
                        // "otherwise" branch -- its cells joined by a tab, one row per line.
                        const size_t spans = megapdf_block_span_count(s, bi);
                        for (size_t si = 0; si < spans; si++) {
                            if (si > 0) line += "\t";
                            line += EscapeTableCellMd(Utf16ToUtf8(SpanString(s, bi, si)));
                        }
                        break;
                    }
                    case MEGAPDF_BLOCK_PAGE_IMAGE:
                        line = "*[Page " + std::to_string(b.page + 1) + " has no text layer]*";
                        break;
                    default:
                        skip = true;
                        break;
                }
            } else {
                switch (b.kind) {
                    case MEGAPDF_BLOCK_HEADING:
                    case MEGAPDF_BLOCK_PARAGRAPH:
                    case MEGAPDF_BLOCK_FURNITURE:
                        line = RenderBlockText(s, bi, keep_lines);
                        break;
                    case MEGAPDF_BLOCK_LIST_ITEM: {
                        const std::string indent(static_cast<size_t>((std::max)(0, b.level - 1)) * 2, ' ');
                        const std::string marker = Utf16ToUtf8(BlockString(s, bi, MEGAPDF_BLOCK_MARKER));
                        const std::string text = RenderBlockText(s, bi, keep_lines);
                        line = indent + marker + ((marker.empty() || text.empty()) ? "" : " ") + text;
                        break;
                    }
                    case MEGAPDF_BLOCK_FIELD: {
                        if (!field_infos_loaded) {
                            field_infos_loaded = true;
                            field_infos = LoadFieldInfos(document, page_index);
                        }
                        const FieldInfo* fi = FindField(field_infos, b.bounds);
                        const std::string name = Utf16ToUtf8(BlockString(s, bi, MEGAPDF_BLOCK_MARKER));
                        const std::string value = Utf16ToUtf8(BlockString(s, bi, MEGAPDF_BLOCK_TEXT));
                        const bool checkish =
                            fi != nullptr && (fi->kind == MEGAPDF_FIELD_CHECKBOX || fi->kind == MEGAPDF_FIELD_RADIO);
                        line = checkish ? (std::string(fi->is_checked ? "[x] " : "[ ] ") + name) : (name + ": " + value);
                        break;
                    }
                    case MEGAPDF_BLOCK_FIGURE: {
                        // Alt text needs the tagged path (#358): every FIGURE this phase has
                        // none, so this is currently always a skip -- kept, ahead of that phase,
                        // so a figure never silently prints as empty text once alt text exists.
                        const U16 alt = BlockString(s, bi, MEGAPDF_BLOCK_ALT);
                        if (alt.empty()) { skip = true; break; }
                        line = Utf16ToUtf8(alt);
                        break;
                    }
                    case MEGAPDF_BLOCK_TABLE_ROW: {
                        // Tagged-only, unused before #358 (no TABLE_ROW block is produced by the
                        // heuristic path); design §2's shape (cells joined by a tab) all the same.
                        const size_t spans = megapdf_block_span_count(s, bi);
                        for (size_t si = 0; si < spans; si++) {
                            if (si > 0) line += "\t";
                            line += Utf16ToUtf8(SpanString(s, bi, si));
                        }
                        break;
                    }
                    case MEGAPDF_BLOCK_PAGE_IMAGE:
                        line = "[Page " + std::to_string(b.page + 1) + " has no text layer]";
                        break;
                    default:
                        skip = true;
                        break;
                }
            }
            if (skip) continue;

            const bool continuing_paragraph =
                b.kind == MEGAPDF_BLOCK_PARAGRAPH && b.continues != 0 && pending_open && pending_is_paragraph;
            if (continuing_paragraph) {
                pending_text += " ";
                pending_text += line;
            } else {
                flush_pending();
                pending_text = line;
                pending_open = true;
                pending_is_paragraph = b.kind == MEGAPDF_BLOCK_PARAGRAPH;
            }
        }
        if (page_has_text) pages_with_text++;
    }
    flush_pending();
    megapdf_structure_free(s);

    if (cancelled) return MEGAPDF_ERR_CANCELLED;

    if (!out.empty() && !write(context, out.data(), out.size())) {
        SetLastError(0, "the write callback aborted the extraction");
        return MEGAPDF_ERR_PDFIUM;
    }
    return pages_with_text;
}

}  // extern "C"
