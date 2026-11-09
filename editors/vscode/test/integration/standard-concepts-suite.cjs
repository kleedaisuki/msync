'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const {execFile} = require('node:child_process');
const {promisify} = require('node:util');
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
    const server = process.env.MSYNC_TEST_SERVER_PATH;
    assert.ok(workspace && runRoot && server, 'Launch through the isolated frozen-server host runner');
    const initial = [
        'concept PairShape {',
        '    first?: typename First,',
        '    second?: typename Second,',
        '    map_first: fn(value: First) -> First,',
        '};',
        'fn preserve(value: $std::concepts::Option?) -> $std::concepts::Option? { value };',
        'scope SameNumber {',
        '    Self: Number,',
        '    equal: fn(a: Number, b: Number) -> Bool { a == b },',
        '};',
        'let selected? = preserve($std::data::Option::some("Klee"));',
        '$std::data::Option::get_or(selected, "none") -> ./option.txt;',
        'let numbers = $std::containers::Set::with_eq([3, 1], SameNumber);',
        '$std::containers::Set::insert(numbers, 1);',
        'let members = $std::containers::Set::snapshot(numbers);',
        '$std::string::join($std::ranges::map(members, fn(value: Number) { value as String }), ",") -> ./set.txt;',
        'let tree = $std::containers::RBTreeSet::new([3, 1, 2]);',
        '$std::containers::RBTreeSet::insert(tree, 4);',
        '$std::containers::RBTreeSet::remove(tree, 2);',
        'let ordered = $std::containers::RBTreeSet::snapshot(tree);',
        '$std::string::join($std::ranges::map(ordered, fn(value: Number) { value as String }), ",") -> ./ordered.txt;',
        '',
    ].join('\n');
    const filename = path.join(workspace, 'standard concepts 工作.msync');
    await fs.writeFile(filename, initial);
    const document = await vscode.workspace.openTextDocument(vscode.Uri.file(filename));
    await vscode.window.showTextDocument(document);
    const extension = vscode.extensions.getExtension('msync.msync-language');
    assert.ok(extension);
    await bounded('extension activation', extension.activate());

    const incomplete = initial.indexOf('with_eq(');
    const completion = await bounded('snake_case policy method completion', vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, document.positionAt(incomplete + 'with_eq'.length)));
    const candidates = completion?.items || [];
    const choice = candidates.find(item => (typeof item.label === 'string' ? item.label : item.label.label) === 'with_equality');
    assert.ok(choice, JSON.stringify(candidates.map(item => item.label)));
    const range = choice.textEdit?.range || choice.range?.replacing || choice.range;
    const newText = choice.textEdit?.newText || choice.insertText;
    assert.ok(range instanceof vscode.Range, 'Completion must own its exact source range');
    assert.equal(document.getText(range), 'with_eq');
    assert.equal(newText, 'with_equality');
    const update = new vscode.WorkspaceEdit();
    update.replace(document.uri, range, newText);
    assert.ok(await vscode.workspace.applyEdit(update));
    assert.ok(await document.save());
    const source = document.getText();
    assert.ok(!source.includes('with_with') && !source.includes('Set::Set::') && !source.includes('$$'));
    const position = (spelling, delta = 1) => {
        const offset = source.indexOf(spelling);
        assert.ok(offset >= 0, spelling);
        return document.positionAt(offset + delta);
    };

    const hovers = [];
    for (const [spelling, expected] of [
        ['Option?)', /Option/],
        ['with_equality(', /Equality/],
        ['Set::with_equality', /Scope/],
        ['numbers =', /Scope/],
        ['tree =', /Scope/],
        ['typename First', /First/],
    ]) {
        const text = hoverText(await bounded(`hover ${spelling}`, vscode.commands.executeCommand('vscode.executeHoverProvider', document.uri,
            position(spelling, spelling.startsWith('typename ') ? 'typename '.length + 1 : 1))));
        assert.match(text, expected, `${spelling}: ${text}`);
        hovers.push({spelling, text});
    }
    const references = [
        ['First) ->', document.uri, 'First'],
        ['Option?)', null, 'Option'],
        ['with_equality(', null, 'with_equality'],
    ];
    const definitions = [];
    for (const [spelling, expectedUri, name] of references) {
        const locations = await bounded(`definition ${spelling}`, vscode.commands.executeCommand('vscode.executeDefinitionProvider', document.uri, position(spelling)));
        assert.ok(locations && locations.length, spelling);
        const target = locations[0];
        const uri = target.targetUri || target.uri;
        const selected = target.targetSelectionRange || target.range;
        if (expectedUri) assert.equal(uri.toString(), expectedUri.toString());
        else assert.equal(uri.scheme, 'msync-stdlib');
        const virtual = await bounded(`open definition ${name}`, vscode.workspace.openTextDocument(uri));
        assert.equal(virtual.languageId, 'msync');
        assert.equal(virtual.getText(selected), name);
        if (name === 'First') assert.equal(selected.start.line, 1);
        if (name === 'Option') assert.match(virtual.getText(), /value\?:\s*typename Item/);
        definitions.push({name, uri: uri.toString(), selected: virtual.getText(selected), sourceBytes: Buffer.byteLength(virtual.getText(), 'utf8')});
    }

    const typeCompletion = await bounded('qualified concept completion', vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, position('Option?)', 3)));
    const conceptLabels = (typeCompletion?.items || []).map(item => typeof item.label === 'string' ? item.label : item.label.label);
    assert.ok(conceptLabels.includes('Option'), JSON.stringify(conceptLabels));
    const expected = [
        ['concept PairShape', 'keyword'], ['PairShape {', 'type'],
        ['typename First', 'keyword'], ['First,', 'type'], ['Second,', 'type'],
        ['First) ->', 'type'], ['fn preserve', 'keyword'], ['preserve(value', 'function'],
        ['Option?)', 'type'], ['scope SameNumber', 'keyword'], ['SameNumber {', 'type'],
        ['numbers =', 'variable'], ['data::Option', 'type'], ['Option::some', 'type'],
        ['Set::with_equality', 'type'], ['with_equality(', 'function'],
        ['RBTreeSet::new', 'type'],
    ];
    const themes = [];
    for (const theme of ['Default Dark Modern', 'Default Light Modern']) {
        await vscode.workspace.getConfiguration('workbench').update('colorTheme', theme, vscode.ConfigurationTarget.Global);
        const roles = await semanticRoles(document);
        const observed = [];
        for (const [spelling, role] of expected) {
            const actual = roles.get(source.indexOf(spelling));
            assert.equal(actual?.type, role, `${theme}: ${spelling}: ${JSON.stringify(actual)}`);
            observed.push({spelling, ...actual});
        }
        themes.push({theme, roles: observed});
    }
    const errors = vscode.languages.getDiagnostics(document.uri).filter(value => value.severity === vscode.DiagnosticSeverity.Error);
    assert.deepEqual(errors.map(value => value.message), []);
    await execute(server, ['--no-config', 'check', filename], {cwd: workspace, encoding: 'utf8', timeout: 30_000});
    await execute(server, ['--no-config', 'run', filename], {cwd: workspace, encoding: 'utf8', timeout: 30_000});
    const output = {option: await fs.readFile(path.join(workspace, 'option.txt'), 'utf8'),
        set: await fs.readFile(path.join(workspace, 'set.txt'), 'utf8'), ordered: await fs.readFile(path.join(workspace, 'ordered.txt'), 'utf8')};
    assert.deepEqual(output, {option: 'Klee', set: '3,1', ordered: '1,3,4'});
    const report = {status: 'passed', suite: 'standard-concepts', vscodeVersion: vscode.version,
        completion: {inserted: newText, replaced: 'with_eq'}, hovers, definitions, conceptLabels, themes, output};
    await fs.writeFile(path.join(runRoot, 'standard-concepts-results.json'), JSON.stringify(report, null, 2));
    console.log(`MSYNC_STANDARD_CONCEPTS_HOST ${JSON.stringify({status: report.status, vscodeVersion: vscode.version, themes: themes.length, embeddedDefinitions: definitions.length - 1, output})}`);
}

module.exports = {run};
