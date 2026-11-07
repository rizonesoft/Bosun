"""What this machine can do decides which rows run, so a failed probe must never claim a capability."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "lib"))
import host_caps  # noqa: E402


def boom():
    raise OSError("probe failed")


class DisplaySession(unittest.TestCase):
    def test_active_and_unlocked_is_a_desktop(self):
        self.assertTrue(host_caps.display_session(True, lambda: True, lambda: "Default"))

    def test_locked_is_not(self):
        # A locked screen refuses the input desktop, or shows the Winlogon one.
        self.assertFalse(host_caps.display_session(True, lambda: True, lambda: None))
        self.assertFalse(host_caps.display_session(True, lambda: True, lambda: "Winlogon"))

    def test_disconnected_is_not(self):
        self.assertFalse(host_caps.display_session(True, lambda: False, lambda: "Default"))

    def test_failed_probes_read_false(self):
        self.assertFalse(host_caps.display_session(True, boom, lambda: "Default"))
        self.assertFalse(host_caps.display_session(True, lambda: True, boom))

    def test_not_windows_reads_false(self):
        self.assertFalse(host_caps.display_session(False, lambda: True, lambda: "Default"))


class Capabilities(unittest.TestCase):
    def read(self, windows=True, machine="AMD64", which=None, registered=(), desktop=True):
        return host_caps.capabilities(on_windows=windows, machine=machine, which=lambda name: which,
                                      registered=lambda name: name in registered, desktop=lambda: desktop)

    def test_each_need_is_reported(self):
        self.assertEqual(set(self.read()), set(host_caps.HOST_NEEDS))

    def test_from_faked_probes(self):
        bare = self.read(desktop=False)
        self.assertEqual(bare, {"windows-host": True, "arm64-host": False, "office": False, "libreoffice": False,
                                "display-session": False})
        self.assertTrue(self.read(machine="ARM64")["arm64-host"])
        self.assertTrue(self.read(machine="aarch64")["arm64-host"])
        self.assertTrue(self.read(registered=("WINWORD.EXE",))["office"])
        self.assertTrue(self.read(which="/usr/bin/soffice")["libreoffice"])
        self.assertTrue(self.read(registered=("soffice.exe",))["libreoffice"])
        self.assertTrue(self.read()["display-session"])

    def test_not_windows_has_no_windows_capabilities(self):
        caps = self.read(windows=False, registered=("WINWORD.EXE",))
        self.assertFalse(caps["windows-host"])
        self.assertFalse(caps["office"])

    @unittest.skipUnless(sys.platform == "win32", "the real session probes are Windows calls")
    def test_real_probes_answer_without_raising(self):
        caps = host_caps.capabilities()
        self.assertTrue(caps["windows-host"])
        self.assertIsInstance(caps["display-session"], bool)


if __name__ == "__main__":
    unittest.main()
