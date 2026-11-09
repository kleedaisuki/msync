'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');
const { URI } = require('vscode-uri');
const fs = require('node:fs');
const path = require('node:path');
const { registerArtifactViews, artifactUri, textUri, renderPage } = require('../artifact-viewer.cjs');

class Emitter {
    constructor() { this.listeners = new Set(); this.event = callback => { this.listeners.add(callback); return { dispose: () => this.listeners.delete(callback) }; }; }
    fire(value) { for (const callback of this.listeners) callback(value); }
    dispose() { this.listeners.clear(); }
}

class Cancellation {
    constructor() { this.emitter = new Emitter(); this.token = { isCancellationRequested: false, onCancellationRequested: this.emitter.event }; }
    cancel() { this.token.isCancellationRequested = true; this.emitter.fire(); }
    dispose() { this.emitter.dispose(); }
}

class Position {
    constructor(line, character) { this.line = line; this.character = character; }
}

class Range {
    constructor(...args) {
        [this.start, this.end] = args.length === 2 ? args : [new Position(args[0], args[1]), new Position(args[2], args[3])];
    }
    contains(position) {
        return (position.line > this.start.line || position.line === this.start.line && position.character >= this.start.character)
            && (position.line < this.end.line || position.line === this.end.line && position.character <= this.end.character);
    }
}

function fixture(text = '; typed IR\nfn @f0 main() -> Bool\n  return true\n') {
    const range = (line, start, end) => ({ start: { line, character: start }, end: { line, character: end } });
    return {
        schemaVersion: 1, text, formatVersion: 3, languageVersion: '0.8.0', stage: 'relocatable', optimizationLevel: 2,
        symbols: [{ name: 'main', kind: 12, range: range(1, 0, 25), selectionRange: range(1, 7, 11) }],
        references: [{ range: range(1, 3, 6), targetLine: 1, targetCharacter: 3 }],
        hovers: [{ range: range(1, 3, 6), markdown: '**Function** `main`' }],
    };
}

function hirFixture() {
    const model = fixture([
        'hir format 1 language "0.8.0"',
        'source @s0 "removed source 可莉.msync"',
        'type @t0 = Path',
        'binding @l0 "file" : @t0 mutable',
        'fn @f0 "copy" -> Bool',
        '  @n0: CaptureLocation @l0 : @t0',
        '  @n1: If @n0 -> Bool',
    ].join('\n'));
    model.artifactKind = 'hir';
    model.formatVersion = 1;
    model.stage = 'typed-hir';
    delete model.optimizationLevel;
    const span = (line, start, end) => ({ start: { line, character: start }, end: { line, character: end } });
    model.symbols = [{ name: 'file', kind: 13, range: span(3, 0, 33), selectionRange: span(3, 8, 11) }];
    model.references = [{ range: span(5, 23, 26), targetLine: 3, targetCharacter: 8 }];
    model.hovers = [{ range: span(5, 23, 26), markdown: '**Location capture** `file: Path`' }];
    return model;
}

function harness(request = async () => fixture(), nativeWatch) {
    const providers = new Map(), commands = new Map(), languageProviders = new Map();
    const files = new Map(), watchers = [], nativeWatchers = [], errors = [], warnings = [], opened = [], custom = [];
    const closed = new Emitter();
    const disposable = () => ({ dispose() {} });
    const host = {
        Uri: URI, Position, Range, EventEmitter: Emitter, CancellationTokenSource: Cancellation,
        CancellationError: class extends Error { constructor() { super('Cancelled'); } },
        RelativePattern: class { constructor(baseUri, pattern) { this.baseUri = baseUri; this.pattern = pattern; } },
        DocumentSymbol: class { constructor(name, detail, kind, range, selectionRange) { Object.assign(this, { name, detail, kind, range, selectionRange }); } },
        MarkdownString: class { constructor(value) { this.value = value; } },
        Hover: class { constructor(contents, range) { Object.assign(this, { contents, range }); } },
        Location: class { constructor(uri, range) { Object.assign(this, { uri, range }); } },
        commands: {
            registerCommand(name, callback) { commands.set(name, callback); return disposable(); },
            async executeCommand(...args) { opened.push(args); },
        },
        languages: {
            registerDocumentSymbolProvider(selector, value) { languageProviders.set('symbols', { selector, value }); return disposable(); },
            registerHoverProvider(selector, value) { languageProviders.set('hover', { selector, value }); return disposable(); },
            registerDefinitionProvider(selector, value) { languageProviders.set('definition', { selector, value }); return disposable(); },
        },
        workspace: {
            fs: { async readFile(uri) { const value = files.get(uri.toString()); if (!value) throw new Error('File not found: ' + uri.toString()); return value; } },
            onDidCloseTextDocument: closed.event,
            createFileSystemWatcher(pattern) {
                const change = new Emitter(), create = new Emitter(), remove = new Emitter();
                const watcher = { pattern, change, create, remove, onDidChange: change.event, onDidCreate: create.event, onDidDelete: remove.event,
                    dispose() { this.disposed = true; change.dispose(); create.dispose(); remove.dispose(); } };
                watchers.push(watcher); return watcher;
            },
            registerTextDocumentContentProvider(scheme, value) { providers.set(scheme, value); return disposable(); },
            async openTextDocument(uri) {
                opened.push(uri);
                const text = uri.scheme === 'msync-artifact' ? await providers.get(uri.scheme).provideTextDocumentContent(uri) : Buffer.from(files.get(uri.toString())).toString('utf8');
                return { uri, getText: () => text, positionAt(offset) { const prefix = text.slice(0, offset).split('\n'); return new Position(prefix.length - 1, prefix.at(-1).length); } };
            },
        },
        window: {
            registerCustomEditorProvider(type, provider, options) { custom.push({ type, provider, options }); return disposable(); },
            async showTextDocument(document, options) { this.activeTextEditor = { document }; opened.push({ document, options }); return this.activeTextEditor; },
            async showOpenDialog() { return []; },
            async showErrorMessage(message) { errors.push(message); },
            async showWarningMessage(message) { warnings.push(message); },
        },
    };
    const context = { subscriptions: [] };
    const calls = [];
    const watch = nativeWatch || ((directory, options, callback) => {
        const watcher = { directory, options, callback, listeners: new Map(),
            on(event, handler) { this.listeners.set(event, handler); return this; },
            close() { this.disposed = true; },
            change: { fire(uri) { callback('change', path.posix.basename(uri.path)); } },
        };
        nativeWatchers.push(watcher); return watcher;
    });
    const views = registerArtifactViews(host, context, async (parameters, token) => { calls.push({ parameters, token }); return request(parameters, token); }, watch);
    return { host, views, context, providers, commands, languageProviders, files, watchers, nativeWatchers, errors, warnings, opened, custom, calls, closed };
}

function panel() {
    const disposed = new Emitter(), messages = new Emitter();
    return { active: true, viewColumn: 1, disposed,
        onDidDispose: disposed.event,
        webview: { html: '', options: {}, onDidReceiveMessage: messages.event, messages, async postMessage() {} } };
}

test('virtual URI preserves Unicode, spaces, UNC and remote filesystem identities', () => {
    for (const value of [
        'file:///C:/Program%20Files/%E5%8F%AF%E8%8E%89.mso',
        'file://server/share/with%20space/file.mexe',
        'file:///C:/Program%20Files/%E5%8F%AF%E8%8E%89.mhir',
        'vscode-remote://ssh-remote+forge/home/klee/%E5%8F%AF%E8%8E%89.mso?query=1#part',
    ]) {
        const source = URI.parse(value);
        const virtual = textUri({ Uri: URI }, source);
        assert.equal(virtual.scheme, 'msync-artifact');
        assert.ok(virtual.path.endsWith('.msync-ir'));
        assert.equal(artifactUri({ Uri: URI }, virtual).toString(), source.toString());
    }
});

test('artifact views use the configured reader protocol, share one decode and never write bytes', async () => {
    const h = harness();
    const uri = URI.file('/outside workspace/可莉.mso');
    const bytes = Buffer.from([0, 255, 17, 1]); h.files.set(uri.toString(), bytes);
    const virtual = textUri(h.host, uri);
    const [left, right] = await Promise.all([h.views.provideTextDocumentContent(virtual), h.views.provideTextDocumentContent(virtual)]);
    assert.equal(left, fixture().text); assert.equal(left, right);
    assert.equal(h.calls.length, 1);
    assert.equal(h.calls[0].parameters.uri, uri.toString());
    assert.deepEqual(Buffer.from(h.calls[0].parameters.data, 'base64'), bytes);
    assert.deepEqual(h.files.get(uri.toString()), bytes);
    assert.equal(h.nativeWatchers[0].directory, '/outside workspace');
    assert.equal(h.watchers.length, 0, 'Local artifacts must not depend on asynchronous workspace watchers');
    assert.equal(h.languageProviders.get('symbols').selector.language, 'msync-ir');
    h.views.dispose();
});

test('typed HIR uses native semantic metadata without bytecode or source replay', async () => {
    const h = harness(async () => hirFixture());
    const uri = URI.file('/outside workspace/可莉.mhir');
    const original = Buffer.from([77, 72, 73, 82, 0, 255]);
    h.files.set(uri.toString(), original);
    const document = await h.views.openCustomDocument(uri), view = panel();
    await h.views.resolveCustomEditor(document, view);
    assert.match(view.webview.html, /Typed HIR · format 1/);
    assert.match(view.webview.html, /semantic representation; not recovered source/);
    assert.ok(!view.webview.html.includes('Oundefined'));
    assert.ok(!view.webview.html.includes('Typed bytecode'));
    assert.deepEqual(Buffer.from(h.calls[0].parameters.data, 'base64'), original);
    assert.deepEqual(h.files.get(uri.toString()), original);
    const virtual = textUri(h.host, uri);
    const text = await h.views.provideTextDocumentContent(virtual);
    assert.equal(text, hirFixture().text);
    assert.equal(h.calls.length, 1, 'Text and custom view share the semantic decode');
    const symbols = await h.views.provideDocumentSymbols({ uri: virtual });
    assert.equal(symbols[0].name, 'file');
    const hover = await h.views.provideHover({ uri: virtual }, new Position(5, 24));
    assert.match(hover.contents.value, /Location capture/);
    assert.equal(hover.contents.isTrusted, false);
    const definition = await h.views.provideDefinition({ uri: virtual }, new Position(5, 24));
    assert.equal(definition.uri.toString(), virtual.toString());
    assert.equal(definition.range.line ?? definition.range.start.line, 3);
    assert.equal(h.opened.length, 0, 'Deleted original source is not needed or opened for internal HIR links');
    view.disposed.fire(); document.dispose(); h.closed.fire({ uri: virtual });
    h.views.dispose();
});

test('typed HIR active-file text action refreshes after atomic replacement', async () => {
    const h = harness(async parameters => ({ ...hirFixture(), text: Buffer.from(parameters.data, 'base64').toString() }));
    const uri = URI.file('/build/job.MHIR');
    h.files.set(uri.toString(), Buffer.from('hir FIRST'));
    h.host.window.activeTextEditor = { document: { uri } };
    await h.commands.get('msync.openArtifactText')();
    assert.equal(h.host.window.activeTextEditor.document.getText(), 'hir FIRST');
    h.files.set(uri.toString(), Buffer.from('hir SECOND'));
    h.nativeWatchers[0].callback('rename', 'job.MHIR');
    assert.equal(await h.views.provideTextDocumentContent(textUri(h.host, uri)), 'hir SECOND');
    assert.equal(h.calls.length, 2);
    h.views.dispose();
});

test('semantic HIR webview distinguishes declarations, types, locations and structured control', () => {
    const model = hirFixture();
    model.text += '\ntemplate @g0 "copy"\n  @n2: Loop @n1 -> Bool';
    const html = renderPage(URI.file('/job.mhir'), model);
    for (const [role, value] of [
        ['keyword', 'hir'], ['keyword', 'binding'], ['path', '@s0'],
        ['type', '@t0'], ['type', 'Path'], ['local', '@l0'],
        ['function', '@f0'], ['function', '@g0'], ['block', '@n0'],
        ['opcode', 'CaptureLocation'], ['control', 'If'], ['control', 'Loop'],
    ]) assert.ok(html.includes(`<span class="${role}">${value}</span>`), `${role} ${value}`);
});

test('watching a Windows drive-root artifact retains the absolute volume root', async () => {
    const h = harness(); const uri = URI.parse('file:///D:/job.mso');
    h.files.set(uri.toString(), Buffer.from('binary'));
    await h.views.provideTextDocumentContent(textUri(h.host, uri));
    assert.equal(h.nativeWatchers[0].directory.toLowerCase(), URI.from({ scheme: 'file', path: '/d:/' }).fsPath);
    h.views.dispose();
});

test('local atomic publication uses one synchronous directory watch and filters sibling filenames', async () => {
    const h = harness(async parameters => fixture(Buffer.from(parameters.data, 'base64').toString()));
    const left = URI.file('/build/left.mso'), right = URI.file('/build/right.mexe');
    h.files.set(left.toString(), Buffer.from('LEFT')); h.files.set(right.toString(), Buffer.from('RIGHT'));
    await h.views.provideTextDocumentContent(textUri(h.host, left));
    await h.views.provideTextDocumentContent(textUri(h.host, right));
    assert.equal(h.nativeWatchers.length, 1); assert.equal(h.watchers.length, 0);
    const native = h.nativeWatchers[0];
    h.files.set(left.toString(), Buffer.from('REPLACED'));
    native.callback('rename', '.left.mso.msync-stage');
    assert.equal(await h.views.provideTextDocumentContent(textUri(h.host, left)), 'LEFT');
    native.callback('rename', 'left.mso');
    assert.equal(await h.views.provideTextDocumentContent(textUri(h.host, left)), 'REPLACED');
    assert.equal(await h.views.provideTextDocumentContent(textUri(h.host, right)), 'RIGHT');
    assert.equal(h.calls.length, 3);
    h.closed.fire({ uri: textUri(h.host, left) }); assert.notEqual(native.disposed, true);
    h.closed.fire({ uri: textUri(h.host, right) }); assert.equal(native.disposed, true);
    h.views.dispose();
});

test('missing native filenames refresh the owned directory and native errors explain provider fallback', async () => {
    const h = harness(); const uri = URI.file('/build/job.mso');
    h.files.set(uri.toString(), Buffer.from('native'));
    const virtual = textUri(h.host, uri); await h.views.provideTextDocumentContent(virtual);
    const native = h.nativeWatchers[0];
    native.callback('rename', null); await h.views.provideTextDocumentContent(virtual);
    assert.equal(h.calls.length, 2);
    native.listeners.get('error')(new Error('native notification failure'));
    assert.equal(native.disposed, true); assert.equal(h.watchers.length, 1);
    assert.match(h.warnings[0], /native notification failure.*VS Code.*Refresh/);
    h.watchers[0].change.fire(uri); await h.views.provideTextDocumentContent(virtual);
    assert.equal(h.calls.length, 3);
    h.views.dispose(); assert.equal(h.watchers[0].disposed, true);
});

test('native registration failure retains workspace notifications and actionable refresh', async () => {
    const h = harness(undefined, () => { throw new Error('watch service unavailable'); });
    const uri = URI.file('/build/job.mso'); h.files.set(uri.toString(), Buffer.from('native'));
    await h.views.provideTextDocumentContent(textUri(h.host, uri));
    assert.equal(h.nativeWatchers.length, 0); assert.equal(h.watchers.length, 1);
    assert.match(h.warnings[0], /watch service unavailable/);
    h.views.dispose();
});

test('default custom editor is read-only and has bytecode metadata, outline and text controls', async () => {
    const h = harness(); const uri = URI.file('/job.mexe'); h.files.set(uri.toString(), Buffer.from('binary'));
    const document = await h.views.openCustomDocument(uri), view = panel();
    await h.views.resolveCustomEditor(document, view);
    assert.equal(h.custom[0].type, 'msync.bytecode');
    assert.equal(h.custom[0].options.webviewOptions.enableFindWidget, true);
    assert.equal(h.custom[0].options.supportsMultipleEditorsPerDocument, true);
    assert.equal(typeof h.views.saveCustomDocument, 'undefined');
    assert.match(view.webview.html, /format 3/); assert.match(view.webview.html, /language 0.8.0/);
    assert.match(view.webview.html, /O2/); assert.match(view.webview.html, /Artifact outline/);
    assert.match(view.webview.html, /Open IR as Text/); assert.match(view.webview.html, /not recovered source/);
    await h.commands.get('msync.openArtifactText')(uri);
    assert.equal(h.host.window.activeTextEditor.document.uri.scheme, 'msync-artifact');
    view.disposed.fire(); document.dispose();
    h.closed.fire(h.host.window.activeTextEditor.document);
    assert.equal(h.nativeWatchers[0].disposed, true);
    h.views.dispose();
});

test('text providers expose structured symbols, untrusted Markdown hovers and internal definitions', async () => {
    const h = harness(); const uri = URI.file('/job.mso'); h.files.set(uri.toString(), Buffer.from('binary'));
    const virtual = textUri(h.host, uri); await h.views.provideTextDocumentContent(virtual);
    const document = { uri: virtual };
    const symbols = await h.views.provideDocumentSymbols(document);
    assert.equal(symbols[0].name, 'main'); assert.ok(symbols[0].selectionRange instanceof Range);
    const hover = await h.views.provideHover(document, new Position(1, 4));
    assert.equal(hover.contents.value, '**Function** `main`'); assert.equal(hover.contents.isTrusted, false);
    const definition = await h.views.provideDefinition(document, new Position(1, 4));
    assert.equal(definition.uri.toString(), virtual.toString()); assert.equal(definition.range.line, 1);
    h.views.dispose();
});

test('source navigation is explicit, uses UTF-16 offsets and never executes command URIs', async () => {
    const model = fixture();
    const target = URI.file('/source/可莉.msync');
    model.references = [
        { range: { start: { line: 1, character: 3 }, end: { line: 1, character: 6 } }, sourceUri: target.toString(), sourceStart: 4, sourceLength: 2 },
        { sourceUri: 'command:workbench.action.tasks.runTask?bad', sourceStart: 0, sourceLength: 1 },
    ];
    const h = harness(async () => model); const uri = URI.file('/job.mso'); h.files.set(uri.toString(), Buffer.from('binary'));
    h.files.set(target.toString(), Buffer.from('🌟x\n目标'));
    await h.views.provideTextDocumentContent(textUri(h.host, uri));
    assert.equal(h.opened.length, 0, 'Viewing bytecode must not open source eagerly');
    const entry = h.views.entry(uri);
    await h.views.followReference(entry, 0, 1);
    assert.equal(h.opened.at(-1).options.selection.start.line, 1);
    assert.equal(h.opened.at(-1).options.selection.start.character, 0);
    const count = h.opened.length; await h.views.followReference(entry, 1, 1);
    assert.equal(h.opened.length, count);
    h.views.dispose();
});

test('corrupt artifact error remains readable and repaired file refreshes without reopening', async () => {
    let valid = false;
    const h = harness(async () => { if (!valid) throw new Error('Unsupported bytecode format 999'); return fixture(); });
    const uri = URI.file('/job.mso'); h.files.set(uri.toString(), Buffer.from('broken'));
    const virtual = textUri(h.host, uri);
    const error = await h.views.provideTextDocumentContent(virtual);
    assert.match(error, /Unsupported bytecode format 999/); assert.match(error, /not been modified or executed/);
    valid = true; h.files.set(uri.toString(), Buffer.from('repaired')); h.nativeWatchers[0].change.fire(uri);
    assert.equal(await h.views.provideTextDocumentContent(virtual), fixture().text);
    assert.equal(h.calls.length, 2);
    h.views.dispose();
});

test('rewrite cancels and discards an older decode while retaining the newest generation', async () => {
    let complete;
    const h = harness(async () => h.calls.length === 1 ? new Promise(resolve => { complete = resolve; }) : fixture('new'));
    const uri = URI.file('/job.mso'); h.files.set(uri.toString(), Buffer.from('old'));
    const virtual = textUri(h.host, uri);
    const first = h.views.provideTextDocumentContent(virtual);
    while (!complete) await Promise.resolve();
    h.files.set(uri.toString(), Buffer.from('new')); h.nativeWatchers[0].change.fire(uri);
    assert.equal(h.calls[0].token.isCancellationRequested, true);
    complete(fixture('stale'));
    assert.equal(await first, 'new');
    assert.equal(await h.views.provideTextDocumentContent(virtual), 'new');
    h.views.dispose();
});

test('server restart invalidates cached readers while keeping the original artifact identity', async () => {
    const h = harness(); const uri = URI.parse('vscode-remote://ssh-remote+forge/home/a.mexe');
    h.files.set(uri.toString(), Buffer.from('native'));
    const virtual = textUri(h.host, uri); await h.views.provideTextDocumentContent(virtual);
    h.views.serverChanged(); await h.views.provideTextDocumentContent(virtual);
    assert.equal(h.calls.length, 2); assert.equal(h.calls[1].parameters.uri, uri.toString());
    assert.equal(h.watchers[0].pattern.baseUri.scheme, 'vscode-remote');
    h.views.dispose();
});

test('cancelled view request is not turned into a persistent bytecode error', async () => {
    let complete;
    const h = harness(async () => new Promise(resolve => { complete = resolve; }));
    const uri = URI.file('/job.mso'); h.files.set(uri.toString(), Buffer.from('binary'));
    const cancellation = new Cancellation();
    const pending = h.views.provideTextDocumentContent(textUri(h.host, uri), cancellation.token);
    while (!complete) await Promise.resolve();
    cancellation.cancel(); await assert.rejects(pending, /Cancelled/);
    complete(fixture());
    assert.equal(await h.views.provideTextDocumentContent(textUri(h.host, uri)), fixture().text);
    h.views.dispose();
});

test('webview treats hostile bytecode/debug data as inert escaped text', () => {
    const model = fixture('const %c0: String = "</script><script>globalThis.pwned=1</script>"\n\u0001 command:bad');
    model.symbols[0].name = '<img src=x onerror="pwned()">';
    model.hovers[0].markdown = '" onmouseover="pwned()';
    const html = renderPage(URI.file('/<script>.mso'), model);
    assert.ok(!html.includes('<img src=x')); assert.ok(!html.includes('<script>globalThis.pwned'));
    assert.match(html, /&lt;script&gt;/); assert.ok(!html.includes('href="command:'));
    assert.match(html, /default-src 'none'/); assert.match(html, /script-src 'nonce-/);
    assert.match(html, /read-only; not recovered source/);
});

test('manifest offers binary opening without routing IR through source diagnostics', () => {
    const manifest = require('../package.json');
    assert.equal(manifest.version, require('../package-lock.json').version);
    const editor = manifest.contributes.customEditors.find(item => item.viewType === 'msync.bytecode');
    assert.equal(editor.priority, 'default');
    assert.ok(editor.selector.some(item => item.filenamePattern === '*.mhir'));
    assert.ok(editor.selector.some(item => item.filenamePattern === '*.MHIR'));
    assert.ok(manifest.contributes.languages.some(item => item.id === 'msync-ir'));
    assert.ok(manifest.activationEvents.includes('onCustomEditor:msync.bytecode'));
    assert.ok(manifest.contributes.commands.some(item => item.command === 'msync.refreshArtifact'));
});

test('IR grammar colors real labels and future instruction mnemonics without source-language parsing', async () => {
    const textmate = require('vscode-textmate'), oniguruma = require('vscode-oniguruma');
    const wasm = fs.readFileSync(require.resolve('vscode-oniguruma/release/onig.wasm'));
    await oniguruma.loadWASM(wasm.buffer.slice(wasm.byteOffset, wasm.byteOffset + wasm.byteLength));
    const registry = new textmate.Registry({
        onigLib: Promise.resolve({ createOnigScanner: patterns => new oniguruma.OnigScanner(patterns), createOnigString: value => new oniguruma.OnigString(value) }),
        loadGrammar: async () => JSON.parse(fs.readFileSync(path.join(__dirname, '../syntaxes/msync-ir.tmLanguage.json'), 'utf8')),
    });
    const grammar = await registry.loadGrammar('source.msync-ir');
    for (const [line, token, expected] of [
        ['const @c0: Path = path(raw="./可莉")', '@c0', 'constant.other.reference.msync-ir'],
        ['fn @f0 "main"(%0: Path) -> Bool', '@f0', 'entity.name.function.msync-ir'],
        ['block @b0_0000 [0..4) pure successors []:', '@b0_0000', 'entity.name.label.msync-ir'],
        ['  0042: JumpIfFalse @b0_0000', 'JumpIfFalse', 'keyword.control.msync-ir'],
        ['  0042: Barrier before-and-after (acqrel)', 'acqrel', 'keyword.control.msync-ir'],
        ['  0042: Acquire session-owned pending work', 'Acquire', 'keyword.control.msync-ir'],
        ['  0042: ProcessCall arguments 0, release flags 1', 'release', 'keyword.control.msync-ir'],
        ['  0042: FutureFieldOpcode @c0 %2', 'FutureFieldOpcode', 'keyword.operator.msync-ir'],
        ['  0042: FutureFieldOpcode @c0 %2', '%2', 'variable.other.msync-ir'],
        ...['Modulo', 'LoadSingleton', 'StoreSingleton'].map(opcode => [`  0042: ${opcode} 1`, opcode, 'keyword.operator.msync-ir']),
        ['  0042: GetMember "filename", slot #2 (layout-checked hint)', '"filename"', 'variable.other.property.msync-ir'],
        ['  0042: GetMember "filename", slot #2 (layout-checked hint)', 'slot', 'variable.other.property.layout.msync-ir'],
        ['  0042: GetMember "filename", slot #2 (layout-checked hint)', 'layout-checked', 'variable.other.property.layout.msync-ir'],
        ['  0042: MakeScope fields ["filename", ".codex"], allocation-site 7', '".codex"', 'variable.other.property.msync-ir'],
        ['  0042: MakeScope fields ["filename", ".codex"], allocation-site 7', 'allocation-site', 'variable.other.property.layout.msync-ir'],
        ['const @c0: Path = path(raw="./可莉")', 'path', 'support.type.path.msync-ir'],
        ['const @c0: Path = path(raw="./可莉")', 'Path', 'entity.name.type.msync-ir'],
        ['const @c0: Path = path(raw="./可莉")', './可莉', 'string.quoted.double.msync-ir'],
        ['const @c0: Number = number(-0, bits=0x8000000000000000)', '-0', 'constant.numeric.msync-ir'],
        ['const @c0: Number = number(-0, bits=0x8000000000000000)', '0x8000000000000000', 'constant.numeric.msync-ir'],
        ['const @c0: Number = number(1.2e-7)', '1.2e-7', 'constant.numeric.msync-ir'],
        ['const @c0: Number = number(-Infinity)', '-Infinity', 'constant.numeric.msync-ir'],
        ['const @c0: Number = number(NaN)', 'NaN', 'constant.numeric.msync-ir'],
        ['const @c0: Bool = true', 'true', 'constant.language.msync-ir'],
        ['const @c0: String = "hello; world"', 'world', 'string.quoted.double.msync-ir'],
        ['type @t0 = Path', '@t0', 'entity.name.type.msync-ir'],
        ['binding @l0 "file" : @t0 mutable', '@l0', 'variable.other.msync-ir'],
        ['source @s0 "file.msync"', '@s0', 'support.type.path.msync-ir'],
        ['template @g0 "copy"', '@g0', 'entity.name.function.msync-ir'],
        ['session_dispatch @d0 (@t0) -> @t0', 'session_dispatch', 'keyword.declaration.msync-ir'],
        ['session_dispatch @d0 (@t0) -> @t0', '@d0', 'entity.name.function.msync-ir'],
        ['  @n2: CaptureLocation @l0 : @t0', '@n2', 'entity.name.label.msync-ir'],
        ['  @n2: CaptureLocation @l0 : @t0', 'CaptureLocation', 'keyword.operator.msync-ir'],
        ['  @n3: If @n2 -> Bool', 'If', 'keyword.control.msync-ir'],
        ['  @n3: Match @n2 -> Bool', 'Match', 'keyword.control.msync-ir'],
        ['  @n4: DeferRange @f0 -> Range', 'DeferRange', 'keyword.control.msync-ir'],
    ]) {
        const start = line.indexOf(token);
        const actual = grammar.tokenizeLine(line, textmate.INITIAL).tokens.find(item => item.startIndex <= start && item.endIndex > start);
        assert.ok(actual.scopes.includes(expected), JSON.stringify({ line, token, actual }));
    }
    registry.dispose();
});

test('both binary webviews color separate IR roles through current-theme variables', () => {
    const text = [
        'const @c0: Path = path(raw="./可莉", cwd="/home/klee")',
        'const @c1: Number = number(-0, bits=0x8000000000000000)',
        'const @c2: Bool = true',
        'const @c3: String = "color:#ff0000"',
        'fn @f0 "main"(%0: Path) -> Bool captures 0 locals 1 {',
        'block @b0_0000 [0..4) effects successors [@b0_0042]:',
        '  0000: LoadLocal %0',
        '  0001: GetMember "filename", slot #2 (layout-checked hint)',
        '  0002: MakeScope fields [".codex", "host"], allocation-site 7',
        '  0003: JumpIfFalse @b0_0042',
        '  0004: Barrier before-and-after (acqrel)',
        '  0005: ProcessCall arguments 0, release flags 1',
        '  0006: Return',
        '}',
    ].join('\n');
    for (const [extension, stage] of [['mso', 'relocatable'], ['mexe', 'executable']]) {
        const model = { ...fixture(text), stage, symbols: [], references: [], hovers: [] };
        const html = renderPage(URI.file('/job.' + extension), model);
        for (const [role, value] of [
            ['function', '@f0'], ['constant', '@c0'], ['block', '@b0_0000'], ['local', '%0'],
            ['type', 'Path'], ['path', 'path'], ['string', '&quot;./可莉&quot;'],
            ['number', '-0'], ['number', '0x8000000000000000'], ['literal', 'true'],
            ['opcode', 'LoadLocal'], ['control', 'JumpIfFalse'], ['control', 'Barrier'], ['control', 'Return'],
            ['control', 'acqrel'], ['control', 'release'], ['field', '&quot;filename&quot;'], ['field', '&quot;.codex&quot;'],
            ['layout', 'slot'], ['layout', 'layout-checked'], ['layout', 'allocation-site'],
        ]) assert.ok(html.includes(`<span class="${role}">${value}</span>`), `${extension}: ${role} ${value}`);
        for (const name of ['keyword', 'operator', 'function', 'constant', 'reference', 'variable', 'property', 'number'])
            assert.ok(html.includes(`var(--vscode-symbolIcon-${name}Foreground,`), name);
        const styles = /<style[^>]*>([\s\S]*?)<\/style>/.exec(html)[1];
        assert.ok(!/color:\s*#[0-9a-f]{3,8}\b/i.test(styles), 'Syntax colors must not hard-code a palette');
    }
});
