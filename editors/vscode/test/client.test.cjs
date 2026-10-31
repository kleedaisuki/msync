'use strict';

const assert = require('node:assert/strict');
const Module = require('node:module');
const path = require('node:path');
const { test } = require('node:test');

function harness(command = 'msync', startError) {
    const errors = [];
    const created = [];
    class Client {
        constructor(...args) {
            this.args = args;
            this.started = 0;
            this.stopped = 0;
            created.push(this);
        }
        async start() {
            this.started += 1;
            if (startError) throw startError;
        }
        async stop() { this.stopped += 1; }
    }
    const host = {
        workspace: { getConfiguration: section => {
            assert.equal(section, 'msync');
            return { get: (key, fallback) => {
                assert.equal(key, 'serverPath');
                assert.equal(fallback, 'msync');
                return command;
            } };
        } },
        window: { showErrorMessage: message => { errors.push(message); return Promise.resolve(); } },
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
    return { extension, errors, created, context: { subscriptions: [] } };
}

test('activation launches stdio client without shell and registers file/untitled documents', async () => {
    const { extension, errors, created, context } = harness('/path with spaces/msync');
    await extension.activate(context);
    assert.equal(created.length, 1);
    const client = created[0];
    assert.deepEqual(client.args[2], {
        command: '/path with spaces/msync', args: ['--language-server'], options: { shell: false },
    });
    assert.deepEqual(client.args[3].documentSelector, [
        { scheme: 'file', language: 'msync' }, { scheme: 'untitled', language: 'msync' },
    ]);
    assert.equal(context.subscriptions[0], client);
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
