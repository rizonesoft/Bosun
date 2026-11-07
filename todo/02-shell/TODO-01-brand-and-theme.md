---
schema_version: 1
id: brand-and-theme
domain: 02-shell
status: active
title: "TODO-01 -- Brand and theme"
---

# TODO-01 -- Brand and theme

> **Goal:** Every colour and icon traces to one source, with both themes accessible and a drift check that fails on hand edits.

> **Current state:** Measured 2026-11-07. `design/tokens/colors.json` and the icon SVG masters in `design/icon/` exist from the 2026-11-07 brainstorm. No generator, ICO or XAML exists. Steward's theme is hand-edited and dark only.

## Inputs

- `design/SPEC.md` §1 and §18.
- `R:\GitHub\Office\scripts\build-icons.py`, `R:\GitHub\Recto\design\tools\build_resources.py:505`, `R:\GitHub\Isotone\docs\design\app-icons.md`.
- `R:\GitHub\Steward\src\Steward.Console\Theme\Controls.xaml`.
- -> XREF: D00 T02 §2 -- consumed by this file
- -> XREF: D02 T02 §9 -- consumed by this file
- -> XREF: D02 T02 §1 -- consumes this file

## Outcome

- A token edit regenerates dark and light XAML, and `--check` fails on drift.
- `resources/devmode.ico` carries eight frames and regenerates byte for byte.

**Adjacency:** all=not-applicable (internal tooling and contracts with no records of their own)

**Adjacency rationale:** Assets and styles only.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Generate the theme from tokens | D00 T02 §2 | [ ] |
| 2 | §2 | Contrast and stray colour tests | §1 | [ ] |
| 3 | §3 | Icon generator and ICO | §1 | [ ] |
| 4 | §4 | Control templates | §2, D02 T02 §9 | [ ] |

## 1. Generate the theme from tokens

Hand-edited XAML is how themes drift. The generator owns the colour resources and everything else refers to them.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Create `scripts/build-theme.py`. Done when: it writes `src/DevMode.Panel/Theme/Generated/Dark.xaml` and `Light.xaml` from `design/tokens/colors.json`.
2. Add `--check`. Done when: a hand edit makes it exit nonzero.

- [ ] Emit `C.*` colours and `B.*` brushes plus radius, spacing and type resources, with the key names of `R:\GitHub\Steward\src\Steward.Console\Theme\Dark.xaml:14`. Done when: both files load in a WPF test. Cheaper substitute: editing the generated XAML by hand.
- [ ] Refuse a token present in one theme and missing in the other. Done when: a fixture exits 2 and names the token.
- [ ] Add `--check` to `scripts/check-all.ps1`. Done when: a changed generated file fails the gate.
- [ ] Commit: `theme: generate XAML from tokens`

**Test checkpoint:** `python scripts/build-theme.py --check` exits 0, and exits nonzero after a hand edit.

## 2. Contrast and stray colour tests

Steward proved these catch real faults (it raised `TextMute` for contrast). Here they cover both themes.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Port `ThemeContrastTests` and `TabStripStyleTests`. Done when: both themes are asserted.
2. Add the no-hex rule across all XAML. Done when: a stray colour fails.

- [ ] Assert at least 4.5:1 for each text role on `Base`, `Surface` and `Raised`, in `tests/DevMode.Panel.Tests/ThemeContrastTests.cs`, citing `R:\GitHub\Steward\tests\Steward.Console.Tests`. Done when: a lowered token fails the test. Cheaper substitute: checking only the dark theme.
- [ ] Fail any hex colour or font size outside `Theme/Generated` in `tests/DevMode.Panel.Tests/XamlRulesTests.cs`. Done when: a hex in a view fails it.
- [ ] Commit: `theme: contrast and stray colour tests`

**Test checkpoint:** `devmode run -- dotnet test DevMode.slnx --filter FullyQualifiedName~ThemeContrast` exits 0, and fails if the change is reverted.

## 3. Icon generator and ICO

The SVG masters are a first design. This makes them a build product, deterministic, with the frame set Steward's single ICO lacks.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Create `scripts/build-icons.py` with `resvg_py` and Pillow. Done when: `resources/devmode.ico` and PNGs regenerate identically.
2. Add tray state overlays. Done when: Normal, Pressure and Needs you exist at 16, 20 and 24 px.

- [ ] Render frames 16, 20, 24, 32, 40, 48, 64 and 256 per `design/SPEC.md` §18, citing `R:\GitHub\Recto\design\tools\build_resources.py:505`. Done when: the ICO holds exactly eight frames, 16, 24 and 32 come from their own masters, and 20 comes from the 16 px master. Cheaper substitute: downscaling the 256 frame for small sizes.
- [ ] Add `--check` to `scripts/build-icons.py` and to `scripts/check-all.ps1`. Done when: a changed PNG fails. Cheaper substitute: no drift check, as with `steward.ico`.
- [ ] Generate `resources/tray-normal.ico`, `tray-pressure.ico` and `tray-needs.ico` with a shape difference, not only colour. Done when: the three frames differ at 16 px in greyscale.
- [ ] Add `scripts/requirements-icons.txt` pinning `resvg-py`, `Pillow` and `fonttools`. Done when: a clean virtual environment runs the generator.
- [ ] Commit: `brand: icon generator, ICO and tray states`

**Test checkpoint:** `python scripts/build-icons.py --check` exits 0, and exits nonzero after a master is edited.

## 4. Control templates

Stock WPF chrome breaks a dark theme piece by piece. Steward's templates are proven and only need retargeting to generated tokens.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Port `Controls.xaml` to `src/DevMode.Panel/Theme/Controls.xaml`. Done when: every style resolves in both themes.
2. Add the missing `Btn.Small` and focus visuals. Done when: keyboard focus is visible on all controls.

- [ ] Port `Btn`, `Btn.Primary`, `Btn.Danger`, `NavList`, `ScrollBar`, `TabStrip`, `Field`, `CheckBox` and `ComboBox` from `R:\GitHub\Steward\src\Steward.Console\Theme\Controls.xaml`. Done when: each style applies without a resource lookup failure in both themes. Cheaper substitute: literal colours left in a template.
- [ ] Add `FocusVisualStyle` and a 32 px minimum height for every interactive control. Done when: a test walks the styles and asserts both.
- [ ] Add `DataGrid` styling with tabular numbers for the panel's many tables. Done when: an off-screen render of a table shows it in both themes.
- [ ] Commit: `theme: control templates`

**Test checkpoint:** `devmode run -- dotnet test DevMode.slnx --filter FullyQualifiedName~ControlStyles` exits 0, and fails if the change is reverted.

## Verification

- `python scripts/build-theme.py --check` and `python scripts/build-icons.py --check` pass.
- `dotnet test DevMode.slnx` passes.
