'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { execFile } = require('node:child_process');
const { promisify } = require('node:util');
const { createHash } = require('node:crypto');
const vscode = require('vscode');
const { connect, until } = require('./artifact-ui-browser.cjs');

const execute = promisify(execFile);

async function digest(file) {
    return createHash('sha256').update(await fs.readFile(file)).digest('hex');
}

async function run() {
    const { MSYNC_TEST_WORKSPACE: workspace, MSYNC_TEST_ROOT: runRoot, MSYNC_TEST_SERVER_PATH: binary,
        MSYNC_TEST_DEBUG_PORT: port } = process.env;
    assert.ok(workspace && runRoot && binary && port, 'Launch with MSYNC_TEST_ARTIFACT_UI_ONLY=1');
    const cwd = path.join(workspace, 'Inspector 𐐀');
    await fs.mkdir(path.join(cwd, 'input', 'nested'), { recursive: true });
    await fs.writeFile(path.join(cwd, 'input', 'one.txt'), 'first payload');
    await fs.writeFile(path.join(cwd, 'input', 'nested', 'two.txt'), 'second payload');
    const source = path.join(cwd, 'script.msync');
    const hir = path.join(cwd, 'Program.mhir'), object = path.join(cwd, 'Program.mso'), executable = path.join(cwd, 'Program.mexe');
    const bindings = Array.from({ length: 180 }, (_, index) =>
        `let value_${index} = "DATA ${index === 2 || index === 179 ? 'needle ' : ''}${index}";`);
    await fs.writeFile(source, [
        'fn identity(value: Path) -> Path { value };',
        ...bindings,
        'let result = identity(./input);',
        './input -> ./output/;',
        'value_179',
    ].join('\n') + '\n');
    const command = args => execute(binary, ['--no-config', '--no-telemetry', '--color', 'never', ...args], { cwd, encoding: 'utf8' });
    await command(['compile', source, '--emit', 'hir', '-o', hir]);
    await command(['compile', source, '-O0', '-o', object]);
    await command(['bind-ir', object, '-o', executable]);
    const files = [hir, object, executable];
    const hashes = await Promise.all(files.map(digest));
    const decode = async file => JSON.parse((await command(['disassemble', file, '--json'])).stdout);
    const browser = await connect(port);
    const workbench = vscode.workspace.getConfiguration('workbench');
    const previousTheme = workbench.get('colorTheme');
    const screenshots = [];
    const display = async file => {
        await vscode.commands.executeCommand('vscode.openWith', vscode.Uri.file(file), 'msync.bytecode');
        return browser.artifact(path.basename(file));
    };
    const evaluate = (frame, expression) => browser.evaluate(frame, expression);
    const theme = async (name, css, file) => {
        await workbench.update('colorTheme', name, vscode.ConfigurationTarget.Global);
        return browser.artifact(path.basename(file), `document.body.classList.contains(${JSON.stringify(css)})`);
    };
    const screenshot = async name => {
        const file = path.join(runRoot, name + '.png');
        await fs.writeFile(file, await browser.screenshot());
        screenshots.push(file);
    };
    const goTo = (frame, line) => evaluate(frame, `
        document.querySelector('#goto-line').value = ${line + 1};
        document.querySelector('#goto').requestSubmit();
        Number(document.querySelector('.line.selected').dataset.line);
    `);
    const selected = frame => evaluate(frame, 'Number(document.querySelector(".line.selected").dataset.line)');
    try {
        await vscode.commands.executeCommand('workbench.action.closeAuxiliaryBar');
        let frame = await display(hir);
        frame = await theme('Default Dark Modern', 'vscode-dark', hir);
        assert.equal(await evaluate(frame, 'document.querySelector(".stage").textContent'), 'Typed HIR');
        const typed = await decode(hir);
        const identity = typed.symbols.find(symbol => symbol.kind === 12 && symbol.name.includes('identity'));
        assert.ok(identity);
        await evaluate(frame, `
            const group = document.querySelector('#outline-group');
            group.value = 'Functions and templates';
            group.dispatchEvent(new Event('change'));
            Array.from(document.querySelectorAll('.outline-entry')).find(item => item.textContent.includes('identity')).click();
        `);
        assert.equal(await selected(frame), identity.selectionRange.start.line);
        assert.match(await evaluate(frame, 'document.querySelector("#detail-content").textContent'), /identity/);
        await screenshot('hir-dark');

        frame = await display(object);
        frame = await theme('Default Light Modern', 'vscode-light', object);
        assert.equal(await evaluate(frame, 'document.querySelector(".stage").textContent'), 'Relocatable bytecode');
        const model = await decode(object), lines = model.text.split('\n');
        const hits = lines.flatMap((line, index) => line.includes('needle') ? [index] : []);
        assert.equal(hits.length, 2);
        assert.ok(hits[1] - hits[0] > 160);
        await evaluate(frame, `
            const find = document.querySelector('#find');
            find.focus();
            find.value = 'needle';
            find.dispatchEvent(new Event('input'));
        `);
        frame = await browser.artifact(path.basename(object), `document.querySelector('.line.selected')?.dataset.line === "${hits[0]}"`);
        await evaluate(frame, "document.querySelector('#find').dispatchEvent(new KeyboardEvent('keydown', { key: 'F3', bubbles: true }));");
        assert.equal(await selected(frame), hits[1]);
        assert.equal(await evaluate(frame, 'document.querySelector("#match-count").textContent'), '2 / 2');
        assert.ok(await evaluate(frame, 'document.querySelectorAll(".line").length') <= 160);
        const generation = await evaluate(frame, 'Number(document.body.dataset.generation)');
        await vscode.commands.executeCommand('msync.refreshArtifact', vscode.Uri.file(object));
        frame = await browser.artifact(path.basename(object),
            `Number(document.body.dataset.generation) > ${generation} &&
             document.querySelector('#match-count')?.textContent === '2 / 2' && document.querySelector('.line.selected')?.dataset.line === "${hits[1]}"`);
        assert.equal(await evaluate(frame, 'document.querySelector("#find").value'), 'needle');
        await screenshot('object-light-search');

        const sourceIndex = model.references.findIndex(item => item.sourceUri && vscode.Uri.parse(item.sourceUri).fsPath === source);
        assert.ok(sourceIndex >= 0);
        const sourceLine = model.references[sourceIndex].range.start.line;
        assert.equal(await goTo(frame, sourceLine), sourceLine);
        await evaluate(frame, `document.querySelector('[data-reference="${sourceIndex}"]').click()`);
        await until('source navigation', () => vscode.window.activeTextEditor?.document.uri.fsPath === source);

        frame = await display(executable);
        frame = await theme('Default High Contrast', 'vscode-high-contrast', executable);
        assert.equal(await evaluate(frame, 'document.querySelector(".stage").textContent'), 'Executable bytecode');
        const bound = await decode(executable);
        const plan = bound.executionPlans.find(item => item.transfers.some(node => node.predecessors.length));
        assert.ok(plan, 'The real bound directory graph should have predecessors');
        const node = plan.transfers.find(node => node.predecessors.length);
        const parent = bound.symbols.find(symbol => symbol.name === `@p${plan.id}`);
        const item = parent.children.find(symbol => symbol.name === `@p${plan.id}n${node.id}`);
        const predecessor = parent.children.find(symbol => symbol.name === `@p${plan.id}n${node.predecessors[0]}`);
        const line = item.selectionRange.start.line;
        await evaluate(frame, `document.querySelector('[data-definition="${line}"]').click()`);
        assert.equal(await selected(frame), line);
        assert.match(await evaluate(frame, 'document.querySelector("#detail-content").textContent'), /Predecessors/);
        assert.match(await evaluate(frame, 'document.querySelector("#detail-content").textContent'), /Writes/);
        await screenshot('executable-high-contrast');
        await evaluate(frame, `
            Array.from(document.querySelectorAll('#detail-content button'))
                .find(button => button.textContent === ${JSON.stringify(predecessor.name)}).click();
        `);
        assert.equal(await selected(frame), predecessor.selectionRange.start.line);
        await vscode.commands.executeCommand('msync.openArtifactText', vscode.Uri.file(executable));
        const document = vscode.window.activeTextEditor?.document;
        assert.equal(document?.uri.scheme, 'msync-artifact');
        assert.equal(document?.getText(), bound.text);
        assert.equal(document?.isDirty, false);
        assert.deepEqual(await Promise.all(files.map(digest)), hashes);
        assert.equal(await fs.stat(path.join(cwd, 'output')).then(() => true, () => false), false);
        const result = { status: 'passed', vscodeVersion: vscode.version, stages: files.map(file => path.extname(file)),
            crossPageSearch: true, retainedQueryAndSelection: true, sourceNavigation: true, predecessorNavigation: true,
            readonly: true, noExecution: true, screenshots, hashes, artifactLines: lines.length };
        await fs.writeFile(path.join(runRoot, 'artifact-ui-results.json'), JSON.stringify(result, null, 2));
        console.log(`MSYNC_ARTIFACT_UI_HOST ${JSON.stringify(result)}`);
    } finally {
        browser.dispose();
        await workbench.update('colorTheme', previousTheme, vscode.ConfigurationTarget.Global);
    }
}

module.exports = { run };
