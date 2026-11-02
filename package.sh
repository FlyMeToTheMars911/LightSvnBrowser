#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
        if ! command -v powershell.exe >/dev/null 2>&1 || ! command -v cygpath >/dev/null 2>&1; then
            printf '%s\n' 'ERROR: Windows PowerShell and cygpath are required. Run package.cmd from Windows if they are not on PATH.' >&2
            exit 2
        fi
        script_path="$(cygpath -w "$script_dir/scripts/Package.ps1")"
        ;;
    Linux*)
        if [[ -z "${WSL_INTEROP:-}${WSL_DISTRO_NAME:-}" ]] || ! command -v powershell.exe >/dev/null 2>&1 || ! command -v wslpath >/dev/null 2>&1; then
            printf '%s\n' 'ERROR: Linux cannot package this Windows .NET Framework application natively. Use Windows, or WSL with Windows PowerShell interop enabled.' >&2
            exit 2
        fi
        script_path="$(wslpath -w "$script_dir/scripts/Package.ps1")"
        ;;
    *)
        printf '%s\n' 'ERROR: This project targets Windows WinForms/.NET Framework. Native Linux/macOS builds are unsupported; use Windows or WSL Windows PowerShell interop.' >&2
        exit 2
        ;;
esac

# Preserve each original argument; path options accept Windows paths or project-relative paths.
exec powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "$script_path" "$@"
