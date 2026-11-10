#!/bin/bash
# Builds GameImporter.dll on Linux with the .NET SDK's C# compiler against your own Valheim + BepInEx install.
#   ./build.sh /path/to/Valheim [out.dll]
set -euo pipefail
VAL="${1:?path to your Valheim folder (with BepInEx installed)}"
OUT="${2:-GameImporter.dll}"
CSC=$(ls -d /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll /usr/share/dotnet/sdk/*/Roslyn/bincore/csc.dll 2>/dev/null | tail -1)
[ -n "$CSC" ] || { echo "csc.dll not found - install the .NET SDK" >&2; exit 1; }
REFS=()
for f in "$VAL"/valheim_Data/Managed/*.dll "$VAL"/BepInEx/core/BepInEx.dll "$VAL"/BepInEx/core/0Harmony.dll; do REFS+=("/reference:$f"); done
dotnet "$CSC" /nologo /target:library /nostdlib+ /noconfig /langversion:latest /optimize+ \
  /nowarn:CS0618,CS0436,CS1701,CS1702 "/out:$OUT" "${REFS[@]}" "$(dirname "$0")"/src/*.cs
echo "built $OUT"
