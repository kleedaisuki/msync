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
    hasScope('$pwd::pwd/input', '$pwd', 'entity.name.namespace.msync');
});

test('quoted strings and regular-expression classes shield punctuation', () => {
    const line = 'root/<[>#]+\\.txt> | views::take(1) # note';
    hasScope(line, '<', 'string.regexp.msync');
    hasScope(line, '[>#]', 'constant.other.character-class.regexp.msync');
    hasScope(line, '# note', 'comment.line.number-sign.msync');
    hasScope('text = "a\\n -> # b"', '# b', 'string.quoted.double.msync');
    hasScope('text = "a\\n -> # b"', '\\n', 'constant.character.escape.msync');
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

test('unfinished single-line literals do not poison later lines and native spans cover UTF16 input', () => {
    let state = textmate.INITIAL;
    for (const line of ['text = "unfinished', 'if true:', '    root/<[a-z', 'delete file']) {
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
    hasScope('delete file', 'delete', 'keyword.control.msync');
});

test('manifest and language configuration are internally consistent', () => {
    const manifest = require('../package.json');
    const configuration = require('../language-configuration.json');
    assert.equal(manifest.main, './out/extension.cjs');
    assert.equal(manifest.contributes.grammars[0].scopeName, 'source.msync');
    assert.equal(manifest.dependencies['vscode-languageclient'], '11.0.0');
    assert.equal(configuration.comments.lineComment, '#');
    const indentation = new RegExp(configuration.indentationRules.increaseIndentPattern);
    for (const line of ['boolean ready(x):', 'if true:', 'else if false:', 'else:', 'for x in items:', 'loop:', 'result = loop:']) {
        assert.ok(indentation.test(line), line);
    }
    for (const line of ['# if true:', 'file -> result', 'root := path']) {
        assert.ok(!indentation.test(line), line);
    }
});

test('every compiler-tooling range spelling has native namespace/function scopes', () => {
    const source = fs.readFileSync(path.join(__dirname, '../../../src/highlight.rs'), 'utf8');
    const constants = source.split('pub const RANGE_ADAPTORS:')[1].split('];')[0];
    const spellings = Array.from(constants.matchAll(/"([^"]+)"/g), match => match[1]);
    assert.ok(spellings.length >= 25);
    for (const spelling of spellings) {
        const [namespace, name] = spelling.split('::');
        hasScope(`items | ${spelling}`, namespace, 'entity.name.namespace.msync');
        hasScope(`items | ${spelling}`, name, 'support.function.range.msync');
    }
});
