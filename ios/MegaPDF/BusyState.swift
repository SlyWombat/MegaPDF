import Foundation
import UIKit

// Feedback while the app is working (#145): one busy state per open document.
//
//   * The moment an operation starts it is recorded here, so the control that started it
//     is disabled and repeat taps are ignored (`begin` answers nil while blocking work runs).
//   * An indeterminate indicator appears only once the work has run for 0.5 s, so quick
//     work never flickers, and once shown it stays at least 0.3 s.
//   * Document-level work (opening, saving, search) shows a strip under the top toolbar;
//     page-level work (the page checks, applying a change) a small spinner on the page or
//     the line. Each has its own indicator and timing.

/// What the indicator says. Every label is also what VoiceOver reads.
enum BusyLabel: Equatable {
    case opening
    case saving
    case checkingSavedFile
    case checkingPage
    case applying
    case searching
    /// A Markdown export (#386) -- distinct from `.saving`, since it isn't one (see
    /// `ViewerModel.exportMarkdownFile`).
    case exportingText

    var text: String {
        switch self {
        case .opening: return String(localized: "Opening…")
        case .saving: return String(localized: "Saving…")
        case .checkingSavedFile: return String(localized: "Checking the saved file…")
        case .checkingPage: return String(localized: "Checking this page…")
        case .applying: return String(localized: "Applying…")
        case .searching: return String(localized: "Searching…")
        case .exportingText: return String(localized: "Exporting…")
        }
    }
}

/// Where the indicator for a piece of work goes.
enum BusyScope: Equatable {
    /// The strip under the navigation bar.
    case document
    /// A spinner on the page, centred on `rect` (PDF points) when one is given.
    case page(Int, PdfRect?)

    var isDocument: Bool { self == .document }
}

/// One piece of running work.
struct BusyWork: Identifiable, Equatable {
    let id: Int
    var label: BusyLabel
    var scope: BusyScope
    /// Blocking work stops editing and further blocking work until it ends. Search doesn't.
    let blocking: Bool
    /// Close, Discard and the file commands wait for it too (saves, password changes, applying).
    var blocksFileCommands: Bool
    /// Off while the work waits on the person (the #139 warning is up): no spinner behind an alert.
    var showsIndicator: Bool
}

/// A handle on running work, returned by `begin`.
struct BusyToken: Equatable {
    fileprivate let id: Int
}

/// The clock the indicator timing runs on; tests drive a manual one.
protocol BusyScheduler {
    var now: TimeInterval { get }
    /// Runs `action` on the main actor after `delay` seconds, unless the returned handle is cancelled first.
    func schedule(after delay: TimeInterval, _ action: @escaping @MainActor () -> Void) -> BusyScheduled
}

protocol BusyScheduled {
    func cancel()
}

/// The real clock.
struct SystemBusyScheduler: BusyScheduler {
    var now: TimeInterval { ProcessInfo.processInfo.systemUptime }

    func schedule(after delay: TimeInterval, _ action: @escaping @MainActor () -> Void) -> BusyScheduled {
        let item = DispatchWorkItem { MainActor.assumeIsolated { action() } }
        DispatchQueue.main.asyncAfter(deadline: .now() + max(0, delay), execute: item)
        return item
    }
}

extension DispatchWorkItem: BusyScheduled {}

@MainActor
final class BusyState: ObservableObject {
    nonisolated static let showDelay: TimeInterval = 0.5
    nonisolated static let minimumVisible: TimeInterval = 0.3

    /// Everything running, oldest first. Changes the instant work begins or ends.
    @Published private(set) var works: [BusyWork] = []
    /// The strip under the toolbar, when it is showing.
    @Published private(set) var strip: BusyWork?
    /// The spinner on a page, when it is showing.
    @Published private(set) var pageIndicator: BusyWork?

    private let scheduler: BusyScheduler
    private let showDelay: TimeInterval
    private let minimumVisible: TimeInterval
    private let announce: @MainActor (String) -> Void
    private var nextId = 1
    private lazy var stripTiming = IndicatorTiming(owner: self, isDocument: true)
    private lazy var pageTiming = IndicatorTiming(owner: self, isDocument: false)

    init(scheduler: BusyScheduler = SystemBusyScheduler(),
         showDelay: TimeInterval = BusyState.showDelay,
         minimumVisible: TimeInterval = BusyState.minimumVisible,
         announce: @escaping @MainActor (String) -> Void = { UIAccessibility.post(notification: .announcement, argument: $0) }) {
        self.scheduler = scheduler
        self.showDelay = showDelay
        self.minimumVisible = minimumVisible
        self.announce = announce
    }

    /// Blocking work is running: page taps, tools and edits wait.
    var isBlocked: Bool { works.contains { $0.blocking } }

    /// Close, Discard and file commands wait: a save, a password change or a change being applied runs.
    var blocksFileCommands: Bool { works.contains { $0.blocksFileCommands } }

    /// Starts work. Blocking work answers nil while other blocking work runs: the tap is ignored.
    func begin(_ label: BusyLabel, scope: BusyScope, blocking: Bool = true,
               blocksFileCommands: Bool = false, showsIndicator: Bool = true) -> BusyToken? {
        if blocking && isBlocked { return nil }
        let work = BusyWork(id: nextId, label: label, scope: scope, blocking: blocking,
                            blocksFileCommands: blocksFileCommands, showsIndicator: showsIndicator)
        nextId += 1
        works.append(work)
        refresh()
        return BusyToken(id: work.id)
    }

    /// Changes what running work says, where it shows, or whether it shows at all.
    func update(_ token: BusyToken, label: BusyLabel? = nil, scope: BusyScope? = nil,
                blocksFileCommands: Bool? = nil, showsIndicator: Bool? = nil) {
        guard let i = works.firstIndex(where: { $0.id == token.id }) else { return }
        var work = works[i]
        if let label { work.label = label }
        if let scope { work.scope = scope }
        if let blocksFileCommands { work.blocksFileCommands = blocksFileCommands }
        if let showsIndicator { work.showsIndicator = showsIndicator }
        guard work != works[i] else { return }
        works[i] = work
        refresh()
    }

    /// Ends work. Safe to call twice.
    func end(_ token: BusyToken) {
        guard let i = works.firstIndex(where: { $0.id == token.id }) else { return }
        works.remove(at: i)
        refresh()
    }

    /// Runs `body` as busy work, ending it however `body` finishes. Nil when blocked.
    func run<T>(_ label: BusyLabel, scope: BusyScope, blocking: Bool = true, blocksFileCommands: Bool = false,
                _ body: (BusyToken) async throws -> T) async rethrows -> T? {
        guard let token = begin(label, scope: scope, blocking: blocking,
                                blocksFileCommands: blocksFileCommands) else { return nil }
        defer { end(token) }
        return try await body(token)
    }

    /// Drops everything at once, indicators included.
    func reset() {
        works = []
        stripTiming.reset()
        pageTiming.reset()
        strip = nil
        pageIndicator = nil
    }

    /// The document closed: its page-level work is over, and the spinner goes at once. Document-level
    /// work stays, because whoever started it (an open, a password change that reopens) ends it.
    func endPageWork() {
        works.removeAll { !$0.scope.isDocument }
        pageTiming.reset()
        pageIndicator = nil
        refresh()
    }

    // MARK: - indicator timing

    /// What each indicator should show now: the newest blocking work, else the newest other work.
    fileprivate func candidate(document: Bool) -> BusyWork? {
        let eligible = works.filter { $0.showsIndicator && $0.scope.isDocument == document }
        return eligible.last(where: { $0.blocking }) ?? eligible.last
    }

    fileprivate var clockNow: TimeInterval { scheduler.now }

    fileprivate func schedule(after delay: TimeInterval, _ action: @escaping @MainActor () -> Void) -> BusyScheduled {
        scheduler.schedule(after: delay, action)
    }

    fileprivate var delays: (show: TimeInterval, minimum: TimeInterval) { (showDelay, minimumVisible) }

    fileprivate func publish(_ work: BusyWork?, document: Bool) {
        if document {
            let appearing = strip == nil && work != nil
            if strip != work { strip = work }
            if appearing, let work { announce(work.label.text) }
        } else if pageIndicator != work {
            pageIndicator = work
        }
    }

    private func refresh() {
        stripTiming.refresh()
        pageTiming.refresh()
    }
}

/// The 0.5 s / 0.3 s rule for one indicator.
@MainActor
private final class IndicatorTiming {
    private unowned let owner: BusyState
    private let isDocument: Bool
    private var showTimer: BusyScheduled?
    private var hideTimer: BusyScheduled?
    private var shownAt: TimeInterval?

    init(owner: BusyState, isDocument: Bool) {
        self.owner = owner
        self.isDocument = isDocument
    }

    func refresh() {
        let candidate = owner.candidate(document: isDocument)
        if let candidate {
            hideTimer?.cancel()
            hideTimer = nil
            if shownAt != nil {
                owner.publish(candidate, document: isDocument)
            } else if showTimer == nil {
                // The delay runs from when there was first something to show.
                showTimer = owner.schedule(after: owner.delays.show) { [weak self] in self?.show() }
            }
        } else {
            showTimer?.cancel()
            showTimer = nil
            guard let shownAt, hideTimer == nil else { return }
            let remaining = owner.delays.minimum - (owner.clockNow - shownAt)
            if remaining <= 0 {
                hide()
            } else {
                hideTimer = owner.schedule(after: remaining) { [weak self] in self?.hide() }
            }
        }
    }

    func reset() {
        showTimer?.cancel()
        hideTimer?.cancel()
        showTimer = nil
        hideTimer = nil
        shownAt = nil
    }

    private func show() {
        showTimer = nil
        guard let candidate = owner.candidate(document: isDocument) else { return }
        shownAt = owner.clockNow
        owner.publish(candidate, document: isDocument)
    }

    private func hide() {
        hideTimer = nil
        // Work that began while the indicator lingered keeps it up.
        if let candidate = owner.candidate(document: isDocument) {
            owner.publish(candidate, document: isDocument)
            return
        }
        shownAt = nil
        owner.publish(nil, document: isDocument)
    }
}
