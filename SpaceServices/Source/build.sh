#!/usr/bin/env bash
set -euo pipefail

MOD_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION_DIR="$MOD_ROOT/1.6"
MANAGED="/mnt/essd/SteamLibrary/steamapps/common/RimWorld/RimWorldLinux_Data/Managed"
HARMONY="/mnt/essd/SteamLibrary/steamapps/workshop/content/294100/2009463077/Current/Assemblies/0Harmony.dll"
CSC="/usr/share/dotnet/sdk/$(dotnet --version)/Roslyn/bincore/csc.dll"

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
