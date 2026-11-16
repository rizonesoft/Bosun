# The end-to-end smoke

`tests/Bosun.Panel.E2E` drives the real `Bosun.Panel.exe` on the desktop: real windows, real keys and clicks (through `RealInput`, which sends nothing unless the panel is where it means to send it), the real notification area. It needs an unlocked desktop, and it runs with the build:

```powershell
bosun run -- dotnet test --project tests/Bosun.Panel.E2E
```

## Which screen it runs on

Every panel the smoke starts opens on the test screen, never first on the main one, so a run stays off the screen you are working on (D02 T03 §12). The harness writes each panel's `panel.json` with a window place centred on that screen before the panel starts, and `PanelProcess` fails any test whose panel was first shown elsewhere.

The test screen is:

- the screen `BOSUN_TEST_SCREEN` names, as a device name Windows gives it, such as `\\.\DISPLAY3`; a run whose variable names no screen on the machine is refused with the screens there are;
- otherwise the largest screen that is not the main one (the leftmost of equals);
- otherwise, on a machine with one screen, the main one.

To see the device names: `Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Screen]::AllScreens | Select-Object DeviceName, Primary, Bounds`.

Two things still reach the main screen, because Windows keeps them there: the notification area's icon and its menu (the tray tests), which Windows shows only on the main taskbar; and the keyboard focus and the pointer, which a test takes while it types or clicks into the panel and gives back after.
