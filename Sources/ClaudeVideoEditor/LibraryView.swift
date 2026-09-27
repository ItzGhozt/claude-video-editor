import SwiftUI

// MARK: - Save a prompt

/// Sheet for saving a prompt into a folder of the library.
struct SavePromptSheet: View {
    let text: String
    @Environment(\.dismiss) private var dismiss
    @ObservedObject private var library = PromptLibrary.shared
    @State private var title = ""
    @State private var folder = PromptLibrary.defaultFolder
    @State private var newFolder = ""
    @State private var creatingFolder = false

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Label("Save to Saved Prompts", systemImage: "star.fill").font(.title2.bold())
            TextField("Name (optional)", text: $title, prompt: Text(PromptLibrary.cleanTitle("", text: text)))
                .textFieldStyle(.roundedBorder)
            HStack {
                if creatingFolder {
                    TextField("New folder name", text: $newFolder).textFieldStyle(.roundedBorder)
                    Button("Cancel") { creatingFolder = false; newFolder = "" }
                } else {
                    Picker("Folder", selection: $folder) {
                        ForEach(library.folders, id: \.self) { Text($0).tag($0) }
                    }
                    Button { creatingFolder = true } label: { Label("New Folder", systemImage: "folder.badge.plus") }
                }
            }
            ScrollView {
                Text(text).font(.callout).foregroundStyle(.secondary).textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(maxHeight: 160)
            .padding(8)
            .background(RoundedRectangle(cornerRadius: 8).fill(Color.secondary.opacity(0.1)))
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Save") {
                    let f = creatingFolder ? newFolder : folder
                    if creatingFolder { library.addFolder(f) }
                    library.add(title: title, text: text, folder: f)
                    dismiss()
                }
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.defaultAction)
                .disabled(creatingFolder && newFolder.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .padding(20)
        .frame(width: 460)
    }
}

// MARK: - Like / dislike feedback on a reply

/// Popover behind the 👍/👎 buttons: the user says what they liked or didn't,
/// and it's added to their style preferences.
struct FeedbackPopover: View {
    let like: Bool
    @Binding var isPresented: Bool
    var onSaved: () -> Void = {}
    @State private var text = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Label(like ? "What did you like?" : "What didn't you like?",
                  systemImage: like ? "hand.thumbsup.fill" : "hand.thumbsdown.fill")
                .font(.headline)
            Text("Claude will remember this for future edits in every project.")
                .font(.caption).foregroundStyle(.secondary)
            TextField(like ? "e.g. the warm grade, cutting on the beat" : "e.g. fast zoom transitions, captions too big",
                      text: $text, axis: .vertical)
                .lineLimit(2...4)
                .textFieldStyle(.roundedBorder)
                .onSubmit(save)
            HStack {
                Spacer()
                Button("Cancel") { isPresented = false }
                Button("Remember", action: save)
                    .buttonStyle(.borderedProminent)
                    .disabled(text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
        .padding(14)
        .frame(width: 340)
    }

    private func save() {
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        StyleMemory.shared.add(text, like: like)
        onSaved()
        isPresented = false
    }
}

// MARK: - Library window

struct LibraryView: View {
    @EnvironmentObject var store: Store
    @State private var tab = 0

    var body: some View {
        VStack(spacing: 0) {
            Picker("", selection: $tab) {
                Label("Saved Prompts", systemImage: "star").tag(0)
                Label("My Style", systemImage: "heart.text.square").tag(1)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .frame(maxWidth: 320)
            .padding(12)
            Divider()
            if tab == 0 { SavedPromptsPane() } else { StylePane() }
        }
        .frame(minWidth: 720, minHeight: 520)
        .onAppear {
            if ProcessInfo.processInfo.environment["CE_LIBRARY_TAB"] == "style" { tab = 1 }
        }
    }
}

private struct SavedPromptsPane: View {
    @EnvironmentObject var store: Store
    @ObservedObject private var library = PromptLibrary.shared
    @State private var folder = PromptLibrary.defaultFolder
    @State private var selection: SavedPrompt.ID?
    @State private var newFolder = ""
    @State private var addingFolder = false
    @State private var search = ""

    private var shown: [SavedPrompt] {
        let list = search.isEmpty ? library.prompts(in: folder)
            : library.prompts.filter { $0.title.localizedCaseInsensitiveContains(search) || $0.text.localizedCaseInsensitiveContains(search) }
        return list
    }

    var body: some View {
        HSplitView {
            // Folders
            VStack(spacing: 0) {
                List(selection: Binding(get: { folder }, set: { if let f = $0 { folder = f; selection = nil } })) {
                    ForEach(library.folders, id: \.self) { f in
                        HStack {
                            Label(f, systemImage: f == PromptLibrary.defaultFolder ? "star" : "folder")
                            Spacer()
                            Text("\(library.prompts(in: f).count)").foregroundStyle(.secondary).font(.caption)
                        }
                        .tag(f)
                        .contextMenu {
                            if f != PromptLibrary.defaultFolder {
                                Button("Delete Folder (prompts move to Favorites)") { library.deleteFolder(f); folder = PromptLibrary.defaultFolder }
                            }
                        }
                    }
                }
                Divider()
                if addingFolder {
                    HStack {
                        TextField("Folder name", text: $newFolder).textFieldStyle(.roundedBorder)
                            .onSubmit(addFolder)
                        Button("Add", action: addFolder)
                    }
                    .padding(8)
                } else {
                    Button { addingFolder = true } label: { Label("New Folder", systemImage: "folder.badge.plus") }
                        .buttonStyle(.borderless)
                        .padding(8)
                }
            }
            .frame(minWidth: 180, idealWidth: 200, maxWidth: 260)

            // Prompts in the folder
            VStack(spacing: 0) {
                TextField("Search all saved prompts", text: $search)
                    .textFieldStyle(.roundedBorder).padding(8)
                List(shown, selection: $selection) { p in
                    VStack(alignment: .leading, spacing: 2) {
                        Text(p.title).lineLimit(1)
                        Text(p.text).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                    }
                    .padding(.vertical, 2)
                    .tag(p.id)
                }
                .overlay {
                    if shown.isEmpty {
                        VStack(spacing: 6) {
                            Image(systemName: "star").font(.title)
                            Text(search.isEmpty ? "No saved prompts here yet.\nClick the ☆ on any message you've sent to save it."
                                                : "No matches")
                                .multilineTextAlignment(.center)
                        }
                        .foregroundStyle(.secondary)
                    }
                }
            }
            .frame(minWidth: 220, idealWidth: 260)

            // Detail
            Group {
                if let id = selection, let p = library.prompts.first(where: { $0.id == id }) {
                    PromptDetail(prompt: p)
                } else {
                    Text("Select a prompt").foregroundStyle(.secondary).frame(maxWidth: .infinity, maxHeight: .infinity)
                }
            }
            .frame(minWidth: 300)
        }
    }

    private func addFolder() {
        library.addFolder(newFolder)
        if !newFolder.trimmingCharacters(in: .whitespaces).isEmpty { folder = newFolder.trimmingCharacters(in: .whitespaces) }
        newFolder = ""
        addingFolder = false
    }
}

private struct PromptDetail: View {
    @EnvironmentObject var store: Store
    @Environment(\.dismissWindow) private var dismissWindow
    @ObservedObject private var library = PromptLibrary.shared
    let prompt: SavedPrompt
    @State private var title = ""
    @State private var text = ""
    @State private var folder = ""
    @State private var confirmDelete = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            TextField("Name", text: $title).textFieldStyle(.roundedBorder).font(.headline)
            Picker("Folder", selection: $folder) {
                ForEach(library.folders, id: \.self) { Text($0).tag($0) }
            }
            TextEditor(text: $text)
                .font(.body)
                .scrollContentBackground(.hidden)
                .padding(6)
                .background(RoundedRectangle(cornerRadius: 8).fill(Color(nsColor: .textBackgroundColor)))
                .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color.secondary.opacity(0.3)))
            HStack {
                Button(role: .destructive) { confirmDelete = true } label: { Image(systemName: "trash") }
                    .help("Delete")
                    .confirmationDialog("Delete \"\(prompt.title)\"?", isPresented: $confirmDelete) {
                        Button("Delete", role: .destructive) { library.delete(prompt) }
                    }
                Button {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(text, forType: .string)
                } label: { Label("Copy", systemImage: "doc.on.doc") }
                Spacer()
                Button { use() } label: { Label("Use in Chat", systemImage: "text.bubble") }
                    .buttonStyle(.borderedProminent)
                    .disabled(store.selected == nil)
                    .help(store.selected.map { "Put this prompt in the message box for \($0.name)" } ?? "Add a project first")
            }
        }
        .padding(12)
        .onAppear(perform: load)
        .onChange(of: prompt.id) { load() }
        .onChange(of: title) { persist() }
        .onChange(of: text) { persist() }
        .onChange(of: folder) { persist() }
    }

    private func load() {
        title = prompt.title
        text = prompt.text
        folder = prompt.folder
    }

    private func persist() {
        guard !text.isEmpty, title != prompt.title || text != prompt.text || folder != prompt.folder else { return }
        var p = prompt
        p.title = title.isEmpty ? PromptLibrary.cleanTitle("", text: text) : title
        p.text = text
        p.folder = folder
        library.update(p)
    }

    private func use() {
        guard let project = store.selected else { return }
        let s = store.session(for: project)
        s.draft = s.draft.isEmpty ? text : s.draft + "\n\n" + text
        NSApp.activate(ignoringOtherApps: true)
        dismissWindow(id: "library")
    }
}

private struct StylePane: View {
    @ObservedObject private var memory = StyleMemory.shared
    @State private var newLike = ""
    @State private var newDislike = ""
    @State private var confirmClear = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                Text("My Style").font(.title2.bold())
                Text("Claude reads this before every edit and every Prompt Creator prompt. It adds to it when you tell it something lasting (\"I hate fast zooms\"), and when you use 👍 / 👎 on its replies. Edit or remove anything here.")
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            HStack(alignment: .top, spacing: 16) {
                column(title: "Likes", icon: "hand.thumbsup.fill", color: .green, items: memory.likes, newText: $newLike, like: true)
                column(title: "Dislikes", icon: "hand.thumbsdown.fill", color: .red, items: memory.dislikes, newText: $newDislike, like: false)
            }
            HStack {
                Button("Show File in Finder") {
                    NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: StyleMemory.file)])
                }
                Spacer()
                Button("Clear All…", role: .destructive) { confirmClear = true }
                    .disabled(memory.isEmpty)
                    .confirmationDialog("Forget all your style preferences?", isPresented: $confirmClear) {
                        Button("Clear All", role: .destructive) { memory.clear() }
                    }
            }
        }
        .padding(16)
        .onAppear { memory.reload() }
    }

    private func column(title: String, icon: String, color: Color, items: [String], newText: Binding<String>, like: Bool) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Label(title, systemImage: icon).font(.headline).foregroundStyle(color)
            List {
                ForEach(items, id: \.self) { item in
                    StyleRow(item: item)
                }
                if items.isEmpty {
                    Text("Nothing yet").foregroundStyle(.secondary)
                }
            }
            .frame(minHeight: 220)
            HStack {
                TextField(like ? "Add something you like" : "Add something you don't like", text: newText)
                    .textFieldStyle(.roundedBorder)
                    .onSubmit { memory.add(newText.wrappedValue, like: like); newText.wrappedValue = "" }
                Button("Add") { memory.add(newText.wrappedValue, like: like); newText.wrappedValue = "" }
                    .disabled(newText.wrappedValue.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .frame(maxWidth: .infinity)
    }
}

private struct StyleRow: View {
    let item: String
    @State private var editing = false
    @State private var text = ""

    var body: some View {
        HStack {
            if editing {
                TextField("", text: $text).textFieldStyle(.roundedBorder)
                    .onSubmit { StyleMemory.shared.update(old: item, new: text); editing = false }
                Button("Done") { StyleMemory.shared.update(old: item, new: text); editing = false }
            } else {
                Text(item).frame(maxWidth: .infinity, alignment: .leading)
                    .onTapGesture(count: 2) { text = item; editing = true }
                Button { text = item; editing = true } label: { Image(systemName: "pencil") }
                    .buttonStyle(.borderless).help("Edit")
                Button { StyleMemory.shared.remove(item) } label: { Image(systemName: "xmark.circle") }
                    .buttonStyle(.borderless).help("Remove")
            }
        }
    }
}
