'use strict';

const vscode = require('vscode');
const { LanguageClient } = require('vscode-languageclient/node');
const { registerSshPrompts } = require('./ssh-prompts.cjs');
const { registerArtifactViews } = require('./artifact-viewer.cjs');

let client;
let sshPrompts;
let artifactViews;
let enabled = false;
let lifecycle = Promise.resolve();

function serialize(action) {
    const next = lifecycle.then(action, action);
    lifecycle = next;
    return next;
}

function startupMessage(message) {
    void vscode.window.showErrorMessage(message, 'Open Settings').then(action => {
        if (action === 'Open Settings') {
            return vscode.commands.executeCommand('workbench.action.openSettings', 'msync.serverPath');
        }
    });
}

async function stop() {
    const running = client;
    client = undefined;
    sshPrompts?.dispose();
    sshPrompts = undefined;
    if (running) await running.dispose();
}

async function start() {
    const configured = vscode.workspace.getConfiguration('msync').get('serverPath', 'msync');
    const command = typeof configured === 'string' ? configured.trim() : '';
    if (!command) {
        startupMessage('msync.serverPath is empty or not a string. Set it to the msync executable to enable language services.');
        return;
    }
    const candidate = new LanguageClient('msync', 'msync Language Server', {
        command,
        args: ['--language-server'],
        options: { shell: false },
    }, {
        documentSelector: [
            { scheme: 'file', language: 'msync' },
            { scheme: 'untitled', language: 'msync' },
            { scheme: 'msync-stdlib', language: 'msync' },
        ],
        outputChannelName: 'msync Language Server',
        initializationOptions: { sshPrompt: true },
    });
    const prompts = registerSshPrompts(vscode, candidate);
    try {
        await candidate.start();
        client = candidate;
        sshPrompts = prompts;
    } catch (error) {
        prompts.dispose();
        await candidate.dispose();
        const detail = error instanceof Error ? error.message : String(error);
        startupMessage(`Cannot start msync language services: ${detail}. Install msync or configure msync.serverPath, then use msync: Restart Language Server. Syntax highlighting remains available.`);
    }
}

function restart() {
    return serialize(async () => {
        if (!enabled) return;
        await stop();
        await start();
        artifactViews?.serverChanged();
    });
}

async function activate(context) {
    enabled = true;
    context.subscriptions.push(
        vscode.commands.registerCommand('msync.restartLanguageServer', restart),
        vscode.workspace.onDidChangeConfiguration(event => {
            if (event.affectsConfiguration('msync.serverPath')) {
                void restart().catch(error => startupMessage(`Cannot restart msync language services: ${error.message}`));
            }
        }),
        vscode.workspace.registerTextDocumentContentProvider('msync-stdlib', {
            provideTextDocumentContent(uri, token) {
                if (!client) throw new Error('msync language server is not running. Use msync: Restart Language Server.');
                return client.sendRequest('msync/stdlibSource', { uri: uri.toString() }, token);
            },
        }),
    );
    artifactViews = registerArtifactViews(vscode, context, async (parameters, token) => {
        await lifecycle;
        if (!client) throw new Error('msync language server is not running. Configure msync.serverPath, then use msync: Restart Language Server.');
        return client.sendRequest('msync/artifact', parameters, token);
    });
    await restart();
}

function deactivate() {
    enabled = false;
    artifactViews?.dispose();
    artifactViews = undefined;
    return serialize(stop);
}

module.exports = { activate, deactivate };
