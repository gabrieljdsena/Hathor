/* Headless smoke test for the Vohithe voice bus (sequential clips + done callback). */
'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

class FakeAudio {
  constructor() {
    this.attributes = {};
    this.src = '';
    this.paused = true;
    this.volume = 1;
    this.onended = null;
    this.onerror = null;
    this.onabort = null;
    audios.push(this);
  }
  set src(value) { this.attributes.src = value; }
  get src() { return this.attributes.src || ''; }
  pause() { this.paused = true; }
  load() {}
  removeAttribute(name) { delete this.attributes[name]; }
  play() {
    this.paused = false;
    setTimeout(() => { if (this.onended) this.onended(); }, 0);
    return Promise.resolve();
  }
}

const played = [];
const finished = [];
const errors = [];
const audios = [];
const api = {
  ai_dj_clip_url: (id) => {
    played.push(id);
    return Promise.resolve({ ok: true, url: 'http://voice.test/' + id + '.mp3' });
  },
  get_current_volume: () => Promise.resolve(0.38),
  ai_dj_voice_finished: (generation) => {
    finished.push(generation);
    return Promise.resolve(true);
  },
  ai_dj_voice_error: (generation, message) => {
    errors.push([generation, message]);
    return Promise.resolve(true);
  },
};
const window = { pywebview: { api } };
const context = {
  window, Audio: FakeAudio, console, setTimeout, clearTimeout,
  Promise, String, Number, Array, Math,
};
vm.createContext(context);
vm.runInContext(
  fs.readFileSync(path.join(__dirname, '..', '..', 'ui', 'js', 'ai-dj-voice.js'), 'utf8'),
  context,
);

(async () => {
  await window.AiDjVoice.play(['one', 'two'], 0.2, 7);
  assert.deepEqual(played, ['one', 'two']);
  assert.deepEqual(finished, [7]);
  assert.deepEqual(errors, []);
  assert.equal(window.AiDjVoice.isPlaying(), false);
  assert.equal(window.AiDjVoice.currentVolume(), 0.38);
  assert.equal(audios[0].volume, 0.38);
  window.AiDjVoice.setVolume(0.71);
  assert.equal(audios[0].volume, 0.71);
  window.AiDjVoice.setVolume(4);
  assert.equal(window.AiDjVoice.currentVolume(), 1);

  // A replacement play stops the previous generation without reporting it.
  window.AiDjVoice.stop();
  console.log('ai-dj-voice.js smoke test passed');
})().catch((error) => {
  console.error(error);
  process.exit(1);
});
