import PhotosUI
import SwiftUI

/// Library sheet: pick to place, draw a new one, import from Photos, delete.
struct SignaturesSheet: View {
    let signatures: [SignatureEntry]
    var startDrawing = false
    let onPick: (SignatureEntry) -> Void
    let onDrawn: (CGImage) -> Void
    let onPhoto: (Data) -> Void
    let onDelete: (String) -> Void
    let onDismiss: () -> Void

    @State private var drawing = false
    @State private var photoItem: PhotosPickerItem?

    var body: some View {
        NavigationStack {
            List {
                if signatures.isEmpty {
                    Text("No signatures yet. Draw one with your finger, or add a photo of your signature on white paper — the background is removed automatically.")
                        .foregroundStyle(.secondary)
                } else {
                    Section("Tap a signature, then tap the page") {
                        ForEach(signatures) { entry in
                            Button(entry.displayName) { onPick(entry) }
                                .foregroundStyle(.primary)
                        }
                        .onDelete { offsets in
                            offsets.map { signatures[$0].id }.forEach(onDelete)
                        }
                    }
                }
            }
            .navigationTitle("Signatures")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .navigationBarLeading) {
                    Button("Close", action: onDismiss)
                }
                ToolbarItemGroup(placement: .navigationBarTrailing) {
                    Button("Draw") { drawing = true }
                    PhotosPicker("Photos", selection: $photoItem, matching: .images)
                }
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
            .onAppear {
                if startDrawing { drawing = true }
            }
        }
    }
}

/// Finger/stylus capture: strokes over a light canvas, rendered to a
/// transparent CGImage (trim-only cleanup applies downstream).
struct DrawSignatureView: View {
    let onSave: (CGImage) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var strokes: [[CGPoint]] =
        DemoContent.requestedState == "draw"
            ? DemoContent.squiggleStrokes(width: 350, height: 180)
            : []
    @State private var current: [CGPoint] = []
    @State private var canvasSize: CGSize = .zero

    private let inkColor = Color(red: 0.10, green: 0.10, blue: 0.10)

    var body: some View {
        NavigationStack {
            VStack {
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
                .background(Color(white: 0.96))
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
                    .disabled(strokes.isEmpty)
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
        ctx.setStrokeColor(red: 0.10, green: 0.10, blue: 0.10, alpha: 1)
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

/// Selection chrome for a placed signature: drag to move, corner handle to
/// resize (aspect locked), ✕ to remove. Commits to the engine on drag end.
struct StampOverlay: View {
    let stamp: SelectedStamp
    let pageSize: CGSize
    let viewSize: CGSize
    let onCommit: (PdfRect) -> Void
    let onRemove: () -> Void

    @State private var drag: CGSize = .zero
    @State private var widthDelta: CGFloat = 0

    var body: some View {
        let sx = viewSize.width / pageSize.width
        let sy = viewSize.height / pageSize.height
        let baseX = CGFloat(stamp.rect.left) * sx
        let baseY = (pageSize.height - CGFloat(stamp.rect.top)) * sy
        let baseW = CGFloat(stamp.rect.right - stamp.rect.left) * sx
        let baseH = CGFloat(stamp.rect.top - stamp.rect.bottom) * sy
        let scale = max((baseW + widthDelta) / baseW, 0.15)

        ZStack(alignment: .topTrailing) {
            Rectangle()
                .strokeBorder(Color.blue, lineWidth: 2)
            Button {
                onRemove()
            } label: {
                Image(systemName: "xmark.circle.fill")
                    .foregroundStyle(.white, .red)
                    .font(.title3)
            }
            .offset(x: 10, y: -10)
            Rectangle()
                .fill(Color.blue)
                .frame(width: 16, height: 16)
                .frame(maxWidth: .infinity, maxHeight: .infinity,
                       alignment: .bottomTrailing)
                .gesture(
                    DragGesture()
                        .onChanged { widthDelta = $0.translation.width }
                        .onEnded { _ in commit(scale: scale) }
                )
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
        let newW = (stamp.rect.right - stamp.rect.left) * Double(scale)
        let newH = (stamp.rect.top - stamp.rect.bottom) * Double(scale)
        let newLeft = stamp.rect.left + dxPt
        let newTop = stamp.rect.top - dyPt
        drag = .zero
        widthDelta = 0
        onCommit(PdfRect(left: newLeft, bottom: newTop - newH,
                         right: newLeft + newW, top: newTop))
    }
}
