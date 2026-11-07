---
schema_version: 1
id: insights
domain: 06-claude-api
status: active
title: "TODO-02 -- Insights"
---

# TODO-02 -- Insights

> **Goal:** Bosun explains what its tools already measure: a failure, a night's runs, memory peaks and review patterns, on a button press, inside the spend caps, showing what it sends and citing what it read. The rest of Bosun works without a key.

> **Current state:** Measured 2026-11-07. Nothing exists. The owner chose these features and the rules below on 2026-11-07. They depend on the key store, ledger and spend guard in `D06 T01`.

## Inputs

- `design/SPEC.md` §20.
- `docs/specs/2026-11-07-control-panel-design.md`, the Insights section.
- -> XREF: D06 T01 §11 -- consumed by this file
- -> XREF: D03 T01 §3 -- consumed by this file
- -> XREF: D03 T01 §5 -- consumed by this file
- -> XREF: D04 T01 §5 -- consumed by this file
- -> XREF: D04 T02 §2 -- consumed by this file
- -> XREF: D04 T03 §4 -- consumed by this file
- -> XREF: D99 T01 §5 -- consumes this file

## Outcome

- No insight sends anything until the operator has seen the redacted text and pressed Send, unless they chose not to be asked for that feature in that repo.
- Every answer cites its evidence, and an action it suggests goes through the existing confirmed path. Nothing here writes to `todo/`, findings, receipts or the machine.

**Adjacency:** list=not-applicable (answers are not stored records); document=applicable @ D06 T02 §3; settings=applicable @ D06 T02 §4; reporting=applicable @ D06 T02 §6; notifications=not-applicable (runs only on request); permissions=applicable @ D06 T02 §2; audit=applicable @ D06 T02 §3; exchange=not-applicable (nothing imported); reverse=not-applicable (read-only)

**Adjacency rationale:** An insight is advice on demand, with its cost in the ledger and a line in Activity. Any change it suggests is made by the owning page, with that page's own confirmation and undo.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Evidence bundles and redaction | D06 T01 §11 | [ ] |
| 2 | §2 | Send preview and consent | §1, D02 T03 §9 | [ ] |
| 3 | §3 | Insight service and cited answers | §2 | [ ] |
| 4 | §4 | Provider and model selection | §3 | [ ] |
| 5 | §5 | Explain this failure | §4, D03 T01 §5, D04 T03 §4 | [ ] |
| 6 | §6 | Overnight briefing | §4, D03 T01 §5, D04 T03 §4 | [ ] |
| 7 | §7 | Memory advisor | §4, D03 T01 §3 | [ ] |
| 8 | §8 | Finding patterns | §4, D04 T02 §2 | [ ] |
| 9 | §9 | Plan drafts | §4, D04 T01 §5 | [ ] |
| 10 | §10 | Second opinion on a stuck section | §4, D04 T02 §2 | [ ] |

## 1. Evidence bundles and redaction

Every feature sends only a bundle built from tool JSON, so a model never gets free run of the machine. Redaction belongs here, once, rather than in each feature.

**Phase:** 6
**Needs:** none
**Surface:** no

**Build order:**

1. Create `src/Bosun.Core/Insights/EvidenceBundle.cs`. Done when: each item has a source, a label and a size.
2. Create `src/Bosun.Core/Insights/Redactor.cs`. Done when: planted secrets never survive.

- [ ] Create `EvidenceBundle` in `src/Bosun.Core/Insights/EvidenceBundle.cs`, with numbered items, each naming its source tool or file. Done when: `tests/Bosun.Core.Tests/EvidenceBundleTests.cs` proves answers can cite an item by number. Cheaper substitute: pasting whole logs.
- [ ] Bound a bundle's size in `EvidenceBundle.cs`, keeping the tail of a log and saying what was cut. Done when: a huge log stays under the limit and the preview names the cut. Cheaper substitute: failing at the provider's limit.
- [ ] Create `Redactor` in `src/Bosun.Core/Insights/Redactor.cs`, removing API keys, tokens, private keys, connection strings, email addresses and the user profile path. Done when: `tests/Bosun.Core.Tests/RedactorTests.cs` covers each with planted fixtures. Cheaper substitute: asking the model to ignore secrets.
- [ ] Run stored key values through `Redactor.cs` as exact matches, reading `KeyStore` without exposing them. Done when: a planted copy of a stored key in a log is removed.
- [ ] Fail closed in `Redactor.cs`: a redaction error sends nothing. Done when: a test with a throwing rule refuses the send and says why.
- [ ] Commit: `insights: evidence bundles and redaction`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~Redactor` exits 0, and fails if a planted key survives.

## 2. Send preview and consent

The operator sees what leaves the machine, which matters more once the repo is public and logs hold client names in paths. Off by default.

**Phase:** 6
**Needs:** display-session
**Surface:** yes
**Fidelity:** `design/SPEC.md` §20, Preview.
**Job:** see exactly what will be sent, and to whom, before it goes.
**Treatment:** a dialog with the redacted text, provider, model, estimated cost and Send. Cheaper substitute that fails: a checkbox accepted once for everything.
**Chrome:** an overlay with `Radius.Overlay` and `Elevation.Overlay`.

**Build order:**

1. Create `src/Bosun.Panel/Views/InsightPreview.xaml`. Done when: it shows the bundle after redaction.
2. Create `src/Bosun.Core/Insights/Consent.cs`. Done when: choices are per feature and per repo.

- [ ] Add a master switch for Insights in `src/Bosun.Core/PanelSettings.cs`, off by default. Done when: with it off, no button shows and a test proves no send path runs. Cheaper substitute: opt-out.
- [ ] Show the redacted bundle, provider, model and estimated cost in `InsightPreview.xaml`, with items the operator can remove. Done when: the text sent equals the text shown, proven by a fake handler. Cheaper substitute: a summary of what will be sent.
- [ ] Add "Don't ask again for this feature in this repo" in `Consent.cs`, saved and revocable in Settings. Done when: a test proves it holds for that pair only and still redacts.
- [ ] Log each send to `ActivityLog.cs`, naming feature, repo, provider and cost, never the content. Done when: a test finds the line with no evidence text in it.
- [ ] Commit: `insights: send preview and consent`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~InsightPreview` exits 0, and fails if text differs from what was shown.

## 3. Insight service and cited answers

One service owns the request, the spend check and the answer's shape, so a feature only supplies its bundle and its question.

**Phase:** 6
**Needs:** none
**Surface:** no

**Build order:**

1. Create `src/Bosun.Core/Insights/InsightService.cs`. Done when: both providers return the same answer shape.
2. Validate citations. Done when: an invented citation is marked.

- [ ] Send through `IProviderClient` with a `SpendGuard` token in `InsightService.cs`, using a structured schema: summary, findings with evidence numbers, confidence and next steps. Done when: fake handlers for both providers give the same typed answer. Cheaper substitute: free text parsed by regex.
- [ ] Mark a claim citing no real evidence item as Unverified in `InsightService.cs`. Done when: a fixture with a bad citation shows that word. Cheaper substitute: trusting the citations.
- [ ] Treat evidence as data in the prompt in `InsightService.cs`, so instructions inside a log are not followed. Done when: a fixture log saying "ignore your instructions" can't change the schema or add an action.
- [ ] Limit next steps to links to existing panel actions in `InsightService.cs`, from a list each feature provides. Done when: a test proves an unknown action becomes plain text with no button. Cheaper substitute: running what the model suggests.
- [ ] Record every request in the ledger with feature name, and offer Copy on the answer. Done when: a test totals by feature, and a refused or capped request costs nothing and says so.
- [ ] Commit: `insights: service and cited answers`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~InsightService` exits 0, and fails if a fixture log can add an action.

## 4. Provider and model selection

The owner asked for this to be selectable. There is a default and a choice at the moment of asking, and the cost shows before anything is sent.

**Phase:** 6
**Needs:** display-session
**Surface:** yes
**Fidelity:** `design/SPEC.md` §20, Selection.
**Job:** choose which provider and model answers, and know the cost.
**Treatment:** a default in Settings, with a per-request override in the preview. Cheaper substitute that fails: a model fixed in code.
**Chrome:** the Settings page, the preview and the generated `ComboBox` style.

**Build order:**

1. Add defaults to `src/Bosun.Core/PanelSettings.cs`. Done when: they read back.
2. Add the override to `src/Bosun.Panel/Views/InsightPreview.xaml`. Done when: changing it updates the estimate.

- [ ] Add a default provider and model to `PanelSettings.cs`, listing only providers with a stored key. Done when: a test with one key offers one provider, and with none the Insights switch says what to do.
- [ ] Add a per-feature override in Settings, with the default used when unset. Done when: a test proves a feature's choice wins and clearing it falls back.
- [ ] Add a provider and model choice to `InsightPreview.xaml`, updating the estimated cost. Done when: a fake price table changes the estimate on selection. Cheaper substitute: cost shown only afterwards.
- [ ] Fall back with a notice when a saved model has gone from the model list. Done when: a test shows the notice and sends nothing until the operator chooses.
- [ ] Commit: `insights: provider and model selection`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~InsightSelection` exits 0, and fails if a removed model is used silently.

## 5. Explain this failure

The most useful feature: a failed thing gets a likely cause, evidence and the next step, on the page where it failed.

**Phase:** 6
**Needs:** display-session
**Surface:** yes
**Fidelity:** `design/SPEC.md` §20, Explain this failure.
**Job:** understand why something failed without reading the whole log.
**Treatment:** an Explain button on a failed row, with the answer in a side panel. Cheaper substitute that fails: pasting the log into a chat window.
**Chrome:** `Btn` with a Lucide glyph, the shell's side panel.

**Build order:**

1. Create `src/Bosun.Core/Insights/FailureEvidence.cs`. Done when: each failure kind builds a bundle.
2. Add the button. Done when: failed rows on the owning pages offer Explain.

- [ ] Build bundles in `FailureEvidence.cs` for a failed page state, a failed queue job, a stalled or waiting run and a parked section. Done when: `tests/Bosun.Core.Tests/FailureEvidenceTests.cs` covers each from fixtures. Cheaper substitute: one log file for everything.
- [ ] Add Explain to `PageFrame.xaml`'s failed state and to failed rows on the Queue and Unattended run pages. Done when: a test proves the button appears only with Insights on and a key stored.
- [ ] Offer next steps only from that page's actions, in `FailureEvidence.cs`. Done when: a stalled run offers the guard's existing actions and nothing else.
- [ ] Commit: `insights: explain this failure`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~FailureEvidence` exits 0, and fails if a parked section builds no bundle.

## 6. Overnight briefing

A few lines on what the runs did and what needs the operator, on request. The numbers come from tools, so the model only writes them up.

**Phase:** 6
**Needs:** display-session
**Surface:** yes
**Fidelity:** `design/SPEC.md` §20, Overnight briefing.
**Job:** know where things stand in a minute.
**Treatment:** a button on Overview, with facts drawn first and the summary beneath. Cheaper substitute that fails: a summary with no numbers behind it.
**Chrome:** a card on the Overview page.

**Build order:**

1. Create `src/Bosun.Core/Insights/BriefingEvidence.cs`. Done when: a period's facts build a bundle.
2. Add the card to `src/Bosun.Panel/Views/OverviewView.xaml`. Done when: it shows facts with no key, and a summary with one.

- [ ] Collect sections stamped, parked and waiting, runs restarted, automations failed and memory lows since the chosen time, in `BriefingEvidence.cs`. Done when: `tests/Bosun.Core.Tests/BriefingEvidenceTests.cs` totals a fixture.
- [ ] Show those facts without any key in `OverviewView.xaml`. Done when: a test with no key shows them and no Summarise button. Cheaper substitute: hiding the facts behind the model.
- [ ] Add Summarise with a since-last-opened default and a chosen period. Done when: a fixture answer shows beneath the facts, and a figure not in the facts is marked Unverified.
- [ ] Commit: `insights: overnight briefing`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~BriefingEvidence` exits 0, and fails if facts need a key.

## 7. Memory advisor

Interprets logged peaks for the 64 GB decision, thresholds and colliding repos. It advises, and any change goes through the Optimise flow.

**Phase:** 6
**Needs:** display-session
**Surface:** yes
**Fidelity:** `design/SPEC.md` §20, Memory advisor.
**Job:** decide about RAM and thresholds from evidence.
**Treatment:** an Advise button on the Decision card. Cheaper substitute that fails: advice from general knowledge.
**Chrome:** the Decision card on the Memory page.

**Build order:**

1. Create `src/Bosun.Core/Insights/MemoryEvidence.cs`. Done when: `bosun report --json` builds a bundle.
2. Add Advise to `src/Bosun.Panel/Views/MemoryView.xaml`. Done when: too little data is stated in the preview and the answer.

- [ ] Build the bundle from `report --json` in `MemoryEvidence.cs`: peaks, overlaps, repos, days logged and queue waits. Done when: `tests/Bosun.Core.Tests/MemoryEvidenceTests.cs` passes on a fixture.
- [ ] Warn in `MemoryView.xaml` with under 7 days logged, saying how many days the advice rests on, and pass that to the model. Done when: a fixture with 3 days shows the warning and Advise still works. Cheaper substitute: advice that hides how little data it has.
- [ ] Offer threshold suggestions as links to Settings and Optimise Preview. Done when: a test proves nothing changes until that page's own Apply.
- [ ] Commit: `insights: memory advisor`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~MemoryEvidence` exits 0, and fails if 3 days of data shows no warning.

## 8. Finding patterns

Spots repeated mistakes across review findings and repos, so a recurring fault gets fixed at its cause. This reads finished findings. It is not a review, and it never affects one.

**Phase:** 6
**Needs:** display-session
**Surface:** yes
**Fidelity:** `design/SPEC.md` §20, Finding patterns.
**Job:** see what keeps going wrong.
**Treatment:** a button on the Reviews page, listing patterns with links to their findings. Cheaper substitute that fails: a word count of findings.
**Chrome:** the Reviews page.

**Build order:**

1. Create `src/Bosun.Core/Insights/FindingEvidence.cs`. Done when: findings from a chosen scope build a bundle.
2. Add the button to `src/Bosun.Panel/Views/ReviewsView.xaml`. Done when: patterns link to their sources.

- [ ] Build the bundle from `--list-rounds --json` and `query rounds`, for this repo or all registered repos, in `FindingEvidence.cs`. Done when: `tests/Bosun.Core.Tests/FindingEvidenceTests.cs` passes on fixtures from two repos.
- [ ] Link each pattern to its findings in `ReviewsView.xaml`. Done when: a citation opens the findings text. Cheaper substitute: patterns with no source.
- [ ] Prove this path can't write in `FindingEvidence.cs`. Done when: a test finds no write to `docs/reviews`, `todo/` or a receipt, and `AGENTS.md`'s reviewer rule holds.
- [ ] Commit: `insights: finding patterns`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~FindingEvidence` exits 0, and fails if a file is written under `docs/reviews`.

## 9. Plan drafts

Turns plain English into a draft TODO section. The panel never writes to `todo/`, so the draft is text the operator copies, and the repo's own `add-todo` skill files it.

**Phase:** 6
**Needs:** display-session
**Surface:** yes
**Fidelity:** `design/SPEC.md` §20, Plan drafts.
**Job:** capture new work quickly in the right shape.
**Treatment:** a text box and a draft with validator results, plus Copy. Cheaper substitute that fails: writing the draft into `todo/` directly.
**Chrome:** the Plan page.

**Build order:**

1. Create `src/Bosun.Core/Insights/PlanDraft.cs`. Done when: a description gives a draft in the repo's format.
2. Check it. Done when: the repo's own validator reports on it without touching the repo.

- [ ] Build the bundle in `PlanDraft.cs` from the description, the repo's `todo/README.md` and `query plan`. Done when: `tests/Bosun.Core.Tests/PlanDraftTests.cs` passes on a fixture.
- [ ] Add `validate --candidate <folder>` to `scripts/todo-graph.py` and `scripts/lib/todo_graph.py`, reading a temporary tree while using the source repo's receipts and Git context. Done when: `scripts/tests/test_todo_graph.py` proves a bad candidate fails and the source repo's validation is unchanged. Cheaper substitute: changing the working directory, which still validates the real plan.
- [ ] Validate the draft in a temporary copy through that command, in `PlanDraft.cs`. Done when: a bad draft shows the validator's words, and a kit with no `--candidate` reads "Not checked: this kit can't validate a draft". Cheaper substitute: re-implementing the format rules.
- [ ] Offer only Copy in `Views/PlanView.xaml`, with the `add-todo` prompt to use. Done when: a test finds no write path to the repo. Cheaper substitute: a Save button.
- [ ] Commit: `insights: plan drafts`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~PlanDraft` exits 0, and fails if a file in the repo changes.

## 10. Second opinion on a stuck section

When a section is stuck on findings, another model family can say why. Advice only: it can't clear a finding or a stamp, and Claude never reviews, so Claude's work goes to OpenAI.

**Phase:** 6
**Needs:** display-session
**Surface:** yes
**Fidelity:** `design/SPEC.md` §20, Second opinion.
**Job:** get unstuck on a section with open findings.
**Treatment:** a button on a section with findings, with an answer labelled "Advice, not a review". Cheaper substitute that fails: an answer that looks like a review result.
**Chrome:** the section detail pane on the Plan page.

**Build order:**

1. Create `src/Bosun.Core/Insights/SecondOpinion.cs`. Done when: the provider is chosen from the writer.
2. Add the button to `src/Bosun.Panel/Views/SectionDetail.xaml`. Done when: open sections with findings offer it.

- [ ] Choose a provider of a different family from the section's recorded writer in `SecondOpinion.cs`, from the findings file. Done when: `tests/Bosun.Core.Tests/SecondOpinionTests.cs` proves Claude-written work is only offered OpenAI, and is unavailable with no OpenAI key. Cheaper substitute: letting Claude advise on Claude's work.
- [ ] Label every answer "Advice, not a review" in `SectionDetail.xaml`. Done when: a test finds the label, and no path writes findings or calls `stamp` or `repair`.
- [ ] Offer the button on any unstamped section with findings. Done when: a parked and an in-progress fixture show it, and a stamped one does not.
- [ ] Commit: `insights: second opinion`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~SecondOpinion` exits 0, and fails if Claude is offered for Claude-written work.

## Verification

- Release does not depend on this file, so a parked Insight can't hold up the core product.
- `dotnet test Bosun.slnx` passes, with all Insights off by default and the panel fully usable with no key.
- A live Insight on each provider is proven in `D99 T01 §5`.
- `python scripts/todo-graph.py validate` and `python scripts/todo-graph.py plan --check` pass.
