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
| 2 | §2 | Mica backdrop with a solid fallback | D02 T02 §4 | [x] |
| 3 | §3 | Lucide icon map and rail marker | §1, D02 T02 §4 | [x] |
| 4 | §4 | Status pill and segmented control | §1, D02 T01 §4 | [x] |
| 5 | §5 | Gauge, sparkline and stacked bar | §4 | [x] |
| 6 | §6 | Queue slot visuals | §5 | [x] |
| 7 | §7 | Skeleton and page transitions | §1, D02 T02 §4 | [x] |
| 8 | §8 | Command palette | §3 | [x] |
| 9 | §9 | In-window toasts | §4, D02 T02 §4 | [x] |
| 10 | §10 | High contrast theme and renders | §1, §7 | [x] |
| 11 | §11 | Contrast preview and its smoke on the shown window | §10, D02 T02 §10 | [ ] |
| 12 | §12 | Window placement remembered, and the smoke off the main screen | D02 T02 §4 | [x] |

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

> **Started:** 2026-11-16T09:49:32Z

Windows composes Mica outside the process, so it costs the panel almost nothing, but it only exists on some systems and must be dropped live when the operator turns transparency off or enters high contrast.

**Corrected 2026-11-16:** checked against the tree before the plan review. The title bar, the rail and the rounded sheet are `ShellView.xaml`'s (`D02 T02 §4`), not `MainWindow.xaml`'s, which holds only the `WindowChrome`; the sheet itself shipped there in `ad5b0bf` with `R.Sheet` and the rail outside it, and reads as one surface today only because the chrome behind it is `Base` too, so its item is ticked and what is left of it is the chrome going clear under Mica. A test never writes the person's own `EnableTransparency`: the setting is read through a seam, and the live switch is proved by sending the real window `WM_SETTINGCHANGE` with the seam flipped. High contrast comes from `WindowsHighContrast` (`D02 T03 §10`'s, already live), and the theme from `ThemeService`. The tests go in `tests/Bosun.Panel.Tests/Shell/`, and the checkpoint is in the test platform's own filter form.

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

- [x] Add `BackdropPolicy.Choose` in `src/Bosun.Panel/Shell/BackdropPolicy.cs`, taking the build number, transparency and high contrast. Done when: `tests/Bosun.Panel.Tests/Shell/BackdropPolicyTests.cs` gets Mica only at build 22621 or later with transparency on and high contrast off.
- [x] Read `EnableTransparency` under `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize` in `src/Bosun.Panel/Shell/Backdrop.cs`. Done when: a missing value reads as on, matching Windows, in a test seam. Cheaper substitute: assuming transparency is always on.
- [x] Set `DWMWA_SYSTEMBACKDROP_TYPE` to `DWMSBT_MAINWINDOW` in `src/Bosun.Panel/Shell/Backdrop.cs`, with the frame extended and the `HwndSource` background transparent, attached once the window's handle exists (`SourceInitialized`) by `App.MakeWindow` with the application's `ThemeService`, `WindowsHighContrast` and Activity log, whether the window opens shown or in the tray; in Mica both `MainWindow`'s own background (`B.Base` today) and `ShellView`'s are cleared, and in solid both are `Base` again (plan review, P1). Done when: `DwmGetWindowAttribute` reads back the value on the real window started as the application starts it, shown and from the tray, and the window's and the shell's backgrounds are clear. Cheaper substitute: an image of a wallpaper behind the window, or clearing the shell while the window still paints over Mica.
- [x] Set `DWMWA_USE_IMMERSIVE_DARK_MODE` from the active theme in `Backdrop.cs`. Done when: read-back follows a live theme change in both directions.
- [x] Draw solid `Base` under the title bar and rail when the policy says solid, and nothing (the window clear, for Mica) when it says Mica, through `ShellView`'s background in `src/Bosun.Panel/Shell/ShellView.xaml`, the sheet staying solid `Base` in both. Done when: off-screen renders on a transparent host (not `PageRenderer`'s `Base` behind everything, plan review P1) show, with the policy forced solid, the theme's `Base` at the title bar's and the rail's pixels in each theme; with it forced to Mica, those pixels clear and the sheet's still opaque `Base`; both states are `RenderCatalog` cases (solid is `shell.overview`, the shell as it starts, since an identical picture is refused as a second state; Mica is `shell.backdrop-mica`), and the tests fail with the fallback taken out. Cheaper substitute: leaving the window transparent, which paints black or stale pixels.
- [x] Reapply the policy on `WM_SETTINGCHANGE` and `SystemParameters.StaticPropertyChanged` in `Backdrop.cs`, and on `WindowsHighContrast`'s change, which can arrive on a worker thread and is taken to the window's dispatcher; every hook and subscription is let go when the window closes (plan review, P2). Done when: a test flips the transparency seam and sends the real window `WM_SETTINGCHANGE`, and the window switches with no restart, both ways; a contrast change raised on a worker thread turns it solid, and off again returns it to Mica where it is eligible; and after the window closes a change reaches nothing. Cheaper substitute: writing the person's own registry value, which a crash would leave changed.
- [x] Add the rounded sheet with `Radius.Sheet` in `MainWindow.xaml`. Done when: the off-screen render in both themes shows the top-left corner rounded and the rail outside the sheet. (Shipped by `D02 T02 §4` in `ShellView.xaml`, `ad5b0bf`: `Border x:Name="Sheet"`, `CornerRadius="{DynamicResource R.Sheet}"`, in the rail's right column.)
- [x] Log a failed DWM call as one WARN line in the Activity pane, and fall back to solid in `Backdrop.cs`. Done when: a test seam returning a failure HRESULT leaves the window solid with one WARN line. Cheaper substitute: ignoring the HRESULT.
- [x] Commit: `shell: Mica backdrop with a solid fallback`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*Backdrop*"` exits 0, and fails if the high contrast rule is removed from `BackdropPolicy.Choose`.

> **Verified:** 2026-11-16 | §2 | 1c3fa62dd4d7a0f025890caf22a7c3ac3aa06014
> **Review:** `docs/reviews/d02-t03-s2.json`

## 3. Lucide icon map and rail marker

> **Started:** 2026-11-16T11:33:39Z

One map keeps glyphs consistent and lets a test catch a kind the icon pack does not have.

**Corrected 2026-11-16:** checked against the tree before the plan review. The rail, its row template and its two empty slots (`Glyph`, `Badge`) are `ShellView.xaml`'s (`D02 T02 §4`), not `MainWindow.xaml`'s; the selected row's marker today is a static bar each row draws in the `NavList` style (`Theme/Controls.xaml`), so the sliding marker is one element over the rail in `ShellView`, and the static bar goes. Nothing gives a page a count yet: `PageBase.Waiting` is added, 0 by default, for the badge and the row's spoken name to follow, and each page sets its own when it is built (`D03 T02 §3`, the queue's waiters). The glyphs are the mockup's (`design/mockups/control-panel.html`, its `pages` table): Overview `layout-dashboard`, Memory `memory-stick`, Instances `terminal`, Queue `list-ordered`, Optimise `gauge`, Settings `settings`, About `info`; the shared actions are the mockup's too (refresh `refresh-cw`, open folder `folder-open`, open terminal `square-terminal`, pause `pause`, search `search`, and the status icons `circle-check`, `triangle-alert`, `octagon-alert`). The package is pinned at 6.2.1, its latest. The tests go in `tests/Bosun.Panel.Tests/Shell/`, and the checkpoint is in the test platform's own filter form.

**Phase:** 2
**Needs:** display-session
**Surface:** yes
**Fidelity:** `design/SPEC.md` §1 Rail, and §19 Glyphs and Count badge.
**Job:** the operator finds a page by shape and sees at a glance which pages have something waiting.
**Treatment:** a glyph per rail row, a sliding marker and count badges. Cheaper substitute that fails: emoji or font characters for icons.
**Chrome:** the rail from `D02 T02 §4`.

**Build order:**

1. Create `src/Bosun.Panel/Theme/Glyphs.cs`. Done when: every rail row and shared action resolves through it.
2. Extend `src/Bosun.Panel/Shell/ShellView.xaml`. Done when: the marker slides between rows and badges show.

- [x] Add the package `MahApps.Metro.IconPacks.Lucide` to `Directory.Packages.props` and `src/Bosun.Panel/Bosun.Panel.csproj` (**Added 2026-11-15** from `D02 T02 §2`, which held this with a test of `Section.Icon`; the map and its `GlyphTests` are here, so the package is too). Pinned in `Directory.Packages.props` at 6.2.1. Done when: the panel builds with it and `PackIconLucideKind` resolves in `Glyphs.cs`. Cheaper substitute: glyphs drawn from a font's characters.
- [x] Create `src/Bosun.Panel/Theme/Glyphs.cs` mapping each page and shared action to a `PackIconLucideKind`, matching the mockup's choices. Done when: `tests/Bosun.Panel.Tests/Shell/GlyphTests.cs` proves each rail row (`Sections`) and each shared action has exactly the kind named above, and that the rendered rows draw their own mapped kind (plan review: a map giving every page one kind would otherwise pass). Cheaper substitute: glyph names typed into each view.
- [x] Fail a `PackIconLucide` used outside the map in `tests/Bosun.Panel.Tests/XamlRulesTests.cs`. Done when: a direct kind in a view fails the test.
- [x] Add the rail marker (**Corrected 2026-11-16, while building:** in the `NavList` template's own `PART_Marker`, moved by `Theme/RailMarker.cs`, rather than in `ShellView.xaml`, so every rail has it and the style's tests and renders see it; the keyboard and mouse moving the selection are the smoke's `RailTests` and `KeyboardTests`, since off screen a row can't take the focus a click needs) in `ShellView.xaml`, one element over the rail moved to the selected row's position, animated with `Motion.Normal`, and take the static bar out of the `NavList` style. It runs through the installed motion gate (`Motion`) with its easing, finishes at once when animation effects turn off mid-slide, and lets its clock and every subscription go when the window hides, minimises or unloads. Done when: a test sees it part-way between two rows during the normal duration and on the new row after it; on the selected row after keyboard and mouse navigation, programmatic navigation, and after the window is resized; still on the current row when navigation is refused (unsaved edits kept); at once with motion off and when motion turns off mid-slide; and drawn in every theme, contrast included. Cheaper substitute: a static highlight on the selected row.
- [x] Draw each rail row's glyph from `Glyphs.cs` in the rail row template of `src/Bosun.Panel/Shell/ShellView.xaml` (**Added 2026-11-15** from `D02 T01 §4`, whose `NavList` style shows the row's content and leaves the glyph to this map): 16 px, `B.TextDim`, and `B.Accent` on the selected row; in a contrast theme, whose selected row is filled with the highlight, `B.HighlightText` there (plan review). Decorative, as §19 says: neither the glyph nor the marker is a keyboard stop or announced on its own. Done when: the off-screen render of the rail shows its mapped glyph on every row; the selected row's glyph is the highlight's text colour in both contrast palettes, and follows a live theme change; and a UI Automation walk finds no element for the glyph or the marker. Cheaper substitute: a row of words only.
- [x] Add `PageBase.Waiting` (`src/Bosun.Panel/Shell/PageBase.cs`), 0 by default and raising its change, the count a rail row shows. Done when: a test page's count reaches its row live.
- [x] Add `CountBadge` to the rail row template in `src/Bosun.Panel/Shell/ShellView.xaml` (**Corrected 2026-11-15:** this said `Theme/Controls.xaml`; the row template is built by `D02 T02 §4` in the shell's view, and `Controls.xaml` holds the row's style). Done when: a zero count shows nothing and a count of 3 renders.
- [x] Set each rail row's automation name to its title and its count in words, the badge itself not announced again. Done when: a UI Automation test takes one row through 0, 3, 1 and 0, reading "Queue", "Queue, 3 waiting", "Queue, 1 waiting" and "Queue", with the badge showing 3, 1 and then gone. Cheaper substitute: a badge only sighted users can read.
- [x] Commit: `shell: Lucide icon map, rail marker and badges`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*GlyphTests" --filter-class "*RailMarkerTests" --filter-class "*CountBadgeTests"` exits 0, and fails with a rail row left unmapped, a marker that jumps with motion on, or a badge that stays at 0.

> **Verified:** 2026-11-16 | §3 | fcececdf13ebfe8bb2d39c40a39e1391213453b2
> **Review:** `docs/reviews/d02-t03-s3.json`

## 4. Status pill and segmented control

> **Started:** 2026-11-16T13:01:13Z

These two appear on nearly every page. Status keeps its word, so a pill without one must not compile into a page.

**Corrected 2026-11-16:** checked against the tree before the plan review. The icon map (`D02 T03 §3`) has the Ok, Warn and Fail status glyphs; Info and Neutral are added there, as the mockup draws them (Info `circle-dot`, Neutral its idle `pause`; corrected while building, this said `info` and `circle-dot`). "Throws in Debug, Unknown in Release" can't be proved by one build, and a missing word can't be a compile error in XAML: a `StatusPill` written in a view without a `Word` fails the XAML rules test (`tests/Bosun.Panel.Tests/XamlRules.cs`), which is how "must not compile into a page" is held, and at run time an empty or blank word always reads "Unknown". The theme choice the Settings page draws today is the `ThemeBox` combo box, whose tests name it. The tests go in `tests/Bosun.Panel.Tests/Controls/`, and the checkpoint is in the test platform's own filter form.

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

- [x] Create `StatusPill` in `src/Bosun.Panel/Controls/StatusPill.cs` with `Kind`, `Word` and a Lucide glyph from `Glyphs.cs`. Done when: `tests/Bosun.Panel.Tests/Controls/StatusPillTests.cs` renders Ok, Warn, Danger, Info and Neutral in both themes and in contrast, each with its glyph from the map and its word reading at 4.5:1 or better on `Base`, `Surface` and `Raised` (design/SPEC.md §19); a pill whose `Kind` and `Word` change while it is shown changes its glyph, tint, word and name, into and out of "Unknown"; the glyph is `Decorative` (no stop, not said on its own); and each kind is a `RenderCatalog` case, looked at in every theme (plan review). Cheaper substitute: a coloured dot with a tooltip.
- [x] Make a missing `Word` an error: a `StatusPill` in a view with no `Word` fails `XamlRulesTests`, and an empty or blank word reads "Unknown" in `StatusPill.cs`. Done when: the rules test fails a pill written without a word and passes one with it, and a test sets the word to "" and "  " and reads "Unknown" on screen and in its name. Cheaper substitute: a pill showing only a colour.
- [x] Expose `Word` as the automation name in `StatusPill.cs`. Done when: a UI Automation test reads the word.
- [x] Create `SegmentedControl` in `src/Bosun.Panel/Controls/SegmentedControl.cs` on `ListBox`, with the selected indicator using `Motion.Fast` through the installed motion gate: it lands at once with animation effects off or turned off mid-change, and lets the gate go when hidden or unloaded (plan review). In a contrast theme the selected segment is the highlight with its text, the focus ring and a disabled segment Windows' own, in both palettes and following a live switch. Done when: `tests/Bosun.Panel.Tests/Controls/SegmentedControlTests.cs` sees the indicator part-way and then on the new segment, at once with motion off and mid-change, and nothing held after unload; the contrast states in both palettes, live; and `RenderCatalog` cases for a selection, focus and a disabled segment in every theme. Cheaper substitute: a row of toggle buttons with no exclusive selection.
- [x] Support Left, Right, Home and End in `SegmentedControl.cs`. Done when: a UI Automation test moves and reads selection by keyboard only.
- [x] Hold each segment at 32 px or more in `src/Bosun.Panel/Theme/Controls.xaml`. Done when: the style walk from `D02 T01 §4` asserts it.
- [x] Use the segmented control for the theme on the Settings page, `src/Bosun.Panel/Settings/SettingsView.xaml`, and move the tests that named its combo box to it: `KeyboardTests`' closed combo box test (its own combo box elsewhere, or none, said), and `SettingsPageTests`' texts and name, keeping its binding, save and read-back, Discard and failure tests (plan review) (**Added 2026-11-15** from `D02 T02 §5`, which draws the three-way choice as a combo box until this control exists; `design/SPEC.md` §19). Done when: the off-screen render of the page shows the three choices side by side, and a test moves between them with the arrow keys and reads the selected one.
- [x] Commit: `controls: status pill and segmented control`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*StatusPillTests" --filter-class "*SegmentedControlTests" --filter-class "*SettingsPageTests"` exits 0, as does `dotnet test --project tests/Bosun.Panel.E2E --filter-class "*KeyboardTests" --filter-class "*SettingsKeyboardTests"`, and it fails if the empty word check is removed.

> **Verified:** 2026-11-16 | §4 | 8ad338cd4bc337b38011ae055d98a01ac86720eb
> **Review:** `docs/reviews/d02-t03-s4.json`

## 5. Gauge, sparkline and stacked bar

> **Started:** 2026-11-16T14:07:52Z

These are drawn from data and stay cheap: they draw once per data change and hold no timer. An unreadable value must never look like zero.

**Corrected 2026-11-16:** checked against the tree before the plan review, and given what the plan reviews of `D02 T03 §3` and `§4` asked of every surface section. The tests go in `tests/Bosun.Panel.Tests/Controls/` and the checkpoint is in the test platform's own filter form. Each control takes its colours from the theme's tokens (the gauge's arc and its state word the status roles `B.Ok`, `B.Warn`, `B.Danger`, its track `B.Line`; the sparkline `B.Accent`; the stacked bar's segments the theme's series colours), and in a contrast theme Windows' own (`B.Text`, `B.Accent` as the highlight, `B.GrayText` for an empty track), checked in both palettes. Each control is one element with a name a screen reader says in words; nothing inside is said again. Every state is a `RenderCatalog` case, looked at in every theme. Animation goes through the installed motion gate (`Motion`): it lands at once with animation effects off or turned off mid-change, and holds no clock when hidden or unloaded.

**Corrected 2026-11-16 (plan review):** the gauge takes a value and its total (`Maximum`); its arc is value over total, a value past the total fills it, and a missing, zero or non-finite total, or a negative or non-finite value, is "Not available" (rejected, never clamped). The sparkline is drawn for its current size and theme every time it is drawn, from the samples it holds, so a resize or a theme change redraws it with no new samples; with no samples it reads "Not available", with one it says the value with no trend. The stacked bar's segments take the theme's roles in a fixed order (`B.Accent`, `B.Info`, `B.Warn`, `B.Danger`, `B.Ok`, then `B.TextDim`), and a 2 px gap in `B.Base` always separates two segments, so they stay apart in a contrast theme where those roles become one colour; with no shares at all it reads "Not available", which is not all-zero shares.

**Corrected 2026-11-16 (while building):** three choices above changed against the mockup. The gauge's track is `B.Hover`, the mockup's, not `B.Line`. The stacked bar's segments carry no state colour, since a share of memory is not good or bad news: they take `B.Info`, `B.Accent`, `B.TextMute` and `B.TextDim` in turn, as the mockup draws Windows and WSL, and a share marked as the rest of the total (free memory) takes the quieter `B.LineStrong`; the earlier order (`B.Accent`, `B.Info`, `B.Warn`, `B.Danger`, `B.Ok`) drew free memory in the danger colour. The gap between two segments is left undrawn, so it is the colour behind the bar (`B.Base` on the page, where the test reads it). The sparkline writes "Not available" in `B.TextDim` where its line would be.

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

- [x] Create `RadialGauge` in `src/Bosun.Panel/Controls/RadialGauge.cs` with a nullable value, unit and state word. Done when: `tests/Bosun.Panel.Tests/Controls/GaugeTests.cs` compares the arc angle at 0, 50 and 100 percent of a real quantity against its total (7.4 GB of 32 GB), fills it for a value past the total, and reads "Not available" for a total that is missing, zero or not finite, the arc and the word in the state's colour (and Windows' own in both contrast palettes), its automation name the value, unit and word ("7.4 GB free, Safe"), and the gauge's states (a value in each state, and none) as `RenderCatalog` cases in every theme.
- [x] Draw a null value as an empty track with "Not available" in `RadialGauge.cs`. Done when: a test renders null and finds that text and no "0". Cheaper substitute: treating a missing value as 0.
- [x] Animate a gauge value change with `Motion.Slow` through the installed motion gate in `RadialGauge.cs`. Done when: a test sees the arc part-way during the slow duration and at the value after it; no animation clock with motion off, or after motion turns off mid-change, or once hidden or unloaded; and a value changed while shown changes its arc, word and name.
- [x] Create `Sparkline` in `src/Bosun.Panel/Controls/Sparkline.cs` drawing a frozen `StreamGeometry` built for its size from the samples it holds, oldest at the left. Done when: `tests/Bosun.Panel.Tests/Controls/SparklineTests.cs` reads the plotted points (evenly spaced, in order, scaled from the lowest sample at the bottom to the highest at the top, constant samples a flat line across the middle), sees the geometry rebuilt when the samples are replaced and when the control is resized, and the stroke follow a live theme change, and proves no redraw while nothing changes. Cheaper substitute: redrawing on a timer.
- [x] Set the `Sparkline` automation name to the latest value and direction in words, following new samples. Done when: a UI Automation test reads "12.4 GB, rising", then "11.0 GB, falling" and "11.0 GB, steady" as samples change, "11.0 GB" with one sample, "Not available" with none (drawing no line, and never the samples it had before), and the value again when samples return, and the sparkline (with too few samples to draw, and none) is a `RenderCatalog` case in every theme.
- [x] Create `StackedBar` in `src/Bosun.Panel/Controls/StackedBar.cs` with a legend giving each value in words. Done when: `tests/Bosun.Panel.Tests/Controls/StackedBarTests.cs` proves each segment's width is its share of the bar less the gaps (3, 1 and 4 parts of a bar as 3:1:4), the widths and gaps sum to the bar's width, the gap's pixels are `B.Base` in every theme, replacing the shares changes the widths, legend and name together, and the legend holds every value, each segment's swatch beside its name and value so colour is never the only key; a zero share draws no segment but keeps its legend line; all-zero shares draw an empty bar with every legend line at 0 (not held to the full width); no shares at all read "Not available", never a previous bar, until shares return; and the bar (shares, one share, and all zero) is a `RenderCatalog` case in every theme, contrast included. Cheaper substitute: a bar with colour as the only key.
- [x] Reject negative or non-finite samples in all three, showing "Not available" in place. Done when: a test with NaN and a negative value renders that text without an exception.
- [x] Commit: `controls: gauge, sparkline and stacked bar`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*GaugeTests" --filter-class "*SparklineTests" --filter-class "*StackedBarTests"` exits 0, and fails if a null value draws as 0.

> **Verified:** 2026-11-16 | §5 | e5de1ad8d2f9acdc98a55895f5fdbd6dce839fa7
> **Review:** `docs/reviews/d02-t03-s5.json`

## 6. Queue slot visuals

> **Started:** 2026-11-16T15:12:25Z

The queue is what the panel exists to show. It gets visuals of its own, fed entirely by tool JSON.

**Corrected 2026-11-16:** checked against the tree before the plan review. `bosun status --json` is already read and typed by `MemoryClient.StatusAsync` into `StatusDoc` (`src/Bosun.Core/Memory/MemoryDocs.cs`: `SlotDoc` with `busy` true, false or null and its `holder`, a `QueueRecordDoc` with `command`, `repo`, `side`, `phase` (`running`, `starting`, `waiting for guest memory` or null) and `running_s`; `waiters` oldest first with `waiting_for` (`slot` or `memory`), `min_avail_mb` and `waited_s`; `memory` with `available_mb` and `min_avail_mb`, the floor that holds slot 2). The controls take those documents and add no second model; the samples are a real `bosun status --json` captured from `build/Release/bosun.exe` and documents in its exact shape, read through `MemoryJson`, so a renamed field fails the test. A slot's states, each with its word: Free (`busy` false), Running, Starting and Waiting for guest memory (a holder's `phase`), Held (busy with no holder record, said as "holder not known"), and Not available (`busy` null); elapsed time in words ("45 s", "4 min", "1 h 12 min"), none when it can't be told. A held tile carries the mockup's 3 px moving bar on the motion gate's pulse duration, still with animation effects off, and stopped when hidden or unloaded. A waiter says why in words: a free slot, or memory, "slot 2 opens when 6.0 GB is free; 4.5 GB is free now", the available figure "not available" when unread. Each tile and each waiting line is one element a screen reader says in full; colours from the theme's tokens, and Windows' own in a contrast theme, checked in both palettes; every state a `RenderCatalog` case looked at in every theme. The tests go in `tests/Bosun.Panel.Tests/Controls/` and the checkpoint takes the test platform's filter form. The Queue page that places these (`D03 T02 §3`) owns reading the tool and its failure.

**Corrected 2026-11-16 (plan review):** the waiting row reads the document's `records` too: with `readable` false it says the queue's records could not be read (never "Nothing waiting"), and with `skipped` above 0 it keeps the jobs it has and says how many records could not be read. What can't be told is said, never guessed: a holder with no `phase` (an older record) is "Held", not Running; a waiter with no `waiting_for` waits "for a reason not recorded", and one with no floor never shows 0; a record whose `state` is `unknown` (its process could not be checked) says "may have ended". A memory-held waiter's floor is its own `min_avail_mb`, the limit its job was given, and the status's `memory.min_avail_mb` only when the waiter has none. Each tile names its slot ("Slot 1"), the holder's side ("Windows", "WSL"), repo (a null repo "no repo"), command, how long it waited for the slot and how long it has run; each waiting line its place, command, repo, side, why and how long it has waited; all of it seen and said.

**Corrected 2026-11-16 (while building):** the state's word sits in a `StatusPill` on its own line under the slot's number, not beside it: beside it, "Waiting for guest memory" squeezed "Slot 1" to "Slot" in the render. Free, held and unknown slots take glyphs added to the icon map (`SlotFree` the mockup's dot, `SlotHeld` its terminal, `SlotUnknown` a dashed circle). Times are in `QueueWords`, shared by the tile and the waiting row.

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

- [x] Create `QueueSlotTile` in `src/Bosun.Panel/Controls/QueueSlotTile.cs` showing free or holder, command and elapsed time. Done when: `tests/Bosun.Panel.Tests/Controls/QueueVisualTests.cs` renders each state (Free, Running, Starting, Waiting for guest memory, Held, Not available) in Dark, Light and both contrast palettes with its word and glyph, a running holder's slot, side, repo (and "no repo" for none), command, waited and running time, a holder with no phase "Held", a holder whose state is unknown "may have ended", its automation name the whole tile in words, a new document changing the tile in place, and the moving bar on the gate (moving, still with motion off, no clock hidden or unloaded). Cheaper substitute: colour showing busy or free.
- [x] Create `WaitingRow` in `src/Bosun.Panel/Controls/WaitingRow.cs` listing waiting jobs in order with why, including the memory floor for slot 2 in words. Done when: a test with a memory-held job shows its own floor and the available figure in words (two waiters with floors of their own, both apart from the status default, each showing its own), a slot-held job says it waits for a free slot, a waiter with no reason "for a reason not recorded" and none with no floor a 0, each line its place, command, repo, side and waited time, records that could not be read said as such (none read: never "Nothing waiting"; some skipped: the jobs kept and the count said), jobs keep the document's order with their waited time, an unread available figure says "not available" and never 0, no waiters say "Nothing waiting", and each line is said once by a screen reader.
- [x] Commit: `controls: queue slot visuals`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*QueueVisualTests"` exits 0, and fails if a slot state loses its word.

> **Verified:** 2026-11-16 | §6 | 418036a95a92522d6588e51014bb3f501d17c572
> **Review:** `docs/reviews/d02-t03-s6.json`

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

> **Started:** 2026-11-16T16:14:16Z

A keyboard route to every page and action. It adds no action of its own, so it must not become a way around a danger confirmation.

**Corrected 2026-11-16:** checked against the tree before the plan review. A page's commands are a new `PageCommand` list on `PageBase` (`src/Bosun.Panel/Shell/PageBase.cs`): a name, a glyph from `Glyphs.cs`, what it runs, the reason it is disabled (null while it may run) and, for a danger command, the confirmation its own button asks. `PageBase.RefreshNow` already exists, so a page with a refresh lists "Refresh" without writing it again. Leaving a page goes through `ShellViewModel.GoToAsync`, which asks the same Save, Discard or Stay question (`LeaveAsync`) the rail's `SelectionGuard` asks; the palette navigates only through it. The tray's Pause, Resume and Exit are `TrayViewModel.Menu()`'s items, reused as they are so their words and behaviour stay one (`TrayController.Tray`). No danger command exists in the panel yet (the Optimise page's changes bring the first): the palette's rule is proven with a test page whose danger command and button share one confirmation. Ctrl+K and F5 are taken at the window (`MainWindow`'s key handling), so they work with the focus in a text field. The overlay uses `R.Overlay` and `Elevation.Overlay`, Windows' colours in a contrast theme, the theme's motion through the gate, and every row is said in full by a screen reader. Tests: `tests/Bosun.Core.Tests/CommandIndexTests.cs`, `tests/Bosun.Panel.Tests/Shell/CommandPaletteTests.cs`, and the keyboard and focus checks in the end-to-end smoke (`tests/Bosun.Panel.E2E`, on the test screen); the checkpoint takes the test platform's filter form.

**Corrected 2026-11-16 (plan review):** the keyboard is the palette's whole contract: Ctrl+K opens it with the keyboard in its search field; typing filters and ranks, the first match selected; Up and Down move the selection, which follows a new filter; Enter runs the selected row, and a click runs the row clicked; with nothing matching it says "Nothing matches" and Enter does nothing. A row runs only through its own command, checked when it runs: a disabled one runs nothing and says its reason, a running `AsyncCommand` is not started twice, a row's availability follows its command live while the palette is open, and a command that throws reports its failure as its button does and can run again. The palette closes before anything it starts takes the keyboard: a danger command's own confirmation, which runs it once on Yes and not at all on No, never before the answer, and never answered by the Enter that chose it; and the Save, Discard or Stay question, asked only through `ShellViewModel.GoToAsync` (the rail's `SelectionGuard` asks the same through `LeaveAsync`), whose Save, a failed Save, Discard and Stay each leave the page, or keep it with its edits, as the rail does, and Exit's the same. While that question is up, Ctrl+K and F5 do nothing. The tray's Pause or Resume row is rebuilt from `TrayViewModel.Menu()` each time the palette opens and each time the pause starts, ends or runs out, so what it says is what it does. The Settings page lists every action it offers in its state: Save, Discard, its startup action (repair, read again, or open Windows' startup settings) and, when it failed to load, Retry. The overlay is `R.Overlay` with `Elevation.Overlay`; render cases cover results, the selection, a disabled row with its reason and nothing matching in Dark, Light and both contrast palettes; its search field and every row have names a screen reader says, with the selection and a disabled state; its open and close go through the motion gate, at once with animation effects off or turned off midway, and hold no clock once hidden.

**Corrected 2026-11-16 (while building):** the palette fades in on the motion gate and closes at once, so nothing it starts waits on an animation. Ctrl+K and F5 are taken at the window: with nothing focused, as just after the window opens, keys reach only the window, which the end-to-end test caught. Where the keyboard goes is proven only end to end, since an off-screen window takes neither keyboard nor logical focus. A pause running out is noticed by the tray on the panel's one refresh scheduler (`TrayViewModel.NoticePause`), not by a timer of the palette's own (the panel's rule, `RefreshRulesTests`). The renders found the shell view sending a shell bound on another page back to its first page; fixed at `ShellView.Bind` with its own test.

**Phase:** 2
**Needs:** display-session
**Surface:** yes
**Fidelity:** `design/SPEC.md` §19, Command palette.
**Job:** the operator reaches any page or current action without the mouse.
**Treatment:** a centred overlay with a search field and ranked rows. Cheaper substitute that fails: a menu bar.
**Chrome:** an overlay with `R.Overlay` and `Elevation.Overlay`, and glyphs from `Glyphs.cs`.

**Build order:**

1. Create `src/Bosun.Core/CommandIndex.cs`. Done when: ranked search works in a plain unit test.
2. Create `src/Bosun.Panel/Shell/CommandPalette.xaml`. Done when: Ctrl+K opens it on any page.

- [x] Create `CommandIndex` in `src/Bosun.Core/CommandIndex.cs` ranking prefix over word start over substring. Done when: `tests/Bosun.Core.Tests/CommandIndexTests.cs` proves the order.
- [x] Let each page register its actions in `src/Bosun.Panel/Shell/PageBase.cs`. Done when: a test proves only the current page's actions appear, with every page always listed.
- [x] Create the overlay in `src/Bosun.Panel/Shell/CommandPalette.xaml`, opened by Ctrl+K. Done when: a UI Automation test opens it from three pages, including with focus in a text field.
- [x] Route a danger action through that action's own confirmation in `CommandPalette.xaml.cs`. Done when: a test proves a danger command shows the same confirmation as its button. Cheaper substitute: a palette that runs the command directly.
- [x] Show a disabled action with its reason, and refuse to run it. Done when: a test with a disabled action shows the page's reason.
- [x] Restore focus to the prior element on Esc. Done when: a UI Automation test reads the focused element before and after.
- [x] Refuse navigation from the palette while the page has unsaved edits, through `SelectionGuard`. Done when: a test sees the Save or Discard offer from `design/SPEC.md` §1.
- [x] Register the Settings page's Save and Discard in the palette (**Added 2026-11-15** from the plan review of `D02 T02 §5`, which builds them as buttons before the palette exists). Done when: a test finds both in the palette on the Settings page, each disabled with its reason while there is nothing to save, and each running the same save and discard as the buttons, with one line in the Activity log. Cheaper substitute: an action the keyboard can't reach from Ctrl+K.
- [x] Register the tray's Pause notifications for 1 hour, Resume notifications and Exit as commands in the palette (**Added 2026-11-15** from the completion review of `D02 T02 §7`, whose menu is the only way to them). Done when: a test finds each by typing its name, runs it, and reads the same result as the tray's menu line: the pause set with its Activity line, the pause ended, and Exit going through the question about unsaved edits. Cheaper substitute: commands only a mouse on the tray can reach.
- [x] Register the page's Refresh as a command in the palette, with F5 as its key (**Added 2026-11-15** from the plan and completion reviews of `D02 T02 §8`, which builds the header's Refresh button). Done when: a test finds Refresh for the page on screen, runs it from the palette and with F5, and reads the same command as the button's: one extra call, joined to a run under way, with its Activity line; and no entry, and no key, for a page that has no refresh. Cheaper substitute: a refresh only the mouse can reach.
- [x] Commit: `shell: command palette`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*CommandPaletteTests"`, `dotnet test --project tests/Bosun.Core.Tests --filter-class "*CommandIndexTests"` and, on the test screen, `dotnet test --project tests/Bosun.Panel.E2E --filter-class "*CommandPaletteE2ETests"` exit 0, and the first fails if a danger command skips its confirmation.

> **Verified:** 2026-11-16 | §8 | eb3d63184b39857ff85cad9539df6c2e2b31acb8
> **Review:** `docs/reviews/d02-t03-s8.json`

## 9. In-window toasts

> **Started:** 2026-11-16T17:51:16Z

A short result where the operator is looking. The Activity pane stays the record, so a toast never holds the only copy of anything.

**Corrected 2026-11-16:** checked against the tree before the plan review. A toast is the result of a panel action: every action made by `ActivityLog.Command` ends in `ActivityLog.Record` (`src/Bosun.Panel/Shell/ActivityLog.cs`), which now hands its result to the shell's `ToastHost.Show` when the window has one; `Show` writes the Activity line (INFO for done, FAIL with the reason) and then the toast, so no toast exists without its line. Other log lines (a page's own INFO or WARN) are not toasts. The host is the shell's (`ShellViewModel.Toasts`), drawn bottom right in `ShellView`. Each toast's 6 s runs on the shell's `TimeProvider` (the clock pattern of `PageBase.Clock`), never a `DispatcherTimer` of its own (the panel's rule, `RefreshRulesTests`), so a test drives it with a fake clock. Toasts appear at once, with no animation. While the window is hidden the toasts on screen are dropped (their lines are already in the Activity pane) and a result writes only its line. A toast's card is a `StatusPill` (Ok for done, Danger for FAIL), its words, and a Dismiss button named for a screen reader; the words are a `LiveRegion` line, announced once. The card takes the theme's tokens and Windows' colours in a contrast theme, checked in both palettes, and its states are `RenderCatalog` cases. Tests: `tests/Bosun.Panel.Tests/Shell/ToastTests.cs`, and the keyboard Dismiss in the end-to-end smoke on the test screen; the checkpoint takes the test platform's filter form.

**Corrected 2026-11-16 (plan review):** a command's thrown failure goes through the same path as a returned one: `ActivityLog.Command`'s failure callback calls `Record` with the failure, so done, failed and thrown each make exactly one line and one toast, proven through `ICommand.Execute`. The cap and FAILs agree: a new toast takes the place of the oldest toast that is not a FAIL; when all three shown are FAILs, a newcomer is not shown as a toast (its line is in the Activity pane) and the stack says how many more are there ("2 more in the Activity pane"), so no FAIL leaves before it is dismissed and the cap holds. Expiry is the shell's: a timer callback, which a real `TimeProvider` runs off the UI thread, is handed to the dispatcher and acts only if its toast is still the one it was set for (not dismissed, not dropped by hiding). A toast's 6 s pauses while the pointer is over its card or the keyboard is in it, each on its own, and runs on with the time it had left only when neither holds, proven through the card's own hover and focus. Hiding drops the stack; a result while hidden is logged and neither shown nor announced; showing again brings no old toast back. A card is at most 360 px wide and its words at most three lines, cut with an ellipsis, the whole result in its tooltip, its screen reader help text and the Activity line; its Dismiss sits on its first line, so every Dismiss stays reachable with three 2,000 character failures at the window's least size.

**Corrected 2026-11-16 (while building):** the one path that writes a result's Activity line and shows its toast is `ActivityLog.Record`, which writes the line and then calls `ToastHost.Show`; the toast host itself writes nothing, so the checkpoint's mutation is the line leaving that path. Dismissing any toast clears the "more in the Activity pane" count, since the stack has room again. The keyboard in a card is proven end to end (`ToastE2ETests`), since an off-screen window takes no keyboard focus; the in-page tests hold a card by its pointer events.

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

- [x] Create `ToastHost` in `src/Bosun.Panel/Shell/ToastHost.cs`, holding at most 3 and dropping the oldest non-FAIL first. Done when: `tests/Bosun.Panel.Tests/Shell/ToastTests.cs` proves the cap with a fourth toast.
- [x] Dismiss after 6 s unless hovered or focused. Done when: a fake clock test proves the timer pauses on both. Cheaper substitute: a toast that vanishes under the pointer.
- [x] Keep a FAIL toast until it is dismissed. Done when: a fake clock test shows it after 60 s.
- [x] Write the Activity line from the single `Toast.Show` path. Done when: a test proves one line per toast, starting with INFO, WARN or FAIL. Cheaper substitute: a toast with no record.
- [x] Announce each toast through `LiveRegion`. Done when: a test sees one announcement per toast.
- [x] Make Dismiss reachable by keyboard with an automation name. Done when: a UI Automation test closes a toast by keyboard.
- [x] Hold no timer once the stack is empty or the window is hidden, and show pending toasts' Activity lines only when hidden. Done when: a test finds no running timer in both cases.
- [x] Commit: `shell: in-window toasts`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*ToastTests"` and, on the test screen, `dotnet test --project tests/Bosun.Panel.E2E --filter-class "*ToastE2ETests"` exit 0, and the first fails if `ToastHost.Show` stops writing the Activity line.

> **Verified:** 2026-11-16 | §9 | 4a7111a5b8a9c4c80e41d1f6f9aac386511f0901
> **Review:** `docs/reviews/d02-t03-s9.json`

## 10. High contrast theme and renders

> **Started:** 2026-11-16T04:10:25Z

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

- [x] Add `HighContrastTheme.Build(SystemPalette)` and `SystemPalette.Current()` in `src/Bosun.Panel/Theme/HighContrastTheme.cs`, building every `C.*` and `B.*` key of the generated themes from the palette by the mapping above, brushes frozen, and the `Elevation.*` shadows at no opacity. Done when: a test proves the dictionary holds exactly the generated theme's colour and elevation keys, each colour the palette's one the mapping names, and merged over the Dark theme every key the views use resolves. Cheaper substitute: a third generated palette of fixed colours.
- [x] Choose it in `ThemeService` while `SystemParameters.HighContrast` is true, whatever the person's theme choice, merged over the Dark theme, and go back to their choice when it turns off, from `SystemParameters.StaticPropertyChanged`; build it again when Windows' colours change. Done when: a test with the setting faked on, off and on again sees the dictionaries swapped each time with no restart, a colour change rebuilds it, and the person's stored choice is unchanged. Cheaper substitute: high contrast read once at start.
- [x] Say in the Settings page's theme line, while high contrast is on, that Windows' contrast theme is in use and the choice applies when it is off. Done when: a render of Settings in high contrast shows the line. Cheaper substitute: a theme box that seems to do nothing.
- [x] Add `HighContrastMode.IsOn` in `src/Bosun.Panel/Theme/HighContrastMode.cs`, an inherited attached property, set by `ThemeService` on the window while the contrast colours are in force, and give the contrast dictionary `B.GrayText` and `B.HighlightText`. Done when: a test sees it true on the window and on a combo box's open drop-down while contrast is on, and false after. Cheaper substitute: templates reading `SystemParameters.HighContrast`, which a render on a machine without contrast can't show.
- [x] Add contrast-only triggers for disabled controls to every template in `src/Bosun.Panel/Theme/Controls.xaml` that fades one (buttons, check box, field, combo box, list and menu items): full opacity, `B.GrayText` for the text, glyph and border. Done when: a test on live disabled controls in contrast reads opacity 1 and the gray text colour on each, and the same controls in Dark keep their 0.4 fade. Cheaper substitute: the 0.4 fade over the contrast colours, a colour the person never chose.
- [x] Add contrast-only triggers for selected and highlighted items and text selection in `Controls.xaml` (combo box items, list and rail items, table rows and cells, a field's `SelectionBrush` and `SelectionTextBrush`): the highlight colour behind, the highlight text colour on top. Done when: a test on a live selected combo item, rail row and table row in contrast reads the highlight text colour on the content's text, and removing one trigger fails it. Cheaper substitute: the highlight behind the window text colour.
- [x] Apply the contrast colours on the UI thread when Windows speaks on another, in `ThemeService`. Done when: a test raising the change on a worker thread sees the swap happen on the dispatcher's thread. Cheaper substitute: a swap on Windows' thread, which WPF refuses.
- [x] Prove the live paths in `tests/Bosun.Panel.Tests/HighContrastTests.cs`: a control already on screen repaints in the contrast colours and back, the Settings line appears and goes as contrast turns on and off, saving another theme while contrast is on keeps contrast and gives the new theme when it turns off, a start with contrast already on draws contrast at once, and Follow Windows is followed again after. Done when: each passes, with Windows 11's Night sky and Desert palettes. Cheaper substitute: a test of the dictionary swap alone.
- [x] Add `HighContrast` to `PageRenderer.Themes`, the renders' file name rule and `scripts/render-pages.ps1`, drawing with Windows 11's Night sky palette so a picture is the same on every machine. Done when: `pwsh scripts/render-pages.ps1` writes every view in high contrast, and `tests/render_pages_test.ps1` passes with three themes. Cheaper substitute: renders only of the two own themes.
- [x] Check the skeleton, the `NotAvailable` and `Value` texts, the stale line, the focus ring and an open combo box in the high contrast renders against design/SPEC.md §1, adding render cases for a focused control and an open drop-down with a highlighted and a selected item. Done when: each is visible and distinct in the pictures, a skeleton block's border differs from the window colour, a test reads the focus ring's border in the highlight colour on a focused control, and `ThemeContrastTests` passes the contrast theme's text on its window colour in both palettes. Cheaper substitute: looking at one page.
- [x] Commit: `theme: high contrast`

**Test checkpoint:** `bosun run -- dotnet test --solution Bosun.slnx --filter-class "*HighContrastTests"` exits 0 and fails if `ThemeService` ignores `SystemParameters.HighContrast`; `pwsh scripts/render-pages.ps1` writes every view in three themes.

> **Verified:** 2026-11-16 | §10 | fccc820872cb576e36924aef0b1d29d47bba6861
> **Review:** `docs/reviews/d02-t03-s10.json`

## 11. Contrast preview and its smoke on the shown window

Filed 2026-11-16 by the completion review of `D02 T03 §10` (`docs/reviews/02-shell/D02-T03-s10.md`): the contrast colours are proved on live controls off screen, where no control can take keyboard focus and no drop-down opens a window, so the focus ring WPF draws on a real Tab and a drop-down opened on screen are not yet proved in contrast. Turning on Windows' own contrast theme for a test would change the whole desktop of whoever is at it, so the panel gains a preview: started with `BOSUN_PANEL_CONTRAST` naming a Windows `.theme` file, it draws in that file's contrast colours as if Windows were in that theme, whatever Windows is in. The same preview lets a designer see any contrast theme without changing their own.

**Corrected 2026-11-16:** checked against the tree before the plan review. The panel has had no drop-down since `D02 T03 §4` made the theme choice a segmented control, so the open list this section was to prove is now the chosen theme segment (the highlight with its text) and the command palette's selected row (`D02 T03 §8`), both lists on screen; the combo box style keeps its off-screen tests. `hc2.theme` and `hcwhite.theme` under `C:\Windows\Resources\Ease of Access Themes` hold the colours `ContrastPalettes` has (checked by hand on this machine: `hc2.theme` is Night sky). `App.MakeWindow` makes `WindowsHighContrast` today (`src/Bosun.Panel/App.xaml.cs`); the preview takes its place there when the variable is set. The pixel checks read the window on the test screen (`BOSUN_TEST_SCREEN`), never the main one. The checkpoint's second command takes the test platform's project form.

**Phase:** 2
**Needs:** display-session
**Surface:** yes
**Fidelity:** `design/SPEC.md` §1 and §19, and Windows' own contrast themes (`C:\Windows\Resources\Ease of Access Themes\hc2.theme`, `hcwhite.theme`).
**Job:** a person in a contrast theme sees where the keyboard is and what is chosen in an open list.
**Treatment:** the focus ring and the drop-down in the theme's highlight colours, read from the real window. Cheaper substitute that fails: a picture of the parts drawn by hand.
**Chrome:** the shell's window from `D02 T02 §4` and the theme service from `D02 T03 §10`.

**Build order:**

1. Read a `.theme` file's colours. Done when: `SystemPalette.FromThemeFile` gives the palette Windows would.
2. Use it when `BOSUN_PANEL_CONTRAST` names one. Done when: the panel draws in that contrast theme.
3. Prove it on the shown window. Done when: the smoke sees the ring and the open list in its colours.

- [ ] Add `SystemPalette.FromThemeFile(string path)` in `src/Bosun.Panel/Theme/HighContrastTheme.cs`, reading the `[Control Panel\Colors]` section's `Window`, `WindowText`, `Hilight`, `HilightText`, `GrayText`, `HotTrackingColor`, `ButtonFace` and `ButtonText`, bounded to 64 KB and refusing a missing or malformed value by name. Done when: a test reads `hc2.theme` and `hcwhite.theme` copies as the palettes `tests/Bosun.Panel.Tests/Helpers/ContrastPalettes.cs` holds, and a damaged copy is refused naming the value. Cheaper substitute: colours typed into the panel.
- [ ] Add `PreviewContrast : IHighContrast` in `src/Bosun.Panel/Theme/ThemeService.cs`, always on with the file's palette, chosen by `App.MakeWindow` when `BOSUN_PANEL_CONTRAST` is set, and saying in the Activity pane that a preview is in force and from which file; a file that can't be read is said there and Windows' own setting used. Done when: a test of the choice sees each case, and a started panel with the variable set draws its window in the file's window colour. Cheaper substitute: a hidden switch with no word of it.
- [ ] Add `ContrastTests` to `tests/Bosun.Panel.E2E`, starting the panel with `BOSUN_PANEL_CONTRAST` naming `hc2.theme`: a real Tab puts the focus ring on screen in the highlight colour, the chosen theme segment shows highlight text on the highlight, and the command palette opened by a real Ctrl+K shows its selected row the same, all read from the screen's pixels. Done when: both pass in a desktop session, and fail with the contrast setters removed from `Controls.xaml`. Cheaper substitute: the off-screen drawings of the parts.
- [ ] Commit: `theme: contrast preview and its smoke`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Panel.E2E --filter-class "*ContrastTests"` exits 0 in a desktop session, and `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*HighContrastTests"` exits 0.

## 12. Window placement remembered, and the smoke off the main screen

> **Started:** 2026-11-16T10:23:08Z

The panel opens centred on the main screen every time, wherever it was last closed, and so does every end-to-end test's panel, which puts each test run on the screen the owner is working on. Asked for by the owner on 2026-11-16 ("move the testing away from my main screen permanently"). The owning fix is the window's: it reopens where it was last, on that screen, and the smoke starts each panel with a remembered place on a screen other than the main one. Moving each window after it appears would still flash it on the main screen.

**Corrected 2026-11-16 (plan review):** a place is kept in screen pixels with the DPI it was saved at, converted to and from `WINDOWPLACEMENT`'s workspace coordinates (offset by the taskbar) through the window's monitor; it is fitted to the screens there are when it is used (scaled for a changed DPI, never under 960 x 600 nor over the work area, moved inside it); a window first appears where it belongs, proved at the moment it is shown, not after; a minimised exit keeps whether it would restore maximised; and the smoke's screen is named or chosen, checked, and refused when it is not there.

**Phase:** 2
**Needs:** display-session
**Surface:** no

**Build order:**

1. Keep the window's place in `panel.json` and reopen there. Done when: a panel closed on another screen reopens on it.
2. Start the smoke's panels on a screen other than the main one. Done when: a smoke run shows no window on the main screen.

- [x] Add `WindowPlace` to `src/Bosun.Core/PanelSettings.cs` (left, top, width and height of the normal bounds in screen pixels, the DPI they were saved at, and whether the window restores maximised) and `PanelSettingsStore` as `window` (missing reads as none; a wrong shape, a size of zero or less, or a DPI outside 48 to 960 is the store's fault, as every field's is). Done when: `tests/Bosun.Core.Tests/Settings/` writes, reads back, and refuses each wrong shape.
- [x] Add `WindowPlacement.Fit` to `src/Bosun.Panel/Shell/WindowPlacement.cs`: given a stored place and the screens now (each with its bounds, work area and DPI), the bounds to open at, or none. The screen is the one under the place's title strip; none there is none. The size is scaled by that screen's DPI over the saved one, kept at least 960 x 600 at that DPI and at most the work area, and the place is moved inside the work area. Done when: `tests/Bosun.Panel.Tests/Shell/WindowPlacementTests.cs` fits a place on a screen at a negative origin, one saved at 144 DPI onto a 96 DPI screen, one too small, one larger than the work area, one hanging off its edge, and one whose screen is gone (none).
- [x] Convert between screen and workspace coordinates in `WindowPlacement` through the window's monitor (`MonitorFromRect`, `GetMonitorInfo`: workspace is screen less the work area's offset), so a stored place is the window's real position whatever the taskbar's side. Done when: a test round-trips a place through `WINDOWPLACEMENT` on each screen of this machine and with a taskbar offset in a fake monitor.
- [x] Remember the place whenever the window is hidden (to the tray), closed, or the panel exits, from `GetWindowPlacement` (a minimised window keeps `WPF_RESTORETOMAXIMIZED` as maximised), saved through the store; a save the store refuses keeps the stored place and every other setting, and is one WARN line; a capture that fails saves nothing and is one WARN line. Done when: a test moves, hides and finds the place stored; exits minimised from maximised and finds maximised; and with the store refusing finds the old place, the theme unchanged and one WARN line.
- [x] Open there: with a fitted place, `MainWindow` opens with `WindowStartupLocation.Manual` and `SetWindowPlacement` before it is first shown (as its handle is made, without showing a window started in the tray), maximised if it was; with none, centred on the main screen as before. And each time a hidden window is shown again (Open from the tray), a window no longer on any screen is fitted again. Done when: a smoke test watching the panel's window with a WinEvent hook (`EVENT_OBJECT_SHOW`) finds its first shown bounds on the stored screen, for a normal launch and for `--tray` then Open; a place on a screen that is gone opens centred; and a hidden window moved off every screen opens back on one.
- [x] Start every panel of the smoke (`tests/Bosun.Panel.E2E/PanelProcess.cs`) with a place centred on its test screen: `BOSUN_TEST_SCREEN` (a device name as Windows gives it, such as `\\.\DISPLAY3`) when set, refusing the run with the screens there are when it names none; else the largest screen that is not the main one; else the main one (one screen, as on CI). Done when: the smoke's first-shown check (above) finds every panel on the resolved screen, and on this machine never on the main one; tests cover an explicit screen, the automatic choice and the one-screen fallback with fake screen lists; and `tests/Bosun.Panel.E2E/README.md` says how to choose the screen.
- [x] Commit: `shell: the window reopens where it was, and the smoke runs off the main screen`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*WindowPlacementTests"` exits 0, and `bosun run -- dotnet test --project tests/Bosun.Panel.E2E` passes with every panel first shown on the resolved test screen; it fails if the stored place is ignored.

> **Verified:** 2026-11-16 | §12 | f7fd767aee7778f95a5ba01d582c5f74b31d7e56
> **Review:** `docs/reviews/d02-t03-s12.json`

## Verification

- Off-screen renders of every component in dark, light and high contrast were reviewed against `design/SPEC.md` §19.
- `bosun run -- dotnet test Bosun.slnx` passes.
- `python scripts/todo-graph.py validate` and `python scripts/todo-graph.py plan --check` pass.
