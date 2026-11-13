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

Hand-edited XAML is how themes drift. The generator owns the colour resources and everything else refers to them.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Create `scripts/build-theme.py`. Done when: it writes `src/Bosun.Panel/Theme/Generated/Dark.xaml` and `Light.xaml` from `design/tokens/colors.json`.
2. Add `--check`. Done when: a hand edit makes it exit nonzero.

- [ ] Emit `C.*` colours and `B.*` brushes plus radius, spacing and type resources, with the key names of `D:\Steward\src\Steward.Console\Theme\Dark.xaml:14`. Done when: both files load in a WPF test. Cheaper substitute: editing the generated XAML by hand.
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
