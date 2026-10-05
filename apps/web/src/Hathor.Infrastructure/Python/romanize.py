"""Hathor lyrics romanizer (fugashi MeCab + UniDic + Hepburn map).

Reads {"text": ..., "is_lrc": bool} on stdin, writes {"text": ...} on stdout.
LRC timestamps pass through untouched. Every other line goes through MeCab
morphological analysis (correct kanji readings, okurigana, rendaku), then a
deterministic kana->Hepburn map. Any failure prints {"error": ...} and exits
non-zero so the caller falls back to the kana table.

Requires: pip install fugashi unidic-lite
"""

import json
import re
import sys

LRC_RE = re.compile(r"^(\[\d+:\d+\.\d+\])(.*)$")

# Katakana reading -> Hepburn (UniDic gives readings in katakana).
KANA = {
    "ア": "a", "イ": "i", "ウ": "u", "エ": "e", "オ": "o",
    "カ": "ka", "キ": "ki", "ク": "ku", "ケ": "ke", "コ": "ko",
    "サ": "sa", "シ": "shi", "ス": "su", "セ": "se", "ソ": "so",
    "タ": "ta", "チ": "chi", "ツ": "tsu", "テ": "te", "ト": "to",
    "ナ": "na", "ニ": "ni", "ヌ": "nu", "ネ": "ne", "ノ": "no",
    "ハ": "ha", "ヒ": "hi", "フ": "fu", "ヘ": "he", "ホ": "ho",
    "マ": "ma", "ミ": "mi", "ム": "mu", "メ": "me", "モ": "mo",
    "ヤ": "ya", "ユ": "yu", "ヨ": "yo",
    "ラ": "ra", "リ": "ri", "ル": "ru", "レ": "re", "ロ": "ro",
    "ワ": "wa", "ヰ": "wi", "ヱ": "we", "ヲ": "wo", "ン": "n",
    "ガ": "ga", "ギ": "gi", "グ": "gu", "ゲ": "ge", "ゴ": "go",
    "ザ": "za", "ジ": "ji", "ズ": "zu", "ゼ": "ze", "ゾ": "zo",
    "ダ": "da", "ヂ": "ji", "ヅ": "zu", "デ": "de", "ド": "do",
    "バ": "ba", "ビ": "bi", "ブ": "bu", "ベ": "be", "ボ": "bo",
    "パ": "pa", "ピ": "pi", "プ": "pu", "ペ": "pe", "ポ": "po",
    "ァ": "a", "ィ": "i", "ゥ": "u", "ェ": "e", "ォ": "o",
    "ャ": "ya", "ュ": "yu", "ョ": "yo",
    "ヴ": "vu",
}
DIGRAPH_STEM = {
    "キ": "k", "シ": "sh", "チ": "ch", "ニ": "n", "ヒ": "h",
    "ミ": "m", "リ": "r", "ギ": "g", "ジ": "j", "ビ": "b", "ピ": "p",
}
SMALL_Y = {"ャ": "ya", "ュ": "yu", "ョ": "yo"}
# Standalone particles keep their spoken reading, not the kana table value.
PARTICLES = {"ハ": "wa", "ヘ": "e", "ヲ": "o"}
VOWELS = set("aeiou")


def kana_to_romaji(kana):
    """Hepburn for one katakana reading (sokuon, long vowels, digraphs)."""
    out = []
    i = 0
    last_vowel = ""
    while i < len(kana):
        c = kana[i]
        if c == "ッ" and i + 1 < len(kana):
            nxt = kana_to_romaji(kana[i + 1 :])
            if nxt[:1].isalpha():
                out.append(nxt[0])
            i += 1
            continue
        if c == "ー":
            out.append(last_vowel)
            i += 1
            continue
        if i + 1 < len(kana) and kana[i + 1] in SMALL_Y and c in DIGRAPH_STEM:
            stem = DIGRAPH_STEM[c]
            y = SMALL_Y[kana[i + 1]]
            roma = stem + y[-1] if stem in ("sh", "ch", "j") else stem + y
            out.append(roma)
            last_vowel = y[-1]
            i += 2
            continue
        roma = KANA.get(c, c)
        out.append(roma)
        if roma[:1] in VOWELS or (roma and roma[-1] in VOWELS):
            last_vowel = roma[-1]
        i += 1
    return "".join(out)


def romanize_line(tagger, line):
    parts = []
    for word in tagger(line):
        surface = word.surface
        if not surface.strip():
            parts.append(surface)
            continue
        kana = reading_of(word) or surface
        pos1 = pos_of(word)
        if pos1 == "助詞" and kana in PARTICLES:
            parts.append(PARTICLES[kana])
        else:
            parts.append(kana_to_romaji(kana))
    return re.sub(r"\s+", " ", " ".join(parts)).strip()


def reading_of(word):
    try:
        feat = word.feature
        for attr in ("kana", "pron"):
            value = getattr(feat, attr, None)
            if value and value != "*":
                return value
        if isinstance(feat, (tuple, list)):
            # UniDic field order fallback (kana is field 24 in unidic-lite).
            for ix in (24, 11):
                if ix < len(feat) and feat[ix] and feat[ix] != "*":
                    return feat[ix]
    except Exception:
        pass
    return None


def pos_of(word):
    try:
        feat = word.feature
        value = getattr(feat, "pos1", None)
        if value:
            return value
        if isinstance(feat, (tuple, list)) and feat:
            return feat[0]
    except Exception:
        pass
    return ""


def main():
    try:
        from fugashi import Tagger
    except ImportError as ex:
        print(json.dumps({"error": f"fugashi not installed: {ex}"}))
        return 2
    try:
        payload = json.loads(sys.stdin.read() or "{}")
    except json.JSONDecodeError as ex:
        print(json.dumps({"error": f"bad request JSON: {ex}"}))
        return 2

    text = payload.get("text") or ""
    is_lrc = bool(payload.get("is_lrc", False))

    try:
        tagger = Tagger()
    except Exception as ex:  # missing UniDic data, broken install, ...
        print(json.dumps({"error": f"mecab init failed: {ex}"}))
        return 2

    out_lines = []
    for line in str(text).split("\n"):
        prefix, content = "", line
        if is_lrc:
            match = LRC_RE.match(line)
            if match:
                prefix, content = match.group(1) + " ", match.group(2)
        try:
            out_lines.append(prefix + romanize_line(tagger, content))
        except Exception:
            out_lines.append(line)  # one bad line never kills the song
    print(json.dumps({"text": "\n".join(out_lines)}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
