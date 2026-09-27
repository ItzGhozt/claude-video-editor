import AppKit
import Foundation

/// CI check: `CE_SELF_TEST=/path/report.txt` runs these checks at launch, writes a
/// report and quits with status 0 (all passed) or 1. Mirrors the Windows
/// `--self-test`.
enum SelfTest {
    static func runIfRequested() {
        guard let report = ProcessInfo.processInfo.environment["CE_SELF_TEST"] else { return }
        // Needs the main run loop (sessions deliver events on it), so start once the app is up.
        DispatchQueue.main.async { run(report: report) }
    }

    private static var log = ""
    private static var failures = 0

    private static func check(_ name: String, _ ok: Bool, _ detail: String = "") {
        if !ok { failures += 1 }
        log += "\(ok ? "PASS" : "FAIL")  \(name)\(detail.isEmpty ? "" : "  -- \(detail)")\n"
    }

    private static func run(report: String) {
        let fm = FileManager.default
        let movies = Toolchain.home + "/Movies"
        let project = movies + "/cve-selftest-project"
        let sibling = movies + "/cve-selftest-other"
        try? fm.createDirectory(atPath: project, withIntermediateDirectories: true)
        try? fm.createDirectory(atPath: sibling, withIntermediateDirectories: true)
        let realProject = URL(fileURLWithPath: project).resolvingSymlinksInPath().path

        // 1. Sandbox + permission rules for a project inside a private folder (Movies).
        do {
            let file = try Sandbox.settingsFile(for: project)
            let obj = try JSONSerialization.jsonObject(with: Data(contentsOf: URL(fileURLWithPath: file))) as? [String: Any] ?? [:]
            let perms = obj["permissions"] as? [String: Any] ?? [:]
            let allow = perms["allow"] as? [String] ?? []
            let deny = perms["deny"] as? [String] ?? []
            let fsys = (obj["sandbox"] as? [String: Any])?["filesystem"] as? [String: Any] ?? [:]
            let denyRead = fsys["denyRead"] as? [String] ?? []
            let allowRead = fsys["allowRead"] as? [String] ?? []
            let allowWrite = fsys["allowWrite"] as? [String] ?? []
            let sandbox = obj["sandbox"] as? [String: Any] ?? [:]
            let rule = { (tool: String, path: String) in "\(tool)(//\(path.drop(while: { $0 == "/" }))/**)" }

            check("sandbox enabled", sandbox["enabled"] as? Bool == true)
            check("no unsandboxed escape hatch", sandbox["allowUnsandboxedCommands"] as? Bool == false)
            check("project readable/writable in sandbox", allowRead.contains(realProject) && allowWrite.contains(realProject))
            check("Movies denied at OS level (narrower allow wins)", denyRead.contains(movies))
            check("project readable by Read tool", allow.contains(rule("Read", realProject)))
            check("Movies not denied wholesale for tools", !deny.contains(rule("Read", movies)))
            check("sibling of project denied for tools", deny.contains(rule("Read", sibling)))
            check("project itself not denied", !deny.contains { $0.contains(realProject) })
            check("Documents denied", deny.contains(rule("Read", Toolchain.home + "/Documents")))
            check("ffmpeg allowed (lavfi inputs)", allow.contains("Bash(ffmpeg:*)"))
            check("ElevenLabs reachable", ((sandbox["network"] as? [String: Any])?["allowedDomains"] as? [String] ?? []).contains("api.elevenlabs.io"))
            let args = try Sandbox.claudeArguments(for: project, extra: [])
            check("acceptEdits mode, no permission bypass",
                  args.contains("acceptEdits") && !args.contains { $0.contains("dangerously") }, args.joined(separator: " "))
        } catch {
            check("settings file", false, error.localizedDescription)
        }

        // 2. Stream parsing.
        let s = ClaudeSession(project: Project(name: "parse", path: project))
        s.newChat()
        s.handleLineForTesting(#"{"type":"system","subtype":"init","session_id":"abc","permissionMode":"acceptEdits"}"#)
        s.handleLineForTesting(#"{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Hel"}}}"#)
        check("partial text streams", s.liveText == "Hel")
        s.handleLineForTesting(#"{"type":"assistant","message":{"content":[{"type":"text","text":"Hello"},{"type":"tool_use","name":"Bash","input":{"command":"ffmpeg -version","description":"Check ffmpeg"}}]}}"#)
        s.handleLineForTesting(#"{"type":"user","message":{"content":[{"type":"tool_result","content":"ffmpeg version 7","is_error":false}]}}"#)
        s.handleLineForTesting(#"{"type":"result","subtype":"success","is_error":false,"session_id":"abc","result":"done"}"#)
        let kinds = s.items.map(\.kind.rawValue).joined(separator: ",")
        check("items parsed in order", kinds == "assistant,tool,toolResult", kinds)
        check("tool row uses the description", s.items.first { $0.kind == .tool }?.text == "Check ffmpeg")
        check("permission mode captured", s.permissionMode == "acceptEdits")
        s.newChat()

        // 3. Real Claude Code on this machine (installed by CI), not signed in.
        guard let claude = Toolchain.claude else {
            log += "SKIP  Claude Code not installed\n"
            finish(report: report, cleanup: [project, sibling])
            return
        }
        let v = Process()
        v.executableURL = URL(fileURLWithPath: claude)
        v.arguments = ["--version"]
        let pipe = Pipe()
        v.standardOutput = pipe
        try? v.run()
        v.waitUntilExit()
        let version = String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        check("claude --version", v.terminationStatus == 0 && version.contains("Claude Code"), version.trimmingCharacters(in: .whitespacesAndNewlines))

        // Starts a real sandboxed session with our settings; with no login it should
        // fail cleanly with an error in the chat, not crash or reject the settings.
        let live = ClaudeSession(project: Project(name: "live", path: project))
        live.newChat()
        live.send("Say hi.")
        let deadline = Date().addingTimeInterval(120)
        func poll() {
            if live.isBusy && Date() < deadline {
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.25, execute: poll)
                return
            }
            let summary = live.items.map { "\($0.kind.rawValue): \($0.text)" }.joined(separator: " | ")
            check("real session starts and finishes a turn", !live.isBusy, summary)
            check("settings accepted", !summary.localizedCaseInsensitiveContains("invalid settings"), summary)
            log += "      session transcript: \(summary)\n"
            live.newChat()
            finish(report: report, cleanup: [project, sibling])
        }
        poll()
    }

    private static func finish(report: String, cleanup: [String]) {
        for p in cleanup { try? FileManager.default.removeItem(atPath: p) }
        log += failures == 0 ? "ALL PASSED\n" : "\(failures) FAILED\n"
        try? log.write(toFile: report, atomically: true, encoding: .utf8)
        exit(failures == 0 ? 0 : 1)
    }
}
