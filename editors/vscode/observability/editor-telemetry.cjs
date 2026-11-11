'use strict';

const path = require('node:path');

const namespace = 'msync.telemetry';
const levels = ['summary', 'phases', 'profile', 'instructions', 'diagnostics'];

function telemetryConfigurationChanged(event) {
    return ['enabled', 'level', 'database'].some(key => event.affectsConfiguration(`${namespace}.${key}`));
}

async function prepareTelemetry(vscode, storage) {
    const configuration = vscode.workspace.getConfiguration(namespace);
    if (configuration.get('enabled', false) !== true) return undefined;
    const level = configuration.get('level', 'phases');
    const configured = configuration.get('database', '');
    if (!levels.includes(level)) throw new Error(`${namespace}.level is not a supported detail level.`);
    if (typeof configured !== 'string' || (configured !== '' && !path.isAbsolute(configured))) {
        throw new Error(`${namespace}.database must be an absolute path on the extension host.`);
    }

    let database = configured;
    if (!database) {
        if (storage.scheme !== 'file') throw new Error('The msync telemetry database needs filesystem storage on the extension host.');
        await vscode.workspace.fs.createDirectory(storage);
        database = path.join(storage.fsPath, 'editor-telemetry.sqlite');
    }
    return new EditorTelemetry(database, level);
}

class EditorTelemetry {
    constructor(database, level) {
        this.serverArguments = ['--telemetry-db', database, '--telemetry-level', level];
    }

    middleware(client) {
        return {
            provideHover: (document, position, token, next) =>
                this.measure(client(), 'hover', () => next(document, position, token), token),
            provideCompletionItem: (document, position, context, token, next) =>
                this.measure(client(), 'completion', () => next(document, position, context, token), token),
        };
    }

    async measure(client, operation, action, token) {
        const start = process.hrtime.bigint();
        let outcome = 'ok';
        try {
            return await action();
        } catch (error) {
            outcome = token?.isCancellationRequested || error?.code === -32800 ? 'cancelled' : 'error';
            throw error;
        } finally {
            // Report only over the originating connection. Its shutdown may
            // race a request's completion; observation must not mask its result.
            try {
                void client.sendNotification('msync/observeClient', {
                    operation,
                    durationNanoseconds: Number(process.hrtime.bigint() - start),
                    outcome: outcome === 'ok' && token?.isCancellationRequested ? 'cancelled' : outcome,
                }).catch(() => {});
            } catch { /* The connection is no longer available. */ }
        }
    }
}

module.exports = { prepareTelemetry, telemetryConfigurationChanged };
