import Foundation

// Contract 9 (#105–#112, #142/#353/#355/#357): document structure inference and the text/
// Markdown writer built over it. Bound here for #386 (Save As → Markdown): the same shape as
// the rest of PdfEngine.swift — opaque handles the core owns, count-then-fill strings, every
// call serialised through the engine actor.

/// One block from `megapdf_structure_load` (contract 9): a heading, paragraph, list item,
/// figure, page image, furniture line or form field, in reading order. `kind`/`source` are
/// `megapdf_block_kind`/`MEGAPDF_STRUCTURE_SOURCE_*` values, left as the core's own `Int32`
/// rather than re-declared as a Swift enum here — nothing in this file switches on them yet.
struct PdfBlock: Equatable {
    let kind: Int32
    let level: Int32
    let page: Int32
    let bounds: PdfRect
    let objectIndex: Int32
    let continuesPrevious: Bool
    let source: Int32
    let confidence: Int32
}

/// Opaque handle over `megapdf_structure`. Create via `PdfEngine.loadStructure`, free via
/// `PdfEngine.freeStructure` — the same explicit create/destroy shape `PdfDocument` uses rather
/// than a Swift `deinit`: every core call runs on the engine actor, which a deinit cannot hop to.
final class PdfStructure: @unchecked Sendable {
    fileprivate let core: OpaquePointer
    private(set) var isDestroyed = false

    fileprivate init(core: OpaquePointer) {
        self.core = core
    }

    fileprivate func destroy() {
        guard !isDestroyed else { return }
        isDestroyed = true
        megapdf_structure_free(core)
    }
}

/// `megapdf_write_text`'s `format` parameter.
enum PdfTextFormat: Int32 {
    case text = 0        // MEGAPDF_WRITE_TEXT
    case markdown = 1    // MEGAPDF_WRITE_MARKDOWN
}

/// `megapdf_write_options.page_break`.
enum PdfPageBreak: Int32 {
    /// U+000C between pages for `.text`; for `.markdown` this renders as a blank line —
    /// megapdf_write_text.cpp's `AppendPageSeparatorMarkdown` only special-cases `.marker`,
    /// so the core's own default already matches the GUI's "blank lines, not form-feed" default
    /// for Markdown (#386) with no extra option needed.
    case formFeed = 0
    case marker = 1      // MEGAPDF_PAGE_BREAK_MARKER
    case none = 2         // MEGAPDF_PAGE_BREAK_NONE
}

/// `megapdf_write_options.fields`.
enum PdfWriteFields: Int32 {
    case filled = 0   // MEGAPDF_WRITE_FIELDS_FILLED — a GUI Save-As's own default (#386)
    case all = 1      // MEGAPDF_WRITE_FIELDS_ALL
    case none = 2     // MEGAPDF_WRITE_FIELDS_NONE
}

/// Options for `PdfEngine.writeText`, defaulted for a GUI Save-As (#386's design note: the
/// CLI's audience wants flags, a Save As dialog does not) — filled fields, no furniture, the
/// PDF's own line breaks left unwrapped, and Markdown's blank-line page break. These are simply
/// `megapdf_write_options`'s zero values spelled out, so a future option a Save As screen DOES
/// want to expose has somewhere to go without re-guessing the core's defaults.
struct PdfTextWriteOptions {
    var keepLines = false
    var pageBreak: PdfPageBreak = .formFeed
    var keepFurniture = false
    var fields: PdfWriteFields = .filled
    var heuristicOnly = false
}

extension PdfEngine {
    /// Infers document structure over `[firstPage, firstPage + pageCount)` (contract 9).
    ///
    /// Not used by `writeText` below: that call makes its own, single contract-9 pass
    /// internally (megapdf_core.h's own comment on `megapdf_write_text`), and megapdf-cli's
    /// design note (core/cli/megapdf_cli.cpp) documents a real case where a second, separate
    /// `megapdf_structure_load` over the same range silently disagreed with the writer's own
    /// pass. This accessor is for a future direct reader of the blocks (an outline, a
    /// block-level search) rather than the Save-As-Markdown path.
    func loadStructure(_ document: PdfDocument, firstPage: Int, pageCount: Int,
                       flags: UInt32 = 0) throws -> PdfStructure {
        guard !document.isDestroyed,
              let handle = megapdf_structure_load(document.core, Int32(firstPage), Int32(pageCount), flags, nil)
        else { throw PdfError.pageLoad(index: firstPage) }
        return PdfStructure(core: handle)
    }

    func freeStructure(_ structure: PdfStructure) {
        structure.destroy()
    }

    func blockCount(_ structure: PdfStructure) -> Int {
        guard !structure.isDestroyed else { return 0 }
        return megapdf_block_count(structure.core)
    }

    func block(_ structure: PdfStructure, at index: Int) -> PdfBlock? {
        guard !structure.isDestroyed else { return nil }
        var raw = megapdf_block()
        guard megapdf_block_get(structure.core, index, &raw) == MEGAPDF_OK else { return nil }
        return PdfBlock(
            kind: raw.kind, level: raw.level, page: raw.page,
            bounds: PdfRect(left: raw.bounds.left, bottom: raw.bounds.bottom,
                            right: raw.bounds.right, top: raw.bounds.top),
            objectIndex: raw.object_index, continuesPrevious: raw.continues != 0,
            source: raw.source, confidence: raw.confidence)
    }

    /// `megapdf_block_string`'s `MEGAPDF_BLOCK_TEXT` field: the block's whole text, exactly the
    /// concatenation of its spans (SDD §6.2 contract 6).
    func blockText(_ structure: PdfStructure, at index: Int) -> String {
        blockString(structure, at: index, which: MEGAPDF_BLOCK_TEXT)
    }

    /// `megapdf_block_string`'s `MEGAPDF_BLOCK_MARKER` field: a list marker, or a FIELD's fully
    /// qualified name; `""` for a block with no marker.
    func blockMarker(_ structure: PdfStructure, at index: Int) -> String {
        blockString(structure, at: index, which: MEGAPDF_BLOCK_MARKER)
    }

    private func blockString(_ structure: PdfStructure, at index: Int, which: megapdf_block_field) -> String {
        guard !structure.isDestroyed else { return "" }
        let needed = megapdf_block_string(structure.core, index, which, nil, 0)
        guard needed > 0 else { return "" }
        var units = [UInt16](repeating: 0, count: needed)
        let written = units.withUnsafeMutableBufferPointer {
            megapdf_block_string(structure.core, index, which, $0.baseAddress, needed)
        }
        return String(utf16CodeUnits: units, count: written)
    }

    /// The whole document's text (or Markdown) through the core's contract-9 writer
    /// (`megapdf_write_text`, #142/#355/#357/#386), with `options`'s GUI Save-As defaults.
    /// `format` selects `MEGAPDF_WRITE_TEXT` or `MEGAPDF_WRITE_MARKDOWN`. UTF-8, LF, no BOM —
    /// byte-identical to what `megapdf-cli extract` writes for the same document and options
    /// (core/cli/megapdf_cli.cpp's own calling shape, ported here).
    func writeText(_ document: PdfDocument, format: PdfTextFormat,
                  options: PdfTextWriteOptions = PdfTextWriteOptions()) throws -> Data {
        guard !document.isDestroyed else { throw PdfError.textExportFailed }
        let pages = Int(megapdf_page_count(document.core))
        guard pages > 0 else { throw PdfError.textExportFailed }

        var wopt = megapdf_write_options()
        wopt.keep_lines = options.keepLines ? 1 : 0
        wopt.page_break = options.pageBreak.rawValue
        wopt.keep_furniture = options.keepFurniture ? 1 : 0
        wopt.fields = options.fields.rawValue
        wopt.heuristic_only = options.heuristicOnly ? 1 : 0

        final class Sink { var data = Data() }
        let sink = Sink()
        let write: megapdf_write_fn = { context, bytes, count in
            guard let context, let bytes, count > 0 else { return 1 }
            Unmanaged<Sink>.fromOpaque(context).takeUnretainedValue().data.append(Data(bytes: bytes, count: count))
            return 1
        }
        let status = withExtendedLifetime(sink) { () -> Int32 in
            megapdf_write_text(document.core, 0, Int32(pages), format.rawValue, &wopt, write,
                               Unmanaged.passUnretained(sink).toOpaque(), nil)
        }
        guard status >= 0 else { throw PdfError.textExportFailed }
        return sink.data
    }
}
