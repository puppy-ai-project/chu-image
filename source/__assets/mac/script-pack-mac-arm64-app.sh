#!/usr/bin/env bash
set -euo pipefail

WORKSPACE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APP_NAME="ChuImage"
PUBLISH_DIR="$WORKSPACE_DIR/__artifacts/publish/osx-arm64"
LEGACY_APP_DIR="$WORKSPACE_DIR/__artifacts/bundle/osx-arm64/ImageGlass.app"
APP_DIR="$WORKSPACE_DIR/__artifacts/bundle/osx-arm64/${APP_NAME}.app"
CONTENTS_DIR="$APP_DIR/Contents"
INFO_PLIST="$CONTENTS_DIR/Info.plist"
BUILD_PROPS_FILE="$WORKSPACE_DIR/Directory.Build.props"
ICON_SOURCE_FILE="$WORKSPACE_DIR/__assets/mac/logo.icns"
ICON_TARGET_FILE="$CONTENTS_DIR/Resources/logo.icns"

IG_VERSION="$(sed -n 's:.*<IgVersion>\(.*\)</IgVersion>.*:\1:p' "$BUILD_PROPS_FILE" | head -n 1)"
if [[ -z "$IG_VERSION" ]]; then
	echo "Error: could not read IgVersion from $BUILD_PROPS_FILE" >&2
	exit 1
fi

# Marketing version (CFBundleShortVersionString): up to 3 integers, e.g. 10.0.0.
# Prefer the explicit <IgBundleShortVersion>; fall back to IgVersion minus its
# last segment.
IG_SHORT_VERSION="$(sed -n 's:.*<IgBundleShortVersion>\(.*\)</IgBundleShortVersion>.*:\1:p' "$BUILD_PROPS_FILE" | head -n 1)"
if [[ -z "$IG_SHORT_VERSION" ]]; then
	IG_SHORT_VERSION="${IG_VERSION%.*}"
	[[ -z "$IG_SHORT_VERSION" ]] && IG_SHORT_VERSION="$IG_VERSION"
fi

# Build number (CFBundleVersion): at most three integers. Prefer the explicit
# <IgBundleBuild>; fall back to the last segment of IgVersion (e.g. 531).
IG_BUILD="$(sed -n 's:.*<IgBundleBuild>\(.*\)</IgBundleBuild>.*:\1:p' "$BUILD_PROPS_FILE" | head -n 1)"
if [[ -z "$IG_BUILD" ]]; then
	IG_BUILD="${IG_VERSION##*.}"
	[[ -z "$IG_BUILD" ]] && IG_BUILD="$IG_VERSION"
fi

# Remove any previous bundle. If a prior run created it as root (e.g. a task was
# run with sudo), the user-level rm fails on every nested file — surface a single
# actionable message instead of a wall of "Permission denied" lines.
for prev_bundle in "$LEGACY_APP_DIR" "$APP_DIR"; do
	if [[ -d "$prev_bundle" ]]; then
		if ! rm -rf "$prev_bundle" 2>/dev/null || [[ -d "$prev_bundle" ]]; then
			echo "Error: could not remove the existing bundle at:" >&2
			echo "       $prev_bundle" >&2
			echo "       It is likely owned by root from a previous sudo run. Remove it with:" >&2
			echo "         sudo rm -rf \"$prev_bundle\"" >&2
			echo "       Then re-run this task WITHOUT sudo." >&2
			exit 1
		fi
	fi
done

mkdir -p "$CONTENTS_DIR/MacOS" "$CONTENTS_DIR/Resources"
cp -R "$PUBLISH_DIR/." "$CONTENTS_DIR/MacOS/"
cp "$ICON_SOURCE_FILE" "$ICON_TARGET_FILE"

# Finder's .DS_Store / ._* files are not Mach-O, so codesign later rejects Contents/MacOS.
find "$CONTENTS_DIR" -type f \( -name ".DS_Store" -o -name "._*" \) -delete

# Windows-only assets the publish step copies in from __assets/__app/. _ext_icons
# holds file-type association icons read by Win32DefaultAppApi; dead weight on macOS.
for win_only_dir in _ext_icons; do
	rm -rf "$CONTENTS_DIR/MacOS/$win_only_dir"
done

INFO_PLIST_TEMPLATE="$WORKSPACE_DIR/__assets/mac/Info.plist"

sed -e "s/\${IG_VERSION}/$IG_VERSION/g" \
    -e "s/\${IG_SHORT_VERSION}/$IG_SHORT_VERSION/g" \
    -e "s/\${IG_BUILD}/$IG_BUILD/g" \
    "$INFO_PLIST_TEMPLATE" > "$INFO_PLIST"

chmod +x "$CONTENTS_DIR/MacOS/${APP_NAME}"

# codesign rejects any non-Mach-O file or folder left in Contents/MacOS/ ("code object is
# not signed at all"), so stage every __assets/__app/ item — loose files included — under
# Contents/Resources/ and symlink it back so BaseDir() lookups still resolve at runtime.
cp -R "$WORKSPACE_DIR/__assets/__app/." "$CONTENTS_DIR/MacOS/"

for data_src in "$WORKSPACE_DIR/__assets/__app/"*; do
	[[ -e "$data_src" ]] || continue
	data_name="$(basename "$data_src")"
	staged_path="$CONTENTS_DIR/MacOS/$data_name"

	if [[ -e "$staged_path" && ! -L "$staged_path" ]]; then
		rm -rf "$CONTENTS_DIR/Resources/$data_name"
		mv "$staged_path" "$CONTENTS_DIR/Resources/$data_name"
		ln -s "../Resources/$data_name" "$staged_path"
	fi
done

# Remove debug artifacts that codesign rejects inside Contents/MacOS/.
find "$APP_DIR" -type f -name "*.pdb" -delete
find "$APP_DIR" -type d -name "*.dSYM" -exec rm -rf {} +

# Ad-hoc sign so local ARM64 launches and Finder "Open With" pass signature checks;
# script-pack-mac-arm64-dmg.sh re-signs with Developer ID (--force) for release.
ENTITLEMENTS_FILE="$WORKSPACE_DIR/__assets/mac/ImageGlass.entitlements"
while IFS= read -r -d '' lib; do
	codesign --force --options runtime --sign - "$lib"
done < <(find "$CONTENTS_DIR/MacOS" -type f \( -name "*.dylib" -o -name "*.so" \) -print0)

codesign --force --options runtime --entitlements "$ENTITLEMENTS_FILE" --sign - "$APP_DIR"

echo "Created bundle: $APP_DIR"
