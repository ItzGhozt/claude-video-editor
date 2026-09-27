#!/bin/zsh
# Builds the app and zips it for a GitHub release.
#   VERSION=1.0.0 scripts/release.sh            # -> dist/Claude-Video-Editor-1.0.0.zip
#   VERSION=1.0.0 scripts/release.sh --publish  # also creates the GitHub release (needs gh)
set -e
cd "${0:A:h}/.."
VERSION="${VERSION:-1.0.0}"
VERSION=$VERSION ./build.sh dist
ZIP="dist/Claude-Video-Editor-$VERSION.zip"
rm -f "$ZIP"
ditto -c -k --keepParent "dist/Claude Video Editor.app" "$ZIP"
echo "Zipped: $ZIP"
if [[ "$1" == "--publish" ]]; then
  gh release create "v$VERSION" "$ZIP" --title "Claude Video Editor $VERSION" --notes-file scripts/release-notes.md
fi
