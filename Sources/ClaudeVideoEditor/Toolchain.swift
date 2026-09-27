import Foundation

/// Finds the command-line tools the app depends on. Apps opened from Finder get a
/// bare PATH (/usr/bin:/bin:...), so nothing installed by Homebrew, the Claude Code
/// installer or uv is visible unless we look in the usual places ourselves.
enum Toolchain {
    static let home = FileManager.default.homeDirectoryForCurrentUser.path

    /// Where video-use is installed when the app installs it (the path its own
    /// install.md recommends).
    static let defaultVideoUseDir = home + "/Developer/video-use"
    static let skillLink = home + "/.claude/skills/video-use"

    private static let searchDirs = [
        home + "/.local/bin", home + "/.claude/local", "/opt/homebrew/bin",
        "/usr/local/bin", home + "/.cargo/bin", "/usr/bin", "/bin",
    ]

    static var claude: String? { find("claude") }
    static var ffmpeg: String? { find("ffmpeg") }
    static var ffprobe: String? { find("ffprobe") }
    static var brew: String? {
        ["/opt/homebrew/bin/brew", "/usr/local/bin/brew"].first(where: isExec)
    }
    static var uv: String? { find("uv") }

    /// The video-use checkout Claude will actually load: wherever the skill link
    /// points, else the default install location.
    static var videoUseDir: String {
        let link = URL(fileURLWithPath: skillLink).resolvingSymlinksInPath().path
        if FileManager.default.fileExists(atPath: link + "/SKILL.md") { return link }
        return defaultVideoUseDir
    }
    static var videoUseInstalled: Bool {
        FileManager.default.fileExists(atPath: videoUseDir + "/SKILL.md")
    }
    static var videoUseRegistered: Bool {
        FileManager.default.fileExists(atPath: skillLink + "/SKILL.md")
    }
    static var videoUsePython: String? {
        let p = videoUseDir + "/.venv/bin/python"
        return isExec(p) ? p : nil
    }

    static func find(_ name: String) -> String? {
        if let hit = searchDirs.map({ $0 + "/" + name }).first(where: isExec) { return hit }
        return loginShellLookup(name)
    }

    private static var shellCache: [String: String] = [:]

    /// Last resort: ask the user's login shell, which knows about nvm, asdf and
    /// other PATH tweaks in their dotfiles.
    private static func loginShellLookup(_ name: String) -> String? {
        if let c = shellCache[name] { return c.isEmpty ? nil : c }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/zsh")
        p.arguments = ["-lic", "command -v \(name)"]
        let out = Pipe()
        p.standardOutput = out
        p.standardError = Pipe()
        p.standardInput = FileHandle.nullDevice
        do { try p.run() } catch { return nil }
        let deadline = Date().addingTimeInterval(5)
        while p.isRunning && Date() < deadline { usleep(50_000) }
        if p.isRunning { p.terminate(); shellCache[name] = ""; return nil }
        let s = String(decoding: out.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
            .split(separator: "\n").last.map(String.init)?.trimmingCharacters(in: .whitespaces) ?? ""
        let hit = s.hasPrefix("/") && isExec(s) ? s : ""
        shellCache[name] = hit
        return hit.isEmpty ? nil : hit
    }

    static func isExec(_ path: String) -> Bool {
        FileManager.default.isExecutableFile(atPath: path)
    }

    /// PATH for every child process: video-use's venv first (so `python` has its
    /// dependencies), then the directories the tools were found in.
    static var environment: [String: String] {
        var env = ProcessInfo.processInfo.environment
        var dirs: [String] = []
        if videoUsePython != nil { dirs.append(videoUseDir + "/.venv/bin") }
        for tool in [claude, ffmpeg, uv] {
            if let t = tool { dirs.append((t as NSString).deletingLastPathComponent) }
        }
        dirs += searchDirs + ["/usr/sbin", "/sbin"]
        dirs += (env["PATH"] ?? "").split(separator: ":").map(String.init)
        var seen = Set<String>()
        env["PATH"] = dirs.filter { seen.insert($0).inserted }.joined(separator: ":")
        env["HOME"] = home
        return env
    }
}
