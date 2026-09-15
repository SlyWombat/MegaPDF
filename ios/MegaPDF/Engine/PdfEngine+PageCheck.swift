import CPdfium
import Foundation

// The #139 page check, run early and in the background (#145).
//
// `PdfEngine` is an actor, so a check run inside it would hold up every render and edit
// queued on the actor for the whole dry run, which is up to a minute on some pages. This one
// runs outside actor isolation, on its own dispatch thread, calling the core directly (the
// core is thread-safe, and lets go of its lock between the dry run's stages). What makes that
// safe is the document's check registry: a check registers before its first core call, and
// `PdfEngine.close` raises every registered flag and waits for the checks to finish before
// megapdf_close(), so a check never touches a closed document or a page handle the close freed.

/// A core cancel flag. Raised from any thread; freed when the last reference goes, which is
/// never while a check still holds it.
final class PageCheckFlag: @unchecked Sendable {
    fileprivate let handle: OpaquePointer

    init?() {
        guard let handle = megapdf_cancel_new() else { return nil }
        self.handle = handle
    }

    func raise() { megapdf_cancel_raise(handle) }

    deinit { megapdf_cancel_free(handle) }
}

extension PdfEngine {

    /// Whether regenerating page `pageIndex` would change how it looks, run off the actor. Swift
    /// task cancellation raises the core's flag, and the check stops at its next stage with
    /// `.cancelled`; so does closing the document. A cached answer comes back at once.
    nonisolated func pageCheck(_ document: PdfDocument, pageIndex: Int) async -> PageCheckAnswer {
        guard !Task.isCancelled else { return .cancelled }
        guard let flag = PageCheckFlag() else { return .unjudged }
        // Registered before the first core call: from here the document stays open until endCheck.
        guard document.beginCheck(flag) else { return .cancelled }
        return await withTaskCancellationHandler {
            await withCheckedContinuation { (continuation: CheckedContinuation<PageCheckAnswer, Never>) in
                Self.checkQueue.async {
                    let answer = Self.runCheck(document, pageIndex: pageIndex, flag: flag)
                    document.endCheck(flag)
                    continuation.resume(returning: answer)
                }
            }
        } onCancel: {
            flag.raise()
        }
    }

    /// A concurrent queue: a cancelled check still finishes its current stage, and a new one
    /// should not wait behind it for a thread.
    private static let checkQueue = DispatchQueue(label: "megapdf.page-check", qos: .utility, attributes: .concurrent)

    /// The blocking part. Never on the actor, never on the main thread.
    private static func runCheck(_ document: PdfDocument, pageIndex: Int, flag: PageCheckFlag) -> PageCheckAnswer {
        guard let page = megapdf_load_page(document.core, Int32(pageIndex)) else { return .unjudged }
        defer { megapdf_close_page(page) }
        var verdict = megapdf_layout_verdict()
        var status = megapdf_page_regeneration_verdict_cached(page, &verdict)
        if status == MEGAPDF_ERR_NOT_JUDGED {
            status = megapdf_page_regeneration_verdict_cancellable(page, flag.handle, &verdict)
        }
        if status == 1 { return .keepsLook }
        if status == 0 { return .wouldChange }
        if status == MEGAPDF_ERR_CANCELLED { return .cancelled }
        return .unjudged
    }
}
