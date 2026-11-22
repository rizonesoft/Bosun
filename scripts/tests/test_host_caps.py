"""What this machine can do decides which rows run, so a failed probe must never claim a capability."""
import ctypes
from pathlib import Path
import sys
import unittest
from unittest import mock

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


@unittest.skipUnless(sys.platform == "win32", "ctypes.windll exists only on Windows")
class NativeCalls(unittest.TestCase):
    """The wrappers themselves, through simulated Windows calls, so one that always says yes fails here."""

    def wts(self, ok=True, state=host_caps.WTS_ACTIVE):
        value = ctypes.c_int(state)
        api = mock.MagicMock()

        def query(server, session, info, buffer, size):
            self.assertEqual((session, info), (host_caps.WTS_CURRENT_SESSION, host_caps.WTS_CONNECT_STATE))
            if not ok:
                return 0
            buffer._obj.value = ctypes.addressof(value)
            return 1
        api.WTSQuerySessionInformationW.side_effect = query
        self.value = value
        return mock.MagicMock(wtsapi32=api)

    def desktop(self, handle: int | None = 123, name="Default", ok=True):
        api = mock.MagicMock()
        api.OpenInputDesktop.return_value = handle

        def info(desktop, index, buffer, size, needed):
            self.assertEqual((desktop, index), (handle, host_caps.UOI_NAME))
            if not ok:
                return 0
            buffer.value = name
            return 1
        api.GetUserObjectInformationW.side_effect = info
        self.api = api
        return mock.MagicMock(user32=api)

    def active(self, **kwargs):
        with mock.patch.object(ctypes, "windll", self.wts(**kwargs)):
            return host_caps.session_active()

    def name(self, **kwargs):
        with mock.patch.object(ctypes, "windll", self.desktop(**kwargs)):
            return host_caps.input_desktop()

    def test_session_states(self):
        self.assertTrue(self.active())
        self.assertFalse(self.active(state=4))  # disconnected
        self.assertFalse(self.active(ok=False))

    def test_input_desktop_states(self):
        self.assertEqual(self.name(), "Default")
        self.assertEqual(self.name(name="Winlogon"), "Winlogon")
        self.assertIsNone(self.name(handle=None))  # a locked screen refuses it
        self.assertIsNone(self.name(ok=False))

    def test_a_desktop_handle_is_always_closed(self):
        self.name()
        self.api.CloseDesktop.assert_called_once_with(123)
        self.name(ok=False)
        self.api.CloseDesktop.assert_called_once_with(123)

    def test_the_memory_is_freed(self):
        windll = self.wts()
        with mock.patch.object(ctypes, "windll", windll):
            host_caps.session_active()
        windll.wtsapi32.WTSFreeMemory.assert_called_once()

    def test_end_to_end_a_locked_screen_is_no_desktop(self):
        with mock.patch.object(ctypes, "windll", mock.MagicMock(wtsapi32=self.wts().wtsapi32, user32=self.desktop(handle=None).user32)):
            self.assertFalse(host_caps.display_session(True))


class Registered(unittest.TestCase):
    def test_a_registered_program_must_really_be_there(self):
        there = {r"C:\Office\WINWORD.EXE"}
        check = lambda path: path in there
        self.assertTrue(host_caps.app_path_exists("x", lambda n: [r"C:\Office\WINWORD.EXE"], check))
        self.assertTrue(host_caps.app_path_exists("x", lambda n: ['"C:\\Office\\WINWORD.EXE"'], check))  # quoted, as some installers write it
        self.assertFalse(host_caps.app_path_exists("x", lambda n: [r"C:\Gone\WINWORD.EXE"], check))
        self.assertFalse(host_caps.app_path_exists("x", lambda n: [], check))
        self.assertFalse(host_caps.app_path_exists("x", lambda n: [None, 3], check))

    def test_either_a_per_user_or_a_per_machine_entry_counts(self):
        check = lambda path: path == "user"
        self.assertTrue(host_caps.app_path_exists("x", lambda n: ["gone", "user"], check))

    @unittest.skipUnless(sys.platform == "win32", "reads the Windows registry")
    def test_real_registry_reads_both_hives(self):
        assert sys.platform == "win32", "the skip decorator keeps this test on Windows"
        import winreg
        opened = []
        real = winreg.OpenKey

        def spy(hive, *args, **kwargs):
            opened.append(hive)
            return real(hive, *args, **kwargs)
        with mock.patch.object(winreg, "OpenKey", spy):
            self.assertIsInstance(host_caps.registered_paths("no-such-program.exe"), list)
        self.assertEqual(opened, [winreg.HKEY_CURRENT_USER, winreg.HKEY_LOCAL_MACHINE])


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
