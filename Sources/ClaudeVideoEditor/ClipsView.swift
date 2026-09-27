import AVKit
import SwiftUI

struct Clip: Identifiable, Hashable {
    let url: URL
    let relative: String
    let modified: Date
    let size: Int64
    var id: URL { url }
}

enum ClipScanner {
    static let exts: Set<String> = ["mp4", "mov", "m4v", "mkv", "avi", "mts", "webm"]

    static func scan(_ root: String) -> [Clip] {
        let rootURL = URL(fileURLWithPath: root)
        guard let en = FileManager.default.enumerator(
            at: rootURL,
            includingPropertiesForKeys: [.contentModificationDateKey, .fileSizeKey, .isDirectoryKey],
            options: [.skipsHiddenFiles, .skipsPackageDescendants]) else { return [] }
        var out: [Clip] = []
        let base = rootURL.standardizedFileURL.path + "/"
        for case let url as URL in en {
            if en.level > 4 { en.skipDescendants(); continue }
            guard exts.contains(url.pathExtension.lowercased()) else { continue }
            let v = try? url.resourceValues(forKeys: [.contentModificationDateKey, .fileSizeKey])
            let full = url.standardizedFileURL.path
            out.append(Clip(url: url,
                            relative: full.hasPrefix(base) ? String(full.dropFirst(base.count)) : url.lastPathComponent,
                            modified: v?.contentModificationDate ?? .distantPast,
                            size: Int64(v?.fileSize ?? 0)))
        }
        return out.sorted { $0.modified > $1.modified }
    }
}

struct ClipsView: View {
    @ObservedObject var session: ClaudeSession
    @State private var clips: [Clip] = []
    @State private var selection: Clip.ID?
    @State private var player = AVPlayer()
    @State private var filter = ""
    @State private var scanning = false

    private var shown: [Clip] {
        filter.isEmpty ? clips : clips.filter { $0.relative.localizedCaseInsensitiveContains(filter) }
    }
    private var selectedClip: Clip? { clips.first { $0.id == selection } }

    var body: some View {
        VSplitView {
            VStack(spacing: 0) {
                PlayerView(player: player)
                    .frame(minHeight: 220)
                    .background(Color.black)
                    .overlay {
                        if selectedClip == nil {
                            VStack(spacing: 6) {
                                Image(systemName: "play.rectangle").font(.system(size: 34))
                                Text("Select a clip to preview")
                            }
                            .foregroundStyle(.secondary)
                            .allowsHitTesting(false)
                        }
                    }
                if let c = selectedClip {
                    HStack {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(c.url.lastPathComponent).font(.headline).lineLimit(1)
                            Text("\(ByteCountFormatter.string(fromByteCount: c.size, countStyle: .file)) · \(c.modified.formatted(date: .abbreviated, time: .shortened))")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button { insert(c) } label: { Label("Use in Chat", systemImage: "text.bubble") }
                            .help("Add this clip's name to your message")
                        Button { NSWorkspace.shared.activateFileViewerSelecting([c.url]) } label: {
                            Image(systemName: "folder")
                        }.help("Show in Finder")
                    }
                    .padding(10)
                }
            }
            .frame(minHeight: 280)

            VStack(spacing: 0) {
                HStack {
                    Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                    TextField("Filter clips", text: $filter).textFieldStyle(.plain)
                    if scanning { ProgressView().controlSize(.small) }
                    Button { rescan() } label: { Image(systemName: "arrow.clockwise") }
                        .buttonStyle(.borderless).help("Refresh")
                }
                .padding(8)
                Divider()
                List(shown, selection: $selection) { c in
                    ClipRow(clip: c)
                        .tag(c.id)
                        .draggable(c.relative)
                        .contextMenu {
                            Button("Use in Chat") { insert(c) }
                            Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([c.url]) }
                        }
                }
                .overlay {
                    if clips.isEmpty && !scanning {
                        Text(session.project.isMounted ? "No video files here yet" : "Drive not mounted")
                            .foregroundStyle(.secondary)
                    }
                }
            }
            .frame(minHeight: 160)
        }
        .onAppear(perform: rescan)
        .onChange(of: session.turnsFinished) { rescan() }
        .onChange(of: selection) {
            if let c = selectedClip {
                player.replaceCurrentItem(with: AVPlayerItem(url: c.url))
            } else {
                player.replaceCurrentItem(with: nil)
            }
        }
        .onDisappear { player.pause() }
    }

    private func insert(_ c: Clip) {
        let token = "`\(c.relative)`"
        session.draft = session.draft.isEmpty ? token + " " : session.draft + " " + token
    }

    private func rescan() {
        let root = session.project.path
        scanning = true
        DispatchQueue.global(qos: .userInitiated).async {
            let found = ClipScanner.scan(root)
            DispatchQueue.main.async {
                clips = found
                scanning = false
            }
        }
    }
}

private struct ClipRow: View {
    let clip: Clip
    private var isNew: Bool { Date().timeIntervalSince(clip.modified) < 3600 }

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "film").foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 1) {
                Text(clip.url.lastPathComponent).lineLimit(1)
                let folder = (clip.relative as NSString).deletingLastPathComponent
                if !folder.isEmpty {
                    Text(folder).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
            }
            Spacer()
            if isNew {
                Text("NEW").font(.caption2.bold())
                    .padding(.horizontal, 5).padding(.vertical, 1)
                    .background(Color.accentColor.opacity(0.2), in: Capsule())
            }
        }
        .padding(.vertical, 2)
    }
}

/// AppKit's player view: native Mac controls, scrubbing and fullscreen.
private struct PlayerView: NSViewRepresentable {
    let player: AVPlayer
    func makeNSView(context: Context) -> AVPlayerView {
        let v = AVPlayerView()
        v.player = player
        v.controlsStyle = .floating
        v.showsFullScreenToggleButton = true
        return v
    }
    func updateNSView(_ v: AVPlayerView, context: Context) {
        if v.player !== player { v.player = player }
    }
}
