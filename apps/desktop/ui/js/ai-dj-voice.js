/* Vohithe commentary voice bus.
 *
 * Runs in the webview as a sequential <audio> queue while Python ducks the
 * pygame music output. Python owns the transition decision; this script only
 * reports natural completion/errors so the next batch is not cut off.
 */
(function () {
  'use strict';

  var audio = null;
  var generation = 0;
  var stopped = true;
  var currentReject = null;
  var volume = 1;

  function bridge() {
    return (window.pywebview && window.pywebview.api) ? window.pywebview.api : null;
  }

  function ensureAudio() {
    if (audio) return audio;
    audio = new Audio();
    audio.preload = 'auto';
    audio.volume = volume;
    return audio;
  }

  function setVolume(value) {
    volume = Math.max(0, Math.min(1, Number(value) || 0));
    if (audio) {
      try { audio.volume = volume; } catch (e) { /* volume is best-effort */ }
    }
    return volume;
  }

  async function syncVolumeFromMain() {
    var api = bridge();
    if (!api || !api.get_current_volume) return volume;
    try {
      var value = api.get_current_volume();
      if (value && typeof value.then === 'function') value = await value;
      return setVolume(value);
    } catch (e) {
      return volume;
    }
  }

  function notifyFinished(gen) {
    var api = bridge();
    if (api && api.ai_dj_voice_finished) {
      try { api.ai_dj_voice_finished(gen).catch(function () {}); } catch (e) {}
    }
  }

  function notifyError(gen, message) {
    var api = bridge();
    if (api && api.ai_dj_voice_error) {
      try { api.ai_dj_voice_error(gen, message || 'Voice playback failed.').catch(function () {}); } catch (e) {}
    }
  }

  function playOne(url) {
    return new Promise(function (resolve, reject) {
      var el = ensureAudio();
      var settled = false;
      function done(ok, error) {
        if (settled) return;
        settled = true;
        currentReject = null;
        el.onended = null;
        el.onerror = null;
        el.onabort = null;
        if (ok) resolve();
        else reject(error || new Error('Voice clip failed.'));
      }
      currentReject = function (error) { done(false, error || new Error('Voice stopped.')); };
      el.onended = function () { done(true); };
      el.onerror = function () { done(false, new Error('Voice clip could not be played.')); };
      el.onabort = function () { done(false, new Error('Voice clip was aborted.')); };
      try {
        el.volume = volume;
        el.src = url;
        el.currentTime = 0;
        el.play().catch(function (error) { done(false, error); });
      } catch (error) {
        done(false, error);
      }
    });
  }

  function stop() {
    generation += 1;
    stopped = true;
    if (currentReject) {
      try { currentReject(new Error('Voice stopped.')); } catch (e) {}
      currentReject = null;
    }
    if (audio) {
      try {
        audio.pause();
        audio.removeAttribute('src');
        audio.load();
      } catch (e) { /* teardown must never throw */ }
    }
  }

  async function play(ids, duckLevel, gen) {
    stop();
    generation = Number(gen) || 0;
    stopped = false;
    var list = Array.isArray(ids) ? ids : [];
    if (!list.length) {
      stopped = true;
      notifyFinished(generation);
      return;
    }
    try {
      var api = bridge();
      if (!api || !api.ai_dj_clip_url) throw new Error('Voice bridge unavailable.');
      await syncVolumeFromMain();
      if (generation !== Number(gen) || stopped) return;
      for (var i = 0; i < list.length; i += 1) {
        if (generation !== Number(gen) || stopped) return;
        var res = await api.ai_dj_clip_url(list[i]);
        if (!res || !res.ok || !res.url) {
          throw new Error((res && res.message) || 'Voice clip unavailable.');
        }
        if (generation !== Number(gen) || stopped) return;
        await playOne(res.url);
      }
      if (generation === Number(gen) && !stopped) {
        stopped = true;
        notifyFinished(generation);
      }
    } catch (error) {
      if (generation !== Number(gen) || stopped) return;
      stopped = true;
      notifyError(generation, error && error.message ? error.message : 'Voice playback failed.');
    }
  }

  window.AiDjVoice = {
    play: play,
    stop: stop,
    setVolume: setVolume,
    syncVolumeFromMain: syncVolumeFromMain,
    isPlaying: function () { return !stopped; },
    currentVolume: function () { return volume; }
  };
})();
