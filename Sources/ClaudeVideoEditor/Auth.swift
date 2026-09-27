import Foundation

/// Two ways to pay for Claude:
///  - Claude account (Pro/Max/Team/Enterprise): the same browser sign-in as Claude
///    Code, driven through `claude auth login`. Credentials live where Claude Code
///    keeps them; the app never sees them.
///  - Anthropic API key: stored in the macOS keychain and handed to each Claude
///    process as ANTHROPIC_API_KEY.
final class AuthManager: ObservableObject {
    enum State: Equatable {
        case checking
        case noClaude                     // Claude Code isn't installed yet
        case signedOut
        case signingIn(url: String?)      // browser flow in progress
        case signedIn(email: String, detail: String)
        case apiKey(masked: String)
    }

    @Published var state: State = .checking
    @Published var error: String?

    static let apiKeyAccount = "anthropic-api-key"
    private static let modeKey = "authMode"   // "account" | "apiKey"

    private var loginProcess: Process?
    private var loginStdin: FileHandle?
    private var pollTimer: Timer?

    var isReady: Bool {
        switch state {
        case .signedIn, .apiKey: return true
        default: return false
        }
    }

    /// Environment for every Claude process the app starts.
    static func claudeEnvironment() -> [String: String] {
        var env = Toolchain.environment
        if UserDefaults.standard.string(forKey: modeKey) == "apiKey",
           let key = Keychain.get(apiKeyAccount) {
            env["ANTHROPIC_API_KEY"] = key
        } else {
            // A stray key in the user's shell would silently bill their API account.
            env.removeValue(forKey: "ANTHROPIC_API_KEY")
        }
        return env
    }

    // MARK: - Status

    func refresh() {
        if case .signingIn = state { return }
        if UserDefaults.standard.string(forKey: Self.modeKey) == "apiKey",
           let key = Keychain.get(Self.apiKeyAccount) {
            state = .apiKey(masked: Self.mask(key))
            return
        }
        guard let claude = Toolchain.claude else { state = .noClaude; return }
        DispatchQueue.global(qos: .userInitiated).async {
            let s = Self.status(claude: claude)
            DispatchQueue.main.async {
                if case .signingIn = self.state { return }
                self.state = s
            }
        }
    }

    private static func status(claude: String) -> State {
        guard let out = run(claude, ["auth", "status", "--json"]),
              let obj = try? JSONSerialization.jsonObject(with: Data(out.utf8)) as? [String: Any]
        else { return .signedOut }
        guard obj["loggedIn"] as? Bool == true else { return .signedOut }
        let email = obj["email"] as? String ?? "Signed in"
        var parts: [String] = []
        if let plan = obj["subscriptionType"] as? String { parts.append("Claude \(plan.capitalized)") }
        if let org = obj["orgName"] as? String, !org.isEmpty { parts.append(org) }
        return .signedIn(email: email, detail: parts.joined(separator: " · "))
    }

    // MARK: - Claude account sign-in

    func signInWithClaude() {
        guard let claude = Toolchain.claude else { state = .noClaude; return }
        cancelSignIn()
        error = nil
        UserDefaults.standard.set("account", forKey: Self.modeKey)

        let p = Process()
        p.executableURL = URL(fileURLWithPath: claude)
        p.arguments = ["auth", "login", "--claudeai"]
        p.environment = Toolchain.environment
        let inPipe = Pipe(), outPipe = Pipe()
        p.standardInput = inPipe
        p.standardOutput = outPipe
        p.standardError = outPipe
        var transcript = ""
        outPipe.fileHandleForReading.readabilityHandler = { [weak self] h in
            let d = h.availableData
            guard !d.isEmpty else { h.readabilityHandler = nil; return }
            transcript += String(decoding: d, as: UTF8.self)
            // "If the browser didn't open, visit: https://..."
            if let r = transcript.range(of: #"https://\S+"#, options: .regularExpression) {
                let url = String(transcript[r])
                DispatchQueue.main.async { self?.state = .signingIn(url: url) }
            }
        }
        p.terminationHandler = { [weak self] proc in
            DispatchQueue.main.async {
                guard let self, proc === self.loginProcess else { return }
                self.loginProcess = nil
                self.finishSignIn(failedWith: proc.terminationStatus == 0 ? nil
                    : transcript.split(separator: "\n").last.map(String.init))
            }
        }
        do { try p.run() } catch {
            self.error = "Couldn't start sign-in: \(error.localizedDescription)"
            return
        }
        loginProcess = p
        loginStdin = inPipe.fileHandleForWriting
        state = .signingIn(url: nil)

        // The CLI may finish on its own once the browser redirects; poll so the
        // UI moves on as soon as Claude Code reports a login either way.
        pollTimer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            DispatchQueue.global().async {
                if case .signedIn = Self.status(claude: claude) {
                    DispatchQueue.main.async { self?.finishSignIn(failedWith: nil) }
                }
            }
        }
    }

    /// For when the browser shows a code to paste back into the app.
    func submitCode(_ code: String) {
        let c = code.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !c.isEmpty else { return }
        loginStdin?.write(Data((c + "\n").utf8))
    }

    func cancelSignIn() {
        pollTimer?.invalidate(); pollTimer = nil
        if let p = loginProcess, p.isRunning { loginProcess = nil; p.terminate() }
        loginStdin = nil
        if case .signingIn = state { state = .signedOut }
    }

    private func finishSignIn(failedWith message: String?) {
        guard case .signingIn = state else { return }
        pollTimer?.invalidate(); pollTimer = nil
        if let p = loginProcess, p.isRunning { loginProcess = nil; p.terminate() }
        loginStdin = nil
        state = .checking
        guard let claude = Toolchain.claude else { state = .noClaude; return }
        let s = Self.status(claude: claude)
        state = s
        if case .signedOut = s, let message, !message.isEmpty { error = message }
    }

    // MARK: - API key

    func useAPIKey(_ raw: String, completion: @escaping (Bool) -> Void) {
        let key = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard key.hasPrefix("sk-ant-") else {
            error = "That doesn't look like an Anthropic API key (they start with sk-ant-)."
            completion(false); return
        }
        error = nil
        // Cheap check that the key works: list models.
        var req = URLRequest(url: URL(string: "https://api.anthropic.com/v1/models?limit=1")!)
        req.setValue(key, forHTTPHeaderField: "x-api-key")
        req.setValue("2023-06-01", forHTTPHeaderField: "anthropic-version")
        URLSession.shared.dataTask(with: req) { _, resp, err in
            DispatchQueue.main.async {
                let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
                if code == 200 {
                    Keychain.set(key, for: Self.apiKeyAccount)
                    UserDefaults.standard.set("apiKey", forKey: Self.modeKey)
                    self.state = .apiKey(masked: Self.mask(key))
                    completion(true)
                } else {
                    self.error = err.map { "Couldn't reach Anthropic: \($0.localizedDescription)" }
                        ?? (code == 401 ? "Anthropic rejected that key." : "Anthropic returned an error (\(code)).")
                    completion(false)
                }
            }
        }.resume()
    }

    // MARK: - Sign out

    /// API key: forgets the key. Claude account: runs `claude auth logout`, which
    /// also signs Claude Code out in the terminal (they share the login).
    func signOut() {
        if case .apiKey = state {
            Keychain.delete(Self.apiKeyAccount)
            UserDefaults.standard.set("account", forKey: Self.modeKey)
            refresh()
            return
        }
        guard let claude = Toolchain.claude else { return }
        DispatchQueue.global().async {
            _ = Self.run(claude, ["auth", "logout"])
            DispatchQueue.main.async { self.refresh() }
        }
    }

    // MARK: - Helpers

    private static func mask(_ key: String) -> String {
        key.count > 14 ? String(key.prefix(10)) + "…" + String(key.suffix(4)) : "API key"
    }

    private static func run(_ exe: String, _ args: [String]) -> String? {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: exe)
        p.arguments = args
        p.environment = Toolchain.environment
        let out = Pipe()
        p.standardOutput = out
        p.standardError = Pipe()
        p.standardInput = FileHandle.nullDevice
        do { try p.run() } catch { return nil }
        let d = out.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return String(decoding: d, as: UTF8.self)
    }
}
