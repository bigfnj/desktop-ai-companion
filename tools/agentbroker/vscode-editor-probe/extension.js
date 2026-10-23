'use strict';

// DEVELOPMENT-ONLY GUI test helper. No backend protocol, model input or relay.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const PROVIDER = 'chatgpt.conversationEditor';
const COMMAND = 'vscode.openWith';
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const RUN = /^editor-probe-([0-9a-f]{32})$/;
let started = false;

function refuse(code) { const error = new Error(code); error.probeCode = code; throw error; }
function samePath(left, right) {
  const a = path.resolve(left), b = path.resolve(right);
  return process.platform === 'win32' ? a.toLowerCase() === b.toLowerCase() : a === b;
}
function safePath(value, root) {
  const full = path.resolve(value), relative = path.relative(path.resolve(root), full);
  if (relative === '..' || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative)) refuse('path_refused');
  if (full.startsWith('\\\\') || /[\x00-\x1f]/.test(full) || full.slice(3).includes(':')) refuse('path_refused');
  const normalized = full.replaceAll('\\', '/').toLowerCase();
  if (/\/(?:\.git\/credentials|\.config\/(?:google-chrome|chromium)|appdata\/local\/(?:google\/chrome|microsoft\/edge)\/user data)(?:\/|$)/.test(normalized)
      || /\/appdata\/.*\/mozilla\/firefox(?:\/|$)/.test(normalized)) refuse('path_refused');
  const parts = full.slice(path.parse(full).root.length).split(path.sep);
  if (parts.some(part => /^(?:\.env(?:\..*)?|\.npmrc|privatekey.*|\.aws|\.ssh|\.kube|\.mozilla)$/i.test(part)
      || /\.(?:env|pem|key|p12|pfx|keystore)$/i.test(part))) refuse('path_refused');
  let current = path.parse(full).root;
  for (const part of parts) {
    current = path.join(current, part);
    let info;
    try { info = fs.lstatSync(current); } catch (error) {
      if (error.code === 'ENOENT') break;
      refuse('path_refused');
    }
    if (info.isSymbolicLink() || !samePath(fs.realpathSync.native(current), current)) refuse('path_refused');
  }
  return full;
}
function readJson(file, root) {
  safePath(file, root);
  const stat = fs.lstatSync(file);
  if (!stat.isFile() || stat.size > 32768) refuse('manifest_invalid');
  try { return JSON.parse(fs.readFileSync(file, 'utf8')); } catch { refuse('manifest_invalid'); }
}
function hash(file, root) {
  safePath(file, root);
  const stat = fs.lstatSync(file);
  if (!stat.isFile() || stat.size > 65536) refuse('manifest_invalid');
  return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
}
function validatePrepared(vscode, context) {
  if (context.extensionMode !== vscode.ExtensionMode.Development) refuse('development_required');
  const extension = path.resolve(context.extensionPath);
  const root = path.dirname(extension);
  const match = RUN.exec(path.basename(root));
  if (path.basename(extension) !== 'extension' || !match
      || path.basename(path.dirname(root)) !== 'agentbroker-editor-probes'
      || path.basename(path.dirname(path.dirname(root))) !== 'artifacts') refuse('workspace_refused');
  safePath(root, root);
  const workspaceFile = path.join(root, 'probe.code-workspace');
  const liveFile = vscode.workspace.workspaceFile;
  if (!liveFile || liveFile.scheme !== 'file' || !samePath(liveFile.fsPath, workspaceFile)) refuse('workspace_refused');
  const manifest = readJson(path.join(root, 'prepared.json'), root);
  const workspace = readJson(workspaceFile, root);
  const target = workspace.settings?.['agentbrokerEditorProbe.targetThreadId'];
  if (manifest.schema !== 1 || manifest.kind !== 'agentbroker-vscode-editor-probe'
      || manifest.developmentOnly !== true || manifest.probeId !== match[1]
      || workspace.settings?.['agentbrokerEditorProbe.preparationId'] !== match[1]
      || typeof target !== 'string' || !GUID.test(target)
      || target === '00000000-0000-0000-0000-000000000000' || manifest.targetThreadId !== target
      || manifest.workspace !== 'probe.code-workspace' || manifest.result !== 'result.json'
      || Object.keys(workspace).sort().join(',') !== 'folders,settings'
      || Object.keys(workspace.settings).length !== 2
      || workspace.folders?.length !== 1 || workspace.folders[0].path !== 'workspace'
      || Object.keys(workspace.folders[0]).join(',') !== 'path') refuse('manifest_invalid');
  const folders = vscode.workspace.workspaceFolders;
  if (folders?.length !== 1 || folders[0].uri.scheme !== 'file'
      || !samePath(folders[0].uri.fsPath, path.join(root, 'workspace'))) refuse('workspace_refused');
  safePath(folders[0].uri.fsPath, root);
  for (const name of ['extension.js', 'package.json']) {
    if (hash(path.join(extension, name), root) !== manifest.sourceHashes?.[name]) refuse('source_changed');
  }
  const resultFile = safePath(path.join(root, 'result.json'), root);
  if (fs.existsSync(resultFile)) refuse('result_exists');
  return { root, workspaceFile, resultFile, target, manifestHash: hash(path.join(root, 'prepared.json'), root) };
}
function result(status, attempted = false, observed = false, timedOut = false) {
  return { schema: 1, developmentMode: true, preparedWorkspaceVerified: true,
    commandAttempted: attempted, exactEditorObserved: observed, timedOut, status };
}
function activeEditorMatches(vscode, targetUri) {
  const input = vscode.window.tabGroups.activeTabGroup.activeTab?.input;
  return input instanceof vscode.TabInputCustom && input.viewType === PROVIDER
    && input.uri.toString() === targetUri.toString();
}
async function openExactEditor(vscode, prepared, timing = {}) {
  if (vscode.workspace.isTrusted !== true) return result('workspace_untrusted');
  const uri = vscode.Uri.file(`/local/${prepared.target}`).with({ scheme: 'openai-codex', authority: 'route' });
  const commandMs = timing.commandMs ?? 15000, tabMs = timing.tabMs ?? 3000;
  let timer;
  try {
    const command = Promise.resolve().then(() => vscode.commands.executeCommand(COMMAND, uri, PROVIDER,
      { viewColumn: vscode.ViewColumn.One, preserveFocus: false, preview: false }));
    const completed = await Promise.race([command.then(() => true), new Promise(resolve => {
      timer = setTimeout(() => resolve(false), commandMs);
    })]);
    if (!completed) return result('open_timeout', true, false, true);
  } catch { return result('open_failed', true); }
  finally { clearTimeout(timer); }
  const until = Date.now() + tabMs;
  do {
    if (activeEditorMatches(vscode, uri)) return result('opened_exact_editor', true, true);
    if (Date.now() >= until) break;
    await new Promise(resolve => setTimeout(resolve, Math.min(100, until - Date.now())));
  } while (Date.now() <= until);
  return result('tab_not_observed', true);
}
function writeResult(prepared, value) {
  safePath(prepared.resultFile, prepared.root);
  if (hash(path.join(prepared.root, 'prepared.json'), prepared.root) !== prepared.manifestHash) refuse('manifest_changed');
  const descriptor = fs.openSync(prepared.resultFile, 'wx', 0o600);
  try { fs.writeFileSync(descriptor, `${JSON.stringify(value)}\n`, 'utf8'); }
  finally { fs.closeSync(descriptor); }
}
async function runProbe(vscode, context, timing) {
  const prepared = validatePrepared(vscode, context);
  const value = await openExactEditor(vscode, prepared, timing);
  writeResult(prepared, value);
  return value;
}
async function activate(context) {
  if (started) return;
  started = true;
  try { await runProbe(require('vscode'), context); }
  catch { /* Refuse without writing to a foreign workspace or exporting raw errors. */ }
}
function deactivate() { /* Never close windows, stop engines or submit work. */ }
module.exports = { activate, deactivate, validatePrepared, openExactEditor, runProbe,
  _test: { safePath, activeEditorMatches } };
