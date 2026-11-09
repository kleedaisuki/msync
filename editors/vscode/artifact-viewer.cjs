'use strict';

const crypto = require('node:crypto');
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

function errorText(error) {
    const detail = error instanceof Error ? error.message : String(error);
    return [
        '; msync bytecode could not be displayed',
        '; The original artifact has not been modified or executed.',
        '',
        ...detail.split(/\r?\n/).map(line => '; ' + line),
        '',
        '; Use msync: Refresh Bytecode View after replacing or repairing the file.',
        '; Check msync.serverPath if the reader is missing or does not support this format.',
    ].join('\n');
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
            `Native file notifications are unavailable for ${parent.fsPath}: ${error.message}. Using VS Code's filesystem watcher; msync: Refresh Bytecode View remains available.`);
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
                    throw new Error('This msync reader returned an unsupported bytecode-view schema. Update the executable and extension together.');
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
            if (message.command === 'reference') action = this.followReference(entry, message.index, panel.viewColumn);
            action?.catch(error => this.vscode.window.showErrorMessage(`Cannot open bytecode reference: ${error.message}`));
        });
        await this.render(entry, panel, token);
    }

    async render(entry, panel, token) {
        const generation = entry.generation;
        if (!panel.webview.html) panel.webview.html = renderPage(entry.uri, undefined, undefined);
        else void panel.webview.postMessage({ loading: true });
        try {
            const model = await this.load(entry, token);
            if (entry.generation === generation && entry.panels.has(panel)) panel.webview.html = renderPage(entry.uri, model, undefined);
        } catch (error) {
            if (entry.generation === generation && entry.panels.has(panel) && !token?.isCancellationRequested)
                panel.webview.html = renderPage(entry.uri, undefined, error);
        }
    }

    selectedUri(uri) {
        if (uri) return artifactUri(this.vscode, uri);
        const active = this.vscode.window.activeTextEditor?.document.uri;
        if (active?.scheme === SCHEME || /\.(?:mso|mexe)$/i.test(active?.path || '')) return artifactUri(this.vscode, active);
        const panel = [...this.entries.values()].find(entry => [...entry.panels].some(view => view.active));
        return panel?.uri;
    }

    async openText(uri) {
        uri = this.selectedUri(uri);
        if (!uri) {
            const picked = await this.vscode.window.showOpenDialog({ canSelectMany: false, filters: { 'msync bytecode': ['mso', 'mexe'] } });
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

function escape(value) {
    return String(value).replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character]);
}

const CONTROL_OPERATIONS = new Set(['Jump', 'JumpIfTrue', 'JumpIfFalse', 'Return', 'IteratorNext', 'Barrier', 'Acquire']);
const FIELD_OPERATIONS = new Set(['MakeScope', 'GetMember', 'SetMember']);

function syntax(text, quotedRole = 'string') {
    const operation = /^(\s*\d+:\s+)([A-Za-z][A-Za-z0-9_]*)/.exec(text);
    if (operation) {
        const role = CONTROL_OPERATIONS.has(operation[2]) ? 'control' : 'opcode';
        const fields = FIELD_OPERATIONS.has(operation[2]) ? 'field' : quotedRole;
        return syntax(operation[1]) + `<span class="${role}">${escape(operation[2])}</span>` + syntax(text.slice(operation[0].length), fields);
    }
    const expression = /("(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*')|(;.*$)|(\b(?:release|acquire|acqrel|before-and-after|before|after|region-start|join)\b)|(\b(?:artifact|context|const|fn|block|entry|origin|format|language|optimization|semantics|captures|locals|successors|effects|pure|readonly)\b)|(@f\d+)|([@%]c\d+)|(@b\d+_[0-9]+|\bb\d+\b)|(%\d+|\b(?:local|capture|arg)\d+\b)|\b(Null|Bool|Number|String|Path|Scope|Function|Connection|Process|Range|Type|Never|Any)\b|(\bpath(?=\s*\())|(\b(?:number|connection|closure|native|type|scope)(?=\s*[({]))|(\b(?:fields|slot|allocation-site|layout-checked|hint|raw|cwd|home|endpoint|bits|source)\b)|(\b(?:null|true|false)\b)|((?<![A-Za-z0-9_])[+-]?(?:0x[0-9a-fA-F]+|(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?|Infinity|NaN)\b)/g;
    const roles = [quotedRole, 'comment', 'control', 'keyword', 'function', 'constant', 'block', 'local', 'type', 'path', 'constructor', 'layout', 'literal', 'number'];
    let result = '', last = 0;
    for (const match of text.matchAll(expression)) {
        result += escape(text.slice(last, match.index));
        let role;
        for (let index = 0; index < roles.length; index++)
            if (match[index + 1] !== undefined) { role = roles[index]; break; }
        result += `<span class="${role}">${escape(match[0])}</span>`;
        last = match.index + match[0].length;
    }
    return result + escape(text.slice(last));
}

function lines(model) {
    const referencesByLine = new Map(), hoversByLine = new Map();
    for (const [index, reference] of (model.references || []).entries()) {
        if (reference.range.start.line !== reference.range.end.line) continue;
        const line = reference.range.start.line;
        if (!referencesByLine.has(line)) referencesByLine.set(line, []);
        referencesByLine.get(line).push({ reference, index });
    }
    for (const hover of model.hovers || []) {
        const line = hover.range.start.line;
        if (!hoversByLine.has(line)) hoversByLine.set(line, []);
        hoversByLine.get(line).push(hover.markdown);
    }
    return model.text.split('\n').map((text, line) => {
        let last = 0, body = '';
        const references = (referencesByLine.get(line) || [])
            .sort((left, right) => left.reference.range.start.character - right.reference.range.start.character);
        for (const { reference, index } of references) {
            const start = reference.range.start.character, end = reference.range.end.character;
            if (start < last) continue;
            body += syntax(text.slice(last, start));
            body += `<button class="code-link" data-reference="${index}">${syntax(text.slice(start, end))}</button>`;
            last = end;
        }
        body += syntax(text.slice(last));
        const hover = (hoversByLine.get(line) || []).join('\n\n');
        return `<div class="line" id="line-${line}" title="${escape(hover)}"><a class="line-number" href="#line-${line}">${line + 1}</a><code>${body || ' '}</code></div>`;
    }).join('\n');
}

function outline(symbols) {
    return symbols.map(symbol => `<li><a href="#line-${symbol.selectionRange.start.line}">${escape(symbol.name)}</a>${symbol.children?.length ? '<ul>' + outline(symbol.children) + '</ul>' : ''}</li>`).join('');
}

function renderPage(uri, model, error) {
    const nonce = crypto.randomBytes(18).toString('base64');
    const controls = '<button data-command="text">Open IR as Text</button><button data-command="refresh">Refresh</button><button data-command="settings">Reader Settings</button>';
    const metadata = model ? `${escape(model.stage)} · format ${escape(model.formatVersion)} · language ${escape(model.languageVersion)} · O${escape(model.optimizationLevel)}` : 'Typed bytecode inspection';
    const content = error ? `<section role="alert"><h2>Cannot display this artifact</h2><pre>${escape(errorText(error))}</pre></section>`
        : model ? `<nav aria-label="Artifact outline"><ul>${outline(model.symbols || [])}</ul></nav><main aria-label="Read-only typed IR">${lines(model)}</main>`
            : '<p role="status">Reading typed bytecode…</p>';
    return `<!DOCTYPE html><html><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'nonce-${nonce}'; script-src 'nonce-${nonce}';"><style nonce="${nonce}">
body{color:var(--vscode-editor-foreground);background:var(--vscode-editor-background);font:var(--vscode-font-size) var(--vscode-font-family);padding:0 18px}header{position:sticky;top:0;background:var(--vscode-editor-background);padding:12px 0;border-bottom:1px solid var(--vscode-panel-border);z-index:1}h1{font-size:1.1em;font-weight:600;margin:0 0 6px;overflow-wrap:anywhere}p{color:var(--vscode-descriptionForeground)}button{cursor:pointer;color:var(--vscode-button-foreground);background:var(--vscode-button-background);border:0;padding:5px 10px;margin-right:6px}button:hover{background:var(--vscode-button-hoverBackground)}a{color:var(--vscode-textLink-foreground)}nav{float:left;max-width:240px;margin:12px 20px 12px 0;overflow-wrap:anywhere}nav ul{list-style:none;padding-left:14px}nav>ul{padding-left:0}nav a{text-decoration:none}main{overflow:auto;padding:12px 0;tab-size:4;font:var(--vscode-editor-font-size) var(--vscode-editor-font-family);line-height:1.55}.line{display:flex;white-space:pre;scroll-margin-top:115px}.line:target{background:var(--vscode-editor-lineHighlightBackground)}.line-number{color:var(--vscode-editorLineNumber-foreground);text-decoration:none;min-width:4ch;text-align:right;margin-right:20px;user-select:none}.code-link{font:inherit;line-height:inherit;display:inline;color:inherit;background:none;padding:0;margin:0;text-decoration:underline;text-decoration-color:var(--vscode-textLink-foreground)}.code-link:hover{background:var(--vscode-editor-hoverHighlightBackground)}.keyword,.control{color:var(--vscode-symbolIcon-keywordForeground,var(--vscode-editor-foreground));font-weight:600}.opcode{color:var(--vscode-symbolIcon-operatorForeground,var(--vscode-editor-foreground))}.string,.path{color:var(--vscode-debugTokenExpression-string,var(--vscode-editor-foreground))}.comment{color:var(--vscode-descriptionForeground)}.function{color:var(--vscode-symbolIcon-functionForeground,var(--vscode-editor-foreground))}.constant{color:var(--vscode-symbolIcon-constantForeground,var(--vscode-editor-foreground))}.block{color:var(--vscode-symbolIcon-referenceForeground,var(--vscode-editor-foreground))}.local{color:var(--vscode-symbolIcon-variableForeground,var(--vscode-editor-foreground))}.type,.constructor{color:var(--vscode-symbolIcon-classForeground,var(--vscode-editor-foreground))}.field,.layout{color:var(--vscode-symbolIcon-propertyForeground,var(--vscode-editor-foreground))}.literal{color:var(--vscode-symbolIcon-booleanForeground,var(--vscode-editor-foreground))}.number{color:var(--vscode-symbolIcon-numberForeground,var(--vscode-editor-foreground))}pre{white-space:pre-wrap;overflow-wrap:anywhere}button:focus-visible,a:focus-visible{outline:1px solid var(--vscode-focusBorder);outline-offset:2px}@media(max-width:700px){nav{float:none;max-width:none}header{position:static}}
</style></head><body><header><h1>${escape(uri.path.split('/').at(-1))}</h1><p>${metadata} — read-only; not recovered source <span id="status" role="status"></span></p>${controls}</header>${content}<script nonce="${nonce}">
const host = acquireVsCodeApi();
const main = document.querySelector('main');
const saved = host.getState();
if (main && saved) { window.scrollTo(0, saved.top); main.scrollLeft = saved.left; }
let saving = false;
function savePosition() {
    if (!main || saving) return;
    saving = true;
    requestAnimationFrame(() => { host.setState({ top: window.scrollY, left: main.scrollLeft }); saving = false; });
}
window.addEventListener('scroll', savePosition, { passive: true });
main?.addEventListener('scroll', savePosition, { passive: true });
document.addEventListener('click', event => {
    const button = event.target.closest('button');
    if (!button) return;
    if (button.dataset.command) host.postMessage({ command: button.dataset.command });
    if (button.dataset.reference) host.postMessage({ command: 'reference', index: Number(button.dataset.reference) });
});
window.addEventListener('message', event => {
    if (Number.isInteger(event.data.reveal)) document.getElementById('line-' + event.data.reveal)?.scrollIntoView({ block: 'center' });
    if (event.data.loading) document.getElementById('status').textContent = ' — Reading…';
});
</script></body></html>`;
}

function registerArtifactViews(vscode, context, request, watch = fs.watch) {
    const views = new ArtifactViews(vscode, request, watch);
    const selector = { scheme: SCHEME, language: LANGUAGE };
    context.subscriptions.push(views,
        vscode.window.registerCustomEditorProvider(VIEW_TYPE, views, { supportsMultipleEditorsPerDocument: true, webviewOptions: { enableFindWidget: true } }),
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
