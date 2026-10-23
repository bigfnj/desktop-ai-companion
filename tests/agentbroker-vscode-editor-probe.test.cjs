'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const probe = require('../tools/agentbroker/vscode-editor-probe/extension.js');
const repo = path.resolve(__dirname, '..');
const source = path.join(repo, 'tools/agentbroker/vscode-editor-probe');
const target = '11111111-1111-4111-8111-111111111111';
const other = '22222222-2222-4222-8222-222222222222';
class Uri {
  constructor(scheme, fsPath, authority = '') { this.scheme = scheme; this.fsPath = fsPath; this.authority = authority; }
  with(values) { return new Uri(values.scheme, this.fsPath, values.authority); }
  toString() { return `${this.scheme}://${this.authority}${this.fsPath.replaceAll('\\', '/')}`; }
  static file(value) { return new Uri('file', value); }
}
class TabInputCustom { constructor(uri, viewType) { this.uri = uri; this.viewType = viewType; } }
function fixture() {
  const id = crypto.randomUUID().replaceAll('-', '');
  const root = path.join(repo, 'artifacts/agentbroker-editor-probes', `editor-probe-${id}`);
  // New random owned directories only. Preserve small fixtures for review; no recursive cleanup.
  assert.equal(fs.existsSync(root), false);
  fs.mkdirSync(path.join(root, 'extension'), { recursive: true });
  fs.mkdirSync(path.join(root, 'workspace'));
  const sourceHashes = {};
  for (const name of ['extension.js', 'package.json']) {
    const bytes = fs.readFileSync(path.join(source, name));
    fs.writeFileSync(path.join(root, 'extension', name), bytes, { flag: 'wx' });
    sourceHashes[name] = crypto.createHash('sha256').update(bytes).digest('hex');
  }
  const workspaceFile = path.join(root, 'probe.code-workspace');
  const manifest = { schema: 1, kind: 'agentbroker-vscode-editor-probe', developmentOnly: true,
    probeId: id, targetThreadId: target, workspace: 'probe.code-workspace', result: 'result.json', sourceHashes };
  const workspace = { folders: [{ path: 'workspace' }], settings: {
    'agentbrokerEditorProbe.targetThreadId': target, 'agentbrokerEditorProbe.preparationId': id } };
  fs.writeFileSync(workspaceFile, JSON.stringify(workspace), { flag: 'wx' });
  fs.writeFileSync(path.join(root, 'prepared.json'), JSON.stringify(manifest), { flag: 'wx' });
  const calls = [];
  const tabGroups = { activeTabGroup: { activeTab: undefined } };
  Object.defineProperty(tabGroups, 'all', { get() { throw new Error('Do not enumerate other tabs'); } });
  const vscode = { ExtensionMode: { Production: 1, Development: 2 }, ViewColumn: { One: 1 }, Uri, TabInputCustom,
    workspace: { isTrusted: true, workspaceFile: Uri.file(workspaceFile),
      workspaceFolders: [{ uri: Uri.file(path.join(root, 'workspace')) }] },
    window: { tabGroups }, commands: { executeCommand: async (...args) => {
      calls.push(args);
      tabGroups.activeTabGroup.activeTab = { input: new TabInputCustom(args[1], args[2]) };
    } } };
  const context = { extensionMode: 2, extensionPath: path.join(root, 'extension') };
  return { root, workspaceFile, manifest, workspace, calls, vscode, context,
    resultFile: path.join(root, 'result.json') };
}
function writeWorkspace(f) { fs.writeFileSync(f.workspaceFile, JSON.stringify(f.workspace)); }
async function refused(f, expected) {
  await assert.rejects(probe.runProbe(f.vscode, f.context, { tabMs: 0 }), error => error.probeCode === expected);
  assert.equal(f.calls.length, 0);
  assert.equal(fs.existsSync(f.resultFile), false);
}

test('one exact native GUI command, active custom tab only, typed private result', async () => {
  const f = fixture();
  const value = await probe.runProbe(f.vscode, f.context, { tabMs: 0 });
  assert.equal(value.status, 'opened_exact_editor');
  assert.equal(value.exactEditorObserved, true);
  assert.equal(f.calls.length, 1);
  const [command, uri, provider, options] = f.calls[0];
  assert.equal(command, 'vscode.openWith');
  assert.equal(uri.toString(), `openai-codex://route/local/${target}`);
  assert.equal(provider, 'chatgpt.conversationEditor');
  assert.deepEqual(options, { viewColumn: 1, preserveFocus: false, preview: false });
  const serialized = fs.readFileSync(f.resultFile, 'utf8');
  assert.deepEqual(JSON.parse(serialized), value);
  assert.equal(serialized.includes(target), false);
  assert.equal(serialized.includes(f.root), false);
  assert.ok(Object.entries(value).every(([key, item]) => key === 'status'
    ? item === 'opened_exact_editor' : typeof item === 'boolean' || typeof item === 'number'));
  await assert.rejects(probe.runProbe(f.vscode, f.context), error => error.probeCode === 'result_exists');
  assert.equal(f.calls.length, 1, 'an existing receipt prevents another GUI command');
});
test('Production mode refuses before any command or result', async () => {
  const f = fixture(); f.context.extensionMode = 1; await refused(f, 'development_required');
});
test('malformed, empty and mismatched workspace targets refuse', async () => {
  for (const invalid of ['bad-id', '00000000-0000-0000-0000-000000000000', other]) {
    const f = fixture(); f.workspace.settings['agentbrokerEditorProbe.targetThreadId'] = invalid;
    writeWorkspace(f); await refused(f, 'manifest_invalid');
  }
});
test('foreign workspace file, folders and preparation identity refuse', async () => {
  const foreign = fixture(); foreign.vscode.workspace.workspaceFile = Uri.file(path.join(foreign.root, 'other.code-workspace'));
  await refused(foreign, 'workspace_refused');
  const folder = fixture(); folder.vscode.workspace.workspaceFolders[0].uri = Uri.file(path.join(folder.root, 'other'));
  await refused(folder, 'workspace_refused');
  const identity = fixture(); identity.workspace.settings['agentbrokerEditorProbe.preparationId'] = 'f'.repeat(32);
  writeWorkspace(identity); await refused(identity, 'manifest_invalid');
  const extra = fixture(); extra.workspace.tasks = { tasks: [] }; writeWorkspace(extra);
  await refused(extra, 'manifest_invalid');
});
test('changed source and pre-existing result refuse before opening', async () => {
  const sourceChanged = fixture(); fs.appendFileSync(path.join(sourceChanged.root, 'extension/extension.js'), '\n// changed\n');
  await refused(sourceChanged, 'source_changed');
  const used = fixture(); fs.writeFileSync(used.resultFile, 'preserve me', { flag: 'wx' });
  await assert.rejects(probe.runProbe(used.vscode, used.context), error => error.probeCode === 'result_exists');
  assert.equal(fs.readFileSync(used.resultFile, 'utf8'), 'preserve me');
  assert.equal(used.calls.length, 0);
});
test('untrusted workspace does not request trust or execute a command', async () => {
  const f = fixture(); f.vscode.workspace.isTrusted = false;
  const value = await probe.runProbe(f.vscode, f.context);
  assert.equal(value.status, 'workspace_untrusted'); assert.equal(f.calls.length, 0);
});
test('wrong tab URI, provider or input type cannot produce exact-editor success', async () => {
  for (const kind of ['uri', 'provider', 'type']) {
    const f = fixture(); f.vscode.commands.executeCommand = async (...args) => {
      f.calls.push(args);
      const input = kind === 'type' ? { uri: args[1], viewType: args[2] }
        : new TabInputCustom(kind === 'uri' ? Uri.file(`/local/${other}`).with({ scheme: 'openai-codex', authority: 'route' }) : args[1],
          kind === 'provider' ? 'other.provider' : args[2]);
      f.vscode.window.tabGroups.activeTabGroup.activeTab = { input };
    };
    const value = await probe.runProbe(f.vscode, f.context, { tabMs: 0 });
    assert.equal(value.status, 'tab_not_observed'); assert.equal(value.exactEditorObserved, false);
    assert.equal(f.calls.length, 1);
  }
});
test('open failure does not export raw errors or retry', async () => {
  const f = fixture(); f.vscode.commands.executeCommand = async (...args) => {
    f.calls.push(args); throw new Error(`private failure ${target} ${f.root}`);
  };
  const value = await probe.runProbe(f.vscode, f.context);
  assert.equal(value.status, 'open_failed'); assert.equal(f.calls.length, 1);
  assert.equal(fs.readFileSync(f.resultFile, 'utf8').includes('private'), false);
});
test('bounded command timeout reports uncertainty without retry or closing windows', async () => {
  const f = fixture(); f.vscode.commands.executeCommand = (...args) => { f.calls.push(args); return new Promise(() => {}); };
  const value = await probe.runProbe(f.vscode, f.context, { commandMs: 2 });
  assert.equal(value.status, 'open_timeout'); assert.equal(value.timedOut, true); assert.equal(f.calls.length, 1);
});
test('a changed preparation cannot receive a receipt after opening', async () => {
  const f = fixture(); f.vscode.commands.executeCommand = async (...args) => {
    f.calls.push(args); fs.appendFileSync(path.join(f.root, 'prepared.json'), ' ');
  };
  await assert.rejects(probe.runProbe(f.vscode, f.context, { tabMs: 0 }), error => error.probeCode === 'manifest_changed');
  assert.equal(fs.existsSync(f.resultFile), false); assert.equal(f.calls.length, 1);
});
test('owned junction is refused without following it or opening a tab', async () => {
  const f = fixture(); const link = path.join(f.root, 'workspace');
  const destination = path.join(f.root, 'ordinary-target'); fs.mkdirSync(destination);
  fs.rmdirSync(link); fs.symlinkSync(destination, link, process.platform === 'win32' ? 'junction' : 'dir');
  try { await refused(f, 'path_refused'); }
  finally { fs.unlinkSync(link); }
});
