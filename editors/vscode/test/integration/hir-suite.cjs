'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { execFile } = require('node:child_process');
const { promisify } = require('node:util');
const { createHash } = require('node:crypto');
const vscode = require('vscode');

const execute = promisify(execFile);

async function bounded(description, promise) {
    let timer;
    try {
        return await Promise.race([promise, new Promise((_, reject) => {
            timer = setTimeout(() => reject(new Error(`Timed out waiting for ${description}`)), 30_000);
        })]);
    } finally { clearTimeout(timer); }
}

function changing(document, predicate, description) {
    let subscription;
    const promise = new Promise(resolve => {
        subscription = vscode.workspace.onDidChangeTextDocument(event => {
            if (event.document.uri.toString() === document.uri.toString() && predicate(document.getText())) resolve();
        });
    });
    return {
        wait: () => bounded(description, predicate(document.getText()) ? Promise.resolve() : promise),
        dispose: () => subscription.dispose(),
    };
}

function flatten(symbols) {
    return symbols.flatMap(symbol => [symbol, ...flatten(symbol.children || [])]);
}

async function digest(filename) {
    return createHash('sha256').update(await fs.readFile(filename)).digest('hex');
}

function source(marker, destination) {
    return 'fn identity(value: Path) -> Path { value };\n'
        + 'let input? = if exists ./input { ./input } else { null };\n'
        + 'if input is not null { identity(input) -> ./backup; };\n'
        + `"${marker}" -> "${destination.replaceAll('"', '""')}";\n`;
}

async function run() {
    const workspace = process.env.MSYNC_TEST_WORKSPACE;
    const runRoot = process.env.MSYNC_TEST_ROOT;
    const binary = process.env.MSYNC_TEST_SERVER_PATH;
    assert.ok(workspace && runRoot && binary, 'Launch through the isolated frozen-server host runner');
    const cwd = path.join(workspace, 'HIR workspace 工作 𐐀');
    await fs.mkdir(cwd, { recursive: true });
    const sourcePath = path.join(cwd, 'semantic source 𐐀.msync');
    const artifactPath = path.join(cwd, 'semantic program 𐐀.mhir');
    const sideEffect = path.join(cwd, 'inspection-must-not-execute');
    const command = args => execute(binary, ['--no-config', '--color', 'never', ...args], { cwd, encoding: 'utf8' });
    async function emit(marker) {
        await fs.writeFile(sourcePath, source(marker, sideEffect));
        await bounded('export typed HIR', command(['compile', sourcePath, '--emit', 'hir', '-o', artifactPath]));
    }
    const decode = async () => JSON.parse((await bounded('inspect native HIR', command(['disassemble', artifactPath, '--json']))).stdout);
    await emit('FIRST HIR GENERATION');
    await fs.unlink(sourcePath);
    const initialHash = await digest(artifactPath);
    const model = await decode();
    assert.equal(model.schemaVersion, 1);
    assert.equal(model.artifactKind, 'hir');
    assert.match(model.stage, /hir/i);
    assert.match(model.text, /FIRST HIR GENERATION/);
    assert.match(model.text, /identity/);
    assert.match(model.text, /ReadLocation|ReadStorage/);
    assert.match(model.text, /\bIf\b/);

    const extension = vscode.extensions.getExtension('msync.msync-language');
    assert.ok(extension);
    const contribution = extension.packageJSON.contributes.customEditors.find(editor => editor.viewType === 'msync.bytecode');
    assert.ok(contribution?.selector.some(selector => selector.filenamePattern === '*.mhir'));
    const uri = vscode.Uri.file(artifactPath);
    const observations = [];
    const workbench = vscode.workspace.getConfiguration('workbench');
    const originalTheme = workbench.get('colorTheme');
    try {
        for (const theme of ['Default Dark Modern', 'Default Light Modern']) {
            await workbench.update('colorTheme', theme, vscode.ConfigurationTarget.Global);
            await bounded('open default HIR editor', vscode.commands.executeCommand('vscode.openWith', uri, 'msync.bytecode'));
            const tab = vscode.window.tabGroups.all.flatMap(group => group.tabs)
                .find(candidate => candidate.input instanceof vscode.TabInputCustom && candidate.input.uri.toString() === uri.toString());
            assert.ok(tab && tab.input.viewType === 'msync.bytecode');
            await bounded('open semantic HIR as text', vscode.commands.executeCommand('msync.openArtifactText', uri));
            const document = vscode.window.activeTextEditor?.document;
            assert.ok(document);
            assert.equal(document.uri.scheme, 'msync-artifact');
            assert.equal(document.languageId, 'msync-ir');
            assert.equal(document.getText(), model.text);
            const symbols = await bounded('HIR outline', vscode.commands.executeCommand('vscode.executeDocumentSymbolProvider', document.uri));
            assert.ok(symbols && flatten(symbols).some(symbol => symbol.name.includes('identity')));
            const reference = model.references.find(value => Number.isInteger(value.targetLine));
            assert.ok(reference, 'Semantic identity navigation does not require original source');
            const definitions = await bounded('HIR definition', vscode.commands.executeCommand('vscode.executeDefinitionProvider', document.uri,
                new vscode.Position(reference.range.start.line, reference.range.start.character)));
            assert.ok(definitions && definitions.some(definition => {
                const target = definition.targetUri || definition.uri;
                const selected = definition.targetSelectionRange || definition.range;
                return target.toString() === document.uri.toString() && selected.start.line === reference.targetLine;
            }));
            assert.ok(model.hovers.length, 'The native semantic renderer supplies type/identity explanations');
            const at = model.hovers[0].range.start;
            const hovers = await bounded('HIR hover', vscode.commands.executeCommand('vscode.executeHoverProvider', document.uri,
                new vscode.Position(at.line, at.character)));
            assert.ok(hovers && hovers.some(hover => hover.contents.length));
            try { await vscode.commands.executeCommand('type', { text: 'CORRUPT' }); }
            catch { /* VS Code can reject a readonly edit through an error response. */ }
            assert.equal(document.getText(), model.text);
            assert.equal(document.isDirty, false);
            observations.push({ theme, symbols: flatten(symbols).length, definitions: definitions.length, hovers: hovers.length });
        }
    } finally { await workbench.update('colorTheme', originalTheme, vscode.ConfigurationTarget.Global); }
    assert.equal(await digest(artifactPath), initialHash);
    assert.equal(await fs.stat(sideEffect).then(() => true, () => false), false);

    const document = vscode.window.activeTextEditor.document;
    let update = changing(document, text => text.includes('SECOND HIR GENERATION'), 'automatic semantic HIR publication refresh');
    try {
        await emit('SECOND HIR GENERATION');
        await fs.unlink(sourcePath);
        await update.wait();
    } finally { update.dispose(); }
    const valid = await fs.readFile(artifactPath);
    const finalHash = await digest(artifactPath);
    update = changing(document, text => /cannot|could not|invalid|not an msync/i.test(text), 'corrupt semantic HIR diagnostic');
    try {
        await fs.writeFile(artifactPath, 'invalid HIR');
        await update.wait();
    } finally { update.dispose(); }
    assert.equal((await fs.readFile(artifactPath)).toString(), 'invalid HIR');
    update = changing(document, text => text.includes('SECOND HIR GENERATION'), 'repaired semantic HIR refresh');
    try {
        await fs.writeFile(artifactPath, valid);
        await update.wait();
    } finally { update.dispose(); }
    assert.equal(await digest(artifactPath), finalHash);
    assert.equal(await fs.stat(sideEffect).then(() => true, () => false), false);
    const result = { suite: 'hir', status: 'passed', vscodeVersion: vscode.version, sourceIndependent: true,
        readonly: true, refresh: true, recovery: true, initialHash, finalHash, observations };
    await fs.writeFile(path.join(runRoot, 'hir-results.json'), JSON.stringify(result, null, 2));
    console.log(`MSYNC_HIR_HOST ${JSON.stringify(result)}`);
}

module.exports = { run };
