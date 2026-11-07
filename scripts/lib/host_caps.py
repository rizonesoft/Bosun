"""What this machine and session can do right now, for the host Needs a TODO section can declare.

A probe that fails reads as false: a row waits, and nothing is claimed that wasn't seen. Stdlib only.
"""
import ctypes
import os
import platform
import shutil
import sys

HOST_NEEDS = ("windows-host", "arm64-host", "office", "libreoffice", "display-session")
WTS_CURRENT_SESSION = 0xFFFFFFFF
WTS_CONNECT_STATE = 8
WTS_ACTIVE = 0
DESKTOP_READOBJECTS = 0x0001
UOI_NAME = 2


def session_active():
    """True when this process's session is connected and active, so not disconnected or waiting at a sign-in screen."""
    api = ctypes.windll.wtsapi32
    buffer, size = ctypes.c_void_p(), ctypes.c_ulong()
    api.WTSQuerySessionInformationW.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_int,
                                                ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(ctypes.c_ulong)]
    api.WTSFreeMemory.argtypes = [ctypes.c_void_p]
    if not api.WTSQuerySessionInformationW(None, WTS_CURRENT_SESSION, WTS_CONNECT_STATE, ctypes.byref(buffer),
                                           ctypes.byref(size)):
        return False
    try:
        return ctypes.c_int.from_address(buffer.value).value == WTS_ACTIVE
    finally:
        api.WTSFreeMemory(buffer)


def input_desktop():
    """The name of the desktop receiving input, or None when it can't be opened, as on a locked screen."""
    user32 = ctypes.windll.user32
    user32.OpenInputDesktop.restype = ctypes.c_void_p
    user32.OpenInputDesktop.argtypes = [ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
    user32.GetUserObjectInformationW.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_ulong,
                                                 ctypes.POINTER(ctypes.c_ulong)]
    user32.CloseDesktop.argtypes = [ctypes.c_void_p]
    handle = user32.OpenInputDesktop(0, False, DESKTOP_READOBJECTS)
    if not handle:
        return None
    try:
        name, needed = ctypes.create_unicode_buffer(256), ctypes.c_ulong()
        if not user32.GetUserObjectInformationW(handle, UOI_NAME, name, ctypes.sizeof(name), ctypes.byref(needed)):
            return None
        return name.value
    finally:
        user32.CloseDesktop(handle)


def display_session(on_windows=None, active=session_active, desktop=input_desktop):
    """An active, unlocked session of this process. Locked, disconnected, unknown or not Windows reads false."""
    if not (sys.platform == "win32" if on_windows is None else on_windows):
        return False
    try:
        return bool(active()) and desktop() == "Default"
    except (OSError, AttributeError, ValueError):
        return False


def registered_paths(name):
    """Where Windows says an application is, per user and per machine, the way Office and LibreOffice install."""
    try:
        import winreg
    except ImportError:
        return []
    found = []
    for hive in (winreg.HKEY_CURRENT_USER, winreg.HKEY_LOCAL_MACHINE):
        try:
            with winreg.OpenKey(hive, rf"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{name}") as key:
                found.append(winreg.QueryValueEx(key, "")[0])
        except OSError:
            continue
    return found


def app_path_exists(name, paths=registered_paths, exists=os.path.isfile):
    """Whether a registered application is really there. An uninstall can leave the registry entry behind."""
    return any(isinstance(path, str) and exists(os.path.expandvars(path).strip('"')) for path in paths(name))


def capabilities(on_windows=None, machine=None, which=shutil.which, registered=app_path_exists, desktop=display_session):
    """Each host Need against this machine and session, read fresh, so a lock or an install counts at once."""
    windows = sys.platform == "win32" if on_windows is None else on_windows
    arch = (machine if machine is not None else platform.machine()).lower()
    return {
        "windows-host": windows,
        "arm64-host": arch in ("arm64", "aarch64"),
        "office": windows and bool(registered("WINWORD.EXE")),
        "libreoffice": bool(which("soffice")) or (windows and bool(registered("soffice.exe"))),
        "display-session": bool(desktop()),
    }
