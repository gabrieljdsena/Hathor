package com.musicplayer.android.data

/**
 * Kana → Hepburn romanization, ported from the server's KanaRomaji fallback.
 * Kanji passes through unchanged (no dictionary on-device), matching the
 * web fallback when the MeCab backend is unavailable.
 */
object LyricsRomanizer {

    private val basic = mapOf(
        'あ' to "a", 'い' to "i", 'う' to "u", 'え' to "e", 'お' to "o",
        'か' to "ka", 'き' to "ki", 'く' to "ku", 'け' to "ke", 'こ' to "ko",
        'さ' to "sa", 'し' to "shi", 'す' to "su", 'せ' to "se", 'そ' to "so",
        'た' to "ta", 'ち' to "chi", 'つ' to "tsu", 'て' to "te", 'と' to "to",
        'な' to "na", 'に' to "ni", 'ぬ' to "nu", 'ね' to "ne", 'の' to "no",
        'は' to "ha", 'ひ' to "hi", 'ふ' to "fu", 'へ' to "he", 'ほ' to "ho",
        'ま' to "ma", 'み' to "mi", 'む' to "mu", 'め' to "me", 'も' to "mo",
        'や' to "ya", 'ゆ' to "yu", 'よ' to "yo",
        'ら' to "ra", 'り' to "ri", 'る' to "ru", 'れ' to "re", 'ろ' to "ro",
        'わ' to "wa", 'ゐ' to "wi", 'ゑ' to "we", 'を' to "wo", 'ん' to "n",
        'が' to "ga", 'ぎ' to "gi", 'ぐ' to "gu", 'げ' to "ge", 'ご' to "go",
        'ざ' to "za", 'じ' to "ji", 'ず' to "zu", 'ぜ' to "ze", 'ぞ' to "zo",
        'だ' to "da", 'ぢ' to "ji", 'づ' to "zu", 'で' to "de", 'ど' to "do",
        'ば' to "ba", 'び' to "bi", 'ぶ' to "bu", 'べ' to "be", 'ぼ' to "bo",
        'ぱ' to "pa", 'ぴ' to "pi", 'ぷ' to "pu", 'ぺ' to "pe", 'ぽ' to "po",
        'ぁ' to "a", 'ぃ' to "i", 'ぅ' to "u", 'ぇ' to "e", 'ぉ' to "o",
        'ゃ' to "ya", 'ゅ' to "yu", 'ょ' to "yo", 'ゎ' to "wa",
        'ア' to "a", 'イ' to "i", 'ウ' to "u", 'エ' to "e", 'オ' to "o",
        'カ' to "ka", 'キ' to "ki", 'ク' to "ku", 'ケ' to "ke", 'コ' to "ko",
        'サ' to "sa", 'シ' to "shi", 'ス' to "su", 'セ' to "se", 'ソ' to "so",
        'タ' to "ta", 'チ' to "chi", 'ツ' to "tsu", 'テ' to "te", 'ト' to "to",
        'ナ' to "na", 'ニ' to "ni", 'ヌ' to "nu", 'ネ' to "ne", 'ノ' to "no",
        'ハ' to "ha", 'ヒ' to "hi", 'フ' to "fu", 'ヘ' to "he", 'ホ' to "ho",
        'マ' to "ma", 'ミ' to "mi", 'ム' to "mu", 'メ' to "me", 'モ' to "mo",
        'ヤ' to "ya", 'ユ' to "yu", 'ヨ' to "yo",
        'ラ' to "ra", 'リ' to "ri", 'ル' to "ru", 'レ' to "re", 'ロ' to "ro",
        'ワ' to "wa", 'ヰ' to "wi", 'ヱ' to "we", 'ヲ' to "wo", 'ン' to "n",
        'ガ' to "ga", 'ギ' to "gi", 'グ' to "gu", 'ゲ' to "ge", 'ゴ' to "go",
        'ザ' to "za", 'ジ' to "ji", 'ズ' to "zu", 'ゼ' to "ze", 'ゾ' to "zo",
        'ダ' to "da", 'ヂ' to "ji", 'ヅ' to "zu", 'デ' to "de", 'ド' to "do",
        'バ' to "ba", 'ビ' to "bi", 'ブ' to "bu", 'ベ' to "be", 'ボ' to "bo",
        'パ' to "pa", 'ピ' to "pi", 'プ' to "pu", 'ペ' to "pe", 'ポ' to "po",
        'ァ' to "a", 'ィ' to "i", 'ゥ' to "u", 'ェ' to "e", 'ォ' to "o",
        'ャ' to "ya", 'ュ' to "yu", 'ョ' to "yo", 'ヮ' to "wa",
        'ヴ' to "vu",
    )

    private val digraphStem = mapOf(
        'き' to "k", 'し' to "sh", 'ち' to "ch", 'に' to "n", 'ひ' to "h",
        'み' to "m", 'り' to "r", 'ぎ' to "g", 'じ' to "j", 'び' to "b",
        'ぴ' to "p", 'う' to "w", 'く' to "kw", 'ぐ' to "gw",
        'キ' to "k", 'シ' to "sh", 'チ' to "ch", 'ニ' to "n", 'ヒ' to "h",
        'ミ' to "m", 'リ' to "r", 'ギ' to "g", 'ジ' to "j", 'ビ' to "b",
        'ピ' to "p", 'ウ' to "w", 'ク' to "kw", 'グ' to "gw",
        'テ' to "t", 'デ' to "d", 'フ' to "f",
    )

    private val smallY = setOf('ゃ', 'ゅ', 'ょ', 'ャ', 'ュ', 'ョ')
    private val lrcTimestamp = Regex("^(\\[\\d+:\\d+\\.\\d+])(.*)$")

    fun romanize(text: String?, isLrc: Boolean = false): String {
        if (text.isNullOrEmpty()) return text ?: ""
        val out = StringBuilder()
        val lines = text.split('\n')
        for ((index, original) in lines.withIndex()) {
            if (index > 0) out.append('\n')
            var line = original
            if (isLrc) {
                val match = lrcTimestamp.find(line)
                if (match != null && match.groupValues[1].isNotEmpty()) {
                    out.append(match.groupValues[1]).append(' ')
                    line = match.groupValues[2]
                }
            }
            out.append(romanizeLine(line))
        }
        return out.toString()
    }

    private fun romanizeLine(line: String): String {
        val sb = StringBuilder()
        var i = 0
        var lastVowel = ""
        while (i < line.length) {
            val c = line[i]
            // Sokuon: double the next syllable's initial consonant.
            if (c == 'っ' || c == 'ッ') {
                val next = peekRomaji(line, i + 1)
                if (next.isNotEmpty() && next[0].isLetter()) sb.append(next[0])
                i++
                continue
            }
            // Long vowel mark: repeat the previous vowel.
            if (c == 'ー') {
                sb.append(lastVowel)
                i++
                continue
            }
            // Digraph: stem + small ゃゅょ. Hepburn contracts sh/ch/j.
            if (i + 1 < line.length && line[i + 1] in smallY && digraphStem.containsKey(c)) {
                val stem = digraphStem.getValue(c)
                val y = basic[line[i + 1]] ?: ""
                val roma = if (stem == "sh" || stem == "ch" || stem == "j") stem + y.last() else stem + y
                sb.append(roma)
                lastVowel = y.lastOrNull()?.toString() ?: ""
                i += 2
                continue
            }
            val r = basic[c]
            if (r != null) {
                sb.append(r)
                lastVowel = r.last().toString()
                i++
                continue
            }
            // Kanji, latin, punctuation pass through.
            sb.append(c)
            i++
        }
        return sb.toString().replace(Regex("\\s+"), " ").trim()
    }

    private fun peekRomaji(line: String, index: Int): String {
        if (index >= line.length) return ""
        val c = line[index]
        if (index + 1 < line.length && line[index + 1] in smallY && digraphStem.containsKey(c)) {
            val stem = digraphStem.getValue(c)
            val y = basic[line[index + 1]] ?: ""
            return stem + y
        }
        return basic[c] ?: ""
    }
}
