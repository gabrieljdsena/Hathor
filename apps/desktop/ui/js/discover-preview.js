/* Discover preview queue (global).
 *
 * Lives outside #main so previews keep playing while the user changes views.
 * Audio uses the webview's <audio> element; main playback is pygame and is
 * paused through the Python bridge when a preview starts. Python stops this
 * queue whenever a main track starts/resumes.
 */
(function () {
  'use strict';

  var LIMIT = 20;
  var PREFETCH = 2;
  var STATUS = {
    pending: 'Queued',
    resolving: 'Resolving',
    ready: 'Ready',
    playing: 'Previewing',
    failed: 'Unavailable'
  };

  var audio = null;
  var dock = null;
  var cache = new Map();
  var session = 0;
  var playToken = 0;
  var startToken = 0;
  var reported = new Set();
  var state = {
    active: false,
    tracks: [],
    index: 0,
    playing: false,
    buffering: false,
    notice: null,
    position: 0,
    duration: 0,
    queueOpen: false
  };

  function bridge() {
    return (window.pywebview && window.pywebview.api) ? window.pywebview.api : null;
  }

  function esc(value) {
    return String(value == null ? '' : value).replace(/[&<>"']/g, function (ch) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch];
    });
  }

  function keyOf(item) {
    return String((item && item.title) || '').trim().toLowerCase() + '|' +
      String((item && item.artist) || '').trim().toLowerCase();
  }

  function fmt(sec) {
    var s = Math.max(0, Math.floor(Number(sec) || 0));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  }

  function ensureAudio() {
    if (audio) return audio;
    audio = new Audio();
    audio.preload = 'none';
    audio.addEventListener('play', function () {
      state.playing = true;
      state.buffering = false;
      render();
    });
    audio.addEventListener('pause', function () {
      state.playing = false;
      render();
    });
    audio.addEventListener('timeupdate', function () {
      state.position = Number(audio.currentTime) || 0;
      syncProgress();
    });
    audio.addEventListener('loadedmetadata', function () {
      state.duration = Number.isFinite(audio.duration) ? audio.duration : 0;
      syncProgress();
    });
    audio.addEventListener('ended', function () {
      if (state.active) playIndex(state.index + 1, session);
    });
    audio.addEventListener('error', function () {
      if (!state.active || !audio.getAttribute('src') || state.buffering) return;
      var item = currentItem();
      if (!item) return;
      skipFailed(item, 'Preview failed - skipping.');
    });
    return audio;
  }

  function ensureDock() {
    if (!dock) dock = document.getElementById('discover-preview-dock');
    return dock;
  }

  function setVolume(value) {
    try {
      ensureAudio().volume = Math.max(0, Math.min(1, Number(value) || 0));
    } catch (e) { /* volume is best-effort */ }
  }

  function syncVolumeFromMain() {
    var api = bridge();
    if (!api || !api.get_current_volume) return;
    try {
      var request = api.get_current_volume();
      if (request && request.then) request.then(setVolume).catch(function () {});
    } catch (e) { /* volume sync is best-effort */ }
  }

  function currentItem() {
    var track = state.tracks[state.index];
    return track ? track.item : null;
  }

  function stopAudioOnly() {
    var el = ensureAudio();
    try {
      el.pause();
      if (el.getAttribute('src')) {
        el.removeAttribute('src');
        el.load();
      }
    } catch (e) { /* audio teardown must never throw */ }
    state.position = 0;
    state.duration = 0;
  }

  function resetState() {
    state = {
      active: false,
      tracks: [],
      index: 0,
      playing: false,
      buffering: false,
      notice: null,
      position: 0,
      duration: 0,
      queueOpen: false
    };
  }

  function render() {
    var el = ensureDock();
    if (!el) return;
    if (!state.active || state.tracks.length === 0) {
      el.classList.add('hidden');
      el.innerHTML = '';
      return;
    }
    el.classList.remove('hidden');
    var track = state.tracks[state.index] || state.tracks[0];
    var item = track.item || {};
    var art = item.artworkUrl
      ? '<img src="' + esc(item.artworkUrl) + '" alt="" class="absolute inset-0 h-full w-full object-cover" loading="lazy">'
      : '';
    var queue = state.queueOpen ? renderQueue() : '';
    var spinner = state.buffering
      ? '<span class="inline-block h-4 w-4 animate-spin rounded-full border-2 border-white/30 border-t-white" aria-hidden="true"></span>'
      : '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="currentColor" class="h-4 w-4 ' + (state.playing ? '' : 'ml-0.5') + '"><path ' + (state.playing
        ? 'd="M6.75 5.25a.75.75 0 0 1 .75-.75H9a.75.75 0 0 1 .75.75v13.5a.75.75 0 0 1-.75.75H7.5a.75.75 0 0 1-.75-.75V5.25Zm7.5 0A.75.75 0 0 1 15 4.5h1.5a.75.75 0 0 1 .75.75v13.5a.75.75 0 0 1-.75.75H15a.75.75 0 0 1-.75-.75V5.25Z"'
        : 'd="M4.5 5.653c0-1.427 1.529-2.38 2.831-1.643L20.5 12 7.331 20.007c-1.302.737-2.831-.216-2.831-1.643V5.653Z"') + ' clip-rule="evenodd"/></svg>';

    el.innerHTML =
      '<div class="relative mx-auto w-full max-w-5xl">' + queue +
      '<div class="flex items-center gap-2.5 rounded-t-xl border-t border-orange-500/20 bg-black/75 px-3 py-2 backdrop-blur-2xl">' +
      '<div class="relative h-10 w-10 shrink-0 overflow-hidden rounded-lg bg-zinc-800">' +
      '<div class="absolute inset-0 flex items-center justify-center text-zinc-600"><svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="currentColor" class="h-5 w-5"><path d="M9 19V6l12-3v13M9 19c0 1.105-1.343 2-3 2s-3-.895-3-2 1.343-2 3-2 3 .895 3 2zm12-3c0 1.105-1.343 2-3 2s-3-.895-3-2 1.343-2 3-2 3 .895 3 2zM9 10l12-3"/></svg></div>' + art + '</div>' +
      '<div class="min-w-0 flex-1"><div class="flex items-center gap-2"><span class="truncate text-sm font-medium text-zinc-100">' + esc(item.title || 'Discover preview') + '</span><span class="hidden shrink-0 text-[9px] font-semibold uppercase tracking-[0.18em] text-orange-400/80 sm:inline">Discover preview</span></div>' +
      '<div class="truncate text-xs text-zinc-500">' + esc(item.artist || '') + '</div></div>' +
      '<span class="hidden shrink-0 text-[10px] font-mono text-zinc-500 sm:inline">' + (state.index + 1) + ' / ' + state.tracks.length + '</span>' +
      '<button type="button" onclick="window.DiscoverPreview.prev()" title="Previous preview" aria-label="Previous preview" class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-zinc-400 transition-colors hover:bg-white/5 hover:text-white cursor-pointer"><svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="currentColor" class="h-4 w-4"><path d="M6 5h2v14H6zM20 5v14l-9-7 9-7z"/></svg></button>' +
      '<button type="button" onclick="window.DiscoverPreview.toggle()" ' + (state.buffering ? 'disabled' : '') + ' title="' + (state.playing ? 'Pause preview' : 'Play preview') + '" aria-label="' + (state.playing ? 'Pause preview' : 'Play preview') + '" class="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-orange-500 text-white shadow-lg shadow-orange-500/30 transition-colors hover:bg-orange-400 disabled:cursor-default disabled:opacity-70 cursor-pointer">' + spinner + '</button>' +
      '<button type="button" onclick="window.DiscoverPreview.next()" title="Next preview" aria-label="Next preview" class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-zinc-400 transition-colors hover:bg-white/5 hover:text-white cursor-pointer"><svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="currentColor" class="h-4 w-4"><path d="M16 5h2v14h-2zM4 5v14l9-7-9-7z"/></svg></button>' +
      '<button type="button" onclick="window.DiscoverPreview.toggleQueue()" title="Preview queue" aria-label="Preview queue" class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full ' + (state.queueOpen ? 'bg-orange-500/10 text-orange-400' : 'text-zinc-400 hover:bg-white/5 hover:text-white') + ' cursor-pointer"><svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" class="h-4 w-4"><path d="M4 6h16M4 10h16M4 14h16M4 18h8m4-4v8m-4-4h8"/></svg></button>' +
      '<button type="button" onclick="window.DiscoverPreview.stop()" title="Close preview" aria-label="Close preview" class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-zinc-500 transition-colors hover:bg-white/5 hover:text-white cursor-pointer"><svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" class="h-4 w-4"><path d="M6 6l12 12M18 6 6 18"/></svg></button></div>' +
      '<div class="mt-1 flex items-center gap-2">' +
      '<span id="discover-preview-current" class="w-9 shrink-0 text-right text-[10px] font-mono text-zinc-500">' + fmt(state.position) + '</span>' +
      '<input id="discover-preview-seek" type="range" min="0" max="' + (state.duration || 0) + '" step="0.1" value="' + Math.min(state.position, state.duration || 0) + '" oninput="window.DiscoverPreview.seek(parseFloat(this.value))" aria-label="Preview position" class="min-w-0 flex-1 cursor-pointer" ' + (state.duration > 0 ? '' : 'disabled') + '>' +
      '<span id="discover-preview-total" class="w-9 shrink-0 text-[10px] font-mono text-zinc-500">' + (state.duration > 0 ? fmt(state.duration) : '--:--') + '</span></div>' +
      (state.notice ? '<div role="status" aria-live="polite" class="mt-1 truncate text-[11px] text-zinc-400">' + esc(state.notice) + '</div>' : '') +
      '</div></div>';
    syncProgress();
  }

  function renderQueue() {
    var rows = state.tracks.map(function (track, index) {
      var item = track.item || {};
      var active = index === state.index;
      var status = STATUS[track.status] || track.status || '';
      return '<button type="button" onclick="window.DiscoverPreview.jumpTo(' + index + ')" class="flex w-full items-center gap-3 rounded-xl px-3 py-2 text-left transition-colors cursor-pointer ' + (active ? 'bg-orange-500/10 text-orange-300' : 'text-zinc-300 hover:bg-white/5') + '">' +
        '<span class="w-4 text-right text-[10px] font-mono text-zinc-600">' + (active && state.playing ? '<span class="inline-flex h-2 w-2 rounded-full bg-orange-400"></span>' : (index + 1)) + '</span>' +
        '<span class="min-w-0 flex-1"><span class="block truncate text-xs font-medium">' + esc(item.title || '') + '</span><span class="block truncate text-[10px] text-zinc-500">' + esc(item.artist || '') + '</span></span>' +
        '<span class="shrink-0 text-[9px] font-semibold uppercase tracking-widest ' + (track.status === 'failed' ? 'text-red-400' : track.status === 'resolving' ? 'text-orange-400' : 'text-zinc-600') + '">' + esc(status) + '</span></button>';
    }).join('');
    return '<div id="discover-preview-queue" class="absolute bottom-full left-0 right-0 mb-2 max-h-64 overflow-y-auto rounded-2xl border border-white/10 bg-zinc-950/95 shadow-2xl backdrop-blur-2xl">' +
      '<div class="sticky top-0 flex items-center justify-between border-b border-white/5 bg-zinc-950/95 px-4 py-2 text-[10px] font-semibold uppercase tracking-[0.18em] text-zinc-500"><span>Preview queue</span><span>' + (state.index + 1) + ' / ' + state.tracks.length + '</span></div>' +
      '<div class="p-1.5">' + rows + '</div></div>';
  }

  function syncProgress() {
    var range = document.getElementById('discover-preview-seek');
    var current = document.getElementById('discover-preview-current');
    var total = document.getElementById('discover-preview-total');
    if (current) current.textContent = fmt(state.position);
    if (total) total.textContent = state.duration > 0 ? fmt(state.duration) : '--:--';
    if (!range) return;
    range.max = state.duration || 0;
    range.value = Math.min(state.position, state.duration || 0);
    range.disabled = !(state.duration > 0);
    var pct = state.duration > 0 ? Math.min(100, Math.max(0, (state.position / state.duration) * 100)) : 0;
    range.style.setProperty('--range-percent', pct + '%');
  }

  function sendFeedback(item, event) {
    var api = bridge();
    if (!api || !api.discover_feedback || !item) return;
    try {
      var request = api.discover_feedback({
        title: item.title || '',
        artist: item.artist || '',
        mbid: item.mbid || null,
        source: item.source || 'chart',
        event: event,
        seedArtist: item.seedArtist || null,
        score: item.score == null ? null : item.score
      });
      if (request && request.catch) request.catch(function () {});
    } catch (e) { /* feedback is best-effort */ }
  }

  function reportStarted(item) {
    var key = keyOf(item);
    if (reported.has(key)) return;
    reported.add(key);
    sendFeedback(item, 'preview');
  }

  function resolveItem(item, force) {
    var key = keyOf(item);
    if (!force && cache.has(key)) return cache.get(key);
    var pending = (async function () {
      var api = bridge();
      if (!api || !api.resolve_discover_preview) {
        throw new Error('Preview bridge unavailable.');
      }
      var res = await api.resolve_discover_preview(item.title || '', item.artist || '');
      if (!res || !res.ok || !res.audioUrl) {
        throw new Error((res && res.message) || 'Preview unavailable.');
      }
      return res;
    })();
    cache.set(key, pending);
    pending.catch(function () {
      if (cache.get(key) === pending) cache.delete(key);
    });
    return pending;
  }

  function skipFailed(item, message) {
    state.playing = false;
    state.buffering = false;
    state.notice = message + (item && item.title ? ' (' + item.title + ')' : '');
    if (state.tracks[state.index]) state.tracks[state.index].status = 'failed';
    render();
    var target = state.index + 1;
    window.setTimeout(function () {
      if (state.active && target === state.index + 1) playIndex(target, session);
    }, 500);
  }

  async function playIndex(target, expectedSession) {
    if (expectedSession !== session) return;
    if (target < 0 || target >= state.tracks.length) {
      stopAudioOnly();
      state.playing = false;
      state.buffering = false;
      state.notice = state.tracks.length ? 'End of preview queue.' : null;
      render();
      return;
    }
    var token = ++playToken;
    state.index = target;
    state.playing = false;
    state.buffering = true;
    state.position = 0;
    state.duration = 0;
    state.notice = null;
    state.tracks = state.tracks.map(function (track, index) {
      return index === target ? Object.assign({}, track, { status: 'resolving' }) : track;
    });
    render();
    stopAudioOnly();
    if (expectedSession !== session || token !== playToken) return;

    var track = state.tracks[target];
    var item = track.item;
    var resolved;
    try {
      resolved = await resolveItem(item, false);
    } catch (err) {
      if (expectedSession !== session || token !== playToken) return;
      state.tracks[target] = Object.assign({}, state.tracks[target], { status: 'failed' });
      state.buffering = false;
      state.playing = false;
      state.notice = (err && err.message ? err.message : 'Preview unavailable') + ' - skipping.';
      render();
      window.setTimeout(function () {
        if (state.active && token === playToken) playIndex(target + 1, session);
      }, 650);
      return;
    }
    if (expectedSession !== session || token !== playToken) return;
    state.tracks[target] = Object.assign({}, state.tracks[target], resolved, { status: 'ready' });
    state.buffering = false;
    render();

    var el = ensureAudio();
    el.src = resolved.audioUrl;
    el.currentTime = 0;
    try {
      await el.play();
    } catch (err) {
      if (expectedSession !== session || token !== playToken) return;
      if (err && err.name === 'NotAllowedError') {
        state.playing = false;
        state.buffering = false;
        state.notice = 'Press play to start the preview.';
        render();
        return;
      }
      try {
        var retry = await resolveItem(item, true);
        if (expectedSession !== session || token !== playToken) return;
        state.tracks[target] = Object.assign({}, state.tracks[target], retry, { status: 'ready' });
        el.src = retry.audioUrl;
        el.currentTime = 0;
        await el.play();
        resolved = retry;
      } catch (retryErr) {
        if (expectedSession !== session || token !== playToken) return;
        skipFailed(item, 'Preview failed.');
        return;
      }
    }
    if (expectedSession !== session || token !== playToken) return;
    state.tracks[target] = Object.assign({}, state.tracks[target], resolved, { status: 'playing' });
    state.playing = true;
    state.buffering = false;
    state.notice = null;
    render();
    reportStarted(item);

    for (var offset = 1; offset <= PREFETCH; offset += 1) {
      var next = state.tracks[target + offset];
      if (next) resolveItem(next.item, false).catch(function () {});
    }
  }

  async function pauseMain() {
    var api = bridge();
    if (api && api.pause_for_preview) {
      try { await api.pause_for_preview(); } catch (e) { /* best effort */ }
    }
  }

  var DiscoverPreview = {
    start: async function (items, startIndex) {
      var list = Array.isArray(items) ? items : [];
      if (!list.length) return;
      var index = Math.max(0, Math.min(Number(startIndex) || 0, list.length - 1));
      var windowItems = list.slice(index, index + LIMIT);
      if (!windowItems.length) return;
      var token = ++startToken;
      syncVolumeFromMain();
      await pauseMain();
      // Main playback (or a newer preview) may have taken over while the
      // Python pause call was in flight; do not resurrect a stale start.
      if (token !== startToken) return;
      session += 1;
      playToken += 1;
      reported = new Set();
      stopAudioOnly();
      state = {
        active: true,
        tracks: windowItems.map(function (item) { return { item: item, status: 'pending' }; }),
        index: 0,
        playing: false,
        buffering: true,
        notice: null,
        position: 0,
        duration: 0,
        queueOpen: false
      };
      render();
      playIndex(0, session);
    },
    toggle: async function () {
      if (!state.active || !state.tracks.length || state.buffering) return;
      var el = ensureAudio();
      if (!el.getAttribute('src')) {
        await pauseMain();
        playIndex(state.index, session);
        return;
      }
      if (el.paused) {
        await pauseMain();
        try {
          if (el.ended) el.currentTime = 0;
          await el.play();
        } catch (err) {
          state.playing = false;
          state.notice = 'Preview playback blocked.';
          render();
        }
      } else {
        el.pause();
      }
    },
    next: function () {
      if (!state.active) return;
      playIndex(state.index + 1, session);
    },
    prev: function () {
      if (!state.active) return;
      playIndex(Math.max(0, state.index - 1), session);
    },
    jumpTo: function (index) {
      if (!state.active) return;
      playIndex(Number(index) || 0, session);
    },
    seek: function (seconds) {
      if (!state.active) return;
      var el = ensureAudio();
      try {
        el.currentTime = Number(seconds) || 0;
        state.position = el.currentTime || 0;
      } catch (e) { /* seeking before metadata is best-effort */ }
      syncProgress();
    },
    toggleQueue: function () {
      state.queueOpen = !state.queueOpen;
      render();
    },
    stop: function () {
      startToken += 1;
      session += 1;
      playToken += 1;
      stopAudioOnly();
      resetState();
      render();
    },
    isActive: function () {
      return !!(state.active && state.tracks.length);
    },
    setVolume: setVolume,
    resolveVideo: async function (item) {
      var api = bridge();
      if (!api || !api.resolve_discover_video) throw new Error('Video preview unavailable.');
      var res = await api.resolve_discover_video((item && item.title) || '', (item && item.artist) || '');
      if (!res || !res.ok || !res.videoId) throw new Error((res && res.message) || 'No video found.');
      return res;
    }
  };

  window.DiscoverPreview = DiscoverPreview;
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', ensureDock);
  } else {
    ensureDock();
  }
})();
