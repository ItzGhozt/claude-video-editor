import AppKit

/// Developer aid: `CE_SNAPSHOT=/path/out.png` makes the app save a picture of its
/// windows a few seconds after launch (the terminal can't screen-capture other apps
/// without Screen Recording permission). `CE_OPEN_PROMPT=1` opens the Prompt Creator too;
/// `CE_SNAPSHOT_DELAY=<seconds>` waits longer (default 5).
enum DevSnapshot {
    static func scheduleIfRequested() {
        let env = ProcessInfo.processInfo.environment
        guard let out = env["CE_SNAPSHOT"] else { return }
        let delay = env["CE_SNAPSHOT_DELAY"].flatMap(Double.init) ?? 5
        DispatchQueue.main.asyncAfter(deadline: .now() + delay) {
            for (i, w) in NSApp.windows.enumerated() where w.isVisible && w.contentView != nil {
                guard let view = w.contentView?.superview ?? w.contentView,
                      let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { continue }
                view.cacheDisplay(in: view.bounds, to: rep)
                let path = i == 0 ? out : out.replacingOccurrences(of: ".png", with: "-\(i).png")
                try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: path))
            }
        }
    }
}
