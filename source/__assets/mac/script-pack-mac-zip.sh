#!/usr/bin/env bash
# Build, sign, notarize, and ZIP both macOS architectures.
set -euo pipefail

SIGN_IDENTITY="${SIGN_IDENTITY:-Developer ID Application: Phap Duong (7DV5HBKZ58)}"
NOTARY_PROFILE="${NOTARY_PROFILE:-imageglass-notary}"

WORKSPACE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
MAC_DIR="$WORKSPACE_DIR/__assets/mac"
PROJECT_FILE="$WORKSPACE_DIR/ImageGlass.Mac/ImageGlass.Mac.csproj"
BUILD_PROPS_FILE="$WORKSPACE_DIR/Directory.Build.props"
ENTITLEMENTS_FILE="$MAC_DIR/ImageGlass.entitlements"
OUTPUT_DIR="$WORKSPACE_DIR/__artifacts/bundle"

if ! command -v dotnet >/dev/null || ! command -v codesign >/dev/null || ! command -v xcrun >/dev/null; then
	echo "Error: dotnet and Xcode Command Line Tools are required." >&2
	exit 1
fi
if ! security find-identity -v -p codesigning | grep -qF "$SIGN_IDENTITY"; then
	echo "Error: signing identity not found in keychain: $SIGN_IDENTITY" >&2
	exit 1
fi
if ! notary_out="$(xcrun notarytool history --keychain-profile "$NOTARY_PROFILE" 2>&1)"; then
	echo "Error: notarytool check failed for keychain profile '$NOTARY_PROFILE':" >&2
	echo "$notary_out" | sed 's:^:       :' >&2
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
	rm -f "$OUTPUT_DIR/ChuImage-${IG_VERSION}-mac-${ARCH}.zip" \
		"$OUTPUT_DIR/.ChuImage-${IG_VERSION}-mac-${ARCH}-notary.zip"
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
	NOTARY_ZIP="$OUTPUT_DIR/.ChuImage-${IG_VERSION}-mac-${ARCH}-notary.zip"

	echo "==> Publishing ChuImage $IG_VERSION ($RID)"
	rm -rf "$PUBLISH_DIR"
	dotnet publish "$PROJECT_FILE" -c Release -r "$RID" -p:Platform="$PLATFORM" \
		-o "$PUBLISH_DIR" --self-contained true

	echo "==> Assembling $APP_DIR"
	bash "$MAC_DIR/script-pack-mac-app.sh" "$ARCH"

	echo "==> Signing $RID app bundle"
	find "$APP_DIR/Contents/MacOS" -type f -name "*.pdb" -delete
	find "$APP_DIR/Contents/MacOS" -type d -name "*.dSYM" -exec rm -rf {} +
	find "$APP_DIR/Contents/MacOS" -type f \( -name ".DS_Store" -o -name "._*" \) -delete
	while IFS= read -r -d '' lib; do
		codesign --force --timestamp --options runtime --sign "$SIGN_IDENTITY" "$lib"
	done < <(find "$APP_DIR/Contents/MacOS" -type f \( -name "*.dylib" -o -name "*.so" \) -print0)
	codesign --force --timestamp --options runtime \
		--entitlements "$ENTITLEMENTS_FILE" --sign "$SIGN_IDENTITY" "$APP_DIR"
	codesign --verify --deep --strict --verbose=2 "$APP_DIR"

	echo "==> Submitting $RID app for notarization"
	rm -f "$NOTARY_ZIP" "$FINAL_ZIP"
	ditto -c -k --sequesterRsrc --keepParent "$APP_DIR" "$NOTARY_ZIP"
	xcrun notarytool submit "$NOTARY_ZIP" --keychain-profile "$NOTARY_PROFILE" --wait
	rm -f "$NOTARY_ZIP"
	xcrun stapler staple "$APP_DIR"
	xcrun stapler validate "$APP_DIR"

	echo "==> Creating $FINAL_ZIP"
	ditto -c -k --sequesterRsrc --keepParent "$APP_DIR" "$FINAL_ZIP"
	rm -rf "$APP_DIR"
	echo "Created: $FINAL_ZIP"
done

echo "==> Both macOS ZIP packages are ready in $OUTPUT_DIR"
