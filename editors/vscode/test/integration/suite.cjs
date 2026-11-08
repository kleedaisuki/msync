'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const vscode = require('vscode');

const TIMEOUT = 30_000;

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
    assert.equal(await fs.readFile(path.join(workspace, 'preserved.txt'), 'utf8'), 'keep me\n');
    await assert.rejects(fs.access(path.join(workspace, 'never-created.txt')), { code: 'ENOENT' });
}

async function run() {
    const workspace = process.env.MSYNC_TEST_WORKSPACE;
    const runRoot = process.env.MSYNC_TEST_ROOT;
    assert.ok(workspace && runRoot, 'Launch through test/integration/run.cjs');
    const phases = [];
    const record = async (phase, detail = {}) => {
        phases.push({ phase, ...detail });
        console.log(`MSYNC_HOST_GATE ${JSON.stringify(phases.at(-1))}`);
        await fs.writeFile(path.join(runRoot, 'host-results.json'), JSON.stringify({ vscodeVersion: vscode.version, phases }, null, 2));
    };
    let document;
    try {
        const quotedOutput = '"' + path.join(workspace, 'never-created.txt').replaceAll('"', '""') + '"';
        const source = [
            'let label="可莉🌟";',
            'fn identity(item) { item };',
            'let answer = identity(true);',
            'let file? = if true { ~/.codex } else { null };',
            'let invalid = file;',
            `"must never execute" -> $local::${quotedOutput};`,
            '',
        ].join('\n');
        const filename = path.join(workspace, 'journey.msync');
        await fs.writeFile(filename, source);
        document = await vscode.workspace.openTextDocument(vscode.Uri.file(filename));
        await vscode.window.showTextDocument(document);
        assert.equal(document.languageId, 'msync');
        const extension = vscode.extensions.getExtension('msync.msync-language');
        assert.ok(extension);
        assert.equal(extension.packageJSON.main, './out/extension.cjs');
        await extension.activate();
        await record('production-extension-active', { extension: extension.id });

        const errors = await until('nullable binding diagnostic', () => vscode.languages.getDiagnostics(document.uri),
            values => values.some(value => value.severity === vscode.DiagnosticSeverity.Error && /null|nullable|\?/i.test(value.message)));
        await record('nullable-diagnostic', { messages: errors.map(value => value.message) });
        await effectsAreAbsent(workspace);

        const invalid = document.getText().indexOf('let invalid = file;');
        const fix = new vscode.WorkspaceEdit();
        fix.replace(document.uri, new vscode.Range(document.positionAt(invalid), document.positionAt(invalid + 'let invalid = file;'.length)), 'let valid? = file;');
        assert.ok(await vscode.workspace.applyEdit(fix));
        await until('diagnostic clearing after edit', () => vscode.languages.getDiagnostics(document.uri), values => values.length === 0);
        await record('incremental-edit-clears-diagnostic', { version: document.version });

        const format = await vscode.commands.executeCommand('vscode.executeFormatDocumentProvider', document.uri, { insertSpaces: true, tabSize: 4 });
        assert.ok(format && format.length);
        const edit = new vscode.WorkspaceEdit();
        edit.set(document.uri, format);
        assert.ok(await vscode.workspace.applyEdit(edit));
        assert.ok(document.getText().includes('"可莉🌟"'));
        assert.ok(document.getText().includes('fn identity'));
        await record('brace-document-formatting', { edits: format.length });

        const current = document.getText();
        const use = document.positionAt(current.lastIndexOf('identity(true)') + 2);
        const definitions = await vscode.commands.executeCommand('vscode.executeDefinitionProvider', document.uri, use);
        const declaration = document.positionAt(current.indexOf('identity(item)'));
        assert.ok(definitions && definitions.some(target => {
            const uri = target.targetUri || target.uri;
            const range = target.targetSelectionRange || target.range;
            return uri.toString() === document.uri.toString() && range.start.isEqual(declaration);
        }));
        await record('generic-function-definition', { name: 'identity' });

        const completion = await vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, use);
        assert.ok(completion && completion.items.some(item => (typeof item.label === 'string' ? item.label : item.label.label) === 'identity'));
        const hover = await vscode.commands.executeCommand('vscode.executeHoverProvider', document.uri, use);
        assert.ok(hover && hover.length);
        const symbols = await vscode.commands.executeCommand('vscode.executeDocumentSymbolProvider', document.uri);
        assert.ok(symbols && symbols.some(symbol => symbol.name === 'identity'));
        await record('symbol-completion-hover');

        const legend = await vscode.commands.executeCommand('vscode.provideDocumentSemanticTokensLegend', document.uri);
        const semantic = await vscode.commands.executeCommand('vscode.provideDocumentSemanticTokens', document.uri);
        assert.ok(legend && semantic && semantic.data.length);
        let line = 0;
        let character = 0;
        for (let index = 0; index < semantic.data.length; index += 5) {
            line += semantic.data[index];
            character = semantic.data[index] === 0 ? character + semantic.data[index + 1] : semantic.data[index + 1];
            assert.ok(semantic.data[index + 2] > 0);
            assert.ok(character + semantic.data[index + 2] <= document.lineAt(line).text.length);
            assert.ok(legend.tokenTypes[semantic.data[index + 3]]);
        }
        await record('semantic-utf16-ranges', { count: semantic.data.length / 5 });
        await effectsAreAbsent(workspace);

        await vscode.commands.executeCommand('msync.restartLanguageServer');
        await until('completion after server restart', () => vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, use),
            value => value && value.items.some(item => (typeof item.label === 'string' ? item.label : item.label.label) === 'identity'));
        await effectsAreAbsent(workspace);
        await record('restart-and-no-runtime-effects');

        const polishSource = [
            'scope MyClass {',
            '    new: fn() { scope { x: ~/.codex } },',
            '    get: fn(some) { some::x }',
            '};',
            'let my_instance = MyClass::new();',
            'let construct = MyClass::new;',
            'let ClassAlias = MyClass;',
            'MyClass::get(my_instance) -> ./;',
            'let c = $std::bool::both(MyClass::get(my_instance) == ~/.codex, MyClass::get(my_instance) != ./);',
            '',
        ].join('\n');
        const polishFile = path.join(workspace, 'typed-scope.msync');
        await fs.writeFile(polishFile, polishSource);
        document = await vscode.workspace.openTextDocument(vscode.Uri.file(polishFile));
        await vscode.window.showTextDocument(document);
        const flattenHover = items => items.flatMap(item => item.contents.map(content => typeof content === 'string' ? content : content.value)).join('\n');
        const scopePosition = document.positionAt(polishSource.indexOf('MyClass'));
        const scopeHover = await until('inferred Scope hover with members', async () =>
            flattenHover(await vscode.commands.executeCommand('vscode.executeHoverProvider', document.uri, scopePosition) || []),
        value => /Scope/.test(value) && /new/.test(value) && /get/.test(value));
        const getPosition = document.positionAt(polishSource.lastIndexOf('get(my_instance)') + 1);
        const functionHover = flattenHover(await vscode.commands.executeCommand('vscode.executeHoverProvider', document.uri, getPosition) || []);
        assert.match(functionHover, /fn.*get\(/);
        assert.doesNotMatch(functionHover, /msync 0\.8 typed expression/);
        await record('inferred-scope-and-function-hover', { scopeHover, functionHover });

        const expected = [
            ['MyClass', 'type'], ['new:', 'function'], ['get:', 'function'],
            ['my_instance', 'type'], ['construct', 'function'], ['ClassAlias', 'type'],
        ];
        const actualRoles = await until('inferred semantic roles on scope/function aliases', async () => {
            const roles = new Map();
            const legend = await vscode.commands.executeCommand('vscode.provideDocumentSemanticTokensLegend', document.uri);
            const semantic = await vscode.commands.executeCommand('vscode.provideDocumentSemanticTokens', document.uri);
            if (!legend || !semantic) return roles;
            let line = 0;
            let character = 0;
            for (let index = 0; index < semantic.data.length; index += 5) {
                line += semantic.data[index];
                character = semantic.data[index] === 0 ? character + semantic.data[index + 1] : semantic.data[index + 1];
                roles.set(document.offsetAt(new vscode.Position(line, character)), legend.tokenTypes[semantic.data[index + 3]]);
            }
            return roles;
        }, roles => expected.every(([text, type]) => roles.get(polishSource.indexOf(text)) === type));
        await record('inferred-semantic-roles', { roles: expected.map(([text]) => ({ text, type: actualRoles.get(polishSource.indexOf(text)) })) });
        assert.equal(vscode.languages.getDiagnostics(document.uri).length, 0);
        const polishEdits = await vscode.commands.executeCommand('vscode.executeFormatDocumentProvider', document.uri, { insertSpaces: true, tabSize: 4 });
        const polishFormat = new vscode.WorkspaceEdit();
        polishFormat.set(document.uri, polishEdits);
        assert.ok(await vscode.workspace.applyEdit(polishFormat));
        assert.ok(document.getText().split(/\r?\n/).every(line => line.length <= 80));
        await record('eighty-column-formatting', { maximumWidth: Math.max(...document.getText().split(/\r?\n/).map(line => line.length)) });

        const instanceSource = document.getText();
        const instanceStart = instanceSource.indexOf('MyClass::new()');
        const invalidInstance = new vscode.WorkspaceEdit();
        invalidInstance.insert(document.uri, document.positionAt(instanceStart + 'MyClass::new()'.length), '::x');
        assert.ok(await vscode.workspace.applyEdit(invalidInstance));
        const typedErrors = await until('contextual member diagnostic', () => vscode.languages.getDiagnostics(document.uri),
            values => values.some(value => /Path/.test(value.message)));
        const memberError = typedErrors.find(value => /Path/.test(value.message));
        assert.doesNotMatch(memberError.message, /file:\/\//);
        assert.match(memberError.message, /Scope/);
        assert.match(memberError.message, /[Hh]elp/);
        assert.ok(memberError.relatedInformation && memberError.relatedInformation.length);
        await record('contextual-member-diagnostic', { message: memberError.message, related: memberError.relatedInformation.map(item => item.message) });
        await effectsAreAbsent(workspace);

    } catch (error) {
        await fs.writeFile(path.join(runRoot, 'failure.json'), JSON.stringify({
            error: error instanceof Error ? error.stack : String(error),
            document: document && { uri: document.uri.toString(), text: document.getText() },
            diagnostics: document && vscode.languages.getDiagnostics(document.uri).map(value => ({ message: value.message, range: value.range })),
        }, null, 2));
        throw error;
    }
}

module.exports = { run };
