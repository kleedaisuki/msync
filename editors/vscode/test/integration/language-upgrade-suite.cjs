'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const vscode = require('vscode');

async function bounded(description, promise) {
    let timer;
    try {
        return await Promise.race([promise, new Promise((_, reject) => {
            timer = setTimeout(() => reject(new Error(`Timed out waiting for ${description}`)), 30_000);
        })]);
    } finally { clearTimeout(timer); }
}

function hoverText(values) {
    return (values || []).flatMap(value => value.contents.map(content => typeof content === 'string' ? content : content.value)).join('\n');
}

async function semanticRoles(document) {
    const legend = await bounded('semantic legend', vscode.commands.executeCommand('vscode.provideDocumentSemanticTokensLegend', document.uri));
    const semantic = await bounded('semantic tokens', vscode.commands.executeCommand('vscode.provideDocumentSemanticTokens', document.uri));
    assert.ok(legend && semantic);
    const roles = new Map();
    let line = 0, character = 0;
    for (let index = 0; index < semantic.data.length; index += 5) {
        line += semantic.data[index];
        character = semantic.data[index] === 0 ? character + semantic.data[index + 1] : semantic.data[index + 1];
        const start = new vscode.Position(line, character);
        const end = new vscode.Position(line, character + semantic.data[index + 2]);
        roles.set(document.offsetAt(start), {text: document.getText(new vscode.Range(start, end)), type: legend.tokenTypes[semantic.data[index + 3]]});
    }
    return roles;
}

async function run() {
    const workspace = process.env.MSYNC_TEST_WORKSPACE;
    const runRoot = process.env.MSYNC_TEST_ROOT;
    assert.ok(workspace && runRoot, 'Launch through the isolated native-host runner');
    const source = [
        'concept Named { name: String, };',
        'concept Labeled { Named, };',
        'fn label(item: Labeled) -> String { item::name };',
        'scope Catalog { title: "catalog", };',
        'let instance = scope { name: "Klee", };',
        'let result = label(instance);',
        'let numbers = [1.5, .5, 1e3, 0xFF, 0b10_01, 0o77, 1_000, -0];',
        'let text = 1.5e2 as String;',
        'let power = 2**3**2;',
        'let local_path = ./input @ $local;',
        'let absolute = $std::math::abs(-0);',
        'let decision = true and true and true and true and true and true and true and true and true and true and true and true;',
        'let untouched = "literal \\target and doubled ""quotes""";',
        '',
    ].join('\n');
    const filename = path.join(workspace, 'language upgrade 工作.msync');
    await fs.writeFile(filename, source);
    const document = await vscode.workspace.openTextDocument(vscode.Uri.file(filename));
    await vscode.window.showTextDocument(document);
    const extension = vscode.extensions.getExtension('msync.msync-language');
    assert.ok(extension);
    await bounded('extension activation', extension.activate());
    const position = (spelling, delta = 1) => {
        const offset = source.indexOf(spelling);
        assert.ok(offset >= 0, spelling);
        return document.positionAt(offset + delta);
    };
    const hovers = [];
    for (const [spelling, expected] of [['Labeled)', /Labeled/], ['text =', /String/], ['local_path =', /Path/], ['numbers =', /Range/], ['Catalog {', /Scope/]]) {
        const text = hoverText(await bounded(`hover ${spelling}`, vscode.commands.executeCommand('vscode.executeHoverProvider', document.uri, position(spelling))));
        assert.match(text, expected, spelling);
        hovers.push({spelling, text});
    }
    const definitions = await bounded('concept definition', vscode.commands.executeCommand('vscode.executeDefinitionProvider', document.uri, position('Labeled)')));
    assert.ok(definitions && definitions.length, 'Concept constraint navigates to its declaration');
    const target = definitions[0];
    assert.equal((target.targetUri || target.uri).toString(), document.uri.toString());
    const selection = target.targetSelectionRange || target.range;
    assert.equal(document.getText(selection), 'Labeled');
    const symbols = await bounded('document outline', vscode.commands.executeCommand('vscode.executeDocumentSymbolProvider', document.uri));
    assert.ok(symbols.some(symbol => symbol.name === 'Named'));
    assert.ok(symbols.some(symbol => symbol.name === 'Labeled'));
    const completion = await bounded('concept constraint completion', vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, position('Labeled)', 0)));
    const conceptLabels = (completion?.items || []).map(item => typeof item.label === 'string' ? item.label : item.label.label);
    assert.ok(conceptLabels.includes('Named') && conceptLabels.includes('Labeled'), JSON.stringify(conceptLabels));

    const expected = [
        ['concept Named', 'keyword'], ['Named {', 'type'], ['Labeled)', 'type'],
        ['fn label', 'keyword'], ['label(item', 'function'], ['scope Catalog', 'keyword'],
        ['Catalog {', 'type'], ['instance =', 'variable'], ['scope { name', 'keyword'],
        ['1.5,', 'number'], ['.5,', 'number'], ['1e3,', 'number'], ['0xFF,', 'number'],
        ['0b10_01,', 'number'], ['0o77,', 'number'], ['1_000,', 'number'],
        ['**3**2', 'operator'], ['as String', 'controlKeyword'], ['@ $local', 'operator'],
        ['std::math', 'type'], ['math::abs', 'type'], ['abs(-0)', 'function'],
    ];
    const themes = [];
    for (const theme of ['Default Dark Modern', 'Default Light Modern']) {
        await vscode.workspace.getConfiguration('workbench').update('colorTheme', theme, vscode.ConfigurationTarget.Global);
        const roles = await semanticRoles(document);
        for (const [spelling, role] of expected) {
            const actual = roles.get(source.indexOf(spelling));
            assert.equal(actual?.type, role, `${theme}: ${spelling}: ${JSON.stringify(actual)}`);
        }
        themes.push({theme, roles: expected.map(([spelling]) => ({spelling, ...roles.get(source.indexOf(spelling))}))});
    }
    const errors = vscode.languages.getDiagnostics(document.uri).filter(value => value.severity === vscode.DiagnosticSeverity.Error);
    assert.deepEqual(errors.map(value => value.message), []);
    const applyFormat = async () => {
        const edits = await bounded('formatting', vscode.commands.executeCommand('vscode.executeFormatDocumentProvider', document.uri, {insertSpaces: true, tabSize: 4}));
        assert.ok(edits);
        const update = new vscode.WorkspaceEdit();
        update.set(document.uri, edits);
        assert.ok(await vscode.workspace.applyEdit(update));
    };
    await applyFormat();
    const formatted = document.getText();
    assert.ok(formatted.split(/\r?\n/).every(line => line.length <= 80));
    assert.ok(formatted.includes('"literal \\target and doubled ""quotes"""'));
    assert.match(formatted, /\n\s+and true/);
    await applyFormat();
    assert.equal(document.getText(), formatted, 'Formatting is idempotent');
    const report = {status: 'passed', suite: 'language-upgrade', vscodeVersion: vscode.version, hovers,
        definitions: definitions.length, conceptLabels, themes, maximumWidth: Math.max(...formatted.split(/\r?\n/).map(line => line.length)), formatted};
    await fs.writeFile(path.join(runRoot, 'language-upgrade-results.json'), JSON.stringify(report, null, 2));
    console.log(`MSYNC_LANGUAGE_UPGRADE_HOST ${JSON.stringify({status: report.status, vscodeVersion: vscode.version, themes: themes.length, maximumWidth: report.maximumWidth})}`);
}

module.exports = {run};
