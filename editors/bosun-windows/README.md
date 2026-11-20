# Bosun window reporter

A VS Code extension of Bosun's own (D03 T02 §4 and §5). VS Code keeps no record outside itself of which windows are open and which folders each holds, so Bosun's Instances page could only place an editor by the folder it was started in. With this extension, each open window writes one small record to `%LOCALAPPDATA%\Bosun\editor-windows\<session id>.json` (its roots as full URIs, a WSL remote window's included, and the editor and extension host processes), rewrites it when its folders change and removes it when it closes. `bosun instances` reads the records and lists the editor once per root, in each repo it has open. Nothing is sent anywhere.

## Install, check and remove

Run these from the Bosun checkout (the folder holding `scripts\`):

- Install into each editor whose CLI is on the PATH as `code`, `code-insiders` or `antigravity-ide`: `pwsh scripts/install-editor-extension.ps1`. It installs into the default profile and into every profile that keeps extensions of its own, reads each back from that profile's own extension list, and says which profiles use the default's extensions. VS Code starts it in the windows already open.
- Install into one editor by its CLI, or into an editor started with its own folders: `pwsh scripts/install-editor-extension.ps1 -Cli 'C:\Path\To\Editor\bin\code.cmd' -UserDataDir 'D:\data' -ExtensionsDir 'D:\ext'` (give only what applies). A Portable Mode install (a `data` folder beside the editor) is found without `-UserDataDir`.
- Remove it the same way with `-Uninstall`. A window open at the time keeps reporting until it is reloaded or closed, because VS Code runs a removed extension until then.
- Check it: open the Instances page (or run `bosun instances`). Each window's folder is a row of its own, and an editor whose windows are not all placed is a notice that names the command to run for that editor.
- Test it: `pwsh tests/editor_extension_test.ps1`, which runs `node --test` over `test/` and checks the packed VSIX and the installer's profile handling.

The installer exits 1 when anything is left undone (an editor that can't be run, a profile list it can't read, a profile name the command shell would change, a listing that disagrees), saying what; running it again does what is still missing.

## How it runs

It is a UI extension (`extensionKind: ["ui"]`), so in a WSL window it runs on the Windows side and still sees the window's remote folders. A window with no record (the extension not installed in its profile, or the editor started with `--disable-extensions`) is said on the Instances page; an editor with none keeps its launch-folder row, marked `placed_by: "launch"`.
