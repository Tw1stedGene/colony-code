#!/usr/bin/env bash
# Compiles mod/Source/ColonyCode/Bridge/*.cs + BridgeTests.cs against Mono's normal framework and runs them.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
CSC="${CSC:-csc}"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/colonycode-bridge-test.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/home"

"$CSC" -nologo -langversion:9 -target:exe -warn:4 -out:"$WORK/BridgeTests.exe" \
  "$ROOT"/mod/Source/ColonyCode/Bridge/*.cs "$HERE/BridgeTests.cs"

# Temp HOME so the test never touches the real ~/.colony-code/bridge.json.
HOME="$WORK/home" mono --debug "$WORK/BridgeTests.exe"
