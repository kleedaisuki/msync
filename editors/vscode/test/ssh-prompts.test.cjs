'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');
const { registerSshPrompts } = require('../ssh-prompts.cjs');

function harness() {
    const requests = new Map();
    const notifications = new Map();
    const shown = [];
    const sources = [];
    const notices = [];
    class CancellationTokenSource {
        constructor() {
            this.token = { isCancellationRequested: false };
            this.listeners = [];
            this.token.onCancellationRequested = callback => {
                this.listeners.push(callback);
                return { dispose() {} };
            };
            sources.push(this);
        }
        cancel() {
            this.token.isCancellationRequested = true;
            for (const callback of this.listeners) callback();
        }
        dispose() { this.disposed = true; }
    }
    function display(kind, options, token, items) {
        return new Promise(resolve => {
            shown.push({ kind, options, token, items, resolve });
            token.onCancellationRequested(() => resolve(undefined));
        });
    }
    const vscode = {
        CancellationTokenSource,
        window: {
            showInformationMessage: message => { notices.push(message); return Promise.resolve(); },
            showInputBox: (options, token) => display('input', options, token),
            showQuickPick: (items, options, token) => display('confirm', options, token, items),
        },
    };
    const client = {
        onRequest(method, callback) {
            requests.set(method, callback);
            return { dispose() { requests.delete(method); } };
        },
        onNotification(method, callback) {
            notifications.set(method, callback);
            return { dispose() { notifications.delete(method); } };
        },
    };
    const registration = registerSshPrompts(vscode, client);
    return {
        shown, sources, notices, registration,
        request: parameters => requests.get('msync/sshPrompt')(parameters),
        cancel: id => notifications.get('msync/sshPromptCancel')({ id }),
        requests, notifications,
    };
}

const prompt = (id, kind = 'secret') => ({ id, kind, endpoint: 'ssh://forge', prompt: 'Native SSH prompt: ' });
const tick = async () => { await Promise.resolve(); await Promise.resolve(); };

test('secret prompt keeps native wording and returns only the supplied value', async () => {
    const h = harness();
    const answer = h.request(prompt('one'));
    await tick();
    assert.equal(h.shown.length, 1);
    assert.deepEqual(h.shown[0].options, {
        title: 'SSH: ssh://forge', prompt: 'Native SSH prompt: ', password: true, ignoreFocusOut: true,
    });
    h.shown[0].resolve('entered value');
    assert.deepEqual(await answer, { value: 'entered value' });
    assert.equal(h.sources[0].disposed, true);
    h.registration.dispose();
});

test('host key verification offers exact native prompt and explicit yes or no', async () => {
    const h = harness();
    const answer = h.request(prompt('host', 'confirm'));
    await tick();
    assert.equal(h.shown[0].kind, 'confirm');
    assert.equal(h.shown[0].options.placeHolder, 'Native SSH prompt: ');
    assert.equal(h.shown[0].items[0].detail, 'Native SSH prompt: ');
    h.shown[0].resolve(h.shown[0].items[1]);
    assert.deepEqual(await answer, { value: 'no' });
    h.registration.dispose();
});

test('empty supplied response differs from dismissing the prompt', async () => {
    const h = harness();
    const empty = h.request(prompt('empty', 'text'));
    await tick();
    assert.equal(h.shown[0].options.password, false);
    h.shown[0].resolve('');
    assert.deepEqual(await empty, { value: '' });
    const dismissed = h.request(prompt('dismiss'));
    await tick();
    h.shown[1].resolve(undefined);
    assert.deepEqual(await dismissed, { value: null });
    h.registration.dispose();
});

test('concurrent endpoint prompts serialize without consuming each other', async () => {
    const h = harness();
    const first = h.request(prompt('first'));
    const second = h.request({ ...prompt('second'), endpoint: 'ssh://other' });
    await tick();
    assert.equal(h.shown.length, 1);
    h.shown[0].resolve('first answer');
    assert.deepEqual(await first, { value: 'first answer' });
    await tick();
    assert.equal(h.shown.length, 2);
    assert.equal(h.shown[1].options.title, 'SSH: ssh://other');
    h.shown[1].resolve('second answer');
    assert.deepEqual(await second, { value: 'second answer' });
    h.registration.dispose();
});

test('server cancellation closes active UI and skips cancelled queued UI', async () => {
    const h = harness();
    const first = h.request(prompt('first'));
    const second = h.request(prompt('second'));
    await tick();
    h.cancel('second');
    h.cancel('first');
    assert.deepEqual(await first, { value: null });
    assert.deepEqual(await second, { value: null });
    assert.equal(h.shown.length, 1);
    assert.ok(h.sources.every(source => source.disposed));
    h.registration.dispose();
});

test('restart disposal retires all prompt UI and unregisters old handlers', async () => {
    const h = harness();
    const first = h.request(prompt('first'));
    const second = h.request(prompt('second'));
    await tick();
    h.registration.dispose();
    assert.deepEqual(await first, { value: null });
    assert.deepEqual(await second, { value: null });
    assert.equal(h.requests.size, 0);
    assert.equal(h.notifications.size, 0);
    assert.equal(h.shown.length, 1);
});


test('security-key notice remains visible without blocking native authentication', async () => {
    const h = harness();
    assert.deepEqual(await h.request(prompt('touch', 'notice')), { value: '' });
    assert.deepEqual(h.notices, ['SSH: ssh://forge — Native SSH prompt: ']);
    assert.equal(h.shown.length, 0);
    h.registration.dispose();
});
