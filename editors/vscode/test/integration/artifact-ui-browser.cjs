'use strict';

const { setTimeout: delay } = require('node:timers/promises');

async function until(description, read) {
    const deadline = Date.now() + 20_000;
    let lastError;
    do {
        try {
            const value = await read();
            if (value) return value;
        } catch (error) { lastError = error; }
        await delay(80);
    } while (Date.now() < deadline);
    throw new Error(`Timed out waiting for ${description}${lastError ? ': ' + lastError : ''}`);
}

class Browser {
    constructor(socket) {
        this.socket = socket;
        this.id = 0;
        this.requests = new Map();
        this.targets = new Map();
        this.contexts = new Map();
        socket.addEventListener('message', event => this.received(JSON.parse(event.data)));
    }

    received(message) {
        if (message.id) {
            const request = this.requests.get(message.id);
            if (!request) return;
            this.requests.delete(message.id);
            clearTimeout(request.timer);
            if (message.error) request.reject(new Error(message.error.message));
            else request.resolve(message.result);
            return;
        }
        const contexts = this.contexts.get(message.sessionId);
        if (!contexts) return;
        if (message.method === 'Runtime.executionContextCreated')
            contexts.add(message.params.context.id);
        if (message.method === 'Runtime.executionContextDestroyed')
            contexts.delete(message.params.executionContextId);
        if (message.method === 'Runtime.executionContextsCleared') contexts.clear();
    }

    send(method, params = {}, sessionId) {
        return new Promise((resolve, reject) => {
            const id = ++this.id;
            const timer = setTimeout(() => { this.requests.delete(id); reject(new Error(`Browser command ${method} timed out`)); }, 10_000);
            this.requests.set(id, { resolve, reject, timer });
            this.socket.send(JSON.stringify({ id, method, params, sessionId }));
        });
    }

    async refresh() {
        const { targetInfos } = await this.send('Target.getTargets');
        const seen = new Set();
        for (const target of targetInfos) {
            if (!['page', 'iframe'].includes(target.type)) continue;
            seen.add(target.targetId);
            if (!this.targets.has(target.targetId)) {
                const { sessionId } = await this.send('Target.attachToTarget', { targetId: target.targetId, flatten: true });
                this.targets.set(target.targetId, { sessionId, type: target.type, url: target.url });
                this.contexts.set(sessionId, new Set());
                await this.send('Runtime.enable', {}, sessionId);
            }
        }
        for (const [id, target] of this.targets) {
            if (seen.has(id)) continue;
            this.targets.delete(id);
            this.contexts.delete(target.sessionId);
        }
    }

    async evaluate(frame, expression) {
        const { result, exceptionDetails } = await this.send('Runtime.evaluate', {
            expression, contextId: frame.contextId, returnByValue: true, awaitPromise: true,
        }, frame.sessionId);
        if (exceptionDetails) throw new Error(exceptionDetails.exception?.description || exceptionDetails.text);
        return result.value;
    }

    async artifact(name, predicate = 'true') {
        return until(name + ' webview', async () => {
            await this.refresh();
            for (const [sessionId, contexts] of this.contexts) {
                for (const contextId of contexts) {
                    const frame = { sessionId, contextId };
                    try {
                        const found = await this.evaluate(frame,
                            `document.querySelector('h1')?.textContent === ${JSON.stringify(name)} &&
                             !!document.querySelector('#code .line.selected') && (${predicate})`);
                        if (found) return frame;
                    } catch { /* A replaced iframe may disappear between enumeration and inspection. */ }
                }
            }
        });
    }

    async screenshot() {
        await this.refresh();
        const workbench = [...this.targets.values()].find(target => target.type === 'page' && target.url.includes('workbench'));
        const { data } = await this.send('Page.captureScreenshot', { format: 'png' }, workbench.sessionId);
        return Buffer.from(data, 'base64');
    }

    dispose() { this.socket.close(); }
}

async function connect(port) {
    const version = await until('isolated Electron debugger', async () => {
        const response = await fetch(`http://127.0.0.1:${port}/json/version`);
        if (response.ok) return response.json();
    });
    const socket = new WebSocket(version.webSocketDebuggerUrl);
    await new Promise((resolve, reject) => {
        socket.addEventListener('open', resolve, { once: true });
        socket.addEventListener('error', reject, { once: true });
    });
    return new Browser(socket);
}

module.exports = { connect, until };
