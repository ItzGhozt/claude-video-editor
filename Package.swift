// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "ClaudeVideoEditor",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "ClaudeVideoEditor",
            path: "Sources/ClaudeVideoEditor",
            linkerSettings: [
                .linkedFramework("AVKit"),
                .linkedFramework("AVFoundation"),
                .linkedFramework("Speech"),
                .linkedFramework("Security"),
            ]
        )
    ]
)
