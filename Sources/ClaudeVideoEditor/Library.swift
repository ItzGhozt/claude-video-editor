import Foundation

private var appSupportDir: URL {
    let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("Claude Video Editor", isDirectory: true)
    try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    return dir
}

// MARK: - Saved prompts

struct SavedPrompt: Identifiable, Codable, Hashable {
    var id = UUID()
    var title: String
    var text: String
    var folder: String
    var created = Date()
}

/// Favourite prompts, filed into folders, shared by every project.
final class PromptLibrary: ObservableObject {
    static let shared = PromptLibrary()
    static let defaultFolder = "Favorites"

    @Published private(set) var prompts: [SavedPrompt] = []
    /// Folders the user created, including empty ones.
    @Published private(set) var extraFolders: [String] = []

    private var url: URL { appSupportDir.appendingPathComponent("saved-prompts.json") }
    private struct Saved: Codable { var prompts: [SavedPrompt]; var folders: [String] }

    init() {
        if let d = try? Data(contentsOf: url), let s = try? JSONDecoder().decode(Saved.self, from: d) {
            prompts = s.prompts
            extraFolders = s.folders
        }
    }

    var folders: [String] {
        var seen = Set<String>()
        return ([Self.defaultFolder] + extraFolders + prompts.map(\.folder).sorted())
            .filter { seen.insert($0).inserted }
    }

    func prompts(in folder: String) -> [SavedPrompt] {
        prompts.filter { $0.folder == folder }.sorted { $0.created > $1.created }
    }

    func add(title: String, text: String, folder: String) {
        let f = folder.trimmingCharacters(in: .whitespaces).isEmpty ? Self.defaultFolder : folder.trimmingCharacters(in: .whitespaces)
        prompts.append(SavedPrompt(title: Self.cleanTitle(title, text: text), text: text, folder: f))
        save()
    }

    func update(_ p: SavedPrompt) {
        guard let i = prompts.firstIndex(where: { $0.id == p.id }) else { return }
        prompts[i] = p
        save()
    }

    func delete(_ p: SavedPrompt) {
        prompts.removeAll { $0.id == p.id }
        save()
    }

    func addFolder(_ name: String) {
        let n = name.trimmingCharacters(in: .whitespaces)
        guard !n.isEmpty, !folders.contains(n) else { return }
        extraFolders.append(n)
        save()
    }

    /// Deletes a folder; its prompts move to Favorites.
    func deleteFolder(_ name: String) {
        guard name != Self.defaultFolder else { return }
        extraFolders.removeAll { $0 == name }
        for i in prompts.indices where prompts[i].folder == name { prompts[i].folder = Self.defaultFolder }
        save()
    }

    func contains(text: String) -> Bool { prompts.contains { $0.text == text } }

    /// A short title from the first words when the user doesn't give one.
    static func cleanTitle(_ title: String, text: String) -> String {
        let t = title.trimmingCharacters(in: .whitespacesAndNewlines)
        if !t.isEmpty { return t }
        let words = text.split(whereSeparator: \.isWhitespace).prefix(7).joined(separator: " ")
        return words.count < text.count ? words + "…" : words
    }

    private func save() {
        if let d = try? JSONEncoder().encode(Saved(prompts: prompts, folders: extraFolders)) {
            try? d.write(to: url, options: .atomic)
        }
    }
}

// MARK: - Style memory

/// The user's likes and dislikes, kept as a small Markdown file that every Claude
/// session reads at start and may update when the user states a lasting preference.
/// The user can review and edit it in the Library window.
final class StyleMemory: ObservableObject {
    static let shared = StyleMemory()

    @Published private(set) var likes: [String] = []
    @Published private(set) var dislikes: [String] = []

    static var directory: String {
        let d = appSupportDir.appendingPathComponent("memory", isDirectory: true)
        try? FileManager.default.createDirectory(at: d, withIntermediateDirectories: true)
        return d.path
    }
    static var file: String { directory + "/preferences.md" }

    init() { reload() }

    var isEmpty: Bool { likes.isEmpty && dislikes.isEmpty }

    func reload() {
        guard let text = try? String(contentsOfFile: Self.file, encoding: .utf8) else {
            write()   // create the file so Claude can edit it
            return
        }
        var l: [String] = [], d: [String] = [], section = ""
        for raw in text.split(separator: "\n") {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.lowercased().hasPrefix("## like") { section = "likes"; continue }
            if line.lowercased().hasPrefix("## dislike") { section = "dislikes"; continue }
            guard line.hasPrefix("- ") || line.hasPrefix("* ") else { continue }
            let item = String(line.dropFirst(2)).trimmingCharacters(in: .whitespaces)
            guard !item.isEmpty else { continue }
            if section == "likes" { l.append(item) } else if section == "dislikes" { d.append(item) }
        }
        if l != likes { likes = l }
        if d != dislikes { dislikes = d }
    }

    func add(_ text: String, like: Bool) {
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !t.isEmpty else { return }
        reload()
        if like { if !likes.contains(t) { likes.append(t) }; dislikes.removeAll { $0 == t } }
        else { if !dislikes.contains(t) { dislikes.append(t) }; likes.removeAll { $0 == t } }
        write()
    }

    func remove(_ text: String) {
        reload()
        likes.removeAll { $0 == text }
        dislikes.removeAll { $0 == text }
        write()
    }

    func update(old: String, new: String) {
        let n = new.trimmingCharacters(in: .whitespacesAndNewlines)
        reload()
        if let i = likes.firstIndex(of: old) { if n.isEmpty { likes.remove(at: i) } else { likes[i] = n } }
        if let i = dislikes.firstIndex(of: old) { if n.isEmpty { dislikes.remove(at: i) } else { dislikes[i] = n } }
        write()
    }

    func clear() {
        likes = []
        dislikes = []
        write()
    }

    /// Text for system prompts: the list itself, or nil when there's nothing yet.
    var summary: String? {
        guard !isEmpty else { return nil }
        var s = ""
        if !likes.isEmpty { s += "Likes:\n" + likes.map { "- " + $0 }.joined(separator: "\n") + "\n" }
        if !dislikes.isEmpty { s += "Dislikes:\n" + dislikes.map { "- " + $0 }.joined(separator: "\n") + "\n" }
        return s
    }

    private func write() {
        let text = """
        # My editing style

        Preferences the user has told Claude Video Editor. Claude reads this before every edit.
        Keep each item short. One per line, starting with "- ".

        ## Likes
        \(likes.map { "- " + $0 }.joined(separator: "\n"))

        ## Dislikes
        \(dislikes.map { "- " + $0 }.joined(separator: "\n"))

        """
        try? text.write(toFile: Self.file, atomically: true, encoding: .utf8)
    }
}
