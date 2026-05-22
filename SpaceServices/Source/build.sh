#!/usr/bin/env bash
set -euo pipefail

MOD_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MANAGED="/mnt/essd/SteamLibrary/steamapps/common/RimWorld/RimWorldLinux_Data/Managed"
HARMONY="/mnt/essd/SteamLibrary/steamapps/workshop/content/294100/2009463077/Current/Assemblies/0Harmony.dll"
CSC="/usr/share/dotnet/sdk/$(dotnet --version)/Roslyn/bincore/csc.dll"

mkdir -p "$MOD_ROOT/Assemblies"

mapfile -t SOURCES < <(find "$MOD_ROOT/Source/SpaceServices" -name '*.cs' | sort)

dotnet "$CSC" \
  -nologo \
  -target:library \
  -langversion:7.3 \
  -nostdlib+ \
  -out:"$MOD_ROOT/Assemblies/SpaceServices.dll" \
  -r:"$MANAGED/mscorlib.dll" \
  -r:"$MANAGED/netstandard.dll" \
  -r:"$MANAGED/System.dll" \
  -r:"$MANAGED/System.Core.dll" \
  -r:"$MANAGED/Assembly-CSharp.dll" \
  -r:"$MANAGED/UnityEngine.dll" \
  -r:"$MANAGED/UnityEngine.CoreModule.dll" \
  -r:"$MANAGED/UnityEngine.IMGUIModule.dll" \
  -r:"$HARMONY" \
  "${SOURCES[@]}"
