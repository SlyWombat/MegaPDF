import PhotosUI
import SwiftUI

/// The signature library (#100): the ink itself on white cards, tap to place,
/// long-press or the card's menu for Rename and Delete, and two equal buttons to
/// add another.
///
/// A card grid rather than a list of names, because the user is choosing a
/// squiggle, not a label; and a half-height sheet so the page they are about to
/// sign stays in view above it. The same design as the Android sheet (#99) and
/// the desktop flyouts, in this platform's idiom.
struct SignaturesSheet: View {
    let signatures: [SignatureEntry]
    var startDrawing = false
    /// Reads a signature's stored image. Called off the main actor; file I/O only.
    let loadImage: @Sendable (SignatureEntry) -> CGImage?
    let onPick: (SignatureEntry) -> Void
    let onDrawn: (CGImage) -> Void
    let onPhoto: (Data) -> Void
    let onRename: (String, String) -> Void
    let onDelete: (String) -> Void
    let onDismiss: () -> Void

    @State private var drawing = false
    @State private var typing = false
    @State private var photoItem: PhotosPickerItem?
    @State private var thumbnails: [String: UIImage] = [:]
    @State private var pendingDelete: SignatureEntry?
    @State private var renaming: SignatureEntry?
    @State private var renameText = ""
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize

    private let columns = [GridItem(.adaptive(minimum: 150, maximum: 240), spacing: 12)]

    var body: some View {
        NavigationStack {
            ScrollView {
                if signatures.isEmpty {
                    emptyState
                } else {
                    LazyVGrid(columns: columns, spacing: 12) {
                        ForEach(signatures) { entry in
                            card(for: entry)
                        }
                    }
                    .padding(.horizontal, 16)
                    .padding(.top, 8)
                }
            }
            .background(Self.surface)
            .safeAreaInset(edge: .bottom) { addRow }
            .navigationTitle("Signatures")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .navigationBarLeading) {
                    Button("Close", action: onDismiss)
                }
            }
            .confirmationDialog(
                Text("Delete \(pendingDelete?.displayName ?? "")?"),
                isPresented: Binding(get: { pendingDelete != nil }, set: { if !$0 { pendingDelete = nil } }),
                titleVisibility: .visible
            ) {
                Button("Delete", role: .destructive) {
                    if let entry = pendingDelete { onDelete(entry.id) }
                    pendingDelete = nil
                }
                Button("Cancel", role: .cancel) { pendingDelete = nil }
            } message: {
                Text("This cannot be undone.")
            }
            .alert(
                "Rename signature",
                isPresented: Binding(get: { renaming != nil }, set: { if !$0 { renaming = nil } })
            ) {
                TextField("Name", text: $renameText)
                    .autocorrectionDisabled()
                Button("Save") {
                    let name = renameText.trimmingCharacters(in: .whitespacesAndNewlines)
                    if let entry = renaming, !name.isEmpty, name != entry.displayName {
                        onRename(entry.id, name)
                    }
                    renaming = nil
                }
                Button("Cancel", role: .cancel) { renaming = nil }
            }
            .onChange(of: photoItem) { item in
                guard let item else { return }
                Task {
                    if let data = try? await item.loadTransferable(type: Data.self) {
                        onPhoto(data)
                    }
                    photoItem = nil
                }
            }
            .sheet(isPresented: $drawing) {
                DrawSignatureView { image in onDrawn(image) }
            }
            .sheet(isPresented: $typing) {
                // A typed name takes the drawn path (#101): ink on a transparent
                // raster that the store trims and keeps like any other.
                TypeSignatureView { image in onDrawn(image) }
            }
            .onAppear {
                if startDrawing { drawing = true }
            }
            .task(id: signatures.map(\.id)) { await loadThumbnails() }
        }
        .presentationDetents([.medium, .large])
        .presentationDragIndicator(.visible)
        .modifier(SolidSheetBackground(color: Self.surface))
    }

    /// One flat surface for the sheet, its bar and its add row, so nothing behind
    /// the sheet (the page's own signature, say) shows through the glass.
    private static let surface = Color(.systemGroupedBackground)

    // MARK: - pieces

    private var emptyState: some View {
        VStack(spacing: 12) {
            Image(systemName: "signature")
                .font(.system(size: 34, weight: .light))
                .foregroundStyle(.secondary)
            Text("No signatures yet. Draw one with your finger, or add a photo of your signature on white paper — the background is removed automatically.")
                .font(.subheadline)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity)
        .padding(24)
        .background(Color(.secondarySystemGroupedBackground),
                    in: RoundedRectangle(cornerRadius: 16, style: .continuous))
        .padding(.horizontal, 16)
        .padding(.top, 8)
    }

    private func card(for entry: SignatureEntry) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Button {
                onPick(entry)
            } label: {
                ZStack {
                    // White on purpose in both appearances: it is ink on paper, and
                    // the placed signature lands on a white page.
                    RoundedRectangle(cornerRadius: 12, style: .continuous)
                        .fill(Color.white)
                    RoundedRectangle(cornerRadius: 12, style: .continuous)
                        .strokeBorder(Color.primary.opacity(0.12), lineWidth: 1)
                    if let image = thumbnails[entry.id] {
                        Image(uiImage: image)
                            .resizable()
                            .scaledToFit()
                            .padding(12)
                    } else {
                        ProgressView().tint(.gray)
                    }
                }
                .frame(height: 88)
                .contentShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
            }
            .buttonStyle(.plain)
            .contextMenu { cardActions(entry) }
            .accessibilityLabel(Text("\(entry.displayName), signature"))
            .accessibilityHint(Text("Places it on the page"))
            .accessibilityAction(named: Text("Rename")) { beginRename(entry) }
            .accessibilityAction(named: Text("Delete")) { pendingDelete = entry }

            HStack(spacing: 4) {
                Text(entry.displayName)
                    .font(.subheadline.weight(.medium))
                    .foregroundStyle(.primary)
                    // A second line rather than "Mega W." becoming "Me…" at a
                    // large text size (#167).
                    .lineLimit(2)
                Spacer(minLength: 0)
                Menu {
                    cardActions(entry)
                } label: {
                    Image(systemName: "ellipsis.circle")
                        .font(.body)
                        .foregroundStyle(.secondary)
                        .frame(width: 32, height: 32)
                        .contentShape(Rectangle())
                }
                .tint(.secondary)
                .accessibilityLabel(Text("More options for \(entry.displayName)"))
            }
            .padding(.horizontal, 4)
        }
    }

    @ViewBuilder
    private func cardActions(_ entry: SignatureEntry) -> some View {
        Button { beginRename(entry) } label: { Label("Rename", systemImage: "pencil") }
        Button(role: .destructive) { pendingDelete = entry } label: { Label("Delete", systemImage: "trash") }
    }

    private var addRow: some View {
        // Three across stops working at an accessibility text size: a third of a
        // phone's width cannot hold "Dessiner" at AX5, and the labels were being
        // truncated to a single letter — "D…", "T…", "P…" — which tells you
        // nothing about what the button does. Above those sizes the three go in a
        // column instead, one full-width button each, icon beside the label.
        let stacked = dynamicTypeSize.isAccessibilitySize
        let layout = stacked ? AnyLayout(VStackLayout(spacing: 12)) : AnyLayout(HStackLayout(spacing: 12))
        return layout {
            Button { drawing = true } label: {
                Label("Draw", systemImage: "pencil.tip")
                    .frame(maxWidth: .infinity)
            }
            Button { typing = true } label: {
                Label("Type", systemImage: "keyboard")
                    .frame(maxWidth: .infinity)
            }
            PhotosPicker(selection: $photoItem, matching: .images) {
                Label("Photo", systemImage: "photo")
                    .frame(maxWidth: .infinity)
            }
        }
        .buttonStyle(.borderedProminent)
        .controlSize(.large)
        // Icon above the label: three of these share a phone's width in French too
        // ("Dessiner" / "Taper" / "Photo"), where the default row layout hyphenated
        // the first word. In a column there is room for the ordinary side-by-side
        // label, so the stacked style is only for the row.
        .labelStyle(StackedLabelStyle(stacked: !stacked))
        .padding(.horizontal, 16)
        .padding(.top, 12)
        .padding(.bottom, 8)
        .background(Self.surface)
    }

    private func beginRename(_ entry: SignatureEntry) {
        renameText = entry.displayName
        renaming = entry
    }

    /// Decodes the PNGs off the main actor; only the ids not yet cached.
    private func loadThumbnails() async {
        let missing = signatures.filter { thumbnails[$0.id] == nil }
        guard !missing.isEmpty else { return }
        let load = loadImage
        let decoded: [(String, UIImage)] = await Task.detached(priority: .userInitiated) {
            missing.compactMap { entry in
                load(entry).map { (entry.id, UIImage(cgImage: $0)) }
            }
        }.value
        for (id, image) in decoded { thumbnails[id] = image }
    }
}

/// `presentationBackground` arrived in 16.4; on 16.0–16.3 the sheet keeps the
/// system material, which is only a cosmetic bleed-through, not a functional loss.
private struct SolidSheetBackground: ViewModifier {
    let color: Color

    func body(content: Content) -> some View {
        if #available(iOS 16.4, *) {
            content.presentationBackground(color)
        } else {
            content
        }
    }
}

/// Finger/stylus capture: strokes over a light canvas, rendered to a
/// transparent CGImage (trim-only cleanup applies downstream).
struct DrawSignatureView: View {
    let onSave: (CGImage) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var strokes: [[CGPoint]] = []
    @State private var current: [CGPoint] = []
    @State private var canvasSize: CGSize = .zero
    private let screenshotMode = DemoContent.requestedState == "draw"

    // SDD §6.2 fixes the user's mark at #202020. This was 0.10 — #1A1A1A — so a
    // signature and a check mark on the same page came out in different inks.
    private let inkColor = Color(red: Brand.inkLevel, green: Brand.inkLevel, blue: Brand.inkLevel)

    var body: some View {
        NavigationStack {
            VStack {
                ZStack {
                    Canvas { context, size in
                        let width = max(size.height / 36, 3)
                        for points in strokes + [current] where points.count > 1 {
                            var path = Path()
                            path.move(to: points[0])
                            for p in points.dropFirst() { path.addLine(to: p) }
                            context.stroke(path, with: .color(inkColor),
                                           style: StrokeStyle(lineWidth: width,
                                                              lineCap: .round, lineJoin: .round))
                        }
                    }
                    // Screenshot mode shows the demo signature as if just drawn.
                    if screenshotMode, let sig = DemoContent.signatureImage() {
                        Image(uiImage: UIImage(cgImage: sig))
                            .resizable()
                            .scaledToFit()
                            .padding(28)
                    }
                }
                .background(Brand.signaturePad)
                .frame(height: 220)
                .gesture(
                    DragGesture(minimumDistance: 0)
                        .onChanged { value in
                            canvasSize = CGSize(width: UIScreen.main.bounds.width, height: 220)
                            current.append(value.location)
                        }
                        .onEnded { _ in
                            if current.count > 1 { strokes.append(current) }
                            current = []
                        }
                )
                Spacer()
            }
            .navigationTitle("Draw your signature")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .navigationBarLeading) {
                    Button("Cancel") { dismiss() }
                }
                ToolbarItemGroup(placement: .navigationBarTrailing) {
                    Button("Clear") { strokes = []; current = [] }
                    Button("Save") {
                        if let image = renderStrokes() { onSave(image) }
                        dismiss()
                    }
                    .disabled(strokes.isEmpty && !screenshotMode)
                }
            }
        }
    }

    private func renderStrokes() -> CGImage? {
        let scale = UIScreen.main.scale
        let w = Int(max(canvasSize.width, 1) * scale)
        let h = Int(220 * scale)
        guard let ctx = CGContext(
            data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue |
                CGBitmapInfo.byteOrder32Little.rawValue) else { return nil }
        // Flip: CGContext origin is bottom-left; stroke points are top-left.
        ctx.translateBy(x: 0, y: CGFloat(h))
        ctx.scaleBy(x: scale, y: -scale)
        // The same §6.2 ink as the live stroke above; the raster is what gets
        // placed into the PDF, so the two must not drift apart.
        ctx.setStrokeColor(red: Brand.inkLevel, green: Brand.inkLevel, blue: Brand.inkLevel, alpha: 1)
        ctx.setLineWidth(max(220 / 36, 3))
        ctx.setLineCap(.round)
        ctx.setLineJoin(.round)
        for points in strokes where points.count > 1 {
            ctx.move(to: points[0])
            for p in points.dropFirst() { ctx.addLine(to: p) }
            ctx.strokePath()
        }
        return ctx.makeImage()
    }
}

/// Icon over title, for buttons that must share a narrow row (#100).
///
/// `stacked` is false above the accessibility text sizes, where the caller puts
/// the buttons in a column instead and the ordinary side-by-side label fits.
struct StackedLabelStyle: LabelStyle {
    var stacked = true

    func makeBody(configuration: Configuration) -> some View {
        if stacked {
            VStack(spacing: 4) {
                configuration.icon
                title(configuration)
            }
            .padding(.vertical, 2)
        } else {
            HStack(spacing: 8) {
                configuration.icon
                title(configuration)
            }
            .padding(.vertical, 2)
        }
    }

    private func title(_ configuration: Configuration) -> some View {
        configuration.title
            .font(.subheadline.weight(.medium))
            // Two lines and a lower floor, not one line at 0.8: at a large text
            // size the old pair truncated the word away entirely (#167).
            .lineLimit(2)
            .minimumScaleFactor(0.5)
            .multilineTextAlignment(.center)
    }
}

/// Type-a-name signature (#101): the third way to sign, for a phone without a
/// stylus. The name is shown live in a script face on a white card — what the
/// placed stamp will look like — and rendered to a transparent CGImage that goes
/// through the same trim-and-store path as a drawn one.
struct TypeSignatureView: View {
    let onSave: (CGImage) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var name = ""
    @FocusState private var focused: Bool

    /// Snell Roundhand ships with iOS; the italic serif is the fallback if a
    /// future release drops it, so the feature degrades rather than breaks.
    private static let faceName = "SnellRoundhand-Bold"
    private static let previewSize: CGFloat = 34

    var body: some View {
        NavigationStack {
            VStack(alignment: .leading, spacing: 16) {
                TextField("Name", text: $name)
                    .textFieldStyle(.roundedBorder)
                    .textInputAutocapitalization(.words)
                    .focused($focused)
                    .submitLabel(.done)
                    .onSubmit(commit)
                ZStack {
                    RoundedRectangle(cornerRadius: 12, style: .continuous)
                        .fill(Color.white)
                        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous)
                            .strokeBorder(Color.primary.opacity(0.12), lineWidth: 1))
                    Text(name.isEmpty ? String(localized: "Your name, as a signature") : name)
                        .font(Self.previewFont)
                        .foregroundStyle(name.isEmpty ? Color.gray : Color(white: Double(Brand.inkLevel)))
                        .lineLimit(1)
                        .minimumScaleFactor(0.4)
                        .padding(.horizontal, 16)
                }
                .frame(height: 120)
                .accessibilityHidden(true)
                Spacer()
            }
            .padding(16)
            .navigationTitle("Type your name")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Add", action: commit)
                        .disabled(name.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
            .onAppear { focused = true }
        }
    }

    private static var previewFont: Font {
        UIFont(name: faceName, size: previewSize) != nil
            ? .custom(faceName, size: previewSize)
            : .system(size: previewSize, design: .serif).italic()
    }

    private func commit() {
        let text = name.trimmingCharacters(in: .whitespaces)
        guard !text.isEmpty else { return }
        if let image = Self.render(text) { onSave(image) }
        dismiss()
    }

    /// The name as ink on a transparent raster, large enough to stay sharp when
    /// placed: 160 pt glyphs with a 32 pt margin; the store trims to the ink.
    static func render(_ text: String) -> CGImage? {
        let font = UIFont(name: faceName, size: 160) ?? UIFont.italicSystemFont(ofSize: 160)
        let ink = UIColor(white: CGFloat(Brand.inkLevel), alpha: 1)
        let attributed = NSAttributedString(string: text, attributes: [.font: font, .foregroundColor: ink])
        let margin: CGFloat = 32
        let unbounded = CGSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        let bounds = attributed.boundingRect(with: unbounded, options: [.usesLineFragmentOrigin, .usesFontLeading], context: nil)
        let size = CGSize(width: ceil(bounds.width + margin * 2), height: ceil(bounds.height + margin * 2))
        let format = UIGraphicsImageRendererFormat()
        format.scale = 1
        format.opaque = false
        let image = UIGraphicsImageRenderer(size: size, format: format).image { _ in
            attributed.draw(at: CGPoint(x: margin, y: margin))
        }
        return image.cgImage
    }
}

/// What a base-14 face is called in the UI. The PDF names are exact and must not
/// change (SDD §6.2 contract 4); these are only what the buttons say. Not
/// localised on purpose: Helvetica, Times and Courier are names in every language.
func textBoxFontLabel(_ fontName: String) -> String {
    fontName == "Times-Roman" ? "Times" : fontName
}

/// The one text editor in the app: placing new text (#34), correcting a box
/// already on the page (#36), and choosing its size and face (#43).
///
/// Deliberately small. Six sizes and three faces are what "make this match the
/// form I am filling in" needs, and SDD §3.1 keeps anything more out.
struct TextBoxSheet: View {
    let isEditing: Bool
    @Binding var text: String
    @Binding var fontSize: Double
    @Binding var fontName: String
    let onCommit: () -> Void
    let onCancel: () -> Void

    // Typed as keys so both branches are looked up in the catalog.
    private var title: LocalizedStringKey { isEditing ? "Edit text" : "Add text" }
    private var commitLabel: LocalizedStringKey { isEditing ? "Save" : "Add" }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("Text", text: $text)
                        .autocorrectionDisabled()
                } footer: {
                    // Two literals, not a ternary, so the catalog sees both.
                    if isEditing {
                        Text("This replaces the text you tapped.")
                    } else {
                        Text("This will be added where you tapped.")
                    }
                }
                Picker("Size", selection: $fontSize) {
                    ForEach(textSizes, id: \.self) { size in
                        Text(verbatim: "\(Int(size))").tag(size)
                    }
                }
                Picker("Font", selection: $fontName) {
                    ForEach(PdfEngine.standardFonts, id: \.self) { face in
                        Text(textBoxFontLabel(face)).tag(face)
                    }
                }
            }
            .navigationTitle(title)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel", action: onCancel)
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(commitLabel, action: onCommit)
                        .disabled(text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
            }
        }
        // .large as well as .medium (#167): at an accessibility text size the
        // Size and Font pickers fall below a half-height sheet, and with only
        // one detent offered there is no way to pull it up to reach them.
        .presentationDetents([.medium, .large])
    }
}

/// Selection chrome for something the user placed on the page: drag to move,
/// corner handle to resize (aspect locked), ✕ to remove.
///
/// Signatures and text boxes share it (#36) rather than growing a second
/// interaction model — `resizable` and `onEdit` are the only differences between
/// them. A text box has no resize handle because resizing one would mean changing
/// its font size, and SDD §3.1 keeps formatting controls out of the app.
struct SelectionOverlay: View {
    let rect: PdfRect
    let pageSize: CGSize
    let viewSize: CGSize
    let onCommit: (PdfRect) -> Void
    let onRemove: () -> Void
    var resizable = true
    /// When set, a pencil appears alongside the ✕ — the discoverable way to
    /// correct a text box, since a quick second tap is taken by zoom.
    var onEdit: (() -> Void)?

    @State private var drag: CGSize = .zero
    @State private var widthDelta: CGFloat = 0

    var body: some View {
        let sx = viewSize.width / pageSize.width
        let sy = viewSize.height / pageSize.height
        let baseX = CGFloat(rect.left) * sx
        let baseY = (pageSize.height - CGFloat(rect.top)) * sy
        let baseW = CGFloat(rect.right - rect.left) * sx
        let baseH = CGFloat(rect.top - rect.bottom) * sy
        // widthDelta only ever moves when the resize handle exists, so a
        // non-resizable selection commits at scale 1 — a pure translation.
        let scale = max((baseW + widthDelta) / baseW, 0.15)

        ZStack(alignment: .topTrailing) {
            Rectangle()
                .strokeBorder(Brand.accent, lineWidth: 2)
            Button {
                onRemove()
            } label: {
                Image(systemName: "xmark.circle.fill")
                    .foregroundStyle(.white, .red)
                    .font(.title3)
            }
            .accessibilityLabel("Remove")
            .offset(x: 10, y: -10)
            if let onEdit {
                Button(action: onEdit) {
                    Image(systemName: "pencil.circle.fill")
                        .foregroundStyle(.white, .blue)
                        .font(.title3)
                }
                .accessibilityLabel("Edit text")
                .frame(maxWidth: .infinity, alignment: .leading)
                .offset(x: -10, y: -10)
            }
            if resizable {
                Rectangle()
                    .fill(Brand.accent)
                    .frame(width: 16, height: 16)
                    .frame(maxWidth: .infinity, maxHeight: .infinity,
                           alignment: .bottomTrailing)
                    .gesture(
                        DragGesture()
                            .onChanged { widthDelta = $0.translation.width }
                            .onEnded { _ in commit(scale: scale) }
                    )
            }
        }
        .frame(width: baseW * scale, height: baseH * scale)
        .offset(x: baseX + drag.width, y: baseY + drag.height)
        .gesture(
            DragGesture()
                .onChanged { drag = $0.translation }
                .onEnded { _ in commit(scale: scale) }
        )
    }

    private func commit(scale: CGFloat) {
        let sx = viewSize.width / pageSize.width
        let sy = viewSize.height / pageSize.height
        let dxPt = Double(drag.width / sx)
        let dyPt = Double(drag.height / sy)
        let newW = (rect.right - rect.left) * Double(scale)
        let newH = (rect.top - rect.bottom) * Double(scale)
        let newLeft = rect.left + dxPt
        let newTop = rect.top - dyPt
        drag = .zero
        widthDelta = 0
        onCommit(PdfRect(left: newLeft, bottom: newTop - newH,
                         right: newLeft + newW, top: newTop))
    }
}
