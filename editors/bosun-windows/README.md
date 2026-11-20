# Bosun window reporter

A VS Code extension of Bosun's own (D03 T02 §4). VS Code keeps no record outside itself of which windows are open and which folders each holds, so Bosun's Instances page could only place an editor by the folder it was started in. With this extension, each open window writes one small record to `%LOCALAPPDATA%\Bosun\editor-windows\<session id>.json` (its roots as full URIs, a WSL remote window's included, and the editor and extension host processes), rewrites it when its folders change and removes it when it closes. `bosun instances` reads the records and lists the editor once per root, in each repo it has open. Nothing is sent anywhere.

- Install or update it in every VS Code-family editor on the PATH: `pwsh scripts/install-editor-extension.ps1`. VS Code starts it in the windows already open.
- Remove it: `pwsh scripts/install-editor-extension.ps1 -Uninstall`.
- Test it: `pwsh tests/editor_extension_test.ps1`, which runs `node --test` over `test/` and checks the packed VSIX.

It is a UI extension (`extensionKind: ["ui"]`), so in a WSL window it runs on the Windows side and still sees the window's remote folders. A window whose record is missing (the extension not installed) keeps its launch-folder row, marked `placed_by: "launch"`, and `bosun instances` says how to place it.
