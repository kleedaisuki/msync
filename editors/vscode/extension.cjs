'use strict';

const vscode = require('vscode');
const { LanguageClient } = require('vscode-languageclient/node');

let client;

async function activate(context) {
    const configured = vscode.workspace.getConfiguration('msync').get('serverPath', 'msync');
    const command = typeof configured === 'string' ? configured.trim() : '';
    if (!command) {
        void vscode.window.showErrorMessage('msync.serverPath is empty or not a string. Set it to the msync 0.6+ executable to enable language services.');
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
        ],
        outputChannelName: 'msync Language Server',
    });
    client = candidate;
    context.subscriptions.push(candidate);
    try {
        await candidate.start();
    } catch (error) {
        client = undefined;
        const detail = error instanceof Error ? error.message : String(error);
        void vscode.window.showErrorMessage(`Cannot start msync language services: ${detail}. Install msync 0.6+ or configure msync.serverPath. Syntax highlighting remains available.`);
    }
}

async function deactivate() {
    const running = client;
    client = undefined;
    if (running) {
        await running.stop();
    }
}

module.exports = { activate, deactivate };
