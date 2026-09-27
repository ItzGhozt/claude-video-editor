import Foundation

/// Turns rambling spoken notes into a clear editing prompt. Runs a one-shot,
/// tool-less Claude call, so it can't touch any files.
enum PromptBuilder {
    static func instructions(notes: String, project: Project, clips: [Clip]) -> String {
        let listing = clips.prefix(120).map { "- \($0.relative)" }.joined(separator: "\n")
        return """
        You help a video editor write instructions for another Claude session that edits their \
        footage with ffmpeg. Below are their spoken, unedited thoughts (speech-to-text, so expect \
        filler words, repeats and mis-heard words). Turn them into ONE clear, detailed prompt they \
        can send to that editing session.

        Project folder: \(project.name) (\(project.path))
        Video files in the folder, newest first:
        \(listing.isEmpty ? "(none found)" : listing)

        Write the prompt in the editor's own voice ("I want…"), organised under short headings, and \
        include only what applies:
        - Goal: what the final video is and where it will be posted
        - Source clips: which files to use. Match mis-heard names to the real filenames above; \
        if unsure, say "the clips of …"
        - Structure: order, what to keep and what to cut, the opening hook and the ending
        - Length and pacing
        - Look and sound: grade, music, ambient audio, transitions
        - Text: captions, subtitles, titles
        - Format: aspect ratio and resolution (default to 9:16, 1080x1920, if they mention \
        reels, TikTok or Instagram)
        - Output: a filename and save location inside the project folder
        - Check-ins: ask to see the cut list before rendering anything

        \(StyleMemory.shared.summary.map { "Their saved style preferences (work in the relevant ones unless their thoughts say otherwise):\n\($0)\n" } ?? "")\
        Keep every specific detail they mention. Don't invent preferences they didn't express. \
        If something important is missing, add a final "Questions for me" section with at most \
        3 short questions. Output ONLY the prompt text: no preamble, no code fences.

        Their thoughts:
        \"\"\"
        \(notes)
        \"\"\"
        """
    }

    static func build(notes: String, project: Project, clips: [Clip],
                      completion: @escaping (Result<String, Error>) -> Void) {
        guard let claude = Toolchain.claude else {
            completion(.failure(NSError(domain: "PromptBuilder", code: 1,
                userInfo: [NSLocalizedDescriptionKey: "Claude Code isn't installed. Open Setup from the Help menu."])))
            return
        }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: claude)
        p.currentDirectoryURL = URL(fileURLWithPath: NSTemporaryDirectory())
        // With the default agent system prompt, a tool-less run still tries to call
        // tools and fails ("tool call could not be parsed"), so replace it outright.
        p.arguments = ["-p", "--output-format", "json", "--tools", "",
                       "--system-prompt", "You are a writing assistant with no tools. You never call tools or take actions; you only reply with the requested text."]
        p.arguments = (p.arguments ?? []) + ClaudeModel.current.arguments
        p.environment = AuthManager.claudeEnvironment()
        let inPipe = Pipe(), outPipe = Pipe(), errPipe = Pipe()
        p.standardInput = inPipe
        p.standardOutput = outPipe
        p.standardError = errPipe
        DispatchQueue.global(qos: .userInitiated).async {
            do { try p.run() } catch {
                DispatchQueue.main.async { completion(.failure(error)) }
                return
            }
            inPipe.fileHandleForWriting.write(Data(instructions(notes: notes, project: project, clips: clips).utf8))
            try? inPipe.fileHandleForWriting.close()
            let out = outPipe.fileHandleForReading.readDataToEndOfFile()
            let err = String(decoding: errPipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
            p.waitUntilExit()
            let obj = try? JSONSerialization.jsonObject(with: out) as? [String: Any]
            let result: Result<String, Error>
            if let text = obj?["result"] as? String, obj?["is_error"] as? Bool != true {
                result = .success(text.trimmingCharacters(in: .whitespacesAndNewlines))
            } else {
                let msg = (obj?["result"] as? String)
                    ?? err.trimmingCharacters(in: .whitespacesAndNewlines)
                result = .failure(NSError(domain: "PromptBuilder", code: Int(p.terminationStatus),
                                          userInfo: [NSLocalizedDescriptionKey: msg.isEmpty ? "Claude didn't return a prompt." : msg]))
            }
            DispatchQueue.main.async { completion(result) }
        }
    }
}
