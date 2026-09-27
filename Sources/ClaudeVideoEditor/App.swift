import AppKit
import SwiftUI

@main
struct ClaudeVideoEditorApp: App {
    @StateObject private var store = Store()
    @StateObject private var auth = AuthManager()
    @StateObject private var setup = SetupManager()

    init() {
        // Launched as a bare executable (e.g. `swift run`), macOS won't give us a
        // Dock icon or keyboard focus; the .app bundle doesn't need this.
        NSApplication.shared.setActivationPolicy(.regular)
        DevSnapshot.scheduleIfRequested()
        SelfTest.runIfRequested()
    }

    var body: some Scene {
        WindowGroup("Claude Video Editor") {
            RootView()
                .environmentObject(store).environmentObject(auth).environmentObject(setup)
        }
        .defaultSize(width: 1320, height: 820)
        .commands {
            CommandGroup(after: .newItem) {
                MenuButtons(kind: .promptCreator)
                MenuButtons(kind: .library)
            }
            CommandGroup(replacing: .help) {
                MenuButtons(kind: .guide)
                MenuButtons(kind: .credits)
                Divider()
                Link("video-use on GitHub", destination: URL(string: "https://github.com/browser-use/video-use")!)
                Link("Claude Video Editor on GitHub", destination: URL(string: "https://github.com/ItzGhozt/claude-video-editor")!)
            }
        }

        Window("Prompt Creator", id: "prompt-creator") {
            PromptCreatorView().environmentObject(store)
        }
        .defaultSize(width: 680, height: 720)

        Window("Library", id: "library") {
            LibraryView().environmentObject(store)
        }
        .defaultSize(width: 900, height: 620)

        Window("Start Guide", id: "guide") { StartGuideView().frame(minWidth: 560, minHeight: 600) }
            .defaultSize(width: 720, height: 760)

        Window("Credits", id: "credits") { CreditsView() }
            .defaultSize(width: 600, height: 560)

        Settings {
            SettingsView().environmentObject(auth).environmentObject(setup)
        }
    }
}

private struct MenuButtons: View {
    enum Kind { case promptCreator, library, guide, credits }
    let kind: Kind
    @Environment(\.openWindow) private var openWindow
    var body: some View {
        switch kind {
        case .promptCreator:
            Button("Prompt Creator") { openWindow(id: "prompt-creator") }
                .keyboardShortcut("p", modifiers: [.command, .shift])
        case .library:
            Button("Saved Prompts & My Style") { openWindow(id: "library") }
                .keyboardShortcut("l", modifiers: [.command, .shift])
        case .guide:
            Button("Start Guide") { openWindow(id: "guide") }
        case .credits:
            Button("Credits & Licenses") { openWindow(id: "credits") }
        }
    }
}

struct SettingsView: View {
    var body: some View {
        TabView {
            SignInView().tabItem { Label("Account", systemImage: "person.crop.circle") }
            SetupChecklistView().tabItem { Label("Setup", systemImage: "wrench.and.screwdriver") }
        }
        .frame(width: 760, height: 640)
    }
}

/// First run shows onboarding; afterwards the editor, with a sign-in sheet if the
/// account was signed out in the meantime.
struct RootView: View {
    @EnvironmentObject var auth: AuthManager
    @EnvironmentObject var setup: SetupManager
    @AppStorage("onboardingDone") private var onboardingDone = false

    var body: some View {
        if onboardingDone {
            MainView()
                .sheet(isPresented: .constant(needsSignIn)) {
                    VStack(spacing: 0) {
                        SignInView()
                        Divider()
                        HStack { Spacer(); Button("Quit") { NSApp.terminate(nil) } }.padding(12)
                    }
                    .frame(width: 600, height: 560)
                }
                .onAppear { auth.refresh(); setup.refresh() }
                .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
                    auth.refresh()
                }
        } else {
            OnboardingView().frame(minWidth: 820, minHeight: 640)
        }
    }

    private var needsSignIn: Bool {
        switch auth.state {
        case .signedOut, .noClaude, .signingIn: return true
        default: return false
        }
    }
}

struct MainView: View {
    @EnvironmentObject var store: Store
    @EnvironmentObject var setup: SetupManager
    @Environment(\.openWindow) private var openWindow
    @State private var confirmNewChat = false

    var body: some View {
        NavigationSplitView {
            List(selection: $store.selectedPath) {
                Section("Projects") {
                    ForEach(store.projects) { p in
                        ProjectRow(project: p, session: store.session(for: p))
                            .tag(Optional(p.path))
                            .contextMenu {
                                Button("Show in Finder") { NSWorkspace.shared.open(URL(fileURLWithPath: p.path)) }
                                Button("Remove from List") { store.remove(p) }
                            }
                    }
                }
            }
            .dropDestination(for: URL.self) { urls, _ in
                let dirs = urls.filter { (try? $0.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true }
                dirs.forEach(store.add)
                return !dirs.isEmpty
            }
            .overlay {
                if store.projects.isEmpty {
                    VStack(spacing: 6) {
                        Image(systemName: "folder.badge.plus").font(.title)
                        Text("Add a folder of footage,\nor drop one here").multilineTextAlignment(.center)
                    }
                    .foregroundStyle(.secondary)
                }
            }
            .safeAreaInset(edge: .bottom) {
                VStack(spacing: 8) {
                    if !setup.allRequiredDone {
                        SettingsLink {
                            Label("Finish Setup", systemImage: "exclamationmark.triangle").frame(maxWidth: .infinity)
                        }
                        .tint(.orange)
                    }
                    Button { openWindow(id: "prompt-creator") } label: {
                        Label("Prompt Creator", systemImage: "mic.badge.plus").frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
                    Button { openWindow(id: "library") } label: {
                        Label("Saved Prompts & Style", systemImage: "star").frame(maxWidth: .infinity)
                    }
                    Button { store.addFolder() } label: {
                        Label("Add Folder…", systemImage: "folder.badge.plus").frame(maxWidth: .infinity)
                    }
                }
                .padding(10)
            }
            .navigationSplitViewColumnWidth(min: 190, ideal: 220)
        } content: {
            Group {
                if let p = store.selected {
                    if p.isMounted {
                        ChatView(session: store.session(for: p))
                            .id(p.path)
                            .navigationTitle(p.name)
                            .navigationSubtitle(p.path)
                    } else {
                        ContentUnavailableView("Folder not found",
                                               systemImage: "externaldrive.badge.xmark",
                                               description: Text("\(p.path) isn't available. If it's on an external drive, plug it in and pick the project again."))
                    }
                } else {
                    ContentUnavailableView {
                        Label("Add a project", systemImage: "film.stack")
                    } description: {
                        Text("A project is a folder of footage. Claude can only work inside the folder you pick.")
                    } actions: {
                        Button("Add Folder…") { store.addFolder() }.buttonStyle(.borderedProminent)
                        Button("Start Guide") { openWindow(id: "guide") }
                    }
                }
            }
            .navigationSplitViewColumnWidth(min: 440, ideal: 640)
        } detail: {
            Group {
                if let p = store.selected, p.isMounted {
                    ClipsView(session: store.session(for: p)).id(p.path)
                } else {
                    Color.clear
                }
            }
            .navigationSplitViewColumnWidth(min: 340, ideal: 440)
        }
        .toolbar {
            if let p = store.selected {
                let s = store.session(for: p)
                ToolbarItem(placement: .primaryAction) {
                    Button { confirmNewChat = true } label: { Label("New Chat", systemImage: "square.and.pencil") }
                        .help("Start a fresh conversation for this project")
                        .confirmationDialog("Start a new chat for \(p.name)?", isPresented: $confirmNewChat) {
                            Button("New Chat", role: .destructive) { s.newChat() }
                        } message: {
                            Text("The current conversation will be cleared. Files in the folder are not touched.")
                        }
                }
                ToolbarItem(placement: .primaryAction) {
                    Button { NSWorkspace.shared.open(URL(fileURLWithPath: p.path)) } label: {
                        Label("Open Folder", systemImage: "folder")
                    }
                    .help("Open the project folder in Finder")
                }
            }
        }
        .onAppear {
            let env = ProcessInfo.processInfo.environment
            if env["CE_OPEN_PROMPT"] != nil { openWindow(id: "prompt-creator") }
            if env["CE_OPEN_LIBRARY"] != nil { openWindow(id: "library") }
            // Dev aid: `CE_TEST_SEND="..."` sends one message to the selected project on launch.
            if let msg = env["CE_TEST_SEND"], let p = store.selected { store.session(for: p).send(msg) }
            // Dev aid: `CE_TEST_DRAFT="..."` fills the message box (for checking long prompts).
            if let d = env["CE_TEST_DRAFT"], let p = store.selected { store.session(for: p).draft = d }
        }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            DispatchQueue.main.async { store.reloadProjects() }
        }
    }
}

private struct ProjectRow: View {
    let project: Project
    @ObservedObject var session: ClaudeSession

    var body: some View {
        HStack {
            Image(systemName: project.isMounted ? "folder.fill" : "externaldrive.badge.xmark")
                .foregroundStyle(project.isMounted ? Color.accentColor : .secondary)
            VStack(alignment: .leading, spacing: 1) {
                Text(project.name).lineLimit(1)
                Text((project.path as NSString).deletingLastPathComponent.replacingOccurrences(of: Toolchain.home, with: "~"))
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.head)
            }
            Spacer()
            if session.isBusy { ProgressView().controlSize(.mini) }
        }
        .opacity(project.isMounted ? 1 : 0.5)
    }
}
