---
schema_version: 1
id: premium-components
domain: 02-shell
status: active
title: "TODO-03 -- Premium components"
---

# TODO-03 -- Premium components

> **Goal:** The panel has the Mica shell, Lucide glyphs and premium components from `design/SPEC.md` §19, in both themes, inside the 150 MB and 1% CPU idle budget.

> **Current state:** Measured 2026-11-07. `design/mockups/control-panel.html` shows the finish in HTML, and the owner approved it on 2026-11-07, expecting to fine-tune it during the build. `design/tokens/colors.json` holds the radius, elevation, motion, soft status and `LineStrong` tokens. No WPF code exists. `D02 T02 §2` adds the Lucide icon pack.

## Inputs

- `design/SPEC.md` §1 and §19, and `design/mockups/control-panel.html` for the look. The SPEC wins.
- `docs/specs/2026-11-07-control-panel-design.md`, the decisions table.
- -> XREF: D02 T01 §1 -- consumed by this file
- -> XREF: D02 T01 §4 -- consumed by this file
- -> XREF: D02 T02 §4 -- consumed by this file
- -> XREF: D03 T01 §2 -- consumes this file
- -> XREF: D03 T02 §2 -- consumes this file
- -> XREF: D05 T02 §3 -- consumes this file
- -> XREF: D07 T01 §1 -- consumes this file

## Outcome

- Each component renders off-screen in dark, light and high contrast, with its unavailable and reduced-motion states.
- The idle budget test still passes with every component on screen, and a hidden page runs no animation.

**Adjacency:** all=not-applicable (shared components with no records of their own)

**Adjacency rationale:** The pages own their data, settings and audit lines. These components only present what a page gives them.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Elevation and motion tokens | D02 T01 §1 | [ ] |
| 2 | §2 | Mica backdrop with a solid fallback | D02 T02 §4 | [ ] |
| 3 | §3 | Lucide icon map and rail marker | §1, D02 T02 §4 | [ ] |
| 4 | §4 | Status pill and segmented control | §1, D02 T01 §4 | [ ] |
| 5 | §5 | Gauge, sparkline and stacked bar | §4 | [ ] |
| 6 | §6 | Queue slot visuals | §5 | [ ] |
| 7 | §7 | Skeleton and page transitions | §1, D02 T02 §4 | [ ] |
| 8 | §8 | Command palette | §3 | [ ] |
| 9 | §9 | In-window toasts | §4, D02 T02 §4 | [ ] |

## 1. Elevation and motion tokens

The generator owns these like every other token, so no view carries a shadow or a duration of its own. Motion also has one gate, so honouring the system setting is not left to each animation.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Extend `scripts/build-theme.py`. Done when: the generated theme holds the elevation and motion resources.
2. Create `src/Bosun.Panel/Theme/Motion.cs`. Done when: every duration resolves through it.

- [ ] Emit `elevation` from `design/tokens/colors.json` in `scripts/build-theme.py`, choosing `OpacityDark` or `OpacityLight` per theme. Done when: both generated files hold `Elevation.Card` and `Elevation.Overlay` with different opacity. Cheaper substitute: a shadow literal in a style.
- [ ] Emit `motion` durations and the easing in `scripts/build-theme.py`. Done when: `Motion.Fast`, `Motion.Normal`, `Motion.Slow` and `Motion.Pulse` load in a WPF test. Cheaper substitute: durations typed into each storyboard.
- [ ] Refuse a missing or non-numeric elevation or motion value in `scripts/build-theme.py`. Done when: a fixture exits 2 and names the token.
- [ ] Create `src/Bosun.Panel/Theme/Motion.cs` reading `SystemParameters.ClientAreaAnimation`, and returning zero durations when it is off. Done when: `tests/Bosun.Panel.Tests/MotionTests.cs` passes with the setting faked both ways. Cheaper substitute: a panel-only animation setting that ignores Windows.
- [ ] Update `Motion.cs` on `SystemParameters.StaticPropertyChanged`. Done when: a test raises the change and a new duration reads zero, with no restart.
- [ ] Assert the soft status tints in `tests/Bosun.Panel.Tests/ThemeContrastTests.cs`, compositing each `*Soft` over `Base`, `Surface` and `Raised`. Done when: each status word reads at 4.5:1 or better in both themes, and a stronger tint fails the test. Cheaper substitute: measuring the word on the plain surface only.
- [ ] Extend `tests/Bosun.Panel.Tests/XamlRulesTests.cs` to fail a literal `Duration`, `BlurRadius` or `CornerRadius` in a view. Done when: a literal in a view fails it.
- [ ] Commit: `theme: elevation and motion tokens`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~Motion` exits 0, and fails if the zero-duration path is removed.

## 2. Mica backdrop with a solid fallback

Windows composes Mica outside the process, so it costs the panel almost nothing, but it only exists on some systems and must be dropped live when the operator turns transparency off or enters high contrast.

**Phase:** 2
**Needs:** display-session
**Surface:** yes
**Fidelity:** `design/SPEC.md` §1 and §19, Backdrop.
**Job:** the shell looks native on Windows 11 and stays legible everywhere else.
**Treatment:** Mica under the title bar and rail, a solid sheet for content. Cheaper substitute that fails: a tinted transparent window with a live blur.
**Chrome:** the custom title bar from `D02 T02 §4`; no new control.

**Build order:**

1. Create `src/Bosun.Panel/Shell/BackdropPolicy.cs`. Done when: its table test passes for each combination.
2. Create `src/Bosun.Panel/Shell/Backdrop.cs`. Done when: the running window shows Mica on this machine and solid `Base` with transparency off.

- [ ] Add `BackdropPolicy.Choose` in `src/Bosun.Panel/Shell/BackdropPolicy.cs`, taking the build number, transparency and high contrast. Done when: `tests/Bosun.Panel.Tests/BackdropPolicyTests.cs` gets Mica only at build 22621 or later with transparency on and high contrast off.
- [ ] Read `EnableTransparency` under `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize` in `src/Bosun.Panel/Shell/Backdrop.cs`. Done when: a missing value reads as on, matching Windows, in a test seam. Cheaper substitute: assuming transparency is always on.
- [ ] Set `DWMWA_SYSTEMBACKDROP_TYPE` to `DWMSBT_MAINWINDOW` in `src/Bosun.Panel/Shell/Backdrop.cs`, with the frame extended and the `HwndSource` background transparent. Done when: `DwmGetWindowAttribute` reads back the value on this machine. Cheaper substitute: an image of a wallpaper behind the window.
- [ ] Set `DWMWA_USE_IMMERSIVE_DARK_MODE` from the active theme in `Backdrop.cs`. Done when: read-back follows a live theme change in both directions.
- [ ] Draw solid `Base` under the title bar and rail when the policy says solid, in `src/Bosun.Panel/Shell/MainWindow.xaml`. Done when: off-screen renders with the policy forced solid show no transparent pixel. Cheaper substitute: leaving the window transparent, which paints black or stale pixels.
- [ ] Reapply the policy on `WM_SETTINGCHANGE` and `SystemParameters.StaticPropertyChanged` in `Backdrop.cs`. Done when: a test writes `EnableTransparency` and restores it, and the window switches with no restart.
- [ ] Add the rounded sheet with `Radius.Sheet` in `MainWindow.xaml`. Done when: the off-screen render in both themes shows the top-left corner rounded and the rail outside the sheet.
- [ ] Log a failed DWM call as one WARN line in the Activity pane, and fall back to solid in `Backdrop.cs`. Done when: a test seam returning a failure HRESULT leaves the window solid with one WARN line. Cheaper substitute: ignoring the HRESULT.
- [ ] Commit: `shell: Mica backdrop with a solid fallback`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~Backdrop` exits 0, and fails if the high contrast rule is removed from `BackdropPolicy.Choose`.

## 3. Lucide icon map and rail marker

One map keeps glyphs consistent and lets a test catch a kind the icon pack does not have.

**Phase:** 2
**Needs:** display-session
**Surface:** yes
**Fidelity:** `design/SPEC.md` §1 Rail, and §19 Glyphs and Count badge.
**Job:** the operator finds a page by shape and sees at a glance which pages have something waiting.
**Treatment:** a glyph per rail row, a sliding marker and count badges. Cheaper substitute that fails: emoji or font characters for icons.
**Chrome:** the rail from `D02 T02 §4`.

**Build order:**

1. Create `src/Bosun.Panel/Theme/Glyphs.cs`. Done when: every rail row and shared action resolves through it.
2. Extend `src/Bosun.Panel/Shell/MainWindow.xaml`. Done when: the marker slides between rows and badges show.

- [ ] Create `src/Bosun.Panel/Theme/Glyphs.cs` mapping each page and shared action to a `PackIconLucideKind`, matching the mockup's choices. Done when: `tests/Bosun.Panel.Tests/GlyphTests.cs` proves every rail row has a glyph and each one is a defined kind. Cheaper substitute: glyph names typed into each view.
- [ ] Fail a `PackIconLucide` used outside the map in `tests/Bosun.Panel.Tests/XamlRulesTests.cs`. Done when: a direct kind in a view fails the test.
- [ ] Add the rail marker in `MainWindow.xaml`, animated with `Motion.Normal`. Done when: it lands on the selected row after keyboard and mouse navigation, and jumps at once with motion off. Cheaper substitute: a static highlight on the selected row.
- [ ] Add `CountBadge` to the rail row template in `src/Bosun.Panel/Theme/Controls.xaml`. Done when: a zero count shows nothing and a count of 3 renders.
- [ ] Set each rail row's automation name to include its count in words. Done when: a UI Automation test reads "Queue, 3 waiting". Cheaper substitute: a badge only sighted users can read.
- [ ] Commit: `shell: Lucide icon map, rail marker and badges`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~Glyph` exits 0, and fails with a rail row left unmapped.

## 4. Status pill and segmented control

These two appear on nearly every page. Status keeps its word, so a pill without one must not compile into a page.

**Phase:** 2
**Needs:** display-session
**Surface:** yes
**Fidelity:** `design/SPEC.md` §19, Status pill and Segmented control.
**Job:** the operator reads a state at a glance, with no reliance on colour, and flips a short choice in one press.
**Treatment:** a tinted pill with icon and word, and a compact segmented group. Cheaper substitute that fails: a coloured dot, or a combo box for two options.
**Chrome:** the generated theme and `D02 T01 §4` templates.

**Build order:**

1. Create `src/Bosun.Panel/Controls/StatusPill.cs`. Done when: each state word renders in both themes.
2. Create `src/Bosun.Panel/Controls/SegmentedControl.cs`. Done when: arrow keys move the selection.

- [ ] Create `StatusPill` in `src/Bosun.Panel/Controls/StatusPill.cs` with `Kind`, `Word` and a Lucide glyph from `Glyphs.cs`. Done when: `tests/Bosun.Panel.Tests/StatusPillTests.cs` renders Ok, Warn, Danger, Info and Neutral in both themes. Cheaper substitute: a coloured dot with a tooltip.
- [ ] Make an empty `Word` an error in `StatusPill.cs`. Done when: a test with an empty word throws in Debug and renders "Unknown" in Release. Cheaper substitute: a pill showing only a colour.
- [ ] Expose `Word` as the automation name in `StatusPill.cs`. Done when: a UI Automation test reads the word.
- [ ] Create `SegmentedControl` in `src/Bosun.Panel/Controls/SegmentedControl.cs` on `ListBox`, with the selected indicator using `Motion.Fast`. Done when: a render shows the indicator on the selected item in both themes. Cheaper substitute: a row of toggle buttons with no exclusive selection.
- [ ] Support Left, Right, Home and End in `SegmentedControl.cs`. Done when: a UI Automation test moves and reads selection by keyboard only.
- [ ] Hold each segment at 32 px or more in `src/Bosun.Panel/Theme/Controls.xaml`. Done when: the style walk from `D02 T01 §4` asserts it.
- [ ] Commit: `controls: status pill and segmented control`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~StatusPill` exits 0, and fails if the empty word check is removed.

## 5. Gauge, sparkline and stacked bar

These are drawn from data and stay cheap: they draw once per data change and hold no timer. An unreadable value must never look like zero.

**Phase:** 2
**Needs:** display-session
**Surface:** yes
**Fidelity:** `design/SPEC.md` §19, Radial gauge, Sparkline and Stacked bar.
**Job:** the operator sees how full memory is and which way it is going without reading a table.
**Treatment:** retained-mode visuals with words beside each one. Cheaper substitute that fails: a chart library's default look at a large memory cost.
**Chrome:** the generated theme; three custom elements.

**Build order:**

1. Create `src/Bosun.Panel/Controls/RadialGauge.cs`. Done when: value, unit and state word render, and an absent value draws the empty track.
2. Create `src/Bosun.Panel/Controls/Sparkline.cs` and `StackedBar.cs`. Done when: each renders real sample data in both themes.

- [ ] Create `RadialGauge` in `src/Bosun.Panel/Controls/RadialGauge.cs` with a nullable value, unit and state word. Done when: `tests/Bosun.Panel.Tests/GaugeTests.cs` compares the arc angle at 0, 50 and 100 percent.
- [ ] Draw a null value as an empty track with "Not available" in `RadialGauge.cs`. Done when: a test renders null and finds that text and no "0". Cheaper substitute: treating a missing value as 0.
- [ ] Animate a gauge value change with `Motion.Slow` in `RadialGauge.cs`. Done when: a test sees no animation clock with motion off.
- [ ] Create `Sparkline` in `src/Bosun.Panel/Controls/Sparkline.cs` drawing a frozen `StreamGeometry`. Done when: a test proves redraw only when the sample array changes. Cheaper substitute: redrawing on a timer.
- [ ] Set the `Sparkline` automation name to the latest value and direction in words. Done when: a UI Automation test reads "12.4 GB, rising".
- [ ] Create `StackedBar` in `src/Bosun.Panel/Controls/StackedBar.cs` with a legend giving each value in words. Done when: a test proves segment widths sum to the bar's width and the legend holds every value. Cheaper substitute: a bar with colour as the only key.
- [ ] Reject negative or non-finite samples in all three, showing "Not available" in place. Done when: a test with NaN and a negative value renders that text without an exception.
- [ ] Commit: `controls: gauge, sparkline and stacked bar`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~Gauge` exits 0, and fails if a null value draws as 0.

## 6. Queue slot visuals

The queue is what the panel exists to show. It gets visuals of its own, fed entirely by tool JSON.

**Phase:** 2
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §19, Queue slot.
**Job:** the operator sees who holds the slots and what is waiting.
**Treatment:** a tile per slot and an ordered waiting row. Cheaper substitute that fails: a table row alone for a slot.
**Chrome:** the generated theme; `StatusPill` from §4.

**Build order:**

1. Create `src/Bosun.Panel/Controls/QueueSlotTile.cs`. Done when: free, holder and waiting states render from a sample of `bosun status --json`.
2. Create `src/Bosun.Panel/Controls/WaitingRow.cs`. Done when: waiting jobs render in order with why.

- [ ] Create `QueueSlotTile` in `src/Bosun.Panel/Controls/QueueSlotTile.cs` showing free or holder, command and elapsed time. Done when: `tests/Bosun.Panel.Tests/QueueVisualTests.cs` renders each state in both themes with its word. Cheaper substitute: colour showing busy or free.
- [ ] Create `WaitingRow` in `src/Bosun.Panel/Controls/WaitingRow.cs` listing waiting jobs in order with why, including the memory floor for slot 2 in words. Done when: a test with a memory-held job shows the floor and the available figure.
- [ ] Commit: `controls: queue slot visuals`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~QueueVisual` exits 0, and fails if a slot state loses its word.

## 7. Skeleton and page transitions

Every page has a loading state under `design/SPEC.md` §1. A shared shape stops each page inventing its own spinner, and the delay stops a fast load flashing.

**Phase:** 2
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §1 page states, and §19 Skeleton and Page transition.
**Job:** a page change feels calm, and loading never looks like missing data.
**Treatment:** placeholder blocks shaped like the data, and a short fade with a 4 px rise. Cheaper substitute that fails: a spinner, or an empty table that looks like no data.
**Chrome:** the shell's page frame from `D02 T02 §4`.

**Build order:**

1. Create `src/Bosun.Panel/Controls/Skeleton.cs`. Done when: it appears only after 150 ms.
2. Extend the page frame in `src/Bosun.Panel/Shell/PageFrame.xaml`. Done when: content changes animate and the rail does not.

- [ ] Create `Skeleton` in `src/Bosun.Panel/Controls/Skeleton.cs` with `Card`, `Table` and `Gauge` shapes. Done when: off-screen renders show each shape in both themes.
- [ ] Delay showing the skeleton by 150 ms in the page frame. Done when: a test with a 50 ms load never shows it and one with a 500 ms load does. Cheaper substitute: a skeleton that flashes on each refresh.
- [ ] Mark the skeleton busy through `AutomationProperties.ItemStatus` and the live region, once per load. Done when: a test sees one "Loading" announcement. Cheaper substitute: placeholder blocks a screen reader announces as nothing.
- [ ] Keep current data on a background refresh in `PageFrame.xaml`. Done when: a test proves a 5 s poll never shows the skeleton over existing data.
- [ ] Add the page transition with `Motion.Normal` in `PageFrame.xaml`. Done when: a test proves opacity and offset animate only on navigation, and apply at once with motion off.
- [ ] Add a failed state with the tool's message and Retry to `PageFrame.xaml`. Done when: a test with a failing fake shows the message, and Retry calls the page's refresh once. Cheaper substitute: a generic error line.
- [ ] Add an empty state slot to `PageFrame.xaml`, with each page giving why and what to do. Done when: a test proves a page can't declare empty with no text.
- [ ] Add a `NotAvailable` value template with its reason to `src/Bosun.Panel/Theme/Controls.xaml`. Done when: a null value shows "Not available" and the reason, never 0.
- [ ] Show a stopped distro's last data with its age in `PageFrame.xaml`. Done when: a fixture with WSL stopped reads the age in words on a page showing WSL data.
- [ ] Commit: `shell: skeleton and page transitions`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~Skeleton` exits 0, and fails if the 150 ms delay is removed.

## 8. Command palette

A keyboard route to every page and action. It adds no action of its own, so it must not become a way around a danger confirmation.

**Phase:** 2
**Needs:** display-session
**Surface:** yes
**Fidelity:** `design/SPEC.md` §19, Command palette.
**Job:** the operator reaches any page or current action without the mouse.
**Treatment:** a centred overlay with a search field and ranked rows. Cheaper substitute that fails: a menu bar.
**Chrome:** an overlay with `Radius.Overlay` and `Elevation.Overlay`, and glyphs from `Glyphs.cs`.

**Build order:**

1. Create `src/Bosun.Core/CommandIndex.cs`. Done when: ranked search works in a plain unit test.
2. Create `src/Bosun.Panel/Shell/CommandPalette.xaml`. Done when: Ctrl+K opens it on any page.

- [ ] Create `CommandIndex` in `src/Bosun.Core/CommandIndex.cs` ranking prefix over word start over substring. Done when: `tests/Bosun.Core.Tests/CommandIndexTests.cs` proves the order.
- [ ] Let each page register its actions in `src/Bosun.Panel/Shell/PageBase.cs`. Done when: a test proves only the current page's actions appear, with every page always listed.
- [ ] Create the overlay in `src/Bosun.Panel/Shell/CommandPalette.xaml`, opened by Ctrl+K. Done when: a UI Automation test opens it from three pages, including with focus in a text field.
- [ ] Route a danger action through that action's own confirmation in `CommandPalette.xaml.cs`. Done when: a test proves a danger command shows the same confirmation as its button. Cheaper substitute: a palette that runs the command directly.
- [ ] Show a disabled action with its reason, and refuse to run it. Done when: a test with a disabled action shows the page's reason.
- [ ] Restore focus to the prior element on Esc. Done when: a UI Automation test reads the focused element before and after.
- [ ] Refuse navigation from the palette while the page has unsaved edits, through `SelectionGuard`. Done when: a test sees the Save or Discard offer from `design/SPEC.md` §1.
- [ ] Commit: `shell: command palette`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~CommandPalette` exits 0, and fails if a danger command skips its confirmation.

## 9. In-window toasts

A short result where the operator is looking. The Activity pane stays the record, so a toast never holds the only copy of anything.

**Phase:** 2
**Needs:** display-session
**Surface:** yes
**Fidelity:** `design/SPEC.md` §19, Toast.
**Job:** the operator sees that an action finished without watching the Activity pane.
**Treatment:** small stacked cards at the bottom right. Cheaper substitute that fails: a modal message box after each action.
**Chrome:** an overlay with `Elevation.Overlay` and `StatusPill` from §4.

**Build order:**

1. Create `src/Bosun.Panel/Shell/ToastHost.cs`. Done when: the stack, timeout and cap work in a test.
2. Wire `src/Bosun.Panel/Shell/ActivityLog.cs`. Done when: each toast has its Activity line.

- [ ] Create `ToastHost` in `src/Bosun.Panel/Shell/ToastHost.cs`, holding at most 3 and dropping the oldest non-FAIL first. Done when: `tests/Bosun.Panel.Tests/ToastTests.cs` proves the cap with a fourth toast.
- [ ] Dismiss after 6 s unless hovered or focused. Done when: a fake clock test proves the timer pauses on both. Cheaper substitute: a toast that vanishes under the pointer.
- [ ] Keep a FAIL toast until it is dismissed. Done when: a fake clock test shows it after 60 s.
- [ ] Write the Activity line from the single `Toast.Show` path. Done when: a test proves one line per toast, starting with INFO, WARN or FAIL. Cheaper substitute: a toast with no record.
- [ ] Announce each toast through `LiveRegion`. Done when: a test sees one announcement per toast.
- [ ] Make Dismiss reachable by keyboard with an automation name. Done when: a UI Automation test closes a toast by keyboard.
- [ ] Hold no timer once the stack is empty or the window is hidden, and show pending toasts' Activity lines only when hidden. Done when: a test finds no running timer in both cases.
- [ ] Commit: `shell: in-window toasts`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~Toast` exits 0, and fails if `Toast.Show` stops writing the Activity line.

## Verification

- Off-screen renders of every component in dark, light and high contrast were reviewed against `design/SPEC.md` §19.
- `bosun run -- dotnet test Bosun.slnx` passes.
- `python scripts/todo-graph.py validate` and `python scripts/todo-graph.py plan --check` pass.
