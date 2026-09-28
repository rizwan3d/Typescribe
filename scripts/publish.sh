#!/usr/bin/env bash
set -euo pipefail

RID="${1:-}"
if [[ -z "$RID" ]]; then
  OS="$(uname -s)"
  ARCH="$(uname -m)"
  case "$OS/$ARCH" in
    Linux/x86_64) RID="linux-x64" ;;
    Linux/aarch64|Linux/arm64) RID="linux-arm64" ;;
    Darwin/x86_64) RID="osx-x64" ;;
    Darwin/arm64) RID="osx-arm64" ;;
    *) echo "Could not infer a supported RID. Pass it explicitly." >&2; exit 2 ;;
  esac
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACTS="$ROOT/artifacts"
PUBLISH="$ARTIFACTS/publish/$RID"
PROJECT="$ROOT/src/Typescribe.Desktop/Typescribe.Desktop.csproj"

rm -rf "$PUBLISH"
mkdir -p "$PUBLISH" "$ARTIFACTS"

dotnet restore "$ROOT/Typescribe.slnx"
dotnet publish "$PROJECT" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -o "$PUBLISH" \
  -p:PublishAot=true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true

cp "$ROOT/THIRD_PARTY_NOTICES.md" "$PUBLISH/THIRD_PARTY_NOTICES.md"
cp "$ROOT/README.md" "$PUBLISH/README.md"

case "$RID" in
  linux-*)
    ARCHIVE="$ARTIFACTS/Typescribe-$RID.tar.gz"
    rm -f "$ARCHIVE"
    tar -C "$PUBLISH" -czf "$ARCHIVE" .
    ;;
  osx-*)
    APP="$ARTIFACTS/Typescribe.app"
    rm -rf "$APP"
    mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
    cp -R "$PUBLISH"/. "$APP/Contents/MacOS/"
    cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDisplayName</key><string>Typescribe</string>
  <key>CFBundleExecutable</key><string>Typescribe</string>
  <key>CFBundleIdentifier</key><string>app.typescribe.desktop</string>
  <key>CFBundleName</key><string>Typescribe</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST
    if command -v codesign >/dev/null 2>&1; then
      codesign --force --deep --sign - "$APP"
    fi
    ARCHIVE="$ARTIFACTS/Typescribe-$RID.zip"
    rm -f "$ARCHIVE"
    (cd "$ARTIFACTS" && zip -qry "$(basename "$ARCHIVE")" Typescribe.app)
    rm -rf "$APP"
    ;;
  *)
    echo "Unsupported RID: $RID" >&2
    exit 2
    ;;
esac

echo "Created: $ARCHIVE"
