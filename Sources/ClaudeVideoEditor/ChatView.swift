import SwiftUI

struct QuickAction: Identifiable {
    let title: String
    let icon: String
    let prompt: String
    var id: String { title }

    static let all: [QuickAction] = [
        .init(title: "Highlight reel", icon: "sparkles",
              prompt: "Make a highlight reel from the best moments in this folder. Show me the list of moments you picked before you render."),
        .init(title: "Cut the dead air", icon: "scissors",
              prompt: "Cut out the filler words, false starts and dead space, keeping everything in order. Show me the cut list before rendering."),
        .init(title: "Export 9:16", icon: "rectangle.portrait",
              prompt: "Export this as a 9:16 reel: 1080x1920 h264, 30 fps, AAC 48 kHz stereo. Use the blurred-edge fill if the source isn't already 9:16."),
        .init(title: "Subtitles", icon: "captions.bubble",
              prompt: "Transcribe the speech and burn in clean, readable subtitles."),
        .init(title: "Color grade", icon: "paintpalette",
              prompt: "Suggest a color grade for this footage, render a before/after still for me to check, then apply it once I approve."),
        .init(title: "What's here?", icon: "list.bullet.rectangle",
              prompt: "Give me a quick overview of the footage in this folder: how many clips, total length, what's in them, and any finished exports."),
    ]
}

struct ChatView: View {
    @ObservedObject var session: ClaudeSession
    @FocusState private var composerFocused: Bool

    var body: some View {
        VStack(spacing: 0) {
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 10) {
                        if session.items.isEmpty && session.liveText.isEmpty {
                            EmptyChat(project: session.project)
                        }
                        ForEach(session.items) { item in
                            ItemView(item: item).id(item.id)
                        }
                        if !session.liveText.isEmpty {
                            Bubble(text: session.liveText, isUser: false)
                        }
                        if session.isBusy {
                            HStack(spacing: 6) {
                                ProgressView().controlSize(.small)
                                Text("Working…").foregroundStyle(.secondary)
                            }
                            .padding(.leading, 4)
                        }
                        Color.clear.frame(height: 1).id("bottom")
                    }
                    .padding(16)
                }
                .onChange(of: session.items.count) { withAnimation { proxy.scrollTo("bottom") } }
                .onChange(of: session.liveText) { proxy.scrollTo("bottom") }
                .onAppear { proxy.scrollTo("bottom") }
            }

            Divider()

            FlowLayout(spacing: 6) {
                    ForEach(QuickAction.all) { a in
                        Button {
                            session.draft = session.draft.isEmpty ? a.prompt : session.draft + "\n\n" + a.prompt
                            composerFocused = true
                        } label: {
                            Label(a.title, systemImage: a.icon)
                        }
                        .buttonStyle(.bordered)
                        .controlSize(.small)
                        .help(a.prompt)
                    }
            }
            .padding(.horizontal, 12).padding(.top, 8)

            HStack(alignment: .bottom, spacing: 8) {
                ComposerEditor(text: $session.draft,
                               placeholder: "Tell Claude what to edit…  (Return sends, ⇧Return for a new line)") {
                    session.send(session.draft)
                }
                .focused($composerFocused)
                .padding(.horizontal, 6)
                .padding(.vertical, 4)
                .background(RoundedRectangle(cornerRadius: 10).fill(Color(nsColor: .textBackgroundColor)))
                .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.secondary.opacity(0.3)))
                if session.isBusy {
                    Button(role: .destructive) { session.stop() } label: {
                        Image(systemName: "stop.circle.fill").font(.title)
                    }
                    .buttonStyle(.borderless).help("Stop")
                } else {
                    Button { session.send(session.draft) } label: {
                        Image(systemName: "arrow.up.circle.fill").font(.title)
                    }
                    .buttonStyle(.borderless)
                    .disabled(session.draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                    .keyboardShortcut(.return, modifiers: .command)
                    .help("Send (⌘Return)")
                }
            }
            .padding(12)
        }
        .onAppear { composerFocused = true }
    }
}

private struct EmptyChat: View {
    let project: Project
    var body: some View {
        VStack(spacing: 8) {
            Image(systemName: "film.stack").font(.system(size: 40)).foregroundStyle(.secondary)
            Text(project.name).font(.title2.bold())
            Text(project.path).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
            Text("Pick a quick action below, drag a clip in from the right, or use the Prompt Creator to talk it through first.")
                .multilineTextAlignment(.center).foregroundStyle(.secondary).frame(maxWidth: 380)
        }
        .frame(maxWidth: .infinity).padding(.top, 60)
    }
}

private struct ItemView: View {
    let item: ChatItem
    @State private var expanded = false

    var body: some View {
        switch item.kind {
        case .user: Bubble(text: item.text, isUser: true)
        case .assistant: Bubble(text: item.text, isUser: false)
        case .tool, .toolResult:
            VStack(alignment: .leading, spacing: 4) {
                Button { expanded.toggle() } label: {
                    HStack(spacing: 6) {
                        Image(systemName: item.kind == .tool ? "hammer" : (item.text == "Error" ? "xmark.octagon" : "arrow.turn.down.right"))
                        Text(item.text).lineLimit(1)
                        if item.detail?.isEmpty == false {
                            Image(systemName: expanded ? "chevron.down" : "chevron.right").font(.caption2)
                        }
                    }
                    .font(.caption).foregroundStyle(item.text == "Error" ? .red : .secondary)
                }
                .buttonStyle(.plain)
                if expanded, let d = item.detail, !d.isEmpty {
                    Text(d).font(.system(.caption, design: .monospaced))
                        .textSelection(.enabled)
                        .padding(8)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .background(RoundedRectangle(cornerRadius: 6).fill(Color.secondary.opacity(0.1)))
                }
            }
            .padding(.leading, 4)
        case .notice:
            Text(item.text).font(.caption).foregroundStyle(.secondary).frame(maxWidth: .infinity)
        case .error:
            Label(item.text, systemImage: "exclamationmark.triangle")
                .font(.callout).foregroundStyle(.red).textSelection(.enabled)
        }
    }
}

/// The message box: grows with its text up to about 10 lines, then scrolls.
/// Return sends; Shift- or Option-Return inserts a new line.
struct ComposerEditor: View {
    @Binding var text: String
    let placeholder: String
    let onSend: () -> Void

    var body: some View {
        // An invisible copy of the text decides the height (capped), and the
        // real editor fills that space and scrolls when the text is longer.
        Text(text.isEmpty ? " " : text + " ")
            .font(.body)
            .padding(.horizontal, 5)
            .padding(.vertical, 6)
            .opacity(0)
            .frame(maxWidth: .infinity, minHeight: 32, maxHeight: 220, alignment: .topLeading)
            .fixedSize(horizontal: false, vertical: true)
            .overlay {
                TextEditor(text: $text)
                    .font(.body)
                    .scrollContentBackground(.hidden)
                    .padding(.vertical, 6)
                    .onKeyPress(.return, phases: .down) { press in
                        if press.modifiers.contains(.shift) || press.modifiers.contains(.option) { return .ignored }
                        onSend()
                        return .handled
                    }
            }
            .overlay(alignment: .topLeading) {
                if text.isEmpty {
                    Text(placeholder)
                        .foregroundStyle(.tertiary)
                        .padding(.horizontal, 5)
                        .padding(.vertical, 6)
                        .allowsHitTesting(false)
                }
            }
    }
}

struct Bubble: View {
    let text: String
    let isUser: Bool

    var body: some View {
        HStack {
            if isUser { Spacer(minLength: 60) }
            Text(markdown)
                .textSelection(.enabled)
                .padding(.horizontal, 12).padding(.vertical, 8)
                .background(RoundedRectangle(cornerRadius: 14)
                    .fill(isUser ? Color.accentColor : Color.secondary.opacity(0.15)))
                .foregroundStyle(isUser ? .white : .primary)
            if !isUser { Spacer(minLength: 60) }
        }
    }

    private var markdown: AttributedString {
        (try? AttributedString(markdown: text, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace)))
            ?? AttributedString(text)
    }
}

/// Lays children out left to right, wrapping onto new rows, so every quick
/// action stays visible however narrow the chat column is.
struct FlowLayout: Layout {
    var spacing: CGFloat = 6

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let rows = arrange(width: proposal.width ?? .infinity, subviews: subviews)
        let h = rows.last.map { $0.y + $0.height } ?? 0
        let w = rows.map(\.width).max() ?? 0
        return CGSize(width: proposal.width ?? w, height: h)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        for row in arrange(width: bounds.width, subviews: subviews) {
            var x = bounds.minX
            for i in row.indices {
                let size = subviews[i].sizeThatFits(.unspecified)
                subviews[i].place(at: CGPoint(x: x, y: bounds.minY + row.y), proposal: ProposedViewSize(size))
                x += size.width + spacing
            }
        }
    }

    private struct Row { var indices: [Int] = []; var y: CGFloat = 0; var width: CGFloat = 0; var height: CGFloat = 0 }

    private func arrange(width: CGFloat, subviews: Subviews) -> [Row] {
        var rows: [Row] = [Row()]
        for i in subviews.indices {
            let size = subviews[i].sizeThatFits(.unspecified)
            if !rows[rows.count - 1].indices.isEmpty && rows[rows.count - 1].width + spacing + size.width > width {
                let last = rows[rows.count - 1]
                rows.append(Row(y: last.y + last.height + spacing))
            }
            var r = rows[rows.count - 1]
            r.width += (r.indices.isEmpty ? 0 : spacing) + size.width
            r.height = max(r.height, size.height)
            r.indices.append(i)
            rows[rows.count - 1] = r
        }
        return rows
    }
}
