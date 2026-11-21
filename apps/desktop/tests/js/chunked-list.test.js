'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

class FakeElement {
  constructor(tag) {
    this.tagName = tag;
    this.children = [];
    this.parentNode = null;
    this.className = '';
    this.attributes = {};
    this._html = '';
  }
  set innerHTML(value) {
    this._html = String(value || '');
    this.children = [];
  }
  get innerHTML() { return this._html; }
  appendChild(child) {
    child.parentNode = this;
    this.children.push(child);
    return child;
  }
  insertBefore(child, ref) {
    const index = this.children.indexOf(ref);
    if (index < 0) return this.appendChild(child);
    child.parentNode = this;
    this.children.splice(index, 0, child);
    return child;
  }
  remove() {
    if (!this.parentNode) return;
    const index = this.parentNode.children.indexOf(this);
    if (index >= 0) this.parentNode.children.splice(index, 1);
    this.parentNode = null;
  }
  setAttribute(name, value) { this.attributes[name] = String(value); }
}

class FakeObserver {
  constructor(callback) {
    this.callback = callback;
    this.connected = true;
    this.targets = [];
    FakeObserver.instances.push(this);
  }
  observe(target) { this.targets.push(target); }
  disconnect() { this.connected = false; }
  trigger() {
    this.callback(this.targets.map((target) => ({ isIntersecting: true, target })));
  }
}
FakeObserver.instances = [];

const context = {
  window: {},
  document: { createElement: (tag) => new FakeElement(tag) },
  IntersectionObserver: FakeObserver,
  Array,
  Math,
  Number,
  String,
};
vm.createContext(context);
vm.runInContext(
  fs.readFileSync(path.join(__dirname, '..', '..', 'ui', 'js', 'chunked-list.js'), 'utf8'),
  context,
);

const container = new FakeElement('div');
const items = Array.from({ length: 500 }, (_, i) => i + 1);
const state = context.window.renderChunkedList(container, items, (value) => {
  const row = new FakeElement('div');
  row.textContent = String(value);
  return row;
}, { chunkSize: 200 });

const rows = () => container.children.filter((child) => !child.className.includes('chunked-list-sentinel'));
assert.equal(rows().length, 200);
assert.equal(container.children.length, 201); // rows + sentinel
FakeObserver.instances[0].trigger();
assert.equal(rows().length, 400);
FakeObserver.instances[0].trigger();
assert.equal(rows().length, 500);
assert.equal(container.children.length, 500); // sentinel removed
assert.equal(FakeObserver.instances[0].connected, false);

// Re-rendering replaces the old observer/list rather than appending to it.
context.window.renderChunkedList(container, [1, 2, 3], (value) => {
  const row = new FakeElement('div');
  row.textContent = String(value);
  return row;
}, { chunkSize: 200 });
assert.equal(rows().length, 3);
assert.equal(container.children.length, 3);
assert.equal(FakeObserver.instances[1].connected, false);
console.log('chunked-list.js smoke test passed');
