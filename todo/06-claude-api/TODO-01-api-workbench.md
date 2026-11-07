---
schema_version: 1
id: api-workbench
domain: 06-claude-api
status: active
title: "TODO-01 -- API workbench"
---

# TODO-01 -- API workbench

> **Goal:** The operator's Anthropic and OpenAI keys do extra development from the panel: single requests and overflow agent runs in worktrees, within hard caps, with every cent in a local ledger. Anyone can configure keys inside Bosun, with no 1Password needed.

> **Current state:** Measured 2026-11-07. `ANTHROPIC_API_KEY` is unset on this machine, by design. Empty placeholders `Bosun Anthropic API key` and `Bosun OpenAI API key` are in the 1Password Conclave vault for the owner to fill. No code exists.

## Inputs

- `design/SPEC.md` §13, §14, §15.
- `docs/specs/2026-11-07-control-panel-design.md`, the API key section.
- The official `Anthropic` and `OpenAI` NuGet packages; model IDs and prices rechecked against each provider's live pages when built.
- -> XREF: D02 T02 §5 -- consumed by this file
- -> XREF: D00 T01 §8 -- consumed by this file
- -> XREF: D07 T01 §4 -- consumes this file
- -> XREF: D02 T03 §4 -- consumed by this file
- -> XREF: D02 T03 §5 -- consumed by this file
- -> XREF: D02 T03 §7 -- consumed by this file
- -> XREF: D99 T01 §5 -- consumes this file
- -> XREF: D07 T01 §5 -- consumes this file
- -> XREF: D06 T02 §1 -- consumes this file

## Outcome

- With no key or no caps, nothing is sent to either provider.
- An overflow run on a fake CLI is costed in the ledger and stays in its worktree. Live proof with real keys is `D99 T01 §5`.

**Adjacency:** list=applicable @ D06 T01 §7; document=not-applicable (none); settings=applicable @ D06 T01 §5; reporting=applicable @ D06 T01 §8; notifications=applicable @ D06 T01 §5; permissions=applicable @ D06 T01 §5; audit=applicable @ D06 T01 §4; exchange=applicable @ D06 T01 §8; reverse=applicable @ D06 T01 §7

**Adjacency rationale:** One set of caps in US dollars covers both providers. Caps have no defaults on purpose: the operator's first act is choosing them. The worktree is the reverse of an overflow run, since nothing lands until the operator merges it.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Key store in Windows Credential Manager | D02 T02 §5 | [ ] |
| 2 | §2 | Optional import from 1Password | §1 | [ ] |
| 3 | §3 | Client and model list | §1 | [ ] |
| 4 | §4 | Usage ledger and price table | §3 | [ ] |
| 5 | §5 | Hard spend caps | §4 | [ ] |
| 6 | §6 | Workbench: Ask mode | §5, D02 T02 §9, D02 T03 §5, D02 T03 §7 | [ ] |
| 7 | §7 | Overflow agent runs | §6, D00 T01 §8 | [ ] |
| 8 | §8 | Usage page | §4, D02 T03 §4, D02 T03 §7 | [ ] |
| 9 | §9 | OpenAI client and model list | §3 | [ ] |
| 10 | §10 | OpenAI prices and ledger | §4, §9 | [ ] |
| 11 | §11 | Spend guard across providers | §5, §10 | [ ] |
| 12 | §12 | Workbench Ask on OpenAI | §6, §11 | [ ] |
| 13 | §13 | Overflow agent runs on Codex | §7, §11 | [ ] |

## 1. Key store in Windows Credential Manager

The key must never pass through Git, arguments, logs or the parent environment. Windows Credential Manager is the standard store for a public release: each key is protected by DPAPI for the current user, and people can see and remove it in a Windows screen they already know. The owner chose this on 2026-11-07.

**Phase:** 6
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §15, API keys.
**Job:** store each key once, inside Bosun, and know it's there.
**Treatment:** status word plus Paste and Remove. Cheaper substitute that fails: a visible text box.
**Chrome:** the Settings page.

**Build order:**

1. Create `src/Bosun.Core/Api/KeyStore.cs`. Done when: a key round-trips per provider through Credential Manager.
2. Add Settings controls. Done when: Paste and Remove work with a status word.

- [ ] Store each key with `CredWrite` as a generic credential named `Bosun/anthropic` or `Bosun/openai`, with `CRED_PERSIST_LOCAL_MACHINE` so it never roams, in `src/Bosun.Core/Api/KeyStore.cs`. Done when: a test uses a fake key under a test prefix, round-trips it and removes it. Cheaper substitute: a config file, or an environment variable.
- [ ] Refuse a key over the `CRED_MAX_CREDENTIAL_BLOB_SIZE` limit in `KeyStore.cs`. Done when: an oversized fake is refused with a clear message and no partial write.
- [ ] Show only Stored or Not stored per provider in `design/SPEC.md` §15 controls, using a `PasswordBox` and clearing buffers. Done when: no path can print the value, proven by a scan of log output with a planted key. Cheaper substitute: showing the last four characters.
- [ ] Add Remove with confirmation per provider. Done when: `CredRead` finds nothing and status reads Not stored.
- [ ] Commit: `api: DPAPI key store`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~KeyStore` exits 0, and fails if the change is reverted.

## 2. Optional import from 1Password

Pasting a key in Settings is the route for everyone. Import is a convenience for people who use 1Password, so it only appears when `op` is installed. The run proves it with a fake `op`, because unlocking the vault needs a person, and `D99 T01 §5` proves it live.

**Phase:** 6
**Needs:** none
**Surface:** no

**Build order:**

1. Add Import from 1Password. Done when: `op read` output goes straight to `KeyStore`.
2. Add `scripts/check-api-key.py`. Done when: it prints booleans only.

- [ ] Show Import in Settings only when `op` resolves, in `src/Bosun.Core/Api/KeyImport.cs`. Done when: a test with no `op` shows no button and says nothing of 1Password. Cheaper substitute: a button that fails for everyone else.
- [ ] Pipe `op read` for a chosen item through `ProcessRunner` in memory only, in `KeyImport.cs`. Done when: with a fake `op`, no log, argument or temp file holds the value. Cheaper substitute: `op read` into a variable that's logged, or asking for the key in chat.
- [ ] Let the operator pick the item by title in `KeyImport.cs`, listing names only. Done when: a fake listing shows titles, and values are never requested. Cheaper substitute: printing an item to read one field.
- [ ] Create `scripts/check-api-key.py`, reading the store and printing only booleans per provider. Done when: its output holds `stored` and `models_ok` and no value, proven with a planted fixture key.
- [ ] Commit: `api: optional import from 1Password`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~KeyImport` exits 0, and fails if a fake value reaches a log.

## 3. Client and model list

Use the official SDK. A connection test should spend nothing, so it lists models.

**Phase:** 6
**Needs:** none
**Surface:** no

**Build order:**

1. Add the `Anthropic` package and `ApiClient`. Done when: the client gets its key from `KeyStore` only.
2. List models. Done when: Test connection reports a clear result.

- [ ] Create `design/pricing.json`, keyed by provider, with its as-of date for `claude-opus-5-5`, `claude-sonnet-5-5` and `claude-haiku-5-5`, confirmed against the live pricing page. Done when: input, output and cached rates are present, and a test recomputes a known case. Cheaper substitute: prices copied without a date.
- [ ] Define `IProviderClient` in `src/Bosun.Core/Api/IProviderClient.cs`, covering key, models, streaming, usage and stop reason. Done when: `ApiClient` implements it, and pages use only the interface. Cheaper substitute: Anthropic types in the view model.
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

1. Create `Ledger.cs`. Done when: each row has usage and computed cost.
2. Handle unknown prices. Done when: a row reads Unpriced and counts at the highest known rate.

- [ ] Append to `%LOCALAPPDATA%\Bosun\api\ledger-YYYY-MM.jsonl` with `request_id`, model, input, cached, cache write, output and cost. Done when: a crash mid-write loses at most the partial last line, which the reader tolerates and reports. Cheaper substitute: keeping spend only in memory.
- [ ] Treat an unknown model as Unpriced, at the highest known rate. Done when: a test proves the cap still counts it. Cheaper substitute: cost 0 for an unknown model.
- [ ] Add a `provider` field to each row in `%LOCALAPPDATA%\Bosun\api\ledger-YYYY-MM.jsonl`. Done when: a test totals by provider.
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
- [ ] Build the Budget card with today and month against the caps, in `Views/WorkbenchView.xaml`. Done when: bars and words match a fixture ledger.
- [ ] Build the form with Mode, Provider, Repo, Model, Effort, Task and Budget, in `Views/WorkbenchView.xaml`. Done when: Model lists follow Provider, and Budget is required for an overflow agent. Cheaper substitute: free text for the model.
- [ ] Add Test connection to Settings, listing models for one provider. Done when: a fake handler shows success and each failure message, and spends nothing.
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

- [ ] Create the worktree under `%LOCALAPPDATA%\Bosun\overflow\<run>` with `git worktree add`, on its own branch. Done when: the repo's working tree is untouched, and a repo with a live run is refused the same worktree. Cheaper substitute: running in the main checkout.
- [ ] Start `claude -p --output-format stream-json --max-budget-usd <budget>`, refusing to start if the installed `claude --help` lacks that flag, with the key only in that child's environment block. Done when: a test reads the child's environment and finds it there, and not in the panel's or in arguments. Cheaper substitute: setting `ANTHROPIC_API_KEY` for the user, or starting a run with no budget flag.
- [ ] Reserve the whole run budget through `SpendGuard` before launch, releasing the unspent part at the end from the reported cost. Done when: a run whose budget would pass a cap never starts, and two parallel runs can't share the same headroom. Cheaper substitute: checking spend only after the run has cost money.
- [ ] Watch reported cost as a backstop, stopping the process tree if it passes the budget and recording the overage as a warning. Done when: a fake CLI that overspends is stopped, its cost is recorded, and Activity names the overage. Cheaper substitute: treating the backstop as the cap.
- [ ] List runs with state, cost and worktree, with Open worktree and Open log. Done when: after a run, the worktree remains for the operator, with no automatic merge. Cheaper substitute: auto-merging results.
- [ ] Refuse to route reviews through this path. Done when: a task naming `review-dispatch.py` as its reviewer never uses Claude, and the dispatcher's rule stays the guard.
- [ ] Add a Stop action to each active run, stopping its process tree. Done when: a fake CLI stops within a second, and its cost so far is recorded.
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
- [ ] Show the dated price table, and mark a row Unpriced on the page, in `Views/UsageView.xaml`. Done when: a fixture with an unknown model reads Unpriced and counts at the highest known rate.
- [ ] Add Provider totals and filter to `Views/UsageView.xaml`. Done when: totals by provider equal a fixture ledger.
- [ ] Commit: `api: usage page`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~UsagePage` exits 0, and fails if the change is reverted.

## 9. OpenAI client and model list

The owner added OpenAI to the Workbench on 2026-11-07. It sits behind the same interface as Anthropic, so pages and guards don't change. The official package is used, and a connection test spends nothing.

**Phase:** 6
**Needs:** none
**Surface:** no

**Build order:**

1. Add the `OpenAI` package and `OpenAiClient`. Done when: it gets its key from `KeyStore` only.
2. List models. Done when: Test connection reports a clear result.

- [ ] Implement `IProviderClient` in `src/Bosun.Core/Api/OpenAiClient.cs` with the stored key and `OPENAI_API_KEY` ignored. Done when: a fake handler proves the header comes from the store. Cheaper substitute: reading the environment.
- [ ] List models through the OpenAI models endpoint in `OpenAiClient.cs`, falling back to the priced list with a notice. Done when: offline shows the fallback and says so.
- [ ] Map OpenAI errors to clear messages in `OpenAiClient.cs`, most specific first. Done when: 401, 429, an exhausted quota and 5xx each give their own message.
- [ ] Commit: `api: OpenAI client and model list`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~OpenAiClient` exits 0, and fails if the environment key is read.

## 10. OpenAI prices and ledger

Cost comes from the same local ledger, and an unknown price must not read as free.

**Phase:** 6
**Needs:** none
**Surface:** no

**Build order:**

1. Extend `design/pricing.json`. Done when: OpenAI models carry dated rates.
2. Record usage from OpenAI responses. Done when: ledger rows match a fixture.

- [ ] Add the OpenAI models offered in the Workbench to `design/pricing.json`, confirmed against the live pricing page. Done when: input, cached and output rates are present, and a test recomputes a known case. Cheaper substitute: prices from memory.
- [ ] Map OpenAI usage, including cached and reasoning tokens, in `src/Bosun.Core/Api/Ledger.cs`. Done when: a fixture response gives the expected row, with `provider` set.
- [ ] Treat an unknown OpenAI model as Unpriced, at the highest known rate across providers. Done when: a test proves the cap still counts it. Cheaper substitute: cost 0.
- [ ] Commit: `api: OpenAI prices and ledger`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~Ledger` exits 0, and fails if an unknown OpenAI model costs 0.

## 11. Spend guard across providers

Both providers draw on one set of caps, so spending on one can't hide from the other.

**Phase:** 6
**Needs:** none
**Surface:** no

**Build order:**

1. Make `SpendGuard` provider-aware. Done when: both clients need its token.
2. Add estimates for OpenAI. Done when: a reservation exists before each send.

- [ ] Require a `SpendGuard` token in the `OpenAiClient` constructor. Done when: a test proves it can't send without one. Cheaper substitute: a check only in the view model.
- [ ] Estimate an OpenAI reservation in `src/Bosun.Core/Api/SpendGuard.cs` from a conservative token count and the request's output limit. Done when: two parallel requests on different providers can't both pass on the same headroom, in a test.
- [ ] Commit: `api: spend guard across providers`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~SpendGuard` exits 0, and fails if `OpenAiClient` can send without a token.

## 12. Workbench Ask on OpenAI

Same page, same budget, with honest handling of that provider's stop and refusal states.

**Phase:** 6
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §13.
**Job:** ask a question on the OpenAI key, knowing what it costs.
**Treatment:** the Provider choice in the same form. Cheaper substitute that fails: a second page.
**Chrome:** the Workbench page.

**Build order:**

1. Stream through `OpenAiClient`. Done when: text arrives as it streams, and Stop cancels.
2. Handle stop states. Done when: refusal, length and content filter each draw properly.

- [ ] Stream through the official SDK in `src/Bosun.Core/Api/OpenAiClient.cs`. Done when: a fake stream shows text as it arrives, and Stop cancels the request. Cheaper substitute: buffering until the end.
- [ ] Show the refusal text, and the length and content filter stop reasons, in `Views/WorkbenchView.xaml`. Done when: a fixture for each shows its message. Cheaper substitute: treating a refusal as an empty answer.
- [ ] Disable Start per provider with a clear line when that provider has no key, or no caps exist. Done when: the line links to Settings.
- [ ] Commit: `api: workbench Ask on OpenAI`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~WorkbenchAsk` exits 0, and fails if a refusal shows as empty.

## 13. Overflow agent runs on Codex

Overflow on the OpenAI key runs Codex CLI in the same kind of worktree. Codex has no flag equal to `--max-budget-usd` that this plan can rely on, so a small local gateway holds the budget: it reserves each request's maximum cost before forwarding it and refuses one that would pass the budget. As a bonus, the child never holds the key.

**Phase:** 6
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §13.
**Job:** get more done on the OpenAI key, within a budget.
**Treatment:** the Provider choice on the overflow form. Cheaper substitute that fails: a run with no budget.
**Chrome:** the Workbench page.

**Build order:**

1. Add a provider to `OverflowRunner`. Done when: an OpenAI run starts `codex exec` in a new worktree.
2. Enforce the budget. Done when: a request that would pass it never reaches the provider.

- [ ] Create `src/Bosun.Core/Api/BudgetGateway.cs`, a loopback-only forwarder running for one overflow run, holding the key and passing streams through. Done when: a test with a fake provider proves requests and streamed responses arrive intact, and the port takes only the run's random token. Cheaper substitute: watching reported usage alone.
- [ ] Reserve each request's maximum cost in `BudgetGateway.cs` before forwarding, settling from the response's usage. Done when: a test proves a request that would pass the run's budget is refused before reaching the fake provider, with a clear error to the CLI.
- [ ] Start `codex exec --json` in the worktree from `src/Bosun.Core/Api/OverflowRunner.cs`, pointing it at the gateway through its provider configuration. Done when: a test finds the key in neither the child's environment nor its arguments. Cheaper substitute: giving the child the key.
- [ ] Reserve the whole run budget through `SpendGuard` before launch, settling from reported usage. Done when: a run whose budget would pass a cap never starts.
- [ ] Stop the process tree in `OverflowRunner.cs` when the gateway refuses for budget. Done when: a fake CLI is stopped, and Activity names the budget reached.
- [ ] Treat a response with no usage as costing its full reservation in `BudgetGateway.cs`. Done when: a test proves the budget still counts it, and the run carries on while room remains. Cheaper substitute: cost 0.
- [ ] Refuse to route reviews through this path. Done when: a test proves a task naming `review-dispatch.py` as its reviewer never runs here, and the dispatcher's rule stays the guard.
- [ ] Commit: `api: overflow agent runs on Codex`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~OverflowRunner` exits 0, and fails if an over-budget request reaches the fake provider.

## Verification

- `dotnet test Bosun.slnx` passes.
- Live keys are proven in `D99 T01 §5`, not in the unattended run.
