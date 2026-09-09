#!/bin/bash
set -euo pipefail

fail() {
    printf '%s\n' "$*" >&2
    exit 1
}

[ "$(uname -s)" = "Darwin" ] || fail "macOS packaging requires a Mac with Xcode command-line tools."
script_dir="$(cd "$(dirname "$0")" && pwd -P)"
repository_root="$(cd "$script_dir/.." && pwd -P)"
project="$repository_root/RatioMaster.App/RatioMaster.App.csproj"
output_root="$script_dir/Output"
icon_source="$repository_root/RatioMaster.App/Resources/mipmap-xxxhdpi/icon.png"
if [ ! -f "$icon_source" ]; then
    icon_source="$repository_root/RatioMaster.App/Assets/icon.ico"
fi

for tool in dotnet xcrun sips iconutil plutil codesign ditto xmllint; do
    command -v "$tool" >/dev/null 2>&1 || fail "Missing required command: $tool"
done
xcrun --find clang >/dev/null

[ "$#" -le 1 ] || fail "Usage: bash Installer/package_macos.sh [osx-arm64|osx-x64]"
rid=""
if [ "$#" -eq 1 ]; then
    rid="$1"
fi
if [ -z "$rid" ]; then
    case "$(uname -m)" in
        arm64) rid="osx-arm64" ;;
        x86_64) rid="osx-x64" ;;
        *) fail "Unsupported Mac architecture." ;;
    esac
fi
case "$rid" in
    osx-arm64) binary_architecture="arm64" ;;
    osx-x64) binary_architecture="x86_64" ;;
    *) fail "Only osx-arm64 and osx-x64 can be packaged on macOS." ;;
esac

cd "$repository_root"
version="$(xmllint --xpath 'string(/Project/PropertyGroup/Version)' "$project")"
assembly="$(xmllint --xpath 'string(/Project/PropertyGroup/AssemblyName)' "$project")"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "The project Version must contain three numeric components."
[[ "$assembly" =~ ^[A-Za-z0-9._-]+$ ]] || fail "The project AssemblyName is not a supported bundle executable name."
[ -f "$icon_source" ] || fail "The project icon is missing."

mkdir -p "$output_root"
output_root="$(cd "$output_root" && pwd -P)"
lock_path="$output_root/.validation-package.lock"
lock_owned=0
stage_root=""
cleanup() {
    if [ -n "$stage_root" ]; then
        case "$stage_root" in
            "$output_root"/.macos-package.*) rm -rf -- "$stage_root" ;;
        esac
    fi
    if [ "$lock_owned" -eq 1 ]; then
        rm -f -- "$lock_path"
    fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
if (set -o noclobber; printf '%s\n' "macOS packaging PID=$$" > "$lock_path") 2>/dev/null; then
    lock_owned=1
else
    fail "Another validation or package process owns $lock_path. Check for an interrupted process before removing a stale lock."
fi

artifact_name="$(printf '%s_%s_v%s' "$assembly" "$rid" "$version")"
bundle_destination="$output_root/$artifact_name.app"
archive_destination="$output_root/$artifact_name.zip"
[ ! -e "$bundle_destination" ] || fail "The bundle already exists: $bundle_destination. Move it aside before rebuilding; its possible session data will not be deleted."
publish_directory="$repository_root/RatioMaster.App/publish/$rid"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
dotnet publish "$project" -c Release "-p:DesktopRid=$rid" -p:IncludeAndroid=false \
    -o "$publish_directory" --nologo 2>&1 | tee "$output_root/package-$rid.log"

binary="$publish_directory/$assembly"
[ -f "$binary" ] || fail "The native executable was not produced: $binary"
architecture="$(xcrun lipo -archs "$binary")"
[ "$architecture" = "$binary_architecture" ] || fail "The published executable architecture does not match $rid: $architecture"
minimum_os="$(xcrun vtool -show-build "$binary" | awk '$1 == "minos" { print $2; exit }')"
[[ "$minimum_os" =~ ^[0-9]+(\.[0-9]+)*$ ]] || fail "The executable does not expose a valid minimum macOS version."

stage_root="$(mktemp -d "$output_root/.macos-package.XXXXXX")"
stage_bundle="$stage_root/$artifact_name.app"
macos_directory="$stage_bundle/Contents/MacOS"
resources_directory="$stage_bundle/Contents/Resources"
mkdir -p "$macos_directory" "$resources_directory"
if [ -f "$repository_root/LICENSE" ]; then
    cp "$repository_root/LICENSE" "$resources_directory/LICENSE"
fi
cp "$binary" "$macos_directory/$assembly"
chmod +x "$macos_directory/$assembly"
for library in "$publish_directory/"*.dylib; do
    [ -f "$library" ] || continue
    cp "$library" "$macos_directory/"
done

# Native executables and libraries are copied explicitly; session and user catalogue files stay outside the archive.
iconset="$stage_root/RatioMaster.iconset"
mkdir -p "$iconset"
source_png="$stage_root/source.png"
sips -s format png "$icon_source" --out "$source_png" >/dev/null
for size in 16 32 128 256 512; do
    ordinary="$(printf '%s/icon_%sx%s.png' "$iconset" "$size" "$size")"
    retina="$(printf '%s/icon_%sx%s@2x.png' "$iconset" "$size" "$size")"
    doubled="$((size * 2))"
    sips -z "$size" "$size" "$source_png" --out "$ordinary" >/dev/null
    sips -z "$doubled" "$doubled" "$source_png" --out "$retina" >/dev/null
done
iconutil --convert icns --output "$resources_directory/RatioMaster.icns" "$iconset"

plist="$stage_bundle/Contents/Info.plist"
plutil -create xml1 "$plist"
plutil -insert CFBundleName -string "$assembly" "$plist"
plutil -insert CFBundleDisplayName -string "$assembly" "$plist"
plutil -insert CFBundleIdentifier -string 'net.ratiomaster.app' "$plist"
plutil -insert CFBundleVersion -string "$version" "$plist"
plutil -insert CFBundleShortVersionString -string "$version" "$plist"
plutil -insert CFBundlePackageType -string 'APPL' "$plist"
plutil -insert CFBundleExecutable -string "$assembly" "$plist"
plutil -insert CFBundleIconFile -string 'RatioMaster.icns' "$plist"
plutil -insert NSHighResolutionCapable -bool YES "$plist"
plutil -insert LSMinimumSystemVersion -string "$minimum_os" "$plist"
plutil -lint "$plist"

for library in "$macos_directory/"*.dylib; do
    [ -f "$library" ] || continue
    codesign --force --sign - --timestamp=none "$library"
done
codesign --force --sign - --timestamp=none "$macos_directory/$assembly"
codesign --force --sign - --timestamp=none "$stage_bundle"
codesign --verify --deep --strict "$stage_bundle"

archive_name="$artifact_name.zip"
ditto -c -k --sequesterRsrc --keepParent "$stage_bundle" "$stage_root/$archive_name"
(
    cd "$stage_root"
    shasum -a 256 "$archive_name" > "$archive_name.sha256"
)
mv "$stage_bundle" "$bundle_destination"
mv -f "$stage_root/$archive_name" "$archive_destination"
mv -f "$stage_root/$archive_name.sha256" "$archive_destination.sha256"
printf 'Created %s\nCreated %s\n' "$bundle_destination" "$archive_destination"
printf '%s\n' "The bundle has an ad-hoc signature; it is not Developer ID signed or notarized."

