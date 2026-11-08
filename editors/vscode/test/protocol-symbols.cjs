'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const Module = require('node:module');
const protocol = require('vscode-languageserver-protocol/node');

class HostValue {
    constructor(...args) { this.args = args; }
}
class Range {
    constructor(startLine, startCharacter, endLine, endCharacter) {
        this.start = { line: startLine, character: startCharacter };
        this.end = { line: endLine, character: endCharacter };
    }
}
class Location {
    constructor(uri, range) { Object.assign(this, { uri, range }); }
}
class DocumentSymbol {
    constructor(name, detail, kind, range, selectionRange) {
        Object.assign(this, { name, detail, kind, range, selectionRange });
    }
}

// Only VS Code's value constructors are supplied; classification and conversion
// execute the same installed client implementation used by the production bundle.
const host = Object.fromEntries([
    'CallHierarchyItem', 'CodeAction', 'CodeLens', 'CompletionItem', 'Diagnostic',
    'DocumentLink', 'InlayHint', 'SymbolInformation', 'TypeHierarchyItem',
].map(name => [name, HostValue]));
Object.assign(host, {
    Range, DocumentSymbol, Location,
    CodeActionKind: Object.fromEntries(["Empty", "QuickFix", "Refactor", "RefactorExtract", "RefactorInline", "RefactorRewrite", "Source", "SourceOrganizeImports", "SourceFixAll", "Notebook"].map(name => [name, { value: name }])),
    Uri: { parse: value => value },
    SymbolTag: { Deprecated: 1 },
    SymbolKind: { Property: 6 },
});
const originalLoad = Module._load;
Module._load = function(request, parent, main) {
    if (request === 'vscode') return host;
    return originalLoad.call(this, request, parent, main);
};
let converter;
try {
    converter = require(path.join(__dirname, '../node_modules/vscode-languageclient/lib/common/protocolConverter.js')).createConverter();
} finally {
    Module._load = originalLoad;
}

async function main() {
    const arrays = JSON.parse(fs.readFileSync(0, 'utf8'));
    const counts = [];
    for (const values of arrays) {
        assert.ok(Array.isArray(values));
        if (values.length === 0) { counts.push(0); continue; }
        // This is the production documentSymbol feature's actual union dispatch.
        const documentSymbols = protocol.DocumentSymbol.is(values[0]);
        const converted = documentSymbols
            ? await converter.asDocumentSymbols(values)
            : await converter.asSymbolInformations(values);
        assert.equal(converted.length, values.length);
        for (const value of values) {
            assert.ok(documentSymbols ? protocol.DocumentSymbol.is(value) : protocol.Location.is(value.location), value);
            assert.notEqual(value.detail, null);
        }
        counts.push(converted.length);
    }
    console.log(JSON.stringify({ arrays: counts.length, symbolCounts: counts }));
}
main().catch(error => { console.error(error.stack); process.exitCode = 1; });
