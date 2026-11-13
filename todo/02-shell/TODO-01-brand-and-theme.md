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
| 1 | §1 | Generate the theme from tokens | D00 T02 §2 | [ ] |
| 2 | §2 | Contrast and stray colour tests | §1 | [ ] |
| 3 | §5 | App icon ICO from the masters | -- | [ ] |
| 4 | §3 | Tray state icons and the icon drift gate | §1, §5 | [ ] |
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

- [ ] Emit `C.*` colours and `B.*` brushes plus radius, spacing and type resources, with the key names of `D:\Steward\src\Steward.Console\Theme\Dark.xaml:14`, in `scripts/build-theme.py`: for each theme a `ResourceDictionary` with a `Color` `C.<name>` and a frozen `SolidColorBrush` `B.<name>` for each colour, in the token file's order, a `CornerRadius` `R.<name>` for each radius, and a `sys:Double` `S.<name>` for each space and `T.<name>` for each type size, under a header that names the generator and the token file and says not to edit the file, written temporary-file-then-replace. Done when: both files load in a WPF test, `tests/Bosun.Panel.Tests/ThemeTests.cs`, through `XamlReader` on an STA thread, and for each theme every key the token file gives (each colour's `C.` and `B.`, each radius, space and type size) reads back with its type and value, each brush frozen and of its colour, and no other key is in the dictionary. Cheaper substitute: editing the generated XAML by hand.
- [ ] Refuse bad tokens in `scripts/build-theme.py` with exit 2 and a message naming the token's path (`dark.TextDim`, `radius`), writing nothing and keeping the old outputs: a token file that isn't JSON or isn't an object; a missing `dark`, `light`, `radius`, `space` or `type`, or one that isn't an object or is empty; a colour present in one theme and missing in the other; a missing key a consumer uses (the 20 colours, `Card`, `Control`, `Sheet`, `Overlay` and `Pill`, `XS` to `XL`, and `Caption`, `Body`, `Subtitle`, `Title` and `Metric`); a colour that isn't `#RRGGBB` or `#AARRGGBB`; and a radius, space or type size that isn't a number from 0 to 1000 (a radius of 999 or more, the pill, stays as it is). Done when: `scripts/tests/test_build_theme.py` runs each fixture through `--tokens` and `--out`, and reads exit 2 and the path, with the outputs in that folder byte for byte as they were.
- [ ] Add `--check` in `scripts/build-theme.py`: it writes nothing, and exits 1 naming each generated file that is missing or differs from what the tokens give (line endings aside), 0 when both match. Done when: `scripts/tests/test_build_theme.py` reads 0 on a fresh build, 1 after a hand edit and after a deleted file, and 0 again after a rebuild, and a CRLF copy of a matching file still reads 0; and, editing a copy of the token file (a dark colour, a light colour, a radius, a space and a type size, one at a time), reads 1 before regenerating, finds the edited value in the regenerated file, and reads 0 after.
- [ ] Add the check to `scripts/check-all.ps1` as the `theme` step (`python scripts/build-theme.py --check`), after `plan`. Done when: a changed generated file fails the gate, shown by `pwsh scripts/check-all.ps1 -Only theme` exiting nonzero after a hand edit and 0 after a rebuild.
- [ ] Commit: `theme: generate XAML from tokens`

**Test checkpoint:** `python scripts/build-theme.py --check` exits 0, and exits nonzero after a hand edit; `python scripts/run-tests.py` passes `test_build_theme`; and `bosun run -- dotnet test --solution Bosun.slnx --filter-class "*ThemeTests*"` passes.

## 2. Contrast and stray colour tests

Steward proved these catch real faults (it raised `TextMute` for contrast). Here they cover both themes.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Port `ThemeContrastTests` and `TabStripStyleTests`. Done when: both themes are asserted.
2. Add the no-hex rule across all XAML. Done when: a stray colour fails.

- [ ] Assert at least 4.5:1 for each text role on `Base`, `Surface` and `Raised`, in `tests/Bosun.Panel.Tests/ThemeContrastTests.cs`, citing `D:\Steward\tests\Steward.Console.Tests`. Done when: a lowered token fails the test. Cheaper substitute: checking only the dark theme.
- [ ] Fail any hex colour or font size outside `Theme/Generated` in `tests/Bosun.Panel.Tests/XamlRulesTests.cs`. Done when: a hex in a view fails it.
- [ ] Commit: `theme: contrast and stray colour tests`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~ThemeContrast` exits 0, and fails if the change is reverted.

## 3. Tray state icons and the icon drift gate

The app ICO and its generator shipped first in §5 (the desktop shortcut needed it, 2026-11-12). This section adds the tray states to `scripts/build-icons.py` and puts its drift check in the gates. **Corrected 2026-11-12:** the first, eighth-frame and requirements items moved to §5, where they are built.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Add tray state overlays to `scripts/build-icons.py`. Done when: Normal, Pressure and Needs you exist at 16, 20 and 24 px.
2. Gate the drift check. Done when: `scripts/check-all.ps1` fails on a hand-edited ICO.

- [ ] Generate `resources/tray-normal.ico`, `tray-pressure.ico` and `tray-needs.ico` from `scripts/build-icons.py` with a shape difference, not only colour, in the state colours from the §1 theme. Done when: the three frames differ at 16 px in greyscale, and `--check` covers all four ICO files.
- [ ] Run `python scripts/build-icons.py --check` from `scripts/check-all.ps1`. Done when: a hand-edited `resources/bosun.ico` fails the gate. Cheaper substitute: no drift check, as with `steward.ico`.
- [ ] Add `fonttools` to `scripts/requirements-icons.txt` only if the tray glyphs need it. Done when: a clean virtual environment from that file runs the generator.
- [ ] Commit: `brand: tray state icons and the icon drift gate`

**Test checkpoint:** `python scripts/build-icons.py --check` exits 0 with all four ICO files, and exits nonzero after a master is edited.

## 4. Control templates

Stock WPF chrome breaks a dark theme piece by piece. Steward's templates are proven and only need retargeting to generated tokens.

**Phase:** 2
**Needs:** none
**Surface:** no

**Corrected 2026-11-13:** in Steward the `Btn`, `Btn.Primary` and `Btn.Danger` styles live in `Theme\Dark.xaml` (lines 97, 133, 139), not `Controls.xaml`, and `ScrollBar`, `CheckBox` and `ComboBox` there are implicit styles (`TargetType` only, `Controls.xaml:144`, `:323`, and `ComboBoxItem` at `:361`), not keyed ones: they are ported from where they are, as implicit styles where Steward has them so, and `Btn.Small` is new, as the item says.

**Build order:**

1. Port `Controls.xaml` to `src/Bosun.Panel/Theme/Controls.xaml`. Done when: every style resolves in both themes.
2. Add the missing `Btn.Small` and focus visuals. Done when: keyboard focus is visible on all controls.

- [ ] Port `Btn`, `Btn.Primary`, `Btn.Danger`, `Btn.Chrome`, `LogBox`, `NavList`, `ScrollBar`, `TabStrip`, `Field`, `CheckBox` and `ComboBox` from `D:\Steward\src\Steward.Console\Theme\Controls.xaml`. Done when: each style applies without a resource lookup failure in both themes. Cheaper substitute: literal colours left in a template.
- [ ] Add `FocusVisualStyle` and a 32 px minimum height for every interactive control. Done when: a test walks the styles and asserts both.
- [ ] Add `DataGrid` styling with tabular numbers for the panel's many tables. Done when: an off-screen render of a table shows it in both themes.
- [ ] Commit: `theme: control templates`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~ControlStyles` exits 0, and fails if the change is reverted.

## 5. App icon ICO from the masters

Filed and built 2026-11-12 on the owner's request: the "Bosun unattended run" desktop shortcut showed PowerShell's icon because `scripts/start-run.ps1` looks for `resources/bosun.ico`, which §3 was to make only after the theme. The app ICO needs only the SVG masters, so it ships here with no dependency, and §3 keeps the tray states.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Create `scripts/build-icons.py` with `resvg_py`. Done when: `resources/bosun.ico` regenerates byte for byte.
2. Point the shortcut at it. Done when: `pwsh scripts/start-run.ps1 -InstallShortcut` writes a shortcut whose icon is `resources\bosun.ico`.

- [x] Render frames 16, 20, 24, 32, 40, 48, 64 and 256 per `design/SPEC.md` §18 in `scripts/build-icons.py` (`FRAMES`), stored as PNG frames written by `build`, citing `D:\Recto\design\tools\build_resources.py:505`. Done when: the ICO holds exactly eight frames, 16, 24 and 32 come from their own masters, and 20 comes from the 16 px master (`test_frames_follow_spec`). Cheaper substitute: downscaling the 256 frame for small sizes.
- [x] Add `--check` to `scripts/build-icons.py`, exiting 1 when `resources/bosun.ico` differs from what the masters give. Done when: `test_check_fails_after_master_edit` passes and `test_committed_ico_matches_masters` keeps the committed file current.
- [x] Write `resources/bosun.ico` with an atomic temporary-file-then-replace write (`write_atomic`). Done when: the file is committed and `--check` exits 0.
- [x] Add `scripts/requirements-icons.txt` pinning `resvg-py` and `Pillow` (the tests read frames with Pillow). Done when: the generator and `scripts/tests/test_build_icons.py` run from those two packages.
- [x] Reinstall the desktop shortcut with `pwsh scripts/start-run.ps1 -InstallShortcut`. Done when: its `IconLocation` reads `D:\Bosun\resources\bosun.ico,0` and the extracted icon is the Bosun tile.
- [ ] Commit: `brand: app icon ICO from the masters, used by the run shortcut`

**Test checkpoint:** `python scripts/run-tests.py test_build_icons` passes; rendering the 20 px frame from the 256 px master makes `test_frames_follow_spec` fail.

## Verification

- `python scripts/build-theme.py --check` and `python scripts/build-icons.py --check` pass.
- `dotnet test Bosun.slnx` passes.
