---
schema_version: 1
id: api-workbench
domain: 06-claude-api
status: active
title: "TODO-01 -- Claude API workbench"
---

# TODO-01 -- Claude API workbench

> **Goal:** The operator's API key does extra development from the panel: single requests and overflow Claude Code runs in worktrees, within hard caps, with every cent in a local ledger.

> **Current state:** Measured 2026-11-07. `ANTHROPIC_API_KEY` is unset on this machine, by design. The operator reports having a key; the 1Password Conclave vault is the recovery source. No code exists.

## Inputs

- `design/SPEC.md` §13, §14, §15.
- `docs/specs/2026-11-07-control-panel-design.md`, the API key section.
- The official `Anthropic` NuGet package; model IDs and prices as of 2026-10-06 per the Claude API reference, rechecked when built.
- -> XREF: D02 T02 §5 -- consumed by this file
- -> XREF: D00 T01 §8 -- consumed by this file
- -> XREF: D07 T01 §4 -- consumes this file
- -> XREF: D02 T03 §4 -- consumed by this file
- -> XREF: D02 T03 §5 -- consumed by this file
- -> XREF: D02 T03 §7 -- consumed by this file

## Outcome

- With no key or no caps, nothing is sent.
- A real overflow run is costed in the ledger and stays in its worktree.

**Adjacency:** list=applicable; document=not-applicable (none); settings=applicable @ D06 T01 §5; reporting=applicable @ D06 T01 §8; notifications=applicable @ D06 T01 §5; permissions=applicable @ D06 T01 §5; audit=applicable @ D06 T01 §4; exchange=applicable @ D06 T01 §8; reverse=applicable @ D06 T01 §7

**Adjacency rationale:** Caps have no defaults on purpose: the operator's first act is choosing them. The worktree is the reverse of an overflow run, since nothing lands until the operator merges it.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | DPAPI key store | D02 T02 §5 | [ ] |
| 2 | §2 | Import the key from 1Password | §1 | [ ] |
| 3 | §3 | Client and model list | §1 | [ ] |
| 4 | §4 | Usage ledger and price table | §3 | [ ] |
| 5 | §5 | Hard spend caps | §4 | [ ] |
| 6 | §6 | Workbench: Ask mode | §5, D02 T02 §9, D02 T03 §5, D02 T03 §7 | [ ] |
| 7 | §7 | Overflow agent runs | §6, D00 T01 §8 | [ ] |
| 8 | §8 | Usage page | §4, D02 T03 §4, D02 T03 §7 | [ ] |

## 1. DPAPI key store

The key must never pass through Git, arguments, logs or the parent environment. This follows the same DPAPI CurrentUser pattern as the Conclave tunnel credential.

**Phase:** 6
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §15, Claude API.
**Job:** store the key once and know it's there.
**Treatment:** status word plus Paste and Remove. Cheaper substitute that fails: a visible text box.
**Chrome:** the Settings page.

**Build order:**

1. Create `src/Bosun.Core/Api/KeyStore.cs`. Done when: a key round-trips and is unreadable as another scope.
2. Add Settings controls. Done when: Paste and Remove work with a status word.

- [ ] Store with `ProtectedData` at `%LOCALAPPDATA%\Bosun\secrets\anthropic.dpapi`, citing `R:\Codex\conclave\scripts\Manage-Cloud.ps1:46`. Done when: a test uses a fake key, and the file holds no plaintext. Cheaper substitute: a config file, or an environment variable.
- [ ] Show only Stored or Not stored in `design/SPEC.md` §15 controls, using a `PasswordBox` and clearing buffers. Done when: no path can print the value, proven by a scan of log output with a planted key. Cheaper substitute: showing the last four characters.
- [ ] Add Remove with confirmation. Done when: the file is gone and status reads Not stored.
- [ ] Commit: `api: DPAPI key store`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~KeyStore` exits 0, and fails if the change is reverted.

## 2. Import the key from 1Password

Importing once is the approved route, but unlocking the vault needs the operator present, so this stays apart from everything that only needs a key stored.

**Phase:** 6
**Needs:** attended
**Surface:** no

**Build order:**

1. Add Import from 1Password. Done when: `op read` output goes straight to `KeyStore`.
2. Run it once, attended. Done when: status reads Stored, and a harmless Models API call succeeds.

- [ ] Pipe `op read` for the Conclave vault item through `ProcessRunner` in memory only. Done when: no log, argument or temp file holds the value. Cheaper substitute: `op read` into a variable that's logged, or asking for the key in chat.
- [ ] Find the item in the Conclave vault with names only, recording the item name in `docs/`. Done when: the listing prints no values. Cheaper substitute: printing an item to read one field.
- [ ] Create `scripts/check-api-key.py`, reading the store and printing only booleans. Done when: its output holds `stored` and `models_ok` and no value, proven with a planted fixture key.
- [ ] Run the import, then list models. Done when: the call lists models, which spends no tokens.
- [ ] Commit: `api: import key from 1Password`

**Test checkpoint:** `python scripts/check-api-key.py` prints only `stored: true` and `models_ok: true`.

## 3. Client and model list

Use the official SDK. A connection test should spend nothing, so it lists models.

**Phase:** 6
**Needs:** none
**Surface:** no

**Build order:**

1. Add the `Anthropic` package and `ApiClient`. Done when: the client gets its key from `KeyStore` only.
2. List models. Done when: Test connection reports a clear result.

- [ ] Construct `AnthropicClient` in `src/Bosun.Core/Api/ApiClient.cs` with the stored key and `ANTHROPIC_API_KEY` ignored. Done when: a test with a fake handler proves the header comes from the store. Cheaper substitute: raw `HttpClient` calls, or reading the environment.
- [ ] List models through the Models API, falling back to the priced list with a notice. Done when: offline shows the fallback and says so.
- [ ] Map typed SDK exceptions to clear messages, most specific first. Done when: 401, 429 and 5xx each give their own message, and retryable is distinguished. Cheaper substitute: one catch-all message.
- [ ] Commit: `api: client and model list`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~ApiClient` exits 0, and fails if the change is reverted.

## 4. Usage ledger and price table

Cost is computed locally from each response's usage, because Anthropic's usage reports need a separate Admin key. An unknown price must not read as free.

**Phase:** 6
**Needs:** none
**Surface:** no

**Build order:**

1. Create `design/pricing.json` and `Ledger.cs`. Done when: each row has usage and computed cost.
2. Handle unknown prices. Done when: a row reads Unpriced and counts at the highest known rate.

- [ ] Create `design/pricing.json` with its as-of date for `claude-opus-5-5`, `claude-sonnet-5-5` and `claude-haiku-5-5`, confirmed against the live pricing page. Done when: input, output and cached rates are present, and a test recomputes a known case. Cheaper substitute: prices copied without a date.
- [ ] Append to `%LOCALAPPDATA%\Bosun\api\ledger-YYYY-MM.jsonl` with `request_id`, model, input, cached, cache write, output and cost. Done when: a crash mid-write loses at most the partial last line, which the reader tolerates and reports. Cheaper substitute: keeping spend only in memory.
- [ ] Treat an unknown model as Unpriced, at the highest known rate. Done when: a test proves the cap still counts it. Cheaper substitute: cost 0 for an unknown model.
- [ ] Commit: `api: usage ledger and price table`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~Ledger` exits 0, and fails if the change is reverted.

## 5. Hard spend caps

No defaults: until the operator sets both, the panel sends nothing. The check is before the send, and it reserves an estimate so that parallel requests can't overshoot together.

**Phase:** 6
**Needs:** none
**Surface:** no

**Build order:**

1. Create `SpendGuard`. Done when: every path to the API goes through it.
2. Add the cap settings and notices. Done when: 80% warns and 100% refuses.

- [ ] Refuse before sending when a cap is unset or the estimate would pass it, in `src/Bosun.Core/Api/SpendGuard.cs`. Done when: `ApiClient` can't send without a token from `SpendGuard`, enforced by its constructor. Cheaper substitute: a warning after the money is spent.
- [ ] Reserve an estimate from `count_tokens` and `max_tokens`, settled from the ledger. Done when: two parallel requests can't both pass on the same headroom, in a test. Cheaper substitute: checking only the spend so far.
- [ ] Add daily and monthly caps in Settings, with `SpendGuard` as consumer, and a toast at 80%. Done when: both read back, and values must be positive.
- [ ] Commit: `api: hard spend caps`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~SpendGuard` exits 0, and fails if the change is reverted.

## 6. Workbench: Ask mode

Single requests for smaller questions, with streaming and honest refusal handling. Tools aren't offered here.

**Phase:** 6
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §13.
**Job:** ask Claude something on the API key, knowing what it costs.
**Treatment:** budget card, form, streamed answer. Cheaper substitute that fails: a chat clone with no budget.
**Chrome:** the shell's page frame.

**Build order:**

1. Create `Views/WorkbenchView.xaml`. Done when: Ask streams text.
2. Handle stop reasons. Done when: refusal, cap and cancel each draw properly.

- [ ] Stream through the SDK with `claude-opus-5-5` by default, and `thinking` adaptive with effort selectable. Done when: text arrives as it streams, and Stop cancels the request. Cheaper substitute: buffering until the end.
- [ ] Check `stop_reason` before reading content, showing the refusal category and message, with `fallbacks` enabled per the API reference. Done when: a fixture refusal shows category and text. Cheaper substitute: treating a refusal as an empty answer.
- [ ] Mark stable context for prompt caching and record `cache_read_input_tokens`. Done when: the ledger shows cached tokens on a repeat in a fake-handler test.
- [ ] Disable Start with a clear line when no key or caps exist. Done when: the line links to Settings. Cheaper substitute: a Start that fails after clicking.
- [ ] Commit: `api: workbench Ask mode`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~WorkbenchAsk` exits 0, and fails if the change is reverted.

## 7. Overflow agent runs

This is the valuable mode: full Claude Code on the API key when the subscription is out or more parallel work is wanted. The key goes only into that child's environment, and a run writes only in its own worktree.

**Phase:** 6
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §13.
**Job:** get more done when the subscription isn't enough, within a budget.
**Treatment:** a mode in the same form, plus a run list. Cheaper substitute that fails: printing an `export` line.
**Chrome:** the Workbench page.

**Build order:**

1. Create `OverflowRunner`. Done when: a run starts `claude -p` in a new worktree with stream JSON.
2. Enforce the budget before spending. Done when: the run's whole budget is reserved against the caps at start and passed as `--max-budget-usd`.

- [ ] Create the worktree under `R:\scratch\bosun-overflow\<run>` with `git worktree add`, on its own branch. Done when: the repo's working tree is untouched, and a repo with a live run is refused the same worktree. Cheaper substitute: running in the main checkout.
- [ ] Start `claude -p --output-format stream-json --max-budget-usd <budget>`, refusing to start if the installed `claude --help` lacks that flag, with the key only in that child's environment block. Done when: a test reads the child's environment and finds it there, and not in the panel's or in arguments. Cheaper substitute: setting `ANTHROPIC_API_KEY` for the user, or starting a run with no budget flag.
- [ ] Reserve the whole run budget through `SpendGuard` before launch, releasing the unspent part at the end from the reported cost. Done when: a run whose budget would pass a cap never starts, and two parallel runs can't share the same headroom. Cheaper substitute: checking spend only after the run has cost money.
- [ ] Watch reported cost as a backstop, stopping the process tree if it passes the budget and recording the overage as a warning. Done when: a fake CLI that overspends is stopped, its cost is recorded, and Activity names the overage. Cheaper substitute: treating the backstop as the cap.
- [ ] List runs with state, cost and worktree, with Open worktree and Open log. Done when: after a run, the worktree remains for the operator, with no automatic merge. Cheaper substitute: auto-merging results.
- [ ] Refuse to route reviews through this path. Done when: a task naming `review-dispatch.py` as its reviewer never uses Claude, and the dispatcher's rule stays the guard.
- [ ] Commit: `api: overflow agent runs`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~OverflowRunner` exits 0, and fails if the change is reverted.

## 8. Usage page

The ledger, made readable, with an export and an honest warning about an old price table.

**Phase:** 6
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §14.
**Job:** see what the API key has cost and on what.
**Treatment:** a table with totals and export. Cheaper substitute that fails: pointing to the Console.
**Chrome:** the shell's page frame.

**Build order:**

1. Create `Views/UsageView.xaml`. Done when: rows and totals show.
2. Add CSV export and price warnings. Done when: both work.

- [ ] Build the table and totals per `design/SPEC.md` §14. Done when: totals equal a fixture ledger, to the cent.
- [ ] Add Export CSV, with temp-then-rename. Done when: the output reparses, and formula-like cells are neutralised. Cheaper substitute: raw cells starting with `=`.
- [ ] Warn when `design/pricing.json` is over 90 days old. Done when: a fake clock shows the warning.
- [ ] Commit: `api: usage page`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~UsagePage` exits 0, and fails if the change is reverted.

## Verification

- `dotnet test Bosun.slnx` passes.
- An attended check: a small real overflow run is capped, costed and contained.
