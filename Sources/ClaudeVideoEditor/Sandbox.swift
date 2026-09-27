import Foundation

/// Builds the Claude Code settings that confine one session to one project folder.
///
/// Two layers, because they enforce different things:
///  - `sandbox.*` is enforced by macOS (Seatbelt) on every Bash command's actual
///    file and network syscalls. When read rules overlap, the more specific path
///    wins, so "deny ~/Movies, allow ~/Movies/my-shoot" works.
///  - `permissions.*` governs Claude's own Read/Edit tools. There a deny always
///    beats an allow, so a private folder that *contains* the project can't be
///    denied wholesale -- its other entries are denied instead.
///
/// Permission checks stay ON. Sessions run in `acceptEdits` mode: edits inside the
/// allowed folders are auto-approved, sandboxed Bash is auto-approved
/// (`autoAllowBashIfSandboxed`), and anything else is refused -- in `-p` mode there
/// is no one to ask, so an unapproved action fails instead of prompting.
enum Sandbox {
    static let home = Toolchain.home

    /// Home folders that commonly hold private files.
    static let privateDirs = ["Documents", "Desktop", "Downloads", "Pictures", "Movies", "Music",
                              "Library", ".ssh", ".aws", ".gnupg", ".config", ".claude/projects"]

    /// Hosts sandboxed commands may reach: ElevenLabs for video-use transcription,
    /// package indexes for lazily installed helpers, GitHub for downloads.
    static let allowedDomains = [
        "api.elevenlabs.io", "*.elevenlabs.io",
        "pypi.org", "files.pythonhosted.org",
        "github.com", "*.githubusercontent.com",
        "registry.npmjs.org",
    ]

    static var scratchDir: String {
        "/private/tmp/claude-\(getuid())/claude-video-editor"
    }

    /// Writes the settings file for `project` and returns its path.
    static func settingsFile(for project: String) throws -> String {
        let media = [URL(fileURLWithPath: project).resolvingSymlinksInPath().path]
        let uid = getuid()
        // /tmp is a symlink; rules match the literal path a command uses, so list both.
        let scratch = ["/private/tmp/claude-\(uid)", "/tmp/claude-\(uid)"]
        try FileManager.default.createDirectory(atPath: scratchDir, withIntermediateDirectories: true)

        // Skills, read-only. Resolve symlinks: the sandbox checks the real path, and
        // video-use is normally a link into ~/Developer.
        var readOnly = [home + "/.claude/skills"]
        let skillsDir = home + "/.claude/skills"
        for name in (try? FileManager.default.contentsOfDirectory(atPath: skillsDir)) ?? [] {
            let real = URL(fileURLWithPath: skillsDir + "/" + name).resolvingSymlinksInPath().path
            if !real.hasPrefix(skillsDir + "/") { readOnly.append(real) }
        }
        readOnly = prune(Array(Set(readOnly)))

        // The style-memory folder, so Claude can record likes/dislikes the user
        // states. It sits inside ~/Library (denied below); the sandbox lets the
        // narrower allow win, and the tool rules deny everything around it.
        let memory = [URL(fileURLWithPath: StyleMemory.directory).resolvingSymlinksInPath().path]

        let allowed = media + scratch + memory + readOnly
        let privatePaths = privateDirs.map { home + "/" + $0 }

        func rule(_ tool: String, _ path: String) -> String {
            // '//' prefix = absolute path in permission-rule syntax.
            "\(tool)(//\(path.drop(while: { $0 == "/" }))/**)"
        }

        // Permission-level denies: whole private dirs, except where an allowed path
        // lives inside one -- then deny everything around that path instead.
        var toolDeny: [String] = []
        for p in privatePaths {
            let inside = allowed.filter { isInside($0, p) }
            let targets = inside.isEmpty ? [p] : siblingsAround(inside, within: p)
            for t in targets { toolDeny += [rule("Read", t), rule("Edit", t)] }
        }
        toolDeny += readOnly.map { rule("Edit", $0) }
        toolDeny += ["Edit(//etc/**)", "Edit(//usr/**)", "Edit(//opt/**)", "Edit(//System/**)"]

        // Skill: load video-use. Agent: video-use renders animations in parallel
        // sub-agents, which inherit these same rules.
        // ffmpeg/ffprobe: sandboxed Bash is normally auto-approved, but a generated
        // input like `-f lavfi -i color=c=black:s=1080x1920` reads to the path
        // checker as an unknown file and asks for approval, which -p mode turns
        // into a refusal. The kernel sandbox still confines what these commands
        // can read and write.
        var toolAllow: [String] = (media + scratch + memory).flatMap { [rule("Read", $0), rule("Edit", $0)] }
        toolAllow += readOnly.map { rule("Read", $0) }
        toolAllow += ["Skill", "Agent", "Glob", "Grep", "TodoWrite", "Bash(ffmpeg:*)", "Bash(ffprobe:*)"]
        let writable: [String] = media + scratch + memory

        let settings: [String: Any] = [
            "permissions": [
                // Off on purpose: it makes Claude Code guess which paths a Bash command
                // reads, and any path computed at run time (most ffmpeg pipelines)
                // prompts even with permissions skipped. The kernel sandbox below
                // enforces reads on the real syscalls instead.
                "blockReadsOutsideWorkingDirectories": false,
                "additionalDirectories": Array(scratch + memory + readOnly),
                "allow": toolAllow,
                "deny": toolDeny,
            ],
            "sandbox": [
                "enabled": true,
                "failIfUnavailable": true,
                "autoAllowBashIfSandboxed": true,
                "allowUnsandboxedCommands": false,
                "filesystem": [
                    // External volumes need allowRead as well as allowWrite.
                    "allowWrite": writable,
                    "allowRead": allowed,
                    "denyRead": privatePaths + ["/Volumes/Macintosh HD/Users"],
                ],
                "network": ["allowedDomains": allowedDomains],
            ],
        ]

        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Claude Video Editor/sandbox", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let file = dir.appendingPathComponent(safeName(project) + ".json")
        let data = try JSONSerialization.data(withJSONObject: settings, options: [.prettyPrinted, .sortedKeys])
        try data.write(to: file, options: .atomic)
        return file.path
    }

    /// Arguments that start a confined Claude session in `project`.
    static func claudeArguments(for project: String, extra: [String]) throws -> [String] {
        ["--settings", try settingsFile(for: project), "--permission-mode", "acceptEdits"] + extra
    }

    // MARK: - Path helpers

    static func isInside(_ path: String, _ dir: String) -> Bool {
        path == dir || path.hasPrefix(dir.hasSuffix("/") ? dir : dir + "/")
    }

    /// Everything under `root` except the allowed paths and the folders leading to them.
    /// e.g. root ~/Movies, allowed ~/Movies/shoots/day1 -> every other entry of
    /// ~/Movies and of ~/Movies/shoots.
    static func siblingsAround(_ allowed: [String], within root: String) -> [String] {
        var out: [String] = []
        func walk(_ dir: String) {
            let keep = allowed.filter { isInside($0, dir) && $0 != dir }
            guard !keep.isEmpty else { return }
            for entry in (try? FileManager.default.contentsOfDirectory(atPath: dir)) ?? [] {
                let full = dir + "/" + entry
                if allowed.contains(where: { isInside($0, full) }) {
                    if !allowed.contains(full) { walk(full) }
                } else {
                    out.append(full)
                }
            }
        }
        walk(root)
        return out
    }

    /// Drops entries already covered by a parent entry.
    private static func prune(_ paths: [String]) -> [String] {
        paths.sorted().filter { p in !paths.contains { $0 != p && isInside(p, $0) } }
    }

    private static func safeName(_ path: String) -> String {
        path.replacingOccurrences(of: "/", with: "_")
    }
}
