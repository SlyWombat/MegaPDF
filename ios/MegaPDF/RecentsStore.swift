import Foundation

/// Recent documents — the iOS analog of Android's `RecentFilesStore`.
/// Identity is a security-scoped bookmark (the URL alone loses access rights
/// across launches). JSON in Application Support, atomic replace on write.
struct RecentEntry: Codable, Equatable, Identifiable {
    var id: String { bookmarkBase64 }
    let bookmarkBase64: String
    let displayName: String
    let lastOpenedEpochMs: Int64

    /// Where the file was when it was opened, for the line under its name (#165).
    ///
    /// Optional, and that is the whole migration: an entry written before #165 has
    /// no `location` key, and a synthesised `Codable` decoder reads a missing key
    /// for an optional property as nil. Such an entry shows without a subtitle
    /// until `ViewerModel` fills it in from its bookmark, or until it is reopened.
    var location: RecentLocation?

    init(bookmarkBase64: String, displayName: String, lastOpenedEpochMs: Int64,
         location: RecentLocation? = nil) {
        self.bookmarkBase64 = bookmarkBase64
        self.displayName = displayName
        self.lastOpenedEpochMs = lastOpenedEpochMs
        self.location = location
    }

    var bookmarkData: Data? { Data(base64Encoded: bookmarkBase64) }

    /// The file name and where it lives, for a screen reader — the two together,
    /// because that is what tells one "agreement.pdf" from another (#2, #165).
    /// Matches the Windows half's phrasing.
    func accessibilityLabel(available: Bool = true) -> String {
        let name = displayName
        switch (location, available) {
        case let (location?, true):
            return String(localized: "\(name), in \(location.subtitle)",
                          comment: "#165: a recent document and the place it lives")
        case let (location?, false):
            return String(localized: "\(name), not found, in \(location.subtitle)",
                          comment: "#165: a recent document that is no longer where it was")
        case (nil, true):
            return name
        case (nil, false):
            return String(localized: "\(name), not found",
                          comment: "#165: a recent document that is no longer where it was")
        }
    }

    /// Two entries are the same document when their bookmarks match, or when the
    /// name and the place both do.
    ///
    /// Bookmark bytes are not stable across re-opens of one file, so the bookmark
    /// alone would let the list fill with copies of the same document. The name
    /// alone — which is what this used before #165 — threw away the second
    /// "agreement.pdf" the moment one was opened from another folder, which is the
    /// very thing this issue is about. An entry that has no location yet (written
    /// before #165) still compares by name, because there is nothing better to
    /// compare it by and collapsing it into its own newer entry is right.
    func isSameDocument(as other: RecentEntry) -> Bool {
        if bookmarkBase64 == other.bookmarkBase64 { return true }
        guard displayName == other.displayName else { return false }
        guard let mine = location, let theirs = other.location else { return true }
        return mine == theirs
    }
}

final class RecentsStore {
    private let fileURL: URL
    private let maxEntries: Int

    init(fileURL: URL? = nil, maxEntries: Int = 10) {
        self.maxEntries = maxEntries
        if let fileURL {
            self.fileURL = fileURL
        } else {
            let dir = FileManager.default.urls(
                for: .applicationSupportDirectory, in: .userDomainMask)[0]
            self.fileURL = dir.appendingPathComponent("recent.json")
        }
    }

    func load() -> [RecentEntry] {
        guard let data = try? Data(contentsOf: fileURL),
              let entries = try? JSONDecoder().decode([RecentEntry].self, from: data)
        else { return [] }  // corrupt store starts fresh rather than crashing
        return entries
    }

    /// Most recent first, one row per document, capped.
    @discardableResult
    func add(_ entry: RecentEntry) -> [RecentEntry] {
        let updated = ([entry] + load().filter { !$0.isSameDocument(as: entry) })
            .prefix(maxEntries)
        write(Array(updated))
        return Array(updated)
    }

    /// Fills in the location of an entry already stored, for the entries written
    /// before #165 (see `ViewerModel.backfillRecentLocations`). A no-op if the
    /// entry has gone in the meantime, so a slow backfill cannot resurrect a row
    /// somebody has just removed.
    @discardableResult
    func setLocation(_ location: RecentLocation?, id: String) -> [RecentEntry] {
        var entries = load()
        guard let index = entries.firstIndex(where: { $0.id == id }) else { return entries }
        entries[index].location = location
        write(entries)
        return entries
    }

    @discardableResult
    func remove(id: String) -> [RecentEntry] {
        let updated = load().filter { $0.id != id }
        write(updated)
        return updated
    }

    private func write(_ entries: [RecentEntry]) {
        guard let data = try? JSONEncoder().encode(entries) else { return }
        try? FileManager.default.createDirectory(
            at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: fileURL, options: .atomic)
    }
}
