'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { test, before } = require('node:test');
const textmate = require('vscode-textmate');
const oniguruma = require('vscode-oniguruma');

let grammar;
before(async () => {
    const wasm = fs.readFileSync(require.resolve('vscode-oniguruma/release/onig.wasm'));
    await oniguruma.loadWASM(wasm.buffer.slice(wasm.byteOffset, wasm.byteOffset + wasm.byteLength));
    const registry = new textmate.Registry({
        onigLib: Promise.resolve({
            createOnigScanner: patterns => new oniguruma.OnigScanner(patterns),
            createOnigString: value => new oniguruma.OnigString(value),
        }),
        loadGrammar: async scope => scope === 'source.msync'
            ? JSON.parse(fs.readFileSync(path.join(__dirname, '../syntaxes/msync.tmLanguage.json'), 'utf8'))
            : null,
    });
    grammar = await registry.loadGrammar('source.msync');
    assert.ok(grammar);
});

function scopes(line, word) {
    const start = line.indexOf(word);
    assert.notEqual(start, -1, word);
    const token = grammar.tokenizeLine(line, textmate.INITIAL).tokens
        .find(item => item.startIndex <= start && item.endIndex > start);
    assert.ok(token, `${word}: ${line}`);
    return token.scopes;
}

function hasScope(line, word, expected) {
    const actual = scopes(line, word);
    assert.ok(actual.includes(expected), `${word} in ${line}: expected ${expected}, got ${actual}`);
}

test('named, anonymous and native functions retain callable and parameter roles', () => {
    for (const line of ['fn copy(file: Path | String, des) {', 'item: fn copy(file: Path | String, des) {', 'native fn copy(file: Path | String, des) -> Bool;']) {
        hasScope(line, 'fn', 'storage.type.function.msync');
        hasScope(line, 'copy', 'entity.name.function.msync');
        hasScope(line, 'file', 'variable.parameter.msync');
        hasScope(line, 'Path', 'support.type.msync');
        hasScope(line, 'String', 'support.type.msync');
    }
    hasScope('let callback = fn (file) { file };', 'fn', 'storage.type.function.msync');
    hasScope('let callback = fn (file) { file };', 'file', 'variable.parameter.msync');
    hasScope('item: fn SomeOperation { true },', 'SomeOperation', 'entity.name.function.msync');
    hasScope('let result = copy(input, output);', 'copy', 'entity.name.function.msync');
    hasScope('native fn exists(path: Path) -> Bool;', 'native', 'keyword.control.msync');
});

test('let bindings, nullable tests and types have independent scopes', () => {
    hasScope('let file? = null;', 'let', 'storage.type.variable.msync');
    hasScope('let file? = null;', 'file', 'variable.other.readwrite.msync');
    hasScope('let file? = null;', '?', 'keyword.operator.nullable.msync');
    hasScope('let file? = null;', 'null', 'constant.language.null.msync');
    for (const type of ['Range', 'String', 'Function', 'Scope', 'Path', 'Process', 'Connection', 'Null', 'Bool']) {
        hasScope(`let value: ${type} = input;`, type, 'support.type.msync');
    }
    hasScope('if file is not null { file -> target };', 'is', 'keyword.operator.logical.msync');
    hasScope('if file? {} else { file -> target };', '?', 'keyword.operator.msync');
    hasScope('let kind = decltype(file);', 'decltype', 'keyword.control.msync');
    for (const word of ['async', 'await', 'delete', 'macro', 'symbol', 'boolean']) {
        assert.ok(!scopes(`${word} item;`, word).some(scope => /keyword|storage.type/.test(scope)));
    }
});

test('blocks and match arms remain expressions independent of indentation', () => {
    const line = 'let file = match exists ~/.codex { true => { ~/.codex }, _ => { ~ }, };';
    hasScope(line, 'match', 'keyword.control.msync');
    hasScope(line, 'exists', 'keyword.operator.logical.msync');
    hasScope(line, 'true', 'constant.language.boolean.msync');
    hasScope(line, '=>', 'keyword.operator.msync');
    hasScope(line, '{', 'punctuation.section.block.begin.msync');
    hasScope(line, '}', 'punctuation.section.block.end.msync');
    hasScope(line, ';', 'punctuation.separator.msync');
    hasScope('match file { <.+> => {}, _ => {} };', '<.+>', 'string.regexp.msync');
    hasScope('match decltype(file) { Path => {}, _ => {} };', 'Path', 'support.type.msync');
    hasScope('scope hello { item: true, };', 'scope', 'storage.type.namespace.msync');
    hasScope('scope hello { item: true, };', 'hello', 'entity.name.type.msync');
    hasScope('let hello = scope { item: true };', 'scope', 'keyword.control.msync');
});

test('paths preserve namespace, lookup, composition and callback roles', () => {
    for (const line of [
        'copy($local, .codex/codex.prompt);',
        '"C:\\Users\\STONE\\.codex"/codex.prompt',
        '$local::.codex/codex.prompt',
        '($local)::("C:\\Users\\STONE\\.codex" / codex.prompt)',
        '$std::ranges::filter(keep)',
    ]) {
        const pieces = grammar.tokenizeLine(line, textmate.INITIAL).tokens
            .map(token => ({ text: line.slice(token.startIndex, token.endIndex), scopes: token.scopes }))
            .filter(piece => !piece.scopes.includes('string.quoted.double.msync'));
        for (const piece of pieces) {
            if (piece.text.includes('$') || piece.text.includes('::') || piece.text.includes('/')) {
                assert.ok(['$', '::', '/'].includes(piece.text), JSON.stringify(piece));
                assert.ok(piece.scopes.includes('keyword.operator.msync'), JSON.stringify(piece));
                assert.ok(!piece.scopes.includes('entity.name.namespace.msync'), JSON.stringify(piece));
            }
        }
    }
    hasScope('let config = $wsl::codex(config.toml);', 'wsl', 'entity.name.namespace.msync');
    hasScope('let config = $wsl::codex(config.toml);', 'codex', 'entity.name.function.msync');
    for (const namespace of ['$view', 'views', 'ranges', '$std::ranges']) {
        hasScope(`items | ${namespace}::filter(present)`, 'filter', 'support.function.range.msync');
    }
    for (const word of ['or', 'not', 'and', 'delete', 'true', 'fn', 'match', 'Path']) {
        assert.ok(!scopes(`root/${word}/file -> output;`, word).some(scope => /keyword|constant\.language|support.type/.test(scope)));
    }
});

test('ordered ranges do not absorb booleans, calls, paths or regex classes', () => {
    const line = 'let items = ["可莉🌟", true, $local::"two words", codex(config.toml)];';
    hasScope(line, '[', 'punctuation.section.brackets.begin.msync');
    hasScope(line, ']', 'punctuation.section.brackets.end.msync');
    hasScope(line, '可莉🌟', 'string.quoted.double.msync');
    hasScope(line, 'true', 'constant.language.boolean.msync');
    hasScope(line, 'local', 'entity.name.namespace.msync');
    hasScope(line, 'two words', 'string.quoted.double.msync');
    hasScope(line, 'codex', 'entity.name.function.msync');
    hasScope('let items = [root/<[>#]+\\.txt>];', '[>#]', 'constant.other.character-class.regexp.msync');
    const endpoint = grammar.tokenizeLine('let items = [tcp://[::1]:80];', textmate.INITIAL).tokens;
    assert.ok(endpoint.some(token => token.scopes.includes('punctuation.section.brackets.end.msync')));
});

test('comparisons and comments are shielded by literal Strings and regex classes', () => {
    for (const operator of ['<', '<=', '>', '>=', '==', '!=']) {
        hasScope(`"可莉"${operator}"琴"`, operator, 'keyword.operator.msync');
    }
    assert.ok(!scopes('left<right', '<').includes('string.regexp.msync'));
    hasScope('root/<[>#]+\\.txt> | views::take(1) # note', '# note', 'comment.line.number-sign.msync');
    hasScope('let file = path; // note', '// note', 'comment.line.double-slash.msync');
    hasScope('"// # text";', '//', 'string.quoted.double.msync');
    hasScope('let file = root/<[//#]+>;', '[//#]', 'constant.other.character-class.regexp.msync');
});

test('ordinary Strings preserve backslashes, doubled quotes and multiline contents', () => {
    for (const line of [
        String.raw`let path = "D:\Code\agents\target\xmlsquish"/codex.prompt;`,
        String.raw`let path = "C:\Users\STONE\.codex"/codex.prompt;`,
        String.raw`let path = "D:\quoted""directory\target\"/codex.prompt;`,
    ]) {
        hasScope(line, 'codex.prompt', 'variable.other.readwrite.msync');
        const tokens = grammar.tokenizeLine(line, textmate.INITIAL).tokens;
        assert.ok(!tokens.some(token => token.scopes.some(scope => scope.startsWith('constant.character.escape'))));
        const backslashes = tokens.filter(token => line.slice(token.startIndex, token.endIndex).includes('\\'));
        assert.ok(backslashes.length);
        assert.ok(backslashes.every(token => token.scopes.includes('string.quoted.double.msync')));
    }
    const first = grammar.tokenizeLine('let text = "可莉🌟 literal \\target', textmate.INITIAL);
    const middle = grammar.tokenizeLine('inside ""quotes"" // # $local:: / <selector>', first.ruleStack);
    assert.ok(middle.tokens.every(token => token.scopes.includes('string.quoted.double.msync')));
    const closing = grammar.tokenizeLine('last line" / child; // comment', middle.ruleStack);
    assert.ok(closing.tokens.some(token => token.scopes.includes('keyword.operator.msync')));
    assert.ok(closing.tokens.some(token => token.scopes.includes('comment.line.double-slash.msync')));
    const after = grammar.tokenizeLine('if true {};', closing.ruleStack);
    assert.ok(after.tokens.some(token => token.scopes.includes('keyword.control.msync')));
    assert.ok(!after.tokens.some(token => token.scopes.includes('string.quoted.double.msync')));
});

test('unfinished selectors recover at newline instead of leaking into unrelated code', () => {
    const first = grammar.tokenizeLine('root/<[a-z', textmate.INITIAL);
    const next = grammar.tokenizeLine('if true {', first.ruleStack);
    hasScope('if true {', 'if', 'keyword.control.msync');
    assert.ok(!next.tokens.some(token => token.scopes.some(scope => scope.startsWith('string.'))));
});

test('URI protocol, separator and atomic address have distinct roles before punctuation', () => {
    for (const uri of [
        'ssh://forge', 'ssh://user@forge:2222/work/tree',
        'http://example.test/a?x=1#fragment', 'https://example.test',
        'tcp://127.0.0.1:8022', 'tcp://[::1]:8022',
        'ssh://user@[2001:db8::1]:2222/work/tree', 'ssh://forge/工作🌟/config',
    ]) {
        const separator = uri.indexOf('://');
        const line = `let remote = ${uri};`;
        hasScope(line, uri.slice(0, separator), 'support.type.protocol.msync');
        hasScope(line, '://', 'keyword.operator.protocol.msync');
        hasScope(line, uri.slice(separator + 3), 'string.unquoted.endpoint.msync');
        const endpoint = grammar.tokenizeLine(line, textmate.INITIAL).tokens
            .filter(token => token.scopes.includes('string.unquoted.endpoint.msync'));
        assert.equal(endpoint.length, 1);
        assert.equal(line.slice(endpoint[0].startIndex, endpoint[0].endIndex), uri.slice(separator + 3));
        hasScope(line, ';', 'punctuation.separator.msync');
    }
    hasScope('let remote = ssh://;', 'ssh', 'support.type.protocol.msync');
    hasScope('let remote = tcp://[::', '[::', 'string.unquoted.endpoint.msync');
    hasScope('let remote = "ssh://forge";', 'ssh://forge', 'string.quoted.double.msync');
});

test('manifest, brace editing, snippets and semantic fallback remain consistent', () => {
    const manifest = require('../package.json');
    const configuration = require('../language-configuration.json');
    assert.equal(manifest.version, '0.8.0');
    assert.equal(manifest.main, './out/extension.cjs');
    assert.equal(manifest.contributes.grammars[0].scopeName, 'source.msync');
    assert.equal(configuration.comments.lineComment, '//');
    assert.equal(configuration.folding.offSide, false);
    const indentation = new RegExp(configuration.indentationRules.increaseIndentPattern);
    for (const line of ['scope hello {', 'fn ready(x) {', 'if true {', '} else {', 'for x in items {', 'let result = match value {']) {
        assert.ok(indentation.test(line), line);
    }
    for (const line of ['// if true {', '# fn no() {', 'file -> result;', 'let root = path;', 'if true:']) {
        assert.ok(!indentation.test(line), line);
    }
    for (const [open, close] of [['[', ']'], ['{', '}'], ['(', ')']]) {
        assert.ok(configuration.brackets.some(pair => pair[0] === open && pair[1] === close));
        assert.ok(configuration.autoClosingPairs.some(pair => pair.open === open && pair.close === close));
        assert.ok(configuration.surroundingPairs.some(pair => pair[0] === open && pair[1] === close));
    }
    const words = new RegExp(configuration.wordPattern, 'g');
    assert.deepEqual('$local::.codex/codex.prompt;'.match(words), ['local', '.codex', 'codex.prompt']);
    assert.deepEqual('let file? = {~};'.match(words), ['let', 'file', '~']);
    const snippets = JSON.parse(fs.readFileSync(path.join(__dirname, '../snippets/msync.code-snippets'), 'utf8'));
    assert.equal(snippets.Function.body[0], 'fn ${1:copy}(${2:source, destination}) {');
    assert.equal(snippets['Native function'].body[0], 'native fn ${1:name}(${2:argument}: ${3:Path}) -> ${4:Bool};');
    const roles = new Map(manifest.contributes.semanticTokenTypes.map(type => [type.id, type.superType]));
    assert.equal(roles.get('protocol'), 'type');
    assert.equal(roles.get('endpoint'), 'string');
});


test('direct function and Scope values have semantic-compatible lexical fallbacks', () => {
    for (const line of ['let callback = fn (item) { item };', 'callback: fn (item) { item },']) {
        hasScope(line, 'callback', 'entity.name.function.msync');
    }
    for (const line of ['scope MyClass {', 'let MyClass = scope {', 'MyClass: scope {']) {
        hasScope(line, 'MyClass', 'entity.name.type.msync');
    }
    hasScope('new: fn() { scope { x: ~/.codex } },', 'new', 'entity.name.function.msync');
    hasScope('let callback = fn (item) { item };', 'item', 'variable.parameter.msync');
    const manifest = require('../package.json');
    assert.equal(manifest.contributes.configurationDefaults['[msync]']['editor.semanticHighlighting.enabled'], true);
    assert.equal(manifest.contributes.configurationDefaults['[msync]']['editor.defaultFormatter'], 'msync.msync-language');
    const mappings = manifest.contributes.semanticTokenScopes[0].scopes;
    assert.deepEqual(mappings.function, ['entity.name.function.msync']);
    assert.deepEqual(mappings.type, ['entity.name.type.msync']);
});
