'use strict';

const { renderPage, errorText } = require('./artifact-page.cjs');
const fs = require('node:fs');
const path = require('node:path');

const SCHEME = 'msync-artifact';
const LANGUAGE = 'msync-ir';
const VIEW_TYPE = 'msync.bytecode';

function artifactUri(vscode, uri) {
    if (uri.scheme === SCHEME) return vscode.Uri.parse(Buffer.from(uri.query, 'base64url').toString('utf8'));
    return uri;
}

function textUri(vscode, uri) {
    const source = artifactUri(vscode, uri);
    return vscode.Uri.from({
        scheme: SCHEME,
        path: source.path + '.msync-ir',
        query: Buffer.from(source.toString(), 'utf8').toString('base64url'),
    });
}

function cancelled(vscode, token) {
    if (token?.isCancellationRequested) throw new vscode.CancellationError();
}

function awaitRequest(vscode, promise, token) {
    if (!token) return promise;
    cancelled(vscode, token);
    return new Promise((resolve, reject) => {
        const subscription = token.onCancellationRequested(() => reject(new vscode.CancellationError()));
        promise.then(resolve, reject).finally(() => subscription.dispose());
    });
}

class ArtifactViews {
    constructor(vscode, request, watch) {
        this.vscode = vscode;
        this.request = request;
        this.watch = watch;
        this.entries = new Map();
        this.directories = new Map();
        this.changed = new vscode.EventEmitter();
        this.onDidChange = this.changed.event;
        this.disposed = false;
    }

    entry(uri) {
        uri = artifactUri(this.vscode, uri);
        const key = uri.toString();
        if (this.entries.has(key)) return this.entries.get(key);
        const entry = { uri, generation: 0, panels: new Set(), documents: 0, textOpen: false };
        const directory = path.posix.dirname(uri.path);
        const parent = uri.with({ path: /^\/[A-Za-z]:$/.test(directory) ? directory + '/' : directory, query: '', fragment: '' });
        entry.watch = this.watchEntry(entry, parent);
        this.entries.set(key, entry);
        return entry;
    }

    workspaceWatch(entry, parent) {
        const uri = entry.uri;
        const watcher = this.vscode.workspace.createFileSystemWatcher(new this.vscode.RelativePattern(parent, path.posix.basename(uri.path)));
        return [watcher, watcher.onDidChange(() => this.invalidate(entry)),
            watcher.onDidCreate(() => this.invalidate(entry)), watcher.onDidDelete(() => this.invalidate(entry))];
    }

    watchEntry(entry, parent) {
        if (entry.uri.scheme !== 'file') return this.workspaceWatch(entry, parent);
        const key = parent.toString();
        let directory = this.directories.get(key);
        if (!directory) {
            directory = { key, parent, entries: new Set(), names: new Map() };
            try {
                // Workspace RelativePattern watches depend on VS Code's asynchronous
                // recursive backend. A synchronous local directory subscription
                // closes the initial-open/atomic-publication gap without polling.
                directory.watcher = this.watch(parent.fsPath, { persistent: false }, (_, filename) => this.localChange(directory, filename));
            } catch (error) {
                this.watchFailure(parent, error);
                return this.workspaceWatch(entry, parent);
            }
            directory.watcher.on('error', error => this.localFailure(directory, error));
            this.directories.set(key, directory);
        }
        const name = this.watchName(path.posix.basename(entry.uri.path));
        if (!directory.names.has(name)) directory.names.set(name, new Set());
        directory.entries.add(entry);
        directory.names.get(name).add(entry);
        return [{ dispose: () => {
            directory.entries.delete(entry);
            const entries = directory.names.get(name);
            entries?.delete(entry);
            if (!entries?.size) directory.names.delete(name);
            if (!directory.entries.size) { directory.watcher.close(); this.directories.delete(key); }
        } }];
    }

    watchName(name) { return process.platform === 'win32' ? name.toLowerCase() : name; }

    localChange(directory, filename) {
        if (this.disposed || this.directories.get(directory.key) !== directory) return;
        const entries = filename == null ? directory.entries : directory.names.get(this.watchName(String(filename)));
        for (const entry of entries || []) this.invalidate(entry);
    }

    localFailure(directory, error) {
        if (this.disposed || this.directories.get(directory.key) !== directory) return;
        directory.watcher.close();
        this.directories.delete(directory.key);
        for (const entry of directory.entries) entry.watch = this.workspaceWatch(entry, directory.parent);
        directory.entries.clear(); directory.names.clear();
        this.watchFailure(directory.parent, error);
    }

    watchFailure(parent, error) {
        void this.vscode.window.showWarningMessage(
            `Native file notifications are unavailable for ${parent.fsPath}: ${error.message}. Using VS Code's filesystem watcher; msync: Refresh Artifact View remains available.`);
    }

    async load(entry, token) {
        cancelled(this.vscode, token);
        if (!entry.pending && !entry.model && !entry.error) {
            const generation = entry.generation;
            const cancellation = new this.vscode.CancellationTokenSource();
            entry.cancellation = cancellation;
            const pending = (async () => {
                const bytes = await this.vscode.workspace.fs.readFile(entry.uri);
                cancelled(this.vscode, cancellation.token);
                const model = await this.request({ uri: entry.uri.toString(), data: Buffer.from(bytes).toString('base64') }, cancellation.token);
                cancelled(this.vscode, cancellation.token);
                if (!model || model.schemaVersion !== 1 || typeof model.text !== 'string')
                    throw new Error('This msync reader returned an unsupported artifact-view schema. Update the executable and extension together.');
                if (entry.generation === generation) entry.model = model;
                return model;
            })();
            entry.pending = pending;
            pending.catch(error => {
                if (entry.generation === generation && !cancellation.token.isCancellationRequested) entry.error = error;
            }).finally(() => {
                cancellation.dispose();
                if (entry.pending === pending) { entry.pending = undefined; entry.cancellation = undefined; }
            });
        }
        if (entry.error) throw entry.error;
        return awaitRequest(this.vscode, entry.pending || Promise.resolve(entry.model), token);
    }

    invalidate(entry) {
        if (this.disposed) return;
        entry.generation++;
        entry.cancellation?.cancel();
        entry.pending = undefined;
        entry.model = undefined;
        entry.error = undefined;
        if (entry.textOpen) this.changed.fire(textUri(this.vscode, entry.uri));
        for (const panel of entry.panels) void this.render(entry, panel);
    }

    serverChanged() {
        for (const entry of this.entries.values()) this.invalidate(entry);
    }

    release(entry) {
        if (entry.documents || entry.textOpen || entry.panels.size) return;
        this.entries.delete(entry.uri.toString());
        entry.cancellation?.cancel();
        for (const item of entry.watch) item.dispose();
    }

    async provideTextDocumentContent(uri, token) {
        const entry = this.entry(uri);
        entry.textOpen = true;
        while (true) {
            const generation = entry.generation;
            try {
                const model = await this.load(entry, token);
                if (generation === entry.generation) return model.text;
            } catch (error) {
                cancelled(this.vscode, token);
                if (generation === entry.generation) return errorText(error);
            }
        }
    }

    closeText(document) {
        if (document.uri.scheme !== SCHEME) return;
        const entry = this.entries.get(artifactUri(this.vscode, document.uri).toString());
        if (!entry) return;
        entry.textOpen = false;
        this.release(entry);
    }

    async openCustomDocument(uri) {
        const entry = this.entry(uri);
        entry.documents++;
        return { uri, dispose: () => { entry.documents--; this.release(entry); } };
    }

    async resolveCustomEditor(document, panel, token) {
        const entry = this.entry(document.uri);
        entry.panels.add(panel);
        panel.webview.options = { enableScripts: true, localResourceRoots: [] };
        panel.onDidDispose(() => { entry.panels.delete(panel); this.release(entry); });
        panel.webview.onDidReceiveMessage(message => {
            let action;
            if (message.command === 'refresh') this.invalidate(entry);
            if (message.command === 'text') action = this.openText(entry.uri);
            if (message.command === 'settings') action = this.vscode.commands.executeCommand('workbench.action.openSettings', 'msync.serverPath');
            if (message.command === 'reference' && message.generation === entry.generation) action = this.followReference(entry, message.index, panel.viewColumn);
            action?.catch(error => this.vscode.window.showErrorMessage(`Cannot open artifact reference: ${error.message}`));
        });
        await this.render(entry, panel, token);
    }

    async render(entry, panel, token) {
        const generation = entry.generation;
        if (!panel.webview.html) panel.webview.html = renderPage(entry.uri, undefined, undefined);
        else void panel.webview.postMessage({ loading: true, generation });
        try {
            const model = await this.load(entry, token);
            if (entry.generation === generation && entry.panels.has(panel)) panel.webview.html = renderPage(entry.uri, model, undefined, generation);
        } catch (error) {
            if (entry.generation === generation && entry.panels.has(panel) && !token?.isCancellationRequested)
                panel.webview.html = renderPage(entry.uri, undefined, error);
        }
    }

    selectedUri(uri) {
        if (uri) return artifactUri(this.vscode, uri);
        const active = this.vscode.window.activeTextEditor?.document.uri;
        if (active?.scheme === SCHEME || /\.(?:mso|mexe|mhir)$/i.test(active?.path || '')) return artifactUri(this.vscode, active);
        const panel = [...this.entries.values()].find(entry => [...entry.panels].some(view => view.active));
        return panel?.uri;
    }

    async openText(uri) {
        uri = this.selectedUri(uri);
        if (!uri) {
            const picked = await this.vscode.window.showOpenDialog({ canSelectMany: false, filters: { 'msync artifacts': ['mhir', 'mso', 'mexe'] } });
            if (!picked?.length) return;
            uri = picked[0];
        }
        const document = await this.vscode.workspace.openTextDocument(textUri(this.vscode, uri));
        return this.vscode.window.showTextDocument(document, { preview: false });
    }

    async refresh(uri) {
        uri = this.selectedUri(uri);
        if (!uri) return;
        this.invalidate(this.entry(uri));
    }

    async model(document, token) {
        try { return await this.load(this.entry(document.uri), token); }
        catch (error) { cancelled(this.vscode, token); return undefined; }
    }

    async provideDocumentSymbols(document, token) {
        const model = await this.model(document, token);
        const convert = item => {
            const symbol = new this.vscode.DocumentSymbol(item.name, item.detail || '', item.kind, range(this.vscode, item.range), range(this.vscode, item.selectionRange));
            symbol.children = (item.children || []).map(convert);
            return symbol;
        };
        return (model?.symbols || []).map(convert);
    }

    async provideHover(document, position, token) {
        const model = await this.model(document, token);
        const item = model?.hovers?.find(value => range(this.vscode, value.range).contains(position));
        if (!item) return undefined;
        const content = new this.vscode.MarkdownString(item.markdown);
        content.isTrusted = false;
        return new this.vscode.Hover(content, range(this.vscode, item.range));
    }

    async provideDefinition(document, position, token) {
        const entry = this.entry(document.uri);
        const model = await this.model(document, token);
        const reference = model?.references?.find(value => range(this.vscode, value.range).contains(position));
        if (!reference) return undefined;
        return this.location(entry, reference, document.uri);
    }

    async location(entry, reference, uri = textUri(this.vscode, entry.uri)) {
        if (Number.isInteger(reference.targetLine))
            return new this.vscode.Location(uri, new this.vscode.Position(reference.targetLine, reference.targetCharacter || 0));
        if (!reference.sourceUri) return undefined;
        let target = this.vscode.Uri.parse(reference.sourceUri);
        if (/^[A-Za-z]:[\\/]/.test(reference.sourceUri) || reference.sourceUri.startsWith('/') || reference.sourceUri.startsWith('\\\\')) target = this.vscode.Uri.file(reference.sourceUri);
        if (!['file', 'vscode-remote', 'msync-stdlib', 'untitled'].includes(target.scheme)) return undefined;
        if (target.scheme === 'file' && entry.uri.scheme === 'vscode-remote') target = entry.uri.with({ path: target.path, query: '', fragment: '' });
        const source = await this.vscode.workspace.openTextDocument(target);
        const start = source.positionAt(reference.sourceStart || 0);
        const end = source.positionAt((reference.sourceStart || 0) + (reference.sourceLength || 0));
        return new this.vscode.Location(target, new this.vscode.Range(start, end));
    }

    async followReference(entry, index, column) {
        const reference = entry.model?.references?.[index];
        if (!reference) return;
        if (Number.isInteger(reference.targetLine)) {
            for (const panel of entry.panels) await panel.webview.postMessage({ reveal: reference.targetLine });
            return;
        }
        const target = await this.location(entry, reference);
        if (!target) return;
        const source = await this.vscode.workspace.openTextDocument(target.uri);
        await this.vscode.window.showTextDocument(source, { viewColumn: column, selection: target.range });
    }

    dispose() {
        if (this.disposed) return;
        this.disposed = true;
        for (const entry of this.entries.values()) {
            entry.cancellation?.cancel();
            for (const item of entry.watch) item.dispose();
        }
        this.entries.clear();
        this.directories.clear();
        this.changed.dispose();
    }
}

function range(vscode, value) {
    return new vscode.Range(value.start.line, value.start.character, value.end.line, value.end.character);
}

function registerArtifactViews(vscode, context, request, watch = fs.watch) {
    const views = new ArtifactViews(vscode, request, watch);
    const selector = { scheme: SCHEME, language: LANGUAGE };
    context.subscriptions.push(views,
        vscode.window.registerCustomEditorProvider(VIEW_TYPE, views, { supportsMultipleEditorsPerDocument: true }),
        vscode.workspace.registerTextDocumentContentProvider(SCHEME, views),
        vscode.workspace.onDidCloseTextDocument(document => views.closeText(document)),
        vscode.commands.registerCommand('msync.openArtifactText', uri => views.openText(uri)),
        vscode.commands.registerCommand('msync.refreshArtifact', uri => views.refresh(uri)),
        vscode.languages.registerDocumentSymbolProvider(selector, views),
        vscode.languages.registerHoverProvider(selector, views),
        vscode.languages.registerDefinitionProvider(selector, views));
    return views;
}

module.exports = { registerArtifactViews, artifactUri, textUri, renderPage };
