#!/bin/bash
# Builds Convert.app (universal: Apple Silicon + Intel) into ./build.
#
#   ./build.sh            build only
#   ./build.sh --install  also copy to ~/Applications and install the Finder Quick Action
#   ./build.sh --zip      also create build/Convert.zip for a GitHub release
set -euo pipefail
cd "$(dirname "$0")"

if [[ ! -f Resources/AppIcon.icns ]]; then
  ICONSET="$(mktemp -d)/AppIcon.iconset"; mkdir -p "$ICONSET"
  swift tools/make_icon.swift "$ICONSET/icon_512x512@2x.png"
  for s in 16 32 128 256 512; do
    sips -z $s $s "$ICONSET/icon_512x512@2x.png" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
    sips -z $((s*2)) $((s*2)) "$ICONSET/icon_512x512@2x.png" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
  done
  iconutil -c icns "$ICONSET" -o Resources/AppIcon.icns
fi

swift build -c release --arch arm64 --arch x86_64
BIN=".build/apple/Products/Release/Convert"

APP="build/Convert.app"
rm -rf "${APP:?}"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/Convert"
cp Resources/Info.plist "$APP/Contents/Info.plist"
cp Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
codesign --force --sign - "$APP"   # ad-hoc signature (not notarized)
echo "Built $APP ($(lipo -archs "$APP/Contents/MacOS/Convert"))"

LSREGISTER=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister

case "${1:-}" in
  --install)
    DEST="$HOME/Applications/Convert.app"
    mkdir -p "$HOME/Applications"
    rm -rf "${DEST:?}"
    ditto "$APP" "$DEST"
    "$LSREGISTER" -f "$DEST"
    # Only the installed copy should handle files and the Quick Action.
    "$LSREGISTER" -u "$PWD/$APP" 2>/dev/null || true
    "$DEST/Contents/MacOS/Convert" --install-quick-action
    echo "Installed $DEST"
    ;;
  --zip)
    "$LSREGISTER" -u "$PWD/$APP" 2>/dev/null || true
    rm -f build/Convert.zip
    ditto -c -k --keepParent "$APP" build/Convert.zip
    echo "Packaged build/Convert.zip"
    ;;
esac
