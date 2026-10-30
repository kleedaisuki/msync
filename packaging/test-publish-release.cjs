'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const publish = require('./publish-release.cjs');

function fixture(t, options = {}) {
  const scratch = path.join(__dirname, '../.temp');
  fs.mkdirSync(scratch, {recursive: true});
  const directory = fs.mkdtempSync(path.join(scratch, 'publish-test-'));
  t.after(() => fs.rmSync(directory, {recursive: true, force: true}));
  fs.mkdirSync(path.join(directory, 'dist'));
  fs.mkdirSync(path.join(directory, 'docs/releases'), {recursive: true});
  fs.writeFileSync(path.join(directory, 'docs/releases/0.4.0.md'), 'fixture notes');
  for (const file of ['msync-0.4.0-windows-x86_64-setup.exe', 'msync-0.4.0-windows-x86_64.zip', 'msync-0.4.0-linux-x86_64.tar.gz']) {
    fs.writeFileSync(path.join(directory, 'dist', file), `fresh ${file}`);
  }
  let tag = options.tag ?? null;
  const calls = [];
  const missing = () => { throw Object.assign(new Error('not found'), {status: 404}); };
  const github = {rest: {
    git: {
      getRef: async () => tag ? {data: {object: tag}} : missing(),
      getTag: async () => ({data: {object: options.annotatedTarget}}),
      createRef: async ({sha}) => {calls.push('tag'); tag = {type: 'commit', sha: options.tagRace ?? sha};},
    },
    repos: {
      getReleaseByTag: async () => options.release ? {data: options.release} : missing(),
      createRelease: async args => {calls.push('draft'); assert.equal(args.draft, true); return {data: {id: 1}};},
      listReleaseAssets: async () => ({data: options.assets ?? []}),
      uploadReleaseAsset: async args => {
        calls.push(args.name); assert.equal(args.headers['content-length'], args.data.length);
        if (options.uploadFailure) throw new Error('upload failed');
      },
      updateRelease: async () => {calls.push('publish'); return {data: {html_url: 'https://example.test/release'}};},
    },
  }};
  const core = {notice() {}, summary: {addLink() {}, async write() {}}};
  return {calls, directory, run: () => publish({github, core, context: {repo: {owner: 'fixture', repo: 'repo'}, sha: 'tested'}, version: '0.4.0', directory})};
}

test('create exact source tag, draft and every checksummed asset before publication', async t => {
  const f = fixture(t); await f.run();
  assert.equal(f.calls[0], 'tag'); assert.equal(f.calls[1], 'draft');
  assert.equal(f.calls.at(-1), 'publish'); assert.equal(f.calls.length, 7);
  const sums = fs.readFileSync(path.join(f.directory, 'dist/SHA256SUMS'), 'utf8');
  assert.equal(sums.trim().split('\n').length, 3);
  assert.match(sums, /^[a-f0-9]{64}  msync-/);
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
