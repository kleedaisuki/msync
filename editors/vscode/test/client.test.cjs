'use strict';

const assert = require('node:assert/strict');
const Module = require('node:module');
const path = require('node:path');
const { test } = require('node:test');

function harness(command = 'msync', startError) {
    const errors = [];
    const created = [];
    const providers = [];
    const commands = new Map();
    const executed = [];
    const configurationListeners = [];
    let selectedAction;
    class Client {
        constructor(...args) {
            this.args = args;
            this.started = 0;
            this.stopped = 0;
            this.requests = [];
            created.push(this);
        }
        async start() {
            this.started += 1;
            if (startError) throw startError;
        }
        async stop() { this.stopped += 1; }
        async dispose() { await this.stop(); }
        async sendRequest(method, params) { this.requests.push(method); return `${method}:${params.uri}`; }
    }
    const host = {
        commands: {
            registerCommand(name, callback) { commands.set(name, callback); return { dispose() {} }; },
            executeCommand(...args) { executed.push(args); return Promise.resolve(); },
        },
        workspace: {
            onDidChangeConfiguration(callback) { configurationListeners.push(callback); return { dispose() {} }; },
            registerTextDocumentContentProvider(scheme, provider) {
                const registration = { scheme, provider, dispose() {} };
                providers.push(registration);
                return registration;
            },
            getConfiguration: section => {
            assert.equal(section, 'msync');
            return { get: (key, fallback) => {
                assert.equal(key, 'serverPath');
                assert.equal(fallback, 'msync');
                return command;
            } };
        } },
        window: { showErrorMessage: (message, action) => {
            errors.push(message);
            assert.equal(action, 'Open Settings');
            return Promise.resolve(selectedAction);
        } },
    };
    const filename = path.resolve(__dirname, '../extension.cjs');
    delete require.cache[filename];
    const originalLoad = Module._load;
    Module._load = function(request, parent, main) {
        if (request === 'vscode') return host;
        if (request === 'vscode-languageclient/node') return { LanguageClient: Client };
        return originalLoad.call(this, request, parent, main);
    };
    let extension;
    try { extension = require(filename); } finally { Module._load = originalLoad; }
    return { extension, errors, created, providers, commands, executed, configurationListeners,
        setCommand(value) { command = value; },
        selectSettingsAction() { selectedAction = 'Open Settings'; },
        context: { subscriptions: [] } };
}

test('activation launches stdio client without shell and registers user/library documents', async () => {
    const { extension, errors, created, context } = harness('/path with spaces/msync');
    await extension.activate(context);
    assert.equal(created.length, 1);
    const client = created[0];
    assert.deepEqual(client.args[2], {
        command: '/path with spaces/msync', args: ['--language-server'], options: { shell: false },
    });
    assert.deepEqual(client.args[3].documentSelector, [
        { scheme: 'file', language: 'msync' }, { scheme: 'untitled', language: 'msync' },
        { scheme: 'msync-stdlib', language: 'msync' },
    ]);
    assert.equal(context.subscriptions.length, 3);
    assert.equal(client.started, 1);
    assert.equal(errors.length, 0);
    await extension.deactivate();
    await extension.deactivate();
    assert.equal(client.stopped, 1);
});

test('server startup failure gives actionable diagnostics and preserves grammar extension', async () => {
    const { extension, errors, created, context } = harness('missing-msync', new Error('spawn ENOENT'));
    await extension.activate(context);
    assert.equal(created.length, 1);
    assert.equal(errors.length, 1);
    assert.match(errors[0], /ENOENT/);
    assert.match(errors[0], /msync\.serverPath/);
    assert.match(errors[0], /Syntax highlighting remains available/);
    await extension.deactivate();
});

test('empty server command is diagnosed rather than shell-invoked', async () => {
    const { extension, errors, created, context } = harness('  ');
    await extension.activate(context);
    assert.equal(created.length, 0);
    assert.equal(errors.length, 1);
    assert.match(errors[0], /serverPath is empty/);
    await extension.deactivate();
});

test('manually malformed server setting is diagnosed without throwing during activation', async () => {
    const { extension, errors, created, context } = harness(42);
    await extension.activate(context);
    assert.equal(created.length, 0);
    assert.equal(errors.length, 1);
    assert.match(errors[0], /not a string/);
    await extension.deactivate();
});

test('stdlib definition documents request source from the running server', async () => {
    const { extension, providers, context } = harness();
    await extension.activate(context);
    assert.equal(providers.length, 1);
    assert.equal(providers[0].scheme, 'msync-stdlib');
    const text = await providers[0].provider.provideTextDocumentContent({ toString: () => 'msync-stdlib:///0.7.0/fs.msync' });
    assert.equal(text, 'msync/stdlibSource:msync-stdlib:///0.7.0/fs.msync');
    assert.equal(context.subscriptions[2], providers[0]);
    await extension.deactivate();
});


test('explicit restart replaces the client without duplicating library document providers', async () => {
    const { extension, created, providers, commands, context } = harness();
    await extension.activate(context);
    await commands.get('msync.restartLanguageServer')();
    assert.equal(created.length, 2);
    assert.equal(created[0].stopped, 1);
    assert.equal(created[1].started, 1);
    assert.equal(providers.length, 1);
    await providers[0].provider.provideTextDocumentContent({ toString: () => 'msync-stdlib:///0.7.2/fs.msync' });
    assert.equal(created[0].requests.length, 0);
    assert.equal(created[1].requests.length, 1);
    await extension.deactivate();
    assert.equal(created[1].stopped, 1);
});

test('configuration changes serialize restarts and read the new executable path', async () => {
    const { extension, created, commands, configurationListeners, setCommand, context } = harness();
    await extension.activate(context);
    setCommand('/new path/msync');
    configurationListeners[0]({ affectsConfiguration: name => name === 'msync.serverPath' });
    await commands.get('msync.restartLanguageServer')();
    assert.equal(created.at(-1).args[2].command, '/new path/msync');
    for (const client of created.slice(0, -1)) assert.equal(client.stopped, 1);
    const beforeUnrelatedChange = created.length;
    configurationListeners[0]({ affectsConfiguration: () => false });
    await Promise.resolve();
    assert.equal(created.length, beforeUnrelatedChange);
    await extension.deactivate();
});

test('startup error settings action and corrected configuration recover without window reload', async () => {
    const h = harness('');
    h.selectSettingsAction();
    await h.extension.activate(h.context);
    await Promise.resolve();
    assert.deepEqual(h.executed, [['workbench.action.openSettings', 'msync.serverPath']]);
    h.setCommand('msync');
    await h.commands.get('msync.restartLanguageServer')();
    assert.equal(h.created.length, 1);
    assert.equal(h.created[0].started, 1);
    await h.extension.deactivate();
});
