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
        hasScope(line, 'fn', 'keyword.declaration.function.msync');
        hasScope(line, 'copy', 'entity.name.function.msync');
        hasScope(line, 'file', 'variable.parameter.msync');
        hasScope(line, 'Path', 'entity.name.type.msync');
        hasScope(line, 'String', 'entity.name.type.msync');
    }
    hasScope('let callback = fn (file) { file };', 'fn', 'keyword.declaration.function.msync');
    hasScope('let callback = fn (file) { file };', 'file', 'variable.parameter.msync');
    hasScope('item: fn SomeOperation { true },', 'SomeOperation', 'entity.name.function.msync');
    hasScope('let result = copy(input, output);', 'copy', 'entity.name.function.msync');
    hasScope('native fn exists(path: Path) -> Bool;', 'native', 'keyword.other.msync');
});

test('let bindings, nullable tests and types have independent scopes', () => {
    hasScope('let file? = null;', 'let', 'storage.type.variable.msync');
    hasScope('let file? = null;', 'file', 'variable.other.readwrite.msync');
    hasScope('let file? = null;', '?', 'keyword.control.msync');
    hasScope('let file? = null;', 'null', 'constant.language.null.msync');
    for (const type of ['Range', 'String', 'Function', 'Scope', 'Path', 'Process', 'Connection', 'Null', 'Bool']) {
        hasScope(`let value: ${type} = input;`, type, 'entity.name.type.msync');
    }
    hasScope('if file is not null { file -> target };', 'is', 'keyword.operator.logical.msync');
    hasScope('if file? {} else { file -> target };', '?', 'keyword.control.msync');
    hasScope('let kind = decltype(file);', 'decltype', 'keyword.other.msync');
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
    hasScope('match decltype(file) { Path => {}, _ => {} };', 'Path', 'entity.name.type.msync');
    hasScope('scope hello { item: true, };', 'scope', 'keyword.declaration.scope.msync');
    hasScope('scope hello { item: true, };', 'hello', 'entity.name.type.msync');
    hasScope('let hello = scope { item: true };', 'scope', 'keyword.declaration.scope.msync');
});

test('paths preserve namespace, lookup, composition and callback roles', () => {
    for (const line of [
        'copy($local, .codex/codex.prompt);',
        '"C:\\Users\\STONE\\.codex"/codex.prompt',
        '$local @ .codex/codex.prompt',
        '$local @ ("C:\\Users\\STONE\\.codex" / codex.prompt)',
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
    hasScope('let config = $wsl @ codex(config.toml);', 'wsl', 'variable.other.readwrite.msync');
    hasScope('let config = $wsl @ codex(config.toml);', 'codex', 'entity.name.function.msync');
    hasScope('items | $std::ranges::filter(present)', 'filter', 'entity.name.function.msync');
    for (const namespace of ['$view', '$views', '$ranges', 'view', 'views', 'ranges'])
        assert.ok(!scopes(`items | ${namespace}::filter(present)`, 'filter').includes('support.function.range.msync'));
    for (const word of ['or', 'not', 'and', 'delete', 'true', 'fn', 'match', 'Path']) {
        assert.ok(!scopes(`root/${word}/file -> output;`, word).some(scope => /keyword|constant\.language|support.type/.test(scope)));
    }
});

test('ordered ranges do not absorb booleans, calls, paths or regex classes', () => {
    const line = 'let items = ["可莉🌟", true, $local @ "two words", codex(config.toml)];';
    hasScope(line, '[', 'punctuation.section.brackets.begin.msync');
    hasScope(line, ']', 'punctuation.section.brackets.end.msync');
    hasScope(line, '可莉🌟', 'string.quoted.double.msync');
    hasScope(line, 'true', 'constant.language.boolean.msync');
    hasScope(line, 'local', 'variable.other.readwrite.msync');
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
    hasScope('root/<[>#]+\\.txt> | $std::ranges::take(1) # note', '# note', 'comment.line.number-sign.msync');
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
    assert.match(manifest.version, /^\d+\.\d+\.\d+$/);
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
    hasScope('scope MyClass {', 'MyClass', 'entity.name.type.msync');
    for (const line of ['let my_instance = scope {', 'my_instance: scope {']) {
        hasScope(line, 'my_instance', 'variable.other.readwrite.msync');
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


test('declaration keywords stay keywords while names retain inferred roles', () => {
    for (const line of ['fn named() {', 'let callback = fn () {', 'entry: fn named() {', 'entry: fn () {']) {
        hasScope(line, 'fn', 'keyword.declaration.function.msync');
    }
    hasScope('scope SomeClass { remote: ssh://forge, };', 'scope', 'keyword.declaration.scope.msync');
    hasScope('scope SomeClass { remote: ssh://forge, };', 'SomeClass', 'entity.name.type.msync');
    hasScope('let value: Scope = SomeClass;', 'Scope', 'entity.name.type.msync');
    hasScope('let value = scope { remote: ssh://forge, };', 'scope', 'keyword.declaration.scope.msync');
    hasScope('let value = scope { remote: ssh://forge, };', 'remote', 'variable.other.readwrite.msync');
    for (const word of ['if', 'else', 'match', 'release', 'acquire', 'acqrel']) {
        hasScope(`${word} value {}`, word, 'keyword.control.msync');
    }
    hasScope('if value? {} else {}', '?', 'keyword.control.msync');
    hasScope('let value? = null;', '?', 'keyword.control.msync');
    hasScope('fn pass(value?: Path) { value }', '?', 'keyword.control.msync');
    const manifest = require('../package.json');
    assert.ok(manifest.contributes.semanticTokenTypes.some(type => type.id === 'controlKeyword' && type.superType === 'keyword'));
    const mappings = manifest.contributes.semanticTokenScopes[0].scopes;
    assert.deepEqual(mappings.variable, ['variable.other.readwrite.msync']);
    assert.deepEqual(mappings.controlKeyword, ['keyword.control.msync']);
});


test('explicit ancestor import stays a declaration keyword, not control flow', () => {
    hasScope('global root, .codex;', 'global', 'keyword.other.msync');
});

test('Number literals preserve complete lexical forms without coloring filename fragments', () => {
    for (const literal of ['0', '1.5', '.5', '1.', '1e3', '1.5e-7', '1_000.25', '1e1_0', '0xFF_FF', '0Xab', '0b10_01', '0B11', '0o7_7', '0O12']) {
        const line = `let value = ${literal};`;
        hasScope(line, literal, 'constant.numeric.msync');
        const start = line.indexOf(literal);
        const token = grammar.tokenizeLine(line, textmate.INITIAL).tokens.find(item => item.startIndex === start);
        assert.equal(token.endIndex - token.startIndex, literal.length, `${literal} is one number`);
    }
    hasScope('let value = -0;', '-', 'keyword.operator.msync');
    hasScope('let value = -0;', '0', 'constant.numeric.msync');
    for (const operator of ['+', '-', '*', '**', '/', '%', '@'])
        hasScope(`let value = 7 ${operator} 2;`, operator, 'keyword.operator.msync');
    for (const literal of ['1.5.toml', '1e3.txt', '0xFF-config', '.5.json', 'root/123', 'root/1.5', 'root/0xFF', 'root/file-12', '1__2']) {
        const tokens = grammar.tokenizeLine(`let file = ${literal};`, textmate.INITIAL).tokens;
        assert.ok(!tokens.some(item => item.scopes.includes('constant.numeric.msync')), literal);
    }
    hasScope('let value = 2**3**2;', '**', 'keyword.operator.msync');
    hasScope('let value = 1+2;', '1', 'constant.numeric.msync');
    hasScope('let value = 1+2;', '+', 'keyword.operator.msync');
    hasScope('let value = 1+2;', '2', 'constant.numeric.msync');
});

test('concept contracts and explicit conversions use type and control roles', () => {
    const lines = [
        'concept Monoid {',
        '    Semigroup,',
        '    type Self;',
        '    identity: Self,',
        '    combine: fn(left: Self, right: Self) -> Self,',
        '};',
        'let value = path as String;',
    ];
    let stack = textmate.INITIAL;
    const roles = [];
    for (const line of lines) {
        const result = grammar.tokenizeLine(line, stack);
        roles.push(result.tokens.map(token => ({text:line.slice(token.startIndex, token.endIndex),scopes:token.scopes})));
        stack = result.ruleStack;
    }
    for (const [index, word, role] of [
        [0, 'concept', 'keyword.declaration.concept.msync'],
        [0, 'Monoid', 'entity.name.type.msync'],
        [1, 'Semigroup', 'entity.name.type.msync'],
        [2, 'type', 'keyword.declaration.type.msync'],
        [2, 'Self', 'entity.name.type.msync'],
        [3, 'identity', 'variable.other.property.msync'],
        [3, 'Self', 'entity.name.type.msync'],
        [4, 'combine', 'entity.name.function.msync'],
        [4, 'fn', 'keyword.declaration.function.msync'],
        [4, 'left', 'variable.parameter.msync'],
        [4, 'Self', 'entity.name.type.msync'],
        [6, 'as', 'keyword.control.msync'],
        [6, 'String', 'entity.name.type.msync'],
    ]) assert.ok(roles[index].some(token => token.text === word && token.scopes.includes(role)), JSON.stringify({index,word,role,tokens:roles[index]}));
    hasScope('fn fold(value: Monoid | Semigroup) {', 'Monoid', 'entity.name.type.msync');
    hasScope('fn fold(value: Monoid | Semigroup) {', 'Semigroup', 'entity.name.type.msync');
    hasScope('fn fold(value: Monoid) -> Monoid {', 'Monoid {', 'entity.name.type.msync');
    hasScope('let value = object as Monoid;', 'Monoid', 'entity.name.type.msync');
    hasScope('remote @ root/as/file;', 'as', 'variable.other.readwrite.msync');
});

test('inline associated binders retain declaration and type roles across requirement forms', () => {
    const product = 'concept Product { first?: typename First, second?: typename Second, };';
    hasScope(product, 'typename', 'keyword.declaration.type.msync');
    hasScope(product, 'First', 'entity.name.type.msync');
    hasScope(product, 'Second', 'entity.name.type.msync');
    hasScope(product, '?', 'keyword.control.msync');
    for (const line of [
        'concept Bag { items: Range<typename Item>, };',
        'concept Operation { apply: fn(value: typename Item) -> Item, };',
        'concept Producer { make: fn() -> typename Item, };',
    ]) {
        hasScope(line, 'typename', 'keyword.declaration.type.msync');
        hasScope(line, 'Item', 'entity.name.type.msync');
    }
    hasScope('concept Legacy { type Item; value: Item, };', 'type', 'keyword.declaration.type.msync');
    hasScope('root/typename/file', 'typename', 'variable.other.readwrite.msync');
    hasScope('"typename Item"', 'typename', 'string.quoted.double.msync');
});

test('standard module qualifiers are named types but connection values are identifiers', () => {
    for (const line of ['$std::math::abs(-0);', '$std::string::trim(text);', '$std::ranges::take(items, 2);']) {
        hasScope(line, 'std', 'entity.name.type.msync');
        const module = line.split('::')[1];
        hasScope(line, module, 'entity.name.type.msync');
        const name = line.split('::')[2].split('(')[0];
        hasScope(line, name, 'entity.name.function.msync');
    }
    hasScope('let root = $std;', 'std', 'entity.name.type.msync');
    hasScope('$wsl @ path;', 'wsl', 'variable.other.readwrite.msync');
    hasScope('$local @ path;', 'local', 'variable.other.readwrite.msync');
    const words = new RegExp(require('../language-configuration.json').wordPattern, 'g');
    assert.deepEqual('./path@$wsl'.match(words), ['.', 'path', 'wsl']);
});
