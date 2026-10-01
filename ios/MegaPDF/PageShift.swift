import Foundation

/// How a page operation renumbered the document (#174), for everything the *app* keeps by
/// page index.
///
/// The core keeps its own per-page state right on its own: an open page handle follows its
/// page, and so do that page's redaction marks, layout verdicts and detached objects
/// (contract 10). What it cannot see is this side's index-keyed state — the rendered page
/// images, the page sizes the list lays out from, the settled #139 pages, the search hits and
/// the grid's selection — and the contract says plainly that the app renumbers those itself,
/// "exactly as it would after a page op made elsewhere".
///
/// This is that renumbering, as a value rather than as code spread through the view model:
/// one case per thing a page operation can do to the order, each one mirroring the remap the
/// core's own `RenumberPages` applies at the same moment, so the two cannot drift. Being a
/// pure value it is also the part of #174 that is unit-tested rather than driven through a
/// simulator.
///
/// An operation reports what it did as a *list* of these, in the order it happened, because
/// deleting a selection of five pages is one undo step and five renumberings.
///
/// `map` answers **nil** for a page that is gone, never 0. The desktop leg's comment on the
/// same type is worth repeating: treating "gone" as 0 would move a deleted page's state onto
/// the first page, which is the silent bug this type exists to prevent. Swift spells that as
/// an `Int?` the caller cannot ignore.
enum PageShift: Equatable {
    /// The page at `at` was deleted.
    case removed(at: Int)

    /// `count` pages arrived at `at` — a blank page, a restored one, or the pages of another
    /// file.
    case inserted(at: Int, count: Int)

    /// The page at `from` now stands at `to`; the pages between them shifted by one. The same
    /// arithmetic `megapdf_page_move` renumbers the core's own state with.
    case moved(from: Int, to: Int)

    /// Where the page that stood at `index` stands afterwards; nil when it is gone.
    func map(_ index: Int) -> Int? {
        switch self {
        case let .removed(at):
            if index == at { return nil }
            return index > at ? index - 1 : index
        case let .inserted(at, count):
            return index >= at ? index + count : index
        case let .moved(from, to):
            if index == from { return to }
            if from < to { return (index > from && index <= to) ? index - 1 : index }
            return (index >= to && index < from) ? index + 1 : index
        }
    }

    /// How the page count changed.
    var countDelta: Int {
        switch self {
        case .removed: return -1
        case let .inserted(_, count): return count
        case .moved: return 0
        }
    }
}

extension Array where Element == PageShift {
    /// Where the page that stood at `index` stands after all of these; nil once it is gone.
    /// The identity of the fold is the page itself, which is what a rotation reports: no
    /// shifts at all.
    func map(index: Int) -> Int? {
        var at: Int? = index
        for shift in self {
            guard let current = at else { return nil }
            at = shift.map(current)
        }
        return at
    }

    /// The page count after these shifts.
    func mapCount(_ count: Int) -> Int {
        reduce(count) { $0 + $1.countDelta }
    }
}

extension Dictionary where Key == Int {
    /// A map keyed by page index, renumbered: entries for deleted pages are dropped. Used for
    /// the rendered page images and their render keys, so a delete or a move does not throw
    /// away the pictures of every page that did not move.
    func shifted(by shifts: [PageShift]) -> Self {
        guard !shifts.isEmpty else { return self }
        var out = Self(minimumCapacity: count)
        for (index, value) in self {
            if let to = shifts.map(index: index) { out[to] = value }
        }
        return out
    }
}

extension Set where Element == Int {
    /// A set of page indices, renumbered: deleted pages leave it. The grid's selection, and
    /// the #139 pages a person has already settled.
    func shifted(by shifts: [PageShift]) -> Self {
        guard !shifts.isEmpty else { return self }
        return Set(compactMap { shifts.map(index: $0) })
    }
}

extension Array {
    /// A per-page list — the page sizes the viewer lays out from — with one shift applied.
    ///
    /// `inserted` is the value for each page that arrived, which only the engine can answer,
    /// so the caller reads those from the document and hands them in; it is ignored for the
    /// other two shifts. Out-of-range indices are left alone rather than trapped: this runs
    /// against a list the engine has already changed, and a crash is never the right answer
    /// to a disagreement about how many pages there are.
    func shifted(by shift: PageShift, inserted: [Element] = []) -> [Element] {
        var out = self
        switch shift {
        case let .removed(at):
            guard indices.contains(at) else { return out }
            out.remove(at: at)
        case let .inserted(at, _):
            out.insert(contentsOf: inserted, at: Swift.min(Swift.max(at, 0), out.count))
        case let .moved(from, to):
            guard indices.contains(from) else { return out }
            let element = out.remove(at: from)
            out.insert(element, at: Swift.min(Swift.max(to, 0), out.count))
        }
        return out
    }
}
