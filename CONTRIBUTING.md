# Contributing to Colony Code

This repo holds the RimWorld mod (`mod/`), the Claude Code plugin (`plugin/`), developer tools (`tools/`) and tests.
Players don't need any of this; see the [README](README.md).

## Architecture

```
 Claude Code (your login)                         RimWorld + Colony Code mod
┌──────────────────────────┐   POST /events      ┌───────────────────────────────┐
│ main session  -> lead    │ ──────────────────► │ HTTP bridge 127.0.0.1:7878    │
│ subagents     -> workers │                     │   token in ~/.colony-code/    │
│ plugin "colony-code"     │ ◄────────────────── │   bridge.json                 │
│   hooks, inbox timer     │   GET /inbox        │ pawns, desks, letters, alerts │
└──────────────────────────┘   GET /decision/<id>└───────────────────────────────┘
```

- **`mod/`** is the RimWorld mod. It hosts the bridge and writes `~/.colony-code/bridge.json` (port
  and token) when the game starts.
- **`plugin/`** is the Claude Code plugin. It reads that file, posts what Claude Code does, and
  polls the game for goals, messages and answers. When the game is not running, it stays quiet and
  Claude Code works as usual.


## Building and testing

Only needed if you want to change the mod or the plugin. Players never build anything.

You need Mono's `csc` (or `mcs`) and either the RimWorld install or the reference assemblies from
`scripts/fetch-refs.sh`; no .NET SDK or NuGet client. The dev tools need Node.js 24 or newer, with
no npm packages. `scripts/build-all.sh` builds both `mod/1.6` and `mod/1.7`.

### 1. Build and link the mod

```sh
# Defaults to the macOS Steam install; override for your machine.
RIMWORLD_MANAGED="/path/to/RimWorld/.../Managed" RW_VER=1.6 mod/build.sh
# -> mod/1.6/Assemblies/ColonyCode.dll

# Symlink mod/ into RimWorld's Mods folder as "ColonyCode".
RIMWORLD_MODS="/path/to/RimWorld/Mods" scripts/link-mod.sh
```

Enable **Colony Code** in the game's mod list and load a colony. The bridge starts with the game and
writes `~/.colony-code/bridge.json`.

### 2. Drive the game without spending Claude usage

`tools/replay.mjs` posts a scripted scenario to the running bridge: the lead clocks in, thinks, reads
files, edits with diffs, spawns a test-runner worker, asks permission for `npm install` and waits
for your answer, then finishes the goal.

```sh
node tools/replay.mjs                       # tools/scenarios/basic.json at real speed
node tools/replay.mjs --speed 3 --loop      # faster, forever (Ctrl-C sends session.end)
node tools/replay.mjs --inbox               # also print goals/messages you send from the game
node tools/replay.mjs --help
```

Answer the permission letter in the game. Allow and Deny take different branches. With no answer
within `--decision-timeout` seconds (default 300), replay acts as if you had answered in the
terminal and posts `decision.closed`.

### 3. Develop the plugin without RimWorld

`tools/fake-bridge.mjs` stands in for the game. It implements the same protocol, writes the same
`bridge.json`, pretty-prints every event it receives (diffs in color), and takes commands on stdin:

```sh
node tools/fake-bridge.mjs
```

| Command | Effect |
|---|---|
| `goal <text>` | queue `goal.submit` for the plugin |
| `say @<agent\|all> <text>` | queue `user.message` |
| `interrupt <agentId>` | queue `agent.interrupt` |
| `allow [decisionId]` | answer a permission with `Allow once` (id optional if only one is open; prefixes work) |
| `deny [decisionId] [reason]` | answer with `Deny` and an optional reason |
| `answer <decisionId> <n\|option> [-- text]` | answer with any option |
| `state` / `decisions` / `goals` / `inbox` | print the agents table and other state |

Options: `--port N` (default: first free of 7878 to 7887; `0` = random), `--token HEX`, `--home DIR`,
`--quiet`. It refuses to overwrite a `bridge.json` that belongs to a running game unless you pass
`--force`. Set `COLONY_HOME=/some/dir` to keep the fake bridge, the plugin and replay away from the
real `~/.colony-code`.

Replay works against the fake bridge too, which is handy for checking a scenario:

```sh
node tools/fake-bridge.mjs        # terminal 1
node tools/replay.mjs --speed 4   # terminal 2; type "allow" in terminal 1 when asked
```

### 4. Load the plugin in Claude Code

```sh
claude --plugin-dir plugin
```

Start the game (or the fake bridge) first, or at any time later: the plugin connects when
`bridge.json` appears.

### Tests

```sh
node --test tools/test      # fake bridge + replay, about 7 s
tests/bridge/run.sh         # C# bridge tests for the mod
python3 tests/patches/check_patches.py   # mod/Patches XPaths match the game's Defs (needs lxml)
```

## Repo layout

```
mod/       RimWorld mod (About/, Source/ColonyCode/, 1.6/Assemblies/, build.sh)
plugin/    Claude Code plugin (.claude-plugin/plugin.json, hooks/)
tools/     fake-bridge.mjs, replay.mjs, scenarios/, test/
scripts/   link-mod.sh
tests/     C# bridge tests, patch sanity check
```
