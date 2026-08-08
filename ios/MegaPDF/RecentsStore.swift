import Foundation

/// Recent documents — the iOS analog of Android's `RecentFilesStore`.
/// Identity is a security-scoped bookmark (the URL alone loses access rights
/// across launches). JSON in Application Support, atomic replace on write.
struct RecentEntry: Codable, Equatable, Identifiable {
    var id: String { bookmarkBase64 }
    let bookmarkBase64: String
    let displayName: String
    let lastOpenedEpochMs: Int64

    var bookmarkData: Data? { Data(base64Encoded: bookmarkBase64) }
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

    /// Most recent first, deduped by display name + bookmark, capped.
    @discardableResult
    func add(_ entry: RecentEntry) -> [RecentEntry] {
        let updated = ([entry] + load().filter {
            $0.bookmarkBase64 != entry.bookmarkBase64 && $0.displayName != entry.displayName
        }).prefix(maxEntries)
        write(Array(updated))
        return Array(updated)
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
