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

async function applyCompletion(document, item) {
    const replacement = item.textEdit?.range || item.range?.replacing || item.range;
    const insertion = item.textEdit?.newText || item.insertText;
    assert.ok(replacement instanceof vscode.Range, 'Filesystem completion must own the replacement range');
    assert.equal(typeof insertion, 'string');
    const edit = new vscode.WorkspaceEdit();
    edit.replace(document.uri, replacement, insertion);
    assert.ok(await vscode.workspace.applyEdit(edit));
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
        const source = `label="可莉🌟"\nboolean keep(item):\n\treturn item\nsymbol codex(file):\n    return ~/.codex/file\nconfig := $wsl::codex(config.toml)\nitems := [true,false,keep(true)] | $view::filter(keep)\nview_api := $view\nalias_items := [true,false] | view_api::filter(keep)\nfs => $std::fs\nstandard_ready = fs::is_directory(~/.codex)\nlogical = $std::bool::both(true, false)\nscope hello:\n    codex := ~/.codex\nnamed := hello::codex\nendpoints := [$local,$wsl]\nstandard_items := [true,false] | $std::ranges::filter(keep)\n"must never execute" -> ${output}\ndelete ${victim}\nif release "not a bool":\n    "must never execute" -> ${branchOutput}\n`;
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
        assert.ok(document.getText().includes('    return item'));
        assert.ok(document.getText().includes('[true, false, keep(true)]'));
        assert.ok(document.getText().includes('if release true:'));
        await record('document-formatting', { edits: formatting.length });

        const cursor = document.positionAt(document.getText().indexOf('$view::filter') + '$view::f'.length);
        const completion = await bounded('completion response', vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, cursor));
        assert.ok(completion && completion.items.some(item => (typeof item.label === 'string' ? item.label : item.label.label) === '$view::filter'));
        await record('range-completion', { label: '$view::filter' });

        const aliasCursor = document.positionAt(document.getText().indexOf('view_api::filter') + 'view_api::f'.length);
        const aliasCompletion = await bounded('API Scope alias completion', vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, aliasCursor));
        assert.ok(aliasCompletion && aliasCompletion.items.some(item => (typeof item.label === 'string' ? item.label : item.label.label) === 'filter'));
        await record('api-scope-alias-completion', { namespace: 'view_api', member: 'filter' });

        const current = document.getText();
        const qualifiedCall = document.positionAt(current.indexOf('$wsl::codex') + '$wsl::'.length);
        const definitions = await bounded('scope-qualified symbol definition', vscode.commands.executeCommand('vscode.executeDefinitionProvider', document.uri, qualifiedCall));
        const declared = document.positionAt(current.indexOf('codex(file)'));
        assert.ok(definitions && definitions.some(target => {
            const uri = target.targetUri || target.uri;
            const range = target.targetSelectionRange || target.range;
            return uri.toString() === document.uri.toString() && range.start.isEqual(declared);
        }), 'Scope qualification must not hide the lexical symbol declaration');
        await record('qualified-symbol-definition', { name: 'codex' });

        const fsCursor = document.positionAt(current.indexOf('fs::is_directory') + 'fs::is_d'.length);
        const fsCompletion = await bounded('stdlib alias completion', vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, fsCursor));
        assert.ok(fsCompletion && fsCompletion.items.some(item => (typeof item.label === 'string' ? item.label : item.label.label) === 'is_directory'));
        const fsDefinition = await bounded('stdlib native definition', vscode.commands.executeCommand('vscode.executeDefinitionProvider', document.uri, fsCursor));
        const native = fsDefinition.find(target => (target.targetUri || target.uri).scheme === 'msync-stdlib');
        assert.ok(native, 'Filesystem primitives must navigate to their embedded declaration source');
        const nativeUri = native.targetUri || native.uri;
        const nativeDocument = await bounded('stdlib source document', vscode.workspace.openTextDocument(nativeUri));
        const nativeRange = native.targetSelectionRange || native.range;
        assert.equal(nativeDocument.getText(nativeRange), 'is_directory');
        assert.ok(nativeDocument.getText().includes('native is_directory(path) -> Bool = "fs.is_directory"'));
        await record('stdlib-native-source-navigation', { uri: nativeUri.toString(), member: 'is_directory' });

        const pureCursor = document.positionAt(current.indexOf('$std::bool::both') + '$std::bool::'.length);
        const pureDefinitions = await bounded('stdlib source function definition', vscode.commands.executeCommand('vscode.executeDefinitionProvider', document.uri, pureCursor));
        const pure = pureDefinitions.find(target => (target.targetUri || target.uri).scheme === 'msync-stdlib');
        assert.ok(pure);
        const pureDocument = await bounded('stdlib source function document', vscode.workspace.openTextDocument(pure.targetUri || pure.uri));
        assert.equal(pureDocument.getText(pure.targetSelectionRange || pure.range), 'both');
        assert.ok(pureDocument.getText().includes('return left and right'));
        const parameterUse = pureDocument.positionAt(pureDocument.getText().indexOf('return left and right') + 'return '.length);
        const parameterDefinitions = await bounded('navigation inside stdlib source', vscode.commands.executeCommand('vscode.executeDefinitionProvider', pureDocument.uri, parameterUse));
        const parameterDeclaration = pureDocument.positionAt(pureDocument.getText().indexOf('both(left, right)') + 'both('.length);
        assert.ok(parameterDefinitions.some(target => (target.targetUri || target.uri).toString() === pureDocument.uri.toString() && (target.targetSelectionRange || target.range).start.isEqual(parameterDeclaration)));
        await record('stdlib-source-function-navigation', { member: 'both' });

        const rangeCursor = document.positionAt(current.indexOf('$std::ranges::filter') + '$std::ranges::'.length);
        const rangeDefinitions = await bounded('dependent native range definition', vscode.commands.executeCommand('vscode.executeDefinitionProvider', document.uri, rangeCursor));
        const rangeNative = rangeDefinitions.find(target => (target.targetUri || target.uri).scheme === 'msync-stdlib');
        assert.ok(rangeNative);
        const rangeDocument = await bounded('dependent native source document', vscode.workspace.openTextDocument(rangeNative.targetUri || rangeNative.uri));
        assert.equal(rangeDocument.getText(rangeNative.targetSelectionRange || rangeNative.range), 'filter');
        assert.ok(rangeDocument.getText().includes('native filter(values, predicate) = "ranges.filter"'));
        await record('range-native-source-navigation', { member: 'filter' });

        const memberCursor = document.positionAt(current.indexOf('hello::codex') + 'hello::'.length);
        const memberDefinitions = await bounded('lexical scope member definition', vscode.commands.executeCommand('vscode.executeDefinitionProvider', document.uri, memberCursor));
        const memberDeclaration = document.positionAt(current.indexOf('    codex :=') + 4);
        assert.ok(memberDefinitions.some(target => (target.targetSelectionRange || target.range).start.isEqual(memberDeclaration)));
        await record('lexical-namespace-definition', { namespace: 'hello', member: 'codex' });

        await fs.writeFile(path.join(workspace, 'codex.prompt'), 'local editor fixture\n');
        await fs.writeFile(path.join(workspace, 'two words.prompt'), 'quoted filename fixture\n');
        await fs.mkdir(path.join(workspace, '.codex'));
        await fs.writeFile(path.join(workspace, '.codex', 'codex.prompt'), 'nested editor fixture\n');
        const completionFile = path.join(workspace, 'path-completion.msync');
        await fs.writeFile(completionFile, 'probe := $local::');
        const pathDocument = await vscode.workspace.openTextDocument(vscode.Uri.file(completionFile));
        await vscode.window.showTextDocument(pathDocument);
        const pathCursor = pathDocument.positionAt(pathDocument.getText().length);
        const fileCompletion = await until('endpoint filesystem completion',
            () => vscode.commands.executeCommand('vscode.executeCompletionItemProvider', pathDocument.uri, pathCursor),
            value => value && value.items.some(item => (typeof item.label === 'string' ? item.label : item.label.label) === 'codex.prompt'));
        const fileItem = fileCompletion.items.find(item => (typeof item.label === 'string' ? item.label : item.label.label) === 'codex.prompt');
        assert.equal(fileItem.kind, vscode.CompletionItemKind.File);
        await applyCompletion(pathDocument, fileItem);
        assert.equal(pathDocument.getText(), 'probe := $local::"codex.prompt"');
        await until('completed path compiles', () => vscode.languages.getDiagnostics(pathDocument.uri), value => value.length === 0);
        await record('endpoint-filesystem-completion', { label: 'codex.prompt', appliedText: pathDocument.getText() });
        const resetPath = async source => {
            const edit = new vscode.WorkspaceEdit();
            edit.replace(pathDocument.uri, new vscode.Range(pathDocument.positionAt(0), pathDocument.positionAt(pathDocument.getText().length)), source);
            assert.ok(await vscode.workspace.applyEdit(edit));
        };
        await resetPath('probe := $local::two');
        const spaced = await until('spaced filename completion',
            () => vscode.commands.executeCommand('vscode.executeCompletionItemProvider', pathDocument.uri, pathDocument.positionAt(pathDocument.getText().length)),
            value => value && value.items.some(item => (typeof item.label === 'string' ? item.label : item.label.label) === 'two words.prompt'));
        await applyCompletion(pathDocument, spaced.items.find(item => (typeof item.label === 'string' ? item.label : item.label.label) === 'two words.prompt'));
        assert.equal(pathDocument.getText(), 'probe := $local::"two words.prompt"');
        await record('quoted-filename-completion', { appliedText: pathDocument.getText() });

        await resetPath('probe := $local::');
        const hiddenDirectory = await until('hidden directory completion',
            () => vscode.commands.executeCommand('vscode.executeCompletionItemProvider', pathDocument.uri, pathDocument.positionAt(pathDocument.getText().length)),
            value => value && value.items.some(item => (typeof item.label === 'string' ? item.label : item.label.label).replace(/\/$/, '') === '.codex'));
        const directoryItem = hiddenDirectory.items.find(item => (typeof item.label === 'string' ? item.label : item.label.label).replace(/\/$/, '') === '.codex');
        assert.equal(directoryItem.kind, vscode.CompletionItemKind.Folder);
        await applyCompletion(pathDocument, directoryItem);
        assert.equal(pathDocument.getText(), 'probe := $local::".codex"/');
        const nested = await until('nested file completion',
            () => vscode.commands.executeCommand('vscode.executeCompletionItemProvider', pathDocument.uri, pathDocument.positionAt(pathDocument.getText().length)),
            value => value && value.items.some(item => (typeof item.label === 'string' ? item.label : item.label.label) === 'codex.prompt'));
        await applyCompletion(pathDocument, nested.items.find(item => (typeof item.label === 'string' ? item.label : item.label.label) === 'codex.prompt'));
        assert.equal(pathDocument.getText(), 'probe := $local::".codex"/"codex.prompt"');
        await record('hidden-directory-file-completion', { appliedText: pathDocument.getText() });
        await vscode.window.showTextDocument(document);

        const legend = await bounded('semantic token legend', vscode.commands.executeCommand('vscode.provideDocumentSemanticTokensLegend', document.uri));
        const tokens = await bounded('semantic tokens', vscode.commands.executeCommand('vscode.provideDocumentSemanticTokens', document.uri));
        assert.ok(legend && tokens && tokens.data.length, 'The real client must register a semantic token provider');
        const pieces = semanticPieces(document, legend, tokens.data);
        assert.ok(pieces.some(piece => piece.text === 'keep' && piece.type === 'function'));
        assert.ok(pieces.some(piece => piece.text === 'item' && piece.type === 'parameter'));
        assert.ok(pieces.some(piece => piece.text === '"可莉🌟"' && piece.type === 'string'));
        assert.ok(pieces.some(piece => piece.text === '$' && piece.type === 'operator'));
        assert.ok(pieces.some(piece => piece.text === '::' && piece.type === 'operator'));
        assert.ok(pieces.some(piece => piece.text === '/' && piece.type === 'operator'));
        await record('semantic-tokens', { count: tokens.data.length / 5, tokenTypes: legend.tokenTypes });
        await bounded('explicit language server restart', vscode.commands.executeCommand('msync.restartLanguageServer'));
        const afterRestart = await bounded('completion after language server restart', vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, fsCursor));
        assert.ok(afterRestart && afterRestart.items.some(item => (typeof item.label === 'string' ? item.label : item.label.label) === 'is_directory'));
        await record('language-server-restart', { completion: 'is_directory' });
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
