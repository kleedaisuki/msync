'use strict';

function registerSshPrompts(vscode, client) {
    const pending = new Map();
    let queue = Promise.resolve();
    let disposed = false;

    async function show(parameters, token) {
        if (disposed || token.isCancellationRequested) return { value: null };
        const title = `SSH: ${parameters.endpoint}`;
        if (parameters.kind === 'notice') {
            void vscode.window.showInformationMessage(`${title} — ${parameters.prompt}`);
            return { value: '' };
        }
        if (parameters.kind === 'confirm') {
            const selected = await vscode.window.showQuickPick([
                { label: 'Yes', detail: parameters.prompt, value: 'yes' },
                { label: 'No', detail: 'Do not accept this SSH host key.', value: 'no' },
            ], { title, placeHolder: parameters.prompt, ignoreFocusOut: true }, token);
            return { value: token.isCancellationRequested ? null : selected?.value ?? null };
        }
        const value = await vscode.window.showInputBox({
            title,
            prompt: parameters.prompt,
            password: parameters.kind === 'secret',
            ignoreFocusOut: true,
        }, token);
        return { value: token.isCancellationRequested ? null : value ?? null };
    }

    const request = client.onRequest('msync/sshPrompt', parameters => {
        if (disposed) return { value: null };
        const cancellation = new vscode.CancellationTokenSource();
        pending.set(parameters.id, cancellation);
        const result = queue.then(() => show(parameters, cancellation.token));
        queue = result.then(() => {}, () => {});
        return result.finally(() => {
            pending.delete(parameters.id);
            cancellation.dispose();
        });
    });
    const cancel = client.onNotification('msync/sshPromptCancel', parameters => {
        pending.get(parameters.id)?.cancel();
    });
    return {
        dispose() {
            disposed = true;
            for (const cancellation of pending.values()) cancellation.cancel();
            request.dispose();
            cancel.dispose();
        },
    };
}

module.exports = { registerSshPrompts };
