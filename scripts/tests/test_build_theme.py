"""Tests for scripts/build-theme.py: the generated theme follows the tokens, bad tokens are refused by path with nothing
written, and --check fails a hand edit and a token edit until the theme is rebuilt (D02 T01 §1)."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import re
import shutil
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("build_theme", ROOT / "scripts" / "build-theme.py")
assert SPEC is not None and SPEC.loader is not None
theme = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(theme)


class BuildThemeTest(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.tokens = self.tmp / "colors.json"
        shutil.copyfile(ROOT / "design" / "tokens" / "colors.json", self.tokens)
        self.out = self.tmp / "Generated"

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def run_theme(self, *extra):
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            code = theme.main(["--tokens", str(self.tokens), "--out", str(self.out), *extra])
        return code, err.getvalue()

    def load(self):
        return json.loads(self.tokens.read_text(encoding="utf-8"))

    def save(self, tokens):
        self.tokens.write_text(json.dumps(tokens, indent=2), encoding="utf-8")

    def outputs(self):
        return {p.name: p.read_bytes() for p in sorted(self.out.glob("*"))}

    def test_writes_every_token_with_the_steward_key_names(self):
        self.assertEqual(self.run_theme(), (0, ""))
        tokens = self.load()
        for name in ("dark", "light"):
            text = (self.out / f"{name.capitalize()}.xaml").read_text(encoding="utf-8")
            self.assertTrue(text.startswith(theme.HEADER), "the header says it is generated")
            for key, value in tokens[name].items():
                self.assertIn(f'<Color x:Key="C.{key}">{value.upper()}</Color>', text)
                self.assertIn(f'<SolidColorBrush x:Key="B.{key}" Color="{{StaticResource C.{key}}}" po:Freeze="True"/>', text)
            for group, prefix, element in theme.MEASURES:
                for key, value in tokens[group].items():
                    self.assertIn(f'<{element} x:Key="{prefix}.{key}">{value}</{element}>', text)
            # D02 T03 §1: each elevation at this theme's opacity, and the motion, equal to the tokens.
            opacity = "OpacityDark" if name == "dark" else "OpacityLight"
            for key, value in tokens["elevation"].items():
                self.assertIn(f'<DropShadowEffect x:Key="Elevation.{key}" BlurRadius="{value["Blur"]}" ShadowDepth="{value["Depth"]}" '
                              f'Direction="270" Color="#FF000000" Opacity="{value[opacity]}" po:Freeze="True"/>', text)
            durations = {k: v for k, v in tokens["motion"].items() if k != "Ease"}
            for key, ms in durations.items():
                self.assertIn(f'<Duration x:Key="Motion.{key[:-2]}">0:0:{ms // 1000}.{ms % 1000:03d}</Duration>', text)
            x1, y1, x2, y2 = tokens["motion"]["Ease"]
            self.assertIn(f'<KeySpline x:Key="Motion.Ease" ControlPoint1="{x1},{y1}" ControlPoint2="{x2},{y2}" po:Freeze="True"/>', text)
            keys = re.findall(r'x:Key="([^"]+)"', text)
            self.assertEqual(len(keys), len(set(keys)))
            self.assertEqual(len(keys), 2 * len(tokens[name]) + sum(len(tokens[g]) for g, _, _ in theme.MEASURES)
                             + len(tokens["elevation"]) + len(durations) + 1)
        self.assertNotIn(b"\r\n", (self.out / "Dark.xaml").read_bytes())

    def test_the_two_themes_differ_only_in_their_colours_and_shadow_opacities(self):
        self.run_theme()
        dark = (self.out / "Dark.xaml").read_text(encoding="utf-8")
        light = (self.out / "Light.xaml").read_text(encoding="utf-8")
        self.assertNotEqual(dark, light)
        strip = lambda t: re.sub(r'Opacity="[0-9.]+"', 'Opacity=""', re.sub(r"#[0-9A-F]{6,8}", "#", t))
        self.assertEqual(strip(dark), strip(light))
        shadows = lambda t: re.findall(r'x:Key="(Elevation\.\w+)"[^>]* Opacity="([0-9.]+)"', t)
        self.assertNotEqual(shadows(dark), shadows(light), "each theme has its own shadow strength")

    def test_a_changed_elevation_or_duration_reaches_the_generated_theme(self):
        tokens = self.load()
        tokens["elevation"]["Card"]["Blur"] = 24
        tokens["motion"]["FastMs"] = 90
        tokens["motion"]["Ease"] = [0.4, 0, 0.2, 1]
        self.save(tokens)
        self.assertEqual(self.run_theme(), (0, ""))
        for name in ("Dark", "Light"):
            text = (self.out / f"{name}.xaml").read_text(encoding="utf-8")
            self.assertRegex(text, r'x:Key="Elevation\.Card" BlurRadius="24" ')
            self.assertIn('<Duration x:Key="Motion.Fast">0:0:0.090</Duration>', text)
            self.assertIn('ControlPoint1="0.4,0" ControlPoint2="0.2,1"', text)

    def test_bad_tokens_are_refused_by_path_and_nothing_is_written(self):
        self.run_theme()
        before = self.outputs()
        good = self.load()

        def without(group, key):
            t = json.loads(json.dumps(good))
            del t[group][key]
            return t

        def setting(group, key, value):
            t = json.loads(json.dumps(good))
            t[group][key] = value
            return t

        def replacing(group, value):
            t = json.loads(json.dumps(good))
            t[group] = value
            return t

        both_lack = json.loads(json.dumps(good))
        del both_lack["dark"]["TextDim"]
        del both_lack["light"]["TextDim"]
        missing_group = json.loads(json.dumps(good))
        del missing_group["space"]

        def elevation(key, part, value):
            t = json.loads(json.dumps(good))
            if value is None:
                del t["elevation"][key][part]
            else:
                t["elevation"][key][part] = value
            return t

        no_motion = json.loads(json.dumps(good))
        del no_motion["motion"]
        cases = [
            (without("light", "OkSoft"), "light.OkSoft"),             # in dark only
            (setting("light", "Extra", "#123456"), "dark.Extra"),     # in light only
            (both_lack, "dark.TextDim"),                              # both lack a key a consumer uses
            (without("radius", "Control"), "radius.Control"),
            (without("type", "Metric"), "type.Metric"),
            (setting("dark", "Base", "#12345"), "dark.Base"),
            (setting("light", "Ok", "green"), "light.Ok"),
            (setting("dark", "Ok", 5), "dark.Ok"),
            (setting("radius", "Card", -1), "radius.Card"),
            (setting("space", "M", 1001), "space.M"),
            (setting("type", "Body", "13"), "type.Body"),
            (setting("type", "Body", True), "type.Body"),
            (setting("radius", "Card", 10 ** 400), "radius.Card"),    # round 1: past any float, still a refusal
            (setting("space", "M", float("nan")), "space.M"),
            (setting("space", "Extra\n", 12), "space.Extra"),         # round 1: a trailing newline is no name
            (setting("dark", "Base", "#123456\n"), "dark.Base"),
            (replacing("radius", {}), "radius: is empty"),
            (replacing("space", [4, 8]), "space: must be an object"),
            (missing_group, "space: missing"),
            # D02 T03 §1: elevation and motion
            (without("elevation", "Overlay"), "elevation.Overlay: missing"),
            (elevation("Card", "Blur", None), "elevation.Card.Blur: missing"),
            (elevation("Card", "Blur", "20"), "elevation.Card.Blur"),
            (elevation("Card", "Depth", -1), "elevation.Card.Depth"),
            (elevation("Overlay", "OpacityDark", 1.5), "elevation.Overlay.OpacityDark: must be a number from 0 to 1"),
            (elevation("Card", "OpacityLight", None), "elevation.Card.OpacityLight: missing"),
            (elevation("Card", "Spread", 3), "elevation.Card.Spread: not an elevation's part"),
            (setting("elevation", "Card", 20), "elevation.Card: must be an object"),
            (no_motion, "motion: missing"),
            (without("motion", "NormalMs"), "motion.NormalMs: missing"),
            (without("motion", "Ease"), "motion.Ease: missing"),
            (setting("motion", "FastMs", "120"), "motion.FastMs: must be a whole number"),
            (setting("motion", "SlowMs", 400.5), "motion.SlowMs: must be a whole number"),
            (setting("motion", "PulseMs", 10001), "motion.PulseMs"),
            (setting("motion", "FastMs", True), "motion.FastMs"),
            (setting("motion", "Ease", [0.2, 0.8, 0.2]), "motion.Ease: must be four numbers"),
            (setting("motion", "Ease", [0.2, 1.4, 0.2, 1]), "motion.Ease[1]: must be a number from 0 to 1"),
            (setting("motion", "Ease", [-0.1, 0.8, 0.2, 1]), "motion.Ease[0]"),
            (setting("motion", "Bounce", 3), "motion.Bounce: a motion token is a duration"),
            ([], "must be a JSON object"),
        ]
        for tokens, path in cases:
            with self.subTest(path=path):
                self.tokens.write_text(json.dumps(tokens), encoding="utf-8")
                code, err = self.run_theme()
                self.assertEqual(code, 2)
                self.assertIn(path, err)
                self.assertIn("nothing was written", err)
                self.assertEqual(self.outputs(), before, "the old outputs are kept byte for byte")
        self.tokens.write_text("{ not json", encoding="utf-8")
        code, err = self.run_theme()
        self.assertEqual(code, 2)
        self.assertIn("not JSON", err)
        self.assertEqual(self.outputs(), before)

    def test_check_fails_a_hand_edit_and_a_deleted_file_until_rebuilt(self):
        self.assertEqual(self.run_theme(), (0, ""))
        self.assertEqual(self.run_theme("--check")[0], 0)
        dark = self.out / "Dark.xaml"
        dark.write_text(dark.read_text(encoding="utf-8").replace("#0F1319", "#0F1318"), encoding="utf-8")
        code, err = self.run_theme("--check")
        self.assertEqual(code, 1)
        self.assertIn("Dark.xaml", err)
        self.assertNotIn("Light.xaml", err)
        self.run_theme()
        (self.out / "Light.xaml").unlink()
        self.assertEqual(self.run_theme("--check")[0], 1)
        self.run_theme()
        self.assertEqual(self.run_theme("--check")[0], 0)

    def test_check_ignores_line_endings(self):
        self.run_theme()
        dark = self.out / "Dark.xaml"
        dark.write_bytes(dark.read_bytes().replace(b"\n", b"\r\n"))  # as Git's CRLF conversion leaves it
        self.assertEqual(self.run_theme("--check")[0], 0)

    def test_check_fails_a_token_edit_until_rebuilt_and_the_rebuild_carries_it(self):
        self.run_theme()
        edits = [("dark", "Accent", "#123456", '<Color x:Key="C.Accent">#123456</Color>', "Dark.xaml"),
                 ("light", "Line", "#ABCDEF", '<Color x:Key="C.Line">#ABCDEF</Color>', "Light.xaml"),
                 ("radius", "Card", 10, '<CornerRadius x:Key="R.Card">10</CornerRadius>', "Dark.xaml"),
                 ("space", "XL", 32, '<sys:Double x:Key="S.XL">32</sys:Double>', "Light.xaml"),
                 ("type", "Title", 22.5, '<sys:Double x:Key="T.Title">22.5</sys:Double>', "Dark.xaml")]
        for group, key, value, line, file in edits:
            with self.subTest(token=f"{group}.{key}"):
                tokens = self.load()
                tokens[group][key] = value
                self.save(tokens)
                self.assertEqual(self.run_theme("--check")[0], 1, "the generated files no longer match the tokens")
                self.assertEqual(self.run_theme()[0], 0)
                self.assertIn(line, (self.out / file).read_text(encoding="utf-8"))
                self.assertEqual(self.run_theme("--check")[0], 0)

    def temps(self):
        return [p.name for p in self.out.glob("*.tmp")] + [p.name for p in self.out.glob(".*")]

    def edit_token(self):
        tokens = self.load()
        tokens["dark"]["Accent"] = "#123456"
        tokens["light"]["Accent"] = "#654321"
        self.save(tokens)

    def test_a_file_that_cant_be_staged_changes_nothing(self):
        # Completion review: a full disk refuses the second temporary file, before either target is touched.
        self.run_theme()
        before = self.outputs()
        self.edit_token()
        real = theme.tempfile.mkstemp
        calls = []

        def full_on_second(*args, **kwargs):
            calls.append(1)
            if len(calls) == 2:
                raise OSError(28, "No space left on device")
            return real(*args, **kwargs)

        with mock.patch.object(theme.tempfile, "mkstemp", full_on_second):
            code, err = self.run_theme()
        self.assertEqual(code, 3)
        self.assertIn("nothing was changed", err)
        self.assertIn("No space left", err)
        self.assertEqual(self.outputs(), before)
        self.assertEqual(self.temps(), [])
        self.assertEqual(self.run_theme("--check")[0], 1)  # the drift is still seen
        self.assertEqual(self.run_theme(), (0, ""))        # and a retry lands
        self.assertEqual(self.run_theme("--check")[0], 0)

    def test_a_held_file_is_left_as_it_was_and_the_report_says_what_changed(self):
        # Completion review: Light.xaml held open, so Dark is updated and Light is not, and that is said.
        self.run_theme()
        before = self.outputs()
        self.edit_token()
        real = theme.os.replace

        def light_is_held(src, dst):
            if str(dst).endswith("Light.xaml"):
                raise PermissionError(13, "The process cannot access the file because it is being used by another process")
            return real(src, dst)

        with mock.patch.object(theme.os, "replace", light_is_held):
            code, err = self.run_theme()
        self.assertEqual(code, 3)
        after = self.outputs()
        self.assertNotEqual(after["Dark.xaml"], before["Dark.xaml"])
        self.assertEqual(after["Light.xaml"], before["Light.xaml"], "the held file is exactly as it was")
        self.assertRegex(err, r"can't replace .*Light\.xaml")
        self.assertRegex(err, r"Updated: .*Dark\.xaml")
        self.assertRegex(err, r"Left as they were: .*Light\.xaml")
        self.assertEqual(self.temps(), [])
        code, err = self.run_theme("--check")
        self.assertEqual(code, 1)
        self.assertIn("Light.xaml", err)
        self.assertNotIn("Dark.xaml", err)
        self.assertEqual(self.run_theme(), (0, ""))  # released: the retry finishes the pair
        self.assertEqual(self.run_theme("--check")[0], 0)
        self.assertIn(b"#654321", self.outputs()["Light.xaml"])

    def test_check_writes_nothing(self):
        self.assertEqual(self.run_theme("--check")[0], 1)  # nothing generated yet
        self.assertFalse(self.out.exists())

    def test_the_repository_theme_matches_its_tokens(self):
        code = theme.main(["--check"])
        self.assertEqual(code, 0, "src/Bosun.Panel/Theme/Generated is current with design/tokens/colors.json")


if __name__ == "__main__":
    unittest.main()
