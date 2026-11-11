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
    } finally {
        clearTimeout(timer);
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
        assert.ok(character + length <= document.lineAt(line).text.length);
        pieces.push({
            line, character, length,
            text: document.getText(new vscode.Range(line, character, line, character + length)),
            type: legend.tokenTypes[data[index + 3]],
        });
    }
    return pieces;
}

async function run() {
    const workspace = process.env.MSYNC_TEST_WORKSPACE;
    const runRoot = process.env.MSYNC_TEST_ROOT;
    assert.ok(workspace && runRoot, 'Launch through the isolated native-host runner');
    const uris = [
        'ssh://forge',
        'ssh://user@forge:2222/work/tree',
        'http://example.test/a?x=1#fragment',
        'https://example.test',
        'tcp://127.0.0.1:8022',
        'tcp://[::1]:8022',
        'ssh://user@[2001:db8::1]:2222/work/tree',
        'ssh://forge/工作🌟/config',
        'ssh://',
        'tcp://[::',
    ];
    const lines = uris.map((uri, index) => `let remote${index} = ${uri};`);
    const quoted = ['"ssh://forge"', '"tcp://[::1]:8022/path"'];
    lines.push(...quoted.map((literal, index) => `let quoted${index} = ${literal};`));
    lines.push('let local_path = $local @ file;');
    const filename = path.join(workspace, 'protocol-style.msync');
    await fs.writeFile(filename, lines.join('\n') + '\n');
    const document = await vscode.workspace.openTextDocument(vscode.Uri.file(filename));
    await vscode.window.showTextDocument(document);
    assert.equal(document.languageId, 'msync');
    const extension = vscode.extensions.getExtension('msync.msync-language');
    assert.ok(extension);
    await bounded('production extension activation', extension.activate());
    const roles = extension.packageJSON.contributes.semanticTokenTypes;
    assert.ok(roles.some(role => role.id === 'protocol' && role.superType === 'type'));
    assert.ok(roles.some(role => role.id === 'endpoint' && role.superType === 'string'));
    const fallback = extension.packageJSON.contributes.semanticTokenScopes.find(value => value.language === 'msync').scopes;
    assert.deepEqual(fallback.protocol, ['support.type.protocol.msync']);
    assert.deepEqual(fallback.endpoint, ['string.unquoted.endpoint.msync']);
    const legend = await bounded('protocol semantic legend', vscode.commands.executeCommand('vscode.provideDocumentSemanticTokensLegend', document.uri));
    const tokens = await bounded('protocol semantic tokens', vscode.commands.executeCommand('vscode.provideDocumentSemanticTokens', document.uri));
    assert.ok(legend && tokens && tokens.data.length);
    const pieces = semanticPieces(document, legend, tokens.data);
    for (const [line, uri] of uris.entries()) {
        const separator = uri.indexOf('://');
        const start = lines[line].indexOf(uri);
        const expected = [
            { character: start, text: uri.slice(0, separator), type: 'protocol' },
            { character: start + separator, text: '://', type: 'operator' },
        ];
        const address = uri.slice(separator + 3);
        if (address) expected.push({ character: start + separator + 3, text: address, type: 'endpoint' });
        for (const part of expected) {
            assert.ok(pieces.some(piece => piece.line === line && piece.character === part.character
                && piece.length === part.text.length && piece.text === part.text && piece.type === part.type),
            `Missing native semantic URI part ${JSON.stringify({ line, ...part })}; got ${JSON.stringify(pieces.filter(piece => piece.line === line))}`);
        }
        assert.equal(pieces.filter(piece => piece.line === line && piece.type === 'endpoint').length, address ? 1 : 0,
            'The address retains its user, IPv6 host, port and path as one endpoint region');
    }
    for (const [index, literal] of quoted.entries()) {
        const line = uris.length + index;
        assert.ok(pieces.some(piece => piece.line === line && piece.text === literal && piece.type === 'string'));
        assert.ok(!pieces.some(piece => piece.line === line && ['protocol', 'endpoint'].includes(piece.type)));
    }
    assert.ok(pieces.some(piece => piece.line === uris.length + quoted.length && piece.text === 'local' && piece.type === 'variable'));
    const result = { suite: 'protocol', status: 'passed', vscodeVersion: vscode.version, uris,
        legend: legend.tokenTypes, pieces, fallback };
    await fs.writeFile(path.join(runRoot, 'protocol-results.json'), JSON.stringify(result, null, 2));
    console.log(`MSYNC_PROTOCOL_HOST ${JSON.stringify({ status: result.status, uris: uris.length, tokens: pieces.length, vscodeVersion: vscode.version })}`);
}

module.exports = { run };
