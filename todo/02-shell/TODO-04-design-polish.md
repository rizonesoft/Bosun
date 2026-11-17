---
schema_version: 1
id: design-polish
domain: 02-shell
status: active
title: "TODO-04 -- Design polish"
---

# TODO-04 -- Design polish

> **Goal:** Every control and page draws as one fine-tuned system: one focus treatment per control, fields that line up and say their errors, a spacing scale that keeps labels, headings and sections apart, tables and charts that read cleanly, disabled and status states that look deliberate, and copy with no internal plan references. Renders of focused, hover, error and disabled states fail when one of these faults comes back.

> **Current state:** Measured 2026-11-17 from the renders in `build/renders/` and `src/Bosun.Panel/Theme/Controls.xaml`, after the operator asked for every fault that separates a premium interface from a poor one. Twenty-one faults found:
>
> - **Focus and fields:** `Field` turns its own border accent on focus and also draws `FocusRing` 4 px outside (a double outline); a field's text sits about 21 px in where a `ComboBox`'s sits 11 px; an invalid field keeps its normal border; the segmented control's focus ring nearly touches its track.
> - **Spacing:** field labels sit about 2 px above their controls; section headings ("General", "Notifications") are smaller and dimmer than the labels under them; Settings is a 260 px column on an empty page.
> - **Tables:** the Memory table's header and cells start 10 px right of the card title; number columns are left-aligned without tabular figures.
> - **Memory page:** the Decision card's evidence is loose fragments; the page's scroll bar sits on the cards' border.
> - **Charts:** `SeriesChart` writes its top value and its pressure-line label over the lines, and a 24-hour axis reads "11:00" at both ends.
> - **States and chrome:**
>   - a disabled primary button still reads as active, and a disabled secondary one nearly vanishes;
>   - status pills have a soft glow instead of an edge;
>   - a tab badge floats above the text;
>   - a close button is a bare "x";
>   - a long toast is cut off with no way to read it;
>   - the palette's search box is a bordered box inside a bordered card, its icon outside the field;
>   - placeholder pages say "It arrives with D03 T01 §5 of the plan.";
>   - the Activity pane has no heading;
>   - the gallery's log shows a scroll thumb with nothing to scroll.
>
> Only the segmented control has a focus render; no hover, pressed or error render exists.

## Inputs

- `design/SPEC.md` §1 (tokens only, 32 px targets, states, Not available with a reason) and §19 (the premium look); `design/tokens/` for every value.
- `src/Bosun.Panel/Theme/Controls.xaml` (`FocusRing`, `FocusRing.Inside`, `Field`, `Btn`, `Btn.Small`, `Btn.Primary`, `TabStrip.Item`, `StatusPill`, `ToastCard`, `PaletteSearch`, `Cell.Text`, `Cell.Number`, the `ScrollBar` style).
- `src/Bosun.Panel/Settings/SettingsView.xaml`, `src/Bosun.Panel/Memory/MemoryView.xaml`, `src/Bosun.Panel/Controls/SeriesChart.cs`, `src/Bosun.Panel/Shell/PlaceholderPage.cs`, `src/Bosun.Panel/Shell/ShellView.xaml`, `src/Bosun.Panel/Shell/ToastCard.cs`, `src/Bosun.Panel/Shell/CommandPaletteView.xaml`.
- `tests/Bosun.Panel.Tests/Render/RenderCatalog.cs` and `ControlsGallery.cs`: the renders each section adds to.

## Outcome

- A focused field, button, segment, check box, row and tab each show one accent outline, in all three themes, in renders that fail on a second one.
- Fields and dropdowns share one text inset, and an invalid field's border says so.
- Labels, section headings and sections follow one spacing scale from the tokens, and Settings is laid out in cards like the other pages.
- Table columns align with their card's content, numbers right-aligned in tabular figures.
- Chart labels never sit on a line, and a time axis names its ends unambiguously.
- Disabled, status, badge, close, toast, palette and Activity chrome look deliberate, and no page shows an internal plan reference.

**Adjacency:** list=not-applicable (no list is added); document=not-applicable (nothing is saved); settings=not-applicable (no setting is added; Settings is only laid out again); reporting=not-applicable (nothing is reported); notifications=not-applicable (toasts are only drawn better); permissions=not-applicable (per-user, read-only); audit=not-applicable (no action is added); exchange=not-applicable (nothing is imported or exported); reverse=not-applicable (no state changes)

**Adjacency rationale:** The file changes how existing controls and pages draw, nothing they do: each fix lands in the shared theme or the view that owns it, so it reaches every page, and each is held by a render or a measured test.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | One focus outline per control, with focus renders | -- | [x] |
| 2 | §2 | Field insets, error border and label spacing | §1 | [ ] |
| 3 | §3 | Spacing scale, section headings and Settings in cards | §2 | [ ] |
| 4 | §4 | Table alignment and tabular numbers | -- | [ ] |
| 5 | §5 | Chart labels clear of lines and a readable time axis | -- | [ ] |
| 6 | §6 | Deliberate disabled buttons, crisp pills, badge and close glyph | -- | [ ] |
| 7 | §7 | Expandable toasts and a clean palette search field | §1 | [ ] |
| 8 | §8 | Memory page scroll gutter and the Decision evidence list | §4 | [ ] |
| 9 | §9 | Placeholder copy, the Activity heading and the idle scroll thumb | -- | [ ] |

## 1. One focus outline per control, with focus renders

> **Started:** 2026-11-17T01:20:56Z

A focused control shows one accent outline: today `Field` draws its own accent border and the shared ring together, and the segmented control's ring crowds its track. Each control gets exactly one treatment, and the render catalogue draws every interactive control focused, so a second outline fails a test.

**Phase:** 3
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §1 (focus) and §19.
**Job:** see where the keyboard is, once, clearly.
**Treatment:** one 2 px accent ring per control. Cheaper substitute that fails: a ring added on top of a border that already turns accent.
**Chrome:** the shared theme in `Theme/Controls.xaml`.

**Build order:**

1. Give `Field` one focus treatment. Done when: a focused field render shows one outline.
2. Render every interactive control focused in all themes. Done when: the renders are in the catalogue and a double outline fails a test.

- [x] Remove `Field`'s own `IsKeyboardFocused` border trigger in `src/Bosun.Panel/Theme/Controls.xaml`, keeping its `FocusVisualStyle` (`FocusRing`: 2 px, 2 px outside the control) as its only focus cue (**Corrected 2026-11-17** by the plan review: removing the ring instead would leave a 1 px border, not the specified outline). Done when: a focused field draws one 2 px ring 2 px outside its border, its own border unchanged. Cheaper substitute: a thinner second outline.
- [x] Remove the check box's own accent box border on focus (`Theme/Controls.xaml`, the `CheckBox` template's `IsKeyboardFocused` trigger) so its `FocusVisualStyle` is its one focus cue, keeping the checked indicator and the hover border. Done when: a focused check box, checked and unchecked, and one both hovered and focused, each draw one ring, the check mark kept.
- [x] Audit every other style's focus cue in `Theme/Controls.xaml` (`Btn`, `Btn.Small`, `Btn.Primary`, `Btn.Danger`, `Btn.Chrome`, `ComboBox`, `ComboBoxItem`, `DataGridColumnHeader`, `DataGridCell`, `DataGridRow`, `TabStrip.Item`, the rail row, `LogBox`, the segmented item) and remove any focus trigger that changes a border beside the ring. Done when: a list of each style's one focus cue is written in the commit body, and no style has two.
- [x] Give the segmented control's focus ring the track's inner padding in `Theme/Controls.xaml` (`SegmentedControl` item style), so the ring clears the track by its own gap. Done when: `controls.segmented-focus` shows a ring 2 px clear of the track on every side. Cheaper substitute: shrinking the selected segment's fill.
- [x] Draw focus renders from real keyboard focus, not an overlaid ring: give the render host a way to activate its window and move keyboard focus to a named control (`PageRenderer` in `tests/Bosun.Panel.Tests/Render/`), drawing the control's own `FocusVisualStyle` from the adorner layer, and assert `IsKeyboardFocused` before the picture is taken (**Corrected 2026-11-17** by the plan review: `SegmentedFocus` overlays a standalone ring, which proves nothing about the control). Done when: a focus render of a control whose `FocusVisualStyle` is removed draws no ring and its test fails. Cheaper substitute: a ring drawn over the control by the test. (**Corrected 2026-11-17** at build: off screen nothing holds keyboard focus (the hidden host never activates, as D02 T03 found), so `FocusShown` (`tests/Bosun.Panel.Tests/Render/FocusShown.cs`) draws each control's own `FocusVisualStyle` in the adorner layer as WPF's keyboard focus does, a control without one drawing none; `XamlRules` refuses any trigger on keyboard focus, which is how the second outline got in; and `KeyboardTests.A_field_given_the_keyboard_by_Tab_shows_one_outline_not_two` proves a real Tab on screen draws one ring.)
- [x] Add `ControlsGallery` focus renders of every keyboard target: a field, `Btn`, `Btn.Small`, `Btn.Primary`, `Btn.Danger`, `Btn.Chrome` (a window button), a segment selected and not, a check box checked and unchecked, a combo box and an open combo's item, a rail row selected and not, a table row, a column header and a cell selected and not, a tab, and the log box, in `ControlsGallery.cs` and `RenderCatalog.cs`. Done when: each is drawn in Dark, Light and HighContrast and looked at.
- [x] Add `FocusOutlineTests` in `tests/Bosun.Panel.Tests/Render/`: for each focused control, render it focused and unfocused, take the pixels that differ, and check they form one ring: all four sides present, 2 px thick, at the specified clearance (outside for `FocusRing`, inside for `FocusRing.Inside`), and nothing else changed. Done when: it passes, and fails with `Field`'s or the check box's old trigger put back, a control's `FocusVisualStyle` removed, or a side of the ring missing. Cheaper substitute: counting accent edges along one line, which a selected fill or a checked box also passes.
- [x] Keep `FocusRing.Inside`'s HighContrast inner line and test it: in HighContrast, a focused selected rail row and segment show the ring against the selected fill. Done when: a `FocusOutlineTests` case fails with the inner line removed.
- [x] Commit: `theme: one focus outline per control`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*FocusOutlineTests" --filter-class "*PageRendererTests"` exits 0, and fails with `Field`'s focus border trigger restored, a `FocusVisualStyle` removed, or the HighContrast inner line removed; the E2E `KeyboardTests` still draw the ring on a real Tab.

> **Verified:** 2026-11-17 | §1 | 629cf5eeee4bf6e240b8da8178b82517b9a93b6b
> **Review:** `docs/reviews/d02-t04-s1.json`

## 2. Field insets, error border and label spacing

> **Started:** 2026-11-17T02:16:26Z

A field's text sits about 21 px in and a combo box's 11 px, an invalid field keeps its normal border, and a label sits about 2 px above its control. Fields line up with each other and their labels, and an error is shown on the field itself.

**Phase:** 3
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §1 and §19.
**Job:** read and fix a value without the form looking ragged.
**Treatment:** one text inset for every field, a danger border on an invalid field, a token gap between label and control. Cheaper substitute that fails: nudging one field's margin to match another.

**Chrome:** the shared theme.

**Build order:**

1. Make every field's text start at one inset. Done when: a field and a combo box side by side start their text at the same x.
2. Draw an invalid field's border in the danger colour, and space labels from their controls. Done when: the error and spacing renders show both.

- [ ] Find what adds the extra inset in `Field` (the `PART_ContentHost` margin plus the `ScrollViewer`'s own padding) in `src/Bosun.Panel/Theme/Controls.xaml`, and set one inset token shared with the `ComboBox` template. Done when: a test measuring the first text pixel of a field and a combo box in the gallery finds the same x within 1 px. Cheaper substitute: padding the combo box out to the field's inset.
- [ ] Add an attached `FieldState.Invalid` (bool) in `src/Bosun.Panel/Theme/FieldState.cs`, and draw `Field`'s border in `B.Danger` while it is true, in `Theme/Controls.xaml` (**Corrected 2026-11-17** by the plan review: `Validation.HasError` is read-only and the page validates through `FloorFault` with no WPF validation interface; WPF's validation would also add its own error adorner, a second outline beside the focus ring). Done when: a `ControlStylesTests` case sets it on a field in every theme and reads the danger border, and clears it and reads the line colour again. Cheaper substitute: WPF's default red error adorner.
- [ ] Bind the Settings threshold field's `FieldState.Invalid` to the page's own error (`SettingsPage.PressureFloorError` from `FloorFault`, through a bool `HasPressureFloorError`), in `src/Bosun.Panel/Settings/SettingsView.xaml` and `SettingsPage.cs`. Done when: a view-level test on the drawn Settings page types "lots" into the field and reads the danger border, types "4096" and reads the line colour, types "lots" again and Discards and reads the line colour, the field's text back to its stored value.
- [ ] Keep one focus outline on an invalid field: add `field, invalid` to `FocusOutlineTests.All`, drawn focused and unfocused in every theme. Done when: it passes with one 2 px ring outside the danger border, and fails if WPF's validation adorner is turned on for the field.
- [ ] Set the label-to-control gap from a spacing token (`S.LabelGap`, generated from `design/tokens/`) on every field label in `SettingsView.xaml` and the gallery. Done when: a test measures the gap between "Pressure alert threshold" and its field as the token's value. Cheaper substitute: a hand-typed margin on one label.
- [ ] Add a hover render and an error render of a field to the gallery, in every theme. Done when: they are in `RenderCatalog` and looked at.
- [ ] Commit: `theme: fields line up, say their errors and keep their labels apart`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*FieldGeometryTests" --filter-class "*SettingsPageTests" --filter-class "*ControlStylesTests" --filter-class "*FocusOutlineTests"` exits 0, and fails with the old content-host margin restored or the danger border trigger removed; the whole panel suite passes before the push.

## 3. Spacing scale, section headings and Settings in cards

Section headings are smaller and dimmer than the labels under them, sections run together, and Settings is a 260 px column on an empty page. Headings rank above labels, sections are apart by a token, and Settings groups its sections in cards like the Memory page.

**Phase:** 3
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §1 and the Settings section of `design/SPEC.md`.
**Job:** scan a page and see its parts at a glance.
**Treatment:** a heading style above the label style, a section gap token, cards with a readable width. Cheaper substitute that fails: bolding the heading text alone.

**Chrome:** the page frame.

**Build order:**

1. Add a section heading style and a section gap token. Done when: the gallery draws a heading above a label with the gap.
2. Put Settings' sections in cards. Done when: the Settings renders show its sections in cards.

- [ ] Add `Section.Heading` (larger and in `B.Text`, above `Field.Label`) and the `S.Section` gap to the generated theme from `design/tokens/`, and use them in `src/Bosun.Panel/Settings/SettingsView.xaml`. Done when: a test reads the heading's font size above the label's. Cheaper substitute: changing one heading's colour.
- [ ] Put General and Notifications in cards in `SettingsView.xaml`, using the Memory page's card style lifted into `Theme/Controls.xaml` as `Card`, at a readable width (`S.FormWidth`). Done when: `settings.general` shows two cards with their headings and fields inside. Cheaper substitute: a border around the whole column.
- [ ] Use the lifted `Card` style in `src/Bosun.Panel/Memory/MemoryView.xaml` instead of its local `MemoryCard`. Done when: the Memory renders do not move.
- [ ] Redraw every Settings render (general, restored, unavailable, half-saved, threshold-error, notifications-off, contrast) in every theme. Done when: each is looked at.
- [ ] Commit: `settings: sections in cards on one spacing scale`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*SettingsPageTests" --filter-class "*PageRendererTests"` exits 0.

## 4. Table alignment and tabular numbers

A table's header and cells start 10 px right of its card's title, and number columns are left-aligned in proportional figures. A table lines up with its card, and its numbers right-align in tabular figures.

**Phase:** 3
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §1 and §19.
**Job:** compare numbers down a column at a glance.
**Treatment:** cells inset to the card's content edge, numbers right-aligned with tabular figures. Cheaper substitute that fails: padding the card title right to meet the table.

**Chrome:** the shared theme.

**Build order:**

1. Align the table with its card. Done when: the header's first text starts at the card title's x.
2. Right-align numbers in tabular figures. Done when: a column of numbers ends at one x.

- [ ] Set the cell and header inset in `src/Bosun.Panel/Theme/Controls.xaml` (`DataGridCell`, `DataGridColumnHeader`, `Cell.Text`) so a table's text starts at its card's content edge. Done when: a test on the Memory page finds "Repo" and "By repo" starting at one x within 1 px. Cheaper substitute: a negative margin on one table.
- [ ] Make `Cell.Number` right-aligned with `Typography.NumeralAlignment="Tabular"`, and its column header right-aligned to match. Done when: in the Memory and Instances renders each number column's digits end at one x. Cheaper substitute: a monospace font.
- [ ] Show a word in a number column (`Not in the history yet`, `Not available`) in `B.TextDim` with its reason as the tooltip, in `MemoryView.xaml` and `src/Bosun.Panel/Instances/InstancesView.xaml`. Done when: the renders show it dim and right-aligned with the numbers.
- [ ] Commit: `theme: tables line up with their cards, numbers in tabular figures`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*TableAlignmentTests"` exits 0, and fails with the old cell inset.

## 5. Chart labels clear of lines and a readable time axis

`SeriesChart` writes its top value and its pressure-line label over the lines, and a 24-hour axis reads "11:00" at both ends. Labels sit clear of every line, and the time axis says which day and when now is.

**Phase:** 3
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §3 (History chart).
**Job:** read the chart without guessing what a label means.
**Treatment:** labels in reserved gutters, the axis ends named with day and "now", middle ticks. Cheaper substitute that fails: moving a label a few pixels.

**Chrome:** the Memory page's chart card.

**Build order:**

1. Reserve a gutter for the value labels. Done when: no label's box intersects a line's path.
2. Name the axis ends and add ticks. Done when: a 24-hour axis reads a day at its start and "Now" at its end.

- [ ] Reserve a left gutter for the top value and a right gutter for the pressure-line label in `src/Bosun.Panel/Controls/SeriesChart.cs` (`Layout`), the plotted area inside them. Done when: `SeriesChartTests` checks each label's rectangle against the line's points and the pressure line, and fails with the old layout.
- [ ] Label the axis start with its day and time ("Sun 11:00") and the end "Now", with ticks every 6 hours (24 h), day (7 days) or 2 days (14 days), in `SeriesChart.cs`. Done when: `SeriesChartTests` reads the labels for each range. Cheaper substitute: two identical times.
- [ ] Redraw the Memory history renders in every theme. Done when: each is looked at.
- [ ] Commit: `memory: chart labels clear of the lines and a readable time axis`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*SeriesChartTests"` exits 0, and fails with the old label placement.

## 6. Deliberate disabled buttons, crisp pills, badge and close glyph

A disabled primary button is a muddy green that still reads as active, a disabled secondary one nearly vanishes, status pills glow instead of having an edge, a tab badge floats above the text, and a close button is a bare "x". Each is drawn as a deliberate state.

**Phase:** 3
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §1 and §19.
**Job:** tell at a glance what can be pressed and what a status means.
**Treatment:** disabled buttons in the theme's disabled tokens at readable contrast, pills with a 1 px edge and no blur, a badge pill on the text's centre line, a close glyph from the shared map. Cheaper substitute that fails: lowering the opacity further.

**Chrome:** the shared theme.

**Build order:**

1. Draw disabled buttons from disabled tokens. Done when: a disabled primary and secondary read as disabled at 3:1 against the page.
2. Fix the pill, badge and close glyph. Done when: their renders show each crisp and aligned.

- [ ] Draw `Btn`, `Btn.Small` and `Btn.Primary` disabled with `B.DisabledFill`, `B.DisabledText` and `B.DisabledLine` (generated from `design/tokens/`) in `src/Bosun.Panel/Theme/Controls.xaml`, never by opacity alone. Done when: `ContrastTests` reads each disabled button's text at 3:1 or more against its fill in every theme. Cheaper substitute: a 0.4 opacity.
- [ ] Remove the soft shadow or blur from `StatusPill` in `Theme/Controls.xaml`, drawing a 1 px edge in its status colour at reduced alpha. Done when: `controls.status-pills` and the toast renders show crisp edges.
- [ ] Draw the tab badge as a pill centred on the tab text's line in `TabStrip.Item`. Done when: a test finds the badge's centre within 1 px of the text's centre.
- [ ] Replace the bare "x" close button in the gallery and wherever it is used with the shared map's close glyph (`Glyphs`). Done when: no button's content is the letter x, checked by `XamlRulesTests`.
- [ ] Commit: `theme: deliberate disabled states, crisp pills, centred badges and a close glyph`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*ContrastTests" --filter-class "*XamlRulesTests"` exits 0.

## 7. Expandable toasts and a clean palette search field

A long toast is cut off with no way to read the rest, and the palette's search box is a bordered field inside a bordered card with its icon outside it. A toast shows its whole message on request, and the palette's search is one clean field with its icon inside.

**Phase:** 3
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §1 and §19.
**Job:** read a whole result, and search without visual clutter.
**Treatment:** a "Show more" on a toast that was cut off, the search field borderless inside the palette card with its icon inside. Cheaper substitute that fails: a tooltip holding the whole message.

**Chrome:** the shell's toast host and palette.

**Build order:**

1. Let a cut-off toast show its whole message. Done when: a toast longer than three lines offers Show more and expands.
2. Make the palette's search one field. Done when: the palette render shows the icon inside one field.

- [ ] Add Show more to `ToastCard` (`src/Bosun.Panel/Shell/ToastCard.cs` and its template) when the message is cut off, expanding the text and holding the toast open like keyboard focus does. Done when: a test with a five-line message finds Show more, runs it, and reads the whole message unclipped. Cheaper substitute: the message in a tooltip.
- [ ] Draw `PaletteSearch` without its own border inside the palette card, its search icon inside its left inset, in `Theme/Controls.xaml` and `src/Bosun.Panel/Shell/CommandPaletteView.xaml`, keeping one focus outline from §1. Done when: `shell.palette` shows one field with the icon inside.
- [ ] Redraw the toast and palette renders in every theme. Done when: each is looked at.
- [ ] Commit: `shell: toasts show their whole message, the palette searches in one field`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*ToastTests" --filter-class "*CommandPaletteTests"` exits 0.

## 8. Memory page scroll gutter and the Decision evidence list

The Memory page's scroll bar sits on the cards' right border, and the Decision card's evidence is loose lower-case fragments. The scroll bar has its own gutter, and the evidence is a labelled list.

**Phase:** 3
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §3 (the Decision card).
**Job:** read every figure behind the verdict.
**Treatment:** a scroll gutter the cards stop short of, evidence as a list with a marker per line. Cheaper substitute that fails: a margin on one card.

**Chrome:** the Memory page.

**Build order:**

1. Give the page's scroll bar a gutter. Done when: no card's border meets the scroll bar.
2. Draw the evidence as a list. Done when: the Decision renders show a marker per line.

- [ ] Reserve a gutter for `MemoryScroll`'s scroll bar in `src/Bosun.Panel/Memory/MemoryView.xaml` (the content's right margin the scroll bar's width plus a gap). Done when: a test finds every card's right edge left of the scroll bar's left edge. Cheaper substitute: a hidden scroll bar.
- [ ] Draw the Decision evidence as a list with a marker and a line height from the tokens, each line starting with a capital, in `MemoryView.xaml` and `src/Bosun.Core/Memory/Decision.cs`. Done when: `DecisionTests` reads capitalised lines and the Decision renders show a marker per line.
- [ ] Redraw the Memory renders in every theme. Done when: each is looked at.
- [ ] Commit: `memory: the scroll bar has its gutter and the evidence is a list`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*DecisionCardTests" --filter-class "*MemoryPageTests"` exits 0.

## 9. Placeholder copy, the Activity heading and the idle scroll thumb

Placeholder pages show an internal plan reference, the Activity pane has no heading, and a log shows a scroll thumb when nothing overflows. Placeholders speak to the user, the pane says what it is, and a scroll bar shows only when there is something to scroll.

**Phase:** 3
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §1 (Activity pane).
**Job:** understand every part of the window without knowing the plan.
**Treatment:** user-facing placeholder copy, an "Activity" heading on the pane, a scroll bar that shows only on overflow. Cheaper substitute that fails: hiding the scroll bar everywhere.

**Chrome:** the shell.

**Build order:**

1. Rewrite the placeholder copy. Done when: no page shows a plan reference.
2. Head the Activity pane and fix the idle scroll thumb. Done when: the shell renders show both.

- [ ] Change `src/Bosun.Panel/Shell/PlaceholderPage.cs` to say what the page will do ("Coming soon: Overview will show memory, the queue and instances at a glance."), keeping the plan reference out of the UI, and update the tests and E2E that read it. Done when: a test finds no text matching a section reference (`D\d\d T\d\d §\d`) in any render. Cheaper substitute: deleting the second line.
- [ ] Add an "Activity" heading to the pane in `src/Bosun.Panel/Shell/ShellView.xaml` from the section heading style of §3, named for a screen reader. Done when: the shell renders show it and the smoke finds it.
- [ ] Show the log's vertical scroll bar only when its content overflows (`ScrollBarVisibility.Auto` with the theme's `ScrollBar`) in the Activity pane and the gallery's log. Done when: `controls.gallery` shows no thumb with five lines and a test finds one with five hundred.
- [ ] Commit: `shell: user-facing placeholders, a headed Activity pane and no idle scroll thumb`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*ShellTests" --filter-class "*PageRendererTests"` exits 0, and the E2E smoke passes.

## Verification

- Every render in `build/renders/` in Dark, Light and HighContrast looked at after §9, the focused, hover, error and disabled states included, with none of the twenty-one faults in Current state.
- `bosun run -- dotnet test --project tests/Bosun.Panel.Tests` and the E2E smoke pass.
- `python scripts/todo-graph.py validate` and `python scripts/todo-graph.py plan --check` pass.
