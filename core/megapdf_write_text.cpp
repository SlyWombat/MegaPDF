// The plain-text writer (#142, #355), the first consumer of contract 9 (megapdf_structure_*,
// #353). `megapdf-cli extract` (core/cli/megapdf_cli.cpp) is a thin shell over
// megapdf_write_text(): it opens, calls this, and maps the return value and
// megapdf_last_error() to an exit code. Markdown (#357) is a second writer beside this one,
// sharing megapdf_write_options and megapdf_write_fn.
//
// The whole output is built into one in-memory buffer and handed to `write` once. Contract 9
// already materialises the whole page range's blocks in memory (megapdf_structure_load), so
// this adds no new order-of-magnitude cost, and it means a mid-stream abort never has to unwind
// partially-written state: either the callback gets the complete text, or (on cancellation) it
// gets nothing at all, matching megapdf_structure_load's own "cancelled means NULL, not partial"
// contract (#145).
//
// keep_lines is a best-effort feature, not an exact one, and this file's own comment on
// RenderBlockText() below explains why: contract 9's megapdf_span is a same-*style* run
// (megapdf_structure.cpp's SliceIntoSpans slices on font/weight/style, not on line), so a plain
// paragraph in one face commonly comes back as ONE span spanning several visual lines, with no
// public accessor for where the lines inside it were. This writer recovers a break only where
// two consecutive spans' vertical centres do not overlap (BuildLines' own rule, reused at span
// granularity) -- which catches a heading followed by body text, a bold run on its own line, and
// similar style-driven breaks, but not a plain paragraph's internal wrapping. Good enough for
// what --keep-lines exists for (diffing against `pdftotext`'s coarse shape), not a promise that
// every original line comes back.
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
    if (document == nullptr || write == nullptr || page_count <= 0 || format != MEGAPDF_WRITE_TEXT) {
        return MEGAPDF_ERR_ARGUMENT;
    }
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
            switch (opt.page_break) {
                case MEGAPDF_PAGE_BREAK_FORM_FEED: out += "\f\n"; break;
                case MEGAPDF_PAGE_BREAK_MARKER:
                    out += "\n--- page " + std::to_string(page_index + 1) + " ---\n\n";
                    break;
                case MEGAPDF_PAGE_BREAK_NONE: out += "\n"; break;
                default: break;
            }
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
                    const bool checkish = fi != nullptr && (fi->kind == MEGAPDF_FIELD_CHECKBOX || fi->kind == MEGAPDF_FIELD_RADIO);
                    line = checkish ? (std::string(fi->is_checked ? "[x] " : "[ ] ") + name) : (name + ": " + value);
                    break;
                }
                case MEGAPDF_BLOCK_FIGURE: {
                    // Alt text needs the tagged path (#358): every FIGURE this phase has none,
                    // so this is currently always a skip -- kept, ahead of that phase, so a
                    // figure never silently prints as empty text once alt text exists.
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
