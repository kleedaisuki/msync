'use strict';

// No extension-host references: this function is bundled, then run inside the webview.
function startArtifactPage(renderer, generation) {
    const host = acquireVsCodeApi();
    const get = id => document.getElementById(id);
    const dataElement = get('artifact-data');
    const data = JSON.parse(dataElement.textContent);
    dataElement.remove();
    document.addEventListener('click', event => {
        const command = event.target.closest('[data-command]')?.dataset.command;
        if (command) host.postMessage({ command });
    });
    window.addEventListener('message', event => {
        if (event.data?.loading && event.data.generation > generation) get('status').textContent = 'Reading…';
    });
    if (!data) return;

    const PAGE = 160, OUTLINE_PAGE = 64, DETAIL_PAGE = 24;
    const text = data.text.split('\n'), code = get('code'), outline = get('outline'), detail = get('detail');
    const search = get('find'), go = get('goto-line'), category = get('outline-group');
    const state = host.getState() || {};
    const lastPage = Math.floor((text.length - 1) / PAGE);
    const { escape, syntax } = renderer;
    const references = byLine(data.references, true);
    const hovers = byLine(data.hovers);
    const groups = new Map(), symbols = [], identifiers = new Map();
    const plans = new Map(data.executionPlans.map(plan => [plan.id, plan]));
    const transfers = new Map();
    for (const plan of plans.values())
        for (const node of plan.transfers) transfers.set(`@p${plan.id}n${node.id}`, node);
    buildOutline();
    let selected = restoredLine(), page = -1, outlinePage = 0, outlineEntries = [];
    let matches = [], matchLines = new Set(), searchGeneration = 0, saving = false;
    go.max = String(text.length);

    function byLine(items, indexed = false) {
        const map = new Map();
        for (const [index, item] of items.entries()) {
            if (indexed) item.index = index;
            const line = item.range.start.line;
            if (!map.has(line)) map.set(line, []);
            map.get(line).push(item);
        }
        for (const list of map.values())
            list.sort((left, right) => left.range.start.character - right.range.start.character);
        return map;
    }

    function element(tag, value, className) {
        const result = document.createElement(tag);
        if (value !== undefined) result.textContent = value;
        if (className) result.className = className;
        return result;
    }

    function control(label, action) {
        const result = element('button', label);
        result.type = 'button';
        result.addEventListener('click', action);
        return result;
    }

    function groupOf(symbol) {
        if (/^@p\d+$/.test(symbol.name)) return 'Execution plans';
        return ({ 1: 'Sources', 5: 'Types and shapes', 12: 'Functions and templates',
            13: 'Locations', 14: 'Constants' })[symbol.kind] || 'Other declarations';
    }

    function buildOutline() {
        const pending = data.symbols.slice().reverse().map(symbol => ({ symbol, parent: undefined }));
        while (pending.length) {
            const { symbol, parent } = pending.pop();
            const group = parent?.group || groupOf(symbol);
            const line = symbol.selectionRange.start.line;
            const identifier = /(@(?:[ftlgscn]\d+|b\d+_\d+|p\d+(?:n\d+)?))\)?$/.exec(symbol.name)?.[1];
            const item = { name: symbol.name, caption: symbol.name.replace(/\s+\(@[^)]+\)$/, ''), detail: symbol.detail, line, identifier,
                end: symbol.range.end.line, parent, group };
            symbols.push(item);
            if (identifier) identifiers.set(identifier, item);
            if (!groups.has(group)) groups.set(group, []);
            groups.get(group).push(item);
            for (let index = (symbol.children?.length || 0) - 1; index >= 0; index--)
                pending.push({ symbol: symbol.children[index], parent: item });
        }
        symbols.sort((a, b) => a.line - b.line);
        for (const group of ['Execution plans', 'Functions and templates', 'Types and shapes',
            'Locations', 'Constants', 'Sources', 'Other declarations']) {
            const items = groups.get(group);
            if (!items?.length) continue;
            const label = group === 'Functions and templates' ? 'Callables'
                : group === 'Execution plans' ? 'Plans / transfers' : group;
            const option = element('option', `${label} (${items.length})`);
            option.value = group;
            category.append(option);
        }
        if (groups.has(state.group)) category.value = state.group;
        category.disabled = !groups.size;
    }

    function clamp(line) { return Math.max(0, Math.min(text.length - 1, line)); }

    function restoredLine() {
        const previous = Number.isInteger(state.line) ? state.line : 0;
        let item = identifiers.get(state.anchor);
        if (state.caption && item?.caption !== state.caption) {
            const same = (groups.get(state.anchorGroup) || []).filter(entry => entry.caption === state.caption);
            item = same.length === 1 ? same[0] : undefined;
        }
        return clamp(item ? Math.min(item.end, item.line + (state.offset || 0)) : previous);
    }

    function lowerBound(values, line, key = value => value) {
        let low = 0, high = values.length;
        while (low < high) {
            const mid = (low + high) >>> 1;
            if (key(values[mid]) < line) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    function definitionAt(line) {
        let item = symbols[lowerBound(symbols, line + 1, value => value.line) - 1];
        while (item && item.end < line) item = item.parent;
        return item;
    }

    function save() {
        if (saving) return;
        saving = true;
        requestAnimationFrame(() => {
            saving = false;
            const item = definitionAt(selected);
            host.setState({ line: selected, anchor: item?.identifier, caption: item?.caption,
                anchorGroup: item?.group, offset: item && selected - item.line,
                page, top: code.scrollTop, left: code.scrollLeft, query: search.value, match: matchLines.has(selected),
                group: category.value, outlinePage, outline: !outline.hidden, detail: !detail.hidden });
        });
    }

    function row(line) {
        const value = text[line];
        let last = 0, body = '';
        for (const reference of references.get(line) || []) {
            const start = reference.range.start.character, end = reference.range.end.character;
            if (reference.range.end.line !== line || start < last || end > value.length
                || (!Number.isInteger(reference.targetLine) && !reference.sourceUri)) continue;
            body += syntax(value.slice(last, start));
            const label = escape(Number.isInteger(reference.targetLine)
                ? `Jump to line ${reference.targetLine + 1}` : `Open source: ${reference.sourceName || 'definition'}`);
            body += `<button type="button" tabindex="-1" class="code-link" data-reference="${reference.index}" title="${label}">${syntax(value.slice(start, end))}</button>`;
            last = end;
        }
        body += syntax(value.slice(last));
        return `<div class="line${matchLines.has(line) ? ' match' : ''}" data-line="${line}">
<button class="line-number" type="button" tabindex="-1" aria-label="Inspect line ${line + 1}">${line + 1}</button><code>${body || ' '}</code></div>`;
    }

    function renderCode() {
        const start = page * PAGE, end = Math.min(start + PAGE, text.length);
        const rows = [];
        for (let line = start; line < end; line++) rows.push(row(line));
        code.innerHTML = `<div class="code-content">${rows.join('')}</div>`;
        get('line-range').textContent = `Lines ${start + 1}–${end} of ${text.length.toLocaleString()}`;
        get('code-prev').disabled = page === 0;
        get('code-next').disabled = page === lastPage;
    }

    function renderOutline() {
        outlineEntries = groups.get(category.value) || [];
        const maximum = Math.max(0, Math.ceil(outlineEntries.length / OUTLINE_PAGE) - 1);
        outlinePage = Math.max(0, Math.min(maximum, outlinePage));
        const start = outlinePage * OUTLINE_PAGE, end = Math.min(start + OUTLINE_PAGE, outlineEntries.length);
        const fragment = document.createDocumentFragment();
        for (let index = start; index < end; index++) {
            const item = outlineEntries[index], li = element('li');
            const button = control(undefined, () => showLine(item.line));
            button.className = 'outline-entry' + (item.parent ? ' child' : '');
            button.dataset.definition = String(item.line);
            button.title = item.detail ? `${item.name}\n${item.detail}` : item.name;
            button.append(element('span', item.name, 'entry-name'));
            li.append(button);
            fragment.append(li);
        }
        if (!outlineEntries.length) fragment.append(element('li', 'No declarations in this artifact.', 'quiet'));
        get('outline-items').replaceChildren(fragment);
        get('outline-count').textContent = `${symbols.length} total`;
        get('outline-pager').hidden = maximum === 0;
        get('outline-prev').disabled = outlinePage === 0;
        get('outline-next').disabled = outlinePage === maximum;
        get('outline-page').textContent = `${start + 1}–${end} / ${outlineEntries.length}`;
        selectOutline();
    }

    function selectOutline() {
        const item = definitionAt(selected);
        const current = get('outline-items').querySelector('[aria-current="true"]');
        const next = item && get('outline-items').querySelector(`[data-definition="${item.line}"]`);
        if (current !== next) {
            current?.removeAttribute('aria-current');
            next?.setAttribute('aria-current', 'true');
        }
    }

    function follow(reference) {
        if (Number.isInteger(reference.targetLine)) {
            showLine(reference.targetLine);
            code.focus({ preventScroll: true });
        } else if (reference.sourceUri) host.postMessage({ command: 'reference', index: reference.index, generation });
    }

    function metadata(parent, entries) {
        const list = element('dl');
        for (const [label, value] of entries) list.append(element('dt', label), element('dd', String(value)));
        parent.append(list);
    }

    function detailList(parent, title, items, make) {
        if (!items.length) return;
        const section = element('section'), list = element('ul');
        section.append(element('h3', `${title} (${items.length})`), list);
        parent.append(section);
        let index = 0;
        const label = element('span');
        const prev = control('Previous', () => { index -= DETAIL_PAGE; update(); });
        const next = control('Next', () => { index += DETAIL_PAGE; update(); });
        const update = () => {
            const end = Math.min(index + DETAIL_PAGE, items.length);
            const rows = [];
            for (let cursor = index; cursor < end; cursor++) {
                const item = element('li');
                item.append(make(items[cursor], cursor));
                rows.push(item);
            }
            list.replaceChildren(...rows);
            label.textContent = `${index + 1}–${end}`;
            prev.disabled = index === 0;
            next.disabled = end === items.length;
        };
        if (items.length > DETAIL_PAGE) {
            const pager = element('div', undefined, 'pager');
            prev.setAttribute('aria-label', `Previous ${title.toLowerCase()} page`);
            next.setAttribute('aria-label', `Next ${title.toLowerCase()} page`);
            pager.append(prev, label, next);
            section.append(pager);
        }
        update();
    }

    function description(parent, value) {
        for (const block of value.split(/\n\s*\n/)) {
            const paragraph = element('p');
            let last = 0;
            for (const token of block.matchAll(/\*\*([^*]+)\*\*|`([^`]+)`/g)) {
                paragraph.append(block.slice(last, token.index), element(token[1] ? 'strong' : 'code', token[1] || token[2]));
                last = token.index + token[0].length;
            }
            paragraph.append(block.slice(last));
            parent.append(paragraph);
        }
    }

    function jumpTo(identifier, label = identifier) {
        const item = identifiers.get(identifier);
        return item ? control(label, () => { showLine(item.line); code.focus({ preventScroll: true }); }) : element('code', label);
    }

    function planDetail(parent, item) {
        const label = item.parent?.identifier || item.identifier;
        const id = /^@p(\d+)$/.exec(label)?.[1];
        const plan = plans.get(Number(id));
        if (!plan) return;
        const node = transfers.get(item.identifier);
        const owner = `@f${plan.function}`;
        if (!node) {
            metadata(parent, [['Region', `[${plan.start}…${plan.end})`], ['Transfers', plan.transfers.length],
                ['Batches', plan.batches.length], ['Eliminated', plan.eliminatedTransfers]]);
            parent.append(jumpTo(owner, `Owner ${owner}`));
            detailList(parent, 'Transfers', plan.transfers, operation =>
                jumpTo(`${label}n${operation.id}`, `#${operation.id} · ${operation.kind}`));
            detailList(parent, 'Batches', plan.batches, (batch, index) => {
                const caption = `#${index} · ${batch.endpoint} · ${batch.transfers.length} transfers`;
                return jumpTo(`${label}n${batch.transfers[0]}`, caption);
            });
            return;
        }
        metadata(parent, [['Operation', node.kind], ['Mode', node.mode]]);
        parent.append(jumpTo(label, `Plan ${label}`));
        detailList(parent, 'Predecessors', node.predecessors, predecessor => jumpTo(`${label}n${predecessor}`));
        parent.append(element('h3', 'Source'), element('pre', node.source),
            element('h3', 'Destination'), element('pre', node.destination));
        const resource = entry => element('code', `${entry.endpoint} · ${entry.path} · ${entry.subtree ? 'subtree' : 'exact'}`);
        detailList(parent, 'Reads', node.reads, resource);
        detailList(parent, 'Writes', node.writes, resource);
    }

    function renderDetails() {
        if (detail.hidden) return;
        const item = definitionAt(selected), content = get('detail-content');
        const fragment = document.createDocumentFragment();
        get('detail-line').textContent = `Line ${selected + 1}`;
        const annotations = hovers.get(selected) || [];
        if (item) {
            fragment.append(element('h3', item.name));
            if (item.detail && !transfers.has(item.identifier) && !annotations.some(hover => hover.markdown.includes(item.detail)))
                fragment.append(element('p', item.detail, 'quiet'));
            planDetail(fragment, item);
        }
        for (const hover of annotations) description(fragment, hover.markdown);
        detailList(fragment, 'References', references.get(selected) || [], reference => {
            if (Number.isInteger(reference.targetLine)) {
                const target = definitionAt(reference.targetLine);
                return control(`Line ${reference.targetLine + 1}${target ? ' · ' + target.name : ''}`, () => follow(reference));
            }
            const label = reference.sourceName || reference.sourceUri || 'Source not available';
            return reference.sourceUri ? control(`Open source · ${label}`, () => follow(reference)) : element('span', label, 'quiet');
        });
        if (!fragment.hasChildNodes()) fragment.append(element('p', 'No additional annotations for this line.', 'quiet'));
        content.replaceChildren(fragment);
    }

    function showLine(line, reveal = true) {
        selected = clamp(line);
        const nextPage = Math.floor(selected / PAGE);
        if (page !== nextPage) {
            page = nextPage;
            const left = code.scrollLeft;
            renderCode();
            code.scrollTop = 0;
            code.scrollLeft = left;
        }
        const before = code.querySelector('.selected'), row = code.querySelector(`[data-line="${selected}"]`);
        before?.classList.remove('selected');
        row.classList.add('selected');
        if (reveal) {
            const top = row.getBoundingClientRect().top - code.getBoundingClientRect().top;
            const height = row.offsetHeight;
            if (top < 0 || top + height > code.clientHeight) code.scrollTop += top - (code.clientHeight - height) / 2;
        }
        go.value = String(selected + 1);
        get('selection-status').textContent = `Line ${selected + 1}: ${text[selected]}`;
        selectOutline();
        renderDetails();
        updateSearchStatus();
        save();
    }

    function updateSearchStatus() {
        const index = lowerBound(matches, selected);
        const label = get('match-count');
        if (label.dataset.busy) return;
        label.textContent = !search.value ? '' : !matches.length ? 'No matches'
            : matchLines.has(selected) ? `${index + 1} / ${matches.length}` : `${matches.length} lines`;
        get('match-next').disabled = get('match-prev').disabled = !matches.length;
    }

    function find(move = true) {
        const run = ++searchGeneration, query = search.value.toLowerCase(), found = [];
        let index = 0;
        const count = get('match-count');
        count.dataset.busy = 'true';
        count.textContent = query ? 'Searching…' : '';
        get('match-next').disabled = get('match-prev').disabled = true;
        const scan = () => {
            if (run !== searchGeneration) return;
            const deadline = performance.now() + 7;
            while (query && index < text.length) {
                if (text[index].toLowerCase().includes(query)) found.push(index);
                if ((++index & 127) === 0 && performance.now() >= deadline) {
                    requestAnimationFrame(scan);
                    return;
                }
            }
            matches = found;
            matchLines = new Set(found);
            delete count.dataset.busy;
            for (const row of code.querySelectorAll('[data-line]'))
                row.classList.toggle('match', matchLines.has(Number(row.dataset.line)));
            if (move && found.length && !matchLines.has(selected))
                showLine(found[lowerBound(found, selected) % found.length]);
            else updateSearchStatus();
            save();
        };
        requestAnimationFrame(scan);
    }

    function moveMatch(direction) {
        if (!matches.length || get('match-count').dataset.busy) return;
        let index = lowerBound(matches, selected);
        if (direction < 0 || matches[index] === selected) index += direction;
        showLine(matches[(index + matches.length) % matches.length]);
    }

    function toggle(button, pane, visible) {
        pane.hidden = !visible;
        button.setAttribute('aria-expanded', String(visible));
        save();
    }

    category.addEventListener('change', () => { outlinePage = 0; renderOutline(); save(); });
    get('outline-prev').addEventListener('click', () => { outlinePage--; renderOutline(); save(); });
    get('outline-next').addEventListener('click', () => { outlinePage++; renderOutline(); save(); });
    get('code-prev').addEventListener('click', () => showLine(Math.max(0, page - 1) * PAGE));
    get('code-next').addEventListener('click', () => showLine(Math.min(lastPage, page + 1) * PAGE));
    get('goto').addEventListener('submit', event => { event.preventDefault(); showLine(Number(go.value) - 1); code.focus(); });
    get('toggle-outline').addEventListener('click', event => toggle(event.currentTarget, outline, outline.hidden));
    get('toggle-detail').addEventListener('click', event => {
        toggle(event.currentTarget, detail, detail.hidden);
        renderDetails();
    });
    search.addEventListener('input', () => find());
    get('match-prev').addEventListener('click', () => moveMatch(-1));
    get('match-next').addEventListener('click', () => moveMatch(1));
    code.addEventListener('scroll', save, { passive: true });
    code.addEventListener('click', event => {
        const row = event.target.closest('[data-line]');
        if (!row) return;
        const link = event.target.closest('[data-reference]');
        if (!link && !window.getSelection()?.isCollapsed) return;
        showLine(Number(row.dataset.line), false);
        code.focus({ preventScroll: true });
        if (link) follow(data.references[Number(link.dataset.reference)]);
    });
    document.addEventListener('keydown', event => {
        if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'f') {
            event.preventDefault(); search.focus(); search.select(); return;
        }
        if (event.key === 'F3' || event.target === search && event.key === 'Enter') {
            event.preventDefault(); moveMatch(event.shiftKey ? -1 : 1); return;
        }
        if (event.target !== code) return;
        const target = ({ ArrowUp: selected - 1, ArrowDown: selected + 1, PageUp: selected - PAGE,
            PageDown: selected + PAGE, Home: 0, End: text.length - 1 })[event.key];
        if (target !== undefined) { event.preventDefault(); showLine(target); }
    });
    window.addEventListener('message', event => {
        if (Number.isInteger(event.data?.reveal)) showLine(event.data.reveal);
    });
    outlinePage = state.outlinePage || 0;
    toggle(get('toggle-outline'), outline, state.outline ?? !matchMedia('(max-width: 660px)').matches);
    toggle(get('toggle-detail'), detail, state.detail ?? !matchMedia('(max-width: 660px)').matches);
    renderOutline();
    showLine(selected, false);
    if (state.page === page && state.line === selected) code.scrollTop = state.top || 0;
    else if (state.line !== undefined) showLine(selected);
    code.scrollLeft = state.left || 0;
    search.value = state.query || '';
    if (search.value) find(Boolean(state.match));
}

module.exports = { startArtifactPage };
