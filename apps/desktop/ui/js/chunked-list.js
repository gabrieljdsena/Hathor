/* Incremental list rendering for large name directories (artists/albums).
 * Keeps only ~200 rows mounted initially and appends more near the bottom.
 */
(function () {
  'use strict';

  function renderChunkedList(container, items, renderItem, options) {
    if (!container) return null;
    options = options || {};
    const chunk = Math.max(1, Number(options.chunkSize) || 200);
    const list = Array.isArray(items) ? items : [];

    if (container._chunkedList && container._chunkedList.observer) {
      try { container._chunkedList.observer.disconnect(); } catch (e) {}
    }
    container.innerHTML = '';
    container._chunkedList = { observer: null, offset: 0, items: list };

    if (list.length === 0) {
      if (options.emptyHtml) container.innerHTML = options.emptyHtml;
      return null;
    }

    const sentinel = document.createElement('div');
    sentinel.className = 'chunked-list-sentinel h-4 w-full';
    sentinel.setAttribute('aria-hidden', 'true');
    container.appendChild(sentinel);

    let offset = 0;
    function renderMore() {
      const end = Math.min(list.length, offset + chunk);
      for (let i = offset; i < end; i += 1) {
        const node = renderItem(list[i], i);
        if (node) container.insertBefore(node, sentinel);
      }
      offset = end;
      container._chunkedList.offset = offset;
      if (offset >= list.length) {
        try { observer.disconnect(); } catch (e) {}
        if (sentinel.parentNode) sentinel.remove();
      }
    }

    let observer = null;
    if (typeof IntersectionObserver !== 'undefined') {
      observer = new IntersectionObserver((entries) => {
        entries.forEach((entry) => {
          if (entry.isIntersecting) renderMore();
        });
      }, { rootMargin: '500px' });
      observer.observe(sentinel);
      container._chunkedList.observer = observer;
    }
    renderMore();
    if (!observer) {
      // No IntersectionObserver: render everything so search/navigation works.
      while (offset < list.length) renderMore();
      if (sentinel.parentNode) sentinel.remove();
    }
    return container._chunkedList;
  }

  window.renderChunkedList = renderChunkedList;
  window.HathorChunkedList = { render: renderChunkedList };
})();
