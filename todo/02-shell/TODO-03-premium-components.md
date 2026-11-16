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

> **Corrected 2026-11-13:** a WPF skeleton exists from `D00 T02 §1`: `src/Bosun.Panel` (`net11.0-windows`, `UseWPF`) with `App.xaml`, an empty `App.xaml.cs`, `Foundation.xaml` and `FoundationText.cs`, and `tests/Bosun.Panel.Tests` with only `FoundationTests.cs`; there is still no `Theme/`, `Shell/` or `Controls/` folder, no `MainWindow`, no `app.manifest`, and no MahApps, H.NotifyIcon or FlaUI package in `Directory.Packages.props`.

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
| 1 | §1 | Elevation and motion tokens | D02 T01 §1 | [x] |
| 2 | §2 | Mica backdrop with a solid fallback | D02 T02 §4 | [ ] |
| 3 | §3 | Lucide icon map and rail marker | §1, D02 T02 §4 | [ ] |
| 4 | §4 | Status pill and segmented control | §1, D02 T01 §4 | [ ] |
| 5 | §5 | Gauge, sparkline and stacked bar | §4 | [ ] |
| 6 | §6 | Queue slot visuals | §5 | [ ] |
| 7 | §7 | Skeleton and page transitions | §1, D02 T02 §4 | [x] |
| 8 | §8 | Command palette | §3 | [ ] |
| 9 | §9 | In-window toasts | §4, D02 T02 §4 | [ ] |
| 10 | §10 | High contrast theme and renders | §1, §7 | [ ] |

## 1. Elevation and motion tokens

> **Started:** 2026-11-16T02:07:11Z

The generator owns these like every other token, so no view carries a shadow or a duration of its own. Motion also has one gate, so honouring the system setting is not left to each animation.

**Phase:** 2
**Needs:** none
**Surface:** no

**Corrected 2026-11-16:** checked against the tree before the build. The tokens are in `design/tokens/colors.json` (`elevation.Card` and `elevation.Overlay` with `Blur`, `Depth`, `OpacityDark`, `OpacityLight`; `motion` with `FastMs` 120, `NormalMs` 220, `SlowMs` 400, `PulseMs` 2400 and `Ease` [0.2, 0.8, 0.2, 1]), and `scripts/build-theme.py` emits neither yet (its docstring says so). The decisions, each with what reversing it costs:

- An elevation is a frozen `DropShadowEffect` per theme: `BlurRadius` from `Blur`, `ShadowDepth` from `Depth`, `Direction` 270 (straight down, as the mockup's `box-shadow` offsets), `Color` black (the mockup's `rgba(0,0,0,..)`), `Opacity` from `OpacityDark` or `OpacityLight`. Caching the drawn shadow (`BitmapCache`, design/SPEC.md §19) is the consumer's, the card and overlay sections. Reversal cost: one generator function.
- Motion is emitted as `Duration` resources `Motion.Fast`, `Motion.Normal`, `Motion.Slow`, `Motion.Pulse` and a frozen `KeySpline` `Motion.Ease`, the same in both themes. `Motion.cs` is the one gate: it reads those design values from the resources and gives the duration to use, zero for each when Windows' animation effects are off, with `Animates` false so a pulse is not started at all (design/SPEC.md §19: "every change applies at once and no pulse runs"), and `Changed` when the setting changes. Animations are started from code through it, because a storyboard in a style is frozen and can't take a `DynamicResource`. Reversal cost: none.
- `scripts/build-theme.py` refuses, exit 2 naming the token, a missing elevation or motion group or key, a non-number, an opacity outside 0 to 1, a duration outside 0 to 10000 ms, and an `Ease` that is not four numbers with both x values in 0 to 1 (a cubic Bézier's x must stay in its interval, or WPF's `KeySpline` throws).
- The literal rule allows `0`: a `CornerRadius="0"` (the window's square corners on its `WindowChrome`, `src/Bosun.Panel/Shell/MainWindow.xaml:13`) or a zero `Duration` is no rounding and no motion, not a value a token owns. It reads XAML views and code alike, as the colour rule does: `BlurRadius = 8`, `new CornerRadius(8)` and `new Duration(...)` in code fail too.
- The soft tints pass today, tightest light `Danger` on `Raised` at 4.52:1 (measured 2026-11-16).
- The checkpoint named `dotnet test Bosun.slnx --filter FullyQualifiedName~Motion`, which the test platform here takes as `dotnet test --solution Bosun.slnx --filter-class "*MotionTests"`; the generator's refusals are proved by `scripts/tests/test_build_theme.py`.

**Build order:**

1. Extend `scripts/build-theme.py`. Done when: the generated theme holds the elevation and motion resources.
2. Create `src/Bosun.Panel/Theme/Motion.cs`. Done when: every duration resolves through it.

- [x] Emit `elevation` from `design/tokens/colors.json` in `scripts/build-theme.py`, choosing `OpacityDark` or `OpacityLight` per theme. Done when: in a WPF test loading each generated file, `Elevation.Card` and `Elevation.Overlay` are frozen `DropShadowEffect`s whose `BlurRadius`, `ShadowDepth` and `Opacity` equal the token file's for that theme, with `Direction` 270 and `Color` black, and `test_build_theme.py` proves a changed `Blur` reaches the generated file (**Added 2026-11-16** from the plan review). Cheaper substitute: a shadow literal in a style.
- [x] Emit `motion` durations and the easing in `scripts/build-theme.py`. Done when: in a WPF test loading each generated file, `Motion.Fast`, `Motion.Normal`, `Motion.Slow` and `Motion.Pulse` are `Duration`s equal to the token file's milliseconds and `Motion.Ease` is a frozen `KeySpline` whose control points are the token's four numbers, and `test_build_theme.py` proves a changed `FastMs` reaches the generated file (**Added 2026-11-16** from the plan review). Cheaper substitute: durations typed into each storyboard.
- [x] Refuse a missing or non-numeric elevation or motion value in `scripts/build-theme.py`, and an opacity, duration or easing out of its range. Done when: a fixture for each exits 2 and names the token, with nothing written, in `scripts/tests/test_build_theme.py`.
- [x] Create `src/Bosun.Panel/Theme/Motion.cs` reading `SystemParameters.ClientAreaAnimation`, and returning zero durations and `Animates` false when it is off. Done when: `tests/Bosun.Panel.Tests/MotionTests.cs` passes with the setting faked both ways, the durations read on being the generated theme's. Cheaper substitute: a panel-only animation setting that ignores Windows.
- [x] Update `Motion.cs` on `SystemParameters.StaticPropertyChanged` and raise `Changed`. Done when: a test turning the setting on, off and on again sees `Changed` raised once per change and every duration and `Animates` follow each time, with no restart (**Added 2026-11-16** from the plan review).
- [x] Assert the soft status tints in `tests/Bosun.Panel.Tests/ThemeContrastTests.cs`, compositing each `*Soft` over `Base`, `Surface` and `Raised`. Done when: each status word reads at 4.5:1 or better in both themes, and a stronger tint fails the test. Cheaper substitute: measuring the word on the plain surface only.
- [x] Extend `tests/Bosun.Panel.Tests/XamlRules.cs` and `XamlRulesTests.cs` to fail a literal non-zero `Duration`, `BlurRadius` or `CornerRadius` in a view or its code. Done when: a literal in a view, in a setter and in code each fails it, `0` and a resource pass, and the panel's own scan stays clean.
- [x] Commit: `theme: elevation and motion tokens`

**Test checkpoint:** `bosun run -- dotnet test --solution Bosun.slnx --filter-class "*MotionTests"` exits 0, and fails if the zero-duration path is removed; `python scripts/run-tests.py` passes `test_build_theme`.

> **Verified:** 2026-11-16 | §1 | 41c8c124d3bf3dcd0651165ca4ebb41606ba3e25
> **Review:** `docs/reviews/d02-t03-s1.json`

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

- [ ] Add the package `MahApps.Metro.IconPacks.Lucide` to `Directory.Packages.props` and `src/Bosun.Panel/Bosun.Panel.csproj` (**Added 2026-11-15** from `D02 T02 §2`, which held this with a test of `Section.Icon`; the map and its `GlyphTests` are here, so the package is too). Done when: the panel builds with it and `PackIconLucideKind` resolves in `Glyphs.cs`. Cheaper substitute: glyphs drawn from a font's characters.
- [ ] Create `src/Bosun.Panel/Theme/Glyphs.cs` mapping each page and shared action to a `PackIconLucideKind`, matching the mockup's choices. Done when: `tests/Bosun.Panel.Tests/GlyphTests.cs` proves every rail row has a glyph and each one is a defined kind. Cheaper substitute: glyph names typed into each view.
- [ ] Fail a `PackIconLucide` used outside the map in `tests/Bosun.Panel.Tests/XamlRulesTests.cs`. Done when: a direct kind in a view fails the test.
- [ ] Add the rail marker in `MainWindow.xaml`, animated with `Motion.Normal`. Done when: it lands on the selected row after keyboard and mouse navigation, and jumps at once with motion off. Cheaper substitute: a static highlight on the selected row.
- [ ] Draw each rail row's glyph from `Glyphs.cs` in the rail row template of `src/Bosun.Panel/Shell/ShellView.xaml` (**Added 2026-11-15** from `D02 T01 §4`, whose `NavList` style shows the row's content and leaves the glyph to this map): 16 px, `B.TextDim`, and `B.Accent` on the selected row. Done when: the off-screen render of the rail shows a glyph on every row. Cheaper substitute: a row of words only.
- [ ] Add `CountBadge` to the rail row template in `src/Bosun.Panel/Shell/ShellView.xaml` (**Corrected 2026-11-15:** this said `Theme/Controls.xaml`; the row template is built by `D02 T02 §4` in the shell's view, and `Controls.xaml` holds the row's style). Done when: a zero count shows nothing and a count of 3 renders.
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
- [ ] Use the segmented control for the theme on the Settings page, `src/Bosun.Panel/Settings/SettingsView.xaml` (**Added 2026-11-15** from `D02 T02 §5`, which draws the three-way choice as a combo box until this control exists; `design/SPEC.md` §19). Done when: the off-screen render of the page shows the three choices side by side, and a test moves between them with the arrow keys and reads the selected one.
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

> **Started:** 2026-11-16T03:10:22Z

Every page has a loading state under `design/SPEC.md` §1. A shared shape stops each page inventing its own spinner, and the delay stops a fast load flashing.

**Phase:** 2
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §1 page states, and §19 Skeleton and Page transition.
**Job:** a page change feels calm, and loading never looks like missing data.
**Treatment:** placeholder blocks shaped like the data, and a short fade with a 4 px rise. Cheaper substitute that fails: a spinner, or an empty table that looks like no data.
**Chrome:** the shell's page frame from `D02 T02 §4`.

**Corrected 2026-11-16:** checked against the tree before the build. The page frame (`src/Bosun.Panel/Shell/PageFrame.xaml`) and `PageBase` already draw the four states from `D02 T02 §4`, and `D02 T02 §8` gave pages their refresh: the failed state quotes the tool's message with Retry, and Retry runs the refresh once (`tests/Bosun.Panel.Tests/Shell/PageTests.cs:94`, `tests/Bosun.Panel.Tests/Shell/RefreshTests.cs:277`), and `PageBase.ShowEmpty` refuses an empty state without a reason or an action (`PageTests.cs:49`). Those two items are ticked with that evidence. What the Loading state shows today is the word Loading, which the frame's comment hands to this section. The decisions, each with what reversing it costs:

- The skeleton is the page's own shape: `PageBase.LoadingShape` (`Card`, the default, `Table` or `Gauge`), drawn by `Skeleton` in the frame's Loading state as blocks of `B.Raised` on `B.Surface` with a `B.Line` border and the `R.Card` radius, under a dim `Loading` caption, as the mockup's `.sk` blocks are (`design/mockups/control-panel.html:53`). The mockup's moving shimmer is not built: no token gives its speed, design/SPEC.md §19 asks only for the shape and the delay, and a shimmer redraws the window 60 times a second for as long as a load lasts. Reversal cost: one animation through `Motion`, when a token for it exists.
- The 150 ms delay is the skeleton's own (`Skeleton.Delay`, a dependency property): it starts when the Loading state becomes visible and draws nothing until it ends, so a load that ends sooner never shows it. The renders set the delay to zero through the frame (`PageFrame.SkeletonDelay`), since a picture can't wait.
- The skeleton says `Loading <page title>` once per load, when it appears, through the live region helper (`src/Bosun.Panel/Accessibility/LiveRegion.cs`), and carries `AutomationProperties.ItemStatus` `Loading`. A load too fast to show the skeleton says nothing.
- The page transition takes its length and easing from `Motion` (`D02 T03 §1`), the first consumer of the gate: the application puts its one `Motion`, made over `WindowsAnimation`, in its resources under `Motion.Gate`, and the frame finds it there. A frame with no gate (an off-screen render, a test that gives none) changes pages at once. The frame fades its content in from 0 and raises it 4 px over `Motion.Normal` when its page changes (navigation), never when the same page's state or data changes, and never the rail, which is outside the frame. Motion turned off during a transition ends it at once (`Motion.Changed`). Reversal cost: the lookup.
- A value that can't be read is `NotAvailable`, a record in `src/Bosun.Panel/Shell/NotAvailable.cs` holding its reason (a blank reason refused), drawn by a `DataTemplate` in `src/Bosun.Panel/Theme/Controls.xaml` as `Not available` with the reason under it; a `ContentControl` with the `Value` style draws a null as `Not available`, never as nothing or 0. A null can't carry a reason, so a page that knows why gives a `NotAvailable`.
- Stale data is the page's to say, the frame's to draw: `PageBase.ShowLastKnown(string why, DateTimeOffset readAt)` keeps the data on screen and sets a line the frame draws above it in `B.Warn`, `<why>. Showing what was read <age> ago.`, the age in words from the page's clock (`PageBase.Clock`, `TimeProvider.System` by default); `ShowData()` clears it. The pages that read WSL use it (`D03 T02 §2`); here a test page stands for one with WSL stopped.

**Corrected 2026-11-16 (plan review):** the plan review in `docs/reviews/02-shell/D02-T03-s7.md` returned six gaps (3 P1, 3 P2), planned in below. The skeleton and the transition run only while their page is on screen (`PageBase.IsShown`, which is false for a page navigated away from, in a minimised window or in one hidden to the tray): a delay that has not ended is cancelled and says nothing, a transition is ended at once, and a page shown again starts afresh. The skeleton's delay is timed by the frame's `TimeProvider` (`TimeProvider.System` in the application), so a test moves time itself and proves 149 ms shows nothing and 150 ms shows the blocks. The application installs the gate through `Motion.Install(ResourceDictionary, WindowsAnimation)`, which starts listening to Windows and puts the gate under `Motion.Gate`, and disposes it at exit; a test goes through that call. A null value is drawn as `Not available` with `It could not be read.` under it, so the explanation is never missing; a page that knows more gives a `NotAvailable` with its reason. The stale line's age is worked out again on each `ShowLastKnown` (each refresh), when the page comes back on screen and when the person comes back to the window, with no timer of its own.

**Build order:**

1. Create `src/Bosun.Panel/Controls/Skeleton.cs`. Done when: it appears only after 150 ms.
2. Extend the page frame in `src/Bosun.Panel/Shell/PageFrame.xaml`. Done when: content changes animate and the rail does not.

- [x] Create `Skeleton` in `src/Bosun.Panel/Controls/Skeleton.cs` with `Card`, `Table` and `Gauge` shapes, and `PageBase.LoadingShape` choosing one. Done when: off-screen renders show each shape in both themes, in `RenderCatalog.cs`, and the frame's Loading state draws the page's shape. Cheaper substitute: a spinner, or the word Loading alone.
- [x] Delay showing the skeleton by 150 ms in the page frame, timed by its `TimeProvider`. Done when: a test with a 50 ms load never shows it and one with a 500 ms load does, with time moved by the test it is not drawn at 149 ms and is at 150 ms, and a second load waits its own 150 ms (**Added 2026-11-16** from the plan review). Cheaper substitute: a skeleton that flashes on each refresh.
- [x] Mark the skeleton busy through `AutomationProperties.ItemStatus` and the live region, once per load. Done when: a test sees one `Loading <title>` announcement for a slow load and none for a fast one. Cheaper substitute: placeholder blocks a screen reader announces as nothing.
- [x] Keep current data on a background refresh in `PageFrame.xaml`. Done when: a test proves a 5 s poll whose load takes 500 ms never shows the skeleton over existing data, the existing values stay drawn throughout, and the new values are drawn when it ends (**Added 2026-11-16** from the plan review). Cheaper substitute: a skeleton on every poll.
- [x] Add the page transition with `Motion.Normal` and `Motion.Ease` in `PageFrame.xaml.cs`, through the application's `Motion.Gate`. Done when: a test proves opacity and a 4 px offset animate only on navigation, not on a state or data change, apply at once with motion off, and end at once when motion is turned off during one. Cheaper substitute: a storyboard with its own duration.
- [x] Run the skeleton and the transition only while their page is on screen, in `Skeleton.cs` and `PageFrame.xaml.cs`. Done when: a test navigating away, minimising and hiding to the tray during the delay and during a transition sees no announcement, no skeleton and no animation clock afterwards, and the page shown again starts its delay or shows its content afresh, with no old callback touching the new page (**Added 2026-11-16** from the plan review). Cheaper substitute: timers left running on a hidden page.
- [x] Install the gate in the application through `Motion.Install` in `src/Bosun.Panel/Theme/Motion.cs`, called by `App.MakeWindow` and disposed in `App.OnExit`. Done when: a test through `Motion.Install` finds the gate under `Motion.Gate` from a frame in those resources, sees it listening to Windows, follows a setting change through it, and stops listening when it is disposed (**Added 2026-11-16** from the plan review). Cheaper substitute: a gate only tests give, so the application falls back to no motion.
- [x] Add a failed state with the tool's message and Retry to `PageFrame.xaml`. Done when: a test with a failing fake shows the message, and Retry calls the page's refresh once. Cheaper substitute: a generic error line. **Shipped** by `D02 T02 §4` and `§8`: `PageFrame.xaml` (`Failed`), `PageTests.cs:94`, `RefreshTests.cs:277`.
- [x] Add an empty state slot to `PageFrame.xaml`, with each page giving why and what to do. Done when: a test proves a page can't declare empty with no text. **Shipped** by `D02 T02 §4`: `PageFrame.xaml` (`Empty`), `PageBase.ShowEmpty`, `PageTests.cs:49`.
- [x] Add `NotAvailable` in `src/Bosun.Panel/Shell/NotAvailable.cs`, its template with its reason and the `Value` style to `src/Bosun.Panel/Theme/Controls.xaml`. Done when: a `NotAvailable` shows "Not available" and its reason, a null shows "Not available" and "It could not be read.", never 0 or nothing, and a blank reason is refused (**Corrected 2026-11-16** from the plan review: a null with no explanation broke design/SPEC.md §1). Cheaper substitute: a 0 or an empty cell.
- [x] Show a stopped distro's last data with its age through `PageBase.ShowLastKnown` and a line in `PageFrame.xaml`. Done when: a test page with WSL stopped reads `WSL is stopped. Showing what was read 12 minutes ago.` over its data, reads a later age after its clock moves on and the next refresh, after it is hidden and shown again, and after the person comes back to the window, and `ShowData()` clears the line (**Added 2026-11-16** from the plan review). Cheaper substitute: old data shown as if current.
- [x] Commit: `shell: skeleton and page transitions`

**Test checkpoint:** `bosun run -- dotnet test --solution Bosun.slnx --filter-class "*SkeletonTests" --filter-class "*PageFrameTests"` exits 0, and fails if the 150 ms delay is removed; `pwsh scripts/render-pages.ps1` draws the skeleton's three shapes in both themes.

> **Verified:** 2026-11-16 | §7 | 6550fd8d70a8ab26dce5919689144a8eebea7549
> **Review:** `docs/reviews/d02-t03-s7.json`

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
- [ ] Register the Settings page's Save and Discard in the palette (**Added 2026-11-15** from the plan review of `D02 T02 §5`, which builds them as buttons before the palette exists). Done when: a test finds both in the palette on the Settings page, each disabled with its reason while there is nothing to save, and each running the same save and discard as the buttons, with one line in the Activity log. Cheaper substitute: an action the keyboard can't reach from Ctrl+K.
- [ ] Register the tray's Pause notifications for 1 hour, Resume notifications and Exit as commands in the palette (**Added 2026-11-15** from the completion review of `D02 T02 §7`, whose menu is the only way to them). Done when: a test finds each by typing its name, runs it, and reads the same result as the tray's menu line: the pause set with its Activity line, the pause ended, and Exit going through the question about unsaved edits. Cheaper substitute: commands only a mouse on the tray can reach.
- [ ] Register the page's Refresh as a command in the palette, with F5 as its key (**Added 2026-11-15** from the plan and completion reviews of `D02 T02 §8`, which builds the header's Refresh button). Done when: a test finds Refresh for the page on screen, runs it from the palette and with F5, and reads the same command as the button's: one extra call, joined to a run under way, with its Activity line; and no entry, and no key, for a page that has no refresh. Cheaper substitute: a refresh only the mouse can reach.
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

## 10. High contrast theme and renders

Filed 2026-11-16 by the completion review of `D02 T03 §7` (`docs/reviews/02-shell/D02-T03-s7.md`): this file's Outcome promises every component off screen in dark, light and high contrast, and no section built high contrast. Today `ThemeService` (`src/Bosun.Panel/Theme/ThemeService.cs`) knows only the two generated themes, and `PageRenderer.Themes` (`tests/Bosun.Panel.Tests/Render/PageRenderer.cs:56`) draws only Dark and Light. A person who turns on a Windows contrast theme gets the panel's own colours, which is what Windows' contrast themes exist to replace.

**Phase:** 2
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §1 (contrast, states) and §19 (the backdrop is solid in high contrast); Windows' own contrast themes for the colours.
**Job:** a person who needs a contrast theme reads every page, state and control in the colours they chose.
**Treatment:** the theme's keys (`B.*`, `C.*`) mapped to Windows' system colours (`SystemColors`: window, window text, highlight, highlight text, gray text, hot track, button face), with the soft tints, shadows and the skeleton's fills taken to the plain window colour and borders made visible. Cheaper substitute that fails: the dark theme with a stronger accent.
**Chrome:** the generated theme slot `ThemeService` fills, and every view that draws through it.

**Corrected 2026-11-16:** checked against the tree before the build. Views name the colour keys as `Color`s (`C.*`) as well as brushes (`B.*`), and a `Color` resource can't follow a system colour by itself, and a hand-written `HighContrast.xaml` holding the `R.*`, `S.*`, `T.*` and `Motion.*` keys would copy what the generator owns and drift from it. So the decisions, each with what reversing it costs:

- The contrast colours are a dictionary made in code, `HighContrastTheme.Build(SystemPalette)` in `src/Bosun.Panel/Theme/HighContrastTheme.cs`, from Windows' system colours of the moment (`SystemPalette.Current()`, read from `SystemColors`): every `C.*` and `B.*` key of the generated themes (brushes frozen), and the `Elevation.*` shadows at no opacity. It holds nothing else. `ThemeService` merges it over the generated Dark theme, which still gives the measures and the motion, and builds it again when Windows' colours change (`SystemEvents.UserPreferenceChanged`, category `Color`). Reversal cost: one class.
- The mapping (Windows' own guidance for contrast themes: text on window, highlight for selection, gray text only for what is disabled): `Base`, `Surface`, `Raised`, `Hover` and every `*Soft` tint are the window colour; `Text`, `TextDim`, `TextMute`, `Line`, `LineStrong` and the four status colours are the window text colour (a contrast theme has one text colour, and a status still says its word); `Accent` and `AccentDim` are the highlight colour, and `OnAccent` the highlight text colour.
- The renders draw in a fixed palette, Windows 11's Night sky contrast theme, so a picture is the same on every machine; `PageRenderer.Themes` gains `HighContrast`, and the file names `<view>.<state>.highcontrast.png` (the renders' name rule and `scripts/render-pages.ps1` take the third theme).

**Corrected 2026-11-16 (plan review):** the plan review in `docs/reviews/02-shell/D02-T03-s10.md` returned four gaps (2 P1, 2 P2), planned in below. A colour dictionary can't change what the control templates do: they fade a disabled control by opacity (`src/Bosun.Panel/Theme/Controls.xaml:103`, `:182`, `:526`, `:576`, `:687`) and paint a selected item with `B.AccentDim` under the normal text colour (`:605`, `:808`). So contrast is also a mode the templates see: `HighContrastMode.IsOn`, an inherited attached property in `src/Bosun.Panel/Theme/HighContrastMode.cs`, which `ThemeService` sets on the window while the contrast colours are in force (and the renders on their page), and which reaches a combo box's drop-down through its logical tree. Under it the templates add contrast-only triggers, and nothing changes in the Dark and Light themes: a disabled control keeps full opacity and takes Windows' gray text (`B.GrayText`, which only the contrast dictionary holds), and a selected or highlighted item and text selection take the highlight colour with the highlight text colour (`B.Accent` and `B.HighlightText`). The contrast colours are applied on the UI thread whichever thread Windows speaks on. The proof is on live controls, not the dictionary: a control already on screen repaints, the Settings line comes and goes live, and a light contrast palette (Windows 11's Desert) is tested beside Night sky.

**Build order:**

1. Add `HighContrastTheme` in `src/Bosun.Panel/Theme/HighContrastTheme.cs`. Done when: it builds every colour key from a system palette.
2. Switch to it in `ThemeService`. Done when: high contrast on draws it, off draws the chosen theme, live.
3. Draw the renders in it. Done when: every registered view is a picture in high contrast too.

- [ ] Add `HighContrastTheme.Build(SystemPalette)` and `SystemPalette.Current()` in `src/Bosun.Panel/Theme/HighContrastTheme.cs`, building every `C.*` and `B.*` key of the generated themes from the palette by the mapping above, brushes frozen, and the `Elevation.*` shadows at no opacity. Done when: a test proves the dictionary holds exactly the generated theme's colour and elevation keys, each colour the palette's one the mapping names, and merged over the Dark theme every key the views use resolves. Cheaper substitute: a third generated palette of fixed colours.
- [ ] Choose it in `ThemeService` while `SystemParameters.HighContrast` is true, whatever the person's theme choice, merged over the Dark theme, and go back to their choice when it turns off, from `SystemParameters.StaticPropertyChanged`; build it again when Windows' colours change. Done when: a test with the setting faked on, off and on again sees the dictionaries swapped each time with no restart, a colour change rebuilds it, and the person's stored choice is unchanged. Cheaper substitute: high contrast read once at start.
- [ ] Say in the Settings page's theme line, while high contrast is on, that Windows' contrast theme is in use and the choice applies when it is off. Done when: a render of Settings in high contrast shows the line. Cheaper substitute: a theme box that seems to do nothing.
- [ ] Add `HighContrastMode.IsOn` in `src/Bosun.Panel/Theme/HighContrastMode.cs`, an inherited attached property, set by `ThemeService` on the window while the contrast colours are in force, and give the contrast dictionary `B.GrayText` and `B.HighlightText`. Done when: a test sees it true on the window and on a combo box's open drop-down while contrast is on, and false after. Cheaper substitute: templates reading `SystemParameters.HighContrast`, which a render on a machine without contrast can't show.
- [ ] Add contrast-only triggers for disabled controls to every template in `src/Bosun.Panel/Theme/Controls.xaml` that fades one (buttons, check box, field, combo box, list and menu items): full opacity, `B.GrayText` for the text, glyph and border. Done when: a test on live disabled controls in contrast reads opacity 1 and the gray text colour on each, and the same controls in Dark keep their 0.4 fade. Cheaper substitute: the 0.4 fade over the contrast colours, a colour the person never chose.
- [ ] Add contrast-only triggers for selected and highlighted items and text selection in `Controls.xaml` (combo box items, list and rail items, table rows and cells, a field's `SelectionBrush` and `SelectionTextBrush`): the highlight colour behind, the highlight text colour on top. Done when: a test on a live selected combo item, rail row and table row in contrast reads the highlight text colour on the content's text, and removing one trigger fails it. Cheaper substitute: the highlight behind the window text colour.
- [ ] Apply the contrast colours on the UI thread when Windows speaks on another, in `ThemeService`. Done when: a test raising the change on a worker thread sees the swap happen on the dispatcher's thread. Cheaper substitute: a swap on Windows' thread, which WPF refuses.
- [ ] Prove the live paths in `tests/Bosun.Panel.Tests/HighContrastTests.cs`: a control already on screen repaints in the contrast colours and back, the Settings line appears and goes as contrast turns on and off, saving another theme while contrast is on keeps contrast and gives the new theme when it turns off, a start with contrast already on draws contrast at once, and Follow Windows is followed again after. Done when: each passes, with Windows 11's Night sky and Desert palettes. Cheaper substitute: a test of the dictionary swap alone.
- [ ] Add `HighContrast` to `PageRenderer.Themes`, the renders' file name rule and `scripts/render-pages.ps1`, drawing with Windows 11's Night sky palette so a picture is the same on every machine. Done when: `pwsh scripts/render-pages.ps1` writes every view in high contrast, and `tests/render_pages_test.ps1` passes with three themes. Cheaper substitute: renders only of the two own themes.
- [ ] Check the skeleton, the `NotAvailable` and `Value` texts, the stale line, the focus ring and an open combo box in the high contrast renders against design/SPEC.md §1, adding render cases for a focused control and an open drop-down with a highlighted and a selected item. Done when: each is visible and distinct in the pictures, a skeleton block's border differs from the window colour, a test reads the focus ring's border in the highlight colour on a focused control, and `ThemeContrastTests` passes the contrast theme's text on its window colour in both palettes. Cheaper substitute: looking at one page.
- [ ] Commit: `theme: high contrast`

**Test checkpoint:** `bosun run -- dotnet test --solution Bosun.slnx --filter-class "*HighContrastTests"` exits 0 and fails if `ThemeService` ignores `SystemParameters.HighContrast`; `pwsh scripts/render-pages.ps1` writes every view in three themes.

## Verification

- Off-screen renders of every component in dark, light and high contrast were reviewed against `design/SPEC.md` §19.
- `bosun run -- dotnet test Bosun.slnx` passes.
- `python scripts/todo-graph.py validate` and `python scripts/todo-graph.py plan --check` pass.
