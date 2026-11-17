'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { execFile } = require('node:child_process');
const { createHash } = require('node:crypto');
const { promisify } = require('node:util');
const vscode = require('vscode');
const { connect } = require('./artifact-ui-browser.cjs');

const execute = promisify(execFile);
const queries = ['SeArCh-needle', 'SeArCh-needle-target', 'SeArCh-needle-target-rare', 'SeArCh-needle-target-rare-missing',
    'SeArCh-needle', 'unrelated-missing', ''];
const labels = ['first', 'extend', 'extend', 'extend', 'backspace', 'unrelated', 'clear'];

async function run({ workspace, runRoot, binary, port }) {
    const browser = await connect(port);
    const results = {};
    try {
        for (const [name, size] of [['small', 16], ['large', 8192]]) {
            const source = path.join(workspace, `${name}.msync`);
            const artifact = path.join(workspace, `${name}.mso`);
            const command = (...args) => execute(binary, ['--no-config', '--no-telemetry', ...args],
                { cwd: workspace, encoding: 'utf8', maxBuffer: 32 * 1024 * 1024 });
            const frozen = process.env.MSYNC_TEST_ARTIFACT_SEARCH_FIXTURES;
            if (frozen) {
                await fs.copyFile(path.join(frozen, path.basename(artifact)), artifact);
            } else {
                const constants = Array.from({ length: size }, (_, i) => {
                    const marker = i % 8192 === 1 ? 'search-needle-target-rare'
                        : i % 128 === 2 ? 'search-needle-target'
                        : i % 32 === 3 ? 'search-needle' : 'value';
                    return `"${marker}-${i} ${'artifact ordinary field '.repeat(5)}"`;
                });
                await fs.writeFile(source, '[' + constants.join(',\n') + ']\n');
                await command('compile', source, '-O0', '-o', artifact);
            }
            const disassembly = JSON.parse((await command('disassemble', artifact, '--editor-json')).stdout).text;
            const lines = disassembly.split('\n');
            const expected = queries.map(query => query ? lines.filter(line => line.toLowerCase().includes(query.toLowerCase())).length : 0);
            assert.ok(expected[0] > 0 && expected[1] > 0 && expected[2] > 0 && expected[3] === 0);
            await vscode.commands.executeCommand('vscode.openWith', vscode.Uri.file(artifact), 'msync.bytecode');
            const frame = await browser.artifact(path.basename(artifact));
            const observations = [];
            for (let pass = 0; pass < 13; pass++) {
                const cycle = await browser.evaluate(frame, `(async () => {
                const input = document.querySelector('#find'), count = document.querySelector('#match-count');
                const next = document.querySelector('#match-next');
                const samples = [];
                function submit(query) {
                    return new Promise((resolve, reject) => {
                        const started = performance.now();
                        const finish = () => {
                            if (count.dataset.busy) return;
                            observer.disconnect();
                            clearTimeout(timeout);
                            resolve({ elapsedMs: performance.now() - started, label: count.textContent, disabled: next.disabled });
                        };
                        const observer = new MutationObserver(finish);
                        const timeout = setTimeout(() => { observer.disconnect(); reject(new Error('Artifact search did not finish')); }, 3000);
                        observer.observe(count, { attributes: true, childList: true, subtree: true });
                        input.value = query;
                        input.dispatchEvent(new Event('input'));
                        finish();
                    });
                }
                for (const query of ${JSON.stringify(queries)}) {
                    samples.push(await submit(query));
                    await new Promise(resolve => requestAnimationFrame(resolve));
                }
                return samples;
                })()`);
                if (pass > 0) observations.push(...cycle);
            }
            for (const [i, sample] of observations.entries()) {
                const count = expected[i % queries.length];
                assert.equal(sample.disabled, count === 0);
                if (count === 0) assert.equal(sample.label, queries[i % queries.length] ? 'No matches' : '');
                else assert.equal(Number(/(?:\/ |^)(\d+)(?: lines)?$/.exec(sample.label)?.[1]), count, sample.label);
            }
            results[name] = { lines: lines.length, artifactBytes: (await fs.stat(artifact)).size,
                artifactSha256: createHash('sha256').update(await fs.readFile(artifact)).digest('hex'),
                disassemblySha256: createHash('sha256').update(disassembly).digest('hex'),
                expected, samples: observations.map((entry, index) => ({ ...entry, step: labels[index % labels.length] })) };
        }
        await fs.writeFile(path.join(runRoot, 'artifact-search-results.json'), JSON.stringify(results, null, 2));
        console.log(`MSYNC_ARTIFACT_SEARCH_HOST ${JSON.stringify(Object.fromEntries(Object.entries(results)
            .map(([name, result]) => [name, { lines: result.lines, samples: result.samples.length }])))}`);
    } finally {
        browser.dispose();
    }
}

module.exports = { run };
