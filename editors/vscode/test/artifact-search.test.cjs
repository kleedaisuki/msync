'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');
const { createArtifactSearch } = require('../artifact-search.cjs');

test('completed prefixes narrow the ordered candidates and an unrelated edit rescans', () => {
    const data = ['Marker-other', 'miss', 'marker-target', 'Marker-tARGEt-extra', 'unrelated'];
    let reads = 0;
    const lines = new Proxy(data, {
        get(target, key, receiver) {
            if (/^\d+$/.test(key)) reads++;
            return Reflect.get(target, key, receiver);
        },
    });
    const search = createArtifactSearch(lines, () => 0);
    const expected = [0, 2, 3];
    search('MARKER', result => assert.deepEqual(result, expected));
    assert.equal(reads, data.length);
    search('marker-T', result => assert.deepEqual(result, [2, 3]));
    assert.equal(reads, data.length + expected.length);
    search('MaRkEr-T', result => assert.deepEqual(result, [2, 3]));
    assert.equal(reads, data.length + expected.length);
    search('unrelated', result => assert.deepEqual(result, [4]));
    assert.equal(reads, 2 * data.length + expected.length);
    search('', result => assert.deepEqual(result, []));
    assert.equal(reads, 2 * data.length + expected.length);
});

test('prefix admission uses the folded query, including context-sensitive Unicode casing', () => {
    const search = createArtifactSearch(['ΟΣ', 'ΟΣΑ', 'xΟΣΑ'], () => 0);
    search('ΟΣ', result => assert.deepEqual(result, [0]));
    search('ΟΣΑ', result => assert.deepEqual(result, [1, 2]));
});

test('large scans yield; neither replaced nor cleared scans publish stale results', () => {
    const frames = [], completed = [];
    let time = 0;
    const lines = Array.from({ length: 500 }, (_, line) => line % 2 === 0 ? 'old' : 'new');
    const search = createArtifactSearch(lines, () => time += 8, run => frames.push(run));
    search('old', result => completed.push(result));
    assert.equal(frames.length, 1);
    search('new', result => completed.push(result));
    assert.equal(frames.length, 2);
    while (frames.length) frames.shift()();
    assert.deepEqual(completed, [Array.from({ length: 250 }, (_, i) => 2 * i + 1)]);
    search('old', result => completed.push(result));
    search('', result => completed.push(result));
    while (frames.length) frames.shift()();
    assert.equal(completed.length, 2);
    assert.deepEqual(completed[1], []);
});

test('empty results stay empty under a stricter query, but backspace restores broader hits', () => {
    let reads = 0;
    const lines = { length: 1, get 0() { reads++; return 'available'; } };
    const search = createArtifactSearch(lines, () => 0);
    search('available-', result => assert.deepEqual(result, []));
    search('available-more', result => assert.deepEqual(result, []));
    assert.equal(reads, 1);
    search('available', result => assert.deepEqual(result, [0]));
    assert.equal(reads, 2);
});
