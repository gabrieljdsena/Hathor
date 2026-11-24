/* Dedicated desktop login gate.
 *
 * The server URL, username and password are entered here (not in Settings).
 * Only the refresh token is persisted by the Python AuthSession; the password
 * never leaves this form except for the single /auth/login request.
 */
(function () {
    'use strict';

    var gate = null;
    var form = null;
    var urlEl = null;
    var userEl = null;
    var passwordEl = null;
    var revealBtn = null;
    var rememberEl = null;
    var warningEl = null;
    var ackEl = null;
    var statusEl = null;
    var submitBtn = null;
    var keyEl = null;
    var keyRevealBtn = null;
    var keySaveBtn = null;
    var continueKeyBtn = null;
    var busy = false;
    var passwordShown = false;
    var keyShown = false;
    var checkTimer = null;
    var initialized = false;

    function bridge() {
        return (window.pywebview && window.pywebview.api) ? window.pywebview.api : null;
    }

    function normalizeUrl(value) {
        var raw = String(value || '').trim();
        if (!raw) return '';
        if (raw.indexOf('://') < 0) raw = 'http://' + raw;
        raw = raw.replace(/\/+$/, '');
        raw = raw.replace(/\/api\/v1$/, '').replace(/\/api$/, '');
        return raw;
    }

    function isHttp(value) {
        return /^http:\/\//i.test(normalizeUrl(value));
    }

    function setStatus(text, kind) {
        if (!statusEl) return;
        statusEl.textContent = text || '';
        statusEl.classList.remove('is-error', 'is-ok');
        if (kind === 'error') statusEl.classList.add('is-error');
        if (kind === 'ok') statusEl.classList.add('is-ok');
    }

    function setBusy(value) {
        busy = !!value;
        [urlEl, userEl, passwordEl, rememberEl, ackEl, keyEl, keySaveBtn, submitBtn, continueKeyBtn]
            .forEach(function (el) {
                if (el) el.disabled = busy;
            });
        if (revealBtn) revealBtn.disabled = busy;
        if (keyRevealBtn) keyRevealBtn.disabled = busy;
        updateSubmitState();
    }

    function updateTransport() {
        var http = isHttp(urlEl ? urlEl.value : '');
        if (warningEl) warningEl.classList.toggle('is-hidden', !http);
        if (!http && ackEl) ackEl.checked = false;
        updateSubmitState();
    }

    function updateSubmitState() {
        var hasFields = !!(
            (urlEl && urlEl.value.trim()) &&
            (userEl && userEl.value.trim()) &&
            (passwordEl && passwordEl.value)
        );
        var consent = !isHttp(urlEl ? urlEl.value : '') || !!(ackEl && ackEl.checked);
        if (submitBtn) submitBtn.disabled = busy || !hasFields || !consent;
        var key = keyEl ? keyEl.value.trim() : '';
        if (keySaveBtn) {
            keySaveBtn.disabled = busy || !key || !consent || !(urlEl && urlEl.value.trim());
        }
    }

    function togglePassword() {
        if (!passwordEl) return;
        passwordShown = !passwordShown;
        passwordEl.type = passwordShown ? 'text' : 'password';
        if (revealBtn) {
            revealBtn.textContent = passwordShown ? 'Hide' : 'Show';
            revealBtn.setAttribute('aria-label', passwordShown ? 'Hide password' : 'Show password');
            revealBtn.setAttribute('aria-pressed', passwordShown ? 'true' : 'false');
        }
    }

    function toggleKey() {
        if (!keyEl) return;
        keyShown = !keyShown;
        keyEl.type = keyShown ? 'text' : 'password';
        if (keyRevealBtn) {
            keyRevealBtn.textContent = keyShown ? 'Hide' : 'Show';
            keyRevealBtn.setAttribute('aria-label', keyShown ? 'Hide API key' : 'Show API key');
            keyRevealBtn.setAttribute('aria-pressed', keyShown ? 'true' : 'false');
        }
    }

    function keyAck() {
        try { return localStorage.getItem('hathor_login_key_ack') === '1'; } catch (_) { return false; }
    }

    function showGate(cfg, keyMode) {
        if (!gate) return;
        gate.classList.remove('is-hidden');
        if (continueKeyBtn) continueKeyBtn.classList.toggle('is-hidden', !keyMode);
        window._authGateOpen = true;
        document.body.classList.add('auth-locked');
        if (cfg) {
            if (urlEl && !urlEl.value.trim()) urlEl.value = cfg.url || '';
            if (userEl && !userEl.value.trim()) userEl.value = cfg.username || '';
            if (keyMode) {
                setStatus('A saved API key is configured. Sign in with your Web account, or continue with the key.', '');
            } else if (cfg.message && !statusEl.textContent) {
                setStatus(cfg.message, '');
            }
        }
        updateTransport();
        setTimeout(function () {
            var target = (urlEl && !urlEl.value.trim()) ? urlEl
                : (userEl && !userEl.value.trim()) ? userEl : passwordEl;
            if (target && typeof target.focus === 'function') target.focus();
        }, 60);
    }

    function hideGate() {
        if (!gate) return;
        gate.classList.add('is-hidden');
        window._authGateOpen = false;
        document.body.classList.remove('auth-locked');
        if (passwordEl) passwordEl.value = '';
        if (keyEl) keyEl.value = '';
    }

    async function refresh() {
        var api = bridge();
        if (!api || typeof api.get_api_config_view !== 'function') return;
        try {
            var cfg = await api.get_api_config_view();
            if (cfg && cfg.configured) {
                if (cfg.mode === 'key' && cfg.keySet && !keyAck()) {
                    showGate(cfg, true);
                    return;
                }
                hideGate();
                return;
            }
            showGate(cfg, false);
        } catch (e) {
            showGate(null);
            setStatus('Could not read the saved session. You can still sign in.', 'error');
        }
    }

    function continueWithKey() {
        if (busy) return;
        try { localStorage.setItem('hathor_login_key_ack', '1'); } catch (_) {}
        hideGate();
        window.location.reload();
    }

    async function submitLogin(event) {
        if (event) event.preventDefault();
        if (busy) return;
        var api = bridge();
        if (!api || typeof api.login !== 'function') {
            setStatus('The desktop bridge is not ready yet.', 'error');
            return;
        }
        if (!(urlEl && urlEl.value.trim()) || !(userEl && userEl.value.trim()) ||
            !(passwordEl && passwordEl.value)) {
            setStatus('Enter the server, username and password.', 'error');
            return;
        }
        if (isHttp(urlEl.value) && !(ackEl && ackEl.checked)) {
            setStatus('Confirm the plain-HTTP warning first.', 'error');
            return;
        }
        setBusy(true);
        setStatus('Signing in…', '');
        try {
            var res = await api.login(
                normalizeUrl(urlEl.value),
                userEl.value.trim(),
                passwordEl.value,
                rememberEl ? !!rememberEl.checked : true
            );
            if (res && res.ok) {
                if (passwordEl) passwordEl.value = '';
                setStatus(res.message || 'Signed in. Loading your library…', 'ok');
                setTimeout(function () { window.location.reload(); }, 350);
            } else {
                setStatus((res && res.message) || 'Sign-in failed.', 'error');
                if (passwordEl) {
                    passwordEl.value = '';
                    passwordEl.focus();
                }
            }
        } catch (e) {
            setStatus('Sign-in failed: ' + (e && e.message ? e.message : e), 'error');
        } finally {
            setBusy(false);
        }
    }

    async function saveApiKey() {
        if (busy) return;
        var api = bridge();
        if (!api || typeof api.save_api_config !== 'function') {
            setStatus('The desktop bridge is not ready yet.', 'error');
            return;
        }
        if (!(urlEl && urlEl.value.trim()) || !(keyEl && keyEl.value.trim())) {
            setStatus('Enter the server and API key.', 'error');
            return;
        }
        if (isHttp(urlEl.value) && !(ackEl && ackEl.checked)) {
            setStatus('Confirm the plain-HTTP warning first.', 'error');
            return;
        }
        setBusy(true);
        setStatus('Saving API key…', '');
        try {
            var res = await api.save_api_config(normalizeUrl(urlEl.value), keyEl.value.trim());
            if (res && res.ok) {
                if (keyEl) keyEl.value = '';
                setStatus(res.message || 'API key saved. Loading…', 'ok');
                setTimeout(function () { window.location.reload(); }, 350);
            } else {
                setStatus((res && res.message) || 'Could not save the API key.', 'error');
            }
        } catch (e) {
            setStatus('Could not save the API key: ' + (e && e.message ? e.message : e), 'error');
        } finally {
            setBusy(false);
        }
    }

    function init() {
        if (initialized) return;
        initialized = true;
        gate = document.getElementById('login-gate');
        form = document.getElementById('login-form');
        urlEl = document.getElementById('login-url');
        userEl = document.getElementById('login-username');
        passwordEl = document.getElementById('login-password');
        revealBtn = document.getElementById('login-reveal');
        rememberEl = document.getElementById('login-remember');
        warningEl = document.getElementById('login-http-warning');
        ackEl = document.getElementById('login-http-ack');
        statusEl = document.getElementById('login-status');
        submitBtn = document.getElementById('login-submit');
        keyEl = document.getElementById('login-api-key');
        keyRevealBtn = document.getElementById('login-key-reveal');
        keySaveBtn = document.getElementById('login-key-save');
        continueKeyBtn = document.getElementById('login-continue-key');
        if (!gate || !form) return;

        form.addEventListener('submit', submitLogin);
        if (urlEl) urlEl.addEventListener('input', updateTransport);
        if (ackEl) ackEl.addEventListener('change', updateSubmitState);
        if (passwordEl) passwordEl.addEventListener('input', updateSubmitState);
        if (userEl) userEl.addEventListener('input', updateSubmitState);
        if (revealBtn) revealBtn.addEventListener('click', togglePassword);
        if (keyRevealBtn) keyRevealBtn.addEventListener('click', toggleKey);
        if (keyEl) keyEl.addEventListener('input', updateSubmitState);
        if (keySaveBtn) keySaveBtn.addEventListener('click', saveApiKey);
        if (continueKeyBtn) continueKeyBtn.addEventListener('click', continueWithKey);

        updateTransport();
        refresh();
        window.addEventListener('pywebviewready', refresh, { once: true });
        if (!checkTimer) checkTimer = setInterval(refresh, 5000);
        window.HathorLoginGate = { refresh: refresh };
    }

    if (bridge()) {
        init();
    } else {
        document.addEventListener('DOMContentLoaded', init, { once: true });
        window.addEventListener('pywebviewready', init, { once: true });
    }
})();
