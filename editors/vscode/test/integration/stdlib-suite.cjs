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
    return values.flatMap(value => value.contents.map(content => typeof content === 'string' ? content : content.value)).join('\n');
}

async function run() {
    const workspace = process.env.MSYNC_TEST_WORKSPACE;
    const runRoot = process.env.MSYNC_TEST_ROOT;
    assert.ok(workspace && runRoot, 'Launch through the isolated native-host runner');
    const source = [
        'let file = "./input" as Path;',
        'let rows = $std::ranges::zip([file], [1]);',
        'for row in rows { let selected = row::first; };',
        'let text = $std::string::join(["a", "b"], "|");',
        'let normalize = $std::functional::compose($std::string::to_uppercase, $std::string::trim);',
        'let normalized = normalize(" hello 世界🙂 ");',
        '',
    ].join('\n');
    const filename = path.join(workspace, 'library navigation 体验.msync');
    await fs.writeFile(filename, source);
    const document = await vscode.workspace.openTextDocument(vscode.Uri.file(filename));
    await vscode.window.showTextDocument(document);
    assert.equal(document.languageId, 'msync');
    const extension = vscode.extensions.getExtension('msync.msync-language');
    assert.ok(extension);
    await bounded('production extension activation', extension.activate());
    const position = spelling => {
        const offset = source.indexOf(spelling);
        assert.ok(offset >= 0, spelling);
        return document.positionAt(offset + 1);
    };
    const hovers = [];
    for (const [spelling, type] of [['file =', 'Path'], ['selected =', 'Path'], ['text =', 'String'], ['normalized =', 'String']]) {
        const result = await bounded(`hover ${spelling}`, vscode.commands.executeCommand('vscode.executeHoverProvider', document.uri, position(spelling)));
        const text = hoverText(result || []);
        assert.ok(text.includes(type), `${spelling}: ${text}`);
        hovers.push({ spelling, type, text });
    }
    const definitions = [];
    for (const [spelling, module] of [['zip(', 'ranges'], ['join(', 'string'], ['compose(', 'functional']]) {
        const result = await bounded(`definition ${spelling}`, vscode.commands.executeCommand('vscode.executeDefinitionProvider', document.uri, position(spelling)));
        assert.ok(result && result.length, spelling);
        const target = result[0];
        const uri = target.targetUri || target.uri;
        const range = target.targetSelectionRange || target.range;
        assert.equal(uri.scheme, 'msync-stdlib');
        assert.ok(uri.path.endsWith(`/0.8.0/${module}.msync`), uri.toString());
        const virtual = await bounded(`open embedded ${module}`, vscode.workspace.openTextDocument(uri));
        assert.equal(virtual.languageId, 'msync');
        assert.ok(virtual.getText(range).includes(spelling.slice(0, -1)), virtual.getText(range));
        await vscode.window.showTextDocument(virtual);
        definitions.push({ module, uri: uri.toString(), selected: virtual.getText(range), bytes: Buffer.byteLength(virtual.getText(), 'utf8') });
    }
    await vscode.window.showTextDocument(document);
    const completion = await bounded('structured Range member completion', vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, position('first;')));
    const labels = (completion?.items || []).map(item => typeof item.label === 'string' ? item.label : item.label.label);
    assert.ok(labels.includes('first') && labels.includes('second'), JSON.stringify(labels));
    const errors = vscode.languages.getDiagnostics(document.uri).filter(value => value.severity === vscode.DiagnosticSeverity.Error);
    assert.deepEqual(errors.map(value => value.message), []);
    const report = { status: 'passed', suite: 'stdlib', vscodeVersion: vscode.version, hovers, definitions, memberCompletion: labels };
    await fs.writeFile(path.join(runRoot, 'stdlib-results.json'), JSON.stringify(report, null, 2));
    console.log(`MSYNC_STDLIB_HOST ${JSON.stringify({ status: report.status, hovers: hovers.length, embeddedSources: definitions.length, vscodeVersion: vscode.version })}`);
}

module.exports = { run };
