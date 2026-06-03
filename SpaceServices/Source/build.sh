#!/usr/bin/env bash
set -euo pipefail

MOD_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION_DIR="$MOD_ROOT/1.6"
CSC="/usr/share/dotnet/sdk/$(dotnet --version)/Roslyn/bincore/csc.dll"

steam_library_paths() {
  if [[ -n "${STEAM_LIBRARY_DIRS:-}" ]]; then
    tr ':' '\n' <<< "$STEAM_LIBRARY_DIRS"
  fi

  local vdf
  for vdf in \
    "$HOME/.steam/steam/steamapps/libraryfolders.vdf" \
    "$HOME/.local/share/Steam/steamapps/libraryfolders.vdf" \
    "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/libraryfolders.vdf"
  do
    [[ -f "$vdf" ]] || continue
    sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"/\1/p' "$vdf"
  done
}

find_managed_dir() {
  if [[ -n "${RIMWORLD_MANAGED_DIR:-}" ]]; then
    printf '%s\n' "$RIMWORLD_MANAGED_DIR"
    return
  fi
  if [[ -n "${RIMWORLD_DIR:-}" ]]; then
    printf '%s\n' "$RIMWORLD_DIR/RimWorldLinux_Data/Managed"
    return
  fi

  local library candidate
  local libraries=()
  mapfile -t libraries < <(steam_library_paths)
  for library in "${libraries[@]}"; do
    candidate="$library/steamapps/common/RimWorld/RimWorldLinux_Data/Managed"
    if [[ -d "$candidate" ]]; then
      printf '%s\n' "$candidate"
      return
    fi
  done
}

find_harmony_dll() {
  if [[ -n "${HARMONY_DLL:-}" ]]; then
    printf '%s\n' "$HARMONY_DLL"
    return
  fi

  local library candidate
  local libraries=()
  mapfile -t libraries < <(steam_library_paths)
  for library in "${libraries[@]}"; do
    candidate="$library/steamapps/workshop/content/294100/2009463077/Current/Assemblies/0Harmony.dll"
    if [[ -f "$candidate" ]]; then
      printf '%s\n' "$candidate"
      return
    fi
  done
}

MANAGED="$(find_managed_dir)"
HARMONY="$(find_harmony_dll)"

if [[ -z "$MANAGED" || ! -d "$MANAGED" ]]; then
  echo "Could not find RimWorld managed assemblies. Set RIMWORLD_MANAGED_DIR or RIMWORLD_DIR." >&2
  exit 1
fi

if [[ -z "$HARMONY" || ! -f "$HARMONY" ]]; then
  echo "Could not find Harmony. Set HARMONY_DLL, or make sure workshop item 2009463077 is installed." >&2
  exit 1
fi

mkdir -p "$VERSION_DIR/Assemblies"

mapfile -t SOURCES < <(find "$MOD_ROOT/Source/SpaceServices" -name '*.cs' | sort)

dotnet "$CSC" \
  -nologo \
  -target:library \
  -langversion:7.3 \
  -nostdlib+ \
  -out:"$VERSION_DIR/Assemblies/SpaceServices.dll" \
  -r:"$MANAGED/mscorlib.dll" \
  -r:"$MANAGED/netstandard.dll" \
  -r:"$MANAGED/System.dll" \
  -r:"$MANAGED/System.Core.dll" \
  -r:"$MANAGED/Assembly-CSharp.dll" \
  -r:"$MANAGED/UnityEngine.dll" \
  -r:"$MANAGED/UnityEngine.CoreModule.dll" \
  -r:"$MANAGED/UnityEngine.IMGUIModule.dll" \
  -r:"$MANAGED/UnityEngine.TextRenderingModule.dll" \
  -r:"$HARMONY" \
  "${SOURCES[@]}"
