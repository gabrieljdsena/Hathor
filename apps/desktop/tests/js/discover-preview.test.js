/* Headless smoke test for the global Discover preview queue.
 * No browser/pywebview dependency: minimal DOM + Audio fakes.
 */
'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

class ClassList {
  constructor() { this.values = new Set(); }
  add(...names) { names.forEach((name) => this.values.add(name)); }
  remove(...names) { names.forEach((name) => this.values.delete(name)); }
  contains(name) { return this.values.has(name); }
  toggle(name, force) {
    if (force === undefined) {
      if (this.values.has(name)) { this.values.delete(name); return false; }
      this.values.add(name);
      return true;
    }
    if (force) this.values.add(name); else this.values.delete(name);
    return !!force;
  }
}

class Element {
  constructor() {
    this.classList = new ClassList();
    this.innerHTML = '';
    this.textContent = '';
    this.style = { setProperty() {} };
  }
}

const dock = new Element();
const listeners = {};

class FakeAudio {
  constructor() {
    this.paused = true;
    this.ended = false;
    this.currentTime = 0;
    this.duration = 0;
    this.volume = 1;
    this.attributes = {};
  }
  addEventListener(name, fn) { listeners[name] = fn; }
  play() {
    this.paused = false;
    this.currentTime = 0;
    this.duration = 120;
    if (listeners.play) listeners.play();
    return Promise.resolve();
  }
  pause() {
    this.paused = true;
    if (listeners.pause) listeners.pause();
  }
  load() {}
  getAttribute(name) { return this.attributes[name] || null; }
  removeAttribute(name) { delete this.attributes[name]; }
  set src(value) { this.attributes.src = value; }
  get src() { return this.attributes.src || ''; }
}

const api = {
  pause_for_preview: () => Promise.resolve(true),
  get_current_volume: () => Promise.resolve(0.7),
  resolve_discover_preview: () => Promise.resolve({
    ok: true, audioUrl: 'http://audio.test/x.mp3', videoId: 'v1',
  }),
  resolve_discover_video: () => Promise.resolve({ ok: true, videoId: 'v1' }),
  discover_feedback: () => Promise.resolve({ ok: true }),
};

const window = { pywebview: { api }, setTimeout, clearTimeout };
const document = {
  readyState: 'complete',
  getElementById(id) { return id === 'discover-preview-dock' ? dock : null; },
  addEventListener() {},
};

const context = {
  window, document, Audio: FakeAudio, console, setTimeout, clearTimeout,
  Promise, Map, Set, String, Number, Math, Object, Array, JSON,
  parseInt, parseFloat, isFinite,
};
vm.createContext(context);
const source = fs.readFileSync(
  path.join(__dirname, '..', '..', 'ui', 'js', 'discover-preview.js'),
  'utf8',
);
vm.runInContext(source, context);

(async () => {
  await window.DiscoverPreview.start([
    { title: 'Test Song', artist: 'Test Artist', source: 'similar', artworkUrl: '' },
  ], 0);
  await new Promise((resolve) => setTimeout(resolve, 50));

  assert.equal(window.DiscoverPreview.isActive(), true);
  assert.equal(dock.classList.contains('hidden'), false);
  assert.match(dock.innerHTML, /Test Song/);
  assert.match(dock.innerHTML, /Discover preview/);

  window.DiscoverPreview.seek(10);
  window.DiscoverPreview.setVolume(0.4);
  window.DiscoverPreview.stop();

  assert.equal(window.DiscoverPreview.isActive(), false);
  assert.equal(dock.classList.contains('hidden'), true);
  assert.equal(dock.innerHTML, '');
  console.log('discover-preview.js smoke test passed');
})().catch((error) => {
  console.error(error);
  process.exit(1);
});
