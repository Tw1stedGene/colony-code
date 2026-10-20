# colony-code (Claude Code plugin)

Shows your Claude Code session and its subagents in RimWorld, embodied by the remote workers who live
at your agent desks.
The plugin reports to the **Colony Code** RimWorld mod, which runs a small HTTP bridge on
`127.0.0.1:7878`. It also takes goals, messages, interrupts and permission answers back from the game.

You sign in to Claude Code yourself. Neither the plugin nor the mod ever touches your credentials.

## Requirements

- Claude Code **2.1.302 or newer**. The plugin uses the early-access function-hooks API.
- The Colony Code mod enabled in RimWorld 1.6 (1.7 planned). When the game is not running, the
  plugin does nothing and the status line shows `○ RimWorld not running`.

## Install

The repository is its own plugin marketplace (`.claude-plugin/marketplace.json` at its root lists
this folder). At the prompt of a Claude Code terminal session:

```
/plugin install colony-code --marketplace Tw1stedGene/colony-code
```

Answer `y` to add the marketplace, then pick a scope (user scope loads it in every session). From a
shell:

```sh
claude plugin install colony-code --marketplace Tw1stedGene/colony-code
```

In the desktop app, where `/plugin` does not run: click + next to the prompt box, then Plugins, Add
marketplace with `Tw1stedGene/colony-code`, then Add plugin.

`Tw1stedGene/colony-code` is the GitHub repository that holds this project. A private repository
installs only for people who can read it and whose machine already has a git credential for
github.com. The RimWorld mod itself is installed separately, in the game's `Mods/` folder.

### Known limits (Claude Code 2.1.302)

- **Permission letters on organization-managed Claude Code.** When a machine has managed settings
  (a local `managed-settings.json`, or settings your organization pushes to your account), Claude
  Code seats its built-in security plugin `cc-plugin-sec-default` outermost, and it passes
  permission requests on past every plugin a person installs. Every install path counts as such a
  plugin: `--plugin-dir`, user, project and marketplace installs alike. The debug log says
  `cc-plugin-sec-default@builtin seated outermost: this machine has managed settings`, then
  `colony-code: its classic.PermissionRequest hook was skipped`; no permission letter appears and
  you answer in the terminal as usual. Only an administrator can change that, by listing the plugin
  among the organization's managed plugins. On a personal install without managed settings the
  security plugin is not seated outermost and permission letters are expected to work (not yet
  verified on such an account). Questions (`AskUserQuestion`) go through `tool.call` and are not
  affected.
- **Headless runs only report.** A `claude -p` or SDK session posts events but never takes inbox
  items (goals, messages, tasks, merges), so a scripted run is never steered from the game. `-p`
  denies anything that would prompt before any hook runs.
- **The task board works without task tools.** Each prompt becomes a goal card (`goal-<n>`), each
  subagent a card under its goal (`agent-<id>`), each task from the game a card at once
  (`game-<id>`); Claude Code's own TaskCreate/TodoWrite tasks are added when a build has them.
  Messages from the game get no card.
- `COLONY_HOME` overrides `~/.colony-code` as the folder the plugin reads `bridge.json` from
  (`tools/fake-bridge.mjs` honours it too), so tests can stay away from a running game.

### Install (development)

```sh
claude --plugin-dir /path/to/colony-code/plugin
```

`CLAUDE_CODE_PLUGIN_DIRS=/path/to/colony-code/plugin` does the same for sessions that cannot
take a flag, such as one the desktop app starts. Or add your working copy as a marketplace
(`claude plugin marketplace add /path/to/colony-code`) and install from it: `/reload-plugins`
then re-reads the folder.

## What it does

The plugin talks to the game over the Colony Code bridge protocol (v0, with the v0.2 additions).

| Claude Code | Sent to the game |
|---|---|
| session start | `session.start` (with `label`, the repo folder name) and `agent.upsert` for the lead (`lead-<first 8 chars of session id>`), then `worktree.list` |
| each tool call | before it runs: `agent.upsert` with state, activity and station. After it runs: `agent.log` with a one-line summary, plus a diff for Edit/Write/MultiEdit, the output head for Bash, or the error |
| subagent spawned | `agent.upsert` for a worker (id = the subagent's agentId, label = `<type>: <description>`) |
| turn finished | `done` (or `error`), then `idle` in the lounge after 10 s |
| permission prompt | `decision.open` with `["Allow once", "Deny"]`, or `["Allow once", "Always allow", "Deny"]` when Claude Code suggested permission rules; the context then ends with `Always allow covers: <the rules, in words>`. "Always allow" answers with exactly those suggested rules (`updatedPermissions`). The plugin waits up to 5 minutes for your answer in the game. If the game does not answer or cannot be reached, the normal terminal prompt appears, and `decision.closed` is sent once you answer it there |
| `AskUserQuestion` | one `decision.open` (`kind: "question"`, id `ask-<tool_use_id>-<i>`, `multiSelect`, `allowText: true`) per question. Once every question is answered in the game, the plugin hands Claude the tool result itself and no terminal dialog appears. On a 5-minute timeout or when the game is unreachable it closes the decisions and the normal dialog appears |
| turn finished with usage | `usage.update`: cumulative input (uncached + cache writes), output and cache-read tokens of the session's turns (subagents included) and the number of main turns. Never money |
| turn finished (main) | a fresh `worktree.list` |
| session end | `session.end` |

| From the game inbox | In Claude Code |
|---|---|
| `goal.submit` | Submitted as a prompt. It runs once the session is idle and is reported back with `goal.upsert` |
| `user.message` | Submitted as `[RimWorld message for <to>] <text>` |
| `agent.interrupt` (the lead) | Aborts the running turn |
| `task.create` | Submitted as `[RimWorld task] Work on this when appropriate (add it to your task list if you have one): <text>` |
| `diff.request` | `diff.result`: the checkout's uncommitted changes vs `HEAD`, or a worktree's changes since it left the base branch (committed and uncommitted), as files, hunks and numbered lines (at most 200 files and 5000 lines) |
| `merge.request` `merge` | `git merge --no-ff --no-edit <branch>` in your checkout, as you (your git config). Refused while the checkout has uncommitted changes; a conflict is aborted and the files are listed |
| `merge.request` `request_changes` | Submitted as `[RimWorld review] Changes requested on <branch>: <text>` |
| `merge.request` `reject` | `git worktree remove --force <path>` and `git branch -D <branch>` |

Every review action answers with `merge.result` and a fresh `worktree.list`. Git runs through
`$.process.run` by argument vector (never a shell), one job at a time, in the session's checkout.
The plugin never pushes, fetches or touches a remote, and the game only names worktrees by their
folder name: branches and paths always come from `git worktree list`.

The plugin finds the game through `~/.colony-code/bridge.json`, which the mod writes at startup.
Before it sends the token anywhere, it asks the server to prove it holds it (an HMAC of a random
challenge), so a stale file left by a crashed game cannot
hand the token, your prompts or your permission answers to another process on that port. Then it
sends the token in `X-Colony-Token` on every request, and polls the inbox with `?session=<id>` so
items meant for another Claude Code session stay queued for it. Events are buffered and
posted every 250 ms, or right away once more than 20 are waiting. A post never delays a tool
call. All network errors are swallowed, so the plugin cannot block Claude Code.

The status line shows `RimWorld ● connected` or `○ RimWorld not running`.

## Develop

```sh
claude plugin validate plugin          # manifest + hooks module
claude plugin test plugin              # tests/*.test.ts against the engine, fake game behind $.http.fetch
npx -y -p typescript@5.6 tsc -p plugin-tsconfig/tsconfig.json   # type-check (tsconfig kept outside the plugin)
node tools/fake-bridge.mjs             # a fake game to try it end to end (type help there)
node --test tools/test                 # fake bridge, replay, and review.ts against a real git repo
```

`plugin-tsconfig/tsconfig.json` points at the `claude-code.d.ts` that Claude Code writes for its
build. When Claude Code updates, change that path to the new file.
