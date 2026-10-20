#!/usr/bin/env bash
# Builds mod/$RW_VER/Assemblies/ColonyCode.dll from every .cs under mod/Source/ColonyCode.
# Env:
#   RW_VER            target RimWorld version / output folder: 1.6 (default) or 1.7
#   RIMWORLD_MANAGED  game reference assemblies. Defaults:
#                       1.6: the local macOS Steam install's Managed dir (net472, Mono/Unity)
#                       1.7: <repo>/refs/1.7/ref/net10.0 (Krafs.Rimworld.Ref; run scripts/fetch-refs.sh)
#   DOTNET_REF        1.7 only: .NET 10 BCL reference pack (default <repo>/refs/netcore-10.0/ref/net10.0)
#   CSC               compiler (default: csc from PATH, falls back to mcs)
#   EXTRA_REFS        optional extra reference paths, space separated (e.g. Harmony)
# Defines: RW16 or RW17 (use `#if RW17` to guard version-specific API differences).
#
# RimWorld 1.7 no longer runs on Mono/Unity: it runs on .NET 10, and the UnityEngine
# namespace the mod uses comes from the game's UnityStubs.dll. There are no UnityEngine.*
# module DLLs for 1.7, and the local 1.6 game's Unity DLLs must NOT be mixed in.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
RW_VER="${RW_VER:-1.6}"
# Default references: the local game when it is that version, else the downloaded reference
# assemblies in refs/ (scripts/fetch-refs.sh). One Steam install holds one version at a time.
STEAM_RW="/Volumes/SSD/SteamLibrary/steamapps/common/RimWorld"
case "$RW_VER" in
  1.6) DEFAULT_MANAGED="$STEAM_RW/RimWorldMac.app/Contents/Resources/Data/Managed"
       [[ -f "$DEFAULT_MANAGED/Assembly-CSharp.dll" ]] || DEFAULT_MANAGED="$ROOT/refs/1.6/ref/net472" ;;
  1.7) DEFAULT_MANAGED="$STEAM_RW/RimWorld.app/Contents/MacOS"
       [[ -f "$DEFAULT_MANAGED/UnityStubs.dll" ]] || DEFAULT_MANAGED="$ROOT/refs/1.7/ref/net10.0" ;;
  *) echo "build.sh: unsupported RW_VER=$RW_VER (expected 1.6 or 1.7)" >&2; exit 1 ;;
esac
RIMWORLD_MANAGED="${RIMWORLD_MANAGED:-$DEFAULT_MANAGED}"
SRC="$HERE/Source/ColonyCode"
OUT_DIR="$HERE/$RW_VER/Assemblies"
OUT="$OUT_DIR/ColonyCode.dll"

if [[ -z "${CSC:-}" ]]; then
  if command -v csc >/dev/null 2>&1; then CSC=csc
  elif command -v mcs >/dev/null 2>&1; then CSC=mcs
  else echo "build.sh: no C# compiler (csc or mcs) on PATH" >&2; exit 1
  fi
fi

if [[ ! -f "$RIMWORLD_MANAGED/Assembly-CSharp.dll" ]]; then
  echo "build.sh: Assembly-CSharp.dll not found in RIMWORLD_MANAGED=$RIMWORLD_MANAGED" >&2
  [[ "$RW_VER" == 1.7 ]] && echo "build.sh: run scripts/fetch-refs.sh to download the 1.7 reference assemblies" >&2
  exit 1
fi

SOURCES=()
while IFS= read -r -d '' f; do SOURCES+=("$f"); done < <(find "$SRC" -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' -print0 | sort -z)
if [[ ${#SOURCES[@]} -eq 0 ]]; then
  echo "build.sh: no .cs files under $SRC" >&2
  exit 1
fi

REFS=()
FLAGS=()
if [[ "$RW_VER" == 1.6 ]]; then
  FLAGS+=("-define:RW16")
  for dll in mscorlib System System.Core netstandard Assembly-CSharp \
             UnityEngine UnityEngine.CoreModule UnityEngine.IMGUIModule UnityEngine.TextRenderingModule \
             UnityEngine.InputLegacyModule UnityEngine.ScreenCaptureModule; do
    if [[ -f "$RIMWORLD_MANAGED/$dll.dll" ]]; then
      REFS+=("-r:$RIMWORLD_MANAGED/$dll.dll")
    elif [[ "$dll" == mscorlib || "$dll" == System || "$dll" == System.Core || "$dll" == Assembly-CSharp || "$dll" == UnityEngine.CoreModule ]]; then
      echo "build.sh: required reference $dll.dll missing in $RIMWORLD_MANAGED" >&2
      exit 1
    fi
  done
else
  # -noconfig: keep mono's csc.rsp from adding Mono framework (net4x) references.
  FLAGS+=("-define:RW17" "-noconfig")
  DOTNET_REF="${DOTNET_REF:-$ROOT/refs/netcore-10.0/ref/net10.0}"
  if [[ ! -f "$DOTNET_REF/System.Runtime.dll" ]]; then
    echo "build.sh: .NET 10 reference pack not found in DOTNET_REF=$DOTNET_REF (run scripts/fetch-refs.sh)" >&2
    exit 1
  fi
  for f in "$DOTNET_REF"/*.dll; do REFS+=("-r:$f"); done
  # Game assemblies. BCL-like packages shipped with the game (System.*, Microsoft.*, IndexRange)
  # are skipped: the .NET reference pack above provides those APIs.
  for dll in Assembly-CSharp UnityStubs; do
    [[ -f "$RIMWORLD_MANAGED/$dll.dll" ]] || { echo "build.sh: required reference $dll.dll missing in $RIMWORLD_MANAGED" >&2; exit 1; }
  done
  for f in "$RIMWORLD_MANAGED"/*.dll; do
    case "$(basename "$f")" in System.*|Microsoft.*|IndexRange.dll) continue ;; esac
    REFS+=("-r:$f")
  done
fi
# Optional extra references (e.g. Harmony): space-separated paths in EXTRA_REFS.
for extra in ${EXTRA_REFS:-}; do REFS+=("-r:$extra"); done

mkdir -p "$OUT_DIR"
echo "build.sh: compiling ${#SOURCES[@]} files for RimWorld $RW_VER -> $OUT"
# Compile to a temp file, then rename it into place: a running game (1.7's logger re-reads method
# metadata from the DLL on disk) keeps reading its old copy instead of a half-written file.
TMP_OUT="$OUT_DIR/.ColonyCode.dll.tmp"
"$CSC" -nologo -target:library -nostdlib -langversion:9 -optimize+ -debug- -warn:4 \
  -nowarn:1701,1702 "${FLAGS[@]}" \
  "${REFS[@]}" -out:"$TMP_OUT" "${SOURCES[@]}"
mv -f "$TMP_OUT" "$OUT"
echo "build.sh: OK"
