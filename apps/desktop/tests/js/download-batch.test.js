'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const context = { window: {}, String };
vm.createContext(context);
vm.runInContext(
  fs.readFileSync(path.join(__dirname, '..', '..', 'ui', 'js', 'download-batch.js'), 'utf8'),
  context,
);

const parse = context.window.parseBatchQueries;
const same = (input, expected) => assert.equal(JSON.stringify(parse(input)), JSON.stringify(expected));
same('', []);
same('  \r\n\n', []);
same('Song A\nArtist B\r\n\r\n  Song C  ', ['Song A', 'Artist B', 'Song C']);
same('One\nOne\nTwo', ['One', 'One', 'Two']);
console.log('download-batch.js smoke test passed');
