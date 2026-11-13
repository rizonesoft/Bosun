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
            self.assertNotIn("Elevation", text)  # D02 T03 §1's
            keys = re.findall(r'x:Key="([^"]+)"', text)
            self.assertEqual(len(keys), len(set(keys)))
            self.assertEqual(len(keys), 2 * len(tokens[name]) + sum(len(tokens[g]) for g, _, _ in theme.MEASURES))
        self.assertNotIn(b"\r\n", (self.out / "Dark.xaml").read_bytes())

    def test_the_two_themes_differ_only_in_their_colours(self):
        self.run_theme()
        dark = (self.out / "Dark.xaml").read_text(encoding="utf-8")
        light = (self.out / "Light.xaml").read_text(encoding="utf-8")
        self.assertNotEqual(dark, light)
        strip = lambda t: re.sub(r"#[0-9A-F]{6,8}", "#", t)
        self.assertEqual(strip(dark), strip(light))

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

    def test_check_writes_nothing(self):
        self.assertEqual(self.run_theme("--check")[0], 1)  # nothing generated yet
        self.assertFalse(self.out.exists())

    def test_the_repository_theme_matches_its_tokens(self):
        code = theme.main(["--check"])
        self.assertEqual(code, 0, "src/Bosun.Panel/Theme/Generated is current with design/tokens/colors.json")


if __name__ == "__main__":
    unittest.main()
