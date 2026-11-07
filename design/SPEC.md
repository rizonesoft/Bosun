# DevMode Panel interface spec

This file is the contract. A TODO section with `Surface: yes` cites a section here in `Fidelity:`, and a surface is done only when every control below is working or deferred to a named owner section. This file wins over the mockup and over memory. Later changes are made here first, dated, with the section that made them.

Mockup: [mockups/control-panel.html](./mockups/control-panel.html). Colours, radii, spacing and type sizes come only from [tokens/colors.json](./tokens/colors.json), and no control sets a hex value.

## 1. Shared chrome

- Window: custom title bar 44 px with the icon, "DevMode Panel", a run badge, and minimise, maximise and close. The maximise button answers `HTMAXBUTTON` so Windows 11 snap layouts appear. Minimum size 960 x 600. Per-monitor DPI v2.
- Rail: 208 px, groups Monitor, Workflow, Machine, Claude API, App. A page with unsaved edits may refuse navigation, offering Save or Discard.
- Activity pane: 132 px, resizable with a splitter, 500 entries at most. Each line starts with the word INFO, WARN or FAIL. Every panel action writes a line.
- Status is never colour alone: each state has a word. Every icon-only control has an automation name and a tooltip. Every target is at least 32 px high.
- Every page has four drawn states: loading, data, empty (says why and what to do) and failed (quotes the tool's message and offers Retry). A value that cannot be read shows "Not available" with a reason, never 0.
- `Btn.Primary` is for the one action that matters on a page, and `Btn.Danger` only for an action that stops something or changes the machine. Danger actions confirm once, naming exactly what will happen.
- Theme: Follow Windows (default), Dark or Light. Changes apply live.
- Refresh: pages refresh while visible (memory and queue 5 s, others 30 s, plus manual Refresh). A hidden page polls nothing. In the tray, only the pressure and run checks and the 12-hourly agent update check run. Corrected 2026-11-07 (plan review), adding the update check that `D05 T01 §3` owns.

## 2. Overview

Four metric cards (Windows available, WSL guest available, Queue, Dev instances), the Needs you table (state word, what, where, Open) and the Runs table (repo, state, ready, verified, heartbeat). Open goes to the owning page. An empty Needs you says "Nothing needs you."

## 3. Memory

Cards: Windows used, WSL VM, Overlapping builds. History chart: available memory with the pressure line, range 24 hours, 7 days or 14 days, and Show as table (the accessible equivalent). By repo table sortable by private GB, peak GB and processes. A Decision card summarises the peaks behind the 64 GB question from `devmode report --json` and states when too few days are logged.

## 4. Instances

Table of instance, repo, side, state, GB and uptime, grouped by repo. State words: Working, Reviewing, Idle, Unknown. Actions: Open folder, Open terminal here, and Stop (danger, confirms the process and repo, asks the process to close first). A repo with listed services also shows Park (danger, confirms what will stop) and Resume, owned by `D05 T02 §6`. A build job shows its queue slot. WSL unavailable shows the Windows rows plus a notice.

## 5. Queue

Slots table (slot, holder command, repo, side, waited, running). Waiting table (command, repo, side, waiting for, why). History table of finished jobs. Limits form: slots, second-slot memory floor in MB, maximum wait in seconds, and Save, which writes `config.json` and reads it back. Range errors are shown beside the field.

## 6. Plan

Repo picker. Cards: verified, ready, blocked. Sections grouped by phase with status word, ref, title, needs and rounds. Filter box and status filter. Section detail pane: body rendered as plain formatted text, dependencies with their state, needs, receipt link, findings file link. Graph view: layered dependency drawing with keyboard navigation, with the list as its equivalent. Findings tab shows `todo/findings.md`. Deferred tab shows `query deferred`. An unsupported kit shows "not supported by this kit" with what the probe found.

## 7. Reviews

Pin card (writer route, reviewer, model, CLI found, schedule) from `--check-pin --json`, showing a refusal in full. Review in flight (section, pass, reviewer, elapsed, deadline). Rounds table per section from `--list-rounds --json`. Receipts table from `query rounds`. Selecting a row shows the findings text. This page starts no reviews.

## 8. Unattended run

Repo picker. Cards: Run (state word, session, start), Heartbeat (age, next due), Watchdog (installed, last tick, stale), Counters (restarts, yields). Last API failure line when present. Controls: Start process plan (primary; opens a Windows Terminal tab in the repo; refuses beside a live session, naming it), Stop at end of turn, Halt with a ground (danger; ground picker and required reason), Install or Uninstall watchdog. Guard log tail with auto-scroll and pause. Status words: Active, Stopped, Drained, Stalled, Halted, Expired, Inactive.

## 9. Hooks and automations

Hooks table merged across user, project and local settings: event, matcher, command, source file, script found. Test runs a harmless hook payload against temporary state and shows the output, and is offered only for a hook known to isolate its state. Any other hook shows Test not available with the reason. Automations table from `tasks.json`, Task Scheduler and `last.json`: task, trigger, last run, result word, Run now, Open log, Install, Remove. Install and Remove act on that one task, and are unavailable, with the reason, where a repo's installer can't target one task.

## 10. Fleet

One row per registered repo: run guard kind, dispatcher features, graph kind, hook events, remaining ports. Read-only, with a link to the source file behind each cell. Ports stay deliberate work in that repo.

## 11. Agents

Table of agent, side, installed, latest, running count and state word, for Claude Code, Codex CLI, Antigravity CLI and GitHub CLI on Windows and WSL where installed. Update (confirms; warns of running sessions; runs in the queue; logs to Activity; reads the version back). Check now. Configuration checks table reports booleans only and never prints a value.

## 12. Optimise

Recommendations table: finding, evidence, effect, needs, action. Each change offers Preview (exact change), Apply (asks for elevation only if that action needs it), Verify (reads the setting back) and Undo (restores the saved backup). Items that need the operator show instructions. Items waiting on data say what they wait for and until when.

## 13. Workbench

Budget card (today and month against the caps, with bar and words). New task: Mode (Overflow agent or Ask), Repo, Model, Effort, Task text, Budget for this run (required for an overflow agent, and reserved against the caps before it starts), Start and Stop. Without a stored key, or with no caps set, Start is disabled with a line saying what to do, linking to Settings. Run list: state word, mode, repo, cost so far, worktree path, Open worktree, Open log. Ask streams text as it arrives. A refusal shows its category and message. Model list comes from the Models API, falling back to the priced list with a notice.

## 14. Usage

Ledger table: when, mode, repo, model, input, cached, output, cost. Totals by day, month and model. Export CSV. The dated price table is shown, with a warning when it is older than 90 days or a model has no price, in which case the row is marked "Unpriced" and counted against the caps at the highest known price.

## 15. Settings

General: Start with Windows in the tray, Theme, Pressure alert threshold, Notifications on or off by type. Repos: list, Add, Remove, Scan folder. Claude API: key status word, Paste new key, Import from 1Password, Remove key, daily cap, monthly cap. Every setting names its consumer in the section that builds it. Save writes atomically and reads back.

## 16. About

Version, commit, .NET runtime, log folders, Open crash folder, licence text.

## 17. Tray

Icon with state overlay: Normal, Pressure, Needs you. The tooltip states the same in words. Menu: Open, Queue summary, Pause notifications for 1 hour, Exit. Closing the window keeps the panel in the tray the first time only after saying so. Notifications: memory pressure, run stalled, halted or drained, automation failed, update available.

## 18. Icon

A dark rounded tile with a mint prompt chevron and two bars, mint and amber: the prompt and the two queue slots. Masters: [icon/](./icon/). 40 px and above uses the 256 master. 32, 24 and 16 px are redrawn on the pixel grid with no rim, and 20 px renders from the 16 px file. ICO frames: 16, 20, 24, 32, 40, 48, 64, 256. Edit the masters, never generated files.
