#!/usr/bin/env bash
set -euo pipefail

RID="${1:-}"
if [[ -z "$RID" ]]; then
  echo "usage: $0 <linux-x64|osx-x64|osx-arm64>" >&2
  exit 2
fi

VERSION="0.15.1"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TOOLS="$ROOT/src/Typescribe.Infrastructure/tools"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

case "$RID" in
  linux-x64)
    ASSET="typst-x86_64-unknown-linux-musl.tar.xz"
    SHA256="a6d077d0a95eed5a2eba715b2dae06be954f624ccbf85758a03f389ded33118c"
    ;;
  osx-x64)
    ASSET="typst-x86_64-apple-darwin.tar.xz"
    SHA256="7f9fdd9584866245de9a79e0add8f9236fae6f40a8a45e2c4771ccc14db4e0fa"
    ;;
  osx-arm64)
    ASSET="typst-aarch64-apple-darwin.tar.xz"
    SHA256="48f62ed034aa3a7978309579ac6ca00045e2ef0da73114e8af27cfd8e74dc05a"
    ;;
  *)
    echo "unsupported RID for this script: $RID" >&2
    exit 2
    ;;
esac

URL="https://github.com/typst/typst/releases/download/v${VERSION}/${ASSET}"
ARCHIVE="$TMP/$ASSET"

echo "Downloading Typst $VERSION for $RID..."
curl --fail --location --proto '=https' --tlsv1.2 --retry 3 --output "$ARCHIVE" "$URL"

if command -v sha256sum >/dev/null 2>&1; then
  ACTUAL="$(sha256sum "$ARCHIVE" | awk '{print $1}')"
else
  ACTUAL="$(shasum -a 256 "$ARCHIVE" | awk '{print $1}')"
fi

if [[ "$ACTUAL" != "$SHA256" ]]; then
  echo "Typst archive checksum mismatch." >&2
  echo "Expected: $SHA256" >&2
  echo "Actual:   $ACTUAL" >&2
  exit 1
fi

mkdir -p "$TMP/extracted" "$TOOLS"
tar -xJf "$ARCHIVE" -C "$TMP/extracted"
BINARY="$(find "$TMP/extracted" -type f -name typst -print -quit)"
if [[ -z "$BINARY" ]]; then
  echo "Typst executable not found in release archive." >&2
  exit 1
fi

install -m 0755 "$BINARY" "$TOOLS/typst"
rm -f "$TOOLS/typst.exe"
echo "Pinned Typst $VERSION copied to $TOOLS/typst"
