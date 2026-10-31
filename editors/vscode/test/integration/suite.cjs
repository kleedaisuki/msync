'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const vscode = require('vscode');

const EXTENSION = 'msync.msync-language';
const TIMEOUT = 30_000;

async function bounded(description, promise) {
    let timer;
    try {
        return await Promise.race([promise, new Promise((_, reject) => {
            timer = setTimeout(() => reject(new Error(`Timed out waiting for ${description}`)), TIMEOUT);
        })]);
    } finally {
        clearTimeout(timer);
    }
}

async function until(description, read, accepts) {
    const deadline = Date.now() + TIMEOUT;
    let last;
    do {
        last = await read();
        if (accepts(last)) return last;
        await new Promise(resolve => setTimeout(resolve, 75));
    } while (Date.now() < deadline);
    throw new Error(`Timed out waiting for ${description}; last result: ${JSON.stringify(last)}`);
}

async function effectsAreAbsent(workspace) {
    assert.equal(await fs.readFile(path.join(workspace, 'preserved.txt'), 'utf8'), 'keep me\n', 'Language services must not execute delete');
    for (const name of ['never-created.txt', 'never-created-branch.txt']) {
        await assert.rejects(fs.access(path.join(workspace, name)), { code: 'ENOENT' }, `Language services must not execute transfer into ${name}`);
    }
}

function semanticPieces(document, legend, data) {
    assert.equal(data.length % 5, 0);
    const pieces = [];
    let line = 0;
    let character = 0;
    for (let index = 0; index < data.length; index += 5) {
        line += data[index];
        character = data[index] === 0 ? character + data[index + 1] : data[index + 1];
        const length = data[index + 2];
        assert.ok(length > 0);
        assert.ok(line < document.lineCount);
        assert.ok(character + length <= document.lineAt(line).text.length, 'Semantic ranges must be valid UTF-16 positions');
        pieces.push({
            text: document.getText(new vscode.Range(line, character, line, character + length)),
            type: legend.tokenTypes[data[index + 3]],
        });
    }
    return pieces;
}

async function run() {
    const workspace = process.env.MSYNC_TEST_WORKSPACE;
    const runRoot = process.env.MSYNC_TEST_ROOT;
    assert.ok(workspace && runRoot, 'Launch this suite through test/integration/run.cjs');
    const phases = [];
    let document;
    const record = async (phase, detail = {}) => {
        phases.push({ phase, ...detail });
        console.log(`MSYNC_HOST_GATE ${JSON.stringify(phases[phases.length - 1])}`);
        await fs.writeFile(path.join(runRoot, 'host-results.json'), JSON.stringify({ vscodeVersion: vscode.version, phases }, null, 2));
    };
    try {
        assert.equal(vscode.workspace.getConfiguration('msync').get('serverPath'), process.env.MSYNC_TEST_SERVER_PATH);
        const output = `$local::${JSON.stringify(path.join(workspace, 'never-created.txt'))}`;
        const branchOutput = `$local::${JSON.stringify(path.join(workspace, 'never-created-branch.txt'))}`;
        const victim = `$local::${JSON.stringify(path.join(workspace, 'preserved.txt'))}`;
        const source = `label="可莉🌟"\nboolean keep(item):\n\treturn exists item\nitems := ranges::of("a","b") | views::filter(keep)\n"must never execute" -> ${output}\ndelete ${victim}\nif "not a bool":\n    "must never execute" -> ${branchOutput}\n`;
        const filename = path.join(workspace, 'journey.msync');
        await fs.writeFile(filename, source);
        document = await vscode.workspace.openTextDocument(vscode.Uri.file(filename));
        await vscode.window.showTextDocument(document);
        assert.equal(document.languageId, 'msync');
        const extension = vscode.extensions.getExtension(EXTENSION);
        assert.ok(extension, 'The production msync extension must be installed in the Development Host');
        assert.equal(extension.packageJSON.main, './out/extension.cjs');
        await bounded('production extension activation', extension.activate());
        assert.ok(extension.isActive);
        await record('production-extension-active', { extension: extension.id, vscodeVersion: vscode.version });

        const diagnostics = await until('real type-error diagnostic',
            () => vscode.languages.getDiagnostics(document.uri),
            values => values.some(value => value.severity === vscode.DiagnosticSeverity.Error && /bool/i.test(value.message)));
        await record('typed-diagnostic', { messages: diagnostics.map(value => value.message) });
        await effectsAreAbsent(workspace);

        const bad = document.getText().indexOf('"not a bool"');
        const edit = new vscode.WorkspaceEdit();
        edit.replace(document.uri, new vscode.Range(document.positionAt(bad), document.positionAt(bad + '"not a bool"'.length)), 'true');
        assert.ok(await vscode.workspace.applyEdit(edit));
        await until('diagnostic clearing after a real document edit',
            () => vscode.languages.getDiagnostics(document.uri), values => values.length === 0);
        await record('edit-clears-diagnostic', { documentVersion: document.version });

        const formatting = await bounded('document formatting response', vscode.commands.executeCommand('vscode.executeFormatDocumentProvider', document.uri, { insertSpaces: true, tabSize: 4 }));
        assert.ok(formatting && formatting.length, 'The real server must return canonical formatting edits');
        const formatted = new vscode.WorkspaceEdit();
        formatted.set(document.uri, formatting);
        assert.ok(await vscode.workspace.applyEdit(formatted));
        assert.ok(document.getText().includes('label = "可莉🌟"'));
        assert.ok(document.getText().includes('    return exists'));
        await record('document-formatting', { edits: formatting.length });

        const cursor = document.positionAt(document.getText().indexOf('views::filter') + 'views::f'.length);
        const completion = await bounded('completion response', vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, cursor));
        assert.ok(completion && completion.items.some(item => (typeof item.label === 'string' ? item.label : item.label.label) === 'views::filter'));
        await record('range-completion', { label: 'views::filter' });

        const legend = await bounded('semantic token legend', vscode.commands.executeCommand('vscode.provideDocumentSemanticTokensLegend', document.uri));
        const tokens = await bounded('semantic tokens', vscode.commands.executeCommand('vscode.provideDocumentSemanticTokens', document.uri));
        assert.ok(legend && tokens && tokens.data.length, 'The real client must register a semantic token provider');
        const pieces = semanticPieces(document, legend, tokens.data);
        assert.ok(pieces.some(piece => piece.text === 'keep' && piece.type === 'function'));
        assert.ok(pieces.some(piece => piece.text === 'item' && piece.type === 'parameter'));
        assert.ok(pieces.some(piece => piece.text === '"可莉🌟"' && piece.type === 'string'));
        await record('semantic-tokens', { count: tokens.data.length / 5, tokenTypes: legend.tokenTypes });
        await effectsAreAbsent(workspace);
        await record('no-dsl-side-effects', { preserved: 'preserved.txt', absent: ['never-created.txt', 'never-created-branch.txt'] });
    } catch (error) {
        const diagnostics = document ? vscode.languages.getDiagnostics(document.uri).map(value => ({
            message: value.message, severity: value.severity, source: value.source, range: value.range,
        })) : [];
        await fs.writeFile(path.join(runRoot, 'host-failure.json'), JSON.stringify({
            error: error instanceof Error ? error.stack : String(error),
            vscodeVersion: vscode.version,
            configuredServer: vscode.workspace.getConfiguration('msync').get('serverPath'),
            activeExtensions: vscode.extensions.all.filter(extension => extension.isActive).map(extension => extension.id),
            documentVersion: document?.version,
            diagnostics,
            phases,
        }, null, 2));
        throw error;
    }
}

module.exports = { run };
