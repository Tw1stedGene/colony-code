#!/usr/bin/env bash
# Builds ColonyCode.dll for every supported RimWorld version:
#   1.6 -> mod/1.6/Assemblies (against the local game install, net472)
#   1.7 -> mod/1.7/Assemblies (against refs/, net10.0; downloaded on demand by fetch-refs.sh)
# Pass versions to build a subset, e.g. scripts/build-all.sh 1.7
# Env: RIMWORLD_MANAGED_16 / RIMWORLD_MANAGED_17 override each version's reference dir.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSIONS=("$@"); [[ ${#VERSIONS[@]} -gt 0 ]] || VERSIONS=(1.6 1.7)
for v in "${VERSIONS[@]}"; do
  override_var="RIMWORLD_MANAGED_${v//./}"
  override="${!override_var:-}"
  if [[ "$v" == 1.7 && -z "$override" ]]; then bash "$ROOT/scripts/fetch-refs.sh"; fi
  if [[ -n "$override" ]]; then
    RW_VER="$v" RIMWORLD_MANAGED="$override" bash "$ROOT/mod/build.sh"
  else
    env -u RIMWORLD_MANAGED RW_VER="$v" bash "$ROOT/mod/build.sh"
  fi
done
echo "build-all.sh: built ${VERSIONS[*]}"
