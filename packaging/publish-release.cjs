'use strict';
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

module.exports = async function publish({github, context, core, version, directory = process.cwd()}) {
  if (!/^\d+\.\d+\.\d+$/.test(version)) throw new Error('Expected a numeric package version');
  const repo = context.repo;
  const tag = `v${version}`;
  const digest = bytes => 'sha256:' + crypto.createHash('sha256').update(bytes).digest('hex');
  const read = file => fs.readFileSync(path.join(directory, file));
  let release;
  try { release = (await github.rest.repos.getReleaseByTag({...repo, tag})).data; }
  catch (error) { if (error.status !== 404) throw error; }
  if (release && !release.draft) {
    core.notice(`${tag} is published; assets were not changed. Bump the Cargo version for another release.`);
    return;
  }
  if (release && release.target_commitish !== context.sha) {
    throw new Error(`Existing ${tag} draft targets another revision; refusing to replace it`);
  }

  async function taggedCommit() {
    let object;
    try { object = (await github.rest.git.getRef({...repo, ref: `tags/${tag}`})).data.object; }
    catch (error) { if (error.status === 404) return null; throw error; }
    for (let depth = 0; object.type === 'tag'; depth++) {
      if (depth >= 16) throw new Error(`Too many annotated tag indirections for ${tag}`);
      object = (await github.rest.git.getTag({...repo, tag_sha: object.sha})).data.object;
    }
    if (object.type !== 'commit') throw new Error(`${tag} does not refer to a commit`);
    return object.sha;
  }

  async function requireMatchingTag() {
    if (await taggedCommit() !== context.sha) {
      throw new Error(`${tag} does not point to tested source ${context.sha}; never force or reuse a mismatched release tag`);
    }
  }

  const pointed = await taggedCommit();
  if (pointed !== null && pointed !== context.sha) {
    throw new Error(`${tag} already points to another revision; bump the version or resolve the tag explicitly`);
  }
  if (pointed === null) {
    try { await github.rest.git.createRef({...repo, ref: `refs/tags/${tag}`, sha: context.sha}); }
    catch (error) { if (error.status !== 422) throw error; }
  }
  await requireMatchingTag();

  const files = [
    `msync-${version}-windows-x86_64-setup.exe`,
    `msync-${version}-windows-x86_64.zip`,
    `msync-${version}-linux-x86_64.tar.gz`,
  ].sort();
  const sums = files.map(file => {
    const bytes = read(`dist/${file}`);
    if (!bytes.length) throw new Error(`Empty release asset: ${file}`);
    return `${digest(bytes).slice(7)}  ${file}`;
  });
  fs.writeFileSync(path.join(directory, 'dist/SHA256SUMS'), sums.join('\n') + '\n');
  files.push('SHA256SUMS');

  if (!release) {
    const notes = read(`docs/releases/${version}.md`).toString('utf8');
    release = (await github.rest.repos.createRelease({
      ...repo, tag_name: tag, target_commitish: context.sha,
      name: `msync ${tag}`, draft: true, prerelease: false,
      body: notes + `\n\nSource: [${tag}](https://github.com/${repo.owner}/${repo.repo}/tree/${tag}) (commit ${context.sha}).\n`,
    })).data;
  }
  const existing = (await github.rest.repos.listReleaseAssets({...repo, release_id: release.id})).data;
  if (existing.some(asset => !files.includes(asset.name))) throw new Error('Draft contains unverified extra assets; inspect it explicitly');
  for (const file of files) {
    const bytes = read(`dist/${file}`);
    const asset = existing.find(asset => asset.name === file);
    if (asset) {
      if (asset.digest !== digest(bytes) || asset.size !== bytes.length) throw new Error(`Draft asset differs: ${file}`);
      continue;
    }
    await github.rest.repos.uploadReleaseAsset({
      ...repo, release_id: release.id, name: file, data: bytes,
      headers: {'content-type': 'application/octet-stream', 'content-length': bytes.length},
    });
  }
  await requireMatchingTag();
  const result = await github.rest.repos.updateRelease({...repo, release_id: release.id, draft: false, make_latest: 'true'});
  core.summary.addLink(`Download msync ${tag}`, result.data.html_url);
  await core.summary.write();
};
