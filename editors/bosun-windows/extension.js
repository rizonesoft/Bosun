'use strict';
// The window reporter (D03 T02 §4): one record per open window, kept current while the window is open.
const vscode = require('vscode');
const { Recorder } = require('./record');

let recorder = null;

function activate(context) {
  recorder = new Recorder(vscode);
  recorder.save();
  recorder.clearStale();
  context.subscriptions.push(vscode.workspace.onDidChangeWorkspaceFolders(() => recorder && recorder.save()));
}

function deactivate() {
  if (recorder) recorder.stop();
  recorder = null;
}

module.exports = { activate, deactivate };
