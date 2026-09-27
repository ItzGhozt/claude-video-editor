import SwiftUI

/// Which Claude model sessions and the Prompt Creator use. Uses the CLI's aliases
/// (`opus`, `sonnet`, `haiku`), which always mean the latest model of that family.
enum ClaudeModel: String, CaseIterable, Identifiable {
    case automatic = ""
    case opus, sonnet, haiku

    static let storageKey = "claudeModel"

    var id: String { rawValue }

    var name: String {
        switch self {
        case .automatic: return "Default"
        case .opus: return "Opus"
        case .sonnet: return "Sonnet"
        case .haiku: return "Haiku"
        }
    }

    var blurb: String {
        switch self {
        case .automatic: return "Whatever your Claude plan or settings use by default"
        case .opus: return "Most capable: best for complex edits, slower"
        case .sonnet: return "Fast and smart: great for most edits"
        case .haiku: return "Fastest and lightest: quick questions and simple cuts"
        }
    }

    static var current: ClaudeModel {
        ClaudeModel(rawValue: UserDefaults.standard.string(forKey: storageKey) ?? "") ?? .automatic
    }

    /// Extra `claude` arguments for this choice.
    var arguments: [String] { self == .automatic ? [] : ["--model", rawValue] }
}

/// Menu for picking the model: shows "Model: Sonnet" so it's easy to spot.
/// Takes effect from the next message.
struct ModelPicker: View {
    @AppStorage(ClaudeModel.storageKey) private var raw = ""
    var compact = false

    private var current: ClaudeModel { ClaudeModel(rawValue: raw) ?? .automatic }

    var body: some View {
        Menu {
            ForEach(ClaudeModel.allCases) { m in
                Button {
                    raw = m.rawValue
                } label: {
                    if m == current {
                        Label("\(m.name): \(m.blurb)", systemImage: "checkmark")
                    } else {
                        Text("\(m.name): \(m.blurb)")
                    }
                }
            }
        } label: {
            Label("Model: \(current.name)", systemImage: "cpu")
                .labelStyle(.titleAndIcon)
        }
        .menuStyle(.button)
        .buttonStyle(.bordered)
        .controlSize(compact ? .small : .regular)
        .fixedSize()
        .help("Choose which Claude model to use (Opus, Sonnet, Haiku). Applies from your next message.")
    }
}
