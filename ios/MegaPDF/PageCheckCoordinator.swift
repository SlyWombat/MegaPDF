import Foundation

/// What a page check (#139) found.
enum PageCheckAnswer: Equatable {
    /// Regenerating the page keeps its look: no warning.
    case keepsLook
    /// Regenerating the page would change parts of it the person never touched: warn.
    case wouldChange
    /// The page couldn't be judged. Nothing is refused over it, so it counts as no warning.
    case unjudged
    /// The check stopped before it answered: its flag was raised or the document is closing.
    case cancelled
}

/// What a change waiting on a page check (#145) should do.
enum PageCheckOutcome: Equatable {
    /// Apply: the page keeps its look, couldn't be judged, was settled already, or the check ran
    /// past its budget. The page is settled either way.
    case apply
    /// Ask first: the page would change.
    case warn
    /// The document went away while waiting; apply nothing.
    case abandoned
}

/// One shared page check per page of the open document (#139 follow-up, #145).
///
/// A check starts early, in the background, when a page is first shown or a tool that
/// regenerates content is armed, so its answer is usually ready when the change comes.
/// Starting a check for one page cancels any unfinished check for another, except one a
/// change is waiting on. Settled pages (they keep their look, the person chose Continue, or a
/// change already went on without a warning) are never checked again. A change waits on the
/// running check at most `budget`; past that the check is cancelled and the change applies.
@MainActor
final class PageCheckCoordinator {
    typealias Check = @MainActor (Int) async -> PageCheckAnswer

    /// How long a change waits for an answer.
    var budget: TimeInterval

    private let check: Check
    private var running: (page: Int, task: Task<PageCheckAnswer, Never>)?
    private(set) var settled: Set<Int> = []
    /// The page a change is waiting on; a check started for another page must not cancel it.
    private var awaitedPage: Int?
    /// Bumped by `reset`, so a check that answers for a closed document is ignored.
    private var generation = 0

    init(budget: TimeInterval = 1.5, check: @escaping Check) {
        self.budget = budget
        self.check = check
    }

    func isSettled(_ page: Int) -> Bool { settled.contains(page) }

    /// The person chose Continue, or a change went on: never check this page again.
    func settle(_ page: Int) {
        settled.insert(page)
        if let running, running.page == page {
            running.task.cancel()
            self.running = nil
        }
    }

    /// The page whose check is running, if any.
    var runningPage: Int? { running?.page }

    /// Starts the page's check in the background unless it is settled or already running.
    func start(_ page: Int) {
        guard !settled.contains(page) else { return }
        if let running {
            if running.page == page { return }
            // A change is waiting on that answer; this page's turn comes later.
            if running.page == awaitedPage { return }
            running.task.cancel()
        }
        let generation = self.generation
        let check = self.check
        let task = Task { await check(page) }
        running = (page, task)
        Task { [weak self] in
            let answer = await task.value
            self?.finished(page: page, task: task, answer: answer, generation: generation)
        }
    }

    private func finished(page: Int, task: Task<PageCheckAnswer, Never>, answer: PageCheckAnswer, generation: Int) {
        guard generation == self.generation else { return }
        if let running, running.task == task { self.running = nil }
        // A page that keeps its look, or can't be judged, never needs asking. One that would
        // change is left unsettled: the core caches the answer, so asking again is instant.
        if answer == .keepsLook || answer == .unjudged { settled.insert(page) }
    }

    /// Waits for the page's answer, at most `budget`, starting the check if none is running.
    func outcome(for page: Int) async -> PageCheckOutcome {
        guard !settled.contains(page) else { return .apply }
        let generation = self.generation
        awaitedPage = page
        defer { if self.generation == generation { awaitedPage = nil } }
        start(page)
        guard let running, running.page == page else { return .apply }
        let task = running.task
        let answer = await Self.first(of: task, within: budget)
        guard generation == self.generation else { return .abandoned }
        switch answer {
        case nil:
            // Past the budget: a warning that arrives after the person has moved on helps nobody.
            settle(page)
            return .apply
        case .keepsLook, .unjudged:
            settled.insert(page)
            return .apply
        case .wouldChange:
            return .warn
        case .cancelled:
            // Only a closing document cancels a check a change is waiting on.
            return .abandoned
        }
    }

    /// Cancels every check and forgets every page: the document closed or another opened.
    func reset() {
        generation += 1
        running?.task.cancel()
        running = nil
        settled = []
        awaitedPage = nil
    }

    /// The task's value, or nil when `seconds` pass first. The task itself is left running.
    private static func first(of task: Task<PageCheckAnswer, Never>, within seconds: TimeInterval) async -> PageCheckAnswer? {
        await withCheckedContinuation { (continuation: CheckedContinuation<PageCheckAnswer?, Never>) in
            let once = ResumeOnce(continuation)
            let waiter = Task { @MainActor in once.resume(await task.value) }
            let timer = Task { @MainActor in
                try? await Task.sleep(nanoseconds: UInt64(max(0, seconds) * 1_000_000_000))
                if !Task.isCancelled { once.resume(nil) }
            }
            once.onResume = {
                waiter.cancel()
                timer.cancel()
            }
        }
    }
}

/// Resumes a continuation exactly once, whichever of its racers gets there first.
@MainActor
private final class ResumeOnce {
    private var continuation: CheckedContinuation<PageCheckAnswer?, Never>?
    var onResume: (() -> Void)?

    init(_ continuation: CheckedContinuation<PageCheckAnswer?, Never>) {
        self.continuation = continuation
    }

    func resume(_ value: PageCheckAnswer?) {
        guard let continuation else { return }
        self.continuation = nil
        continuation.resume(returning: value)
        onResume?()
        onResume = nil
    }
}
