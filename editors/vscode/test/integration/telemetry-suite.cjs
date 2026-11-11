'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const { execFile } = require('node:child_process');
const path = require('node:path');
const { promisify } = require('node:util');
const vscode = require('vscode');

const execute = promisify(execFile);

async function bounded(description, action) {
    let timer;
    try {
        return await Promise.race([action, new Promise((_, reject) => {
            timer = setTimeout(() => reject(new Error(`Timed out waiting for ${description}`)), 30_000);
        })]);
    } finally { clearTimeout(timer); }
}

async function missing(filename) {
    await assert.rejects(fs.stat(filename), { code: 'ENOENT' });
}

async function hover(document) {
    const result = await bounded('production hover', vscode.commands.executeCommand(
        'vscode.executeHoverProvider', document.uri, new vscode.Position(1, 3)));
    const text = result.flatMap(value => value.contents.map(content =>
        typeof content === 'string' ? content : content.value)).join('\n');
    assert.match(text, /Number/);
}

async function waitForTokens(database, suffix = '') {
    const python = process.env.MSYNC_TEST_PYTHON || (process.platform === 'win32' ? 'python' : 'python3');
    await bounded(`semantic tokens ${suffix || 'full'} in SQLite`, execute(python, [
        path.join(__dirname, 'verify-telemetry.py'), database, '--wait-for', `textDocument/semanticTokens/full${suffix}`,
    ]));
}

async function run() {
    const runRoot = process.env.MSYNC_TEST_ROOT;
    const workspace = process.env.MSYNC_TEST_WORKSPACE;
    const binary = process.env.MSYNC_TEST_SERVER_PATH;
    assert.ok(runRoot && workspace && binary, 'Launch through the isolated native-host runner');
    const database = path.join(runRoot, 'editor telemetry.sqlite');
    const ignored = path.join(runRoot, 'workspace telemetry.sqlite');
    const source = path.join(workspace, 'editor-telemetry.msync');
    await fs.writeFile(source, 'let unique_user_source_must_not_be_in_diagnostics = 7;\nunique_user_source_must_not_be_in_diagnostics;\n'
        + Array.from({ length: 1000 }, (_, index) => `let item_${index} = ${index};\n`).join(''));
    const document = await vscode.workspace.openTextDocument(vscode.Uri.file(source));
    await vscode.window.showTextDocument(document, { preview: false });
    const extension = vscode.extensions.getExtension('msync.msync-language');
    await bounded('production extension activation', extension.activate());
    const configuration = vscode.workspace.getConfiguration('msync.telemetry');
    assert.equal(configuration.get('enabled'), false, 'Workspace settings cannot enable a machine-local recorder');
    assert.equal(vscode.env.isTelemetryEnabled, false, 'This fixture disables Microsoft telemetry');
    await hover(document);
    await missing(database);
    await missing(ignored);

    const target = vscode.ConfigurationTarget.Global;
    await configuration.update('database', database, target);
    await configuration.update('level', 'diagnostics', target);
    await configuration.update('enabled', true, target);
    await bounded('telemetry-enabled server restart', vscode.commands.executeCommand('msync.restartLanguageServer'));
    await hover(document);
    await waitForTokens(database);
    const edit = new vscode.WorkspaceEdit();
    const number = document.lineAt(0).text.lastIndexOf('7');
    edit.replace(document.uri, new vscode.Range(0, number, 0, number + 1), '77');
    assert.ok(await vscode.workspace.applyEdit(edit));
    await waitForTokens(database, '/delta');
    const completion = await bounded('production completion', vscode.commands.executeCommand(
        'vscode.executeCompletionItemProvider', document.uri, new vscode.Position(1, 3)));
    assert.ok(completion.items.length > 0);
    const library = await bounded('stdlib round trip',
        vscode.workspace.openTextDocument(vscode.Uri.parse('msync-stdlib:///0.8.0/fs.msync')));
    assert.match(library.getText(), /copy_file/);
    const artifact = path.join(workspace, 'editor-telemetry.mso');
    await bounded('compile inert artifact', execute(binary, [
        '--no-config', '--no-telemetry', 'compile', '-O0', source, '-o', artifact,
    ], { cwd: workspace }));
    await bounded('open artifact as IR', vscode.commands.executeCommand('msync.openArtifactText', vscode.Uri.file(artifact)));
    assert.equal(vscode.window.activeTextEditor.document.uri.scheme, 'msync-artifact');
    assert.match(vscode.window.activeTextEditor.document.getText(), /Constant/i);

    await configuration.update('enabled', false, target);
    await bounded('telemetry-disabled server restart', vscode.commands.executeCommand('msync.restartLanguageServer'));
    const saved = await fs.readFile(database);
    assert.ok(saved.length > 0);
    await hover(document);
    const otherLibrary = await bounded('stdlib round trip with collection disabled',
        vscode.workspace.openTextDocument(vscode.Uri.parse('msync-stdlib:///0.8.0/ranges.msync')));
    assert.match(otherLibrary.getText(), /materialize/);
    assert.deepEqual(await fs.readFile(database), saved, 'Opt-out leaves the local database unchanged');
    await missing(ignored);
    console.log(`MSYNC_TELEMETRY_HOST ${JSON.stringify({ vscodeVersion: vscode.version, database, disabledRestored: true })}`);
}

module.exports = { run };
