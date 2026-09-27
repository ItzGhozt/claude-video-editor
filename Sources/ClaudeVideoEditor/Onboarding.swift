import SwiftUI

/// First-run flow: Welcome -> Sign in -> Setup -> Start guide.
struct OnboardingView: View {
    @EnvironmentObject var auth: AuthManager
    @EnvironmentObject var setup: SetupManager
    @AppStorage("onboardingDone") private var onboardingDone = false
    @State private var step = 0

    private let titles = ["Welcome", "Sign in", "Set up", "Start guide"]

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 18) {
                ForEach(titles.indices, id: \.self) { i in
                    HStack(spacing: 6) {
                        ZStack {
                            Circle().fill(i <= step ? Color.accentColor : Color.secondary.opacity(0.25))
                                .frame(width: 22, height: 22)
                            if i < step {
                                Image(systemName: "checkmark").font(.caption.bold()).foregroundStyle(.white)
                            } else {
                                Text("\(i + 1)").font(.caption.bold()).foregroundStyle(i == step ? .white : .secondary)
                            }
                        }
                        Text(titles[i]).foregroundStyle(i == step ? .primary : .secondary)
                    }
                }
            }
            .padding(.vertical, 16)
            Divider()

            Group {
                switch step {
                case 0: WelcomeStep()
                case 1: SignInView()
                case 2: SetupChecklistView()
                default: StartGuideView()
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)

            Divider()
            HStack {
                if step > 0 { Button("Back") { step -= 1 } }
                Spacer()
                if step == 1 && !auth.isReady {
                    Text("Sign in to continue").foregroundStyle(.secondary)
                } else if step == 2 && !setup.allRequiredDone {
                    Button("Skip for now") { step += 1 }
                }
                Button(step == titles.count - 1 ? "Start Editing" : "Continue") {
                    if step == titles.count - 1 { onboardingDone = true } else { step += 1 }
                }
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.defaultAction)
                .disabled(step == 1 && !auth.isReady)
            }
            .padding(16)
        }
        .onAppear {
            auth.refresh(); setup.refresh()
            // Dev aid: `CE_ONBOARD_STEP=2` opens onboarding at that step.
            if let n = ProcessInfo.processInfo.environment["CE_ONBOARD_STEP"].flatMap(Int.init) { step = n }
        }
    }
}

private struct WelcomeStep: View {
    var body: some View {
        ScrollView {
            VStack(spacing: 18) {
                Image(nsImage: NSApp.applicationIconImage).resizable().frame(width: 110, height: 110)
                Text("Welcome to Claude Video Editor").font(.largeTitle.bold())
                Text("Edit your footage by chatting with Claude. Pick a folder of clips, say what you want, and Claude cuts, grades, subtitles and renders it for you.")
                    .font(.title3).multilineTextAlignment(.center).foregroundStyle(.secondary)
                    .frame(maxWidth: 560)

                VStack(alignment: .leading, spacing: 14) {
                    Feature(icon: "bubble.left.and.text.bubble.right", title: "Chat to edit",
                            text: "Describe the video you want in plain words. Claude shows you its plan before rendering.")
                    Feature(icon: "mic.badge.plus", title: "Prompt Creator",
                            text: "Just talk. Your thoughts become a clear, detailed editing brief.")
                    Feature(icon: "play.rectangle", title: "Preview as you go",
                            text: "Every clip and render in your folder, playable right in the app.")
                    Feature(icon: "lock.shield", title: "Stays in its folder",
                            text: "Each project is sandboxed. Claude can only touch the folder you choose.")
                }
                .frame(maxWidth: 520)
                .padding(.top, 6)

                Text("Built on **video-use** by Browser Use, an open-source editing skill for Claude Code.")
                    .font(.callout).foregroundStyle(.secondary).padding(.top, 8)
            }
            .padding(30)
            .frame(maxWidth: .infinity)
        }
    }
}

private struct Feature: View {
    let icon: String, title: String, text: String
    var body: some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: icon).font(.title2).foregroundStyle(Color.accentColor).frame(width: 32)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.headline)
                Text(text).foregroundStyle(.secondary)
            }
        }
    }
}

// MARK: - Sign in

struct SignInView: View {
    @EnvironmentObject var auth: AuthManager
    @EnvironmentObject var setup: SetupManager
    @State private var apiKey = ""
    @State private var code = ""
    @State private var checkingKey = false
    @State private var showKeyField = false
    @State private var confirmSignOut = false

    var body: some View {
        ScrollView {
            VStack(spacing: 18) {
                Image(systemName: "person.crop.circle.badge.checkmark").font(.system(size: 54)).foregroundStyle(Color.accentColor)
                Text("Sign in to Claude").font(.largeTitle.bold())
                content.frame(maxWidth: 480)
                if let e = auth.error {
                    Label(e, systemImage: "exclamationmark.triangle").foregroundStyle(.red).frame(maxWidth: 480)
                }
            }
            .padding(30)
            .frame(maxWidth: .infinity)
        }
        .onAppear { auth.refresh() }
    }

    @ViewBuilder private var content: some View {
        switch auth.state {
        case .checking:
            ProgressView("Checking…")
        case .noClaude:
            VStack(spacing: 12) {
                Text("Claude Code needs to be installed first. It's what signs you in and does the editing.")
                    .multilineTextAlignment(.center).foregroundStyle(.secondary)
                if setup.status[.claude] == .working {
                    ProgressView("Installing Claude Code…")
                } else {
                    Button { setup.install(.claude) } label: {
                        Label("Install Claude Code", systemImage: "arrow.down.circle").frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.borderedProminent).controlSize(.large)
                }
                if case .failed(let why) = setup.status[.claude] { Text(why).foregroundStyle(.red).font(.callout) }
            }
            .onChange(of: setup.status[.claude]) { auth.refresh() }
        case .signedOut:
            VStack(spacing: 14) {
                Text("Use your Claude account (Pro, Max, Team or Enterprise). It's the same sign-in as Claude Code, and usage counts against your plan.")
                    .multilineTextAlignment(.center).foregroundStyle(.secondary)
                Button { auth.signInWithClaude() } label: {
                    Label("Sign in with Claude", systemImage: "person.badge.key").frame(maxWidth: .infinity)
                }
                .buttonStyle(.borderedProminent).controlSize(.large)

                DisclosureGroup("Use an Anthropic API key instead", isExpanded: $showKeyField) {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Pay per use through the Anthropic Console. The key is stored in your Mac's keychain.")
                            .font(.callout).foregroundStyle(.secondary)
                        HStack {
                            SecureField("sk-ant-…", text: $apiKey).textFieldStyle(.roundedBorder)
                            Button("Use Key") {
                                checkingKey = true
                                auth.useAPIKey(apiKey) { _ in checkingKey = false; apiKey = "" }
                            }
                            .disabled(apiKey.isEmpty || checkingKey)
                            if checkingKey { ProgressView().controlSize(.small) }
                        }
                        Link("Get an API key", destination: URL(string: "https://console.anthropic.com/settings/keys")!)
                            .font(.callout)
                    }
                    .padding(.top, 6)
                }
            }
        case .signingIn(let url):
            VStack(spacing: 12) {
                ProgressView()
                Text("Finish signing in in your browser.").font(.headline)
                if let url, let u = URL(string: url) {
                    Link("Browser didn't open? Click here.", destination: u).font(.callout)
                }
                Text("If the page shows a code, paste it here:").font(.callout).foregroundStyle(.secondary)
                HStack {
                    TextField("Code", text: $code).textFieldStyle(.roundedBorder)
                        .onSubmit { auth.submitCode(code); code = "" }
                    Button("Submit") { auth.submitCode(code); code = "" }.disabled(code.isEmpty)
                }
                Button("Cancel") { auth.cancelSignIn() }
            }
        case .signedIn(let email, let detail):
            accountCard(title: email, detail: detail.isEmpty ? "Claude account" : detail, icon: "checkmark.seal.fill",
                        note: "Signing out also signs Claude Code out in Terminal, because they share one login.")
        case .apiKey(let masked):
            accountCard(title: "Anthropic API key", detail: masked, icon: "key.fill",
                        note: "Signing out removes the key from this app's keychain entry.")
        }
    }

    private func accountCard(title: String, detail: String, icon: String, note: String) -> some View {
        VStack(spacing: 12) {
            HStack(spacing: 12) {
                Image(systemName: icon).font(.title).foregroundStyle(.green)
                VStack(alignment: .leading) {
                    Text(title).font(.headline)
                    Text(detail).foregroundStyle(.secondary)
                }
                Spacer()
            }
            .padding(14)
            .background(RoundedRectangle(cornerRadius: 10).fill(Color.secondary.opacity(0.1)))
            Text("You're signed in.").foregroundStyle(.secondary)
            Button("Sign Out…") { confirmSignOut = true }
                .confirmationDialog("Sign out?", isPresented: $confirmSignOut) {
                    Button("Sign Out", role: .destructive) { auth.signOut() }
                } message: { Text(note) }
        }
    }
}

// MARK: - Setup checklist

struct SetupChecklistView: View {
    @EnvironmentObject var setup: SetupManager
    @State private var elevenKey = ""
    @State private var expanded: SetupManager.Item?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                HStack {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Set up your Mac").font(.largeTitle.bold())
                        Text("Click Install on anything that isn't ticked yet. Everything comes from its official source.")
                            .foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button { setup.refresh() } label: { Label("Check Again", systemImage: "arrow.clockwise") }
                }
                ForEach(SetupManager.Item.allCases) { item in row(item) }
            }
            .padding(30)
            .frame(maxWidth: 720)
            .frame(maxWidth: .infinity)
        }
        .onAppear { setup.refresh() }
    }

    private func row(_ item: SetupManager.Item) -> some View {
        let st = setup.status[item] ?? .unknown
        return VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .top, spacing: 12) {
                Group {
                    switch st {
                    case .ok: Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
                    case .working: ProgressView().controlSize(.small)
                    case .failed: Image(systemName: "xmark.circle.fill").foregroundStyle(.red)
                    case .missing: Image(systemName: "circle").foregroundStyle(.secondary)
                    case .unknown: Image(systemName: "circle.dotted").foregroundStyle(.secondary)
                    }
                }
                .font(.title2).frame(width: 26)
                VStack(alignment: .leading, spacing: 3) {
                    HStack {
                        Text(item.title).font(.headline)
                        if item.optional { Text("recommended").font(.caption).foregroundStyle(.secondary) }
                    }
                    Text(item.blurb).font(.callout).foregroundStyle(.secondary)
                    if case .failed(let why) = st { Text(why).font(.callout).foregroundStyle(.red) }
                }
                Spacer()
                if item != .elevenLabs && st != .ok && st != .working {
                    Button(item == .homebrew ? "Open Installer" : "Install") { setup.install(item); expanded = item }
                        .buttonStyle(.borderedProminent)
                        .disabled(item == .ffmpeg && Toolchain.brew == nil)
                }
            }
            if item == .elevenLabs && st != .ok {
                HStack {
                    SecureField("Paste your ElevenLabs API key", text: $elevenKey).textFieldStyle(.roundedBorder)
                    Button("Save") { setup.saveElevenLabsKey(elevenKey); elevenKey = "" }
                        .disabled(elevenKey.isEmpty || st == .working)
                    Link("Get a key", destination: SetupManager.elevenLabsDocs)
                }
                .padding(.leading, 38)
            }
            if let log = setup.log[item], !log.isEmpty {
                DisclosureGroup("Details", isExpanded: Binding(get: { expanded == item || st == .working },
                                                               set: { expanded = $0 ? item : nil })) {
                    ScrollView {
                        Text(log).font(.system(.caption, design: .monospaced)).textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .frame(maxHeight: 140)
                }
                .font(.callout)
                .padding(.leading, 38)
            }
        }
        .padding(14)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color.secondary.opacity(0.08)))
    }
}

// MARK: - Start guide

struct StartGuideView: View {
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                Text("Start guide").font(.largeTitle.bold())
                GuideStep(n: 1, title: "Add a project folder",
                          text: "Click **Add Folder…** in the sidebar and pick a folder of footage. Claude can only see and change files inside that folder, so put everything the edit needs in it.")
                GuideStep(n: 2, title: "Say what you want",
                          text: "Type in the chat, or click a **quick action** (Highlight reel, Export 9:16, Subtitles…) to start from a ready-made request. Drag a clip from the right-hand list into the message box to refer to it by name.")
                GuideStep(n: 3, title: "Or talk it through with the Prompt Creator",
                          text: "Click **Prompt Creator** (⇧⌘P), press the mic (⌘D) and describe the video out loud: what it's for, which clips, the vibe, music, length. Click **Create Prompt** and Claude turns your thoughts into a clear brief. Edit it, then **Send to Claude**.")
                GuideStep(n: 4, title: "Save prompts, teach it your style",
                          text: "Hover over a message you sent and click the **☆** to save it into a folder (the **Saved** menu and **Saved Prompts & Style** window bring it back). Tell Claude lasting preferences (\"I hate fast zooms\") or use **👍 / 👎** on its replies, and it remembers them for every future edit. Review or change them under **My Style**.")
                GuideStep(n: 5, title: "Check the plan, then render",
                          text: "Claude usually shows you a cut list before rendering. Reply with changes (\"swap the 2nd and 3rd clips\", \"make it warmer\") until it's right.")
                GuideStep(n: 6, title: "Watch the result",
                          text: "New renders appear in the clip list with a **NEW** tag. Click one to play it, or right-click › Show in Finder.")
                VStack(alignment: .leading, spacing: 8) {
                    Text("Tips").font(.title3.bold())
                    Text("• Each project keeps its own conversation, even after you quit. Use **New Chat** in the toolbar to start fresh.")
                    Text("• Click the **Stop** button to interrupt a long job. The next message carries on from there.")
                    Text("• The first transcription of a long clip can take a few minutes.")
                    Text("• Open this guide any time from **Help › Start Guide**.")
                }
                .foregroundStyle(.secondary)
            }
            .padding(30)
            .frame(maxWidth: 680, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
    }
}

private struct GuideStep: View {
    let n: Int, title: String, text: LocalizedStringKey
    var body: some View {
        HStack(alignment: .top, spacing: 14) {
            Text("\(n)").font(.headline).foregroundStyle(.white)
                .frame(width: 28, height: 28).background(Circle().fill(Color.accentColor))
            VStack(alignment: .leading, spacing: 4) {
                Text(title).font(.headline)
                Text(text).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

// MARK: - Credits

struct CreditsView: View {
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                HStack(spacing: 14) {
                    Image(nsImage: NSApp.applicationIconImage).resizable().frame(width: 64, height: 64)
                    VStack(alignment: .leading) {
                        Text("Claude Video Editor").font(.title.bold())
                        Text("A Mac app for editing video by chatting with Claude.").foregroundStyle(.secondary)
                    }
                }
                Divider()
                Text("The editing engine").font(.title3.bold())
                Text("All of the actual video editing (transcription, cutting, color grading, subtitles, animation overlays and self-review of renders) is done by **video-use**, an open-source Claude Code skill created by **Browser Use** (originally written by Gregor Žunič). This app is a desktop front end for it and installs it from its official repository.")
                    .fixedSize(horizontal: false, vertical: true)
                HStack {
                    Link("video-use on GitHub", destination: URL(string: "https://github.com/browser-use/video-use")!)
                    Text("·").foregroundStyle(.secondary)
                    Link("Browser Use", destination: URL(string: "https://browser-use.com")!)
                }
                Text("video-use is released under the MIT License, © 2026 Browser Use.")
                    .font(.callout).foregroundStyle(.secondary)
                Divider()
                Text("Also built with").font(.title3.bold())
                Text("• **Claude Code** by Anthropic, the agent that runs each editing session\n• **ffmpeg** for rendering\n• **ElevenLabs Scribe** for transcription (used by video-use)\n• **uv** by Astral for Python environments")
                    .fixedSize(horizontal: false, vertical: true)
                Text("Claude Video Editor is an independent project and isn't affiliated with or endorsed by Anthropic or Browser Use.")
                    .font(.callout).foregroundStyle(.secondary)
            }
            .padding(24)
        }
        .frame(minWidth: 520, minHeight: 480)
    }
}
