---
schema_version: 1
id: brand-and-theme
domain: 02-shell
status: active
title: "TODO-01 -- Brand and theme"
---

# TODO-01 -- Brand and theme

> **Goal:** Every colour and icon traces to one source, with both themes accessible and a drift check that fails on hand edits.

> **Current state:** Measured 2026-11-07. `design/tokens/colors.json` and the icon SVG masters in `design/icon/` exist from the 2026-11-07 brainstorm. No generator, ICO or XAML exists. Steward's theme is hand-edited and dark only. **Corrected 2026-11-12:** `scripts/build-icons.py` and `resources/bosun.ico` exist (§5); the theme generator, tray icons and XAML do not.

## Inputs

- `design/SPEC.md` §1 and §18.
- `D:\Office\scripts\build-icons.py`, `D:\Recto\design\tools\build_resources.py:505`, `D:\Isotone\docs\design\app-icons.md`.
- `D:\Steward\src\Steward.Console\Theme\Controls.xaml`.

**Corrected 2026-11-13:** the sibling repos are at `D:\<repo>` (`D:\Steward`, `D:\Office`, `D:\Recto`, `D:\Resolute`, `D:\Isotone`); `R:\GitHub` holds only CarbonCD and IsotoneStack, so every sibling-repo path this file named, given under `R:\GitHub`, now reads `D:\<repo>`.
- -> XREF: D00 T02 §2 -- consumed by this file
- -> XREF: D02 T02 §9 -- consumed by this file
- -> XREF: D02 T02 §1 -- consumes this file
- -> XREF: D02 T03 §1 -- consumes this file
- -> XREF: D02 T03 §4 -- consumes this file

## Outcome

- A token edit regenerates dark and light XAML, and `--check` fails on drift.
- `resources/bosun.ico` carries eight frames and regenerates byte for byte.

**Adjacency:** all=not-applicable (internal tooling and contracts with no records of their own)

**Adjacency rationale:** Assets and styles only.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Generate the theme from tokens | D00 T02 §2 | [x] |
| 2 | §2 | Contrast and stray colour tests | §1 | [x] |
| 3 | §5 | App icon ICO from the masters | -- | [x] |
| 4 | §3 | Tray state icons and the icon drift gate | §1, §5 | [x] |
| 5 | §4 | Control templates | §2, D02 T02 §9 | [ ] |

## 1. Generate the theme from tokens

> **Started:** 2026-11-13T13:47:19Z

Hand-edited XAML is how themes drift. The generator owns the colour resources and everything else refers to them.

**Corrected 2026-11-13:** checked against the tree before the build. `design/tokens/colors.json` holds `dark` and `light` with the same 20 colours each, `radius` (5), `space` (5) and `type` (5), plus `elevation` and `motion`, which `D02 T03 §1` emits, so this section doesn't. An eight-digit colour is alpha first (`OkSoft` `#2446C96B` is `Ok` `#46C96B` at alpha `0x24`), as WPF reads `#AARRGGBB`, so colours pass through unchanged. Steward's key names (`D:\Steward\src\Steward.Console\Theme\Dark.xaml`) are `C.<name>` colours, `B.<name>` brushes, `T.<name>` type sizes and `R.<name>` corner radii; spacing takes `S.<name>`. `src/Bosun.Panel` has no `Theme/` folder yet, and `scripts/check-all.ps1` lists its steps in one table (`New-Step`). The script follows `scripts/build-icons.py`: a docstring giving its usage, `--check` exiting 1 when a generated file differs, and tests in `scripts/tests/`, which `scripts/run-tests.py` runs. Decided here (industry default): the generated files are LF text with a header naming the generator and the token file and saying not to edit them, written temporary-file-then-replace, and `--check` compares them with line endings normalised, so Git's CRLF conversion is never a difference. Reversal cost: none beyond the generator.

**Corrected 2026-11-13 (plan review):** the plan review in `docs/reviews/02-shell/D02-T01-s1.md` returned two gaps (1 P1, 1 P2), planned in below: the WPF test reads back every emitted key, type and value from the token file itself, brushes included and frozen, and the script tests edit real tokens in a copy, so `--check` must fail before regeneration and the regenerated files must hold the edits; and the script refuses, by path, a group that isn't an object, is empty or lacks a key its consumers use, keeping the old outputs. To test that without touching the repository's own files, the script takes `--tokens <file>` and `--out <folder>`.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Create `scripts/build-theme.py`. Done when: it writes `src/Bosun.Panel/Theme/Generated/Dark.xaml` and `Light.xaml` from `design/tokens/colors.json`.
2. Add `--check`. Done when: a hand edit makes it exit nonzero.

- [x] Emit `C.*` colours and `B.*` brushes plus radius, spacing and type resources, with the key names of `D:\Steward\src\Steward.Console\Theme\Dark.xaml:14`, in `scripts/build-theme.py`: for each theme a `ResourceDictionary` with a `Color` `C.<name>` and a frozen `SolidColorBrush` `B.<name>` for each colour, in the token file's order, a `CornerRadius` `R.<name>` for each radius, and a `sys:Double` `S.<name>` for each space and `T.<name>` for each type size, under a header that names the generator and the token file and says not to edit the file, written temporary-file-then-replace. Done when: both files load in a WPF test, `tests/Bosun.Panel.Tests/ThemeTests.cs`, through `XamlReader` on an STA thread, and for each theme every key the token file gives (each colour's `C.` and `B.`, each radius, space and type size) reads back with its type and value, each brush frozen and of its colour, and no other key is in the dictionary. Cheaper substitute: editing the generated XAML by hand.
- [x] Refuse bad tokens in `scripts/build-theme.py` with exit 2 and a message naming the token's path (`dark.TextDim`, `radius`), writing nothing and keeping the old outputs: a token file that isn't JSON or isn't an object; a missing `dark`, `light`, `radius`, `space` or `type`, or one that isn't an object or is empty; a colour present in one theme and missing in the other; a missing key a consumer uses (the 20 colours, `Card`, `Control`, `Sheet`, `Overlay` and `Pill`, `XS` to `XL`, and `Caption`, `Body`, `Subtitle`, `Title` and `Metric`); a colour that isn't `#RRGGBB` or `#AARRGGBB`; and a radius, space or type size that isn't a number from 0 to 1000 (a radius of 999 or more, the pill, stays as it is). Done when: `scripts/tests/test_build_theme.py` runs each fixture through `--tokens` and `--out`, and reads exit 2 and the path, with the outputs in that folder byte for byte as they were.
- [x] Add `--check` in `scripts/build-theme.py`: it writes nothing, and exits 1 naming each generated file that is missing or differs from what the tokens give (line endings aside), 0 when both match. Done when: `scripts/tests/test_build_theme.py` reads 0 on a fresh build, 1 after a hand edit and after a deleted file, and 0 again after a rebuild, and a CRLF copy of a matching file still reads 0; and, editing a copy of the token file (a dark colour, a light colour, a radius, a space and a type size, one at a time), reads 1 before regenerating, finds the edited value in the regenerated file, and reads 0 after.
- [x] Add the check to `scripts/check-all.ps1` as the `theme` step (`python scripts/build-theme.py --check`), after `plan`. Done when: a changed generated file fails the gate, shown by `pwsh scripts/check-all.ps1 -Only theme` exiting nonzero after a hand edit and 0 after a rebuild.
- [x] Freeze the brushes for real in `scripts/build-theme.py`: the `po` namespace is declared but not `mc:Ignorable`, since WPF's reader skips an ignorable namespace and left every brush unfrozen. Found by `tests/Bosun.Panel.Tests/ThemeTests.cs`, which read each brush's `IsFrozen`. Done when: that test passes, as it failed with the namespace ignorable.
- [x] Settle the completion review's gap (`docs/reviews/02-shell/D02-T01-s1.md`) in `scripts/build-theme.py` (`write_outputs`): both files are staged as temporary files before either is replaced, a failure exits 3 saying what was updated and what was left as it was, and no temporary file stays. Done when: `scripts/tests/test_build_theme.py` fails the second staging (nothing changed) and the second replace (the held file byte for byte as it was, the report naming both), with `--check` still seeing the drift and a retry landing.
- [x] Commit: `theme: generate XAML from tokens`

**Test checkpoint:** `python scripts/build-theme.py --check` exits 0, and exits nonzero after a hand edit; `python scripts/run-tests.py` passes `test_build_theme`; and `bosun run -- dotnet test --solution Bosun.slnx --filter-class "*ThemeTests*"` passes.

> **Verified:** 2026-11-13 | §1 | a07111b17d9cb9f153498b6dc94b79d4ff34bf0d
> **Review:** `docs/reviews/d02-t01-s1.json`

## 2. Contrast and stray colour tests

> **Started:** 2026-11-13T14:59:14Z

Steward proved these catch real faults (it raised `TextMute` for contrast). Here they cover both themes.

**Corrected 2026-11-13:** checked against the tree before the build. Steward's `ThemeContrastTests.cs` reads `Theme\Dark.xaml` from the test's output folder and checks `Text`, `TextDim` and `TextMute` on `Base`, `Surface` and `Raised`; here the colours are read from the generated files of `D02 T01 §1` in the checkout, for both themes, and the roles are every colour the panel draws words in: the three text roles, `Accent`, the four status colours (`design/SPEC.md` §1: each state has a word), and `OnAccent` on `Accent` for `Btn.Primary`. Measured 2026-11-13, every one of those is at least 4.60:1 in both themes (the lowest are light `Accent` on `Raised` at 4.60 and dark `TextMute` on `Raised` at 4.94), so no token changes. The soft status tints are `D02 T03 §1`'s. Steward's `TabStripStyleTests.cs` holds two things: its no-colour rule, which becomes the rule here, and its assertions on the `TabStrip.Item` style, which need `Theme/Controls.xaml` and so move to `D02 T01 §4`, where that style is ported (an item is added there). The panel's XAML today is `App.xaml` and `Foundation.xaml`, with no colour or font size in either. The checkpoint's filter is the Microsoft.Testing.Platform form the repo uses (`--filter-class`), not `--filter FullyQualifiedName~`. Decided here: the rule reads every `.xaml` and `.cs` under `src/Bosun.Panel` outside `Theme/Generated`, since a colour typed into code-behind drifts as surely as one typed into a view. Reversal cost: none.

**Corrected 2026-11-13 (plan review):** the plan review in `docs/reviews/02-shell/D02-T01-s2.md` returned three gaps (1 P1, 2 P2), planned in below: the font-size rule also covers C# and XAML property elements; the scan itself is proved on a nested tree of files, with `Theme/Generated` left out; and contrast composites a translucent text colour over its surface, since `D02 T01 §1` accepts `#AARRGGBB` for any colour, and a surface that isn't opaque is a failure.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Port `ThemeContrastTests`. Done when: both themes are asserted.
2. Add the no-stray-colour rule across the panel's XAML and code. Done when: a stray colour fails.

- [x] Assert at least 4.5:1 for each text role on `Base`, `Surface` and `Raised`, in `tests/Bosun.Panel.Tests/ThemeContrastTests.cs`, citing `D:\Steward\tests\Steward.Console.Tests`: for each generated theme, `Text`, `TextDim`, `TextMute`, `Accent`, `Ok`, `Warn`, `Danger` and `Info` on each of the three surfaces, and `OnAccent` on `Accent`, by WCAG 2 relative luminance, through one function (`ThemeContrast.Failures`) that takes a theme's colours and names each failing pair with its ratio, compositing a text colour that has alpha over the surface first, and failing a surface that isn't opaque; and `TextMute` stays below `TextDim`, and `TextDim` below `Text`, in contrast on `Base`. Done when: both real themes give no failure; a lowered token fails the test (the dark theme's colours with `TextMute` lowered to `#5A6270` name `TextMute` on `Raised`, and the light theme's with `Accent` raised to `#5B8DEF` name `Accent`); an alpha-only change fails it (the dark theme's `Text` at alpha `0x40`, its RGB unchanged, names `Text` on each surface, while at alpha `0xFF` it passes); a translucent surface is named; and a theme missing a role is a failure, not a pass. Cheaper substitute: checking only the dark theme.
- [x] Fail any colour or font size outside `Theme/Generated` in `tests/Bosun.Panel.Tests/XamlRulesTests.cs`, through one function (`XamlRules.Violations`) over a file's text, run on every `.xaml` and `.cs` under `src/Bosun.Panel` outside `Theme/Generated`: in XAML, an attribute value or element text that is a hex colour (`#RGB`, `#ARGB`, `#RRGGBB` or `#AARRGGBB`), a named colour on a brush or colour property (`Foreground`, `Background`, `BorderBrush`, `Fill`, `Stroke`, `Color` and a `Setter` for one), `Transparent` excepted, and a `FontSize` that is a number, as an attribute, a `Setter` or a property element (`<TextBlock.FontSize>13</TextBlock.FontSize>`); in C#, a hex colour in a string, `Color.FromRgb`, `Color.FromArgb`, `ColorConverter`, `Colors.` or `Brushes.` members other than `Transparent`, and a font size set to a number (`FontSize = 13`, an object initialiser's `FontSize = 13`, `SetValue(...FontSizeProperty, 13)` and `new Setter(...FontSizeProperty, 13.0)`). Each violation names the file, the line and what was found. The scan (`XamlRules.Scan`) walks a folder for every `.xaml` and `.cs` at any depth, leaving out `Theme/Generated`, `bin` and `obj`. Done when: the panel's real files give none and at least `App.xaml` and one `.cs` file were read; a hex in a view fails it, as does each of the other cases, from fixture text, while `{StaticResource B.Text}`, `{DynamicResource T.Body}`, `Transparent`, `Text="Issue #123"`, `FontSize = (double)FindResource("T.Body")` and `SetResourceReference(FontSizeProperty, "T.Body")` pass; and a scan of a temporary tree reports a violation in `Views/Deep/A.xaml` and one in `Shell/B.cs`, and nothing for the same text under `Theme/Generated`.
- [x] Settle the review's findings in `tests/Bosun.Panel.Tests/XamlRules.cs`: a C# statement broken over lines and a `<Setter.Value>` judged as its setter's property (round 1); a font size with a unit, and C# block comments blanked before matching so they neither hide a literal nor make commented-out code count (completion review). Done when: `tests/Bosun.Panel.Tests/XamlRulesTests.cs` holds a failing fixture for each and a passing one for commented-out code.
- [x] Commit: `theme: contrast and stray colour tests`

**Test checkpoint:** `bosun run -- dotnet test --solution Bosun.slnx --filter-class "*ThemeContrast*" --filter-class "*XamlRules*"` exits 0, and fails if the change is reverted: with `dark.TextMute` lowered to `#5A6270` in `design/tokens/colors.json` and the theme regenerated, `ThemeContrastTests` fails; with a hex colour added to `src/Bosun.Panel/Foundation.xaml`, `XamlRulesTests` fails.

> **Verified:** 2026-11-13 | §2 | 9d23badcf6e5e28d91122e37f8caa8387e4aa77a
> **Review:** `docs/reviews/d02-t01-s2.json`

## 3. Tray state icons and the icon drift gate

> **Started:** 2026-11-13T18:51:51Z

The app ICO and its generator shipped first in §5 (the desktop shortcut needed it, 2026-11-12). This section adds the tray states to `scripts/build-icons.py` and puts its drift check in the gates. **Corrected 2026-11-12:** the first, eighth-frame and requirements items moved to §5, where they are built.

**Corrected 2026-11-13:** checked against the tree before the build. `scripts/build-icons.py` builds `resources/bosun.ico` from the masters and, for the run shortcuts (`D00 T03 §20`), a badged copy outside the repository (`badged`: a filled circle with a white rim, bottom right, composed in SVG and rendered by `resvg_py`); the tray overlays are drawn the same way, so no font is needed and `fonttools` is not added. Its `--check` reads one file (`--out`). `scripts/check-all.ps1` has no icon step; the committed ICO is kept current today only by `test_committed_ico_matches_masters` in the Python suite. `design/SPEC.md` §17 names the states (Normal, Pressure, Needs you) and says the tooltip states the same in words; the consumer is `D02 T02 §7`, which reads `resources/tray-*.ico`. Decided here (industry defaults, each cheap to change in the generator): Normal is the plain icon with no overlay, Pressure a triangle in the theme's `Warn`, Needs you a disc in its `Info`, each with the white rim the shortcut badge has, so the three differ in shape and read on a light or a dark taskbar; the colours are the dark theme's, read from `design/tokens/colors.json`, so a token change fails the drift check until the icons are rebuilt; and the frames are 16, 20, 24 and 32 px (the notification area at 100%, 125%, 150% and 200% scaling; the section said 16, 20 and 24), with 16 and 20 px from the 16 px master, 24 from the 24 px one and 32 from the 32 px one, as `design/SPEC.md` §18 has the small sizes drawn.

**Corrected 2026-11-13 (plan review):** the plan review in `docs/reviews/02-shell/D02-T01-s3.md` returned four gaps (1 P1, 3 P2), planned in below. The drift cases are exact: a token edit can never change `tray-normal.ico`, so `dark.Warn` flags Pressure alone and `dark.Info` Needs you alone, the 256 px master `bosun.ico` alone and a small master all four. The shapes are proved as shapes (how much of its bounding box each overlay fills, where it sits, its rim), not only as differences. The rim is the theme's `Base`, not white: a white rim vanishes on a light taskbar, where amber on light grey is 1.6:1, while a dark rim keeps the shape's edge at 3:1 or better against its own fill on any background (this replaces the white rim decided above). The token reader refuses bad input before anything is written, and an eight-digit colour is split into SVG `fill` and `fill-opacity`, since SVG reads `#RRGGBBAA` where the tokens are `#AARRGGBB`. And the four files are staged before any is replaced, with a failure reported as `scripts/build-theme.py` reports one.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Add tray state overlays to `scripts/build-icons.py`. Done when: Normal, Pressure and Needs you exist at 16, 20, 24 and 32 px.
2. Gate the drift check. Done when: `scripts/check-all.ps1` fails on a hand-edited ICO.

- [x] Generate `resources/tray-normal.ico`, `tray-pressure.ico` and `tray-needs.ico` from `scripts/build-icons.py` (`TRAY_FRAMES`, `TRAY_STATES`, `build_tray`) with a shape difference, not only colour, in the state colours from the §1 theme (`dark.Warn` and `dark.Info` of `design/tokens/colors.json`): each file holds frames of 16, 20, 24 and 32 px drawn from the masters `design/SPEC.md` §18 names for those sizes, Normal with no overlay, Pressure with a triangle and Needs you with a disc, bottom right with a rim in the theme's `Base`. Done when: `scripts/tests/test_build_icons.py` reads four frames of the right sizes from each file; the three differ at every size in greyscale; Normal's frames are the plain render of their masters; at 32 px the pixels each overlay changes lie in its box, flush in the bottom-right corner (**Corrected 2026-11-13:** this said the bottom-right quadrant; looked at, a box that small drew a smudge at 16 px, so the box is two thirds of the frame, in whole pixels), the disc's fill covers 0.70 to 0.85 of its bounding box and is symmetric left to right and top to bottom, and the triangle's covers 0.40 to 0.60 of its and narrows towards the top (so two discs of different sizes could not pass); the centre of each overlay is its token's colour and its edge is `Base`; each fill is at least 3:1 against that rim; and the rendered frames are looked at, at 16 and 32 px on a light and on a dark background, with what was seen recorded in the commit body.
- [x] Read the overlay colours safely in `scripts/build-icons.py` (`tray_colours`): `dark.Warn`, `dark.Info` and `dark.Base` from `design/tokens/colors.json` (or `--tokens <file>`), refusing with exit 2, a message naming the path and nothing written a token file that can't be read or isn't JSON, a missing one of the three, and a colour that isn't `#RRGGBB` or `#AARRGGBB`; an eight-digit colour is drawn as `fill="#RRGGBB"` with `fill-opacity` from its leading alpha. Done when: tests run each refusal and find the four files byte for byte as they were, and a Pressure built with `dark.Warn` `#80FFB547` has a centre pixel of that colour at about half strength over the frame beneath, not the colour SVG would read from the same eight digits.
- [x] Write the four files as one set in `scripts/build-icons.py` (`write_set`): each is staged as a temporary file beside its target before any is replaced; a staging failure changes nothing; a replace that fails leaves that file and the ones after it as they were, removes the temporary files, and exits 3 saying which files were updated and which were not. Done when: tests fail the third staging (nothing changed, no temporary file left) and the second replace (the first file new, the rest byte for byte old, the report naming both groups), `--check` still names the stale files, and a retry completes the set.
- [x] Cover all four files with `--check` and the default build in `scripts/build-icons.py`: with no `--out`, the build writes `resources/bosun.ico` and the three tray files, and `--check` exits 1 naming each of the four that is missing or differs from what the masters and tokens give, with the repair command; with `--out` the app icon alone is built or checked, as today, unless `--tray-out <folder>` names where the tray files go (the tests' form). A badged icon (`--badge-fill`) is unchanged. Done when: tests read exit 0 for the committed four; exit 1 naming only `tray-pressure.ico` after it is hand-edited; only `tray-pressure.ico` after `dark.Warn` changes, only `tray-needs.ico` after `dark.Info` changes, all three tray files' overlays being untouched by a change to `dark.Accent`, which names none; only `bosun.ico` after the 256 px master is edited, and all four after the 16 px master is; and a rebuild makes each pass again.
- [x] Run `python scripts/build-icons.py --check` from `scripts/check-all.ps1` as the `icons` step, after `theme`, and give the order in `AGENTS.md` and `tests/check_all_test.ps1`. Done when: a hand-edited `resources/bosun.ico` fails the gate (`pwsh scripts/check-all.ps1 -Only icons` exits nonzero) and a rebuild passes it. Cheaper substitute: no drift check, as with `steward.ico`.
- [x] Add `fonttools` to `scripts/requirements-icons.txt` only if the tray glyphs need it. Done when: the overlays are drawn as SVG shapes, so the file is unchanged, and a clean environment built from it runs the generator: CI's fresh runner installs exactly that file (`.github/workflows/build.yml`, `plan.yml`) and runs `test_build_icons` and the `icons` step.
- [x] Commit: `brand: tray state icons and the icon drift gate`

**Test checkpoint:** `python scripts/build-icons.py --check` exits 0 with all four ICO files, and exits nonzero after a master is edited; `python scripts/run-tests.py test_build_icons` passes; and `pwsh scripts/check-all.ps1 -Only icons` fails after a hand edit of `resources/bosun.ico`.

> **Verified:** 2026-11-13 | §3 | 6eff3036ee5b0d481f35bd6f9e52e5e0c2265603
> **Review:** `docs/reviews/d02-t01-s3.json`

## 4. Control templates

Stock WPF chrome breaks a dark theme piece by piece. Steward's templates are proven and only need retargeting to generated tokens.

**Phase:** 2
**Needs:** none
**Surface:** no

**Corrected 2026-11-13:** in Steward the `Btn`, `Btn.Primary` and `Btn.Danger` styles live in `Theme\Dark.xaml` (lines 97, 133, 139), not `Controls.xaml`, and `ScrollBar`, `CheckBox` and `ComboBox` there are implicit styles (`TargetType` only, `Controls.xaml:144`, `:323`, and `ComboBoxItem` at `:361`), not keyed ones: they are ported from where they are, as implicit styles where Steward has them so, and `Btn.Small` is new, as the item says.

**Corrected 2026-11-15:** checked against the tree and the design before the build. What the section said before is in the four items it had (one port item, the tab assertions, focus and height, the table); they are split and made exact below. The facts and the decisions, each with what reversing it costs:

- Steward's templates are dark only and name their brushes with `StaticResource`. Bosun swaps `Theme/Generated/Dark.xaml` and `Light.xaml` live (`design/SPEC.md` §1, built by `D02 T02 §5`), and a `StaticResource` is read once, so every key of the generated theme (`B.*`, `R.*`, `S.*`, `T.*`) is named with `DynamicResource` here, and the swap repaints with nothing reloaded. `StaticResource` is used only for keys `Controls.xaml` itself holds (`BasedOn`, the fonts, the focus ring). Reversal cost: a find and replace, and `D02 T02 §5` reloading `Controls.xaml` on every swap.
- The shapes follow Bosun's own mockup (`design/mockups/control-panel.html:41`, `:42`, `:50`, `:39`), not Steward's: a button is 32 px high on `Raised` with a `Line` border and medium weight, hovering to `Hover` and `LineStrong`; `Btn.Primary` is `Accent` with `OnAccent` words; `Btn.Danger` is `Danger` words and border on nothing, hovering to `DangerSoft`; a field is `Raised` with a `Line` border that turns `Accent` with keyboard focus; a table has `TextMute` caption headers and `Line` rules between rows, hovering to `Hover`. Steward's are the proven structure (template parts, triggers, the keyboard path).
- The mockup's `.mono` and `.note` are 12 px, and the tokens hold no 12 (`Caption` 11, `Body` 13): `T.Mono` and `Field` take `T.Body`. Reversal cost: a new type token.
- Steward's `F.Ui`, `F.Display` and `F.Mono` font families (`Theme\Dark.xaml:52`) are needed by these styles and are not tokens (`design/SPEC.md` names colours, radii, elevation, motion, spacing and type sizes): they are declared once, in `Controls.xaml`.
- Steward's implicit `TextBlock` style (`Theme\Dark.xaml:66`) is not ported: an implicit `TextBlock` style in the application's resources reaches the text inside every control template and overrides the control's own `Foreground`, so the words of `Btn.Danger` would draw in `Text`. Text takes its font and colour by inheritance from the window, which `D02 T02 §4` sets. Reversal cost: one style.
- `Btn.Chrome`'s close hover wrote `White` (`Controls.xaml:29`), a literal: it is `B.Base` on `B.Danger`, which `ThemeContrastTests` must hold at 4.5:1 in both themes.
- Steward's rail row template draws a `PackIconLucide` bound to the row's `Icon` (`Controls.xaml:62`). Bosun has no icon pack until `D02 T03 §3`, whose map is the only place a glyph kind may be named, so `NavList` here styles the row (hover, selected, the marker bar, the focus ring) and shows the row's content through a `ContentPresenter`; the row's data template is built by `D02 T02 §4` (its `Section` record) and the glyph and count badge by `D02 T03 §3`, each with an item added there on 2026-11-15.
- Steward's `LogBox` carries a `DataTemplate` for its own `LogEntry` type (`Controls.xaml:113`). Bosun's entry type is `ActivityLog`'s, built by `D02 T02 §4`, so `LogBox` here is the list and its row style, and the entry template lands with the type, with an item added there on 2026-11-15.
- Steward's `ComboBox` template writes the words "no profile" and "no profiles found" (`Controls.xaml:414`, `:432`). Here the placeholder is the control's `Tag`, shown while nothing is selected, and no words are written in the template.
- Steward's `PasswordBox` style is not ported: the panel holds no credential (`design/SPEC.md`, Scope).
- `ToolTip` is added: every icon-only control has a tooltip (`design/SPEC.md` §1), and the stock one is a light box on a dark window.
- Steward's small corners are literals (3 on the check box, 4 on a list row, 2 on the marker), and `D02 T03 §1` will fail a literal `CornerRadius`. Rows and fields take `R.Control`, the marker and the scroll thumb `R.Pill`, and the check box needs a corner no token gives (8 on a 16 px box is a circle, which reads as a radio button): `radius.Mark` (4) is added to `design/tokens/colors.json` and to `design/SPEC.md` §19, Rounding, dated, and both generated files are rebuilt. Reversal cost: one token.
- `App.xaml` merges only `Foundation.xaml` today, so nothing would read these styles: it merges `Theme/Generated/Dark.xaml` (until `D02 T02 §5` chooses and swaps the theme) and `Theme/Controls.xaml`.
- The checkpoint named `dotnet test Bosun.slnx --filter FullyQualifiedName~ControlStyles`, which the test platform here does not take: it is `--solution Bosun.slnx --filter-class`, as `D02 T02 §3` runs.

**Build order:**

1. Port `Controls.xaml` to `src/Bosun.Panel/Theme/Controls.xaml`. Done when: every style resolves in both themes.
2. Add the missing `Btn.Small` and focus visuals. Done when: keyboard focus is visible on all controls.
3. Add the table style and the gallery in `tests/Bosun.Panel.Tests/Render/RenderCatalog.cs`. Done when: `pwsh scripts/render-pages.ps1` writes the gallery and the table in both themes.

- [ ] Add `radius.Mark` (4) to `design/tokens/colors.json`, name it in `design/SPEC.md` §19 Rounding (dated, citing this section) and rebuild with `python scripts/build-theme.py`. Done when: `python scripts/build-theme.py --check` exits 0 and both generated files hold `R.Mark`. Cheaper substitute: a literal corner in a template.
- [ ] Create `src/Bosun.Panel/Theme/Controls.xaml` with the fonts `F.Ui`, `F.Display` and `F.Mono` (after `D:\Steward\src\Steward.Console\Theme\Dark.xaml:52`), the text styles `T.Mono`, `T.SectionLabel` and `FieldLabel`, and the `FocusRing` style: a 2 px `B.Accent` outline, 2 px outside the control, with the control's corner (`design/mockups/control-panel.html:42`). Done when: the file loads in WPF with either generated theme merged beside it and each of those keys is in it.
- [ ] Port `Btn`, `Btn.Primary`, `Btn.Danger` and `Btn.Chrome` to `Controls.xaml`, with the shapes and states of `design/mockups/control-panel.html:41`: hover, pressed, disabled at 0.4 opacity with the arrow cursor, and `Btn.Chrome` 46 by 44 with its `Tag` of `danger` hovering to `B.Danger` under `B.Base`. Done when: `tests/Bosun.Panel.Tests/ControlStylesTests.cs` applies each style to a `Button` in each theme, and reads back the brushes of the style and of its hover and disabled triggers as that theme's. Cheaper substitute: literal colours left in a template.
- [ ] Add `Btn.Small` to `Controls.xaml`: `Btn` with `T.Caption` words and 8 px side padding, still 32 px high (`design/SPEC.md` §1: every target is at least 32 px high). Done when: the style walk reads its minimum height as 32.
- [ ] Port `NavList` to `Controls.xaml`: the list and its row style, with hover on `B.Hover`, the selected row on `B.Raised` with a 3 px `B.Accent` marker bar and `B.Accent` words, and the row's content through a `ContentPresenter` (the row template is `D02 T02 §4`'s, the glyph and badge `D02 T03 §3`'s). Done when: a test selects a row of a `ListBox` in the style and reads the marker's brush as the theme's `Accent`, and an unselected row's as transparent. Cheaper substitute: the selected row told by colour alone, with no bar.
- [ ] Port `LogBox` to `Controls.xaml`: the list and its row style, with no horizontal scrolling and rows that are not tab stops (a line is read, not acted on), leaving the entry template to `D02 T02 §4`. Done when: a test fills a `ListBox` in the style with strings and finds each drawn, and the row's `Focusable` false.
- [ ] Port the implicit `ScrollBar` style to `Controls.xaml`: a 10 px bar with no stepper buttons and an `R.Pill` thumb in `B.Line`, `B.TextMute` under the pointer, in both orientations. Done when: a test scrolls a `ScrollViewer` taller than its space and finds the thumb with the theme's `Line` brush.
- [ ] Port `TabStrip` and `TabStrip.Item` to `Controls.xaml`, with the trailing slot (`Tag`) beside the tabs. Done when: a test applies `TabStrip` to a `TabControl` of two tabs and the selected one's words read back in the theme's `Accent`.
- [ ] Port the `TabStrip.Item` assertions of `D:\Steward\tests\Steward.Console.Tests\TabStripStyleTests.cs` to `tests/Bosun.Panel.Tests/TabStripStyleTests.cs` (**Added 2026-11-13** from `D02 T01 §2`, which ports that file's no-colour rule but has no `Controls.xaml` to read): unselected tabs draw in `B.TextDim`, the selected one in `B.Accent` with an underline, a disabled one at 0.4 opacity with its tooltip still shown, and each is at least 44 px high. Done when: the test reads `src/Bosun.Panel/Theme/Controls.xaml` and fails if a setter is changed.
- [ ] Port `Field` to `Controls.xaml` for `TextBox`: `B.Raised` with a `B.Line` border, `B.Accent` with keyboard focus, read-only words in `B.TextDim`, disabled at 0.6 opacity, the caret in `B.Text` and the selection in `B.AccentDim`. Done when: a test reads those brushes back in each theme.
- [ ] Port the implicit `CheckBox` style to `Controls.xaml`: a 16 px box with the `R.Mark` corner, `B.Accent` with an `B.OnAccent` tick when checked, the whole row (box and words) the target. Done when: a test checks the box and reads the tick visible and the box's brush as the theme's `Accent`. Cheaper substitute: the stock check box, a white square on a dark page.
- [ ] Port the implicit `ComboBox` and `ComboBoxItem` styles to `Controls.xaml`, with the drop-down on `B.Raised` under a `B.Line` border and the placeholder taken from `Tag` while nothing is selected. Done when: a test with nothing selected finds the `Tag`'s words drawn, and with an item selected finds the item's and not the placeholder's. Cheaper substitute: brush setters on the stock template, which ignores them.
- [ ] Add the implicit `ToolTip` style to `Controls.xaml`: `B.Raised` with a `B.LineStrong` border, the `R.Control` corner and `B.Text` words at `T.Caption`. Done when: a test applies it and reads those brushes back in each theme.
- [ ] Add `FocusVisualStyle` (the `FocusRing`) and a 32 px minimum height to every interactive style in `Controls.xaml`. Done when: `ControlStylesTests` walks every `Style` in the file, top level and nested, and each either sets both (a `MinHeight` or `Height` of 32 or more) or is in the test's named list of styles that are not targets, each with its reason (the text styles, the focus ring, the scroll bar, a log line, a tooltip, the table and its cells); a style added without either fails it. Cheaper substitute: asserting a list of style names typed into the test, which a new style slips past.
- [ ] Add the implicit `DataGrid` styling to `Controls.xaml` (grid, column header, row and cell): read-only by default, no grid lines but a `B.Line` rule under each row, `B.TextMute` caption headers 32 px high, rows 32 px or more that hover to `B.Hover` and select on `B.AccentDim`, and the keyed cell text styles `Cell.Text` and `Cell.Number`, the second right-aligned with `Typography.NumeralAlignment` `Tabular` (`design/mockups/control-panel.html:39`). Done when: a test lays out a grid with a text and a number column and reads the number cell's `TextBlock` as right-aligned with tabular numerals.
- [ ] Prove every theme key the file names exists, in `ControlStylesTests`: each `{DynamicResource <key>}` in `Controls.xaml` is a key of both `Theme/Generated/Dark.xaml` and `Light.xaml`, each `{StaticResource <key>}` is a key declared earlier in `Controls.xaml` itself, and no generated key is named with `StaticResource`. Done when: a misspelt key and a `StaticResource B.Text` each fail it, on a copy of the text. Cheaper substitute: loading the file and trusting it, since WPF draws a missing `DynamicResource` as nothing, without an error.
- [ ] Hold `B.Base` on `B.Danger` (the close button's hover) at 4.5:1 in `tests/Bosun.Panel.Tests/ThemeContrast.cs`, beside the text roles. Done when: `ThemeContrastTests` passes in both themes and fails with `Danger` moved toward `Base` in a copy.
- [ ] Merge `Theme/Generated/Dark.xaml` and `Theme/Controls.xaml` in `src/Bosun.Panel/App.xaml`, after `Foundation.xaml`. Done when: a test reads `App.xaml` and finds the three dictionaries in that order, and the panel builds. Cheaper substitute: styles no window can reach.
- [ ] Register a `controls` view in `tests/Bosun.Panel.Tests/Render/RenderCatalog.cs` with the states `gallery` (every style above, each in its states: a row of buttons with one disabled, the tabs, a field and a read-only one, check boxes on and off, a combo box with and without a selection, the rail list with a selected row, the log list) and `table` (a `DataGrid` of names and numbers). Done when: `pwsh scripts/render-pages.ps1` writes `controls.gallery` and `controls.table` in both themes, and the pictures have been looked at. Cheaper substitute: a table drawn with stock chrome, white headers on a dark page.
- [ ] Commit: `theme: control templates`

**Test checkpoint:** `bosun run -- dotnet test --solution Bosun.slnx --filter-class "*ControlStyles*" --filter-class "*TabStripStyle*"` exits 0, and fails if the change is reverted; `python scripts/build-theme.py --check` exits 0.

## 5. App icon ICO from the masters

> **Started:** 2026-11-13T16:10:22Z

Filed and built 2026-11-12 on the owner's request: the "Bosun unattended run" desktop shortcut showed PowerShell's icon because `scripts/start-run.ps1` looks for `resources/bosun.ico`, which §3 was to make only after the theme. The app ICO needs only the SVG masters, so it ships here with no dependency, and §3 keeps the tray states.

**Corrected 2026-11-13:** checked against the tree before its review. The section was built and committed on 2026-11-12 (`b16d74d`) with every item but the commit ticked, and never reviewed or stamped; it runs its review cycle as a section already implemented, with its checkpoint re-run. One item has been overtaken: since `D00 T03 §20` (stamped), `scripts/start-run.ps1 -InstallShortcut` writes two campaign shortcuts, each carrying a badged icon that `scripts/build-icons.py --out ... --badge-fill` renders from these same masters into the user's application data folder, and `resources\bosun.ico` is the base of those and the icon a shortcut carries when its badge can't be rendered (`tests/start_run_test.ps1` proves both). So the item's `IconLocation` of `D:\Bosun\resources\bosun.ico,0` held when it was ticked and is now the fallback case. The committed ICO is kept current by `test_committed_ico_matches_masters`, which CI runs through `scripts/run-tests.py`; the `check-all` gate step for icons is `D02 T01 §3`'s. The generator's later badge work (`ee6a9ea`, `2dce31e`, `a82113f`) belongs to `D00 T03 §20` and was reviewed there.

**Corrected 2026-11-13 (plan review):** the plan review in `docs/reviews/02-shell/D02-T01-s5.md` returned one gap (P2), planned in below: `test_frames_follow_spec` checked the masters of 16, 20, 24 and 32 px only, so 40, 48 and 64 px drawn from the 32 px master passed it, and a regenerated ICO would then have satisfied the drift check too. The test now holds `design/SPEC.md` §18's table itself and checks every frame against it, and the wrong-master mutation is kept as a test.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Create `scripts/build-icons.py` with `resvg_py`. Done when: `resources/bosun.ico` regenerates byte for byte.
2. Point the shortcut at it. Done when: `pwsh scripts/start-run.ps1 -InstallShortcut` writes a shortcut whose icon is `resources\bosun.ico` (**Corrected 2026-11-13:** today that is the shortcut's icon when its runner badge can't be rendered, and the base every badged icon is drawn on).

- [x] Render frames 16, 20, 24, 32, 40, 48, 64 and 256 per `design/SPEC.md` §18 in `scripts/build-icons.py` (`FRAMES`), stored as PNG frames written by `build`, citing `D:\Recto\design\tools\build_resources.py:505`. Done when: the ICO holds exactly eight frames, 16, 24 and 32 come from their own masters, and 20 comes from the 16 px master (`test_frames_follow_spec`). Cheaper substitute: downscaling the 256 frame for small sizes.
- [x] Add `--check` to `scripts/build-icons.py`, exiting 1 when `resources/bosun.ico` differs from what the masters give. Done when: `test_check_fails_after_master_edit` passes and `test_committed_ico_matches_masters` keeps the committed file current.
- [x] Write `resources/bosun.ico` with an atomic temporary-file-then-replace write (`write_atomic`). Done when: the file is committed and `--check` exits 0.
- [x] Add `scripts/requirements-icons.txt` pinning `resvg-py` and `Pillow` (the tests read frames with Pillow). Done when: the generator and `scripts/tests/test_build_icons.py` run from those two packages.
- [x] Reinstall the desktop shortcut with `pwsh scripts/start-run.ps1 -InstallShortcut`. Done when: its `IconLocation` reads `D:\Bosun\resources\bosun.ico,0` and the extracted icon is the Bosun tile.
- [x] Prove every frame's master in `scripts/tests/test_build_icons.py` (**Added 2026-11-13** from the plan review): `test_frames_follow_spec` checks each of the eight frames against the master `design/SPEC.md` §18 names, from a table written in the test, and `test_a_frame_from_the_wrong_master_is_off_spec` draws 40, 48 and 64 px from the 32 px master, 20 px from the 256 one and 256 px from the 32 one. Done when: each wrong table is named off spec for exactly its sizes, and the committed ICO still matches the masters.
- [x] Name the right repair when `--check` fails, in `scripts/build-icons.py` (`main`) (**Added 2026-11-13** from the completion review): the command it prints carries the `--masters` and `--out` it was given, quoted. Done when: `test_the_repair_a_failed_check_names_mends_what_was_checked` follows the printed command for an output path with a space and the check then passes, and the default check still names the plain command.
- [x] Commit: `brand: app icon ICO from the masters, used by the run shortcut`

**Test checkpoint:** `python scripts/run-tests.py test_build_icons` passes; rendering the 20 px frame from the 256 px master, or the 40, 48 and 64 px frames from the 32 px one, in `scripts/build-icons.py` (`FRAMES`) makes `test_frames_follow_spec` fail; and `python scripts/build-icons.py --check` exits 0.

> **Verified:** 2026-11-13 | §5 | ab7204548d6370e2b60594e373650632f1ea2c6e
> **Review:** `docs/reviews/d02-t01-s5.json`

## Verification

- `python scripts/build-theme.py --check` and `python scripts/build-icons.py --check` pass.
- `dotnet test Bosun.slnx` passes.
