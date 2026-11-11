'use strict';

const crypto = require('node:crypto');
const { createCodeRenderer } = require('./artifact-code.cjs');
const { startArtifactPage } = require('./artifact-page-client.cjs');
const { styles } = require('./artifact-page-style.cjs');
const { escape } = createCodeRenderer();

function errorText(error) {
    const detail = error instanceof Error ? error.message : String(error);
    return [
        '; msync artifact could not be displayed',
        '; The original artifact has not been modified or executed.',
        '',
        ...detail.split(/\r?\n/).map(line => '; ' + line),
        '',
        '; Use msync: Refresh Artifact View after replacing or repairing the file.',
        '; Check msync.serverPath if the reader is missing or does not support this format.',
    ].join('\n');
}

function stageOf(uri, model) {
    if (model ? model.artifactKind === 'hir' : /\.mhir$/i.test(uri.path)) return 'Typed HIR';
    if (model ? model.stage === 'executable' : /\.mexe$/i.test(uri.path)) return 'Executable bytecode';
    return 'Relocatable bytecode';
}

function renderPage(uri, model, error, generation = 0) {
    const nonce = crypto.randomBytes(18).toString('base64');
    const stage = stageOf(uri, model);
    const name = escape(uri.path.split('/').at(-1));
    const planCount = model?.executionPlans?.length || 0;
    const facts = model ? [
        `<strong class="stage">${stage}</strong>`,
        `<span>Format ${escape(model.formatVersion)}</span>`,
        `<span>Language ${escape(model.languageVersion)}</span>`,
        Number.isInteger(model.optimizationLevel) ? `<span>O${model.optimizationLevel}</span>` : '',
        planCount ? `<span>${planCount} execution plan${planCount === 1 ? '' : 's'}</span>` : '',
    ].join('') : `<strong class="stage">${stage}</strong>`;
    const explanation = stage === 'Typed HIR'
        ? 'Read-only semantic representation; not recovered source.'
        : 'Read-only disassembly; not recovered source.';
    const content = error ? `<section class="empty" role="alert"><h2>Cannot display this artifact</h2><pre>${escape(errorText(error))}</pre></section>`
        : model ? inspector() : '<p class="empty" role="status">Reading artifact…</p>';
    // The model is data, not JS or DOM. In particular a debug string cannot close this script element.
    const data = JSON.stringify(model ? {
        text: model.text,
        symbols: model.symbols || [],
        references: model.references || [],
        hovers: model.hovers || [],
        executionPlans: model.executionPlans || [],
    } : null).replace(/</g, '\\u003c');
    return `<!DOCTYPE html><html lang="en"><head><meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'nonce-${nonce}'; script-src 'nonce-${nonce}';">
<title>${name} — msync artifact inspector</title><style nonce="${nonce}">${styles}</style></head>
<body data-generation="${generation}"><header class="masthead">
  <div class="identity"><div class="eyebrow">Artifact inspector</div><h1 title="${name}">${name}</h1></div>
  <div class="actions">
    <button class="primary" type="button" data-command="text">Open IR as Text</button>
    <button type="button" data-command="refresh">Refresh</button>
    <button type="button" data-command="settings">Reader Settings</button>
  </div>
  <div class="facts">${facts}<span class="explanation">${explanation}</span><span id="status" role="status"></span></div>
</header>${content}
<script type="application/json" id="artifact-data" nonce="${nonce}">${data}</script>
<script nonce="${nonce}">(${startArtifactPage.toString()})( (${createCodeRenderer.toString()})(), ${generation} );</script>
</body></html>`;
}

function inspector() {
    return `<div class="workspace">
  <aside id="outline" class="outline" aria-label="Artifact outline">
    <header class="pane-heading"><h2>Outline</h2><span id="outline-count"></span></header>
    <label class="visually-hidden" for="outline-group">Outline category</label>
    <select id="outline-group"></select>
    <nav class="outline-scroll" aria-label="Definitions"><ol id="outline-items"></ol></nav>
    <div class="pager" id="outline-pager">
      <button type="button" id="outline-prev" aria-label="Previous outline page">‹</button>
      <span id="outline-page"></span>
      <button type="button" id="outline-next" aria-label="Next outline page">›</button>
    </div>
  </aside>
  <main class="inspection" aria-label="Read-only typed IR">
    <div class="toolbar">
      <div class="panel-controls">
        <button type="button" id="toggle-outline" aria-controls="outline" aria-expanded="true">Outline</button>
        <button type="button" id="toggle-detail" aria-controls="detail" aria-expanded="true">Details</button>
      </div>
      <div class="search-controls" role="search">
        <label class="visually-hidden" for="find">Search entire artifact</label>
        <input id="find" type="search" placeholder="Search entire artifact" spellcheck="false">
        <button type="button" id="match-prev" aria-label="Previous match" title="Previous match (Shift+F3)" disabled>↑</button>
        <button type="button" id="match-next" aria-label="Next match" title="Next match (F3)" disabled>↓</button>
        <output id="match-count" for="find" aria-live="polite"></output>
      </div>
      <form id="goto" class="goto">
        <label class="visually-hidden" for="goto-line">Go to line</label>
        <input id="goto-line" type="number" min="1" step="1" placeholder="Line" required>
        <button type="submit">Go</button>
      </form>
    </div>
    <div class="panes">
      <section class="listing" aria-label="IR listing">
        <div id="code" class="code-scroll" role="region" tabindex="0"
             aria-label="IR code. Use arrow keys to inspect a line, Page Up or Page Down to change page."></div>
        <footer class="listing-footer">
          <span id="line-range" aria-live="polite"></span>
          <div class="pager">
            <button type="button" id="code-prev" aria-label="Previous code page">Previous</button>
            <button type="button" id="code-next" aria-label="Next code page">Next</button>
          </div>
        </footer>
      </section>
      <aside id="detail" class="detail" aria-label="Selected line details">
        <header class="pane-heading"><h2>Details</h2><span id="detail-line"></span></header>
        <div id="detail-content" class="detail-scroll"></div>
      </aside>
    </div>
    <span class="visually-hidden" id="selection-status" aria-live="polite"></span>
  </main>
</div>`;
}

module.exports = { renderPage, errorText };
