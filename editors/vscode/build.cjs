'use strict';

const fs = require('node:fs/promises');
const path = require('node:path');
const esbuild = require('esbuild');

async function build() {
    await esbuild.build({
        entryPoints: ['extension.cjs'],
        bundle: true,
        platform: 'node',
        format: 'cjs',
        external: ['vscode'],
        minify: true,
        legalComments: 'eof',
        outfile: 'out/extension.cjs',
    });
    const lock = JSON.parse(await fs.readFile('package-lock.json', 'utf8'));
    const notices = [];
    for (const [directory, record] of Object.entries(lock.packages)) {
        if (!directory || record.dev) continue;
        const manifest = JSON.parse(await fs.readFile(path.join(directory, 'package.json'), 'utf8'));
        const filename = (await fs.readdir(directory)).find(name => /^licen[sc]e(?:\.(?:txt|md))?$/i.test(name));
        if (!filename) throw new Error(`Missing license for bundled dependency ${manifest.name}`);
        const license = await fs.readFile(path.join(directory, filename), 'utf8');
        notices.push(`${manifest.name}@${manifest.version}\n${'='.repeat(72)}\n${license.trim()}\n`);
    }
    await fs.writeFile('out/THIRD_PARTY_NOTICES.txt', notices.join('\n'));
}

build().catch(error => {
    console.error(error);
    process.exitCode = 1;
});
