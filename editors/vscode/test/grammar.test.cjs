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
    assert.ok(scopes(line, word).includes(expected), `${word} in ${line}: expected ${expected}, got ${scopes(line, word)}`);
}

test('declarations, parameters, calls and range adapters use theme-standard scopes', () => {
    hasScope('boolean any(a, b, c):', 'boolean', 'storage.type.function.msync');
    hasScope('boolean any(a, b, c):', 'any', 'entity.name.function.msync');
    hasScope('boolean any(item, other):', 'item', 'variable.parameter.msync');
    hasScope('result = any(true, false)', 'any', 'entity.name.function.msync');
    hasScope('files | views::filter(keep) | ranges::empty', 'views', 'entity.name.namespace.msync');
    hasScope('files | views::filter(keep) | ranges::empty', 'filter', 'support.function.range.msync');
    hasScope('files | views::all', 'all', 'support.function.range.msync');
    hasScope('files | views::take(32)', '32', 'constant.numeric.count.msync');
    hasScope('items | $view::filter(present)', 'view', 'entity.name.namespace.msync');
    hasScope('items | $view::filter(present)', 'filter', 'support.function.range.msync');
    hasScope('$view::single(true)', 'single', 'support.function.range.msync');
});

test('nested scopes and scoped calls retain native namespace and function roles', () => {
    hasScope('config := $wsl::codex(config.toml)', 'wsl', 'entity.name.namespace.msync');
    hasScope('config := $wsl::codex(config.toml)', 'codex', 'entity.name.function.msync');
    hasScope('config := ($wsl::project)::codex(config.toml)', 'project', 'variable.other.readwrite.msync');
    hasScope('config := ($wsl::project)::codex(config.toml)', 'codex', 'entity.name.function.msync');
    hasScope('result := outer::inner::file', 'outer', 'entity.name.namespace.msync');
    hasScope('result := outer::inner::file', 'inner', 'entity.name.namespace.msync');
});

test('ordered range punctuation does not absorb booleans, calls, paths or regex classes', () => {
    const line = 'items := ["可莉🌟", true, $local::"two words", codex(config.toml), any(false)]';
    hasScope(line, '[', 'punctuation.section.brackets.begin.msync');
    hasScope(line, ']', 'punctuation.section.brackets.end.msync');
    hasScope(line, '可莉🌟', 'string.quoted.double.msync');
    hasScope(line, 'true', 'constant.language.boolean.msync');
    hasScope(line, 'local', 'entity.name.namespace.msync');
    hasScope(line, 'two words', 'string.quoted.double.msync');
    hasScope(line, 'codex', 'entity.name.function.msync');
    hasScope(line, 'any', 'entity.name.function.msync');
    hasScope(line, 'false', 'constant.language.boolean.msync');
    hasScope('items := [root/<[>#]+\\.txt>] | $view::take(1)', '[>#]', 'constant.other.character-class.regexp.msync');
    hasScope('items := [root/<[>#]+\\.txt>] | $view::take(1)', 'take', 'support.function.range.msync');
    hasScope('items := [tcp://[::1]:80]', 'tcp://', 'string.unquoted.endpoint.msync');
    const endpoint = grammar.tokenizeLine('items := [tcp://[::1]:80]', textmate.INITIAL).tokens;
    assert.ok(endpoint.at(-1).scopes.includes('punctuation.section.brackets.end.msync'));
});

test('scheduled conditions color each modifier without treating it as a path component', () => {
    for (const scheduling of ['release', 'acquire', 'acqrel']) {
        const line = `if ${scheduling} ready(true):`;
        hasScope(line, 'if', 'keyword.control.msync');
        hasScope(line, scheduling, 'keyword.control.msync');
        hasScope(line, 'ready', 'entity.name.function.msync');
        hasScope(line, 'true', 'constant.language.boolean.msync');
        assert.ok(!scopes(`root/${scheduling}/file -> output`, scheduling)
            .some(scope => scope.startsWith('keyword.')));
    }
});

test('booleans, logical operators, all controls and lexical bindings are recognized', () => {
    hasScope('enabled = true and not false', 'true', 'constant.language.boolean.msync');
    hasScope('if true:', 'true', 'constant.language.boolean.msync');
    hasScope('if not(false):', 'false', 'constant.language.boolean.msync');
    for (const word of ['and', 'not', 'or', 'exists']) {
        hasScope(`enabled = true ${word} input`, word, 'keyword.operator.logical.msync');
    }
    for (const word of ['return', 'global', 'drop', 'delete', 'include', 'execute', 'if', 'else', 'for', 'in', 'loop', 'break', 'release', 'acquire', 'acqrel', 'async', 'await']) {
        hasScope(`${word} item:`, word, 'keyword.control.msync');
    }
    hasScope('result = true', 'result', 'variable.other.readwrite.msync');
    hasScope('$pwd::pwd/input', 'pwd', 'entity.name.namespace.msync');
});

test('quoted strings and regular-expression classes shield punctuation', () => {
    const line = 'root/<[>#]+\\.txt> | views::take(1) # note';
    hasScope(line, '<', 'string.regexp.msync');
    hasScope(line, '[>#]', 'constant.other.character-class.regexp.msync');
    hasScope(line, '# note', 'comment.line.number-sign.msync');
    hasScope('text = "a\\n -> # b"', '# b', 'string.quoted.double.msync');
    hasScope('text = "a\\n -> # b"', '\\n', 'string.quoted.double.msync');
    hasScope('remote => https://example.com/a?x=1#fragment', 'https://', 'string.unquoted.endpoint.msync');
});

test('comparisons and reserved-looking filename components are not regexes or controls', () => {
    for (const operator of ['<', '<=', '>', '>=', '==', '!=']) {
        hasScope(`"可莉"${operator}"琴"`, operator, 'keyword.operator.msync');
    }
    for (const word of ['or', 'not', 'and', 'delete', 'true']) {
        const found = scopes(`root/${word}/file -> output`, word);
        assert.ok(!found.some(scope => /keyword|constant\.language/.test(scope)), found.join(', '));
    }
    assert.ok(!scopes('left<right', '<').includes('string.regexp.msync'));
});

test('unfinished selector lines recover while multiline String contents retain literal scopes', () => {
    let state = textmate.INITIAL;
    for (const line of ['root/<[a-z', 'if acquire false:', '[true, false', 'items | $view::filter(ready)']) {
        const result = grammar.tokenizeLine(line, state);
        let cursor = 0;
        for (const token of result.tokens) {
            assert.equal(token.startIndex, cursor);
            assert.ok(token.endIndex > token.startIndex);
            cursor = token.endIndex;
        }
        assert.ok(cursor >= line.length);
        state = result.ruleStack;
    }
    const next = grammar.tokenizeLine('if release true:', state);
    const modifier = next.tokens.find(token => token.startIndex <= 3 && token.endIndex > 3);
    assert.ok(modifier.scopes.includes('keyword.control.msync'));
    assert.ok(!modifier.scopes.some(scope => scope.startsWith('string.')));

    const first = grammar.tokenizeLine('text = "可莉🌟 literal \\target', textmate.INITIAL);
    const middle = grammar.tokenizeLine('inside ""quotes"" # $local:: / <selector>', first.ruleStack);
    assert.ok(middle.tokens.every(token => token.scopes.includes('string.quoted.double.msync')));
    const closing = grammar.tokenizeLine('last line" / child # comment', middle.ruleStack);
    assert.ok(closing.tokens.some(token => token.scopes.includes('keyword.operator.msync')));
    assert.ok(closing.tokens.some(token => token.scopes.includes('comment.line.number-sign.msync')));
    const after = grammar.tokenizeLine('if true:', closing.ruleStack);
    assert.ok(after.tokens.some(token => token.scopes.includes('keyword.control.msync')));
    assert.ok(!after.tokens.some(token => token.scopes.includes('string.quoted.double.msync')));
});

test('manifest and language configuration are internally consistent', () => {
    const manifest = require('../package.json');
    const configuration = require('../language-configuration.json');
    assert.equal(manifest.main, './out/extension.cjs');
    assert.equal(manifest.contributes.grammars[0].scopeName, 'source.msync');
    assert.equal(manifest.dependencies['vscode-languageclient'], '11.0.0');
    assert.equal(configuration.comments.lineComment, '#');
    const indentation = new RegExp(configuration.indentationRules.increaseIndentPattern);
    for (const line of ['scope hello:', 'boolean ready(x):', 'if true:', 'if release ready:', 'if acquire ready:',
        'if acqrel ready:', 'else if acquire false:', 'else:', 'for x in items:', 'loop:', 'result = loop:']) {
        assert.ok(indentation.test(line), line);
    }
    for (const line of ['# if true:', 'file -> result', 'root := path']) {
        assert.ok(!indentation.test(line), line);
    }
    assert.ok(configuration.brackets.some(pair => pair[0] === '[' && pair[1] === ']'));
    assert.ok(configuration.autoClosingPairs.some(pair => pair.open === '[' && pair.close === ']' && pair.notIn.includes('string')));
    assert.ok(configuration.surroundingPairs.some(pair => pair[0] === '[' && pair[1] === ']'));
    const snippets = JSON.parse(fs.readFileSync(path.join(__dirname, '../snippets/msync.code-snippets'), 'utf8'));
    assert.equal(snippets['View pipeline'].body[0], '${1:items} | \\$std::ranges::filter(${2:predicate})');
    assert.equal(snippets['Ordered range'].body[0], '[${1:"first"}, ${2:true}, \\$local::${3:path}]');
    for (const scheduling of ['release', 'acquire', 'acqrel']) {
        assert.equal(snippets[`Conditional ${scheduling}`].body[0], `if ${scheduling} \${1:true}:`);
    }
});

test('every compiler-tooling range spelling has native namespace/function scopes', () => {
    const source = fs.readFileSync(path.join(__dirname, '../../../src/highlight.rs'), 'utf8');
    const constants = source.split('pub const RANGE_ADAPTORS:')[1].split('];')[0];
    const spellings = Array.from(constants.matchAll(/"([^"]+)"/g), match => match[1]);
    assert.ok(spellings.length >= 25);
    for (const spelling of spellings) {
        const pieces = spelling.split('::');
        const name = pieces.pop();
        const namespace = pieces.at(-1).replace(/^\$/, '');
        hasScope(`items | ${spelling}`, namespace, 'entity.name.namespace.msync');
        hasScope(`items | ${spelling}`, name, 'support.function.range.msync');
    }
});

test('explicit namespaces, source natives and standard libraries retain native roles', () => {
    hasScope('scope hello:', 'scope', 'storage.type.namespace.msync');
    hasScope('scope hello:', 'hello', 'entity.name.namespace.msync');
    hasScope('native exists(path) -> Bool = "fs.exists"', 'native', 'storage.type.function.msync');
    hasScope('native exists(path) -> Bool = "fs.exists"', 'exists', 'entity.name.function.msync');
    hasScope('native exists(path) -> Bool = "fs.exists"', 'path', 'variable.parameter.msync');
    hasScope('items | $std::ranges::filter(present)', 'ranges', 'entity.name.namespace.msync');
    hasScope('items | $std::ranges::filter(present)', 'filter', 'support.function.range.msync');
    hasScope('if $std::fs::is_directory(input):', 'std', 'entity.name.namespace.msync');
    hasScope('if $std::fs::is_directory(input):', 'fs', 'entity.name.namespace.msync');
    hasScope('if $std::fs::is_directory(input):', 'is_directory', 'entity.name.function.msync');
    for (const word of ['scope', 'native']) {
        assert.ok(!scopes(`root/${word}/file -> output`, word).some(scope => scope.startsWith('keyword.')));
    }
});

test('lookup, qualification and composition operators have independent theme roles', () => {
    for (const line of [
        'copy($local, .codex/codex.prompt)',
        '"C:\\Users\\STONE\\.codex"/codex.prompt',
        '$local::.codex/codex.prompt',
        '($local)::("C:\\Users\\STONE\\.codex" / codex.prompt)',
        '$std::ranges::filter(keep)',
    ]) {
        const tokens = grammar.tokenizeLine(line, textmate.INITIAL).tokens;
        const pieces = tokens.map(token => ({ text: line.slice(token.startIndex, token.endIndex), scopes: token.scopes }));
        const outsideStrings = pieces.filter(piece => !piece.scopes.includes('string.quoted.double.msync'));
        for (const piece of outsideStrings) {
            if (piece.text.includes('$') || piece.text.includes('::') || piece.text.includes('/')) {
                assert.ok(['$', '::', '/'].includes(piece.text), JSON.stringify(piece));
                assert.ok(piece.scopes.includes('keyword.operator.msync'), JSON.stringify(piece));
                assert.ok(!piece.scopes.includes('entity.name.namespace.msync'), JSON.stringify(piece));
            }
        }
    }
    hasScope('copy($local, .codex/codex.prompt)', 'local', 'entity.name.namespace.msync');
    hasScope('"C:\\Users\\STONE\\.codex"/codex.prompt', 'Users', 'string.quoted.double.msync');
    hasScope('$local::root/<[a/$:]+>', '[a/$:]', 'constant.other.character-class.regexp.msync');
    hasScope('root => ssh://user@host/path', 'ssh://', 'string.unquoted.endpoint.msync');
});

test('editor word selection keeps operators separate from filenames and namespaces', () => {
    const configuration = require('../language-configuration.json');
    const words = new RegExp(configuration.wordPattern, 'g');
    assert.deepEqual('$local::.codex/codex.prompt'.match(words), ['local', '.codex', 'codex.prompt']);
    assert.deepEqual('$std::ranges::filter(可莉🌟)'.match(words), ['std', 'ranges', 'filter', '可莉🌟']);
});

test('ordinary Strings preserve Windows backslashes and doubled quotes without C escape coloring', () => {
    for (const line of [
        String.raw`path := "D:\Code\agents\target\xmlsquish"/codex.prompt`,
        String.raw`path := "C:\Users\STONE\.codex"/codex.prompt`,
        String.raw`path := "D:\quoted""directory\target\"/codex.prompt`,
    ]) {
        hasScope(line, 'codex.prompt', 'variable.other.readwrite.msync');
        hasScope(line, '/codex.prompt', 'keyword.operator.msync');
        const tokens = grammar.tokenizeLine(line, textmate.INITIAL).tokens;
        assert.ok(!tokens.some(token => token.scopes.some(scope => scope.startsWith('constant.character.escape'))));
        const backslashes = tokens.filter(token => line.slice(token.startIndex, token.endIndex).includes('\\'));
        assert.ok(backslashes.length);
        assert.ok(backslashes.every(token => token.scopes.includes('string.quoted.double.msync')));
    }
    const quoted = String.raw`text := "literal \n\t\r\"" # $local::path/<[a-z]+>" / child # actual comment`;
    for (const word of ['\\n', '\\t', '\\r', '# $local', '::', '<[a-z]+>']) {
        hasScope(quoted, word, 'string.quoted.double.msync');
        assert.ok(!scopes(quoted, word).some(scope => /escape|operator|regexp|comment/.test(scope)));
    }
    hasScope(quoted, '""', 'constant.character.quote.msync');
    hasScope(quoted, '# actual comment', 'comment.line.number-sign.msync');
});
