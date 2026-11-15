/* Typo-tolerant search shared by every library view.
 * Mirrors apps/web/frontend/hathor-web/src/utils/fuzzy.ts: exact substring
 * always matches, otherwise each query token must match a haystack token
 * within a small Damerau-Levenshtein distance.
 */
(function () {
  'use strict';

  function normalize(value) {
    var folded = String(value == null ? '' : value)
      .normalize('NFD')
      .replace(/[\u0300-\u036f]/g, '')
      .toLowerCase();
    return folded.split(/\s+/).filter(Boolean).join(' ');
  }

  function tokenize(normalized) {
    return normalized.split(/[\s\-_/,.;:!?"'()[\]]+/).filter(Boolean);
  }

  function maxDistance(token) {
    return token.length < 4 ? 1 : Math.min(3, Math.floor(token.length / 3));
  }

  function damerauLevenshtein(a, b, maxDist) {
    if (a === b) return 0;
    if (a.length === 0) return b.length;
    if (b.length === 0) return a.length;
    var prevPrev = [];
    var prev = [];
    var cur = [];
    for (var j = 0; j <= b.length; j += 1) {
      prevPrev[j] = j;
      prev[j] = j;
    }
    for (var i = 1; i <= a.length; i += 1) {
      cur = [];
      cur[0] = i;
      var rowMin = i;
      for (var k = 1; k <= b.length; k += 1) {
        var cost = a.charAt(i - 1) === b.charAt(k - 1) ? 0 : 1;
        var best = Math.min(prev[k] + 1, cur[k - 1] + 1, prev[k - 1] + cost);
        if (i > 1 && k > 1 &&
            a.charAt(i - 1) === b.charAt(k - 2) &&
            a.charAt(i - 2) === b.charAt(k - 1)) {
          best = Math.min(best, prevPrev[k - 2] + 1);
        }
        cur[k] = best;
        if (best < rowMin) rowMin = best;
      }
      if (rowMin > maxDist) return maxDist + 1;
      prevPrev = prev;
      prev = cur;
    }
    return prev[b.length];
  }

  function matchesToken(hayTokens, token) {
    for (var i = 0; i < hayTokens.length; i += 1) {
      var hay = hayTokens[i];
      if (hay.indexOf(token) > -1) return true;
      var max = maxDistance(token);
      if (Math.abs(hay.length - token.length) > max) continue;
      if (damerauLevenshtein(hay, token, max) <= max) return true;
    }
    return false;
  }

  function fuzzyMatch(haystack, query) {
    if (!query || !String(query).trim()) return true;
    if (!haystack || !String(haystack).trim()) return false;
    var hay = normalize(haystack);
    var needle = normalize(query);
    if (hay.indexOf(needle) > -1) return true;
    var hayTokens = tokenize(hay);
    if (hayTokens.length === 0) return false;
    return tokenize(needle).every(function (token) {
      return matchesToken(hayTokens, token);
    });
  }

  function fuzzyFields(fields, query) {
    if (!query || !String(query).trim()) return true;
    var parts = (fields || []).filter(function (field) {
      return field && String(field).trim();
    });
    if (parts.length === 0) return false;
    var joined = normalize(parts.join(' '));
    var needle = normalize(query);
    if (joined.indexOf(needle) > -1) return true;
    var hayTokens = [];
    parts.forEach(function (part) {
      hayTokens = hayTokens.concat(tokenize(normalize(part)));
    });
    if (hayTokens.length === 0) return false;
    return tokenize(needle).every(function (token) {
      return matchesToken(hayTokens, token);
    });
  }

  window.fuzzyMatch = fuzzyMatch;
  window.fuzzyFields = fuzzyFields;
  window.HathorFuzzy = { match: fuzzyMatch, fields: fuzzyFields, normalize: normalize };
})();
