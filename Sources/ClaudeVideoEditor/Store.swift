import AppKit
import Foundation

struct Project: Identifiable, Hashable, Codable {
    var name: String
    var path: String
    var id: String { path }
    var isMounted: Bool {
        var dir: ObjCBool = false
        return FileManager.default.fileExists(atPath: path, isDirectory: &dir) && dir.boolValue
    }
}

final class Store: ObservableObject {
    @Published var projects: [Project] = []
    @Published var selectedPath: String? {
        didSet { UserDefaults.standard.set(selectedPath, forKey: "selectedPath") }
    }
    private var sessions: [String: ClaudeSession] = [:]

    private static let customKey = "projects"

    init() {
        reloadProjects()
        let last = UserDefaults.standard.string(forKey: "selectedPath")
        selectedPath = projects.first(where: { $0.path == last })?.path ?? projects.first?.path
    }

    var selected: Project? { projects.first { $0.path == selectedPath } }

    func session(for p: Project) -> ClaudeSession {
        if let s = sessions[p.path] { return s }
        let s = ClaudeSession(project: p)
        sessions[p.path] = s
        return s
    }

    func reloadProjects() {
        let list = (UserDefaults.standard.data(forKey: Self.customKey))
            .flatMap { try? JSONDecoder().decode([Project].self, from: $0) } ?? []
        // Create sessions now rather than lazily from inside view bodies: a new
        // session loads its saved chat, and publishing that mid-render trips
        // NSTableView's reentrancy warning in the sidebar.
        for p in list where sessions[p.path] == nil { sessions[p.path] = ClaudeSession(project: p) }
        if list != projects { projects = list } else { objectWillChange.send() }  // mount state may have changed
    }

    func addFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.directoryURL = FileManager.default.urls(for: .moviesDirectory, in: .userDomainMask).first
        panel.message = "Choose a folder of footage. Claude will only be able to work inside it."
        panel.prompt = "Add Project"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        add(url)
    }

    func add(_ url: URL) {
        let path = url.resolvingSymlinksInPath().path
        if !projects.contains(where: { $0.path == path }) {
            let p = Project(name: url.lastPathComponent, path: path)
            sessions[p.path] = ClaudeSession(project: p)
            projects.append(p)
            persistCustom()
        }
        selectedPath = path
    }

    func remove(_ p: Project) {
        projects.removeAll { $0.path == p.path }
        persistCustom()
        if selectedPath == p.path { selectedPath = projects.first?.path }
    }

    private func persistCustom() {
        UserDefaults.standard.set(try? JSONEncoder().encode(projects), forKey: Self.customKey)
    }
}
