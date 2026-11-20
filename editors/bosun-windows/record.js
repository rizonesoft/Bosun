'use strict';
// One window's record for Bosun (D03 T02 §4): which roots this window has open, with their full URIs, written while
// the window is open and removed when it closes. The scanner (`dm::editor_windows` in src/core.hpp) reads it, and takes
// it only while `host_pid` (this extension host) is alive under `main_pid` (the editor's main process).
//
// The `vscode` API, the process and the file system are parameters, so the tests run it with stand-ins.

const nodeFs = require('fs');
const nodePath = require('path');
const crypto = require('crypto');

const SCHEMA_VERSION = 1;

// Whether a process runs: signal 0 tests it without touching it. EPERM is a process this user can't signal, which runs.
function defaultAlive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch (e) {
    return !!e && e.code === 'EPERM';
  }
}

// The folder records live in: %LOCALAPPDATA%\Bosun\editor-windows, or null when the variable is not set.
function recordFolder(env) {
  const base = env && env.LOCALAPPDATA;
  return base ? nodePath.join(base, 'Bosun', 'editor-windows') : null;
}

// A session id as a file name: letters, digits and dashes only, so no id can name a path outside the folder.
function fileName(sessionId) {
  const safe = String(sessionId || '').replace(/[^A-Za-z0-9-]/g, '');
  return (safe || 'unknown') + '.json';
}

// The record as it is written: what this window has open now.
function snapshot(vscode, proc, now) {
  const folders = (vscode.workspace && vscode.workspace.workspaceFolders) || [];
  const file = vscode.workspace && vscode.workspace.workspaceFile;
  return {
    schema_version: SCHEMA_VERSION,
    app_name: String(vscode.env.appName || ''),
    main_pid: proc.ppid,
    host_pid: proc.pid,
    session_id: String(vscode.env.sessionId || ''),
    workspace_file: file ? file.toString() : null,
    roots: folders.map((f) => f.uri.toString()),
    written_utc: now().toISOString(),
  };
}

class Recorder {
  constructor(vscode, options = {}) {
    this.vscode = vscode;
    this.fs = options.fs || nodeFs;
    this.proc = options.proc || process;
    this.now = options.now || (() => new Date());
    this.folder = options.folder !== undefined ? options.folder : recordFolder(this.proc.env);
    this.path = this.folder ? nodePath.join(this.folder, fileName(vscode.env.sessionId)) : null;
    this.lastError = null;
  }

  // Writes the record whole: a temporary file in the same folder, then a rename over the last one. A write that fails
  // leaves the last whole record in place (and no temporary file), and says why in `lastError`; it never throws, so a
  // failure costs Bosun the window's placement and never the editor anything.
  write() {
    if (!this.path) {
      this.lastError = 'LOCALAPPDATA is not set, so there is nowhere to write the record';
      return false;
    }
    const text = JSON.stringify(snapshot(this.vscode, this.proc, this.now)) + '\n';
    const temporary = nodePath.join(this.folder, '.' + nodePath.basename(this.path) + '.' + crypto.randomBytes(6).toString('hex') + '.tmp');
    try {
      this.fs.mkdirSync(this.folder, { recursive: true });
      this.fs.writeFileSync(temporary, text, { encoding: 'utf8', flag: 'wx' });
      this.fs.renameSync(temporary, this.path);
      this.lastError = null;
      return true;
    } catch (e) {
      try {
        this.fs.unlinkSync(temporary);
      } catch (_) {
        // It was never written, or it is gone already.
      }
      this.lastError = String((e && e.message) || e);
      return false;
    }
  }

  // Writes the record, and when the write fails, tries again every `retryMs` until one succeeds or a newer save or
  // `stop` replaces it (round 1: a record held open by another process kept a removed folder live until the next
  // change). One retry is pending at a time.
  save(setTimer = setTimeout, clearTimer = clearTimeout, retryMs = 2000) {
    if (this.retry) {
      clearTimer(this.retry);
      this.retry = null;
    }
    if (this.stopped || this.write()) return true;
    this.retry = setTimer(() => {
      this.retry = null;
      this.save(setTimer, clearTimer, retryMs);
    }, retryMs);
    return false;
  }

  // Ends any pending retry and removes the record: the window is closing.
  stop(clearTimer = clearTimeout) {
    this.stopped = true;
    if (this.retry) clearTimer(this.retry);
    this.retry = null;
    this.remove();
  }

  // Removes the records of windows whose extension host is gone (an editor that crashed or was ended never removed
  // its own), so the scanner's stale notices don't outlive the next start. A record it can't read is left: the
  // scanner says it is damaged. `alive` answers whether a process id runs; by default, signal 0.
  clearStale(alive = defaultAlive) {
    if (!this.folder) return 0;
    let names;
    try {
      names = this.fs.readdirSync(this.folder);
    } catch (_) {
      return 0;
    }
    let cleared = 0;
    for (const name of names) {
      if (!name.endsWith('.json') || nodePath.join(this.folder, name) === this.path) continue;
      const file = nodePath.join(this.folder, name);
      let record;
      try {
        record = JSON.parse(this.fs.readFileSync(file, 'utf8'));
      } catch (_) {
        continue;
      }
      if (!record || !Number.isInteger(record.host_pid) || record.host_pid <= 0 || alive(record.host_pid)) continue;
      try {
        this.fs.unlinkSync(file);
        cleared++;
      } catch (_) {
        // Another window cleared it first, or it is held: the scanner still calls it stale.
      }
    }
    return cleared;
  }

  // Removes the record: the window is closing.
  remove() {
    if (!this.path) return;
    try {
      this.fs.unlinkSync(this.path);
    } catch (e) {
      if (!e || e.code !== 'ENOENT') this.lastError = String((e && e.message) || e);
    }
  }
}

module.exports = { Recorder, recordFolder, fileName, snapshot, SCHEMA_VERSION };
