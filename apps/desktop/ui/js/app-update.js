/* Desktop self-updater UI: checks the Hathor server, downloads the signed
 * package, and asks the Python side to swap it in after the app exits.
 */
(function () {
    'use strict';

    var lastStatus = null;
    var checking = false;
    var downloading = false;
    var offered = false;
    var autoChecked = false;
    var progressUi = null;

    function api() {
        return (window.pywebview && window.pywebview.api) ? window.pywebview.api : null;
    }

    function ready(fn) {
        if (api()) {
            fn();
            return;
        }
        window.addEventListener('pywebviewready', fn, { once: true });
    }

    function fmtBytes(bytes) {
        bytes = Number(bytes) || 0;
        if (bytes >= 1073741824) return (bytes / 1073741824).toFixed(1) + ' GB';
        if (bytes >= 1048576) return (bytes / 1048576).toFixed(1) + ' MB';
        if (bytes >= 1024) return Math.round(bytes / 1024) + ' KB';
        return bytes + ' B';
    }

    function setText(id, text) {
        var el = document.getElementById(id);
        if (el) el.textContent = text || '';
    }

    function setVisible(id, visible) {
        var el = document.getElementById(id);
        if (el) el.classList.toggle('hidden', !visible);
    }

    function render(st) {
        lastStatus = st || lastStatus;
        if (!lastStatus) return;
        var current = lastStatus.current || {};
        var latest = lastStatus.latest || null;
        setText(
            'app-update-version',
            'Hathor ' + (current.versionName || 'dev') +
            ' (build ' + (current.versionCode || 0) + ')'
        );
        setText('app-update-status', lastStatus.message || '');

        var statusEl = document.getElementById('app-update-status');
        if (statusEl) {
            statusEl.classList.remove('text-zinc-500', 'text-emerald-400',
                'text-amber-300', 'text-red-400');
            var kind = 'text-zinc-500';
            if (!lastStatus.ok) kind = 'text-red-400';
            else if (lastStatus.downloaded) kind = 'text-emerald-400';
            else if (lastStatus.updateAvailable) kind = 'text-amber-300';
            statusEl.classList.add(kind);
        }

        var detail = '';
        if (lastStatus.downloaded && latest) {
            detail = latest.versionName + ' is ready (' +
                fmtBytes(lastStatus.cachedBytes) + ').';
        } else if (lastStatus.updateAvailable && latest) {
            detail = 'New version: ' + latest.versionName + '.';
        } else if (lastStatus.cachedBytes > 0) {
            detail = 'Downloaded package: ' + fmtBytes(lastStatus.cachedBytes) + '.';
        } else if (!lastStatus.configured) {
            detail = 'The update channel uses the same server as Web API.';
        }
        if (lastStatus.installHint) {
            detail = detail ? detail + ' ' + lastStatus.installHint : lastStatus.installHint;
        }
        setText('app-update-detail', detail);
        var detailEl = document.getElementById('app-update-detail');
        if (detailEl) detailEl.classList.toggle('hidden', !detail);

        var checkBtn = document.getElementById('app-update-check-btn');
        if (checkBtn) {
            checkBtn.disabled = checking || downloading;
            checkBtn.textContent = checking ? 'Checking...' : 'Check for updates';
        }

        var installBtn = document.getElementById('app-update-install-btn');
        if (installBtn) {
            var showInstall = !!lastStatus.updateAvailable;
            var canInstall = !lastStatus.downloaded || lastStatus.installable;
            installBtn.classList.toggle('hidden', !showInstall || !canInstall);
            installBtn.disabled = downloading;
            installBtn.textContent = lastStatus.downloaded ?
                'Install and restart' : 'Download update';
        }

        var removeBtn = document.getElementById('app-update-remove-btn');
        if (removeBtn) {
            removeBtn.classList.toggle('hidden', !(lastStatus.cachedBytes > 0));
            removeBtn.disabled = downloading;
        }

        var wrap = document.getElementById('app-update-progress-wrap');
        if (wrap && !downloading) wrap.classList.add('hidden');
    }

    async function refresh() {
        var bridge = api();
        if (!bridge || typeof bridge.get_app_update_status !== 'function') return null;
        try {
            var st = await bridge.get_app_update_status();
            render(st);
            return st;
        } catch (e) {
            console.error('App update status failed:', e);
            return null;
        }
    }

    async function check(options) {
        options = options || {};
        var bridge = api();
        if (!bridge || checking || downloading) return lastStatus;
        checking = true;
        render(lastStatus);
        try {
            var st = await bridge.check_app_update();
            render(st);
            if (st && st.updateAvailable) {
                if (options.prompt !== false) offer(st);
            } else if (!options.silent) {
                if (st && st.ok) {
                    if (typeof notyf !== 'undefined') notyf.success(st.message || 'Up to date.');
                } else if (st && st.message) {
                    if (typeof notyf !== 'undefined') notyf.error(st.message);
                }
            }
            return st;
        } catch (e) {
            if (!options.silent && typeof notyf !== 'undefined') {
                notyf.error('Update check failed: ' + (e && e.message ? e.message : e));
            }
            return null;
        } finally {
            checking = false;
            render(lastStatus);
        }
    }

    function offer(st) {
        if (!st || !st.updateAvailable) return;
        if (offered) return;
        if (typeof Swal === 'undefined') return;
        if (Swal.isVisible && Swal.isVisible()) return;
        offered = true;
        if (st.downloaded) {
            promptRestart(st);
            return;
        }
        var latest = st.latest || {};
        var current = st.current || {};
        Swal.fire({
            title: 'Update available',
            html:
                '<div class="text-sm text-zinc-300">Hathor ' +
                (current.versionName || '') + ' &rarr; <b>' +
                (latest.versionName || '') + '</b></div>' +
                '<div class="mt-2 text-xs text-zinc-500">' +
                fmtBytes(latest.sizeBytes || 0) + ' download</div>',
            icon: 'info',
            showCancelButton: true,
            confirmButtonText: 'Download and install',
            cancelButtonText: 'Later',
            customClass: { popup: 'swal-dark' }
        }).then(function (result) {
            if (result.isConfirmed) {
                downloadFlow();
            } else {
                offered = false;
            }
        });
    }

    async function downloadFlow() {
        var bridge = api();
        if (!bridge || downloading) return;
        downloading = true;
        render(lastStatus);
        var wrap = document.getElementById('app-update-progress-wrap');
        if (wrap) wrap.classList.remove('hidden');

        var swalVisible = typeof Swal !== 'undefined';
        if (swalVisible) {
            Swal.fire({
                title: 'Downloading update...',
                html:
                    '<div class="text-xs text-zinc-400 mb-2" id="hathor-update-label">Connecting...</div>' +
                    '<div style="height:6px;background:rgba(255,255,255,.08);border-radius:9999px;overflow:hidden">' +
                    '<div id="hathor-update-bar" style="height:100%;width:0%;background:linear-gradient(90deg,#fb923c,#f97316);transition:width .2s"></div>' +
                    '</div>' +
                    '<div class="text-[11px] text-zinc-500 mt-2" id="hathor-update-detail"></div>' +
                    '<div class="text-[11px] text-zinc-500 mt-1" id="hathor-update-percent"></div>',
                allowOutsideClick: false,
                allowEscapeKey: false,
                showConfirmButton: false,
                customClass: { popup: 'swal-dark' }
            });
        }
        progressUi = swalVisible ? 'swal' : 'settings';
        try {
            var st = await bridge.download_app_update();
            render(st);
            if (swalVisible && Swal.isVisible && Swal.isVisible()) Swal.close();
            if (st && st.downloaded) {
                promptRestart(st);
            } else {
                offered = false;
                if (st && st.message && typeof notyf !== 'undefined') notyf.error(st.message);
            }
        } catch (e) {
            offered = false;
            if (swalVisible && Swal.isVisible && Swal.isVisible()) Swal.close();
            if (typeof notyf !== 'undefined') {
                notyf.error('Update download failed: ' + (e && e.message ? e.message : e));
            }
        } finally {
            downloading = false;
            progressUi = null;
            var progressWrap = document.getElementById('app-update-progress-wrap');
            if (progressWrap) progressWrap.classList.add('hidden');
            render(lastStatus);
        }
    }

    function promptRestart(st) {
        if (typeof Swal === 'undefined') return;
        var latest = st.latest || {};
        Swal.fire({
            title: 'Update ready',
            html:
                '<div class="text-sm text-zinc-300"><b>' +
                (latest.versionName || 'The update') +
                '</b> is downloaded and verified.</div>' +
                '<div class="mt-2 text-xs text-zinc-500">Hathor will close and restart to install it.</div>',
            icon: 'success',
            showCancelButton: true,
            confirmButtonText: 'Install and restart',
            cancelButtonText: 'Later',
            customClass: { popup: 'swal-dark' }
        }).then(function (result) {
            if (result.isConfirmed) installNow();
            else offered = false;
        });
    }

    async function installNow() {
        var bridge = api();
        if (!bridge) return;
        try {
            var res = await bridge.install_app_update();
            if (res && res.ok) {
                if (typeof Swal !== 'undefined') {
                    Swal.fire({
                        title: 'Restarting Hathor...',
                        text: 'The update is being applied.',
                        allowOutsideClick: false,
                        allowEscapeKey: false,
                        showConfirmButton: false,
                        didOpen: function () { Swal.showLoading(); },
                        customClass: { popup: 'swal-dark' }
                    });
                }
            } else if (res && res.message) {
                if (typeof notyf !== 'undefined') notyf.error(res.message);
                render(res);
            }
        } catch (e) {
            if (typeof notyf !== 'undefined') {
                notyf.error('Could not start the updater: ' + (e && e.message ? e.message : e));
            }
        }
    }

    async function removeDownload() {
        var bridge = api();
        if (!bridge || downloading) return;
        var confirmed = true;
        if (typeof Swal !== 'undefined') {
            var answer = await Swal.fire({
                title: 'Remove downloaded update?',
                text: 'Hathor will keep running and can download it again later.',
                icon: 'warning',
                showCancelButton: true,
                confirmButtonText: 'Remove',
                cancelButtonText: 'Cancel',
                customClass: { popup: 'swal-dark' }
            });
            confirmed = !!answer.isConfirmed;
        }
        if (!confirmed) return;
        try {
            var st = await bridge.remove_app_update();
            render(st);
            if (typeof notyf !== 'undefined') notyf.success('Downloaded update removed.');
        } catch (e) {
            if (typeof notyf !== 'undefined') notyf.error('Could not remove the downloaded update.');
        }
    }

    function setProgress(ev) {
        ev = ev || {};
        var percent = ev.percent;
        var label = ev.label || 'Working...';
        var detail = ev.detail || '';
        var percentText = (percent === null || percent === undefined) ?
            '' : Math.max(0, Math.min(100, percent)).toFixed(0) + '%';

        var settingsBar = document.getElementById('app-update-progress-bar');
        if (settingsBar) {
            settingsBar.style.width = (percent === null || percent === undefined) ?
                '35%' : Math.max(0, Math.min(100, percent)) + '%';
        }
        setText('app-update-progress-label', label);
        setText('app-update-progress-detail', [detail, percentText].filter(Boolean).join('  '));

        if (progressUi === 'swal') {
            setText('hathor-update-label', label);
            setText('hathor-update-detail', detail);
            setText('hathor-update-percent', percentText);
            var bar = document.getElementById('hathor-update-bar');
            if (bar) {
                bar.style.width = (percent === null || percent === undefined) ?
                    '35%' : Math.max(0, Math.min(100, percent)) + '%';
            }
        }
    }

    window.appUpdateProgress = setProgress;

    function autoCheck() {
        if (autoChecked) return;
        autoChecked = true;
        setTimeout(function () {
            if (document.hidden) return;
            if (typeof Swal !== 'undefined' && Swal.isVisible && Swal.isVisible()) return;
            check({ silent: true }).catch(function () {});
        }, 3500);
    }

    window.HathorAppUpdate = {
        refresh: refresh,
        check: function () { return check({ silent: false }); },
        autoCheck: autoCheck,
        offer: offer,
        installNow: installNow,
        download: downloadFlow,
        remove: removeDownload,
        primaryAction: function () {
            if (lastStatus && lastStatus.downloaded) return installNow();
            return downloadFlow();
        }
    };

    ready(autoCheck);
})();
