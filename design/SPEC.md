# Bosun Panel interface spec

This file is the contract. A TODO section with `Surface: yes` cites a section here in `Fidelity:`, and a surface is done only when every control below is working or deferred to a named owner section. This file wins over the mockup and over memory. Later changes are made here first, dated, with the section that made them.

Scope, set by the owner on 2026-11-10: the panel shows what `bosun.exe` and the loggers measure (memory, dev instances, the queue) and tunes this machine. It needs no other repo, no agent workflow, no unattended run and no API key, and it changes nothing inside a repository.

Mockup: [mockups/control-panel.html](./mockups/control-panel.html), which still draws the pages removed on 2026-11-10; this file wins. Colours, radii, elevation, motion, spacing and type sizes come only from [tokens/colors.json](./tokens/colors.json), and no control sets a hex value.

## 1. Shared chrome

- Window: custom title bar 44 px with the icon, "Bosun Panel", and minimise, maximise and close. The maximise button answers `HTMAXBUTTON` so Windows 11 snap layouts appear. Minimum size 960 x 600. Per-monitor DPI v2.
- Backdrop: the title bar and rail sit on Mica, and page content sits on a solid sheet with a rounded top-left corner. Where Mica is unavailable the same layout uses solid `Base`, per §19. Added 2026-11-07 (owner request, `D02 T03 §2`).
- Rail: 208 px, groups Monitor, Machine, App, with a Lucide glyph on every row, a sliding marker on the selected row and count badges per §19. A page with unsaved edits may refuse navigation, offering Save or Discard.
- WSL: the Monitor pages show both sides with a Side column. The panel never starts the WSL VM to poll. While WSL is stopped, its rows show "WSL not running" and the last data is marked with its age.
- Activity pane: 132 px, resizable with a splitter, 500 entries at most. Each line starts with the word INFO, WARN or FAIL. Every panel action writes a line.
- Status is never colour alone: each state has a word. Every icon-only control has an automation name and a tooltip. Every target is at least 32 px high.
- Every page has four drawn states: loading, data, empty (says why and what to do) and failed (quotes the tool's message and offers Retry). A value that cannot be read shows "Not available" with a reason, never 0.
- `Btn.Primary` is for the one action that matters on a page, and `Btn.Danger` only for an action that stops something or changes the machine. Danger actions confirm once, naming exactly what will happen.
- Theme: Follow Windows (default), Dark or Light. Changes apply live.
- Refresh: pages refresh while visible (memory and queue 5 s, others 30 s, plus manual Refresh). A hidden page polls nothing. In the tray, only the pressure check runs.

## 2. Overview

Four metric cards (Windows available, WSL guest available, Queue, Dev instances), and the Needs you table (state word, what, where, Open). Open goes to the owning page. An empty Needs you says "Nothing needs you."

## 3. Memory

Cards: Windows used, WSL VM, Overlapping builds. History chart: available memory with the pressure line, range 24 hours, 7 days or 14 days, and Show as table (the accessible equivalent). By repo table sortable by private GB, peak GB and processes. A Decision card summarises the peaks behind the 64 GB question from `bosun report --json` and states when too few days are logged.

## 4. Instances

Table of instance, repo, side, state, GB and uptime, grouped by repo. State words: Working, Reviewing, Idle, Unknown. Actions: Open folder and Open terminal here. The page stops nothing: a repo's processes are that repo's to manage. A build job shows its queue slot. WSL unavailable shows the Windows rows plus a notice.

## 5. Queue

Slots table (slot, holder command, repo, side, waited, running). Waiting table (command, repo, side, waiting for, why). History table of finished jobs. Limits form: slots, second-slot memory floor in MB, maximum wait in seconds, and Save, which writes `config.json` and reads it back. Range errors are shown beside the field.

## 6. Plan

Removed 2026-11-10 (owner decision): Bosun stays out of each repo's workflow and off every API. The number is kept so later sections keep theirs.

## 7. Reviews

Removed 2026-11-10 (owner decision): Bosun stays out of each repo's workflow and off every API. The number is kept so later sections keep theirs.

## 8. Unattended run

Removed 2026-11-10 (owner decision): Bosun stays out of each repo's workflow and off every API. The number is kept so later sections keep theirs.

## 9. Hooks and automations

Removed 2026-11-10 (owner decision): Bosun stays out of each repo's workflow and off every API. The number is kept so later sections keep theirs.

## 10. Fleet

Removed 2026-11-10 (owner decision): Bosun stays out of each repo's workflow and off every API. The number is kept so later sections keep theirs.

## 11. Agents

Removed 2026-11-10 (owner decision): Bosun stays out of each repo's workflow and off every API. The number is kept so later sections keep theirs.

## 12. Optimise

Recommendations table: finding, evidence, effect, needs, action. Each change offers Preview (exact change), Apply (asks for elevation only if that action needs it), Verify (reads the setting back) and Undo (restores the saved backup). Items that need the operator show instructions. Items waiting on data say what they wait for and until when.

## 13. Workbench

Removed 2026-11-10 (owner decision): Bosun stays out of each repo's workflow and off every API. The number is kept so later sections keep theirs.

## 14. Usage

Removed 2026-11-10 (owner decision): Bosun stays out of each repo's workflow and off every API. The number is kept so later sections keep theirs.

## 15. Settings

General: Start with Windows in the tray, Theme, Pressure alert threshold, Notifications on or off by type. Every setting names its consumer in the section that builds it. Save writes atomically and reads back.

## 16. About

Version, commit, .NET runtime, log folders, Open crash folder, licence text.

## 17. Tray

Icon with state overlay: Normal, Pressure, Needs you. The tooltip states the same in words. Menu: Open, Queue summary, Pause notifications for 1 hour, Exit. Closing the window keeps the panel in the tray the first time only after saying so. Notifications: memory pressure.

## 18. Icon

A dark rounded tile with a mint prompt chevron and two bars, mint and amber: the prompt and the two queue slots. Masters: [icon/](./icon/). 40 px and above uses the 256 master. 32, 24 and 16 px are redrawn on the pixel grid with no rim, and 20 px renders from the 16 px file. ICO frames: 16, 20, 24, 32, 40, 48, 64, 256. Edit the masters, never generated files. Menu and control glyphs are not this icon: they come from Lucide, per §19.

## 19. Premium components

Added 2026-11-07 from the owner's request for Lucide menu icons, softer rounding, premium components and a Mica title bar (`D02 T03`). The finish never outranks §1: status keeps its word, every target stays 32 px or more, and a hidden page animates and polls nothing. The whole panel stays within the 150 MB and 1% CPU idle budget.

- Backdrop: Mica through `DWMWA_SYSTEMBACKDROP_TYPE` on Windows 11 build 22621 or later. Solid `Base` on older Windows, with system transparency off, or in high contrast. A change to any of these applies live, without a restart. There is no live blur inside the window.
- Rounding: cards 12, controls 8, sheet 12, overlays 14, pills fully round.
- Elevation: cards use a border and at most the `Card` shadow, cached rather than redrawn. Only the palette and toasts use the `Overlay` shadow.
- Motion: fast 120 ms for hover, normal 220 ms for the rail marker, page changes and overlays, slow 400 ms for values changing. With Windows animation effects off, every change applies at once and no pulse runs.
- Glyphs: Lucide, through one icon map. A glyph beside a word is decorative. An icon-only control keeps its automation name and tooltip.
- Count badge: a rail row shows a count only when that page has something waiting, and the automation name says the count in words.
- Status pill: icon, word and a soft tint of the state's colour. The word is required, and it reads at 4.5:1 or better on `Base`, `Surface` and `Raised`.
- Segmented control: replaces a short exclusive choice, such as a chart range. It uses the arrow keys, exposes a selected state and keeps a 32 px target.
- Radial gauge: a value, its unit and a state word in the centre. It is used on metric cards. An unreadable value draws an empty track with "Not available", never 0.
- Sparkline: a recent trend inside a card. It has no axis, and its automation name gives the latest value and direction in words.
- Stacked bar: shares of a total, such as memory by group, with a legend giving every value in words.
- Queue slot: one tile per slot showing free, holder or waiting, with the holder's command and elapsed time. Waiting jobs follow in order, and the memory floor that holds slot 2 is stated in words.
- Skeleton: the loading state from §1, shaped like the page's data. It appears only after 150 ms, so a fast load does not flash.
- Page transition: content fades and rises 4 px over the normal duration, and only the content, never the rail.
- Command palette: Ctrl+K opens it over any page. It searches pages and the current page's actions. It never runs a danger action without that action's own confirmation. Esc closes it and returns focus. Enter runs the selected row.
- Toast: a transient in-window result of a panel action, at the bottom right, at most 3 at once, dismissed after 6 s unless hovered or focused. Each is announced through the live region, and each also has its line in the Activity pane, so a missed toast loses nothing. FAIL toasts stay until dismissed. Tray notifications in §17 remain the out-of-window path.

## 20. Insights

Removed 2026-11-10 (owner decision): Bosun stays out of each repo's workflow and off every API. The number is kept so later sections keep theirs.
