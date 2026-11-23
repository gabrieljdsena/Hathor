/* Platform handshake: Windows keeps the custom frameless chrome, Linux uses
 * native GTK decorations. The Python bridge owns the decision.
 */
(function () {
    'use strict';

    window.HathorPlatform = window.HathorPlatform || { customChrome: true };

    function api() {
        return (window.pywebview && window.pywebview.api) ? window.pywebview.api : null;
    }

    function apply(info) {
        if (!info) return;
        window.HathorPlatform = info;
        var native = info.customChrome === false;
        document.body.classList.toggle('native-decorations', native);
        document.body.dataset.platform = info.platform || '';
        var zones = document.getElementById('window-resize-zones');
        if (native && zones) zones.style.display = 'none';
    }

    function load() {
        var bridge = api();
        if (!bridge || typeof bridge.get_platform_info !== 'function') return;
        bridge.get_platform_info().then(apply).catch(function (err) {
            console.error('Platform info failed:', err);
        });
    }

    if (api()) load();
    else window.addEventListener('pywebviewready', load, { once: true });
})();
