---
schema_version: 1
id: agents-page
domain: 05-machine
status: active
title: "TODO-01 -- Agents"
---

# TODO-01 -- Agents

> **Goal:** Every agent on both sides shows installed and latest versions, and can be updated safely from the panel.

> **Current state:** Measured 2026-11-07. On 2026-11-07: `claude` 2.1.319 and `codex-cli` 0.167.0 on Windows, `agy` and `gh` also present. WSL versions are unmeasured. No update tooling exists.

## Inputs

- `design/SPEC.md` §11.
- Each agent's own update command, verified when built.
- -> XREF: D02 T02 §3 -- consumed by this file
- -> XREF: D03 T02 §1 -- consumed by this file
- -> XREF: D03 T01 §5 -- consumed by this file
- -> XREF: D02 T03 §4 -- consumed by this file
- -> XREF: D02 T03 §7 -- consumed by this file
- -> XREF: D07 T01 §4 -- consumes this file

## Outcome

- Installed and latest are shown per side, with a real reason when latest can't be found.
- An update runs in the queue and reads its version back.

**Adjacency:** list=applicable @ D05 T01 §2; document=not-applicable (none); settings=applicable @ D05 T01 §3; reporting=not-applicable (none); notifications=applicable @ D05 T01 §3; permissions=applicable @ D05 T01 §2; audit=applicable @ D05 T01 §2; exchange=not-applicable (none); reverse=not-applicable (agents do not support rollback here; the prior version is recorded)

**Adjacency rationale:** Reverse is limited: the prior version is recorded so the operator can reinstall it by hand.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Agent catalog | D02 T02 §3 | [ ] |
| 2 | §2 | Agents page and updates | §1, D03 T02 §1, D02 T02 §9, D02 T03 §4, D02 T03 §7 | [ ] |
| 3 | §3 | Scheduled update check | §2, D03 T01 §5 | [ ] |
| 4 | §4 | Configuration checks | §2 | [ ] |

## 1. Agent catalog

Each agent installs differently, so each has a small definition that was checked against its real tool, not assumed.

**Phase:** 5
**Needs:** none
**Surface:** no

**Build order:**

1. Create `design/agents.json` and `src/Bosun.Core/Agents/AgentCatalog.cs`. Done when: each agent has version, latest and update commands.
2. Run on both sides. Done when: WSL reports its own installs.

- [ ] Verify each CLI's real version and update commands on this machine, recording them in `design/agents.json` for `claude`, `codex`, `agy` and `gh`. Done when: each command is shown working in a fixture captured from its real output. Cheaper substitute: guessing update commands from memory.
- [ ] Get latest from each agent's published source, with a bounded timeout. Done when: offline reads as Not available with a reason. Cheaper substitute: showing the installed version as latest.
- [ ] Compare with a real version parser. Done when: `2.1.9` sorts below `2.1.319`, in a test. Cheaper substitute: string comparison.
- [ ] Commit: `agents: catalog`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~AgentCatalog` exits 0, and fails if the change is reverted.

## 2. Agents page and updates

Updating an agent under a running session can break that session, so it warns first and takes its turn in the queue.

**Phase:** 5
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §11.
**Job:** keep the agents current without breaking running work.
**Treatment:** a table with row actions. Cheaper substitute that fails: a list of commands to run by hand.
**Chrome:** the shell's page frame and `Btn` styles.

**Build order:**

1. Create `Views/AgentsView.xaml`. Done when: the table shows both sides.
2. Add Update. Done when: it runs through `bosun run` and reads back.

- [ ] Build the table per `design/SPEC.md` §11, with running count from the instance scanner. Done when: a missing agent on one side reads Not installed.
- [ ] Add Update with a warning naming running sessions. Done when: the confirm lists them, Activity logs before and after versions, and failure reports the real exit. Cheaper substitute: updating with no warning or read-back.
- [ ] Stop an update during an active unattended run unless the operator overrides. Done when: a test with an active guard state refuses by default.
- [ ] Add Check now to `Views/AgentsView.xaml`. Done when: a press runs one check, and overlap is refused.
- [ ] Commit: `agents: page and updates`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~AgentsViewModel` exits 0, and fails if the change is reverted.

## 3. Scheduled update check

Knowing an update exists shouldn't require opening a page.

**Phase:** 5
**Needs:** none
**Surface:** no

**Build order:**

1. Create `AgentUpdateWatch`. Done when: a check runs every 12 hours while in the tray.
2. Add settings and Needs you. Done when: a type can be turned off.

- [ ] Check at the setting's interval and notify once per new version. Done when: a fake-clock test notifies once for each version. Cheaper substitute: a toast on every check.
- [ ] Add `AgentUpdateWatch` as consumer of the update check setting. Done when: turning it off stops the checks in a test.
- [ ] Add to Needs you. Done when: an available update appears on Overview.
- [ ] Commit: `agents: update notices`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~AgentUpdateWatch` exits 0, and fails if the change is reverted.

## 4. Configuration checks

Some settings have real rules (empty attribution; no key in a session's environment). This tests them while printing no values.

**Phase:** 5
**Needs:** none
**Surface:** no

**Build order:**

1. Create `ConfigChecks`. Done when: each check returns a boolean and reason.
2. Show them. Done when: failures link to the file.

- [ ] Check that `attribution.commit` and `attribution.pr` are empty in `~/.claude/settings.json`, that pins resolve, and that a session's environment has no API key. Done when: each is covered by fixtures, and output holds booleans and key names only. Cheaper substitute: displaying a settings value or token.
- [ ] Add a test scanning check output for secret-shaped strings. Done when: a planted fixture value never appears in the result.
- [ ] Build the configuration checks table in `Views/AgentsView.xaml`. Done when: a test proves each row shows only a boolean word and a planted value never appears.
- [ ] Commit: `agents: configuration checks`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~ConfigChecks` exits 0, and fails if the change is reverted.

## Verification

- `dotnet test Bosun.slnx` passes.
- An attended update of one agent reads back its new version.
