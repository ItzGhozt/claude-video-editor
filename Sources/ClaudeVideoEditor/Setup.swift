import AppKit
import Foundation

/// The first-run checklist: everything video-use needs, each with a status check
/// and (where it can be done without a password) a one-click installer.
final class SetupManager: ObservableObject {
    enum Item: String, CaseIterable, Identifiable {
        case claude, homebrew, ffmpeg, uv, videoUse, elevenLabs
        var id: String { rawValue }

        var title: String {
            switch self {
            case .claude: return "Claude Code"
            case .homebrew: return "Homebrew"
            case .ffmpeg: return "ffmpeg"
            case .uv: return "uv (Python tools)"
            case .videoUse: return "video-use editing skill"
            case .elevenLabs: return "ElevenLabs key (transcription)"
            }
        }

        var blurb: String {
            switch self {
            case .claude: return "The Claude agent that does the editing. Installed from claude.ai."
            case .homebrew: return "The standard Mac package manager, used to install ffmpeg. Needs your Mac password, so it opens in Terminal."
            case .ffmpeg: return "Cuts, grades and renders the video."
            case .uv: return "Sets up the Python libraries video-use uses. Installed from astral.sh."
            case .videoUse: return "The open-source editing skill by Browser Use (github.com/browser-use/video-use). Installed to ~/Developer/video-use."
            case .elevenLabs: return "video-use transcribes speech with ElevenLabs Scribe, so it can cut on words. Free keys at elevenlabs.io."
            }
        }

        /// Optional items don't block "Continue".
        var optional: Bool { self == .elevenLabs }
    }

    enum Status: Equatable { case unknown, ok, missing, working, failed(String) }

    @Published var status: [Item: Status] = [:]
    @Published var log: [Item: String] = [:]

    static let elevenLabsDocs = URL(string: "https://elevenlabs.io/app/settings/api-keys")!

    var allRequiredDone: Bool {
        Item.allCases.filter { !$0.optional }.allSatisfy { status[$0] == .ok }
    }

    func refresh() {
        DispatchQueue.global(qos: .userInitiated).async {
            var s: [Item: Status] = [:]
            s[.claude] = Toolchain.claude != nil ? .ok : .missing
            s[.homebrew] = Toolchain.brew != nil ? .ok : (Toolchain.ffmpeg != nil ? .ok : .missing)
            s[.ffmpeg] = Toolchain.ffmpeg != nil && Toolchain.ffprobe != nil ? .ok : .missing
            s[.uv] = Toolchain.uv != nil ? .ok : .missing
            s[.videoUse] = Toolchain.videoUseRegistered && Toolchain.videoUsePython != nil ? .ok : .missing
            s[.elevenLabs] = Self.elevenLabsKey() != nil ? .ok : .missing
            DispatchQueue.main.async {
                for (k, v) in s where self.status[k] != .working { self.status[k] = v }
            }
        }
    }

    // MARK: - Installers

    func install(_ item: Item) {
        switch item {
        case .claude:
            run(item, "curl -fsSL https://claude.ai/install.sh | bash")
        case .homebrew:
            openInTerminal(#"/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)""#)
            log[item] = "Homebrew's installer is running in Terminal. Follow the prompts there (it asks for your Mac password), then click Check Again."
        case .ffmpeg:
            guard let brew = Toolchain.brew else { status[item] = .failed("Install Homebrew first."); return }
            run(item, "'\(brew)' install ffmpeg")
        case .uv:
            run(item, "curl -LsSf https://astral.sh/uv/install.sh | sh")
        case .videoUse:
            installVideoUse()
        case .elevenLabs:
            break // handled by saveElevenLabsKey
        }
    }

    /// Download video-use from its GitHub repo (a tarball, so git isn't needed),
    /// register it as a Claude Code skill and install its Python dependencies.
    private func installVideoUse() {
        let dir = Toolchain.videoUseDir
        let q = { (s: String) in "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'" }
        var script = "set -e\n"
        if Toolchain.uv == nil {
            script += "echo 'Installing uv first…'\ncurl -LsSf https://astral.sh/uv/install.sh | sh\nexport PATH=\"$HOME/.local/bin:$PATH\"\n"
        }
        script += """
        if [ ! -f \(q(dir))/SKILL.md ]; then
          echo 'Downloading video-use from github.com/browser-use/video-use…'
          mkdir -p "$HOME/Developer"
          tmp=$(mktemp -d)
          curl -fsSL https://github.com/browser-use/video-use/archive/refs/heads/main.tar.gz | tar xz -C "$tmp"
          mv "$tmp/video-use-main" \(q(dir))
        fi
        mkdir -p "$HOME/.claude/skills"
        [ -e "$HOME/.claude/skills/video-use" ] || ln -s \(q(dir)) "$HOME/.claude/skills/video-use"
        cd \(q(dir))
        echo 'Installing Python libraries (this can take a minute)…'
        uv sync
        echo 'Done.'
        """
        run(.videoUse, script)
    }

    private func run(_ item: Item, _ script: String) {
        status[item] = .working
        log[item] = ""
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/bash")
        p.arguments = ["-c", script]
        p.environment = Toolchain.environment
        p.standardInput = FileHandle.nullDevice
        let out = Pipe()
        p.standardOutput = out
        p.standardError = out
        out.fileHandleForReading.readabilityHandler = { [weak self] h in
            let d = h.availableData
            guard !d.isEmpty else { h.readabilityHandler = nil; return }
            let s = String(decoding: d, as: UTF8.self)
            DispatchQueue.main.async {
                guard let self else { return }
                self.log[item] = String(((self.log[item] ?? "") + s).suffix(6000))
            }
        }
        p.terminationHandler = { [weak self] proc in
            DispatchQueue.main.async {
                guard let self else { return }
                if proc.terminationStatus == 0 {
                    self.status[item] = .unknown
                    self.refresh()
                } else {
                    let last = (self.log[item] ?? "").split(separator: "\n").last.map(String.init) ?? ""
                    self.status[item] = .failed(last.isEmpty ? "Install failed (code \(proc.terminationStatus))." : last)
                }
            }
        }
        do { try p.run() } catch { status[item] = .failed(error.localizedDescription) }
    }

    private func openInTerminal(_ command: String) {
        let escaped = command.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
        let src = "tell application \"Terminal\"\nactivate\ndo script \"\(escaped)\"\nend tell"
        var err: NSDictionary?
        NSAppleScript(source: src)?.executeAndReturnError(&err)
        if err != nil {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(command, forType: .string)
            log[.homebrew] = "Couldn't open Terminal automatically. The install command is on your clipboard: open Terminal, paste it and press Return."
        }
    }

    // MARK: - ElevenLabs key (video-use reads it from .env in its folder)

    static func elevenLabsKey() -> String? {
        if let k = ProcessInfo.processInfo.environment["ELEVENLABS_API_KEY"], !k.isEmpty { return k }
        guard let text = try? String(contentsOfFile: Toolchain.videoUseDir + "/.env", encoding: .utf8) else { return nil }
        for line in text.split(separator: "\n") where line.hasPrefix("ELEVENLABS_API_KEY=") {
            let v = line.dropFirst("ELEVENLABS_API_KEY=".count).trimmingCharacters(in: .whitespaces)
            if !v.isEmpty { return v }
        }
        return nil
    }

    func saveElevenLabsKey(_ raw: String) {
        let key = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !key.isEmpty else { return }
        guard Toolchain.videoUseInstalled else {
            status[.elevenLabs] = .failed("Install video-use first; the key is saved in its folder.")
            return
        }
        status[.elevenLabs] = .working
        // Same quota-free check video-use's own installer uses.
        var req = URLRequest(url: URL(string: "https://api.elevenlabs.io/v1/user")!)
        req.setValue(key, forHTTPHeaderField: "xi-api-key")
        URLSession.shared.dataTask(with: req) { _, resp, _ in
            let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
            DispatchQueue.main.async {
                if code == 401 {
                    self.status[.elevenLabs] = .failed("ElevenLabs rejected that key. Check it and paste it again.")
                    return
                }
                let path = Toolchain.videoUseDir + "/.env"
                var lines = ((try? String(contentsOfFile: path, encoding: .utf8)) ?? "")
                    .split(separator: "\n", omittingEmptySubsequences: false).map(String.init)
                    .filter { !$0.hasPrefix("ELEVENLABS_API_KEY=") && !$0.isEmpty }
                lines.append("ELEVENLABS_API_KEY=\(key)")
                do {
                    try (lines.joined(separator: "\n") + "\n").write(toFile: path, atomically: true, encoding: .utf8)
                    try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: path)
                    self.status[.elevenLabs] = .ok
                    // 200 = verified; anything else (offline, 5xx) gets checked on first transcription.
                    self.log[.elevenLabs] = code == 200 ? "Key verified and saved." : "Key saved (couldn't verify it right now)."
                } catch {
                    self.status[.elevenLabs] = .failed("Couldn't save the key: \(error.localizedDescription)")
                }
            }
        }.resume()
    }
}
