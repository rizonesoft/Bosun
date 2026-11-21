'use strict';
// node --test editors/bosun-windows/test/ (D03 T02 §4): the record a window keeps, with a stand-in `vscode`.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { Recorder, fileName, recordFolder } = require('../record');

function uri(text) {
  return { toString: () => text };
}

function standIn(roots, workspaceFile = null) {
  return {
    env: { appName: 'Visual Studio Code', sessionId: 'a1b2c3d4-0001' },
    workspace: { workspaceFolders: roots.map((r) => ({ uri: uri(r) })), workspaceFile: workspaceFile ? uri(workspaceFile) : undefined },
  };
}

function scratch() {
  const base = process.env.BOSUN_SCRATCH || os.tmpdir();
  fs.mkdirSync(base, { recursive: true });
  return fs.mkdtempSync(path.join(base, 'bosun-windows-'));
}

const proc = { pid: 4242, ppid: 4000, env: {} };
const clock = () => new Date('2026-11-20T11:00:00.000Z');

function read(recorder) {
  return JSON.parse(fs.readFileSync(recorder.path, 'utf8'));
}

test('a folder window writes its full URI, its processes and its session', () => {
  const folder = scratch();
  const r = new Recorder(standIn(['file:///d%3A/src/Bosun']), { proc, now: clock, folder });
  assert.equal(r.write(), true);
  assert.deepEqual(read(r), {
    schema_version: 1, app_name: 'Visual Studio Code', main_pid: 4000, host_pid: 4242, session_id: 'a1b2c3d4-0001',
    workspace_file: null, roots: ['file:///d%3A/src/Bosun'], written_utc: '2026-11-20T11:00:00.000Z',
  });
  assert.equal(path.basename(r.path), 'a1b2c3d4-0001.json');
});

test('a WSL remote window keeps its remote URI whole', () => {
  const folder = scratch();
  const r = new Recorder(standIn(['vscode-remote://wsl%2Bubuntu/home/me/app']), { proc, now: clock, folder });
  r.write();
  assert.deepEqual(read(r).roots, ['vscode-remote://wsl%2Bubuntu/home/me/app']);
});

test('a two-root workspace names both roots and its workspace file; an empty window has none', () => {
  const folder = scratch();
  const r = new Recorder(standIn(['file:///d%3A/a/app', 'file:///d%3A/b/app'], 'file:///d%3A/work/two.code-workspace'), { proc, now: clock, folder });
  r.write();
  assert.deepEqual(read(r).roots, ['file:///d%3A/a/app', 'file:///d%3A/b/app']);
  assert.equal(read(r).workspace_file, 'file:///d%3A/work/two.code-workspace');
  const empty = new Recorder({ env: { appName: 'Code', sessionId: 'e-1' }, workspace: {} }, { proc, now: clock, folder });
  empty.write();
  assert.deepEqual(read(empty).roots, []);
});

test('a folder added to the window rewrites the record', () => {
  const folder = scratch();
  const api = standIn(['file:///d%3A/a/app']);
  const r = new Recorder(api, { proc, now: clock, folder });
  r.write();
  api.workspace.workspaceFolders.push({ uri: uri('file:///d%3A/c/lib') });
  r.write();
  assert.deepEqual(read(r).roots, ['file:///d%3A/a/app', 'file:///d%3A/c/lib']);
  assert.deepEqual(fs.readdirSync(folder), ['a1b2c3d4-0001.json'], 'no temporary file is left');
});

test('a write that fails keeps the last whole record and leaves no temporary file', () => {
  const folder = scratch();
  const api = standIn(['file:///d%3A/a/app']);
  const r = new Recorder(api, { proc, now: clock, folder });
  r.write();
  const failing = { ...fs, renameSync: () => { throw Object.assign(new Error('EBUSY: the record is held'), { code: 'EBUSY' }); } };
  api.workspace.workspaceFolders.push({ uri: uri('file:///d%3A/c/lib') });
  const later = new Recorder(api, { proc, now: clock, folder, fs: failing });
  assert.equal(later.write(), false);
  assert.match(later.lastError, /EBUSY/);
  assert.deepEqual(read(r).roots, ['file:///d%3A/a/app'], 'the last whole record stays');
  assert.deepEqual(fs.readdirSync(folder), ['a1b2c3d4-0001.json']);
});

test('deactivating removes the record; a record already gone is not an error', () => {
  const folder = scratch();
  const r = new Recorder(standIn(['file:///d%3A/a/app']), { proc, now: clock, folder });
  r.write();
  r.remove();
  assert.equal(fs.existsSync(r.path), false);
  r.remove();
  assert.equal(r.lastError, null);
});

test('a session id can never name a file outside the folder, and no LOCALAPPDATA writes nothing', () => {
  assert.equal(fileName('..\\..\\evil/x'), 'evilx.json');
  assert.equal(fileName(''), 'unknown.json');
  assert.equal(recordFolder({}), null);
  assert.equal(recordFolder({ LOCALAPPDATA: 'C:\\Users\\me\\AppData\\Local' }), path.join('C:\\Users\\me\\AppData\\Local', 'Bosun', 'editor-windows'));
  const r = new Recorder(standIn(['file:///x']), { proc: { pid: 1, ppid: 0, env: {} }, now: clock });
  assert.equal(r.write(), false);
  assert.match(r.lastError, /LOCALAPPDATA/);
});

test('starting clears the records of windows whose host is gone, and keeps live, unreadable and its own', () => {
  const folder = scratch();
  const write = (name, body) => fs.writeFileSync(path.join(folder, name), body);
  write('gone.json', JSON.stringify({ schema_version: 1, host_pid: 111 }));
  write('live.json', JSON.stringify({ schema_version: 1, host_pid: 222 }));
  write('damaged.json', '{"schema_version":1,');
  write('note.txt', 'not a record');
  const r = new Recorder(standIn(['file:///d%3A/a/app']), { proc: { pid: 333, ppid: 1, env: {} }, now: clock, folder });
  r.write();
  assert.equal(r.clearStale((pid) => pid === 222 || pid === 333), 1);
  assert.deepEqual(fs.readdirSync(folder).sort(), ['a1b2c3d4-0001.json', 'damaged.json', 'live.json', 'note.txt']);
});

test('a write that fails is tried again until it lands, and stopping ends the retry and removes the record', () => {
  const folder = scratch();
  const api = standIn(['file:///d%3A/a/app']);
  let held = true;
  const flaky = { ...fs, renameSync: (a, b) => { if (held) throw Object.assign(new Error('EBUSY: held'), { code: 'EBUSY' }); return fs.renameSync(a, b); } };
  const timers = [];
  const setTimer = (fn) => { timers.push(fn); return timers.length; };
  const clearTimer = () => {};
  const r = new Recorder(api, { proc, now: clock, folder, fs: flaky });
  assert.equal(r.save(setTimer, clearTimer), false);
  assert.equal(timers.length, 1, 'one retry is pending');
  timers.shift()();
  assert.equal(timers.length, 1, 'still held: tried again later');
  held = false;
  timers.shift()();
  assert.equal(timers.length, 0, 'landed: no retry left');
  assert.deepEqual(read(r).roots, ['file:///d%3A/a/app']);
  held = true;
  r.save(setTimer, clearTimer);
  assert.equal(timers.length, 1);
  r.stop(clearTimer);
  timers.shift()();
  assert.equal(timers.length, 0, 'a stopped recorder never writes again');
  assert.equal(fs.existsSync(r.path), false);
});

test('activate writes the record and rewrites it on a folder change; deactivate removes it (through extension.js)', () => {
  const Module = require('module');
  const folder = scratch();
  let changed = null;
  const api = standIn(['file:///d%3A/a/app']);
  api.workspace.onDidChangeWorkspaceFolders = (fn) => { changed = fn; return { dispose() {} }; };
  const resolve = Module._resolveFilename;
  const load = Module._load;
  Module._resolveFilename = function (request, ...rest) { return request === 'vscode' ? 'vscode' : resolve.call(this, request, ...rest); };
  Module._load = function (request, ...rest) { return request === 'vscode' ? api : load.call(this, request, ...rest); };
  const before = process.env.LOCALAPPDATA;
  const base = path.join(folder, 'local');
  process.env.LOCALAPPDATA = base;
  try {
    delete require.cache[require.resolve('../extension')];
    const extension = require('../extension');
    const context = { subscriptions: [] };
    extension.activate(context);
    const file = path.join(base, 'Bosun', 'editor-windows', 'a1b2c3d4-0001.json');
    assert.deepEqual(JSON.parse(fs.readFileSync(file, 'utf8')).roots, ['file:///d%3A/a/app']);
    assert.equal(typeof changed, 'function', 'it listens for folder changes');
    api.workspace.workspaceFolders.push({ uri: uri('file:///d%3A/b/lib') });
    changed();
    assert.deepEqual(JSON.parse(fs.readFileSync(file, 'utf8')).roots, ['file:///d%3A/a/app', 'file:///d%3A/b/lib']);
    extension.deactivate();
    assert.equal(fs.existsSync(file), false);
  } finally {
    Module._resolveFilename = resolve;
    Module._load = load;
    if (before === undefined) delete process.env.LOCALAPPDATA; else process.env.LOCALAPPDATA = before;
  }
});
