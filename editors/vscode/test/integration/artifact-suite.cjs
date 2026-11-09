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

function changing(document, predicate) {
    let subscription;
    const promise = new Promise(resolve => {
        subscription = vscode.workspace.onDidChangeTextDocument(event => {
            if (event.document.uri.toString() === document.uri.toString() && predicate(event.document.getText())) resolve();
        });
    });
    return { wait: () => bounded('artifact document refresh', predicate(document.getText()) ? Promise.resolve() : promise),
        dispose: () => subscription.dispose() };
}

function source(marker, effectPath) {
    return 'fn select(file: Path, ready: Bool) -> Path { if ready { file } else { ./unused } };\n'
        + 'let selected = select(./input, exists ./not-present);\n'
        + `"${marker} <script>alert(""artifact"")</script> command:msync.test.artifactInjection \u0001" -> "${effectPath.replaceAll('"', '""')}";\n`
        + 'selected\n';
}

function flatten(values) {
    return values.flatMap(value => [value, ...flatten(value.children || [])]);
}

async function digest(filename) {
    return createHash('sha256').update(await fs.readFile(filename)).digest('hex');
}

async function run() {
    const workspace = process.env.MSYNC_TEST_WORKSPACE;
    const runRoot = process.env.MSYNC_TEST_ROOT;
    const binary = process.env.MSYNC_TEST_SERVER_PATH;
    assert.ok(workspace && runRoot && binary, 'Launch through the isolated artifact-host runner');
    const cwd = path.join(workspace, 'artifact workspace 工作 𐐀');
    await fs.mkdir(cwd, { recursive: true });
    const sourcePath = path.join(cwd, 'inspectable 𐐀.msync');
    const objectPath = path.join(cwd, 'inspectable 𐐀.mso');
    const executablePath = path.join(cwd, 'inspectable 𐐀.mexe');
    const command = args => execute(binary, ['--no-config', '--color', 'never', ...args], { cwd, encoding: 'utf8' });
    async function compile(marker) {
        await fs.writeFile(sourcePath, source(marker, path.join(cwd, 'viewer-side-effect')));
        await bounded('compile inert artifact', command(['compile', sourcePath, '-O0', '-o', objectPath]));
    }
    const model = async filename => JSON.parse((await command(['disassemble', filename, '--json'])).stdout);
    await compile('FIRST GENERATION');
    await bounded('bind inert executable', command(['bind-ir', objectPath, '--source-file', sourcePath, '-o', executablePath]));
    const initialHashes = { object: await digest(objectPath), executable: await digest(executablePath) };
    await fs.unlink(sourcePath);

    const custom = vscode.extensions.getExtension('msync.msync-language');
    assert.ok(custom);
    const contribution = custom.packageJSON.contributes.customEditors.find(editor => editor.viewType === 'msync.bytecode');
    assert.ok(contribution && contribution.priority === 'default', 'Binary types have a default read-only editor');
    assert.ok(contribution.selector.some(selector => selector.filenamePattern.endsWith('.mso')));
    assert.ok(contribution.selector.some(selector => selector.filenamePattern.endsWith('.mexe')));

    const observations = [];
    for (const [filename, stage] of [[objectPath, 'relocatable'], [executablePath, 'executable']]) {
        const uri = vscode.Uri.file(filename);
        await bounded('open default binary editor', vscode.commands.executeCommand('vscode.open', uri));
        const tab = vscode.window.tabGroups.all.flatMap(group => group.tabs)
            .find(candidate => candidate.input instanceof vscode.TabInputCustom && candidate.input.uri.toString() === uri.toString());
        assert.ok(tab && tab.input.viewType === 'msync.bytecode', 'Opening binary data selects the msync editor');
        await bounded('open typed IR as text', vscode.commands.executeCommand('msync.openArtifactText', uri));
        const editor = vscode.window.activeTextEditor;
        assert.ok(editor);
        const document = editor.document;
        assert.equal(document.uri.scheme, 'msync-artifact');
        assert.equal(document.languageId, 'msync-ir');
        const text = document.getText();
        assert.ok(text.includes('FIRST GENERATION') && text.includes('select') && text.includes('Path'));
        assert.ok(text.toLowerCase().includes(stage));
        assert.ok(!text.includes('\u0001'), 'Control bytes display escaped rather than changing editor text structure');
        const decoded = await model(filename);
        assert.equal(decoded.schemaVersion, 1);
        assert.equal(decoded.stage, stage);
        const symbols = await bounded('typed IR outline', vscode.commands.executeCommand('vscode.executeDocumentSymbolProvider', document.uri));
        assert.ok(symbols && flatten(symbols).some(symbol => symbol.name.includes('select')));
        const reference = decoded.references.find(value => Number.isInteger(value.targetLine));
        assert.ok(reference, 'Unoptimized bytecode exposes internal navigation even without source');
        const definitions = await bounded('typed IR definition', vscode.commands.executeCommand('vscode.executeDefinitionProvider', document.uri,
            new vscode.Position(reference.range.start.line, reference.range.start.character)));
        assert.ok(definitions && definitions.some(definition => {
            const target = definition.targetUri || definition.uri;
            const selection = definition.targetSelectionRange || definition.range;
            return target.toString() === document.uri.toString() && selection.start.line === reference.targetLine;
        }), JSON.stringify(definitions));
        const hoverPosition = decoded.hovers[0].range.start;
        const hovers = await bounded('typed IR hover', vscode.commands.executeCommand('vscode.executeHoverProvider', document.uri,
            new vscode.Position(hoverPosition.line, hoverPosition.character)));
        assert.ok(hovers && hovers.length && hovers.some(hover => hover.contents.length));
        try { await vscode.commands.executeCommand('type', { text: 'CORRUPT' }); }
        catch { /* The user-facing editor may reject typing with an error. */ }
        assert.equal(document.getText(), text);
        assert.equal(document.isDirty, false, 'Typing must not create an editable artifact projection');
        observations.push({ stage, symbols: flatten(symbols).length, definitions: definitions.length, hovers: hovers.length,
            uri: document.uri.toString(), language: document.languageId });
    }
    assert.equal(await digest(objectPath), initialHashes.object);
    assert.equal(await digest(executablePath), initialHashes.executable);
    assert.equal(await fs.stat(path.join(cwd, 'viewer-side-effect')).then(() => true, () => false), false);

    await vscode.commands.executeCommand('msync.openArtifactText', vscode.Uri.file(objectPath));
    const document = vscode.window.activeTextEditor.document;
    let refreshed = changing(document, text => text.includes('SECOND GENERATION'));
    try { await compile('SECOND GENERATION'); await refreshed.wait(); }
    finally { refreshed.dispose(); }
    const generationTwo = await fs.readFile(objectPath);
    const generationTwoHash = await digest(objectPath);
    refreshed = changing(document, text => /cannot|invalid|not an msync|bytecode/i.test(text) && !text.includes('SECOND GENERATION'));
    try {
        await fs.writeFile(objectPath, 'not bytecode');
        await vscode.commands.executeCommand('msync.refreshArtifact', vscode.Uri.file(objectPath));
        await refreshed.wait();
    } finally { refreshed.dispose(); }
    assert.match(document.getText(), /refresh|retry|cannot|bytecode/i);
    assert.equal((await fs.readFile(objectPath)).toString(), 'not bytecode', 'Viewing does not repair or overwrite binary files');
    refreshed = changing(document, text => text.includes('SECOND GENERATION'));
    try {
        await fs.writeFile(objectPath, generationTwo);
        await vscode.commands.executeCommand('msync.refreshArtifact', vscode.Uri.file(objectPath));
        await refreshed.wait();
    } finally { refreshed.dispose(); }
    await bounded('restart language server', vscode.commands.executeCommand('msync.restartLanguageServer'));
    await bounded('refresh after server restart', vscode.commands.executeCommand('msync.refreshArtifact', vscode.Uri.file(objectPath)));
    const afterRestart = await vscode.commands.executeCommand('vscode.executeDocumentSymbolProvider', document.uri);
    assert.ok(afterRestart && flatten(afterRestart).some(symbol => symbol.name.includes('select')));
    assert.equal(await digest(objectPath), generationTwoHash);

    const unsafePath = path.join(cwd, 'unsafe debug metadata.mso');
    const needle = Buffer.from(sourcePath);
    const prefix = Buffer.from('command:msync.test.artifactInjection#');
    assert.ok(needle.length > prefix.length);
    const replacement = Buffer.concat([prefix, Buffer.alloc(needle.length - prefix.length, 'x')]);
    const unsafe = Buffer.from(generationTwo);
    let offset = 0;
    let replaced = 0;
    while ((offset = unsafe.indexOf(needle, offset)) >= 0) {
        replacement.copy(unsafe, offset); offset += needle.length; replaced += 1;
    }
    assert.ok(replaced > 0);
    await fs.writeFile(unsafePath, unsafe);
    let injectedCommands = 0;
    const injection = vscode.commands.registerCommand('msync.test.artifactInjection', () => { injectedCommands += 1; });
    try {
        await vscode.commands.executeCommand('msync.openArtifactText', vscode.Uri.file(unsafePath));
        const unsafeDocument = vscode.window.activeTextEditor.document;
        assert.ok(unsafeDocument.getText().includes('command:msync.test.artifactInjection'));
        const unsafeModel = await model(unsafePath);
        assert.ok(!unsafeModel.references.some(reference => String(reference.sourceUri || '').startsWith('command:')));
        const unsafeOffset = unsafeDocument.getText().lastIndexOf('command:msync.test.artifactInjection');
        const links = await vscode.commands.executeCommand('vscode.executeDefinitionProvider', unsafeDocument.uri, unsafeDocument.positionAt(unsafeOffset));
        assert.ok(!links || links.every(link => (link.uri || link.targetUri).scheme !== 'command'));
        assert.equal(injectedCommands, 0);
        assert.deepEqual(await fs.readFile(unsafePath), unsafe);
    } finally { injection.dispose(); }

    const sourceSmoke = path.join(cwd, 'source still works.msync');
    await fs.writeFile(sourceSmoke, 'fn identity(file: Path) -> Path { file };\nlet value = identity(./input);\n');
    const sourceDocument = await vscode.workspace.openTextDocument(vscode.Uri.file(sourceSmoke));
    await vscode.window.showTextDocument(sourceDocument);
    assert.equal(sourceDocument.languageId, 'msync');
    const sourceSymbols = await bounded('ordinary source outline', vscode.commands.executeCommand('vscode.executeDocumentSymbolProvider', sourceDocument.uri));
    assert.ok(sourceSymbols && flatten(sourceSymbols).some(symbol => symbol.name === 'identity'));
    assert.equal(await fs.stat(path.join(cwd, 'viewer-side-effect')).then(() => true, () => false), false);
    const result = { suite: 'artifact', status: 'passed', vscodeVersion: vscode.version, observations,
        initialHashes, generationTwoHash, sourceIndependent: true, readonly: true, refresh: true, recovery: true,
        restart: true, sourceEditing: true, inertDebugMetadata: true };
    await fs.writeFile(path.join(runRoot, 'artifact-results.json'), JSON.stringify(result, null, 2));
    console.log(`MSYNC_ARTIFACT_HOST ${JSON.stringify(result)}`);
}

module.exports = { run };
