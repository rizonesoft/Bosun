## Plan review: astra (`gpt-6.2-astra` via `codex`)

- Writer: claude
- Run: 2026-11-22T11:14:20Z
- Candidate: a36d6a73c1869d0017f8b86cc64ccb2242c787e2
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: a36d6a73c1869d0017f8b86cc64ccb2242c787e2
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6.2-astra
- Attempt cli: codex
- Lenses: plan
- Seconds: 144
- Lines: 11
- Digest: 9f37fc58f3b2775f

1. **P1, §3 lines 118, 123 and 132.** Base line 89 asks for crash dumps from the normal local gate and CI, with the CI dump retained as an artifact. Its receiving item at `todo/02-shell/TODO-05-shell-follow-ups.md:290` can close after ten clean local runs without exercising either path. The full ledger reported all 35 entries settled, and graph validation passed with this omission. Add local-gate collection and CI artifact retention to the owning item. Prove both with a deliberately crashing test host.
2. **P1, §3 lines 117, 121 and 130.** Base line 80 is marked `Done` for live switching in all four Windows contrast themes. The record cites the system-colour lookup and live application. The cited section’s test palettes at `tests/Bosun.Panel.Tests/Helpers/ContrastPalettes.cs:18` cover Night Sky and Desert. Cite evidence for switching into the remaining two themes and restoring the chosen theme, or schedule the outstanding verification before closing the finding.
3. **P2, §3 lines 121 through 127.** All seven batch commands omit `--cite-lines` and `--record`. Each can pass with its classification row deleted or with the strike pointing to a section that names the same file but owns a different finding. Add both options and the classification file to each batch command, as line 132 already does for the whole range.
4. **P2, §3 lines 117, 120 and 132.** Step 1 requires an inspected `file:line`, but the final checkpoint accepts a directory as evidence. The actual records for base lines 89, 105 and 106 cite entire test directories. For line 89, that citation also leaves the local gate and workflow unaccounted for. Require a concrete file and line for positive claims. For an absence claim, record the files or search scope inspected and what was found.
5. **P2, §3 lines 110, 125 and 132.** The section promises to preserve the existing strike and owner for base line 98. Both the `96-101` batch and the final checkpoint skip entries already struck at the base, so deleting that row or changing its owner passes both. Compare that one row with its base text and disposition, allowing its position to move.
6. **P2, §3 lines 118 and 126.** Base line 105 requires the smoke timing to keep one panel per test. Its receiving item at `todo/02-shell/TODO-05-shell-follow-ups.md:293` accepts five runs and a timing table without that condition. Carry the isolation requirement into the item and record it with the measurements so reusing a warm panel across tests cannot satisfy the finding.
7. **P3, §3 line 130.** The evidence says 33 findings were scheduled. The classification contains 34 scheduled and one done. Correct the breakdown to 31 scheduled in `D02 T05`, two in `D03 T02`, one in `D99 T01`, and one done.

HIGHEST: P1  
PLAN: gaps  
OVERALL: needs-attention

- Planned: gaps 1 and 6 in D02 T05 §11 (crash dumps through the local gate and CI with the dump kept as an artefact, proved by a deliberate crash; the smoke measured keeping one panel per test), gap 2 by scheduling base line 80 into D02 T05 §1 (two of four contrast themes proved), gaps 3 and 7 in the section (every batch with both checks, the breakdown corrected), all in the correction commit
- Planned: gaps 4 and 5 as the ledger's own checks (an inspected path must be a file of the repository; a row struck before the base must stay as it was), with the directory citations of base lines 89, 105 and 106 made files, in the correction commit

## Round 1: astra (`gpt-6.2-astra` via `codex`)

- Writer: claude
- Run: 2026-11-22T11:23:47Z
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: a36d6a73c1869d0017f8b86cc64ccb2242c787e2
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6.2-astra
- Attempt cli: codex
- Lenses: adversarial
- Seconds: 73
- Lines: 10
- Digest: 384a94d023fc03f6

## adversarial

**needs-attention**

1. **P2** `scripts/findings-ledger.py:131` passes a path from the review record directly to `is_file()`. On Windows, a UNC citation makes the reviewer probe a network share during a local checkpoint. An absolute path also bypasses the checkout root, and a link can lead outside it. I reproduced the missing repository boundary locally by replacing base line 76’s inspected path with `.git/HEAD` in memory. The full checkpoint accepted Git metadata as source evidence and reported all 35 entries settled. Validate the citation against repository-relative tracked files before probing the filesystem, and reject links that escape the checkout.
2. **P1** `scripts/findings-ledger.py:130` strips the cited line number before validation. I changed only the positive evidence citation for base line 76 from `RenderCatalog.cs:782` to `RenderCatalog.cs:782000` in memory. The full `76-111` checkpoint returned `0` and reported all 35 entries settled, although the cited line does not exist. A mistyped or stale line number therefore passes the evidence check. Validate each supplied line or range against the cited file’s actual bounds.

HIGHEST: P1  
RECURRING: none  
OVERALL: needs-attention

## Round 2: astra (`gpt-6.2-astra` via `codex`)

- Writer: claude
- Run: 2026-11-22T11:26:38Z
- Effort: low
- Writer model: claude-opus-5-5
- Policy: a36d6a73c1869d0017f8b86cc64ccb2242c787e2
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6.2-astra
- Attempt cli: codex
- Lenses: consistency
- Seconds: 47
- Lines: 10
- Digest: 3b97d68cb415f39a

## consistency

**needs-attention**

1. **P2** `scripts/findings-ledger.py:133` treats Git index membership as proof that the cited file exists in the current checkout. In a disposable checkout, rename `src/Bosun.Panel/Theme/Controls.xaml` without staging the rename, then run the section’s ledger checkpoint for base line 81. Its path-only citation still passes because `git ls-files` lists the old name. Run the same checkpoint for base line 83, which cites the same file with a line number, and `read_text()` raises `FileNotFoundError` instead of reporting the invalid citation. Both forms should produce the ledger’s normal finding-specific diagnostic for a missing file.
2. **P3** `todo/02-shell/TODO-05-shell-follow-ups.md:41` exceeds the repository’s 100-character limit for Markdown table rows. I measured 106 characters on line 41 and 101 on line 44. Reproduce with `rg -n '^\|.{100,}$' todo/02-shell/TODO-05-shell-follow-ups.md`.

HIGHEST: P2  
RECURRING: none  
OVERALL: needs-attention
