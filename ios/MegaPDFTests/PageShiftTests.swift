import CoreGraphics
import XCTest
@testable import MegaPDF

/// The renumbering primitive (#174, contract 10) and the drop arithmetic, held against
/// themselves.
///
/// These are the parts of the page tools that are wrong in ways a screenshot cannot show. A
/// delete moves every page after it; a move shifts a run of them one way or the other; a drop
/// points at a tile but `movePage` speaks in the index the page ends up at. Each of those is one
/// line of arithmetic, and each of them, got wrong, quietly moves a page's rendered image, its
/// search hits or its "already asked about" onto a different page.
///
/// Pure, so they run on the simulator in milliseconds rather than through a document.
final class PageShiftTests: XCTestCase {

    // MARK: - one shift at a time

    func testADeletedPageMovesEverythingAfterItDownOne() {
        let shift = PageShift.removed(at: 2)
        XCTAssertEqual(shift.map(0), 0)
        XCTAssertEqual(shift.map(1), 1)
        XCTAssertEqual(shift.map(3), 2)
        XCTAssertEqual(shift.map(9), 8)
        XCTAssertEqual(shift.countDelta, -1)
    }

    /// **The claim this type exists for.** A page that is gone answers nil, and the type makes
    /// that impossible to read as "page 0" — which is what the desktop leg's own comment warns
    /// about, because treating it as 0 moves a deleted page's state onto the first page.
    func testAPageThatIsGoneIsNilAndNotZero() {
        XCTAssertNil(PageShift.removed(at: 0).map(0))
        XCTAssertNil(PageShift.removed(at: 7).map(7))
        XCTAssertNil([PageShift.removed(at: 3), .moved(from: 0, to: 2)].map(index: 3))
    }

    func testInsertedPagesMoveEverythingFromThereUp() {
        let one = PageShift.inserted(at: 1, count: 1)
        XCTAssertEqual(one.map(0), 0)
        XCTAssertEqual(one.map(1), 2)
        XCTAssertEqual(one.countDelta, 1)

        let many = PageShift.inserted(at: 2, count: 3)
        XCTAssertEqual(many.map(1), 1)
        XCTAssertEqual(many.map(2), 5)
        XCTAssertEqual(many.countDelta, 3)
    }

    /// An insert at the page count appends, and touches nothing.
    func testAnAppendingInsertMovesNothing() {
        let append = PageShift.inserted(at: 4, count: 1)
        for index in 0..<4 { XCTAssertEqual(append.map(index), index) }
    }

    func testMovingAPageLaterPullsThePagesBetweenBack() {
        // [A B C D E], B (1) to 3  ->  [A C D B E]
        let shift = PageShift.moved(from: 1, to: 3)
        XCTAssertEqual(shift.map(0), 0, "A stays")
        XCTAssertEqual(shift.map(1), 3, "B is the page that moved")
        XCTAssertEqual(shift.map(2), 1, "C comes back one")
        XCTAssertEqual(shift.map(3), 2, "D comes back one")
        XCTAssertEqual(shift.map(4), 4, "E is past it")
        XCTAssertEqual(shift.countDelta, 0)
    }

    func testMovingAPageEarlierPushesThePagesBetweenOn() {
        // [A B C D E], D (3) to 1  ->  [A D B C E]
        let shift = PageShift.moved(from: 3, to: 1)
        XCTAssertEqual(shift.map(0), 0)
        XCTAssertEqual(shift.map(1), 2)
        XCTAssertEqual(shift.map(2), 3)
        XCTAssertEqual(shift.map(3), 1, "D is the page that moved")
        XCTAssertEqual(shift.map(4), 4)
    }

    func testAMoveToWhereItAlreadyIsChangesNothing() {
        let shift = PageShift.moved(from: 2, to: 2)
        for index in 0..<5 { XCTAssertEqual(shift.map(index), index) }
    }

    // MARK: - a list of them, which is what an operation reports

    /// A selection delete is one undo step and several renumberings, applied in the order the
    /// operation performed them — highest index first.
    func testDeletingASelectionRenumbersWhatIsLeft() {
        // [0 1 2 3 4 5], delete 1 and 4  ->  [0 2 3 5] at 0,1,2,3
        let shifts: [PageShift] = [.removed(at: 4), .removed(at: 1)]
        XCTAssertEqual(shifts.map(index: 0), 0)
        XCTAssertNil(shifts.map(index: 1))
        XCTAssertEqual(shifts.map(index: 2), 1)
        XCTAssertEqual(shifts.map(index: 3), 2)
        XCTAssertNil(shifts.map(index: 4))
        XCTAssertEqual(shifts.map(index: 5), 3)
        XCTAssertEqual(shifts.mapCount(6), 4)
    }

    /// The other direction: an undo of that delete puts them back where they were.
    func testRestoringASelectionPutsEveryPageBackWhereItWas() {
        let forward = DeletePagesOperation(pages: [1, 4])
        XCTAssertEqual(forward.shifts(reverted: false), [.removed(at: 4), .removed(at: 1)],
                       "highest first, so the next index to delete has not moved")
        XCTAssertEqual(forward.shifts(reverted: true),
                       [.inserted(at: 1, count: 1), .inserted(at: 4, count: 1)],
                       "lowest first, so each page lands at the index it came from")

        let there = forward.shifts(reverted: false)
        let back = forward.shifts(reverted: true)
        for index in 0..<6 where !([1, 4].contains(index)) {
            let moved = there.map(index: index)
            XCTAssertNotNil(moved)
            XCTAssertEqual(back.map(index: moved!), index,
                           "page \(index) has to come back to page \(index)")
        }
    }

    func testAnEmptyListIsTheIdentity() {
        XCTAssertEqual([PageShift]().map(index: 3), 3)
        XCTAssertEqual([PageShift]().mapCount(7), 7)
    }

    // MARK: - what the app keeps by page index

    func testARenderCacheKeepsThePicturesOfPagesThatDidNotMove() {
        let images = [0: "a", 1: "b", 2: "c", 3: "d"]
        let after = images.shifted(by: [.removed(at: 1)])
        XCTAssertEqual(after, [0: "a", 1: "c", 2: "d"],
                       "b's picture goes with b; c and d keep theirs at their new indices")
    }

    func testASelectionFollowsAMoveAndLosesADeletedPage() {
        XCTAssertEqual(Set([1, 3]).shifted(by: [.moved(from: 1, to: 3)]), Set([3, 2]))
        XCTAssertEqual(Set([1, 3]).shifted(by: [.removed(at: 1)]), Set([2]))
        XCTAssertEqual(Set([0, 1, 2]).shifted(by: [.inserted(at: 0, count: 2)]), Set([2, 3, 4]))
    }

    func testAPerPageListTakesTheValuesOnlyTheEngineCanAnswerForInsertedPages() {
        let sizes = ["A4", "A4", "Letter"]
        XCTAssertEqual(sizes.shifted(by: .inserted(at: 1, count: 2), inserted: ["A5", "A5"]),
                       ["A4", "A5", "A5", "A4", "Letter"])
        XCTAssertEqual(sizes.shifted(by: .removed(at: 0)), ["A4", "Letter"])
        XCTAssertEqual(sizes.shifted(by: .moved(from: 2, to: 0)), ["Letter", "A4", "A4"])
        XCTAssertEqual(sizes.shifted(by: .inserted(at: 3, count: 1), inserted: ["A3"]),
                       ["A4", "A4", "Letter", "A3"], "an appending insert")
    }

    /// A list the engine has already changed under us is left alone rather than trapped: a crash
    /// is never the right answer to a disagreement about how many pages there are.
    func testAnOutOfRangeShiftIsSurvivable() {
        let sizes = ["A4"]
        XCTAssertEqual(sizes.shifted(by: .removed(at: 5)), ["A4"])
        XCTAssertEqual(sizes.shifted(by: .moved(from: 4, to: 0)), ["A4"])
    }

    // MARK: - the operations' own reports

    func testARotationRenumbersNothingAndRedrawsEverythingItTurned() {
        let rotate = RotatePagesOperation(pages: [3, 1, 1], quarterTurns: 1)
        XCTAssertEqual(rotate.pages, [1, 3], "deduplicated and in order")
        XCTAssertTrue(rotate.shifts(reverted: false).isEmpty)
        XCTAssertTrue(rotate.shifts(reverted: true).isEmpty)
        XCTAssertEqual(rotate.changedPages, [1, 3],
                       "no shifts must never be read as nothing to redraw")
    }

    func testAMoveIsItsOwnInverseWithTheIndicesSwapped() {
        let move = MovePageOperation(from: 1, to: 4)
        XCTAssertEqual(move.shifts(reverted: false), [.moved(from: 1, to: 4)])
        XCTAssertEqual(move.shifts(reverted: true), [.moved(from: 4, to: 1)])
    }

    func testABlankPageIsUndoneByTakingItBackOff() {
        let insert = InsertBlankPageOperation(at: 2, widthPoints: 612, heightPoints: 792)
        XCTAssertEqual(insert.shifts(reverted: false), [.inserted(at: 2, count: 1)])
        XCTAssertEqual(insert.shifts(reverted: true), [.removed(at: 2)])
    }

    /// Contract 10's inverse for an import is "delete(at) n times", at the same index each time:
    /// each removal closes up behind itself, so the next page to go is at `insertAt` again.
    func testAnImportIsUndoneByTakingExactlyThePagesThatArrivedBackOff() {
        let importPages = ImportPagesOperation(path: "/nowhere.pdf", insertAt: 2)
        XCTAssertTrue(importPages.shifts(reverted: false).isEmpty,
                      "an import knows nothing until it has run")
        importPages.setImportedForTesting(3)
        XCTAssertEqual(importPages.shifts(reverted: false), [.inserted(at: 2, count: 3)])
        XCTAssertEqual(importPages.shifts(reverted: true),
                       [.removed(at: 2), .removed(at: 2), .removed(at: 2)])
    }

    // MARK: - where a dragged page lands

    /// `movePage(from:to:)` speaks in "the index the page stands at afterwards", which is not the
    /// index a drop points at: taking the page out first closes the gap behind it.
    func testADroppedPageLandsWhereTheGapCloses() {
        // [A B C D]; A dropped after C  ->  [B C A D], A at 2 (not 3).
        XCTAssertEqual(PageDrop.destination(dragging: 0, onto: 2, before: false), 2)
        // D dropped before B  ->  [A D B C], D at 1.
        XCTAssertEqual(PageDrop.destination(dragging: 3, onto: 1, before: true), 1)
        // A dropped before C  ->  [B A C D], A at 1.
        XCTAssertEqual(PageDrop.destination(dragging: 0, onto: 2, before: true), 1)
        // D dropped after B  ->  [A B D C], D at 2.
        XCTAssertEqual(PageDrop.destination(dragging: 3, onto: 1, before: false), 2)
    }

    /// Which half of a tile a drop landed on, against the width the tile actually came out — the
    /// one quantity here that is measured rather than reasoned about. A hard-coded guess (the first
    /// draft used 120) puts the midpoint in the wrong place on a grid whose columns are adaptive,
    /// and then a drop near the middle of a tile goes the wrong way.
    func testWhichHalfOfATileADropLandedOn() {
        // A tile as the iPad's sidebar lays it out, and as the phone's sheet does.
        for width in [118.0, 110.7, 96.0, 150.0] as [CGFloat] {
            XCTAssertTrue(PageDrop.isBefore(dropX: 0, tileWidth: width), "the leading edge")
            XCTAssertTrue(PageDrop.isBefore(dropX: width * 0.15, tileWidth: width))
            XCTAssertFalse(PageDrop.isBefore(dropX: width * 0.85, tileWidth: width))
            XCTAssertFalse(PageDrop.isBefore(dropX: width, tileWidth: width), "the trailing edge")
            XCTAssertFalse(PageDrop.isBefore(dropX: width / 2, tileWidth: width),
                           "the midpoint itself belongs to the trailing half, consistently")
        }
        // Asked before the first layout pass: a zero width must not make every drop land behind
        // the tile, which is what dividing by it the other way round would do.
        XCTAssertTrue(PageDrop.isBefore(dropX: 0, tileWidth: 0))
    }

    /// The two halves of the drag, composed: a point inside a tile, the tile's own width, and the
    /// index `movePage` is then given. This is the whole of the drop that is *not* a platform
    /// gesture, and it is checked here because the gesture itself cannot be synthesised (see
    /// `PageDragUITests`).
    func testAPointInsideATileBecomesTheIndexTheMoveIsGiven() {
        let width: CGFloat = 118
        // Dragging page 1 and letting go on the leading half of page 3 → page 1 stands second.
        let leading = PageDrop.isBefore(dropX: 10, tileWidth: width)
        XCTAssertEqual(PageDrop.destination(dragging: 0, onto: 2, before: leading), 1)
        // …and on its trailing half → page 1 stands third.
        let trailing = PageDrop.isBefore(dropX: 110, tileWidth: width)
        XCTAssertEqual(PageDrop.destination(dragging: 0, onto: 2, before: trailing), 2)
        // Dragging page 4 back onto the leading half of page 2 → page 4 stands second.
        XCTAssertEqual(PageDrop.destination(dragging: 3, onto: 1, before: leading), 1)
    }

    func testADropThatAsksForNoChangeSaysSo() {
        XCTAssertEqual(PageDrop.destination(dragging: 2, onto: 2, before: true), 2, "onto itself")
        XCTAssertEqual(PageDrop.destination(dragging: 2, onto: 2, before: false), 2)
        XCTAssertEqual(PageDrop.destination(dragging: 1, onto: 0, before: false), 1,
                       "already right after page 0")
        XCTAssertEqual(PageDrop.destination(dragging: 1, onto: 2, before: true), 1,
                       "already right before page 2")
    }

    /// Every drop, on every tile, against the list the move actually produces — so the arithmetic
    /// is checked against the meaning of `movePage` rather than against itself.
    func testEveryDropAgreesWithWhatTheMoveDoes() {
        let pages = ["A", "B", "C", "D", "E"]
        for from in pages.indices {
            for over in pages.indices {
                for before in [true, false] {
                    let to = PageDrop.destination(dragging: from, onto: over, before: before)
                    let moved = pages.shifted(by: .moved(from: from, to: to))
                    XCTAssertEqual(moved.count, pages.count)
                    XCTAssertEqual(moved[to], pages[from],
                                   "the dragged page must end up at the index the drop asked for")
                    XCTAssertEqual(Set(moved), Set(pages), "no drop may lose or duplicate a page")
                    let neighbour = pages[over]
                    if from != over {
                        let landed = moved.firstIndex(of: pages[from])!
                        let next = moved.firstIndex(of: neighbour)!
                        XCTAssertEqual(before ? landed < next : landed > next, true,
                                       "a drop on the leading half goes in front of that page, "
                                       + "a drop on the trailing half behind it")
                    }
                }
            }
        }
    }
}
