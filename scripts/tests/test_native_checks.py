"""The native build checks: an image's hardening read from its PE headers, and one pinned standard per compile."""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import struct
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts" / "lib"))
from scratch import scratch_tempdir


def load(name):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), ROOT / "scripts" / f"{name}.py")
    assert spec is not None and spec.loader is not None, name
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


pe = load("native-pe-check")
std = load("native-std-check")

ALL = pe.HIGH_ENTROPY_VA | pe.DYNAMIC_BASE | pe.NX_COMPAT | pe.GUARD_CF
X64, ARM64 = 0x8664, 0xAA64


def image(machine=X64, characteristics=ALL, extended=pe.CET_COMPAT, magic=0x20B):
    """A minimal PE image: headers, one section, and in it a debug directory with the extended characteristics entry
    (left out when `extended` is None). Laid out as the linker lays it out, so the reader follows real offsets."""
    optional_size = 240 if magic == 0x20B else 224
    directories = 112 if magic == 0x20B else 96
    optional = bytearray(optional_size)
    struct.pack_into("<H", optional, 0, magic)
    struct.pack_into("<H", optional, 70, characteristics)
    struct.pack_into("<I", optional, directories - 4, 16)
    raw, virtual = 0x200, 0x1000
    if extended is not None:
        struct.pack_into("<II", optional, directories + 8 * pe.DEBUG_DIRECTORY, virtual, 28)
    dos = bytearray(64)
    dos[:2] = b"MZ"
    struct.pack_into("<I", dos, 0x3C, 64)
    coff = struct.pack("<HHIIIHH", machine, 1, 0, 0, 0, optional_size, 0x22)
    section = struct.pack("<8sIIII", b".rdata", 0x200, virtual, 0x200, raw) + bytes(16)
    head = bytes(dos) + b"PE\0\0" + coff + bytes(optional) + section
    body = bytearray(0x200)
    struct.pack_into("<IIHHIIII", body, 0, 0, 0, 0, 0, pe.EX_DLLCHARACTERISTICS, 4, virtual + 28, raw + 28)
    struct.pack_into("<I", body, 28, extended or 0)
    return head + bytes(raw - len(head)) + bytes(body)


class Images(unittest.TestCase):
    def verdict(self, **kwargs):
        return pe.judge(*pe.read_image(image(**kwargs)))

    def test_a_hardened_image_holds(self):
        self.assertEqual(self.verdict(), [])
        self.assertEqual(pe.read_image(image()), ("x64", ALL, pe.CET_COMPAT))

    def test_each_missing_flag_is_named(self):
        for flag, words in ((pe.GUARD_CF, "no control-flow guard"), (pe.DYNAMIC_BASE, "no ASLR"),
                            (pe.HIGH_ENTROPY_VA, "no high-entropy ASLR"), (pe.NX_COMPAT, "not NX compatible")):
            with self.subTest(words=words):
                found = self.verdict(characteristics=ALL & ~flag)
                self.assertEqual(len(found), 1, found)
                self.assertIn(words, found[0])

    def test_an_x64_image_must_be_cet_compatible(self):
        self.assertIn("not CET compatible", self.verdict(extended=0)[0])
        self.assertIn("not CET compatible", self.verdict(extended=None)[0])  # no such debug entry at all
        self.assertEqual(self.verdict(extended=pe.CET_COMPAT | 0x40), [])  # other bits beside it change nothing

    def test_an_arm64_image_needs_no_cet_bit(self):
        self.assertEqual(self.verdict(machine=ARM64, extended=None), [])
        self.assertIn("no control-flow guard", self.verdict(machine=ARM64, characteristics=ALL & ~pe.GUARD_CF)[0])

    def test_another_machine_is_named(self):
        self.assertIn("not x64 or ARM64", self.verdict(machine=0x14C)[0])

    def test_a_32_bit_optional_header_is_read_at_its_own_offsets(self):
        self.assertEqual(pe.read_image(image(magic=0x10B))[1:], (ALL, pe.CET_COMPAT))

    def test_what_is_not_an_image_is_refused_not_judged(self):
        good = image()
        for data, words in ((b"", "MZ"), (b"plain text", "MZ"), (good[:66], "PE signature"), (good[:70], "ends before"), (good[:100], "ends before"),
                            (good[:64] + b"XX\0\0" + good[68:], "PE signature"), (good[:300], "ends before"),
                            (good[:0x200], "ends before")):
            with self.subTest(length=len(data)):
                with self.assertRaises(pe.NotAnImage) as refused:
                    pe.read_image(data)
                self.assertIn(words, str(refused.exception))

    def test_only_the_headers_are_read_however_large_the_file(self):
        class Counted(io.BytesIO):
            """A file that counts what is asked of it, standing in for one far larger than memory."""
            asked, largest = 0, 0

            def read(self, size: int | None = -1):
                assert size is not None and size >= 0, "the whole file was asked for"
                Counted.asked += size
                Counted.largest = max(Counted.largest, size)
                return super().read(size)

        padded = image() + bytes(4 * 1024 * 1024)  # an image with megabytes after its headers
        self.assertEqual(pe.judge(*pe.read_image(Counted(padded))), [])
        self.assertLess(Counted.asked, 4096)
        self.assertLessEqual(Counted.largest, 40)
        Counted.asked = 0
        with self.assertRaises(pe.NotAnImage):
            pe.read_image(Counted(bytes(4 * 1024 * 1024)))  # megabytes of nothing: refused at the first two bytes
        self.assertEqual(Counted.asked, 2)

    def test_a_huge_file_that_is_not_an_image_is_refused_without_being_loaded(self):
        with scratch_tempdir("native-checks") as tmp:
            big = Path(tmp) / "big.exe"
            with big.open("wb") as out:
                out.truncate(256 * 1024 ** 2)  # a length only: the file is never written, and must never be read whole
            code, text = self.run_main(pe, [str(big)])
            self.assertEqual(code, 2)
            self.assertIn("does not start with MZ", text)

    def run_main(self, module, argv):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = module.main(argv)
        return code, out.getvalue()

    def test_the_command_exits_0_1_or_2(self):
        with scratch_tempdir("native-checks") as tmp:
            folder = Path(tmp)
            (folder / "good.exe").write_bytes(image())
            self.assertEqual(self.run_main(pe, [str(folder)])[0], 0)
            (folder / "soft.exe").write_bytes(image(characteristics=ALL & ~pe.GUARD_CF, extended=0))
            code, text = self.run_main(pe, [str(folder)])
            self.assertEqual(code, 1)
            self.assertIn("soft.exe: no control-flow guard", text)
            self.assertIn("soft.exe: not CET compatible", text)
            self.assertIn("ok   " + str(folder / "good.exe"), text)
            (folder / "cut.exe").write_bytes(image()[:90])
            code, text = self.run_main(pe, [str(folder / "cut.exe")])
            self.assertEqual(code, 2)
            self.assertIn("could not be read as a PE image", text)
            self.assertNotIn("Traceback", text)
            empty = folder / "empty"
            empty.mkdir()
            self.assertEqual(self.run_main(pe, [str(empty)])[0], 2)  # an absent build is said, never skipped
            self.assertEqual(self.run_main(pe, [str(folder / "missing.exe")])[0], 2)


class Standard(unittest.TestCase):
    def setUp(self):
        self.tmp = scratch_tempdir("native-checks")
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.cmake = self.root / "CMakeLists.txt"
        self.cmake.write_text("project(X LANGUAGES CXX)\nset(CMAKE_CXX_STANDARD 20)\nset(CMAKE_CXX_STANDARD_REQUIRED ON)\n", encoding="utf-8")
        self.build = self.root / "build"
        self.build.mkdir()

    def database(self, records):
        text = records if isinstance(records, str) else json.dumps(records)
        (self.build / "compile_commands.json").write_text(text, encoding="utf-8")

    def check(self):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = std.main([str(self.build), "--cmake", str(self.cmake)])
        return code, out.getvalue()

    def record(self, command, file="D:/x/src/app.cpp"):
        return {"directory": "D:/x/build", "command": command, "file": file}

    def test_one_pinned_switch_in_either_spelling_holds(self):
        self.database([self.record("cl.exe /nologo -std:c++20 -c app.cpp"), self.record("cl.exe /std:c++20 /c main.cpp", "D:/x/src/main.cpp"),
                       {"directory": "D:/x/build", "arguments": ["cl.exe", "/std:c++20", "/c", "x.cxx"], "file": "D:/x/src/x.cxx"}])
        code, text = self.check()
        self.assertEqual(code, 0, text)
        self.assertIn("3 C++ compiles, each with exactly c++20", text)

    def test_the_pin_is_read_from_cmakelists(self):
        self.cmake.write_text("set( CMAKE_CXX_STANDARD 23 )\n", encoding="utf-8")
        self.database([self.record("cl.exe -std:c++20 -c app.cpp")])
        code, text = self.check()
        self.assertEqual(code, 1)
        self.assertIn("wanted exactly one: c++23", text)

    def test_each_wrong_compile_is_named_with_what_it_carries(self):
        for command, said in (("cl.exe -std:c++20 /std:c++latest -c app.cpp", "c++20, c++latest"),
                              ("cl.exe -std:c++20 -std:c++20 -c app.cpp", "c++20, c++20"),
                              ("cl.exe /std:c++17 -c app.cpp", "c++17"),
                              ("cl.exe /std:c++latest -c app.cpp", "c++latest"),
                              ("cl.exe /W4 -c app.cpp", "no standard switch")):
            with self.subTest(command=command):
                self.database([self.record("cl.exe -std:c++20 -c ok.cpp", "D:/x/src/ok.cpp"), self.record(command)])
                code, text = self.check()
                self.assertEqual(code, 1)
                self.assertIn(f"FAIL D:/x/src/app.cpp: {said}", text)
                self.assertNotIn("ok.cpp", text)

    def test_a_compile_that_is_not_cxx_is_not_judged(self):
        self.database([self.record("cl.exe -std:c++20 -c app.cpp"), self.record("rc.exe /fo x.res x.rc", "D:/x/src/x.rc"),
                       self.record("cl.exe /c legacy.c", "D:/x/src/legacy.c")])
        self.assertEqual(self.check()[0], 0)

    def test_input_it_cannot_judge_exits_2_with_the_reason(self):
        cases = (
            ("[]", "holds no C++ compile"),  # an empty database would pass vacuously
            (json.dumps([self.record("rc.exe x.rc", "D:/x/src/x.rc")]), "holds no C++ compile"),
            ("{ not json", "can't be read as JSON"),
            (json.dumps({"command": "cl"}), "is not a list"),
            (json.dumps([{"file": "D:/x/src/app.cpp"}]), "has no command"),
            (json.dumps([{"command": "cl.exe -std:c++20"}]), "has no file"),
            (json.dumps(["cl.exe -std:c++20 app.cpp"]), "has no file"),
        )
        for text, words in cases:
            with self.subTest(words=words, text=text[:30]):
                self.database(text)
                code, said = self.check()
                self.assertEqual(code, 2, said)
                self.assertIn(words, said)
        (self.build / "compile_commands.json").unlink()
        code, said = self.check()
        self.assertEqual(code, 2)
        self.assertIn("does not exist", said)

    def test_a_cmakelists_with_no_single_standard_exits_2(self):
        self.database([self.record("cl.exe -std:c++20 -c app.cpp")])
        for text, words in (("project(X)\n", "found: none"),
                            ("set(CMAKE_CXX_STANDARD 20)\nset(CMAKE_CXX_STANDARD 23)\n", "['20', '23']")):
            with self.subTest(words=words):
                self.cmake.write_text(text, encoding="utf-8")
                code, said = self.check()
                self.assertEqual(code, 2)
                self.assertIn(words, said)
        self.cmake.unlink()
        self.assertEqual(self.check()[0], 2)

    def test_this_repository_s_pin_is_cxx20(self):
        self.assertEqual(std.pinned_standard(ROOT / "CMakeLists.txt"), "c++20")


if __name__ == "__main__":
    unittest.main()
