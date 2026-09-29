#!/usr/bin/env bash
set -euo pipefail

[ "$#" -ge 2 ] || { printf '%s\n' 'Usage: validate_linux_abi.sh linux-x64|linux-arm64 ELF...' >&2; exit 1; }
rid="$1"
shift
case "$rid" in
    linux-x64) expected_machine='Advanced Micro Devices X86-64' ;;
    linux-arm64) expected_machine='AArch64' ;;
    *) printf 'Unsupported Linux RID: %s\n' "$rid" >&2; exit 1 ;;
esac

export LC_ALL=C
for binary in "$@"; do
    header="$(readelf --file-header "$binary")"
    machine="$(printf '%s\n' "$header" | sed -n 's/^ *Machine: *//p')"
    [ "$machine" = "$expected_machine" ] || { printf 'ELF architecture mismatch: %s (%s)\n' "$binary" "$machine" >&2; exit 1; }
    versions="$(readelf --version-info "$binary")"
    requirements="$(printf '%s\n' "$versions" | awk '
        /^Version needs section/ { needed = 1; next }
        /^Version .* section/ { needed = 0 }
        needed { for (i = 1; i <= NF; i++) if ($i == "Name:" && $(i + 1) ~ /^GLIBC_/) print $(i + 1) }
    ' | sort -u)"
    maximum='none'
    while IFS= read -r requirement; do
        [ -n "$requirement" ] || continue
        version="${requirement#GLIBC_}"
        if [[ ! "$version" =~ ^[0-9]+\.[0-9]+(\.[0-9]+)?$ ]] ||
            [ "$(printf '%s\n' '2.35' "$version" | sort -V | tail -n 1)" != '2.35' ]; then
            printf 'Incompatible glibc requirement in %s: %s (maximum GLIBC_2.35)\n' "$binary" "$requirement" >&2
            exit 1
        fi
        if [ "$maximum" = 'none' ]; then
            maximum="$version"
        else
            maximum="$(printf '%s\n' "$maximum" "$version" | sort -V | tail -n 1)"
        fi
    done <<< "$requirements"
    printf 'Verified %s: %s, glibc %s\n' "$binary" "$rid" "$maximum"
done
