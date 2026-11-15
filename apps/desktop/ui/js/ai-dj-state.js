/* App-wide AI DJ state bridge: nav label, Home card, player-bar popover. */
(function () {
  'use strict';

  var view = null;
  var refreshing = false;
  var startBusy = false;

  function api() {
    return (window.pywebview && window.pywebview.api) ? window.pywebview.api : null;
  }

  function byId(id) { return document.getElementById(id); }

  function setText(id, text) {
    var el = byId(id);
    if (el) el.textContent = text == null ? '' : String(text);
  }

  function setClass(el, className, on) {
    if (el) el.classList.toggle(className, !!on);
  }

  function statusLabel(v) {
    if (!v) return 'Ready';
    if (v.waiting || v.voice === 'waiting') return 'Waiting for voice';
    if (v.voice === 'speaking') return 'Speaking';
    if (v.active) return 'On air';
    if (!v.enabled) return 'Disabled';
    return 'Ready';
  }

  function apply(next) {
    view = next || null;
    window.AiDjUI.view = view;
    var active = !!(view && view.active);
    var enabled = !!(view && view.enabled);
    var configured = !!(view && view.configured);
    var label = statusLabel(view);
    var dotClass = (view && (view.waiting || view.voice === 'waiting')) ? 'bg-amber-400'
      : (view && view.voice === 'speaking') ? 'bg-orange-400'
      : active ? 'bg-emerald-400' : 'bg-zinc-600';

    setText('nav-ai-dj-label', active ? 'AI DJ - ON AIR' : 'AI DJ');
    document.body.classList.toggle('ai-dj-active', active);
    ['toggle-queue-btn', 'np-queue-btn'].forEach(function (id) {
      var button = byId(id);
      if (!button) return;
      button.disabled = active;
      button.title = active ? 'Queue hidden while AI DJ is on air' : 'Toggle Queue';
      button.classList.toggle('opacity-40', active);
      button.classList.toggle('cursor-not-allowed', active);
    });

    var vibeBtn = byId('dj-vibe-btn');
    if (vibeBtn) {
      var show = !!(view && view.apiConfigured);
      vibeBtn.classList.toggle('hidden', !show);
      vibeBtn.classList.toggle('inline-flex', show);
      vibeBtn.disabled = !show || !enabled;
      vibeBtn.title = active ? 'AI DJ - On air' : 'AI DJ';
      vibeBtn.classList.toggle('text-orange-400', active);
    }

    var card = byId('home-card-ai-dj');
    if (card) {
      setText('home-ai-dj-status', active ? 'On air' : (enabled ? 'AI' : 'Unavailable'));
      var badge = byId('home-ai-dj-badge');
      if (badge) {
        badge.classList.toggle('bg-orange-500/15', active);
        badge.classList.toggle('text-orange-300', active);
        badge.classList.toggle('border-orange-500/30', active);
        badge.classList.toggle('bg-white/[0.04]', !active);
        badge.classList.toggle('text-zinc-500', !active);
        badge.classList.toggle('border-white/10', !active);
      }
      setText('home-ai-dj-subtitle', !view
        ? 'Vohithe picks the set and speaks the transitions.'
        : (!enabled ? 'AI DJ is disabled on the server.'
          : (!configured ? 'Configure the assistant URL/token in Hathor secrets.'
            : (active ? label + ' - Vohithe is picking the next set.'
              : 'Vohithe picks the set and speaks the transitions.'))));
      var play = byId('home-ai-dj-play');
      if (play) {
        play.disabled = !enabled || startBusy;
        play.title = active ? 'Start a new AI DJ set' : 'Start AI DJ';
      }
    }

    var statusDot = byId('popover-ai-dj-dot');
    if (statusDot) {
      statusDot.className = 'inline-block h-2 w-2 rounded-full ' + dotClass;
    }
    setText('popover-ai-dj-status', label);
    var popoverStop = byId('dj-vibe-stop');
    if (popoverStop) popoverStop.classList.toggle('hidden', !active);

    try {
      window.dispatchEvent(new CustomEvent('ai-dj-state', { detail: view }));
    } catch (e) {}
  }

  async function refresh() {
    var bridge = api();
    if (!bridge || !bridge.ai_dj_view || refreshing) return view;
    refreshing = true;
    try {
      var next = await bridge.ai_dj_view();
      apply(next);
    } catch (e) {
      // Startup/offline must not disturb playback or the rest of the shell.
    } finally {
      refreshing = false;
    }
    return view;
  }

  async function reload() {
    var bridge = api();
    if (!bridge || !bridge.ai_dj_refresh || refreshing) return view;
    refreshing = true;
    try {
      apply(await bridge.ai_dj_refresh());
    } catch (e) {
      // Leave the previous state visible.
    } finally {
      refreshing = false;
    }
    return view;
  }

  async function start(brief, navigate) {
    var bridge = api();
    if (!bridge || !bridge.ai_dj_start || startBusy) return view;
    startBusy = true;
    var err = byId('dj-vibe-error');
    if (err) { err.textContent = ''; err.classList.add('hidden'); }
    var startBtn = byId('dj-vibe-start');
    var homeBtn = byId('home-ai-dj-play');
    [startBtn, homeBtn].forEach(function (button) { if (button) button.disabled = true; });
    try {
      var next = await bridge.ai_dj_start(brief || '');
      apply(next);
      if (next && next.active) {
        hidePopover();
        if (navigate && typeof window.navigateTo === 'function') {
          window.navigateTo('ai_dj');
        }
      } else if (next && next.error && err) {
        err.textContent = next.error;
        err.classList.remove('hidden');
      }
    } catch (e) {
      if (err) {
        err.textContent = e && e.message ? e.message : String(e);
        err.classList.remove('hidden');
      }
    } finally {
      startBusy = false;
      [startBtn, homeBtn].forEach(function (button) { if (button) button.disabled = false; });
      apply(view);
    }
    return view;
  }

  async function stop() {
    var bridge = api();
    if (!bridge || !bridge.ai_dj_stop) return view;
    try {
      apply(await bridge.ai_dj_stop());
    } catch (e) {
      var err = byId('dj-vibe-error');
      if (err) {
        err.textContent = e && e.message ? e.message : String(e);
        err.classList.remove('hidden');
      }
    }
    return view;
  }

  function showPopover() {
    var popover = byId('dj-vibe-popover');
    if (!popover) return;
    popover.classList.remove('hidden');
    popover.classList.add('flex');
    var input = byId('dj-vibe-brief');
    if (input) setTimeout(function () { try { input.focus(); } catch (e) {} }, 30);
    refresh();
  }

  function hidePopover() {
    var popover = byId('dj-vibe-popover');
    if (!popover) return;
    popover.classList.add('hidden');
    popover.classList.remove('flex');
  }

  function togglePopover(force) {
    var popover = byId('dj-vibe-popover');
    if (!popover) return;
    var open = popover.classList.contains('hidden');
    if (force === true) showPopover();
    else if (force === false) hidePopover();
    else if (open) showPopover();
    else hidePopover();
  }

  function appendBrief(text) {
    var input = byId('dj-vibe-brief');
    if (!input || !text) return;
    input.value = input.value ? input.value.trim() + ' ' + text : text;
    input.focus();
  }

  function togglePopoverRecorder() {
    if (!window.AiDjRecorder) return;
    var button = byId('dj-vibe-speak');
    AiDjRecorder.toggle(
      appendBrief,
      function (recording, error) {
        if (button) {
          button.classList.toggle('bg-red-500/20', !!recording);
          button.classList.toggle('text-red-200', !!recording);
          button.textContent = recording ? 'Listening...' : 'Speak';
        }
        var err = byId('dj-vibe-error');
        if (error && err) { err.textContent = error; err.classList.remove('hidden'); }
      },
      function (error) {
        var err = byId('dj-vibe-error');
        if (err) { err.textContent = error; err.classList.remove('hidden'); }
      }
    );
  }

  function init() {
    var vibeBtn = byId('dj-vibe-btn');
    if (vibeBtn) vibeBtn.addEventListener('click', function () { togglePopover(); });
    var close = byId('dj-vibe-close');
    if (close) close.addEventListener('click', hidePopover);
    var startBtn = byId('dj-vibe-start');
    if (startBtn) startBtn.addEventListener('click', function () {
      var input = byId('dj-vibe-brief');
      start(input ? input.value : '', true);
    });
    var stopBtn = byId('dj-vibe-stop');
    if (stopBtn) stopBtn.addEventListener('click', stop);
    var speak = byId('dj-vibe-speak');
    if (speak) speak.addEventListener('click', togglePopoverRecorder);
    var homePlay = byId('home-ai-dj-play');
    if (homePlay) homePlay.addEventListener('click', function (event) {
      event.stopPropagation();
      start('', true);
    });
    document.addEventListener('click', function (event) {
      var popover = byId('dj-vibe-popover');
      if (!popover || popover.classList.contains('hidden')) return;
      if (popover.contains(event.target) || (vibeBtn && vibeBtn.contains(event.target))) return;
      hidePopover();
    });
    document.addEventListener('keydown', function (event) {
      if (event.key === 'Escape') hidePopover();
    });

    window.toggleDjVibePopover = togglePopover;
    window.startAiDj = start;
    window.stopAiDj = stop;
    window.addEventListener('pywebviewready', refresh, { once: true });
    refresh();
    setInterval(function () {
      if (!document.hidden) refresh();
    }, 12000);
  }

  window.AiDjUI = {
    view: null,
    refresh: refresh,
    reload: reload,
    start: start,
    stop: stop,
    togglePopover: togglePopover,
    apply: apply
  };
  window.onAiDjState = apply;

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
