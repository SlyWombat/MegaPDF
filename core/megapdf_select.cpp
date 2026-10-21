// Contract 11: text selection (#710). See megapdf_core.h for the contract itself.
//
// The layout (characters in reading order, their lines, the XY-cut regions) is built by
// megapdf_structure.cpp, which owns the rules it is built by; this file is the queries over it.
// Every query reads only the snapshot, so none of them takes the core's lock: a pointer move
// during a drag never waits behind a render or a page check on another thread.

#include "megapdf_core.h"
#include "megapdf_core_internal.h"

#include <algorithm>
#include <cmath>
#include <limits>
#include <new>
#include <vector>

using megapdf_internal::SelectionChar;
using megapdf_internal::SelectionLayout;
using megapdf_internal::SelectionLine;

struct megapdf_selectable {
    SelectionLayout layout;
    // The bounding box of each XY-cut region, in the layout frame.
    std::vector<megapdf_rect> regions;
};

namespace {

// The distance from a point to a box, 0 inside it.
double Distance(const megapdf_rect& r, double x, double y) {
    const double dx = x < r.left ? r.left - x : (x > r.right ? x - r.right : 0.0);
    const double dy = y < r.bottom ? r.bottom - y : (y > r.top ? y - r.top : 0.0);
    return std::sqrt(dx * dx + dy * dy);
}

megapdf_rect BoxOf(const SelectionLine& l) { return megapdf_rect{l.l, l.b, l.r, l.t}; }

// The line a point belongs to, or -1 for a page with no text. Two steps, because the nearest
// glyph is the wrong answer exactly where it matters: a point past the end of a short line is
// nearer the longer line below it than the end of its own, and a point in a gutter is near both
// columns. So: first the XY-cut region (the one containing the point, else the nearest), then
// within it the line whose band the point is in (else the nearest band), horizontal distance
// breaking ties between lines that share a band.
int LineAt(const megapdf_selectable& s, double x, double y) {
    const auto& lines = s.layout.lines;
    if (lines.empty()) return -1;
    int region = -1;
    double best = std::numeric_limits<double>::infinity();
    for (size_t r = 0; r < s.regions.size(); r++) {
        const double d = Distance(s.regions[r], x, y);
        if (d < best) { best = d; region = static_cast<int>(r); }
    }
    int line = -1;
    double best_dy = std::numeric_limits<double>::infinity();
    double best_dx = best_dy;
    double best_dc = best_dy;
    for (size_t i = 0; i < lines.size(); i++) {
        const SelectionLine& l = lines[i];
        if (region >= 0 && l.region != region) continue;
        const double dy = y < l.b ? l.b - y : (y > l.t ? y - l.t : 0.0);
        const double dx = x < l.l ? l.l - x : (x > l.r ? x - l.r : 0.0);
        const double dc = std::fabs(y - (l.b + l.t) / 2.0);   // advance boxes can overlap a neighbour's band
        if (dy < best_dy || (dy == best_dy && (dx < best_dx || (dx == best_dx && dc < best_dc)))) {
            best_dy = dy; best_dx = dx; best_dc = dc;
            line = static_cast<int>(i);
        }
    }
    return line;
}

// The character on `line` nearest `x` along the advance.
int CharOnLine(const megapdf_selectable& s, int line, double x) {
    const SelectionLine& l = s.layout.lines[static_cast<size_t>(line)];
    int best = l.first;
    double best_d = std::numeric_limits<double>::infinity();
    for (int i = l.first; i < l.end; i++) {
        const SelectionChar& c = s.layout.chars[static_cast<size_t>(i)];
        const double d = x < c.l ? c.l - x : (x > c.r ? x - c.r : 0.0);
        if (d < best_d) { best_d = d; best = i; }
        if (d == 0.0) break;
    }
    return best;
}

// The line for a point, honouring the tolerance; -1 when there is none within it.
int LineWithin(const megapdf_selectable* s, double x, double y, double tolerance, double* lx, double* ly) {
    if (s == nullptr || s->layout.chars.empty()) return -1;
    if (std::isnan(x) || std::isnan(y) || std::isnan(tolerance)) return -1;
    megapdf_internal::CropToSelection(s->layout, x, y, lx, ly);
    const int line = LineAt(*s, *lx, *ly);
    if (line < 0) return -1;
    if (tolerance >= 0 && Distance(BoxOf(s->layout.lines[static_cast<size_t>(line)]), *lx, *ly) > tolerance) return -1;
    return line;
}

bool IsPunctuation(unsigned int cp) {
    if (cp < 0x80) return (cp >= 0x21 && cp <= 0x2F) || (cp >= 0x3A && cp <= 0x40) || (cp >= 0x5B && cp <= 0x60) ||
                          (cp >= 0x7B && cp <= 0x7E);
    switch (cp) {
        case 0x00A1: case 0x00A7: case 0x00AB: case 0x00AD: case 0x00B6: case 0x00B7: case 0x00BB: case 0x00BF:
            return true;
        default: break;
    }
    return (cp >= 0x2010 && cp <= 0x2027) || (cp >= 0x2030 && cp <= 0x205E) ||   // General Punctuation
           (cp >= 0x3001 && cp <= 0x3003) || (cp >= 0x3008 && cp <= 0x3011) ||   // CJK punctuation, brackets
           (cp >= 0xFF01 && cp <= 0xFF0F) || (cp >= 0xFF1A && cp <= 0xFF20) ||   // fullwidth forms
           (cp >= 0xFF3B && cp <= 0xFF40) || (cp >= 0xFF5B && cp <= 0xFF65);
}

void Normalise(const megapdf_selectable* s, int* start, int* end) {
    const int n = static_cast<int>(s->layout.chars.size());
    if (*start > *end) std::swap(*start, *end);
    *start = (std::max)(0, (std::min)(*start, n));
    *end = (std::max)(0, (std::min)(*end, n));
}

// What a character contributes to a copy, as UTF-16 code units.
void AppendCopied(std::vector<unsigned short>* out, unsigned int cp) {
    if (cp == 0x00AD) cp = 0x002D;   // a soft hyphen is drawn: PDF does not reflow, so it is on the page
    if (cp < 0x20 || (cp >= 0x7F && cp <= 0x9F) || cp == 0xFFFE || cp == 0xFFFF) return;   // controls draw nothing
    if (cp <= 0xFFFF) { out->push_back(static_cast<unsigned short>(cp)); return; }
    if (cp > 0x10FFFF) return;
    cp -= 0x10000;
    out->push_back(static_cast<unsigned short>(0xD800 + (cp >> 10)));
    out->push_back(static_cast<unsigned short>(0xDC00 + (cp & 0x3FF)));
}

}  // namespace

extern "C" {

MEGAPDF_API megapdf_selectable* megapdf_selectable_load(const megapdf_page* page) {
    if (page == nullptr) return nullptr;
    std::lock_guard<std::recursive_mutex> guard(megapdf_internal::Lock());
    if (megapdf_internal::PageClosed(page)) {
        megapdf_internal::ReportClosedPage();
        return nullptr;
    }
    auto* s = new (std::nothrow) megapdf_selectable();
    if (s == nullptr) {
        megapdf_internal::SetLastError(FPDF_ERR_UNKNOWN, "out of memory");
        return nullptr;
    }
    if (!megapdf_internal::BuildSelectionLayout(page, &s->layout)) {
        delete s;
        megapdf_internal::SetLastError(FPDF_ERR_UNKNOWN, "out of memory");
        return nullptr;
    }
    try {
        s->regions.assign(static_cast<size_t>(s->layout.regions), megapdf_rect{0, 0, 0, 0});
        std::vector<bool> seen(s->regions.size(), false);
        for (const SelectionLine& l : s->layout.lines) {
            if (l.region < 0 || static_cast<size_t>(l.region) >= s->regions.size()) continue;
            megapdf_rect& r = s->regions[static_cast<size_t>(l.region)];
            if (!seen[static_cast<size_t>(l.region)]) { r = BoxOf(l); seen[static_cast<size_t>(l.region)] = true; continue; }
            r.left = (std::min)(r.left, l.l); r.bottom = (std::min)(r.bottom, l.b);
            r.right = (std::max)(r.right, l.r); r.top = (std::max)(r.top, l.t);
        }
    } catch (const std::bad_alloc&) {
        delete s;
        megapdf_internal::SetLastError(FPDF_ERR_UNKNOWN, "out of memory");
        return nullptr;
    }
    return s;
}

MEGAPDF_API void megapdf_selectable_free(megapdf_selectable* s) { delete s; }

MEGAPDF_API int megapdf_selectable_length(const megapdf_selectable* s) {
    return s ? static_cast<int>(s->layout.chars.size()) : 0;
}

MEGAPDF_API int megapdf_selectable_caret_at(const megapdf_selectable* s, double x, double y, double tolerance) {
    double lx = 0, ly = 0;
    const int line = LineWithin(s, x, y, tolerance, &lx, &ly);
    if (line < 0) return -1;
    const int i = CharOnLine(*s, line, lx);
    const SelectionChar& c = s->layout.chars[static_cast<size_t>(i)];
    return lx < (c.l + c.r) / 2.0 ? i : i + 1;
}

MEGAPDF_API int megapdf_selectable_char_at(const megapdf_selectable* s, double x, double y, double tolerance) {
    double lx = 0, ly = 0;
    const int line = LineWithin(s, x, y, tolerance, &lx, &ly);
    if (line < 0) return -1;
    return CharOnLine(*s, line, lx);
}

MEGAPDF_API int megapdf_selectable_word_at(const megapdf_selectable* s, double x, double y, double tolerance,
                                           int* out_start, int* out_end) {
    if (s == nullptr) return MEGAPDF_ERR_ARGUMENT;
    if (out_start != nullptr) *out_start = 0;
    if (out_end != nullptr) *out_end = 0;
    double lx = 0, ly = 0;
    const int line = LineWithin(s, x, y, tolerance, &lx, &ly);
    if (line < 0) return 0;
    const int hit = CharOnLine(*s, line, lx);
    const auto& chars = s->layout.chars;
    const int word = chars[static_cast<size_t>(hit)].word;
    int ws = hit, we = hit + 1;
    while (ws > 0 && chars[static_cast<size_t>(ws - 1)].word == word) ws--;
    while (static_cast<size_t>(we) < chars.size() && chars[static_cast<size_t>(we)].word == word) we++;
    // Punctuation at either end is not part of the word ("column." selects "column"), unless
    // the point is on it; punctuation inside a word stays ("don't", "1,000", "e-mail").
    int a = ws, b = we;
    while (a < b && IsPunctuation(chars[static_cast<size_t>(a)].cp)) a++;
    while (b > a && IsPunctuation(chars[static_cast<size_t>(b - 1)].cp)) b--;
    int start = ws, end = we;
    if (a < b) {
        if (hit < a) { start = ws; end = a; }
        else if (hit >= b) { start = b; end = we; }
        else { start = a; end = b; }
    }
    if (out_start != nullptr) *out_start = start;
    if (out_end != nullptr) *out_end = end;
    return end - start;
}

MEGAPDF_API int megapdf_selectable_line_at(const megapdf_selectable* s, double x, double y, double tolerance,
                                           int* out_start, int* out_end) {
    if (s == nullptr) return MEGAPDF_ERR_ARGUMENT;
    if (out_start != nullptr) *out_start = 0;
    if (out_end != nullptr) *out_end = 0;
    double lx = 0, ly = 0;
    const int line = LineWithin(s, x, y, tolerance, &lx, &ly);
    if (line < 0) return 0;
    const SelectionLine& l = s->layout.lines[static_cast<size_t>(line)];
    if (out_start != nullptr) *out_start = l.first;
    if (out_end != nullptr) *out_end = l.end;
    return l.end - l.first;
}

MEGAPDF_API size_t megapdf_selectable_text(const megapdf_selectable* s, int start, int end, unsigned short* out,
                                           size_t capacity) {
    if (s == nullptr) return 0;
    Normalise(s, &start, &end);
    std::vector<unsigned short> text;
    try {
        text.reserve(static_cast<size_t>(end - start) + 16);
        const auto& chars = s->layout.chars;
        for (int i = start; i < end; i++) {
            const SelectionChar& c = chars[static_cast<size_t>(i)];
            if (i > start) {
                const SelectionChar& p = chars[static_cast<size_t>(i - 1)];
                if (c.line != p.line) text.push_back('\n');
                else if (c.word != p.word) text.push_back(' ');
            }
            AppendCopied(&text, c.cp);
        }
    } catch (const std::bad_alloc&) {
        return 0;
    }
    if (out != nullptr && capacity > 0) std::copy_n(text.begin(), (std::min)(capacity, text.size()), out);
    return text.size();
}

MEGAPDF_API size_t megapdf_selectable_rects(const megapdf_selectable* s, int start, int end, megapdf_rect* out,
                                            size_t capacity) {
    if (s == nullptr) return 0;
    Normalise(s, &start, &end);
    if (start == end) return 0;
    const auto& chars = s->layout.chars;
    size_t count = 0;
    const int first_line = chars[static_cast<size_t>(start)].line;
    const int last_line = chars[static_cast<size_t>(end - 1)].line;
    for (int li = first_line; li <= last_line; li++) {
        const SelectionLine& l = s->layout.lines[static_cast<size_t>(li)];
        const int a = (std::max)(start, l.first);
        const int b = (std::min)(end, l.end);
        if (a >= b) continue;
        double left = chars[static_cast<size_t>(a)].l, right = chars[static_cast<size_t>(a)].r;
        for (int i = a + 1; i < b; i++) {
            left = (std::min)(left, chars[static_cast<size_t>(i)].l);
            right = (std::max)(right, chars[static_cast<size_t>(i)].r);
        }
        if (out != nullptr && count < capacity) {
            out[count] = megapdf_internal::SelectionToCrop(s->layout, megapdf_rect{left, l.b, right, l.t});
        }
        count++;
    }
    return count;
}

}  // extern "C"
