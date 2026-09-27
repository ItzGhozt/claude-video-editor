import SwiftUI

struct PromptCreatorView: View {
    @EnvironmentObject var store: Store
    @Environment(\.dismissWindow) private var dismissWindow
    @StateObject private var dictation = Dictation()
    @State private var projectPath: String?
    @State private var notes = ""
    @State private var prompt = ""
    @State private var building = false
    @State private var error: String?
    @State private var saving = false

    private var project: Project? { store.projects.first { $0.path == projectPath } }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Text("Prompt Creator").font(.title2.bold())
                Spacer()
                Picker("Project", selection: $projectPath) {
                    ForEach(store.projects) { p in Text(p.name).tag(Optional(p.path)) }
                }
                .frame(maxWidth: 260)
            }

            // Step 1: talk
            VStack(alignment: .leading, spacing: 8) {
                Label("1. Say what you want", systemImage: "waveform").font(.headline)
                HStack(alignment: .top, spacing: 14) {
                    Button { dictation.toggle() } label: {
                        ZStack {
                            Circle().fill(dictation.isListening ? Color.red : Color.accentColor)
                                .frame(width: 64, height: 64)
                            Image(systemName: dictation.isListening ? "stop.fill" : "mic.fill")
                                .font(.system(size: 26)).foregroundStyle(.white)
                        }
                    }
                    .buttonStyle(.plain)
                    .help(dictation.isListening ? "Stop listening" : "Start talking")
                    .keyboardShortcut("d", modifiers: .command)

                    VStack(alignment: .leading, spacing: 4) {
                        TextEditor(text: $notes)
                            .font(.body)
                            .scrollContentBackground(.hidden)
                            .padding(6)
                            .background(RoundedRectangle(cornerRadius: 8).fill(Color(nsColor: .textBackgroundColor)))
                            .overlay(RoundedRectangle(cornerRadius: 8).stroke(dictation.isListening ? Color.red : Color.secondary.opacity(0.3)))
                            .overlay(alignment: .topLeading) {
                                if notes.isEmpty && dictation.partial.isEmpty {
                                    Text("Click the mic (⌘D) and just talk: what the video is for, which clips, the vibe, music, length… Or type here.")
                                        .foregroundStyle(.secondary).padding(11).allowsHitTesting(false)
                                }
                            }
                            .frame(minHeight: 130)
                        if dictation.isListening {
                            Text(dictation.partial.isEmpty ? "Listening…" : dictation.partial)
                                .font(.callout).italic().foregroundStyle(.secondary).lineLimit(3)
                        }
                        if let p = dictation.problem {
                            Text(p).font(.callout).foregroundStyle(.red)
                        }
                    }
                }
            }

            HStack {
                Button {
                    build()
                } label: {
                    Label(prompt.isEmpty ? "Create Prompt" : "Recreate Prompt", systemImage: "wand.and.stars")
                        .frame(minWidth: 140)
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .disabled(notes.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || building || project == nil)
                .keyboardShortcut(.return, modifiers: .command)
                if building { ProgressView().controlSize(.small); Text("Writing your prompt…").foregroundStyle(.secondary) }
                Spacer()
                Button("Clear") { notes = ""; prompt = ""; error = nil }
                    .disabled(building)
            }
            if let error { Text(error).font(.callout).foregroundStyle(.red).textSelection(.enabled) }

            // Step 2: review
            VStack(alignment: .leading, spacing: 8) {
                Label("2. Check the prompt (you can edit it)", systemImage: "doc.text").font(.headline)
                TextEditor(text: $prompt)
                    .font(.body)
                    .scrollContentBackground(.hidden)
                    .padding(6)
                    .background(RoundedRectangle(cornerRadius: 8).fill(Color(nsColor: .textBackgroundColor)))
                    .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color.secondary.opacity(0.3)))
                    .frame(minHeight: 200)
            }

            HStack {
                Button {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(prompt, forType: .string)
                } label: { Label("Copy", systemImage: "doc.on.doc") }
                Button { saving = true } label: { Label("Save", systemImage: "star") }
                    .help("Save to Saved Prompts")
                    .sheet(isPresented: $saving) { SavePromptSheet(text: prompt) }
                Spacer()
                Button { deliver(sendNow: false) } label: { Label("Put in Chat", systemImage: "text.bubble") }
                    .help("Put the prompt in the chat box so you can tweak it before sending")
                Button { deliver(sendNow: true) } label: { Label("Send to Claude", systemImage: "paperplane.fill") }
                    .buttonStyle(.borderedProminent)
            }
            .disabled(prompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || project == nil)
        }
        .padding(20)
        .frame(minWidth: 620, minHeight: 640)
        .onAppear {
            projectPath = projectPath ?? store.selectedPath
            dictation.onPhrase = { phrase in
                if notes.isEmpty || notes.hasSuffix(" ") || notes.hasSuffix("\n") {
                    notes += phrase
                } else {
                    notes += " " + phrase
                }
            }
        }
        .onDisappear { if dictation.isListening { dictation.stop() } }
    }

    private func build() {
        guard let project else { return }
        if dictation.isListening { dictation.stop() }
        building = true
        error = nil
        let clips = ClipScanner.scan(project.path)
        PromptBuilder.build(notes: notes, project: project, clips: clips) { result in
            building = false
            switch result {
            case .success(let text): prompt = text
            case .failure(let e): error = e.localizedDescription
            }
        }
    }

    private func deliver(sendNow: Bool) {
        guard let project else { return }
        let session = store.session(for: project)
        store.selectedPath = project.path
        if sendNow && !session.isBusy {
            session.send(prompt)
        } else {
            session.draft = session.draft.isEmpty ? prompt : session.draft + "\n\n" + prompt
        }
        dismissWindow(id: "prompt-creator")
        NSApp.activate(ignoringOtherApps: true)
    }
}
