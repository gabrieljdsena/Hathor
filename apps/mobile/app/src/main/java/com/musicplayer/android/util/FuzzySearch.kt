package com.musicplayer.android.util

import java.text.Normalizer

/**
 * Typo-tolerant search, ported from the web `utils/fuzzy.ts` and the server's
 * FuzzySearch: exact substring always matches; otherwise every query token
 * must match a haystack token within a small Damerau-Levenshtein distance.
 */
object FuzzySearch {

    fun matches(haystack: String?, query: String?): Boolean {
        if (query.isNullOrBlank()) return true
        if (haystack.isNullOrBlank()) return false
        val hay = normalize(haystack)
        val needle = normalize(query)
        if (hay.contains(needle)) return true
        val hayTokens = tokenize(hay)
        if (hayTokens.isEmpty()) return false
        return tokenize(needle).all { token -> matchesToken(hayTokens, token) }
    }

    fun matchesFields(fields: List<String?>, query: String?): Boolean {
        if (query.isNullOrBlank()) return true
        val parts = fields.filter { !it.isNullOrBlank() }
        if (parts.isEmpty()) return false
        val joined = normalize(parts.joinToString(" "))
        val needle = normalize(query)
        if (joined.contains(needle)) return true
        val hayTokens = parts.flatMap { tokenize(normalize(it!!)) }
        if (hayTokens.isEmpty()) return false
        return tokenize(needle).all { token -> matchesToken(hayTokens, token) }
    }

    private fun matchesToken(hayTokens: List<String>, token: String): Boolean {
        for (hay in hayTokens) {
            if (hay.contains(token)) return true
            val max = maxDistance(token)
            if (kotlin.math.abs(hay.length - token.length) > max) continue
            if (damerauLevenshtein(hay, token, max) <= max) return true
        }
        return false
    }

    private fun maxDistance(token: String): Int =
        if (token.length < 4) 1 else minOf(3, token.length / 3)

    /** Optimal string alignment: adjacent transpositions count as one edit. */
    private fun damerauLevenshtein(a: String, b: String, maxDist: Int): Int {
        if (a == b) return 0
        if (a.isEmpty()) return b.length
        if (b.isEmpty()) return a.length
        var prevPrev = IntArray(b.length + 1) { it }
        var prev = IntArray(b.length + 1) { it }
        var cur = IntArray(b.length + 1)
        for (i in 1..a.length) {
            cur[0] = i
            var rowMin = i
            for (j in 1..b.length) {
                val cost = if (a[i - 1] == b[j - 1]) 0 else 1
                var best = minOf(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + cost)
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) {
                    best = minOf(best, prevPrev[j - 2] + 1)
                }
                cur[j] = best
                if (best < rowMin) rowMin = best
            }
            if (rowMin > maxDist) return maxDist + 1
            val tmp = prevPrev
            prevPrev = prev
            prev = cur
            cur = tmp
        }
        return prev[b.length]
    }

    /** Strip diacritics so "beyonce" matches "Beyoncé". */
    private fun normalize(value: String): String {
        val folded = Normalizer.normalize(value, Normalizer.Form.NFD)
            .replace(Regex("\\p{M}+"), "")
            .lowercase()
        return folded.split(Regex("\\s+")).filter { it.isNotBlank() }.joinToString(" ")
    }

    private fun tokenize(normalized: String): List<String> =
        normalized.split(Regex("[\\s\\-_/,.;:!?\"'()\\[\\]]+")).filter { it.isNotBlank() }
}
