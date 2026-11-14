#!/usr/bin/env bash
# Build and ZIP both macOS architectures with local ad-hoc signing.
set -euo pipefail

WORKSPACE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
MAC_DIR="$WORKSPACE_DIR/__assets/mac"
PROJECT_FILE="$WORKSPACE_DIR/ImageGlass.Mac/ImageGlass.Mac.csproj"
BUILD_PROPS_FILE="$WORKSPACE_DIR/Directory.Build.props"
OUTPUT_DIR="$WORKSPACE_DIR/__artifacts/bundle"

if ! command -v dotnet >/dev/null || ! command -v codesign >/dev/null || ! command -v xcrun >/dev/null; then
	echo "Error: dotnet and Xcode Command Line Tools are required." >&2
	exit 1
fi

IG_VERSION="$(sed -n 's:.*<IgVersion>\(.*\)</IgVersion>.*:\1:p' "$BUILD_PROPS_FILE" | head -n 1)"
if [[ -z "$IG_VERSION" ]]; then
	echo "Error: could not read IgVersion from $BUILD_PROPS_FILE" >&2
	exit 1
fi
mkdir -p "$OUTPUT_DIR"

# Remove same-version outputs first so a failed architecture cannot leave a stale ZIP
# from an earlier attempt that might be uploaded as if it came from this build.
for ARCH in arm64 x64; do
	rm -f "$OUTPUT_DIR/ChuImage-${IG_VERSION}-mac-${ARCH}.zip"
done

for ARCH in arm64 x64; do
	case "$ARCH" in
		arm64) PLATFORM="ARM64" ;;
		x64) PLATFORM="x64" ;;
	esac
	RID="osx-$ARCH"
	PUBLISH_DIR="$WORKSPACE_DIR/__artifacts/publish/$RID"
	APP_DIR="$WORKSPACE_DIR/__artifacts/bundle/$RID/ChuImage.app"
	FINAL_ZIP="$OUTPUT_DIR/ChuImage-${IG_VERSION}-mac-${ARCH}.zip"

	echo "==> Publishing ChuImage $IG_VERSION ($RID)"
	rm -rf "$PUBLISH_DIR"
	dotnet publish "$PROJECT_FILE" -c Release -r "$RID" -p:Platform="$PLATFORM" \
		-o "$PUBLISH_DIR" --self-contained true

	echo "==> Assembling $APP_DIR"
	bash "$MAC_DIR/script-pack-mac-app.sh" "$ARCH"

	echo "==> Creating $FINAL_ZIP"
	ditto -c -k --sequesterRsrc --keepParent "$APP_DIR" "$FINAL_ZIP"
	rm -rf "$APP_DIR"
	echo "Created: $FINAL_ZIP"
done

echo "==> Both macOS ZIP packages are ready in $OUTPUT_DIR"
