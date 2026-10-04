using System.Text;
using System.Text.RegularExpressions;

namespace Hathor.Domain.Services;

// Kana → Hepburn romanization (desktop pykakasi equivalent for kana;
// kanji has no dictionary here and passes through unchanged — same openly
// noted gap as the Android port). LRC timestamps are preserved when isLrc.
public static class KanaRomaji
{
    private static readonly Dictionary<char, string> Basic = new()
    {
        ['あ'] = "a", ['い'] = "i", ['う'] = "u", ['え'] = "e", ['お'] = "o",
        ['か'] = "ka", ['き'] = "ki", ['く'] = "ku", ['け'] = "ke", ['こ'] = "ko",
        ['さ'] = "sa", ['し'] = "shi", ['す'] = "su", ['せ'] = "se", ['そ'] = "so",
        ['た'] = "ta", ['ち'] = "chi", ['つ'] = "tsu", ['て'] = "te", ['と'] = "to",
        ['な'] = "na", ['に'] = "ni", ['ぬ'] = "nu", ['ね'] = "ne", ['の'] = "no",
        ['は'] = "ha", ['ひ'] = "hi", ['ふ'] = "fu", ['へ'] = "he", ['ほ'] = "ho",
        ['ま'] = "ma", ['み'] = "mi", ['む'] = "mu", ['め'] = "me", ['も'] = "mo",
        ['や'] = "ya", ['ゆ'] = "yu", ['よ'] = "yo",
        ['ら'] = "ra", ['り'] = "ri", ['る'] = "ru", ['れ'] = "re", ['ろ'] = "ro",
        ['わ'] = "wa", ['ゐ'] = "wi", ['ゑ'] = "we", ['を'] = "wo", ['ん'] = "n",
        ['が'] = "ga", ['ぎ'] = "gi", ['ぐ'] = "gu", ['げ'] = "ge", ['ご'] = "go",
        ['ざ'] = "za", ['じ'] = "ji", ['ず'] = "zu", ['ぜ'] = "ze", ['ぞ'] = "zo",
        ['だ'] = "da", ['ぢ'] = "ji", ['づ'] = "zu", ['で'] = "de", ['ど'] = "do",
        ['ば'] = "ba", ['び'] = "bi", ['ぶ'] = "bu", ['べ'] = "be", ['ぼ'] = "bo",
        ['ぱ'] = "pa", ['ぴ'] = "pi", ['ぷ'] = "pu", ['ぺ'] = "pe", ['ぽ'] = "po",
        ['ぁ'] = "a", ['ぃ'] = "i", ['ぅ'] = "u", ['ぇ'] = "e", ['ぉ'] = "o",
        ['ゃ'] = "ya", ['ゅ'] = "yu", ['ょ'] = "yo", ['ゎ'] = "wa",
        ['ア'] = "a", ['イ'] = "i", ['ウ'] = "u", ['エ'] = "e", ['オ'] = "o",
        ['カ'] = "ka", ['キ'] = "ki", ['ク'] = "ku", ['ケ'] = "ke", ['コ'] = "ko",
        ['サ'] = "sa", ['シ'] = "shi", ['ス'] = "su", ['セ'] = "se", ['ソ'] = "so",
        ['タ'] = "ta", ['チ'] = "chi", ['ツ'] = "tsu", ['テ'] = "te", ['ト'] = "to",
        ['ナ'] = "na", ['ニ'] = "ni", ['ヌ'] = "nu", ['ネ'] = "ne", ['ノ'] = "no",
        ['ハ'] = "ha", ['ヒ'] = "hi", ['フ'] = "fu", ['ヘ'] = "he", ['ホ'] = "ho",
        ['マ'] = "ma", ['ミ'] = "mi", ['ム'] = "mu", ['メ'] = "me", ['モ'] = "mo",
        ['ヤ'] = "ya", ['ユ'] = "yu", ['ヨ'] = "yo",
        ['ラ'] = "ra", ['リ'] = "ri", ['ル'] = "ru", ['レ'] = "re", ['ロ'] = "ro",
        ['ワ'] = "wa", ['ヰ'] = "wi", ['ヱ'] = "we", ['ヲ'] = "wo", ['ン'] = "n",
        ['ガ'] = "ga", ['ギ'] = "gi", ['グ'] = "gu", ['ゲ'] = "ge", ['ゴ'] = "go",
        ['ザ'] = "za", ['ジ'] = "ji", ['ズ'] = "zu", ['ゼ'] = "ze", ['ゾ'] = "zo",
        ['ダ'] = "da", ['ヂ'] = "ji", ['ヅ'] = "zu", ['デ'] = "de", ['ド'] = "do",
        ['バ'] = "ba", ['ビ'] = "bi", ['ブ'] = "bu", ['ベ'] = "be", ['ボ'] = "bo",
        ['パ'] = "pa", ['ピ'] = "pi", ['プ'] = "pu", ['ペ'] = "pe", ['ポ'] = "po",
        ['ァ'] = "a", ['ィ'] = "i", ['ゥ'] = "u", ['ェ'] = "e", ['ォ'] = "o",
        ['ャ'] = "ya", ['ュ'] = "yu", ['ョ'] = "yo", ['ヮ'] = "wa",
        ['ヴ'] = "vu",
    };

    // Digraph consonant stems: き + ゃ → kya (ki → k + ya).
    private static readonly Dictionary<char, string> DigraphStem = new()
    {
        ['き'] = "k", ['し'] = "sh", ['ち'] = "ch", ['に'] = "n", ['ひ'] = "h",
        ['み'] = "m", ['り'] = "r", ['ぎ'] = "g", ['じ'] = "j", ['び'] = "b",
        ['ぴ'] = "p", ['う'] = "w", ['く'] = "kw", ['ぐ'] = "gw",
        ['キ'] = "k", ['シ'] = "sh", ['チ'] = "ch", ['ニ'] = "n", ['ヒ'] = "h",
        ['ミ'] = "m", ['リ'] = "r", ['ギ'] = "g", ['ジ'] = "j", ['ビ'] = "b",
        ['ピ'] = "p", ['ウ'] = "w", ['ク'] = "kw", ['グ'] = "gw",
        ['テ'] = "t", ['デ'] = "d", ['フ'] = "f",
    };

    private static readonly HashSet<char> SmallY = ['ゃ', 'ゅ', 'ょ', 'ャ', 'ュ', 'ョ'];
    private static readonly Regex LrcTimestamp = new(@"^(\[\d+:\d+\.\d+\])(.*)$", RegexOptions.Compiled);

    public static string Romanize(string? text, bool isLrc = false)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var lines = text.Split('\n');
        var out_ = new StringBuilder();
        for (var li = 0; li < lines.Length; li++)
        {
            if (li > 0) out_.Append('\n');
            var line = lines[li];
            if (isLrc)
            {
                var m = LrcTimestamp.Match(line);
                if (m.Success) out_.Append(m.Groups[1].Value).Append(' ');
                line = m.Success ? m.Groups[2].Value : line;
            }
            out_.Append(RomanizeLine(line));
        }
        return out_.ToString();
    }

    private static string RomanizeLine(string line)
    {
        var sb = new StringBuilder();
        var i = 0;
        string lastVowel = "";
        while (i < line.Length)
        {
            var c = line[i];
            // Sokuon: double the next syllable's initial consonant.
            if (c is 'っ' or 'ッ')
            {
                var next = PeekRomaji(line, i + 1);
                if (next.Length > 0 && char.IsLetter(next[0]))
                    sb.Append(next[0]);
                i++;
                continue;
            }
            // Long vowel mark: repeat the previous vowel.
            if (c == 'ー')
            {
                sb.Append(lastVowel);
                i++;
                continue;
            }
            // Digraph: stem + small ゃゅょ. Hepburn contracts sh/ch/j
            // (しゃ→sha, ちゅ→chu, じゃ→ja); others keep the y (きゃ→kya).
            if (i + 1 < line.Length && SmallY.Contains(line[i + 1]) && DigraphStem.TryGetValue(c, out var stem))
            {
                var y = Basic[line[i + 1]];
                var roma = stem is "sh" or "ch" or "j" ? stem + y[^1] : stem + y;
                sb.Append(roma);
                lastVowel = y[^1].ToString();
                i += 2;
                continue;
            }
            if (Basic.TryGetValue(c, out var r))
            {
                // ん before vowels/labials → n' ambiguity ignored (plain n, like pykakasi hepburn join).
                sb.Append(r);
                lastVowel = r[^1].ToString();
                i++;
                continue;
            }
            sb.Append(c); // kanji, latin, punctuation pass through
            i++;
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static string PeekRomaji(string line, int i)
    {
        if (i >= line.Length) return "";
        if (i + 1 < line.Length && SmallY.Contains(line[i + 1]) && DigraphStem.TryGetValue(line[i], out var stem))
            return stem + Basic[line[i + 1]];
        return Basic.TryGetValue(line[i], out var r) ? r : "";
    }
}
