'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const { createWriteStream } = require('node:fs');
const path = require('node:path');
const { Writable } = require('node:stream');
const { finished } = require('node:stream/promises');
const { createHash } = require('node:crypto');

async function main() {
    const extensionRoot = path.resolve(__dirname, '../..');
    const projectRoot = path.resolve(extensionRoot, '../..');
    const temporaryRoot = path.join(projectRoot, '.temp');
    assert.ok(process.env.MSYNC_TEST_SERVER_PATH, 'Set MSYNC_TEST_SERVER_PATH to the frozen msync executable');
    assert.ok(path.isAbsolute(process.env.MSYNC_TEST_SERVER_PATH), 'MSYNC_TEST_SERVER_PATH must be absolute');
    const serverPath = await fs.realpath(process.env.MSYNC_TEST_SERVER_PATH);
    assert.ok((await fs.stat(serverPath)).isFile(), 'The frozen msync executable must be a regular file');
    await fs.mkdir(temporaryRoot, { recursive: true });
    const runRoot = process.env.MSYNC_TEST_ROOT
        ? path.resolve(process.env.MSYNC_TEST_ROOT)
        : await fs.mkdtemp(path.join(temporaryRoot, 'vscode-host-'));
    const relative = path.relative(temporaryRoot, runRoot);
    assert.ok(relative && !relative.startsWith('..') && !path.isAbsolute(relative), 'MSYNC_TEST_ROOT must be below the project .temp directory');
    const directories = Object.fromEntries(['home', 'config', 'cache', 'data', 'runtime', 'tmp', 'user-data', 'extensions', 'workspace']
        .map(name => [name, path.join(runRoot, name)]));
    await Promise.all(Object.values(directories).map(directory => fs.mkdir(directory, { recursive: true, mode: 0o700 })));
    Object.assign(process.env, {
        HOME: directories.home,
        USERPROFILE: directories.home,
        XDG_CONFIG_HOME: directories.config,
        XDG_CACHE_HOME: directories.cache,
        XDG_DATA_HOME: directories.data,
        XDG_RUNTIME_DIR: directories.runtime,
        TMPDIR: directories.tmp,
        TMP: directories.tmp,
        TEMP: directories.tmp,
        APPDATA: directories.config,
        LOCALAPPDATA: directories.data,
    });
    for (const name of ['ELECTRON_RUN_AS_NODE', 'VSCODE_IPC_HOOK_CLI', 'VSCODE_PID']) {
        delete process.env[name];
    }
    // The official runner may capture os.tmpdir() while its modules load.
    const { runTests } = require('@vscode/test-electron');
    await fs.mkdir(path.join(directories['user-data'], 'User'), { recursive: true });
    await fs.writeFile(path.join(directories['user-data'], 'User/settings.json'), JSON.stringify({
        'msync.serverPath': serverPath,
        'telemetry.telemetryLevel': 'off',
        'update.mode': 'none',
        'extensions.autoCheckUpdates': false,
        'extensions.autoUpdate': false,
        'security.workspace.trust.enabled': false,
        'workbench.startupEditor': 'none',
        'files.autoSave': 'off',
        'editor.semanticHighlighting.enabled': true,
        'git.enabled': false,
    }, null, 2));
    await fs.writeFile(path.join(directories.workspace, 'preserved.txt'), 'keep me\n');
    const serverSha256 = createHash('sha256').update(await fs.readFile(serverPath)).digest('hex');
    const metadata = { serverPath, serverSha256, runRoot, startedAt: new Date().toISOString() };
    await fs.writeFile(path.join(runRoot, 'run.json'), JSON.stringify(metadata, null, 2));
    const log = createWriteStream(path.join(runRoot, 'host.log'));
    const tee = target => new Writable({ write(chunk, encoding, callback) {
        target.write(chunk);
        log.write(chunk, encoding, callback);
    } });
    console.log(`msync real extension-host gate: ${JSON.stringify(metadata)}`);
    try {
        await runTests({
            extensionDevelopmentPath: extensionRoot,
            extensionTestsPath: path.join(__dirname, 'suite.cjs'),
            extensionTestsEnv: {
                MSYNC_TEST_SERVER_PATH: serverPath,
                MSYNC_TEST_WORKSPACE: directories.workspace,
                MSYNC_TEST_ROOT: runRoot,
            },
            vscodeExecutablePath: process.env.MSYNC_TEST_VSCODE_EXECUTABLE,
            version: process.env.MSYNC_TEST_VSCODE_VERSION || 'stable',
            cachePath: path.join(extensionRoot, '.vscode-test'),
            launchArgs: [
                directories.workspace,
                `--user-data-dir=${directories['user-data']}`,
                `--extensions-dir=${directories.extensions}`,
                '--disable-workspace-trust',
                '--disable-updates',
                '--ozone-platform=x11',
                '--disable-gpu',
                '--skip-welcome',
                '--skip-release-notes',
            ],
            stdout: tee(process.stdout),
            stderr: tee(process.stderr),
        });
        await fs.writeFile(path.join(runRoot, 'result.json'), JSON.stringify({ ...metadata, status: 'passed' }, null, 2));
    } catch (error) {
        await fs.writeFile(path.join(runRoot, 'result.json'), JSON.stringify({
            ...metadata, status: 'failed', error: error instanceof Error ? error.stack : String(error),
        }, null, 2));
        throw error;
    } finally {
        log.end();
        await finished(log);
    }
}

main().catch(error => {
    console.error(error);
    process.exitCode = 1;
});
