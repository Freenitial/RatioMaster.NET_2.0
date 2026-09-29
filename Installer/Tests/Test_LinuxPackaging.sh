#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd "$(dirname "$0")" && pwd -P)"
installer_root="$(cd "$script_dir/.." && pwd -P)"
scratch="$(mktemp -d "${TMPDIR:-/tmp}/ratiomaster-packaging-test.XXXXXXXX")"
cleanup() {
    case "$scratch" in
        "${TMPDIR:-/tmp}"/ratiomaster-packaging-test.*) rm -rf -- "$scratch" ;;
    esac
}
trap cleanup EXIT
case "$(uname -m)" in
    x86_64) rid=linux-x64; wrong_rid=linux-arm64 ;;
    aarch64) rid=linux-arm64; wrong_rid=linux-x64 ;;
    *) exit 125 ;;
esac
printf '%s\n' 'int main(void) { return 0; }' > "$scratch/main.c"
clang "$scratch/main.c" -o "$scratch/compatible"
bash "$installer_root/Scripts/validate_linux_abi.sh" "$rid" "$scratch/compatible"
if bash "$installer_root/Scripts/validate_linux_abi.sh" "$wrong_rid" "$scratch/compatible" > "$scratch/wrong-architecture.log" 2>&1; then
    printf '%s\n' 'An ELF with the wrong architecture was accepted.' >&2
    exit 1
fi

# A versioned import models the GLIBC_2.38 regression without depending on the host libc version.
printf '%s\n' 'int newer_api(void) { return 0; }' > "$scratch/library.c"
printf '%s\n' 'GLIBC_2.38 { global: newer_api; };' > "$scratch/versions.map"
clang -shared -fPIC "$scratch/library.c" "-Wl,--version-script=$scratch/versions.map" -o "$scratch/libnewer.so"
printf '%s\n' 'extern int newer_api(void); int main(void) { return newer_api(); }' > "$scratch/main.c"
clang "$scratch/main.c" "-L$scratch" -lnewer -o "$scratch/incompatible"
if bash "$installer_root/Scripts/validate_linux_abi.sh" "$rid" "$scratch/incompatible" > "$scratch/too-new.log" 2>&1; then
    printf '%s\n' 'An ELF requiring GLIBC_2.38 was accepted.' >&2
    exit 1
fi
grep -q 'GLIBC_2.38' "$scratch/too-new.log"

mkdir -p "$scratch/AppDir/usr/bin"
cp "$installer_root/Data/AppRun" "$scratch/AppDir/AppRun"
cat > "$scratch/AppDir/usr/bin/RatioMaster.NET" <<'STUB'
#!/bin/sh
printf '%s\n' "$LD_LIBRARY_PATH" "$APPIMAGE" "$@"
STUB
chmod +x "$scratch/AppDir/usr/bin/RatioMaster.NET"
APPIMAGE='/some path/RatioMaster.AppImage' LD_LIBRARY_PATH='' sh "$scratch/AppDir/AppRun" 'space value' '--runtime-selftest' > "$scratch/actual"
printf '%s\n' "$scratch/AppDir/usr/bin" '/some path/RatioMaster.AppImage' 'space value' '--runtime-selftest' > "$scratch/expected"
cmp "$scratch/expected" "$scratch/actual"
APPIMAGE='/another/AppImage' LD_LIBRARY_PATH='/system/lib' sh "$scratch/AppDir/AppRun" > "$scratch/actual"
printf '%s\n' "$scratch/AppDir/usr/bin:/system/lib" '/another/AppImage' > "$scratch/expected"
cmp "$scratch/expected" "$scratch/actual"
printf '%s\n' 'PASS: compatible ELF, incompatible glibc, wrong architecture, AppRun argument and environment forwarding.'
