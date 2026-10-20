#!/usr/bin/env bash
# Symlinks this repo's mod/ folder into RimWorld's Mods folder as "ColonyCode".
# Env: RIMWORLD_MODS overrides the Mods folder. Pass --force to replace an existing link.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/mod"
MODS="${RIMWORLD_MODS:-/Volumes/SSD/SteamLibrary/steamapps/common/RimWorld/RimWorldMac.app/Mods}"
DEST="$MODS/ColonyCode"

if [[ ! -d "$MODS" ]]; then
  echo "link-mod.sh: Mods folder not found: $MODS (set RIMWORLD_MODS)" >&2
  exit 1
fi
if [[ ! -f "$SRC/About/About.xml" ]]; then
  echo "link-mod.sh: $SRC does not look like the mod folder" >&2
  exit 1
fi

if [[ -L "$DEST" ]]; then
  current="$(readlink "$DEST")"
  if [[ "$current" == "$SRC" ]]; then
    echo "link-mod.sh: already linked: $DEST -> $SRC"
    exit 0
  fi
  if [[ "${1:-}" == "--force" ]]; then
    rm "$DEST"
  else
    echo "link-mod.sh: $DEST already links to $current (use --force to replace)" >&2
    exit 1
  fi
elif [[ -e "$DEST" ]]; then
  echo "link-mod.sh: $DEST exists and is not a symlink; refusing to touch it" >&2
  exit 1
fi

ln -s "$SRC" "$DEST"
echo "link-mod.sh: linked $DEST -> $SRC"
