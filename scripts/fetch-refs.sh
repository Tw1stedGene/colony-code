#!/usr/bin/env bash
# Downloads the reference assemblies needed to build Colony Code into refs/ (gitignored).
# build.sh uses the local game when it is the version being built, else these.
#   refs/1.6/            Krafs.Rimworld.Ref 1.6 -- game + Mono BCL + Unity reference assemblies (ref/net472)
#   refs/1.7/            Krafs.Rimworld.Ref (RimRef) -- game reference assemblies (ref/net10.0)
#   refs/netcore-10.0/   Microsoft.NETCore.App.Ref   -- .NET 10 BCL reference assemblies
# RimWorld 1.7 runs on .NET 10 (not Mono/Unity); RimRef ships no BCL from 1.7 on, so
# the .NET reference pack is needed too. No dotnet SDK is required: mono's csc compiles
# against these metadata-only assemblies.
# Env: RIMREF_VERSION (default 1.7.4981-unstable), NETREF_VERSION (default 10.0.13). --force re-downloads.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RIMREF_VERSION="${RIMREF_VERSION:-1.7.4981-unstable}"
RIMREF16_VERSION="${RIMREF16_VERSION:-1.6.4871}"
NETREF_VERSION="${NETREF_VERSION:-10.0.13}"
FORCE="${1:-}"

fetch() { # id version destdir marker
  local id="$1" ver="$2" dest="$3" marker="$4"
  if [[ -f "$dest/$marker" && "$FORCE" != "--force" ]]; then
    echo "fetch-refs.sh: $id present ($dest)"; return
  fi
  rm -rf "$dest"; mkdir -p "$dest"
  local pkg="$dest/$id.$ver.nupkg"
  echo "fetch-refs.sh: downloading $id $ver"
  curl -fsSL -o "$pkg" "https://api.nuget.org/v3-flatcontainer/$id/$ver/$id.$ver.nupkg"
  unzip -oq "$pkg" -d "$dest"
  [[ -f "$dest/$marker" ]] || { echo "fetch-refs.sh: $marker missing after unpacking $id $ver" >&2; exit 1; }
}

fetch krafs.rimworld.ref "$RIMREF16_VERSION" "$ROOT/refs/1.6" ref/net472/Assembly-CSharp.dll
fetch krafs.rimworld.ref "$RIMREF_VERSION" "$ROOT/refs/1.7" ref/net10.0/Assembly-CSharp.dll
fetch microsoft.netcore.app.ref "$NETREF_VERSION" "$ROOT/refs/netcore-10.0" ref/net10.0/System.Runtime.dll
echo "fetch-refs.sh: OK"
