#!/usr/bin/env bash
set -euo pipefail

fail() { printf '%s\n' "$*" >&2; exit 1; }
missing() { printf '%s\n' "$*" >&2; exit 125; }
script_dir="$(cd "$(dirname "$0")" && pwd -P)"
installer_root="$(cd "$script_dir/.." && pwd -P)"
repository_root="$(cd "$installer_root/.." && pwd -P)"
check_only=0
if [ "${1:-}" = '--check-prerequisites' ]; then
    check_only=1
    shift
fi
[ "$#" -le 2 ] || fail 'Usage: package_linux.sh [--check-prerequisites] [linux-x64|linux-arm64] [output-directory]'
rid="${1:-linux-x64}"
case "$rid" in
    linux-x64) architecture=x86_64; runtime_hash=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d ;;
    linux-arm64) architecture=aarch64; runtime_hash=00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444 ;;
    *) fail "Unsupported Linux RID: $rid" ;;
esac

# Native AOT links against the build system's libc; Ubuntu 22.04 defines the portable ABI baseline.
[ -r /etc/os-release ] || missing 'Linux native packaging requires Ubuntu 22.04.'
. /etc/os-release
[ "${ID:-}" = ubuntu ] && [ "${VERSION_ID:-}" = 22.04 ] || missing 'Linux native packaging requires Ubuntu 22.04 (glibc 2.35). Use that distribution or the self-contained archive fallback.'
for tool in clang readelf tar curl sha256sum desktop-file-validate flock; do
    command -v "$tool" >/dev/null 2>&1 || missing "Missing Linux packaging prerequisite: $tool"
done
[ -f /usr/include/zlib.h ] || missing 'Install zlib1g-dev before native packaging.'
host_architecture="$(uname -m)"
case "$host_architecture" in
    x86_64) tool_hash=ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0 ;;
    aarch64) tool_hash=f0837e7448a0c1e4e650a93bb3e85802546e60654ef287576f46c71c126a9158 ;;
    *) missing "Unsupported Linux build host: $host_architecture" ;;
esac
compiler_arguments=()
if [ "$architecture" != "$host_architecture" ]; then
    [ "$host_architecture" = x86_64 ] && [ "$architecture" = aarch64 ] || missing 'Only native builds and x64-to-ARM64 cross builds are supported.'
    for tool in aarch64-linux-gnu-gcc aarch64-linux-gnu-objcopy; do
        command -v "$tool" >/dev/null 2>&1 || missing "Missing cross compiler prerequisite: $tool"
    done
fi
dotnet="${DOTNET:-dotnet}"
if ! command -v "$dotnet" >/dev/null 2>&1; then
    dotnet="$HOME/.dotnet/dotnet"
fi
[ -x "$dotnet" ] || command -v "$dotnet" >/dev/null 2>&1 || missing 'Install the SDK selected by global.json before native packaging.'
cd "$repository_root"
"$dotnet" --version || missing 'The installed SDK cannot satisfy global.json.'
if [ "$check_only" -eq 1 ]; then
    exit 0
fi

output_root="${2:-$installer_root/Output}"
mkdir -p "$output_root"
output_root="$(cd "$output_root" && pwd -P)"
exec 9>"$output_root/.linux-package-$rid.lock"
flock -n 9 || fail "Another Linux package build owns $rid in $output_root."
stage_root="$(mktemp -d "${TMPDIR:-/tmp}/ratiomaster-linux.XXXXXXXX")"
cleanup() {
    case "$stage_root" in
        "${TMPDIR:-/tmp}"/ratiomaster-linux.*) rm -rf -- "$stage_root" ;;
    esac
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# A private source copy prevents shared obj/bin collisions and excludes user data from every artifact.
mkdir -p "$stage_root/source"
tar -C "$repository_root" --exclude=bin --exclude=obj --exclude=publish --exclude='*.session' --exclude=sessions.json \
    -cf - RatioMaster.App global.json Directory.Build.props CHANGELOG.md | tar -C "$stage_root/source" -xf -
cd "$stage_root/source"
project="$stage_root/source/RatioMaster.App/RatioMaster.App.csproj"
version="$("$dotnet" msbuild "$project" -nologo -getProperty:Version "-p:DesktopRid=$rid" -p:IncludeAndroid=false)"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail 'The project Version must contain three numeric components.'
if [ "$architecture" != "$host_architecture" ]; then
    wrapper="$stage_root/aarch64-linker"
    cat > "$wrapper" <<'WRAPPER'
#!/usr/bin/env bash
args=()
for argument in "$@"; do
    case "$argument" in
        --target=*|--gcc-toolchain=*) ;;
        *) args+=("$argument") ;;
    esac
done
exec aarch64-linux-gnu-gcc "${args[@]}"
WRAPPER
    chmod +x "$wrapper"
    compiler_arguments=("-p:CppCompilerAndLinker=$wrapper" -p:ObjCopyName=aarch64-linux-gnu-objcopy)
fi
publish_directory="$stage_root/publish"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
"$dotnet" publish "$project" -c Release "-p:DesktopRid=$rid" -p:IncludeAndroid=false -p:PublishAot=true \
    -p:DebugType=none "${compiler_arguments[@]}" -o "$publish_directory"
binary="$publish_directory/RatioMaster.NET"
[ -f "$binary" ] || fail 'Native publishing did not produce RatioMaster.NET.'
shopt -s nullglob
native_files=("$binary" "$publish_directory/"*.so "$publish_directory/"*.so.*)
bash "$script_dir/validate_linux_abi.sh" "$rid" "${native_files[@]}"

app_directory="$stage_root/AppDir"
mkdir -p "$app_directory/usr/bin"
cp "${native_files[@]}" "$app_directory/usr/bin/"
cp "$installer_root/Data/AppRun" "$app_directory/AppRun"
cp "$installer_root/Data/RatioMaster.desktop" "$app_directory/RatioMaster.desktop"
sed -i 's/\r$//' "$app_directory/AppRun" "$app_directory/RatioMaster.desktop"
cp "$repository_root/icon.png" "$app_directory/ratiomaster.png"
cp "$repository_root/LICENSE" "$app_directory/LICENSE"
chmod +x "$app_directory/AppRun" "$app_directory/usr/bin/RatioMaster.NET"
desktop-file-validate "$app_directory/RatioMaster.desktop"

cache="${XDG_CACHE_HOME:-$HOME/.cache}/ratiomaster-appimage"
mkdir -p "$cache"
fetch_tool() {
    local name="$1" url="$2" hash="$3" cached="$cache/$1"
    if [ -f "$cached" ] && printf '%s  %s\n' "$hash" "$cached" | sha256sum -c - >/dev/null 2>&1; then
        return
    fi
    [ "${RM_LINUX_OFFLINE:-0}" != 1 ] || fail "Missing verified AppImage tool in offline mode: $cached"
    curl --fail --location --silent --show-error "$url" -o "$stage_root/$name"
    printf '%s  %s\n' "$hash" "$stage_root/$name" | sha256sum -c -
    mv -f "$stage_root/$name" "$cached"
}
tool_name="appimagetool-1.9.1-$host_architecture.AppImage"
runtime_name="runtime-20251108-$architecture"
fetch_tool "$tool_name" "https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-$host_architecture.AppImage" "$tool_hash"
fetch_tool "$runtime_name" "https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-$architecture" "$runtime_hash"
chmod +x "$cache/$tool_name"
bash "$script_dir/validate_linux_abi.sh" "$rid" "$cache/$runtime_name"
artifact="RatioMaster.NET_v${version}_${architecture}.AppImage"
APPIMAGE_EXTRACT_AND_RUN=1 ARCH="$architecture" "$cache/$tool_name" --runtime-file "$cache/$runtime_name" "$app_directory" "$stage_root/$artifact"
chmod +x "$stage_root/$artifact"
if [ "$architecture" = "$host_architecture" ]; then
    "$app_directory/AppRun" --runtime-selftest
    APPIMAGE_EXTRACT_AND_RUN=1 "$stage_root/$artifact" --runtime-selftest
else
    printf '%s\n' 'Cross-compiled artifact: runtime self-test requires a native ARM64 Linux host.'
fi
(
    cd "$stage_root"
    sha256sum "$artifact" > "$artifact.sha256"
)
mv -f "$stage_root/$artifact" "$output_root/$artifact"
mv -f "$stage_root/$artifact.sha256" "$output_root/$artifact.sha256"
printf 'Created %s\n' "$output_root/$artifact"
