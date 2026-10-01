import SwiftUI
import UniformTypeIdentifiers

// The page tools' screen (#174) — where pages are seen as pages and acted on: rotate, delete,
// reorder, insert a blank page, combine and extract, every one of them a single undo step.
//
// **Why a grid of pages, and why it arrives the way it does.**
//
// There is one grid here and two ways it comes on screen, because an iPhone and an iPad are not
// the same machine:
//
// * **Compact width (every iPhone, and an iPad in Slide Over) — a sheet with detents.** A sheet
//   is what iOS uses for a self-contained task over the thing you are looking at, and its
//   detents are the part that matters: at `.medium` the pages fill the lower half and *the page
//   you were reading is still there above them*, which is the thing Android could not have and
//   had to give up a whole screen for. Dragged to `.large` it is the full grid. It is not a
//   navigation destination: pushing onto the stack on iOS means going deeper into the content,
//   and rearranging the content is not deeper into it.
// * **Regular width (an iPad full screen or the wide half of a Split View) — a sidebar inside
//   the viewer.** An iPad in regular width really is closer to a desktop, and this is the one
//   answer iPadOS already has for structure beside content: the thumbnail sidebar Files' own
//   Quick Look and Books show, and which Preview (⌥⌘2) and Evince (F9) show on the machines
//   #556 shipped to. Beside the page, turning page 3 turns page 3 in front of you.
//
// **Why Select is a mode.** Both Android's grid and the desktops' strips select on a plain tap,
// because there is no other meaning a tap could have there. Here there is: this grid sits beside
// or over the document, so a tap on a thumbnail **goes to that page**, which is what a thumbnail
// has always been for. Picking pages to act on is therefore iOS's own Select mode — Photos,
// Files and Mail all work this way — and it earns its keep twice over, because "turn these six
// pages" then becomes one gesture and **one** undo step.
//
// **Why every command is also on a long press.** A tile's context menu is the surface iOS's own
// PDF markup puts Rotate Left, Rotate Right, Insert and Delete on, so it is where a hand already
// looks; it is also the only route to a command for someone who has not found Select, and on an
// iPad with a pointer it is the right-click menu. Nothing is *only* in the context menu.
//
// **Reordering has three ways in, on purpose.** A long-press drag is what a finger expects, and
// it is the only one that is unusable with VoiceOver and impossible without a touchscreen — so
// every tile also carries *Move Earlier* and *Move Later* as custom accessibility actions and as
// menu rows, and *Move to…* takes a typed position, which is this platform's answer to the
// desktops' cut and paste (#2) and to Android's own *Move to…*.

/// How the pages are on screen. The grid is the same; the furniture around it is not.
enum PagesPresentation: Equatable {
    /// A sheet over the document, compact width.
    case sheet
    /// A sidebar beside the document, regular width.
    case sidebar
}

/// Where a dragged page lands.
///
/// Pure, and pulled out of the view for one reason: this arithmetic is where a reorder goes
/// wrong, and it is wrong in a way a screenshot cannot show. `movePage(from:to:)` speaks in
/// "the index the page stands at afterwards", and the index a drop *points* at is not that
/// index — dropping page 1 after page 3 lands it at 3, not 4, because taking it out first
/// closes the gap behind it. `PageDropTests` drives all four directions.
enum PageDrop {
    /// The `to` index for `movePage(from:to:)` when the page at `from` is dropped on the tile
    /// showing page `over`, on its leading half (`before: true`) or its trailing half.
    ///
    /// Answers `from` itself for a drop that asks for no change, which the caller reads as a
    /// no-op rather than putting one on the undo stack.
    static func destination(dragging from: Int, onto over: Int, before: Bool) -> Int {
        if from == over { return from }
        if before { return from < over ? over - 1 : over }
        return from < over ? over : over + 1
    }

    /// Whether a drop at `x` points **in front of** the tile it landed on.
    ///
    /// Its own function, and tested, because the width it divides is the one thing here that is
    /// *measured* rather than reasoned about: the grid's columns are adaptive, so a hard-coded
    /// guess puts the midpoint in the wrong place and a drop near the middle of a tile goes the
    /// wrong way.
    ///
    /// A width of zero or less is **defined rather than divided**: it answers "before", which is
    /// the half a drop on a tile's leading edge belongs to. The panel never hands one over — it
    /// starts from a sensible default and only replaces it with a positive measurement — but a
    /// total function is one fewer thing for the next caller to get right, and `0 < 0` silently
    /// sending every drop behind its tile is exactly the kind of answer this file is trying not
    /// to have.
    static func isBefore(dropX x: CGFloat, tileWidth: CGFloat) -> Bool {
        guard tileWidth > 0 else { return true }
        return x < tileWidth / 2
    }
}

/// Whether a page tile offers itself as a drag source.
///
/// It always does, except under `-uiTestNoTileDrag`, and that lever exists for a measured
/// reason (#599) rather than for convenience. A tile is both a drag source (`.onDrag`) and a
/// context-menu host (`.contextMenu`), and a *synthesised* long press leaves the drag in
/// flight: XCUITest then never sees the app quiescent for as long as the menu is up, and waits
/// out its full sixty-second idle timeout on **every event** in that window. Measured on the
/// Mac mini against an iPad Pro 13-inch: the long press that opens a tile's menu returns in
/// 61.5 s and the tap on a menu row in 61.1 s, against 1.9 s and 1.1 s with `.onDrag` absent —
/// 122 of the 135 seconds each of the three menu tests in `PageToolsUITests` costs. Worse than
/// the time, those are two events dispatched into an app XCUITest has *given up* waiting for,
/// which is the window #599's reds live in: a tap that does not take, read back afterwards as
/// "the app never reported it".
///
/// Nothing is lost by turning it off there. Dragging a tile is already a by-hand gate on this
/// platform (#570, `PageDragUITests`, `docs/qa/test-matrix.md`) — a synthesised drag cannot
/// express the movement that tells a drag from a long press — so no CI lane drives it either
/// way, and `PageToolsUITests` says in its own first paragraph that drag-to-reorder is not its
/// business. The suite whose business it is passes no flag and gets the real thing.
enum PageTileDrag {
    /// Whether the tiles are drag sources, given a process's launch arguments.
    static func isADragSource(arguments: [String]) -> Bool {
        !arguments.contains("-uiTestNoTileDrag")
    }

    /// This process's answer, read once: a launch argument cannot change under a running app,
    /// and this is read from a view body.
    static let inThisProcess = isADragSource(arguments: ProcessInfo.processInfo.arguments)
}

/// `.onDrag`, unless this process is a UI test that asked for the tile not to be a drag source.
///
/// A modifier rather than an `if` around the tile, so that everything hung on the tile after it
/// — the drop target, the accessibility actions, the identifier — is written once and applies
/// either way.
private struct TileDragSource: ViewModifier {
    let index: Int
    let onLift: () -> Void

    @ViewBuilder
    func body(content: Content) -> some View {
        if PageTileDrag.inThisProcess {
            content.onDrag {
                onLift()
                return NSItemProvider(object: "\(index)" as NSString)
            }
        } else {
            content
        }
    }
}

/// The pages, and everything that can be done to them.
struct PagesPanel: View {
    @ObservedObject var model: ViewerModel
    let documentName: String
    let presentation: PagesPresentation
    let onDismiss: () -> Void

    /// Which tiles the grid has realised, so the thumbnail window is the same answer the grid is
    /// laying out from rather than a guess at it.
    @State private var visible: Set<Int> = []
    @State private var importing = false
    @State private var moveToPage: Int?
    @State private var moveToText = ""
    /// The page a drag is carrying, so the tile it came from can say so.
    @State private var dragging: Int?
    /// How wide a tile actually came out. Every tile in an adaptive grid is the same width, so one
    /// value is enough — and it has to be measured rather than assumed, because which *half* of a
    /// tile a finger let go over is the whole of what a drop means.
    @State private var tileWidth: CGFloat = 120

    private var pageCount: Int { model.pageCount }

    /// What a command acts on: the selection, or — outside Select mode, from a tile's own menu —
    /// the one page it was aimed at.
    private func targets(_ page: Int) -> [Int] {
        if model.pagesSelecting, !model.pageSelection.isEmpty, model.pageSelection.contains(page) {
            return model.pageSelection.sorted()
        }
        return [page]
    }

    private var selection: [Int] { model.pageSelection.sorted() }

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            grid
            if model.pagesSelecting {
                Divider()
                selectionBar
            }
        }
        .background(panelBackground)
        .onPreferenceChange(PageTileWidthKey.self) { width in
            if width > 0 { tileWidth = width }
        }
        // Deliberately no identifier on this container. `accessibilityIdentifier` on a SwiftUI
        // container **replaces** the identifier of every descendant that is not itself an
        // accessibility element — an identifier here made every button inside read as
        // "pagesPanel", which is what `viewerToolStrip` has been doing to its own buttons since
        // #172. Each control names itself, and the tests recognise the panel by what is in it
        // (and by where it is: the sidebar's tiles are beside the page, the sheet's below it).
        // A PDF, copied into this app's container before a page of it is read: contract 10 keeps
        // the other document open inside this one, and a picked URL is not readable that long.
        .fileImporter(isPresented: $importing, allowedContentTypes: [.pdf]) { result in
            guard case let .success(url) = result else { return }
            model.importPages(from: url)
        }
        // The export sheet is presented from here rather than from the viewer underneath, because
        // in compact width this panel *is* a sheet and a presentation attached below it would
        // have nothing to present from.
        .fileExporter(
            isPresented: Binding(get: { model.pageExport != nil },
                                 set: { if !$0 { model.pageExport = nil } }),
            document: model.pageExport.map { StagedExportDocument(file: $0.url) },
            contentType: .pdf,
            defaultFilename: model.pageExport?.defaultName
        ) { result in
            if case .success = result {
                model.finishPageExport(saved: true)
            } else {
                model.finishPageExport(saved: false)
            }
        }
        // A refusal, in one sentence saying what happened and that nothing was changed (#174).
        //
        // Presented here only for the sheet, which an alert attached to the viewer underneath
        // could never appear over; in regular width the sidebar is not modal and `ViewerView`
        // presents the same value. The two conditions are complementary, so the question is never
        // up twice — the shape #173 settled on for the redaction question.
        .alert("Nothing was changed",
               isPresented: Binding(get: { model.pageToolRefusal != nil && presentation == .sheet },
                                    set: { if !$0 { model.pageToolRefusal = nil } })) {
            Button("OK", role: .cancel) { model.pageToolRefusal = nil }
        } message: {
            Text(model.pageToolRefusal ?? "")
        }
        // One number, and the page behind it should stay visible while it is asked for — the same
        // shape as the reading bar's *Go to page* (#506).
        .alert("Move to…", isPresented: Binding(get: { moveToPage != nil },
                                                set: { if !$0 { moveToPage = nil } })) {
            TextField("Page", text: $moveToText)
                .keyboardType(.numberPad)
                .accessibilityIdentifier("pagesMoveToField")
            Button("Move") {
                if let from = moveToPage,
                   let wanted = Int(moveToText.trimmingCharacters(in: .whitespaces)),
                   (1...max(pageCount, 1)).contains(wanted) {
                    model.movePage(from: from, to: wanted - 1)
                }
                moveToPage = nil
            }
            Button("Cancel", role: .cancel) { moveToPage = nil }
        } message: {
            Text("Page \((moveToPage ?? 0) + 1) goes to the position you type.")
        }
    }

    // MARK: - the furniture

    private var header: some View {
        HStack(spacing: 12) {
            Text("Pages")
                .font(Brand.Text.subtitle)
                .accessibilityAddTraits(.isHeader)
            Text(pageCount == 1
                 ? String(localized: "1 page")
                 : String(localized: "\(pageCount) pages"))
                .font(Brand.Text.caption)
                .foregroundStyle(.secondary)
            Spacer(minLength: 8)
            // Undo and Redo, but only on the sheet. In regular width the iPad's own tool strip
            // (#172) is right above the sidebar and already carries them; in compact width this
            // sheet is *over* the bottom toolbar that carries them, and a rotation you cannot
            // take back without first putting the sheet away is not one undo step in any useful
            // sense.
            if presentation == .sheet {
                Button(action: model.undo) {
                    Image(systemName: "arrow.uturn.backward")
                }
                .disabled(!model.canUndo || model.fileCommandsBlocked)
                .accessibilityLabel("Undo")
                .accessibilityIdentifier("pagesUndo")
                Button(action: model.redo) {
                    Image(systemName: "arrow.uturn.forward")
                }
                .disabled(!model.canRedo || model.fileCommandsBlocked)
                .accessibilityLabel("Redo")
                .accessibilityIdentifier("pagesRedo")
            }
            Menu {
                Button("Insert Blank Page") {
                    model.insertBlankPage(at: (model.pageSelection.max() ?? (pageCount - 1)) + 1)
                }
                .disabled(!model.canAssemblePages || model.fileCommandsBlocked)
                Button("Insert Pages from File…") { importing = true }
                    .disabled(!model.canAssemblePages || model.fileCommandsBlocked)
                    .accessibilityIdentifier("pagesInsertFromFile")
                Divider()
                Button("Save Pages As…") {
                    model.startPageExport(pages: selection, documentName: documentName)
                }
                .disabled(!model.canExtractPages || model.fileCommandsBlocked)
            } label: {
                Image(systemName: "plus")
            }
            .accessibilityLabel("Add pages")
            .accessibilityIdentifier("pagesAdd")
            Button(model.pagesSelecting ? "Done" : "Select") {
                model.setPagesSelecting(!model.pagesSelecting)
            }
            .accessibilityIdentifier("pagesSelect")
            if presentation == .sheet {
                Button(action: onDismiss) {
                    Image(systemName: "xmark.circle.fill")
                        .foregroundStyle(.secondary)
                }
                .accessibilityLabel("Close")
                .accessibilityIdentifier("pagesClose")
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
        .modifier(SidebarInk(active: presentation == .sidebar))
    }

    /// The sidebar sits on the same dark wall as the iPad's tool strip above it, so the two read
    /// as one piece of chrome (#172); the sheet is a sheet and keeps the system's own ground,
    /// light or dark as the person has it.
    @ViewBuilder
    private var panelBackground: some View {
        if presentation == .sidebar {
            Brand.backdrop
        } else {
            Color(uiColor: .systemBackground)
        }
    }

    private var grid: some View {
        ScrollView {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 96, maximum: 150), spacing: 12)],
                      spacing: 14) {
                ForEach(Array(0..<pageCount), id: \.self) { index in
                    tile(index)
                        .onAppear {
                            visible.insert(index)
                            pushThumbnailWindow()
                        }
                        .onDisappear {
                            visible.remove(index)
                            pushThumbnailWindow()
                        }
                }
            }
            .padding(16)
            // Said once, where a finger can read it, rather than as a hint on every tile.
            Text("Hold a page to move it")
                .font(Brand.Text.caption)
                .foregroundStyle(.secondary)
                .padding(.bottom, 16)
                .accessibilityHidden(true)
        }
        .modifier(SidebarInk(active: presentation == .sidebar))
    }

    private var selectionBar: some View {
        HStack(spacing: 0) {
            Text(selection.isEmpty
                 ? String(localized: "Select pages")
                 : String(localized: "\(selection.count) selected"))
                .font(Brand.Text.caption)
                .foregroundStyle(.secondary)
                .padding(.leading, 16)
                .accessibilityIdentifier("pagesSelectionCount")
            Spacer(minLength: 8)
            Button { model.rotatePages(selection, quarterTurns: -1) } label: {
                Image(systemName: "rotate.left")
            }
            .accessibilityLabel("Rotate Left")
            .accessibilityIdentifier("pagesRotateLeft")
            Button { model.rotatePages(selection, quarterTurns: 1) } label: {
                Image(systemName: "rotate.right")
            }
            .accessibilityLabel("Rotate Right")
            .accessibilityIdentifier("pagesRotateRight")
            Button { model.startPageExport(pages: selection, documentName: documentName) } label: {
                Image(systemName: "square.and.arrow.down")
            }
            .disabled(!model.canExtractPages)
            .accessibilityLabel("Save Pages As…")
            .accessibilityIdentifier("pagesExport")
            Menu {
                Button("Move Earlier") { moveSelection(by: -1) }
                Button("Move Later") { moveSelection(by: 1) }
                Button("Move to…") { askMoveTo(selection.first) }
                Divider()
                Button("Select All", action: model.selectAllPages)
                Button("Insert Blank Page") {
                    model.insertBlankPage(at: (model.pageSelection.max() ?? (pageCount - 1)) + 1)
                }
            } label: {
                Image(systemName: "ellipsis.circle")
            }
            .accessibilityLabel("More page commands")
            .accessibilityIdentifier("pagesMore")
            Button(role: .destructive) { model.deletePages(selection) } label: {
                Image(systemName: "trash")
            }
            .accessibilityLabel("Delete")
            .accessibilityIdentifier("pagesDelete")
            .padding(.trailing, 16)
        }
        .buttonStyle(.borderless)
        .labelStyle(.iconOnly)
        // One `disabled` over the row rather than six: with nothing picked there is nothing any
        // of these could act on, and a row of live buttons that quietly do nothing is what the
        // other three legs each had to fix. Extract has a second condition of its own above,
        // because it asks a different permission.
        .disabled(selection.isEmpty || !model.canAssemblePages || model.fileCommandsBlocked)
        .padding(.vertical, 10)
        .background(.bar)
    }

    // MARK: - one tile

    @ViewBuilder
    private func tile(_ index: Int) -> some View {
        let selected = model.pageSelection.contains(index)
        VStack(spacing: 4) {
            ZStack(alignment: .topTrailing) {
                thumbnail(index)
                if model.pagesSelecting {
                    Image(systemName: selected ? "checkmark.circle.fill" : "circle")
                        .foregroundStyle(selected ? Brand.accent : .secondary)
                        .background(Circle().fill(.background))
                        .padding(4)
                }
            }
            Text("Page \(index + 1)")
                .font(Brand.Text.caption)
                .foregroundStyle(presentation == .sidebar ? .white : .primary)
        }
        .opacity(dragging == index ? 0.4 : 1)
        .background {
            GeometryReader { geo in
                Color.clear.preference(key: PageTileWidthKey.self, value: geo.size.width)
            }
        }
        .contentShape(Rectangle())
        .onTapGesture {
            if model.pagesSelecting {
                model.togglePageSelection(index)
            } else {
                // Outside Select mode a thumbnail is a way to the page, which is what a
                // thumbnail is for. On the sheet that also means putting the sheet away: the
                // page it has just scrolled to is behind it.
                model.showPage(index)
                if presentation == .sheet { onDismiss() }
            }
        }
        .contextMenu { tileMenu(index) }
        // A long press picks the tile up. `NSItemProvider` carries the page number as text,
        // which is all a drop needs: both ends of this drag are this grid. Through
        // `TileDragSource` rather than `.onDrag` directly, for the reason written there (#599).
        .modifier(TileDragSource(index: index) { dragging = index })
        .onDrop(of: [UTType.plainText], isTargeted: nil) { providers, location in
            drop(providers, at: location, onto: index)
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("Page \(index + 1)")
        .accessibilityAddTraits(.isButton)
        .accessibilityAddTraits(model.pagesSelecting && selected ? .isSelected : [])
        // The two reorder commands a screen reader can actually reach: a drag cannot be
        // performed by VoiceOver, and these are the same one-step moves the menu offers.
        .accessibilityAction(named: Text("Move Earlier")) {
            model.movePage(from: index, to: index - 1)
        }
        .accessibilityAction(named: Text("Move Later")) {
            model.movePage(from: index, to: index + 1)
        }
        .accessibilityIdentifier("pageTile-\(index)")
    }

    @ViewBuilder
    private func thumbnail(_ index: Int) -> some View {
        let sizes = model.pageSizes
        let size = sizes.indices.contains(index) ? sizes[index] : CGSize(width: 612, height: 792)
        let selected = model.pageSelection.contains(index)
        Group {
            if let image = model.pageThumbnails[index] {
                Image(uiImage: UIImage(cgImage: image))
                    .resizable()
                    .interpolation(.high)
            } else {
                Color.white
            }
        }
        .aspectRatio(size.width / max(size.height, 1), contentMode: .fit)
        .overlay {
            RoundedRectangle(cornerRadius: 4)
                .stroke(selected ? Brand.accent : Color.secondary.opacity(0.4),
                        lineWidth: selected ? 3 : 1)
        }
        .clipShape(RoundedRectangle(cornerRadius: 4))
        .shadow(radius: 1, y: 1)
    }

    @ViewBuilder
    private func tileMenu(_ index: Int) -> some View {
        let pages = targets(index)
        Button("Rotate Left") { model.rotatePages(pages, quarterTurns: -1) }
            .disabled(!model.canAssemblePages)
        Button("Rotate Right") { model.rotatePages(pages, quarterTurns: 1) }
            .disabled(!model.canAssemblePages)
        Divider()
        Button("Move Earlier") { model.movePage(from: index, to: index - 1) }
            .disabled(!model.canAssemblePages || index == 0)
        Button("Move Later") { model.movePage(from: index, to: index + 1) }
            .disabled(!model.canAssemblePages || index >= pageCount - 1)
        Button("Move to…") { askMoveTo(index) }
            .disabled(!model.canAssemblePages || pageCount < 2)
        Divider()
        Button("Insert Blank Page") { model.insertBlankPage(at: index + 1) }
            .disabled(!model.canAssemblePages)
        Button("Insert Pages from File…") { importing = true }
            .disabled(!model.canAssemblePages)
        Button("Save Pages As…") {
            model.startPageExport(pages: pages, documentName: documentName)
        }
        .disabled(!model.canExtractPages)
        Divider()
        Button("Delete", role: .destructive) { model.deletePages(pages) }
            .disabled(!model.canAssemblePages)
    }

    // MARK: - the gestures' arithmetic

    private func drop(_ providers: [NSItemProvider], at location: CGPoint, onto index: Int) -> Bool {
        let carried = dragging
        dragging = nil
        guard let from = carried, from != index else { return false }
        // The tile is one grid cell wide; which half of it the finger let go over is what says
        // whether the page goes in front of this one or behind it.
        let before = PageDrop.isBefore(dropX: location.x, tileWidth: tileWidth)
        let to = PageDrop.destination(dragging: from, onto: index, before: before)
        guard to != from else { return false }
        model.movePage(from: from, to: to)
        return true
    }

    private func moveSelection(by delta: Int) {
        guard let page = delta < 0 ? selection.first : selection.last else { return }
        model.movePage(from: page, to: page + delta)
    }

    private func askMoveTo(_ page: Int?) {
        guard let page else { return }
        moveToText = ""
        moveToPage = page
    }

    private func pushThumbnailWindow() {
        guard let first = visible.min(), let last = visible.max() else { return }
        model.updateThumbnailWindow(first: first, last: last)
    }
}

/// White ink on the sidebar's dark wall, and nothing at all on the sheet.
///
/// A modifier rather than `.environment(\.colorScheme, …)` chosen per branch: forcing the light
/// scheme on the sheet would have been the sheet refusing the person's own dark mode, and forcing
/// dark on it would have been the same mistake the other way. The sidebar is a different case —
/// its ground is `Brand.backdrop` whatever the appearance, exactly as the navigation bar's is
/// (`ViewerView`'s `.toolbarColorScheme(.dark, …)`), so the scheme follows the wall rather than
/// the system.
private struct SidebarInk: ViewModifier {
    let active: Bool

    func body(content: Content) -> some View {
        if active {
            content.environment(\.colorScheme, .dark)
        } else {
            content
        }
    }
}

/// How wide a tile came out, reported by the `GeometryReader` behind it. The grid's columns are
/// adaptive, so this is measured rather than assumed: a hard-coded guess puts the midpoint of a
/// tile in the wrong place, and the midpoint is what decides whether a dropped page goes in front
/// of that tile or behind it.
private struct PageTileWidthKey: PreferenceKey {
    static var defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        let next = nextValue()
        if next > 0 { value = next }
    }
}
