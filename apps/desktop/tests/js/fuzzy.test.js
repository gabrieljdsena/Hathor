/* Headless table tests mirroring apps/web/.../fuzzy.test.ts. */
'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const context = { window: {}, String, Math, Array };
vm.createContext(context);
vm.runInContext(
  fs.readFileSync(path.join(__dirname, '..', '..', 'ui', 'js', 'fuzzy.js'), 'utf8'),
  context,
);
const { fuzzyMatch, fuzzyFields } = context.window;

const matches = [
  ['Bohemian Rhapsody Queen', 'bohemian'],
  ['Bohemian Rhapsody Queen', 'Queen Rhapsody'],
  ['Bohemian Rhapsody Queen', 'Bohem'],
  ['Anything', ''],
  ['Beyoncé', 'beyonce'],
];
for (const [hay, query] of matches) {
  assert.equal(fuzzyMatch(hay, query), true, `${hay} ~ ${query}`);
}

const typos = [
  ['Bohemian Rhapsody', 'Bohemain'],
  ['Bohemian Rhapsody', 'Bohemain Rapsody'],
  ['Bohemian Rhapsody', 'Rhapsodyy'],
  ['Bohemian Rhapsody', 'Rhaposdy'],
  ['Bohemian Rhapsody', 'Rahpsody'],
  ['The Beatles', 'Beetles'],
  ['Nothing Else Matters', 'Nothnig Else Maters'],
];
for (const [hay, query] of typos) {
  assert.equal(fuzzyMatch(hay, query), true, `${hay} ~ ${query}`);
}

const rejects = [
  ['Bohemian Rhapsody', 'Stairway to Heaven'],
  ['Bohemian Rhapsody', 'Bhmn'],
  ['Bohemian Rhapsody', 'xyz'],
  ['Bohemian Rhapsody', 'Bohemian Jazz'],
  ['', 'query'],
];
for (const [hay, query] of rejects) {
  assert.equal(fuzzyMatch(hay, query), false, `${hay} ~ ${query}`);
}

assert.equal(fuzzyFields(['Bohemian Rhapsody', 'Queen', 'A Night at the Opera'], 'quuen opera'), true);
assert.equal(fuzzyFields(['Song'], '   '), true);
assert.equal(fuzzyFields(['Song'], 'xyz'), false);
console.log('fuzzy.js smoke test passed');
