import Foundation

struct ChatItem: Identifiable, Codable, Equatable {
    enum Kind: String, Codable { case user, assistant, tool, toolResult, notice, error }
    var id = UUID()
    var kind: Kind
    var text: String
    /// For tool rows: the full command/input, shown when expanded.
    var detail: String?
}

/// One long-lived `claude -p --input-format stream-json ...` process per project,
/// confined to the project folder by the settings from `Sandbox`. Messages go in on stdin as JSON lines; events come back on stdout.
/// If the process dies (Stop, crash, app relaunch) the next message restarts it
/// with --resume, so the conversation carries on.
final class ClaudeSession: ObservableObject, Identifiable {
    let project: Project
    var id: String { project.path }

    @Published var items: [ChatItem] = []
    @Published var liveText = ""          // assistant text still streaming in
    @Published var isBusy = false
    @Published var draft = ""
    @Published var permissionMode: String?
    /// The model Claude reports for the running session, e.g. "claude-sonnet-5".
    @Published var activeModel: String?
    @Published var turnsFinished = 0      // bumped per reply; the clip list watches it

    private var sessionID: String?
    /// The model choice the running process was started with.
    private var runningModel: ClaudeModel?
    private var process: Process?
    private var stdin: FileHandle?
    private var buffer = Data()
    private var stderrTail = ""

    private static var appendedPrompt: String {
        let py = Toolchain.videoUsePython.map { "Run video-use helpers with `\($0)`, which has their dependencies. " } ?? ""
        return """
    You are running inside "Claude Video Editor", a Mac GUI that wraps this session. The user sees \
    your replies as chat bubbles and has a clip browser showing the video files in this folder. \
    Save rendered outputs inside this folder (for example under an `edits/` or `finished/` \
    subfolder) so they show up in the browser. Refer to files by their path relative to this \
    folder. Keep replies short and plain-spoken; the user is an editor, not a programmer. \
    For editing work use the video-use skill if it is available. \(py)\
    You can only work inside this folder; if something outside it is needed, ask the user to \
    copy it in.

    \(Self.stylePrompt)
    """
    }

    /// The user's likes/dislikes, and how to keep them up to date.
    static var stylePrompt: String {
        StyleMemory.shared.reload()
        let current = StyleMemory.shared.summary.map { "The user's saved style preferences (apply them unless they say otherwise):\n\($0)" }
            ?? "The user hasn't saved any style preferences yet."
        return """
        \(current)
        Their preferences file is \(StyleMemory.file). When the user states a lasting preference \
        about how they like their videos (for example "I hate fast zooms", "always use warm grades", \
        "keep reels under 30 seconds"), update that file with the Edit tool: add one short bullet \
        under "## Likes" or "## Dislikes", don't duplicate, and remove any entry it contradicts. \
        Then tell them in a few words that you saved it. Don't record one-off instructions for a \
        single edit, and never write anything else in that folder.
        """
    }

    init(project: Project) {
        self.project = project
        load()
    }

    // MARK: - Sending

    func send(_ raw: String) {
        let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, !isBusy else { return }
        items.append(ChatItem(kind: .user, text: text))
        draft = ""
        isBusy = true
        // Picked a different model since this session started: restart on the new
        // one. --resume keeps the conversation.
        if process?.isRunning == true, runningModel != ClaudeModel.current {
            let old = process
            process = nil
            old?.terminate()
            items.append(ChatItem(kind: .notice, text: "Now using \(ClaudeModel.current.name)"))
            items.append(items.remove(at: items.count - 2))   // keep the user's message last
        }
        if process?.isRunning != true {
            do { try start() } catch {
                items.append(ChatItem(kind: .error, text: "Couldn't start Claude: \(error.localizedDescription)"))
                isBusy = false
                return
            }
        }
        let msg: [String: Any] = [
            "type": "user",
            "message": ["role": "user", "content": [["type": "text", "text": text]]],
        ]
        guard var line = try? JSONSerialization.data(withJSONObject: msg) else { return }
        line.append(0x0A)
        stdin?.write(line)
        save()
    }

    /// Kills the running turn. The session id is kept, so the next message resumes.
    func stop() {
        guard let p = process, p.isRunning else { return }
        p.terminate()
        if isBusy {
            commitLiveText()
            items.append(ChatItem(kind: .notice, text: "Stopped."))
            isBusy = false
        }
        save()
    }

    func newChat() {
        stop()
        process = nil
        sessionID = nil
        items = []
        liveText = ""
        permissionMode = nil
        save()
    }

    // MARK: - Process

    private func start() throws {
        guard let claude = Toolchain.claude else {
            throw NSError(domain: "ClaudeSession", code: 1,
                          userInfo: [NSLocalizedDescriptionKey: "Claude Code isn't installed. Open Setup from the Help menu."])
        }
        var extra = ["-p",
                    "--input-format", "stream-json",
                    "--output-format", "stream-json",
                    "--verbose", "--include-partial-messages",
                    "--append-system-prompt", Self.appendedPrompt]
        extra += ClaudeModel.current.arguments
        if let sid = sessionID { extra += ["--resume", sid] }

        let p = Process()
        p.executableURL = URL(fileURLWithPath: claude)
        p.arguments = try Sandbox.claudeArguments(for: project.path, extra: extra)
        var env = AuthManager.claudeEnvironment()
        // Pinned so the sandbox's scratch allowance covers it (the default
        // per-login /var/folders path can't be predicted).
        env["TMPDIR"] = Sandbox.scratchDir
        p.environment = env
        p.currentDirectoryURL = URL(fileURLWithPath: project.path)

        let inPipe = Pipe(), outPipe = Pipe(), errPipe = Pipe()
        p.standardInput = inPipe
        p.standardOutput = outPipe
        p.standardError = errPipe

        outPipe.fileHandleForReading.readabilityHandler = { [weak self] h in
            let data = h.availableData
            guard !data.isEmpty else { h.readabilityHandler = nil; return }
            DispatchQueue.main.async { self?.consume(data) }
        }
        errPipe.fileHandleForReading.readabilityHandler = { [weak self] h in
            let data = h.availableData
            guard !data.isEmpty else { h.readabilityHandler = nil; return }
            let s = String(decoding: data, as: UTF8.self)
            DispatchQueue.main.async {
                guard let self else { return }
                self.stderrTail = String((self.stderrTail + s).suffix(2000))
            }
        }
        p.terminationHandler = { [weak self] proc in
            DispatchQueue.main.async { self?.exited(proc) }
        }

        stderrTail = ""
        buffer = Data()
        try p.run()
        process = p
        runningModel = ClaudeModel.current
        stdin = inPipe.fileHandleForWriting
    }

    private func exited(_ proc: Process) {
        guard proc === process else { return }
        process = nil
        stdin = nil
        if isBusy {
            // Died mid-turn without a result event.
            commitLiveText()
            let why = stderrTail
                .split(separator: "\n")
                .filter { !$0.contains("nice(5)") }
                .suffix(6).joined(separator: "\n")
            if proc.terminationReason != .uncaughtSignal {
                items.append(ChatItem(kind: .error, text: why.isEmpty
                    ? "Claude exited unexpectedly (code \(proc.terminationStatus))."
                    : why))
            }
            isBusy = false
            save()
        }
    }

    // MARK: - Parsing stream-json

    /// Test hook: feed one stream-json line as if Claude had printed it.
    func handleLineForTesting(_ line: String) { consume(Data((line + "\n").utf8)) }

    private func consume(_ data: Data) {
        buffer.append(data)
        while let nl = buffer.firstIndex(of: 0x0A) {
            let line = buffer[buffer.startIndex..<nl]
            buffer.removeSubrange(buffer.startIndex...nl)
            guard !line.isEmpty,
                  let obj = try? JSONSerialization.jsonObject(with: line) as? [String: Any]
            else { continue }
            handle(obj)
        }
    }

    private func handle(_ ev: [String: Any]) {
        switch ev["type"] as? String {
        case "system":
            if ev["subtype"] as? String == "init" {
                if let sid = ev["session_id"] as? String { sessionID = sid }
                permissionMode = ev["permissionMode"] as? String
                activeModel = ev["model"] as? String
            }
        case "stream_event":
            if let e = ev["event"] as? [String: Any],
               e["type"] as? String == "content_block_delta",
               let d = e["delta"] as? [String: Any],
               d["type"] as? String == "text_delta",
               let t = d["text"] as? String {
                liveText += t
            }
        case "assistant":
            guard let msg = ev["message"] as? [String: Any],
                  let content = msg["content"] as? [[String: Any]] else { return }
            for block in content {
                switch block["type"] as? String {
                case "text":
                    let t = (block["text"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                    liveText = ""
                    if !t.isEmpty { items.append(ChatItem(kind: .assistant, text: t)) }
                case "tool_use":
                    commitLiveText()
                    items.append(Self.toolItem(block))
                default: break
                }
            }
        case "user":
            guard let msg = ev["message"] as? [String: Any],
                  let content = msg["content"] as? [[String: Any]] else { return }
            for block in content where block["type"] as? String == "tool_result" {
                let text = Self.flatten(block["content"])
                let isErr = block["is_error"] as? Bool ?? false
                items.append(ChatItem(kind: .toolResult,
                                      text: isErr ? "Error" : "Output",
                                      detail: String(text.prefix(4000))))
            }
        case "result":
            commitLiveText()
            if let sid = ev["session_id"] as? String { sessionID = sid }
            if ev["is_error"] as? Bool == true {
                let r = ev["result"] as? String ?? (ev["subtype"] as? String ?? "error")
                // Errors like "You've hit your session limit" also arrive as an
                // assistant message first; show them once, as an error.
                if let last = items.last, last.kind == .assistant,
                   last.text == r.trimmingCharacters(in: .whitespacesAndNewlines) {
                    items.removeLast()
                }
                items.append(ChatItem(kind: .error, text: r))
            }
            isBusy = false
            turnsFinished += 1
            StyleMemory.shared.reload()   // Claude may have added a preference
            save()
        default: break
        }
    }

    private func commitLiveText() {
        let t = liveText.trimmingCharacters(in: .whitespacesAndNewlines)
        if !t.isEmpty { items.append(ChatItem(kind: .assistant, text: t)) }
        liveText = ""
    }

    private static func toolItem(_ block: [String: Any]) -> ChatItem {
        let name = block["name"] as? String ?? "Tool"
        let input = block["input"] as? [String: Any] ?? [:]
        let summary: String
        switch name {
        case "Bash":
            summary = (input["description"] as? String) ?? (input["command"] as? String ?? "Running a command")
        case "Read":
            summary = "Looking at " + ((input["file_path"] as? String).map { ($0 as NSString).lastPathComponent } ?? "a file")
        case "Write", "Edit":
            summary = "Writing " + ((input["file_path"] as? String).map { ($0 as NSString).lastPathComponent } ?? "a file")
        case "Skill":
            summary = "Using skill " + (input["skill"] as? String ?? "")
        default:
            summary = name
        }
        var detail = input["command"] as? String
        if detail == nil, let d = try? JSONSerialization.data(withJSONObject: input, options: [.prettyPrinted, .sortedKeys]) {
            detail = String(decoding: d, as: UTF8.self)
        }
        return ChatItem(kind: .tool, text: summary, detail: detail)
    }

    private static func flatten(_ any: Any?) -> String {
        if let s = any as? String { return s }
        if let arr = any as? [[String: Any]] {
            return arr.map { b in
                if let t = b["text"] as? String { return t }
                return "[\(b["type"] as? String ?? "content")]"
            }.joined(separator: "\n")
        }
        return ""
    }

    // MARK: - Persistence (one chat per project)

    private struct Saved: Codable { var sessionID: String?; var items: [ChatItem] }

    private var saveURL: URL {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Claude Video Editor/chats", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let safe = project.path.replacingOccurrences(of: "/", with: "_")
        return dir.appendingPathComponent(safe + ".json")
    }

    private func save() {
        let s = Saved(sessionID: sessionID, items: items)
        if let d = try? JSONEncoder().encode(s) { try? d.write(to: saveURL, options: .atomic) }
    }

    private func load() {
        guard let d = try? Data(contentsOf: saveURL),
              let s = try? JSONDecoder().decode(Saved.self, from: d) else { return }
        sessionID = s.sessionID
        items = s.items
    }

    deinit { process?.terminate() }
}
