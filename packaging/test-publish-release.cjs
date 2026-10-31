'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const publish = require('./publish-release.cjs');

function fixture(t, options = {}) {
  const scratch = path.join(__dirname, '../.temp');
  fs.mkdirSync(scratch, {recursive: true});
  const directory = fs.mkdtempSync(path.join(scratch, 'publish-test-'));
  t.after(() => fs.rmSync(directory, {recursive: true, force: true}));
  fs.mkdirSync(path.join(directory, 'dist'));
  fs.mkdirSync(path.join(directory, 'docs/releases'), {recursive: true});
  fs.writeFileSync(path.join(directory, 'docs/releases/0.6.0.md'),
    'fixture [contract](../design/language-v6.md#booleans) [external](https://example.test) [section](#section)');
  for (const file of ['msync-0.6.0-windows-x86_64-setup.exe', 'msync-0.6.0-windows-x86_64.zip', 'msync-0.6.0-linux-x86_64.tar.gz', 'msync-language-0.6.0.vsix']) {
    fs.writeFileSync(path.join(directory, 'dist', file), `fresh ${file}`);
  }
  let tag = options.tag ?? null;
  const calls = [];
  const bodies = [];
  const missing = () => { throw Object.assign(new Error('not found'), {status: 404}); };
  const github = {rest: {
    git: {
      getRef: async () => tag ? {data: {object: tag}} : missing(),
      getTag: async () => ({data: {object: options.annotatedTarget}}),
      createRef: async ({sha}) => {calls.push('tag'); tag = {type: 'commit', sha: options.tagRace ?? sha};},
    },
    repos: {
      getReleaseByTag: async () => options.release ? {data: options.release} : missing(),
      createRelease: async args => {calls.push('draft'); bodies.push(args.body); assert.equal(args.draft, true); return {data: {id: 1}};},
      listReleaseAssets: async () => ({data: options.assets ?? []}),
      uploadReleaseAsset: async args => {
        calls.push(args.name); assert.equal(args.headers['content-length'], args.data.length);
        if (options.uploadFailure) throw new Error('upload failed');
      },
      updateRelease: async () => {calls.push('publish'); return {data: {html_url: 'https://example.test/release'}};},
    },
  }};
  const core = {notice() {}, summary: {addLink() {}, async write() {}}};
  return {calls, bodies, directory, run: () => publish({github, core, context: {repo: {owner: 'fixture', repo: 'repo'}, sha: 'tested'}, version: '0.6.0', directory})};
}

test('create exact source tag, draft and every checksummed asset before publication', async t => {
  const f = fixture(t); await f.run();
  assert.equal(f.calls[0], 'tag'); assert.equal(f.calls[1], 'draft');
  assert.equal(f.calls.at(-1), 'publish'); assert.equal(f.calls.length, 8);
  const sums = fs.readFileSync(path.join(f.directory, 'dist/SHA256SUMS'), 'utf8');
  assert.equal(sums.trim().split('\n').length, 4);
  assert.match(sums, /^[a-f0-9]{64}  msync-/);
  const extension = fs.readFileSync(path.join(f.directory, 'dist/msync-language-0.6.0.vsix'));
  const checksum = crypto.createHash('sha256').update(extension).digest('hex');
  assert(sums.includes(`${checksum}  msync-language-0.6.0.vsix\n`));
  assert(f.calls.includes('msync-language-0.6.0.vsix'));
});
test('release notes link to tagged source while retaining external and fragment links', async t => {
  const f = fixture(t); await f.run();
  assert(f.bodies[0].includes('[contract](https://github.com/fixture/repo/blob/v0.6.0/docs/design/language-v6.md#booleans)'));
  assert(f.bodies[0].includes('[external](https://example.test) [section](#section)'));
});
test('a missing or empty extension cannot reserve a tag or publish an incomplete release', async t => {
  for (const empty of [false, true]) {
    const f = fixture(t);
    const extension = path.join(f.directory, 'dist/msync-language-0.6.0.vsix');
    if (empty) fs.writeFileSync(extension, '');
    else fs.unlinkSync(extension);
    await assert.rejects(f.run(), empty ? /Empty release asset/ : /ENOENT/);
    assert.deepEqual(f.calls, []);
  }
});
test('draft resumption verifies the extension digest instead of replacing it', async t => {
  const assets = [];
  const f = fixture(t, {
    tag: {type: 'commit', sha: 'tested'},
    release: {id: 1, draft: true, target_commitish: 'tested'},
    assets,
  });
  const bytes = fs.readFileSync(path.join(f.directory, 'dist/msync-language-0.6.0.vsix'));
  assets.push({
    name: 'msync-language-0.6.0.vsix', size: bytes.length,
    digest: 'sha256:' + crypto.createHash('sha256').update(bytes).digest('hex'),
  });
  await f.run();
  assert(!f.calls.includes('msync-language-0.6.0.vsix'));
  assert.equal(f.calls.at(-1), 'publish');

  const changed = fixture(t, {
    release: {id: 1, draft: true, target_commitish: 'tested'},
    assets: [{...assets[0], digest: 'sha256:changed'}],
  });
  await assert.rejects(changed.run(), /Draft asset differs: msync-language/);
  assert(!changed.calls.includes('publish'));
});
test('reject mismatched existing lightweight tag without changing release', async t => {
  const f = fixture(t, {tag: {type: 'commit', sha: 'old'}});
  await assert.rejects(f.run(), /another revision/); assert.deepEqual(f.calls, []);
});
test('annotated tags are dereferenced and must match the tested commit', async t => {
  const f = fixture(t, {tag: {type: 'tag', sha: 'annotation'}, annotatedTarget: {type: 'commit', sha: 'tested'}});
  await f.run(); assert.equal(f.calls[0], 'draft'); assert.equal(f.calls.at(-1), 'publish');
  const bad = fixture(t, {tag: {type: 'tag', sha: 'annotation'}, annotatedTarget: {type: 'commit', sha: 'other'}});
  await assert.rejects(bad.run(), /another revision/); assert.deepEqual(bad.calls, []);
});
test('a tag creation race cannot substitute untested source', async t => {
  const f = fixture(t, {tagRace: 'other'});
  await assert.rejects(f.run(), /does not point to tested/); assert.deepEqual(f.calls, ['tag']);
});
test('published versions are never modified', async t => {
  const f = fixture(t, {release: {draft: false}}); await f.run(); assert.deepEqual(f.calls, []);
});
test('draft resume refuses changed or unverified assets', async t => {
  const f = fixture(t, {release: {id: 1, draft: true, target_commitish: 'tested'}, assets: [{name: 'foreign.exe'}]});
  await assert.rejects(f.run(), /unverified extra/); assert(!f.calls.includes('publish'));
});
test('failed asset upload never publishes a partial release', async t => {
  const f = fixture(t, {uploadFailure: true}); await assert.rejects(f.run(), /upload failed/);
  assert(!f.calls.includes('publish'));
});
