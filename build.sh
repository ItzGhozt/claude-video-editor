#!/bin/zsh
# Builds "Claude Video Editor.app".
#   ./build.sh              # -> dist/Claude Video Editor.app
#   ./build.sh ~/Applications
# Needs the Xcode Command Line Tools (xcode-select --install). Lines about
# "xcrun ... PlatformPath" or XCTest are harmless when full Xcode isn't installed.
set -e
cd "${0:A:h}"
DEST="${1:-dist}"
APP="$DEST/Claude Video Editor.app"
VERSION="${VERSION:-1.0.0}"

ARCHS=(--arch arm64 --arch x86_64)
if ! swift build -c release $ARCHS 2>/dev/null; then
  echo "Universal build unavailable here; building for this Mac only."
  ARCHS=()
  swift build -c release
fi
BIN="$(swift build -c release $ARCHS --show-bin-path)/ClaudeVideoEditor"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/ClaudeVideoEditor"
cp Resources/AppIcon.icns "$APP/Contents/Resources/"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Claude Video Editor</string>
    <key>CFBundleDisplayName</key><string>Claude Video Editor</string>
    <key>CFBundleIdentifier</key><string>io.github.itzghozt.claude-video-editor</string>
    <key>CFBundleExecutable</key><string>ClaudeVideoEditor</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>LSMinimumSystemVersion</key><string>14.0</string>
    <key>LSApplicationCategoryType</key><string>public.app-category.video</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSMicrophoneUsageDescription</key>
    <string>The Prompt Creator listens while you describe the edit you want.</string>
    <key>NSSpeechRecognitionUsageDescription</key>
    <string>The Prompt Creator turns what you say into text, on your Mac.</string>
    <key>NSAppleEventsUsageDescription</key>
    <string>Setup opens Terminal to run the Homebrew installer, which needs your password.</string>
</dict>
</plist>
PLIST

# Ad-hoc signature so macOS can remember the microphone/speech permissions.
codesign --force --deep --sign - "$APP"
echo "Built: $APP ($(lipo -archs "$APP/Contents/MacOS/ClaudeVideoEditor"))"
