/* Text-file batch import parser shared by the Download screen/tests. */
(function () {
  'use strict';

  function parseBatchQueries(text) {
    if (!text) return [];
    return String(text)
      .split(/\r?\n/)
      .map(function (line) { return line.trim(); })
      .filter(function (line) { return line.length > 0; });
  }

  window.parseBatchQueries = parseBatchQueries;
  window.HathorBatch = { parse: parseBatchQueries };
})();
